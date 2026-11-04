using System;
using System.Collections.Generic;
using EFT;
using EFT.AssetsManager;
using EFT.Ballistics;
using EFT.Interactive;
using HarmonyLib;
using UnityEngine;

namespace EuphoriaStylePhysics;

// A dying body on EFT's own corpse ragdoll (CorpseRagdoll: RigidbodySpawner bodies + CharacterJoints). Muscles are torques
// between each body and its parent toward the pose it died in (plus bent knees, a curled spine, arms reaching for the
// wound, a wall or the ground); "support" is an outside hand that holds the pelvis up and the torso upright and fades out,
// so the body sags, stumbles and drops instead of folding at once. All of it is stepped right before each physics step.
internal sealed class DeathMotion : MonoBehaviour
{
    // 0.20.0 (user: "deaths lying down or in the air"): Ground = killed lying down, Airborne = killed with the feet off the ground;
    // both were EFT's ragdoll before. No outside support for either (nothing to hold up, nothing to stand on).
    private enum Style { Stagger, LegGives, HeadRipple, HeadLegsGo, Slump, Tumble, Clutch, Knock, Blast, Ground, Airborne }
    private enum Role { Other, Pelvis, Spine, Neck, Head, ThighL, ThighR, CalfL, CalfR, UpperL, UpperR, ForeL, ForeR, HandL, HandR, FootL, FootR }
    private enum Goal { None, Wall, Wound, Ground, Guard, Gun }

    private sealed class Body
    {
        public Rigidbody Rb;
        public int Parent = -1;
        public Role Role;
        public Quaternion Rel0;   // rotation relative to the parent body when it died
        public Quaternion RelGround = Quaternion.identity;   // 0.20.0: the same, in the pose it landed in (lying hurt moves around it)
        public float Wn;          // muscle natural frequency, rad/s
        public float Tone = 1f;   // per-body muscle multiplier (0: limp)
        public float RelaxTo = -1f;   // 0.17.5: a round just hit it: its muscle gives way until then
        public readonly List<int> Children = new List<int>();
    }

    private sealed class Arm
    {
        public int Up = -1, Fore = -1;
        public Transform Palm;
        public float UpLen, ForeLen;
        public float Side;        // -1 left, +1 right
        public bool Ok;
        // this step's goal
        public Goal Goal;
        public Vector3 Target;
        public float W;
        // closest the hand got to each kind of goal while it was really reaching (for the log)
        public float BestWound = 99f, BestWall = 99f, BestGround = 99f;
    }

    private static readonly List<DeathMotion> Active = new List<DeathMotion>();
    private static readonly Dictionary<Rigidbody, DeathMotion> Driven = new Dictionary<Rigidbody, DeathMotion>();
    private static readonly AccessTools.FieldRef<Player, DamageInfo> LastDamageInfoRef =
        AccessTools.FieldRefAccess<Player, DamageInfo>("LastDamageInfo");
    private static int _errors, _roundLogs;
    private static string _loggedMode;
    private static int _worldMask = -1;
    private static readonly Dictionary<Corpse, (float at, float sum)> RecentRounds = new Dictionary<Corpse, (float, float)>();
    // 0.6.2: the last hit seen on each living player (ShotReactions). In Fika a client's copy of a bot often dies with an
    // empty LastDamageInfo (type 0, no hit point); the hit we saw a moment before is the killing one.
    // The hit point is kept relative to the player (0.6.4): the bot may walk on between that hit and its death.
    private static readonly Dictionary<Player, (DamageInfo info, EBodyPart part, float at, Vector3 local)> LastHits =
        new Dictionary<Player, (DamageInfo, EBodyPart, float, Vector3)>();

    internal static void RecordHit(Player p, DamageInfo info, EBodyPart part)
    {
        if (p == null || info.HitPoint == Vector3.zero) return;
        if (LastHits.Count > 128) LastHits.Clear();
        Vector3 local = Quaternion.Inverse(p.Transform.rotation) * (info.HitPoint - p.Position);
        LastHits[p] = (info, part, Time.time, local);
    }

    private readonly List<Body> _bodies = new List<Body>();
    private readonly Arm[] _arms = { new Arm { Side = -1f }, new Arm { Side = 1f } };
    private System.Random _rnd;
    private string _name, _endReason;
    // 0.9.0 knockdown mode: a living body that falls, lies hurt, then is handed back (Knockdown) instead of going limp
    private bool _knock, _aborted, _ended, _loggedFling, _loggedRoll;
    // 0.15.0 Fika: a knockdown played on a client's copy of a bot; it lies until the host says it gets up (FinishNow)
    private bool _remote, _finishNow;
    private Vector3? _shooterOverride;
    internal void FinishNow() => _finishNow = true;
    // 0.14.0 (user: "can you try to make a sitting pose"; EFT has no sitting state for bots): a knocked-down bot with a
    // badly hurt leg sits up on its own ragdoll, turns to the shooter and holds its gun on him (Knockdown aims and fires it)
    private float _sitFor, _sitFrom, _sitTo, _sitW;
    internal bool Sitting => _sitW > 0.6f;
    internal bool HasShooter => _hasShooter;

    // 0.16.0 (user: they fired "while their head is buried in the ground"): a sitting body fires only while it really sits up
    internal bool UprightToFire(out string why)
    {
        why = null;
        if (_chest < 0 || _pelvis < 0) return true;
        float up = Vector3.Dot(_bodies[_chest].Rb.rotation * _chestUpLocal, Vector3.up);
        if (up < 0.7f) { why = $"slumped (chest {Mathf.Acos(Mathf.Clamp(up, -1f, 1f)) * Mathf.Rad2Deg:F0} deg from upright)"; return false; }
        float headH = _head >= 0 ? _bodies[_head].Rb.position.y - _bodies[_pelvis].Rb.position.y : 1f;
        if (headH < 0.3f) { why = $"head only {headH:F2} m above the pelvis"; return false; }
        return true;
    }
    internal Vector3 ShooterPos => _shooterPos;
    // 0.11.0 blast throw (user: grenade victims thrown): thrown up and away, arms guard the head and the legs tuck in the
    // air, a hard landing, then limp (a kill) or lying hurt and getting up (a knockdown)
    private bool _launched, _landed, _blastKnown;
    private static int _rtSkips;
    private Vector3 _blastAway;
    private float _closeness = 0.6f;
    internal static Vector3 LastBlastPos;
    internal static float LastBlastAt = -10f, LastBlastReach = 8f;
    internal static bool LastBlastGrenade;   // RealTargets only throws for grenade items

    // Prefix on ExplosionSharedMethods.Explosion: where the blast is, before it kills anyone.
    internal static void RecordBlast(Vector3 pos, float maxDistance, bool grenade)
    {
        LastBlastGrenade = grenade;
        LastBlastPos = pos;
        LastBlastAt = Time.time;
        LastBlastReach = Mathf.Clamp(Mathf.Max(maxDistance, 3f) * 1.5f, 3f, 20f);
    }
    private Action<bool> _onEnd;
    private Style _style;
    private float _t, _dur, _mass, _durAdded;
    private int _pelvis = -1, _chest = -1, _head = -1, _thighL = -1, _thighR = -1, _calfL = -1, _calfR = -1;
    private Transform _ankleL, _ankleR;
    private float _pelvisY0, _groundY, _kneelAmount, _stumbleSpeed, _leanMax;
    private float _contact = 1f;          // 0.6.1: support only while a foot is on the ground (0..1, smoothed)
    private bool _loggedNoContact;
    private Vector3 _fwdLocal, _pelvisUpLocal, _chestUpLocal, _stumbleDir;
    private int _legSide;                 // LegGives: -1 left, +1 right
    private float _legsGoAt;              // HeadRipple: when the legs give
    // hand on the wound
    private int _woundArm = -1;           // 0 left, 1 right
    private int _woundArm2 = -1;          // Clutch: the second hand on the wound
    private Rigidbody _woundBody;
    private Vector3 _woundLocal;
    // hand on a wall
    private int _wallArm = -1;
    private Vector3 _wallPoint, _wallNormal;
    private float _wallAt, _nextWallLook;
    private bool _wallTop;          // 0.16.0: the hand is on the top of low cover (crate, table), not on a wall face
    // 0.16.0 knockdown roll (user: getting up "warps"): 0 not checked yet, 1 rolling onto its front, -1 no roll needed
    private int _roll;
    private float _rollSide = 1f;
    private Vector3 _chestFwdLocal;
    // arms out to the ground
    private bool _braces;
    // running death
    private Vector3 _runVel;
    private bool _momentumGiven;
    // catch steps
    private int _stepLeg = -1;
    private bool _groundSnapped;   // 0.20.0: lying hurt took the landed pose as its rest pose
    private float _stepFast, _stepSwing = 0.28f;   // 0.17.5: how fast the push is (0-1) and this step's swing time
    private float _stepT, _stepRest;
    private Vector3 _stepTarget;
    // last twitches
    private float _throesFrom, _throesTo, _throesNext, _throesPower;
    private float _writheNext;
    // lying wounded on the ground after the fall: curls up, a hand on the wound or reaching out, then goes still
    private bool _lieHurt;
    private float _hurtFrom, _hurtTo, _curl;
    private int _reachOutArm;
    // 0.7.1: the shooter (or a point back along the round), for the head turning to it and a hand reaching to it
    private bool _hasShooter, _reachToShooter;
    private Vector3 _shooterPos, _headFwdLocal;
    private Vector3 _rightLocal;

    internal static bool IsDriven(Rigidbody rb) => Driven.ContainsKey(rb);
    internal static int ActiveCount => Active.Count;

    private static int WorldMask
    {
        get
        {
            if (_worldMask == -1)
            {
                _worldMask = LayerMask.GetMask("HighPolyCollider", "LowPolyCollider", "Terrain");
                Plugin.Log.LogInfo($"[EuphoriaStylePhysics] world mask for walls/ground: 0x{_worldMask:X}");
            }
            return _worldMask;
        }
    }

    internal static void Begin(Corpse corpse, Player player)
    {
        if (_errors > 20) return;
        string who = player.Profile?.Nickname ?? player.ProfileId;
        int dyingNow = 0;
        foreach (var a in Active) if (a != null && !a._knock) dyingNow++;   // 0.17.5: knockdowns do not use up the cap
        if (dyingNow >= Plugin.MaxDying.Value)
        {
            Plugin.Log.LogInfo($"[EuphoriaStylePhysics] {who} dies with EFT's ragdoll: {dyingNow} bodies already dying (max {Plugin.MaxDying.Value})");
            return;
        }
        var cam = Camera.main;
        float maxD = Plugin.MaxDistance.Value;
        if (cam != null && (cam.transform.position - player.Position).sqrMagnitude > maxD * maxD)
        {
            if (Plugin.VerboseLog.Value) Plugin.Log.LogInfo($"[EuphoriaStylePhysics] {who} dies with EFT's ragdoll: farther than {maxD:F0} m");
            return;
        }
        var pool = player.GetComponent<PlayerPoolObject>();
        if (pool == null || pool.RigidbodySpawners == null || pool.JointSpawners == null)
        {
            Plugin.Log.LogWarning($"[EuphoriaStylePhysics] {who}: no PlayerPoolObject spawners, EFT ragdoll only");
            return;
        }
        var dm = corpse.gameObject.AddComponent<DeathMotion>();
        if (!dm.Setup(player, pool))
        {
            Destroy(dm);
            return;
        }
        Active.Add(dm);
        foreach (var b in dm._bodies) Driven[b.Rb] = dm;
    }

    // A living bot knocked down (Knockdown has made its ragdoll bodies dynamic). onEnd(true): it lay its time; false: cut short.
    internal static DeathMotion BeginKnockdown(Player player, PlayerPoolObject pool, DamageInfo info, EBodyPart part, Action<bool> onEnd,
        float sitFor = 0f, bool remote = false, Vector3? shooter = null)
    {
        if (_errors > 20) return null;
        var dm = player.gameObject.AddComponent<DeathMotion>();
        dm._knock = true;
        dm._sitFor = sitFor;
        dm._remote = remote;
        dm._shooterOverride = shooter;
        dm._onEnd = onEnd;
        if (!dm.Setup(player, pool, true, info, part))
        {
            dm._aborted = true;
            Destroy(dm);
            return null;
        }
        Active.Add(dm);
        foreach (var b in dm._bodies) Driven[b.Rb] = dm;
        return dm;
    }

    // Stop at once, without handing the body back (the bot died: the corpse takes it over).
    internal void Abort()
    {
        _aborted = true;
        _endReason = "aborted";
        Active.Remove(this);
        foreach (var b in _bodies)
            if (b.Rb != null && Driven.TryGetValue(b.Rb, out var owner) && owner == this) Driven.Remove(b.Rb);
        Destroy(this);
    }

    private void OnDestroy()
    {
        if (_knock && !_aborted && !_ended)
        {
            _ended = true;
            try { _onEnd?.Invoke(_endReason == "done"); }
            catch (Exception e) { Plugin.Log.LogError($"[EuphoriaStylePhysics] knockdown hand-back failed: {e}"); }
        }
        if (_t > 0f)
            Plugin.Log.LogInfo($"[EuphoriaStylePhysics] {_name}: {(_knock ? "down" : "dying")} over after {_t:F2} s ({_endReason ?? "removed"})" +
                               $"{(_pelvis >= 0 && _bodies[_pelvis].Rb != null ? $", body {Vector3.Distance(_bodies[_pelvis].Rb.position, transform.position):F1} m from the root" : "")}" +
                               $"{(_durAdded > 0f ? $", rounds kept it up {_durAdded:F2} s longer" : "")}{HandsReport()}");
        Active.Remove(this);
        foreach (var b in _bodies)
            if (b.Rb != null && Driven.TryGetValue(b.Rb, out var owner) && owner == this) Driven.Remove(b.Rb);
    }

    // ---------------------------------------------------------------- setup

    private bool Setup(Player player, PlayerPoolObject pool, bool knock = false, DamageInfo knockInfo = default, EBodyPart knockPart = default)
    {
        _name = player.Profile?.Nickname ?? player.ProfileId;
        var info = knock ? knockInfo : LastDamageInfoRef(player);
        var part = knock ? knockPart : player.LastDamagedBodyPart;
        string infoFrom = knock ? "the hit" : "game";
        if (!knock && (info.DamageType == 0 || info.HitPoint == Vector3.zero) && LastHits.TryGetValue(player, out var seen) && Time.time - seen.at < 1.5f)
        {
            info = seen.info;
            info.HitPoint = player.Position + player.Transform.rotation * seen.local;
            part = seen.part;
            infoFrom = $"last hit seen {Time.time - seen.at:F2} s before";
        }
        if (!knock) LastHits.Remove(player);
        _rnd = new System.Random(unchecked((player.ProfileId?.GetHashCode() ?? 0) * 31 + Time.frameCount));

        var index = new Dictionary<RigidbodySpawner, int>();
        foreach (var sp in pool.RigidbodySpawners)
        {
            if (sp == null || sp.Rigidbody == null) continue;
            index[sp] = _bodies.Count;
            _bodies.Add(new Body { Rb = sp.Rigidbody, Role = RoleOf(sp.name) });
        }
        foreach (var js in pool.JointSpawners)
        {
            if (js == null || js.connectedBody == null) continue;
            var child = js.GetComponent<RigidbodySpawner>();
            if (child != null && index.TryGetValue(child, out var ci) && index.TryGetValue(js.connectedBody, out var pi))
                _bodies[ci].Parent = pi;
        }

        int spines = 0, chestDepth = -1;
        for (int i = 0; i < _bodies.Count; i++)
        {
            var b = _bodies[i];
            _mass += b.Rb.mass;
            if (b.Parent >= 0)
            {
                b.Rel0 = Quaternion.Inverse(_bodies[b.Parent].Rb.rotation) * b.Rb.rotation;
                _bodies[b.Parent].Children.Add(i);
            }
            switch (b.Role)
            {
                case Role.Pelvis: _pelvis = i; break;
                case Role.Spine:
                    spines++;
                    int depth = Depth(i);
                    if (depth > chestDepth) { chestDepth = depth; _chest = i; }
                    break;
                case Role.Head: _head = i; break;
                case Role.ThighL: _thighL = i; break;
                case Role.ThighR: _thighR = i; break;
                case Role.CalfL: _calfL = i; break;
                case Role.CalfR: _calfR = i; break;
                case Role.UpperL: _arms[0].Up = i; break;
                case Role.UpperR: _arms[1].Up = i; break;
                case Role.ForeL: _arms[0].Fore = i; break;
                case Role.ForeR: _arms[1].Fore = i; break;
            }
            b.Wn = b.Role switch
            {
                Role.ThighL or Role.ThighR or Role.CalfL or Role.CalfR or Role.FootL or Role.FootR => 16f,
                Role.Spine or Role.Pelvis => 14f,
                Role.Neck or Role.Head => 10f,
                _ => 9f
            };
        }

        string roles = string.Join(",", _bodies.ConvertAll(b => $"{b.Rb.name}:{b.Role}>{(b.Parent >= 0 ? _bodies[b.Parent].Role.ToString() : "-")}"));
        if (_pelvis < 0 || _calfL < 0 || _calfR < 0 || _thighL < 0 || _thighR < 0 || _chest < 0)
        {
            Plugin.Log.LogWarning($"[EuphoriaStylePhysics] {_name}: ragdoll bodies not recognised, EFT ragdoll only. Bodies: {roles}");
            return false;
        }

        _ankleL = FindChild(_bodies[_calfL].Rb.transform, "foot") ?? _bodies[_calfL].Rb.transform;
        _ankleR = FindChild(_bodies[_calfR].Rb.transform, "foot") ?? _bodies[_calfR].Rb.transform;
        foreach (var a in _arms)
        {
            if (a.Up < 0 || a.Fore < 0) continue;
            a.Palm = FindChild(_bodies[a.Fore].Rb.transform, "palm");
            a.UpLen = Vector3.Distance(_bodies[a.Up].Rb.position, _bodies[a.Fore].Rb.position);
            a.ForeLen = a.Palm != null ? Vector3.Distance(_bodies[a.Fore].Rb.position, a.Palm.position) : a.UpLen * 0.95f;
            a.Ok = a.UpLen > 0.05f && a.ForeLen > 0.05f;
        }

        var pelvisRb = _bodies[_pelvis].Rb;
        _pelvisY0 = pelvisRb.position.y;
        _groundY = Mathf.Min(_ankleL.position.y, _ankleR.position.y) - 0.06f;
        float height = _pelvisY0 - _groundY;
        Vector3 fwd = Flat(player.Transform.rotation * Vector3.forward);
        if (fwd == Vector3.zero) fwd = Flat(pelvisRb.rotation * Vector3.forward);
        Vector3 right = Vector3.Cross(Vector3.up, fwd);
        _fwdLocal = Quaternion.Inverse(pelvisRb.rotation) * fwd;
        _pelvisUpLocal = Quaternion.Inverse(pelvisRb.rotation) * Vector3.up;
        _chestUpLocal = Quaternion.Inverse(_bodies[_chest].Rb.rotation) * Vector3.up;
        _chestFwdLocal = Quaternion.Inverse(_bodies[_chest].Rb.rotation) * fwd;
        _rightLocal = Quaternion.Inverse(pelvisRb.rotation) * right;

        // what killed it, and how it stood
        var type = info.DamageType;
        const EDamageType blasts = EDamageType.Explosion | EDamageType.GrenadeFragment | EDamageType.Landmine | EDamageType.Artillery |
                                   EDamageType.ThermobaricExplosion | EDamageType.Btr | EDamageType.HotGases;
        string cause = $"{type}/{part}";
        if (!_knock && Plugin.ExplosionsVanilla.Value && (type & blasts) != 0) return Skip($"blast ({cause})");
        bool fall = !_knock && (type & EDamageType.Fall) != 0;
        bool lying = !_knock && height < 0.38f;
        // 0.6.1 (user: bodies float right after dying): the support needs real ground under the feet; off it, Airborne has none
        float footGap = FootGap();
        bool airborne = !_knock && !lying && footGap > 0.15f;
        bool crouched = height < 0.7f;
        if (!_knock && Plugin.HeadshotInstantDrop.Value && part == EBodyPart.Head && (type & (EDamageType.Bullet | EDamageType.Sniper)) != 0)
            return Skip($"headshot, instant drop ({cause})");
        if (!_knock && _rnd.NextDouble() < Plugin.DropDeadChance.Value) return Skip($"drops dead ({cause})");

        Vector3 vel = Vector3.zero;
        try { vel = player.Velocity; } catch { /* observed players without a movement context */ }
        _runVel = Flat3(vel);
        // 0.17.5 (log: "dies: Tumble ... speed 17.7 m/s", body thrown 6.9 m): EFT's velocity can be a jump in position, not a run.
        // No bot runs past ~7 m/s: above 9 it is ignored, a real sprint is held to 7.
        if (_runVel.magnitude > 9f)
        {
            Plugin.Log.LogInfo($"[EuphoriaStylePhysics] {player.Profile?.Nickname ?? player.ProfileId}: speed at death {_runVel.magnitude:F1} m/s is not a run, ignored");
            _runVel = Vector3.zero;
        }
        else if (_runVel.magnitude > 7f) _runVel = _runVel.normalized * 7f;
        bool running = Plugin.RunTumble.Value && !crouched && _runVel.magnitude > 2.5f;

        bool weapon = (type & (EDamageType.Bullet | EDamageType.Sniper | EDamageType.Melee | EDamageType.Blunt | EDamageType.Impact)) != 0;
        Vector3 dir = Flat(info.Direction);
        bool blast = (type & blasts) != 0 && Plugin.BlastThrow.Value;
        if (blast) _style = Style.Blast;
        else if (_knock) _style = Style.Knock;
        else if (airborne) _style = Style.Airborne;
        else if (lying) _style = Style.Ground;
        else if (running) _style = Style.Tumble;
        else if (!weapon || info.HitPoint == Vector3.zero || crouched) _style = Style.Slump;
        else if (part == EBodyPart.Head) _style = _rnd.NextDouble() < Plugin.HeadshotLegsGoChance.Value ? Style.HeadLegsGo : Style.HeadRipple;
        // 0.7.0: a knife or a blow to the body: both hands go to it, it folds onto its knees
        else if (Plugin.MeleeClutch.Value && (type & (EDamageType.Melee | EDamageType.Blunt)) != 0 && part is EBodyPart.Chest or EBodyPart.Stomach)
            _style = Style.Clutch;
        else if (part == EBodyPart.LeftLeg || part == EBodyPart.RightLeg) _style = Style.LegGives;
        else _style = Style.Stagger;

        float min = Plugin.DyingTimeMin.Value, max = Mathf.Max(min, Plugin.DyingTimeMax.Value);
        _dur = _style switch
        {
            Style.HeadLegsGo => 0.15f,
            Style.HeadRipple => Rand(0.45f, 0.8f),
            Style.LegGives => Rand(min, Mathf.Lerp(min, max, 0.6f)),
            Style.Slump => crouched ? Rand(0.5f, 1f) : Rand(Mathf.Max(1f, min), Mathf.Max(1.8f, max)),
            Style.Tumble => Rand(0.35f, 0.6f),
            Style.Knock => Rand(0.35f, 0.6f),
            Style.Blast => 2.5f,   // the longest flight; cut short at the landing
            Style.Airborne => 2.5f,   // the same: cut short when it comes down
            Style.Ground => Rand(0.35f, 0.7f),   // the muscles hold its pose a moment, then let go
            Style.Clutch => Rand(1f, 2.2f),
            _ => Rand(min, max)
        };
        if (fall && _style == Style.Slump) _dur = Rand(0.25f, 0.45f);   // fall damage kills on landing: the legs fold at once
        _legsGoAt = _style == Style.HeadRipple ? _dur * Rand(0.55f, 0.85f) : _dur;
        _kneelAmount = _style switch { Style.Ground or Style.Airborne => 0f, Style.Slump or Style.Clutch => 1f, Style.Tumble or Style.Knock => Rand(0.5f, 0.8f), _ => Rand(0.55f, 1f) };
        _leanMax = _style switch { Style.Slump => Rand(20f, 35f), Style.Tumble => Rand(35f, 55f), Style.Knock => Rand(25f, 40f), Style.Clutch => Rand(30f, 45f), _ => Rand(10f, 30f) };
        _stumbleDir = _style == Style.Tumble ? _runVel.normalized : dir == Vector3.zero ? fwd : dir;
        bool melee = (type & (EDamageType.Melee | EDamageType.Blunt)) != 0;
        _stumbleSpeed = _style switch
        {
            Style.Stagger => Mathf.Clamp(0.45f + info.Damage / 70f, 0.45f, 1.5f) * (melee ? 0.6f : 1f) * Rand(0.7f, 1.2f),
            Style.Clutch => Rand(0.2f, 0.5f),
            Style.Knock => Mathf.Clamp(1f + info.Damage / 60f, 1f, 2f),
            Style.LegGives => Rand(0.3f, 0.7f),
            Style.HeadRipple => Rand(0.2f, 0.6f),
            Style.Tumble => _runVel.magnitude * 0.6f,
            _ => 0f
        } * (_style == Style.Tumble ? 1f : Plugin.StumbleScale.Value);

        if (_style == Style.LegGives)
        {
            _legSide = part == EBodyPart.LeftLeg ? -1 : 1;
            _stumbleDir = (_stumbleDir + right * (0.6f * _legSide)).normalized;
        }

        // the wound and the hand that goes to it
        try
        {
            var shooter = info.Player?.iPlayer;
            if (shooter != null && !ReferenceEquals(shooter, player)) { _shooterPos = shooter.Position + Vector3.up * 1.4f; _hasShooter = true; }
        }
        catch { /* a shooter that is already gone */ }
        if (!_hasShooter && _shooterOverride.HasValue)
        {
            _shooterPos = _shooterOverride.Value;
            _hasShooter = true;
        }
        if (!_hasShooter && info.HitPoint != Vector3.zero && info.Direction.sqrMagnitude > 1e-6f)
        {
            _shooterPos = info.HitPoint - info.Direction.normalized * 15f;
            _hasShooter = true;
        }
        if (_head >= 0) _headFwdLocal = Quaternion.Inverse(_bodies[_head].Rb.rotation) * fwd;
        _woundBody = info.HitCollider != null ? info.HitCollider.attachedRigidbody : null;
        if (_woundBody == null || !_bodies.Exists(b => b.Rb == _woundBody))
            _woundBody = Nearest(info.HitPoint);
        // 0.9.2 (log: a hand reached for a wound 17 m away): the game's last hit can be an old one from somewhere else
        // (died later of bleeding); a hit point that far from every body part is not a wound on this body
        if (_woundBody != null && info.HitPoint != Vector3.zero)
        {
            float near = float.MaxValue;
            foreach (var bb in _bodies) near = Mathf.Min(near, Vector3.Distance(bb.Rb.worldCenterOfMass, info.HitPoint));
            if (near > 0.8f)
            {
                Plugin.Log.LogInfo($"[EuphoriaStylePhysics] {_name}: last hit point is {near:F1} m from the body (an old hit), no hand on a wound");
                _woundBody = null;
            }
        }
        if (_woundBody != null) _woundLocal = _woundBody.transform.InverseTransformPoint(info.HitPoint);
        if (Plugin.HandOnWound.Value && _woundBody != null &&
            (_style == Style.Clutch || (_style == Style.Stagger || _style == Style.LegGives || _style == Style.Knock || _style == Style.Ground) && Often(0.75f)))
            ChooseWoundArm(right);
        if (_style == Style.Clutch && _woundArm >= 0 && _arms[1 - _woundArm].Ok) _woundArm2 = 1 - _woundArm;

        if (_style == Style.Blast)
        {
            Vector3 at = pelvisRb.position;
            if (Time.time - LastBlastAt < 1f)
            {
                Vector3 a = Flat3(at - LastBlastPos);
                _blastKnown = true;
                _blastAway = a.sqrMagnitude > 1e-4f ? a.normalized : -fwd;
                _closeness = Mathf.Clamp01(1f - a.magnitude / Mathf.Max(LastBlastReach, 3f));
            }
            else
            {
                _blastAway = dir != Vector3.zero ? dir : -fwd;
                _closeness = 0.6f;
            }
        }
        _braces = Plugin.GroundBrace.Value && _style != Style.HeadLegsGo && _style != Style.Blast && _style != Style.Ground
                  && (_style == Style.Tumble || _style == Style.Knock || _style == Style.Airborne || Often(0.7f));

        // 0.17.5 ("Blast throw" says blast kills go limp; half of them lay hurt): not after a blast
        if (_knock || _style != Style.HeadLegsGo && _style != Style.HeadRipple && _style != Style.Blast && _rnd.NextDouble() < Plugin.LieWoundedChance.Value)
        {
            _lieHurt = true;
            _hurtFrom = _dur + 0.3f;
            _hurtTo = _hurtFrom + Rand(2f, 5f);
            _curl = Rand(0.4f, 1f);
            _reachOutArm = _woundArm >= 0 ? 1 - _woundArm : _rnd.Next(2);
            _reachToShooter = _hasShooter && Plugin.ReachToShooter.Value && _rnd.NextDouble() < 0.5;
        }
        if (_knock)
        {
            // knocked down: lies its time on the ground, a little curled, then is handed back to get up
            float lieMin = Plugin.KnockdownLieMin.Value, lieMax = Mathf.Max(lieMin, Plugin.KnockdownLieMax.Value);
            _hurtTo = _hurtFrom + Rand(lieMin, lieMax);
            if (_sitFor > 0f)
            {
                // a moment on the ground, then it sits up and stays sitting; it gets up only after that
                _sitFrom = _hurtFrom + 0.8f;
                _sitTo = _sitFrom + _sitFor;
                _hurtTo = _sitTo + 0.8f;
            }
            if (_remote)
            {
                // the host decides when it gets up: hold the lying (or sitting) until its get-up arrives
                _hurtTo += 60f;
                if (_sitFor > 0f) _sitTo += 60f;
            }
            _curl = Rand(0.3f, 0.6f);
        }
        if (!_knock && Plugin.Throes.Value > 0f && _rnd.NextDouble() >= Plugin.ThroesNoneChance.Value)
        {
            _throesFrom = (_lieHurt ? _hurtTo : _dur) + Rand(0.5f, 1.2f);
            _throesTo = _throesFrom + Rand(1.2f, 3f);
            _throesPower = Plugin.Throes.Value * Rand(0.5f, 1f);
        }

        Plugin.Log.LogInfo($"[EuphoriaStylePhysics] {_name} {(_knock ? "is knocked down" : "dies")}: {_style}{(crouched ? " (crouched)" : "")} ({cause} from {infoFrom}, dmg {info.Damage:F0}, " +
                           $"speed {_runVel.magnitude:F1} m/s), {_dur:F2} s on its feet, stumble {_stumbleSpeed:F2} m/s, kneel {_kneelAmount:F2}, " +
                           $"hand {(_woundArm < 0 ? "-" : _woundArm == 0 ? "L" : "R")}{(_woundArm2 >= 0 ? "+" + (_woundArm2 == 0 ? "L" : "R") : "")}->{(_woundBody != null ? _woundBody.name : "-")}, " +
                           $"arms out {(_braces ? "yes" : "no")}, shooter {(_hasShooter ? "known" : "-")}, lies hurt {(_lieHurt ? $"{_hurtTo - _hurtFrom:F1} s" : "no")}, twitches {(_throesTo > 0 ? "yes" : "no")}, {_bodies.Count} bodies, {spines} spine, " +
                           $"{_mass:F0} kg, arms {(_arms[0].Ok ? "L" : "")}{(_arms[1].Ok ? "R" : "")}, physics {PhysicsMode()}");
        if (Plugin.VerboseLog.Value) Plugin.Log.LogInfo($"[EuphoriaStylePhysics] bodies: {roles}");
        if (!_knock) SeparateOverlaps(player);   // Knockdown does its own (it keeps them apart for the whole knockdown)
        return true;
    }

    // 0.17.5 (bodies vibrating): a body's own hit colliders and armour plates overlap where they meet, and the solver pushed them
    // apart every step while the muscles held the pose: a constant buzz. Only shapes that overlap by design stop colliding: trunk,
    // hip and shoulder parts (pelvis, spine, neck, head, thighs, upper arms) at most two links apart, never thigh-thigh or arm-arm.
    // Forearms, hands, calves and feet keep colliding with everything, so a hand on the wound rests on the body, not in it.
    private void SeparateOverlaps(Player player)
    {
        try
        {
            var index = new Dictionary<Rigidbody, int>();
            for (int i = 0; i < _bodies.Count; i++) index[_bodies[i].Rb] = i;
            var cols = new List<Collider>();
            foreach (var c in player.GetComponentsInChildren<Collider>())
                if (c != null && c.enabled && !c.isTrigger && c.attachedRigidbody != null && index.ContainsKey(c.attachedRigidbody)) cols.Add(c);
            int pairs = 0;
            for (int i = 0; i < cols.Count; i++)
                for (int j = i + 1; j < cols.Count; j++)
                {
                    var a = cols[i];
                    var c = cols[j];
                    if (a.attachedRigidbody == c.attachedRigidbody) continue;
                    if (!OverlapByDesign(index[a.attachedRigidbody], index[c.attachedRigidbody])) continue;
                    if (!Physics.ComputePenetration(a, a.transform.position, a.transform.rotation, c, c.transform.position, c.transform.rotation, out _, out float depth)) continue;
                    if (depth <= 0.002f) continue;
                    Physics.IgnoreCollision(a, c, true);
                    pairs++;
                }
            if (Plugin.VerboseLog.Value) Plugin.Log.LogInfo($"[EuphoriaStylePhysics] {_name}: {pairs} own overlapping collider pairs kept apart");
        }
        catch (Exception e) { Plugin.Log.LogWarning($"[EuphoriaStylePhysics] {_name}: own colliders not separated: {e.Message}"); }
    }

    private bool OverlapByDesign(int i, int j)
    {
        static bool Core(Role r) => r is Role.Pelvis or Role.Spine or Role.Neck or Role.Head or Role.ThighL or Role.ThighR or Role.UpperL or Role.UpperR;
        Role a = _bodies[i].Role, b = _bodies[j].Role;
        if (!Core(a) || !Core(b)) return false;
        if (a is Role.ThighL or Role.ThighR && b is Role.ThighL or Role.ThighR) return false;
        if (a is Role.UpperL or Role.UpperR && b is Role.UpperL or Role.UpperR) return false;
        return Links(i, j) <= 2;
    }

    // how many joints apart two bodies are (through their common ancestor)
    private int Links(int i, int j)
    {
        var up = new Dictionary<int, int>();
        for (int k = i, d = 0; k >= 0 && d < 16; k = _bodies[k].Parent, d++) up[k] = d;
        for (int k = j, d = 0; k >= 0 && d < 16; k = _bodies[k].Parent, d++)
            if (up.TryGetValue(k, out int di)) return di + d;
        return 99;
    }

    private bool Skip(string why)
    {
        Plugin.Log.LogInfo($"[EuphoriaStylePhysics] {_name} dies with EFT's ragdoll: {why}");
        return false;
    }

    private void ChooseWoundArm(Vector3 right)
    {
        var w = _bodies.Find(b => b.Rb == _woundBody);
        if (w == null) return;
        bool woundOnArm = w.Role is Role.UpperL or Role.UpperR or Role.ForeL or Role.ForeR or Role.HandL or Role.HandR;
        bool woundLow = w.Role is Role.CalfL or Role.CalfR or Role.FootL or Role.FootR or Role.Head or Role.Neck;
        if (woundOnArm || woundLow) return;
        float side = Vector3.Dot(_woundBody.transform.TransformPoint(_woundLocal) - _bodies[_pelvis].Rb.position, right);
        int arm = Mathf.Abs(side) < 0.05f ? _rnd.Next(2) : side > 0 ? 1 : 0;
        if (_knock && _arms[0].Ok) arm = 0;   // knocked down, the gun stays in the right hand: the left clutches
        if (w.Role == Role.ThighL) arm = 0;
        if (w.Role == Role.ThighR) arm = 1;
        if (_arms[arm].Ok) _woundArm = arm;
    }

    // ---------------------------------------------------------------- rounds into bodies

    // A round into a dying body keeps it up a little longer (a burst keeps it dancing); with our own push on, a round into a
    // corpse whose ragdoll still moves also pushes the part it hit, capped per body so full auto does not launch it.
    internal static void OnRound(Rigidbody rb, Vector3 point, Vector3 dir, float damage)
    {
        if (rb == null || rb.isKinematic || dir.sqrMagnitude < 1e-6f) return;
        bool dying = Driven.TryGetValue(rb, out var dm) && dm != null;
        if (dying)
        {
            if (Plugin.BurstKeepsUp.Value) dm.TakeRound(rb);
            // 0.20.0 (user: "when they die they dont get pushed by rounds until theyre on the ground"): while its time on its feet lasts
            // a round neither pushes it nor makes the hold give way; once it is down, rounds push it as before
            if (dm.OnFeet) return;
            dm.TakeHit(rb, dir);
        }
        // 0.17.5: our own push only when RealTargets does not push corpses itself (both together pushed twice)
        bool push = Plugin.CorpseHits.Value && !Plugin.RealTargetsPushesCorpses();
        if (!push) return;
        var corpse = rb.GetComponentInParent<Corpse>();
        if (corpse == null) return;
        float now = Time.time;
        float impulse = Mathf.Clamp(6f + damage * 0.12f, 4f, 20f) * Plugin.CorpseHitScale.Value;
        // 0.17.5 (raid log: at Round push strength 3 a burst threw bodies 5-6 m, the pelvis and head past 10 m/s): one round gives
        // the part it hits at most 4 m/s (as RealTargets caps its push), and a burst at most 30 N s whatever the strength
        float capped = Mathf.Min(impulse, rb.mass * 4f);
        if (capped < impulse && (Plugin.VerboseLog.Value || _roundLogs < 5))
            Plugin.Log.LogInfo($"[EuphoriaStylePhysics] round into {rb.name}: push {impulse:F1} N s capped to {capped:F1} (4 m/s on its {rb.mass:F1} kg)");
        impulse = capped;
        if (RecentRounds.TryGetValue(corpse, out var r) && now - r.at < 0.3f)
            impulse = Mathf.Min(impulse, Mathf.Max(0f, 30f - r.sum));
        else r = (now, 0f);
        RecentRounds[corpse] = (r.at, r.sum + impulse);
        if (RecentRounds.Count > 64) RecentRounds.Clear();
        if (impulse <= 0f) return;
        rb.WakeUp();
        rb.AddForceAtPosition(dir.normalized * impulse, point, ForceMode.Impulse);
        _roundLogs++;
        if (Plugin.VerboseLog.Value || _roundLogs <= 5)
            Plugin.Log.LogInfo($"[EuphoriaStylePhysics] round #{_roundLogs} into {(dying ? "a dying" : "a dead")} body ({rb.name}): impulse {impulse:F1} N s, dmg {damage:F0}");
    }

    private bool OnFeet => !_knock && _t < _dur;   // 0.20.0: still in its time on its feet (rounds do not push it yet)
    private float _yieldUntil = -1f;
    private Vector3 _pushVel, _pushFrom, _hitDir, _hitV0;

    // 0.17.5: a later round into this dying body (the killing round is EFT's, as RealTargets leaves it): the hold and the hit
    // part's muscle give way for a moment, so the push shows. Only the speed the round adds along its own way is kept: following
    // all of the body's speed also carried the walk it died in and the tip of its fall (user: a body ended 3.3 m away).
    private void TakeHit(Rigidbody rb, Vector3 dir)
    {
        if (_knock) return;
        dir.y = 0f;
        if (dir.sqrMagnitude < 1e-6f) return;
        _hitDir = dir.normalized;
        _hitV0 = Flat3(_bodies[_pelvis].Rb.velocity);
        _pushFrom = _pushVel;
        _yieldUntil = _t + 0.3f;   // RealTargets pushes up to its volley window (0.05 s) after the hit
        foreach (var b in _bodies)
            if (b.Rb == rb)
            {
                b.RelaxTo = _t + 0.25f;
                if (b.Parent >= 0) _bodies[b.Parent].RelaxTo = Mathf.Max(_bodies[b.Parent].RelaxTo, _t + 0.15f);
                break;
            }
    }

    private void TakeRound(Rigidbody rb)
    {
        if (_t >= _dur || _style == Style.HeadLegsGo) return;
        float add = Mathf.Min(0.08f, 0.35f - _durAdded);   // 0.17.2: was 0.12 per round, 0.7 s at most (floaty)
        if (add <= 0f) return;
        _dur += add;
        _durAdded += add;
        ShiftAfterFeet(add);
        if (_legsGoAt > 0f && _style == Style.HeadRipple) _legsGoAt += add;
    }

    // ---------------------------------------------------------------- stepping

    // Called right before every physics step EFT runs (or in FixedUpdate when Unity steps physics itself).
    internal static void StepAll(float dt)
    {
        if (dt <= 0f || Active.Count == 0) return;
        for (int i = Active.Count - 1; i >= 0; i--)
        {
            var dm = Active[i];
            if (dm == null) { Active.RemoveAt(i); continue; }
            try
            {
                if (!dm.Step(dt))
                {
                    Active.RemoveAt(i);   // Destroy only lands at the end of the frame; more steps may come before it
                    Destroy(dm);
                }
            }
            catch (Exception e)
            {
                _errors++;
                Plugin.Log.LogError($"[EuphoriaStylePhysics] dying step failed ({_errors}), body goes limp: {e}");
                Destroy(dm);
                Active.Remove(dm);
            }
        }
    }

    private void FixedUpdate()
    {
        if (Physics.simulationMode != SimulationMode.FixedUpdate) return;
        // Unity steps physics itself (no EFT script stepping): each component steps only itself.
        try
        {
            if (!Step(Time.fixedDeltaTime)) Destroy(this);
        }
        catch (Exception e)
        {
            _errors++;
            Plugin.Log.LogError($"[EuphoriaStylePhysics] dying step failed ({_errors}), body goes limp: {e}");
            Destroy(this);
        }
    }

    internal static string PhysicsMode()
    {
        string mode = Physics.simulationMode == SimulationMode.Script
            ? "EFT " + PhysicsExtensions.Simulation.UpdateMode
            : "Unity " + Physics.simulationMode;
        if (mode != _loggedMode)
        {
            _loggedMode = mode;
            Plugin.Log.LogInfo($"[EuphoriaStylePhysics] physics stepping: {mode}, fixedDeltaTime {Time.fixedDeltaTime:F4}");
        }
        return mode;
    }

    private bool Step(float dt)
    {
        if (_aborted) { _endReason = "aborted"; return false; }
        if (_finishNow) { _endReason = "done"; return false; }
        if (!Plugin.IsOn || (_knock ? !Plugin.KnockdownEnabled.Value : !Plugin.DeathEnabled.Value)) { _endReason = "turned off"; return false; }
        foreach (var b in _bodies)
            if (b.Rb == null) { _endReason = "a body part is gone"; return false; }   // dismemberment mod, or EFT removed the ragdoll
        var pelvis = _bodies[_pelvis].Rb;
        if (pelvis.isKinematic) { _endReason = "EFT stopped the corpse"; return false; }
        // 0.15.1 safety net: nothing we drive should move faster than a hard blast throw
        float cap = _style == Style.Blast ? 14f : 9f;
        foreach (var b in _bodies)
            if (b.Rb.velocity.sqrMagnitude > cap * cap)
            {
                if (!_loggedFling)
                {
                    _loggedFling = true;
                    Plugin.Log.LogWarning($"[EuphoriaStylePhysics] {_name}: {b.Rb.name} at {b.Rb.velocity.magnitude:F1} m/s (up {b.Rb.velocity.y:F1}) at {_t:F2} s, capped to {cap} (the body was flying apart)");
                }
                b.Rb.velocity = b.Rb.velocity.normalized * cap;
            }

        if (!_momentumGiven)
        {
            // EFT starts a corpse at most 2 m/s; a runner keeps most of its run
            _momentumGiven = true;
            if (_style == Style.Tumble || _style == Style.Airborne && _runVel.sqrMagnitude > 0.25f)
                foreach (var b in _bodies)
                    b.Rb.velocity = new Vector3(_runVel.x * 0.85f, b.Rb.velocity.y, _runVel.z * 0.85f);
            if (_style == Style.Blast && !_launched)
            {
                _launched = true;
                // 0.17.0: a grenade kill RealTargets throws too (dead, chest within 6 m) is not thrown twice
                var chestRb = _chest >= 0 ? _bodies[_chest].Rb : _bodies[0].Rb;
                if (!_knock && _blastKnown && LastBlastGrenade && !Plugin.BlastThrowWithRealTargets.Value && Plugin.RealTargetsThrowsCorpses()
                    && Vector3.Distance(chestRb.position, LastBlastPos) <= 6f)
                {
                    _rtSkips++;
                    if (Plugin.VerboseLog.Value || _rtSkips <= 5)
                        Plugin.Log.LogInfo($"[EuphoriaStylePhysics] {_name}: blast throw left to RealTargets (its Blast injuries is on; {_rtSkips} so far)");
                    goto thrown;
                }
                float k = Plugin.BlastThrowScale.Value * (_knock ? 0.7f : 1f);
                Vector3 vel = _blastAway * (Mathf.Lerp(2f, 6f, _closeness) * k) + Vector3.up * (Mathf.Lerp(1.5f, 4.5f, _closeness) * k);
                Vector3 spin = Vector3.Cross(Vector3.up, _blastAway) * Rand(1.5f, 4f) + new Vector3(Rand(-1f, 1f), Rand(-1f, 1f), Rand(-1f, 1f));
                foreach (var b in _bodies)
                {
                    b.Rb.velocity = vel * Rand(0.9f, 1.1f);
                    b.Rb.angularVelocity += spin;
                }
                Plugin.Log.LogInfo($"[EuphoriaStylePhysics] {_name}: thrown by a blast at {vel.magnitude:F1} m/s (closeness {_closeness:F2})");
            }
            thrown:;
        }

        _t += dt;

        float end = Mathf.Max(Mathf.Max(_dur + 0.5f, _throesTo), _lieHurt ? _hurtTo + 0.3f : 0f);
        if (_t > end) { _endReason = "done"; return false; }

        float d = _dur;
        float tone, support, kneel;
        switch (_style)
        {
            case Style.HeadLegsGo:
                tone = 1f - Smooth(0f, d, _t);
                support = 0f;
                kneel = 0f;
                break;
            case Style.HeadRipple:
                tone = 1f - Smooth(_legsGoAt, d, _t);
                support = 0.85f * (1f - Smooth(_legsGoAt, _legsGoAt + 0.08f, _t));
                kneel = 0.4f * Smooth(0.1f, _legsGoAt, _t);
                break;
            case Style.Blast:
                if (!_landed && _t > 0.25f && (_t > 2.5f || pelvis.velocity.y < 0.5f && HeightAboveGround(pelvis.position) < 0.45f)) Land();
                tone = _landed ? Mathf.Lerp(0.5f, 0f, Smooth(_dur, _dur + 0.3f, _t)) : 0.55f;
                support = 0f;
                kneel = 0f;
                break;
            case Style.Ground:
                // lying already: holds its pose a moment (no hold-up, nothing to fall from), then goes limp; lying hurt may follow
                tone = _t < 0.4f * d ? 1f : Mathf.Lerp(1f, 0f, Smooth(0.4f * d, d + 0.2f, _t));
                support = 0f;
                kneel = 0f;
                break;
            case Style.Airborne:
                // in the air: half tone, arms out for the landing; down, it goes limp the way a standing death would
                if (!_landed && _t > 0.1f && pelvis.velocity.y < 0.5f && (FootGap() < 0.1f || HeightAboveGround(pelvis.position) < 0.45f || _t > 2.5f)) Land();
                tone = _landed ? Mathf.Lerp(0.6f, 0f, Smooth(_dur, _dur + 0.3f, _t)) : 0.6f;
                support = 0f;
                kneel = 0f;
                break;
            case Style.Tumble:
            case Style.Knock:
                tone = _t < d ? 1f - 0.5f * Smooth(0f, d, _t) : Mathf.Lerp(0.5f, 0f, Smooth(d, d + 0.25f, _t));
                support = 0.5f * (1f - Smooth(0f, 0.8f * d, _t));
                kneel = _kneelAmount * Smooth(0f, 0.7f * d, _t);
                break;
            default:
                // 0.1.1 (user: floaty/slow): knees go by 0.6 d, the outside support is gone by 0.75 d, muscles fully off at d + 0.2 s
                tone = _t < 0.6f * d ? 1f : Mathf.Lerp(1f, 0.05f, Smooth(0.6f * d, d, _t));
                if (_t > d) tone = Mathf.Lerp(0.05f, 0f, Smooth(d, d + 0.2f, _t));
                // 0.15.2 (user: slow-motion falls): a slump is let go of sooner, it drops instead of sinking
                // 0.17.2 (user: still floaty): the support lets go by 0.55 d (was 0.75 d), so the knees give under real weight
                support = 0.85f * (1f - (_style == Style.Slump ? Smooth(0.2f * d, 0.45f * d, _t) : Smooth(0.3f * d, 0.55f * d, _t)));
                kneel = _kneelAmount * Smooth(0.15f * d, 0.6f * d, _t);
                break;
        }
        float hurt = _lieHurt ? Smooth(_hurtFrom, _hurtFrom + 0.5f, _t) * (1f - Smooth(_hurtTo - 1f, _hurtTo, _t)) : 0f;
        tone = Mathf.Max(tone, 0.3f * hurt);
        // 0.20.0: the lying-hurt pose was an offset on the STANDING pose of the moment of death, so on a body lying down the muscles
        // pulled it back toward standing (and threw bodies killed lying down). When lying hurt starts, the pose it actually landed in
        // is taken as the new rest pose; the curl moves around that from then on.
        if (!_knock && !_groundSnapped && hurt > 0.001f)
        {
            _groundSnapped = true;
            foreach (var gb in _bodies)
                if (gb.Parent >= 0) gb.RelGround = Quaternion.Inverse(_bodies[gb.Parent].Rb.rotation) * gb.Rb.rotation;
        }
        _sitW = _sitFor > 0f ? Smooth(_sitFrom, _sitFrom + 0.9f, _t) * (1f - Smooth(_sitTo, _sitTo + 0.6f, _t)) : 0f;
        tone = Mathf.Max(tone, 0.65f * _sitW);
        tone *= Plugin.MuscleScale.Value;

        Vector3 fwd = Flat(pelvis.rotation * _fwdLocal);
        if (fwd == Vector3.zero) fwd = _stumbleDir;
        Vector3 right = Vector3.Cross(Vector3.up, fwd);
        Vector3 bodyRight = pelvis.rotation * _rightLocal;
        float wnMax = 0.4f / dt;

        LookForWall(support);
        PlanArms(right, fwd, hurt);

        // per-region tone: a headshot ripple goes head first, then the spine, the legs last
        foreach (var b in _bodies)
        {
            float rt = 1f;
            if (_style == Style.HeadRipple)
            {
                if (b.Role is Role.Head or Role.Neck) rt = 0f;
                else if (b.Role is Role.Spine or Role.UpperL or Role.UpperR or Role.ForeL or Role.ForeR or Role.HandL or Role.HandR)
                    rt = 1f - Smooth(0.05f, 0.3f, _t);
            }
            if (_style == Style.LegGives && IsLeg(b.Role, _legSide)) rt = 0.35f;
            if (hurt > 0f && ArmOf(b.Role) >= 0) rt *= 1f - hurt;   // no rifle-hold pose on the ground; an arm with a goal gets its own strength
            if (_t < b.RelaxTo) rt *= 0.3f;   // 0.17.5: the part a round just hit goes with it for a moment
            b.Tone = rt;
        }

        // muscles: torque between each body and its parent toward the target pose
        for (int i = 0; i < _bodies.Count; i++)
        {
            var b = _bodies[i];
            if (b.Parent < 0) continue;
            float k = tone * b.Tone;
            var p = _bodies[b.Parent];
            Quaternion des = p.Rb.rotation * (_groundSnapped ? b.RelGround : b.Rel0);
            // lying hurt, the standing knee bend fades out and the curl (around the body's own right axis) takes over
            // on the landed pose the knee bend is already in it, and the curl is half (it is added to how it already lies)
            if (!_groundSnapped) des = Extra(b.Role, kneel * (1f - hurt), right) * des;
            if (hurt > 0f) des = Curl(b.Role, hurt * _curl * (1f - _sitW) * (_groundSnapped ? 0.5f : 1f), bodyRight) * des;
            if (_sitW > 0f) des = SitPose(b.Role, _sitW, bodyRight) * des;
            if (_style == Style.Blast && !_landed) des = Curl(b.Role, 0.5f, bodyRight) * des;   // balled up in the air
            int arm = ArmOf(b.Role);
            if (arm >= 0 && _arms[arm].Goal != Goal.None) des = Reach(b, _arms[arm], des, right, fwd, ref k);
            if (b.Role == Role.Head && _hasShooter && Plugin.LookAtShooter.Value && _style != Style.HeadRipple && _style != Style.HeadLegsGo)
                des = LookAtShooter(b, des, Mathf.Max(hurt, _sitW));
            if (k <= 0.001f) continue;
            float wn = Mathf.Min(b.Wn * Mathf.Sqrt(k), wnMax);
            Vector3 err = RotErr(des, b.Rb.rotation);
            Vector3 wRel = b.Rb.angularVelocity - p.Rb.angularVelocity;
            float inertia = Inertia(b.Rb);
            // 0.17.5 (bodies vibrating): the torque turns the parent back as well, so the damping per step is 2 wn dt (1 + I_child /
            // I_parent); held under 0.8 so a limb on a light parent (a calf on a thigh, the head) cannot flip its spin every step.
            float wd = Mathf.Min(wn, 0.4f / (dt * (1f + inertia / Mathf.Max(Inertia(p.Rb), 1e-5f))));
            Vector3 alpha = Vector3.ClampMagnitude(wn * wn * err - 2f * wd * wRel, 400f);
            Vector3 tau;
            if (arm >= 0 && _arms[arm].Goal != Goal.None && _arms[arm].W > 0f)
            {
                // 0.6.3 (user: no hands on wounds or walls seen): a reaching arm is sized for everything it swings (forearm, hand)
                // and holds that weight against gravity; sized for the one body alone it could not lift the arm at all.
                Subtree(i, out float m, out Vector3 com);
                Vector3 r = com - b.Rb.position;
                inertia += m * r.sqrMagnitude;
                // 0.17.5 (forearms reversing their spin up to 19.6 times a second): the torque lands on this one body, so what one
                // physics step can take is set by ITS inertia. Damping was allowed up to 3x that per step, which flips the spin every
                // step instead of braking it; now held under 0.8x, the stiffness under 0.3x. The arm's weight is held by PullHands
                // only (holding it here too lifted it twice).
                float own = Inertia(b.Rb);
                float damp = Mathf.Min(2f * wn * inertia, 0.8f * own / dt);
                float stiff = Mathf.Min(wn * wn * inertia, 0.3f * own / (dt * dt));
                tau = Vector3.ClampMagnitude(stiff * err - damp * wRel, 60f);
            }
            else tau = alpha * inertia;
            b.Rb.AddTorque(tau);
            p.Rb.AddTorque(-tau);
        }

        // support: the outside hand that holds the body up while it dies
        if (support > 0.001f)
        {
            float g = -Physics.gravity.y;
            float gap = FootGap();
            // 0.25 m: a kneeling body lifts its ankle bones (heels up), which is not leaving the ground
            _contact = Mathf.MoveTowards(_contact, gap < 0.25f ? 1f : 0f, dt * (gap < 0.25f ? 4f : 12f));
            if (_contact < 0.5f && !_loggedNoContact)
            {
                _loggedNoContact = true;
                Plugin.Log.LogInfo($"[EuphoriaStylePhysics] {_name}: feet off the ground ({gap:F2} m) at {_t:F2} s, support let go");
            }
            support *= _contact * Plugin.HoldUp.Value;
            float drop = (_pelvisY0 - _groundY) * 0.45f * kneel;
            float yT = _pelvisY0 - drop;
            float vy = pelvis.velocity.y;
            float fy = _mass * (g * 0.65f + 70f * (yT - pelvis.position.y) - 14f * vy);   // 0.17.2: 0.8 g was floaty
            fy = Mathf.Clamp(fy, 0f, 2f * _mass * g) * support;
            pelvis.AddForce(Vector3.up * fy);

            float give = _t < _yieldUntil ? 0.4f : 1f;   // 0.17.5: the trunk tips with a hit before it is held up again
            Upright(pelvis, pelvis.rotation * _pelvisUpLocal, Vector3.up, support * give, wnMax, 0.5f * _mass);
            var chest = _bodies[_chest].Rb;
            Vector3 chestUp = Quaternion.AngleAxis(_leanMax * kneel, right) * Vector3.up;
            if (_style == Style.LegGives) chestUp = Quaternion.AngleAxis(-12f * _legSide * kneel, fwd) * chestUp;
            if (_wallArm >= 0) chestUp = Vector3.Slerp(chestUp, (Vector3.up - 0.35f * _wallNormal).normalized, 0.5f);   // lean on the wall
            Upright(chest, chest.rotation * _chestUpLocal, chestUp, support * give, wnMax, 0.3f * _mass);

            // stumble the way the round pushed it (or the run carried it); a wall stops the part going into it
            float env = _style == Style.Tumble ? 1f : 1f - Smooth(0.3f * _dur, 0.7f * _dur, _t);
            Vector3 vT = _stumbleDir * (_stumbleSpeed * env);
            if (_wallArm >= 0)
            {
                // 0.16.0: a wall (or cover) out of the hand's reach: it staggers over to it first, then leans on it
                Vector3 toWall = Flat3(_wallPoint - _bodies[_chest].Rb.position);
                if (toWall.magnitude > 0.5f)
                    vT = toWall.normalized * (Mathf.Max(_stumbleSpeed, 0.6f) * (1f - Smooth(0.6f * _dur, 0.9f * _dur, _t)));
                else
                {
                    float into = Vector3.Dot(vT, -_wallNormal);
                    if (into > 0f) vT += _wallNormal * into;
                }
            }
            Vector3 vH = Flat3(pelvis.velocity);
            // 0.17.5 (GitHub report: "Bullets are not pushing bodies with this mod + real targets"): the hold drove the pelvis back to
            // its planned speed within a quarter second, so a push from a round (RealTargets', ours, EFT's death impulse) was undone
            // at once. For a moment after a hit the planned speed follows what the body really got; then it eases back to the plan.
            if (_t < _yieldUntil)
            {
                float gain = Mathf.Clamp(Vector3.Dot(vH - _hitV0, _hitDir), 0f, 1.5f);   // what this round added, its way only
                _pushVel = Vector3.MoveTowards(_pushVel, Vector3.ClampMagnitude(_pushFrom + _hitDir * gain, 2f), 25f * dt);
            }
            else if (_pushVel != Vector3.zero) _pushVel = _pushVel.sqrMagnitude < 1e-4f ? Vector3.zero : _pushVel * Mathf.Exp(-dt / 0.6f);
            vT += _pushVel;
            Vector3 fh = Vector3.ClampMagnitude(_mass * 4f * (vT - vH), _mass * 6f) * support;
            pelvis.AddForce(fh);

            if (Plugin.Stepping.Value && _style != Style.Tumble) CatchStep(dt, pelvis, vH, right, support);
        }
        // 0.17.5: CatchStep only runs while the hand holds; a step it began and could not finish stayed half done
        else if (_stepLeg >= 0) _stepLeg = -1;

        if (_sitW > 0.001f) HoldSitting(pelvis, wnMax);
        if (_knock && !_remote && _sitW < 0.1f && Plugin.KnockdownRollOver.Value) RollOver(wnMax);
        PullHands(dt);
        if (_throesTo > 0f && _t >= _throesFrom && _t < _throesTo) Twitch();
        if (_knock && hurt > 0.3f && _sitW < 0.1f && _roll != 1 && Plugin.KnockdownWrithe.Value) Writhe(bodyRight);
        return true;
    }

    // Bent knees, hips, a curled spine and a dropping head as the body kneels (world rotations put before the hold pose).
    private Quaternion Extra(Role role, float kneel, Vector3 right)
    {
        float knee = 95f * kneel, hip = -55f * kneel;
        if (_style == Style.LegGives)
        {
            float fast = Smooth(0f, 0.18f, _t);
            if (IsLeg(role, _legSide)) { knee = Mathf.Max(knee, 75f * fast); hip = Mathf.Min(hip, -35f * fast); }
        }
        switch (role)
        {
            case Role.CalfL: case Role.CalfR: return Quaternion.AngleAxis(knee, right);
            case Role.ThighL: case Role.ThighR: return Quaternion.AngleAxis(hip, right);
            case Role.Spine: return Quaternion.AngleAxis(0.35f * _leanMax * kneel, right);
            case Role.Head: case Role.Neck: return Quaternion.AngleAxis(18f * kneel, right);
            default: return Quaternion.identity;
        }
    }

    // ---------------------------------------------------------------- arms: wall, wound, ground

    // Every 0.1 s while the body is still held up: a wall within reach in front or to the side gets a hand on it.
    private void LookForWall(float support)
    {
        if (!Plugin.WallBrace.Value || _wallArm >= 0 || _t < _nextWallLook || support < 0.25f || _style == Style.HeadLegsGo) return;
        _nextWallLook = _t + 0.1f;
        var chest = _bodies[_chest].Rb;
        Vector3 from = chest.position;
        Vector3 fwd = _stumbleDir;
        Vector3 right = Vector3.Cross(Vector3.up, fwd);
        // 0.16.0 (user: no environment touching seen): all around (behind too), out to the wall reach; the stumble way first
        Vector3[] dirs = { fwd, (fwd + right).normalized, (fwd - right).normalized, right, -right, (-fwd + right).normalized, (-fwd - right).normalized, -fwd };
        float reach = Plugin.WallReach.Value;
        float best = reach;
        bool found = false, top = false;
        RaycastHit bestHit = default;
        foreach (var d in dirs)
        {
            if (Physics.Raycast(from, d, out var hit, best, WorldMask, QueryTriggerInteraction.Ignore) && Mathf.Abs(hit.normal.y) < 0.5f)
            {
                best = hit.distance;
                bestHit = hit;
                found = true;
            }
        }
        if (!found)
        {
            // low cover: a flat top at hip to chest height within reach (crates, tables, sandbags) gets a hand leaned on it
            foreach (var d in dirs)
                foreach (float dist in new[] { 0.5f, 0.8f, Mathf.Min(1.1f, reach) })
                {
                    if (found && dist >= best) continue;
                    Vector3 above = from + Vector3.up * 0.1f + d * dist;
                    if (!Physics.Raycast(above, Vector3.down, out var hit, 1.2f, WorldMask, QueryTriggerInteraction.Ignore) || hit.normal.y < 0.7f) continue;
                    float h = hit.point.y - _groundY;
                    if (h < 0.55f || h > 1.15f || Physics.Linecast(from, hit.point + Vector3.up * 0.05f, WorldMask, QueryTriggerInteraction.Ignore)) continue;
                    best = dist;
                    bestHit = hit;
                    found = true;
                    top = true;
                }
            if (!found) return;
        }
        float side = Vector3.Dot(bestHit.point - from, Vector3.Cross(Vector3.up, Flat(_bodies[_pelvis].Rb.rotation * _fwdLocal)));
        int arm = side >= 0f ? 1 : 0;
        if (arm == _woundArm || arm == _woundArm2 || !_arms[arm].Ok) arm = 1 - arm;
        if (arm == _woundArm || arm == _woundArm2 || !_arms[arm].Ok) return;
        _wallArm = arm;
        _wallPoint = bestHit.point;
        _wallTop = top;
        // for a top, "away from the cover" is from the hand point back toward the body
        _wallNormal = top ? Flat(from - bestHit.point) : Flat(bestHit.normal);
        if (_wallNormal == Vector3.zero) { _wallArm = -1; return; }
        _wallAt = _t;
        Plugin.Log.LogInfo($"[EuphoriaStylePhysics] {_name}: {(arm == 0 ? "left" : "right")} hand on {(top ? "low cover" : "a wall")} {(top ? best : bestHit.distance):F2} m away " +
                           $"({bestHit.collider.name}) at {_t:F2} s");
    }

    private void PlanArms(Vector3 right, Vector3 fwd, float hurt)
    {
        for (int i = 0; i < 2; i++)
        {
            var a = _arms[i];
            a.Goal = Goal.None;
            a.W = 0f;
            if (!a.Ok || Shrunk(_bodies[a.Fore].Rb.transform)) continue;   // a cut-off arm (dismemberment mods shrink it away)
            if (_sitW > 0.3f && _hasShooter)
            {
                // both hands out in front toward the shooter, the right at the grip, the left further along the gun
                var chestRb = _bodies[_chest].Rb;
                Vector3 to = _shooterPos - chestRb.position;
                to = to.sqrMagnitude > 0.01f ? to.normalized : fwd;
                Vector3 grip = chestRb.position + to * 0.38f - Vector3.up * 0.08f + bodyRightNow() * 0.06f;
                a.Goal = Goal.Gun;
                a.Target = i == 1 ? grip : grip + to * 0.25f - bodyRightNow() * 0.04f;
                a.W = _sitW;
                continue;
            }
            if (_style == Style.Blast && !_landed)
            {
                // hands up in front of the face while it flies
                var headRb = _head >= 0 ? _bodies[_head].Rb : _bodies[_chest].Rb;
                Vector3 face = _head >= 0 ? headRb.rotation * _headFwdLocal : fwd;
                if (face.sqrMagnitude < 1e-4f) face = fwd;
                a.Goal = Goal.Guard;
                a.Target = headRb.position + face.normalized * 0.22f + bodyRightNow() * (0.1f * a.Side);
                a.W = 0.8f;
                continue;
            }
            if (i == _wallArm)
            {
                // the hand stays on the wall and slides down with the body
                var chest = _bodies[_chest].Rb.position;
                Vector3 p = _wallPoint + _wallNormal * 0.04f;
                p.y = _wallTop ? _wallPoint.y + 0.03f : Mathf.Min(_wallPoint.y + 0.15f, chest.y - 0.05f);
                a.Goal = Goal.Wall;
                a.Target = p;
                a.W = Smooth(_wallAt, _wallAt + 0.2f, _t) * (1f - Smooth(_dur, _dur + 0.3f, _t));
            }
            else if (i == _woundArm2 && _woundBody != null)
            {
                // the second hand goes over the first, a hand's width to its own side
                Vector3 wound = _woundBody.transform.TransformPoint(_woundLocal);
                Vector3 outward = wound - _woundBody.worldCenterOfMass;
                a.Goal = Goal.Wound;
                a.Target = wound + right * (0.08f * a.Side) + (outward.sqrMagnitude > 1e-6f ? outward.normalized * 0.06f : Vector3.zero);
                a.W = Mathf.Max(Smooth(0.15f, 0.5f, _t) * (1f - Smooth(_dur + 0.3f, _dur + 1.0f, _t)), hurt);
            }
            else if (i == _woundArm && _woundBody != null)
            {
                Vector3 wound = _woundBody.transform.TransformPoint(_woundLocal);
                Vector3 outward = wound - _woundBody.worldCenterOfMass;
                a.Goal = Goal.Wound;
                a.Target = wound + (outward.sqrMagnitude > 1e-6f ? outward.normalized * 0.05f : Vector3.zero);
                // 0.12.1 (user: hands on wounds not noticed): the hand gets there sooner and stays while it lies hurt
                // 0.16.0 (user: never noticed a hand on a wound): it stays on the wound through the fall, not only while standing
                a.W = Mathf.Max(Smooth(0.03f, 0.25f, _t) * (1f - Smooth(_dur + 0.3f, _dur + 1.0f, _t)), hurt);
            }
            if (a.Goal == Goal.None || a.W <= 0f) BraceGround(a, right, fwd);
            if ((a.Goal == Goal.None || a.W <= 0f) && hurt > 0f && i == ReachOutArm()) ReachOut(a, hurt);
            if (a.Goal != Goal.None && a.W > 0.6f)
            {
                Vector3 e = _bodies[a.Fore].Rb.position;
                Vector3 h = a.Palm != null ? a.Palm.position : e + (e - _bodies[a.Up].Rb.position).normalized * a.ForeLen;
                float dist = Vector3.Distance(h, a.Target);
                if (a.Goal == Goal.Wound) a.BestWound = Mathf.Min(a.BestWound, dist);
                else if (a.Goal == Goal.Wall) a.BestWall = Mathf.Min(a.BestWall, dist);
                else if (a.Goal == Goal.Ground) a.BestGround = Mathf.Min(a.BestGround, dist);
            }
        }
    }

    // Arms go out to break a forward fall.
    private void BraceGround(Arm a, Vector3 right, Vector3 fwd)
    {
        if (!_braces) return;
        var chest = _bodies[_chest].Rb.position;
        var pelvis = _bodies[_pelvis].Rb.position;
        Vector3 lean = Flat3(chest - pelvis);
        Vector3 dir = lean.magnitude > 0.12f ? lean.normalized : _stumbleDir;
        if (Vector3.Dot(dir, fwd) < 0.1f) return;   // nobody catches a fall backwards with straight arms
        float w = _style == Style.Tumble || _style == Style.Airborne
            ? Smooth(0.05f, 0.25f, _t) * (1f - Smooth(_dur + 0.2f, _dur + 0.6f, _t))
            : Smooth(0.5f * _dur, 0.7f * _dur, _t) * (1f - Smooth(_dur + 0.15f, _dur + 0.5f, _t));
        if (w <= 0f) return;
        Vector3 p = chest + dir * 0.5f + right * (0.2f * a.Side);
        float gy = _groundY;
        if (Physics.Raycast(p + Vector3.up * 0.8f, Vector3.down, out var hit, 2f, WorldMask, QueryTriggerInteraction.Ignore)) gy = hit.point.y;
        p.y = gy + 0.05f;
        a.Goal = Goal.Ground;
        a.Target = p;
        a.W = w;
    }

    // How far the lower foot is above real ground (a ray down from above that foot, through world geometry only);
    // 0 when the ray finds nothing (no reason to drop a body over a gap in the colliders).
    private float FootGap()
    {
        Transform low = _ankleL.position.y <= _ankleR.position.y ? _ankleL : _ankleR;
        Vector3 from = low.position + Vector3.up * 0.5f;
        if (!Physics.Raycast(from, Vector3.down, out var hit, 3f, WorldMask, QueryTriggerInteraction.Ignore)) return 0f;
        return Mathf.Max(0f, low.position.y - 0.06f - hit.point.y);
    }

    // 0.6.4 (user: no hands seen on wounds/walls; log: hands 50-90 cm off): turning the joints toward an IK pose did not
    // get the hands there, so each reaching hand is also pulled straight at its target by a spring at the palm (the elbow and
    // shoulder follow through the joints), holding the arm's weight; the chest takes the opposite pull, so it stays internal.
    private void PullHands(float dt)
    {
        var chest = _bodies[_chest].Rb;
        foreach (var a in _arms)
        {
            if (!a.Ok || a.Goal == Goal.None || a.W <= 0f) continue;
            var fore = _bodies[a.Fore].Rb;
            var up = _bodies[a.Up].Rb;
            Vector3 e = fore.position;
            Vector3 h = a.Palm != null ? a.Palm.position : e + (e - up.position).normalized * a.ForeLen;
            Subtree(a.Up, out float m, out _);
            // 0.17.5: braked against the chest, not the ground: a hand moving with the falling body is not held back (it buzzed)
            Vector3 v = fore.GetPointVelocity(h) - chest.GetPointVelocity(up.position);
            float kp = a.Goal == Goal.Ground ? 90f : 150f;
            // the force lands on the forearm alone: its braking held under 0.6x what the forearm's mass takes in one step (it was
            // ~0.9x, enough to flip the hand back and forth with the joint torque on top)
            float c = Mathf.Min(m * 2f * Mathf.Sqrt(kp), 0.6f * fore.mass / dt);
            Vector3 f = m * kp * (a.Target - h) - c * v - m * Physics.gravity;
            f = Vector3.ClampMagnitude(f, m * 40f) * a.W * Plugin.MuscleScale.Value;
            fore.AddForceAtPosition(f, h);
            chest.AddForceAtPosition(-f, up.position);
        }
    }

    private bool Shrunk(Transform t) => t == null || t.lossyScale.x < 0.2f;

    private Vector3 bodyRightNow() => _bodies[_pelvis].Rb.rotation * _rightLocal;

    private static float HeightAboveGround(Vector3 p)
    {
        return Physics.Raycast(p + Vector3.up * 0.1f, Vector3.down, out var hit, 4f, WorldMask, QueryTriggerInteraction.Ignore)
            ? p.y - hit.point.y : 99f;
    }

    // The thrown body is down: the 'time on its feet' ends here, lying hurt and twitches move up to it.
    private void Land()
    {
        _landed = true;
        float off = _t - _dur;
        _dur = _t;
        ShiftAfterFeet(off);
        Plugin.Log.LogInfo($"[EuphoriaStylePhysics] {_name}: landed after {_t:F2} s in the air");
    }

    // 0.17.5: what comes after its time on its feet (lying hurt, twitches) moves with the end of that time. A round keeping it up
    // moved nothing, so lying hurt began while it still stood (and took the standing pose as the pose it lies in).
    private void ShiftAfterFeet(float off)
    {
        _hurtFrom += off;
        _hurtTo += off;
        if (_throesTo > 0f) { _throesFrom += off; _throesTo += off; }
    }

    // Mass and centre of mass of a body and everything hanging off it.
    private static readonly Stack<int> SubtreeStack = new Stack<int>();

    private void Subtree(int i, out float mass, out Vector3 com)
    {
        mass = 0f;
        Vector3 sum = Vector3.zero;
        var stack = SubtreeStack;   // 0.17.5: was a new Stack per call, several per body per physics step
        stack.Clear();
        stack.Push(i);
        for (int guard = 0; stack.Count > 0 && guard < 64; guard++)
        {
            var b = _bodies[stack.Pop()];
            mass += b.Rb.mass;
            sum += b.Rb.worldCenterOfMass * b.Rb.mass;
            foreach (int c in b.Children) stack.Push(c);
        }
        com = mass > 0f ? sum / mass : _bodies[i].Rb.worldCenterOfMass;
    }

    private string HandsReport()
    {
        var parts = new List<string>();
        for (int i = 0; i < 2; i++)
        {
            var a = _arms[i];
            string side = i == 0 ? "L" : "R";
            if (a.BestWound < 90f) parts.Add($"{side} hand to wound {a.BestWound * 100f:F0} cm");
            if (a.BestWall < 90f) parts.Add($"{side} hand to wall {a.BestWall * 100f:F0} cm");
            if (a.BestGround < 90f) parts.Add($"{side} hand to ground {a.BestGround * 100f:F0} cm");
        }
        return parts.Count == 0 ? "" : ", closest: " + string.Join(", ", parts);
    }

    // The head turns toward the shooter (at most 60 degrees from where the pose would put it) while it dies and lies hurt.
    private Quaternion LookAtShooter(Body b, Quaternion des, float hurt)
    {
        float w = Mathf.Max(Smooth(0.1f, 0.4f, _t) * (1f - Smooth(_dur, _dur + 0.3f, _t)), 0.7f * hurt);
        if (w <= 0f) return des;
        Vector3 cur = des * _headFwdLocal;
        Vector3 to = _shooterPos - b.Rb.position;
        if (to.sqrMagnitude < 0.01f || cur.sqrMagnitude < 1e-6f) return des;
        Quaternion turn = Quaternion.FromToRotation(cur, to.normalized);
        turn.ToAngleAxis(out float ang, out Vector3 axis);
        if (float.IsNaN(axis.x) || ang < 0.5f) return des;
        if (ang > 180f) ang -= 360f;
        turn = Quaternion.AngleAxis(Mathf.Clamp(ang, -60f, 60f), axis);
        return Quaternion.Slerp(des, turn * des, 0.7f * w);
    }

    // Sitting on the ground: thighs out in front, knees a little bent (the hold pose is standing, so this is the change from it).
    private static Quaternion SitPose(Role role, float w, Vector3 bodyRight)
    {
        switch (role)
        {
            case Role.ThighL: case Role.ThighR: return Quaternion.AngleAxis(-82f * w, bodyRight);
            case Role.CalfL: case Role.CalfR: return Quaternion.AngleAxis(28f * w, bodyRight);
            default: return Quaternion.identity;
        }
    }

    // Holds the sitting body up: pelvis and chest upright (the chest a little back), turned toward the shooter, not sliding.
    private void HoldSitting(Rigidbody pelvis, float wnMax)
    {
        Vector3 face = _hasShooter ? Flat(_shooterPos - pelvis.position) : Vector3.zero;
        Vector3 back = face != Vector3.zero ? -face : Vector3.zero;
        Upright(pelvis, pelvis.rotation * _pelvisUpLocal, (Vector3.up + back * 0.15f).normalized, _sitW, wnMax, 0.5f * _mass);
        var chest = _bodies[_chest].Rb;
        Upright(chest, chest.rotation * _chestUpLocal, (Vector3.up + back * 0.18f).normalized, _sitW, wnMax, 0.3f * _mass);
        if (face != Vector3.zero)
        {
            Vector3 now = Flat(pelvis.rotation * _fwdLocal);
            if (now != Vector3.zero)
            {
                float err = Vector3.SignedAngle(now, face, Vector3.up) * Mathf.Deg2Rad;
                float wy = pelvis.angularVelocity.y;
                float wn = Mathf.Min(6f, wnMax);
                pelvis.AddTorque(Vector3.up * ((wn * wn * err - 2f * wn * wy) * _sitW * 0.5f * _mass * 0.09f));
            }
        }
        // sitting still: horizontal drift damped
        Vector3 vH = Flat3(pelvis.velocity);
        pelvis.AddForce(Vector3.ClampMagnitude(-_mass * 3f * vH, _mass * 4f) * _sitW);
    }

    // Lying hurt: the body curls around its middle, around its own right axis (it may lie on its back, front or side).
    private static Quaternion Curl(Role role, float c, Vector3 bodyRight)
    {
        if (c <= 0f) return Quaternion.identity;
        switch (role)
        {
            case Role.CalfL: case Role.CalfR: return Quaternion.AngleAxis(70f * c, bodyRight);
            case Role.ThighL: case Role.ThighR: return Quaternion.AngleAxis(-60f * c, bodyRight);
            case Role.Spine: return Quaternion.AngleAxis(15f * c, bodyRight);
            case Role.Head: case Role.Neck: return Quaternion.AngleAxis(20f * c, bodyRight);
            default: return Quaternion.identity;
        }
    }

    // The arm that reaches out along the ground while lying hurt: the free one (not on the wound or a wall).
    private int ReachOutArm()
    {
        int a = _reachOutArm;
        if (a == _wallArm) a = 1 - a;
        return _arms[a].Ok ? a : -1;
    }

    // A hand reaching out past the head, along the ground.
    private void ReachOut(Arm a, float hurt)
    {
        var head = _head >= 0 ? _bodies[_head].Rb.position : _bodies[_chest].Rb.position;
        var pelvis = _bodies[_pelvis].Rb.position;
        Vector3 along = Flat3(head - pelvis);
        if (along.sqrMagnitude < 0.01f) return;
        along.Normalize();
        Vector3 side = Vector3.Cross(Vector3.up, along) * a.Side;
        if (_reachToShooter)
        {
            // toward the one who shot it, from the chest
            var chest = _bodies[_chest].Rb.position;
            Vector3 to = Flat3(_shooterPos - chest);
            if (to.sqrMagnitude > 0.01f)
            {
                to.Normalize();
                head = chest + to * 0.25f;
                along = to;
                side = Vector3.Cross(Vector3.up, along) * (a.Side * 0.4f);
            }
        }
        Vector3 p = head + along * 0.35f + side * 0.25f;
        float gy = _groundY;
        if (Physics.Raycast(p + Vector3.up * 0.8f, Vector3.down, out var hit, 2f, WorldMask, QueryTriggerInteraction.Ignore)) gy = hit.point.y;
        p.y = gy + 0.08f;
        a.Goal = Goal.Ground;
        a.Target = p;
        a.W = 0.7f * hurt;
    }

    // Two-bone IK for an arm going to its goal: target rotations for the upper arm and forearm, blended in.
    private Quaternion Reach(Body b, Arm a, Quaternion holdDes, Vector3 right, Vector3 fwd, ref float k)
    {
        float w = a.W;
        if (w <= 0f) return holdDes;
        Vector3 s = _bodies[a.Up].Rb.position, e = _bodies[a.Fore].Rb.position;
        Vector3 h = a.Palm != null ? a.Palm.position : e + (e - s).normalized * a.ForeLen;
        Vector3 target = a.Target;

        Vector3 toT = target - s;
        float dist = Mathf.Clamp(toT.magnitude, 0.05f, a.UpLen + a.ForeLen - 0.01f);
        Vector3 dir = toT.sqrMagnitude > 1e-8f ? toT.normalized : fwd;
        float cosA = Mathf.Clamp((a.UpLen * a.UpLen + dist * dist - a.ForeLen * a.ForeLen) / (2f * a.UpLen * dist), -1f, 1f);
        float sinA = Mathf.Sqrt(1f - cosA * cosA);
        Vector3 side = right * a.Side;
        Vector3 pole = -Vector3.up + side * 0.6f - fwd * 0.3f;
        Vector3 poleOrtho = pole - Vector3.Dot(pole, dir) * dir;
        if (poleOrtho.sqrMagnitude < 1e-6f) poleOrtho = side;
        Vector3 elbowDir = cosA * dir + sinA * poleOrtho.normalized;
        Vector3 e2 = s + elbowDir * a.UpLen;

        Quaternion ik = b.Role is Role.UpperL or Role.UpperR
            ? Quaternion.FromToRotation(e - s, e2 - s) * b.Rb.rotation
            : Quaternion.FromToRotation(h - e, target - e2) * b.Rb.rotation;
        float hold = a.Goal == Goal.Ground ? 0.6f : 0.8f;   // a catch is weaker than a held wound
        k = Mathf.Max(k, hold * w * Plugin.MuscleScale.Value);
        return Quaternion.Slerp(holdDes, ik, w);
    }

    // ---------------------------------------------------------------- legs

    // A foot that falls behind the stumbling body is swung under it.
    private void CatchStep(float dt, Rigidbody pelvis, Vector3 vH, Vector3 right, float support)
    {
        Vector3 under = new Vector3(pelvis.position.x, _groundY, pelvis.position.z);
        if (_stepLeg < 0)
        {
            _stepRest -= dt;
            if (_stepRest > 0f || support < 0.35f) return;
            float offL = Flat3(_ankleL.position - under).magnitude, offR = Flat3(_ankleR.position - under).magnitude;
            if (Mathf.Max(offL, offR) < Mathf.Lerp(0.28f, 0.2f, Mathf.InverseLerp(1f, 3.5f, vH.magnitude))) return;   // fast: step sooner
            _stepLeg = offL > offR ? 0 : 1;
            _stepT = 0f;
            // 0.17.5 (high Stumble values looked like sliding back): a fast push takes quicker, longer, stronger steps, aimed where
            // the pelvis will be when the foot lands
            _stepFast = Mathf.InverseLerp(1f, 3.5f, vH.magnitude);
            _stepSwing = Mathf.Lerp(0.28f, 0.2f, _stepFast);
            Vector3 lateral = right * (_stepLeg == 0 ? -0.1f : 0.1f);
            _stepTarget = under + vH * (_stepSwing + 0.05f) + Flat3(_stumbleDir) * 0.12f + lateral;
        }
        _stepT += dt;
        var calf = _bodies[_stepLeg == 0 ? _calfL : _calfR].Rb;
        var ankle = _stepLeg == 0 ? _ankleL : _ankleR;
        float s = Mathf.Clamp01(_stepT / _stepSwing);
        _stepTarget.y = _groundY;   // keep tracking the ground the body started on
        Vector3 goal = _stepTarget + Vector3.up * (0.06f + 0.12f * Mathf.Sin(Mathf.PI * s));
        // damped against the body's own speed, not the ground: a foot moving along with a fast stumble is not braked
        Vector3 v = calf.GetPointVelocity(ankle.position) - vH;
        Vector3 f = calf.mass * (300f * (goal - ankle.position) - 35f * v);
        calf.AddForceAtPosition(Vector3.ClampMagnitude(f, calf.mass * (60f + 90f * _stepFast)) * support, ankle.position);
        if (_stepT >= _stepSwing)
        {
            _stepLeg = -1;
            _stepRest = Mathf.Lerp(0.08f, 0.02f, _stepFast);
        }
    }

    // 0.16.0 (user: getting up "warps"): EFT's prone lies face down, so a body on its back turned over inside the blend. Before
    // it gets up, a body lying face up rolls itself onto its side, then its front, with its own muscles (as Euphoria's get-ups start).
    private void RollOver(float wnMax)
    {
        if (_roll < 0) return;
        float start = _hurtTo - 1.0f;
        if (_t < start) return;
        var chest = _bodies[_chest].Rb;
        var pelvis = _bodies[_pelvis].Rb;
        Vector3 front = chest.rotation * _chestFwdLocal;
        Vector3 chestRight = Flat(chest.rotation * Vector3.Cross(_chestUpLocal, _chestFwdLocal));
        if (_roll == 0)
        {
            if (front.y < 0.3f) { _roll = -1; return; }   // on its front or side already
            _roll = 1;
            _hurtTo += 0.6f;   // the roll takes its time before the hand-back
            // roll toward the shooter when it is known (it ends facing the fight), else either way
            _rollSide = _hasShooter && chestRight != Vector3.zero ? (Vector3.Dot(Flat3(_shooterPos - chest.position), chestRight) >= 0f ? 1f : -1f)
                                                                  : (_rnd.Next(2) == 0 ? -1f : 1f);
            Plugin.Log.LogInfo($"[EuphoriaStylePhysics] {_name}: lies face up (front {front.y:F2}), rolls onto its front before getting up");
        }
        float w = Smooth(start, start + 0.3f, _t) * 0.6f;
        // first onto the side (front horizontal), then face down
        Vector3 want = front.y > 0.35f && chestRight != Vector3.zero ? chestRight * _rollSide : Vector3.down;
        Upright(chest, front, want, w, wnMax, 0.3f * _mass);
        Upright(pelvis, pelvis.rotation * _fwdLocal, want, w, wnMax, 0.5f * _mass);
        if (front.y < -0.6f && !_loggedRoll)
        {
            _loggedRoll = true;
            Plugin.Log.LogInfo($"[EuphoriaStylePhysics] {_name}: on its front after {_t - start:F2} s of rolling");
        }
    }

    // Down but alive (a knockdown): it rolls a little and draws a knee up now and then; the dead lie still.
    private void Writhe(Vector3 bodyRight)
    {
        if (_t < _writheNext) return;
        _writheNext = _t + Rand(0.7f, 1.3f);
        var pelvis = _bodies[_pelvis].Rb;
        Vector3 along = pelvis.rotation * _pelvisUpLocal;   // the body's long axis, lying or not
        pelvis.AddTorque(along * ((_rnd.Next(2) == 0 ? -1f : 1f) * Rand(0.8f, 1.5f)), ForceMode.VelocityChange);
        int thigh = _rnd.Next(2) == 0 ? _thighL : _thighR;
        if (thigh >= 0) _bodies[thigh].Rb.AddTorque(bodyRight * -Rand(1f, 2f), ForceMode.VelocityChange);
    }

    private static readonly List<Body> TwitchCandidates = new List<Body>();

    private void Twitch()
    {
        if (_t < _throesNext) return;
        // 0.17.5 (bodies vibrating): a kick every 0.12-0.4 s at 4 rad/s on hands and feet read as a buzz, not a spasm. Now one every
        // 0.35-0.9 s, half as hard, on forearms, calves and the head only (hands and feet are too light).
        _throesNext = _t + Rand(0.35f, 0.9f);
        float fade = 1f - Smooth(_throesFrom, _throesTo, _t);
        var candidates = TwitchCandidates;
        candidates.Clear();
        foreach (var b in _bodies)
            if ((b.Role is Role.ForeL or Role.ForeR or Role.CalfL or Role.CalfR or Role.Head) && !Shrunk(b.Rb.transform))
                candidates.Add(b);
        if (candidates.Count == 0) return;
        var pick = candidates[_rnd.Next(candidates.Count)];
        Vector3 axis = new Vector3(Rand(-1f, 1f), Rand(-1f, 1f), Rand(-1f, 1f)).normalized;
        pick.Rb.AddTorque(axis * (2f * _throesPower * fade), ForceMode.VelocityChange);
    }

    // massShare: the mass this body keeps upright above it (pelvis ~ half the body, chest ~ the upper third), at ~0.3 m.
    private static void Upright(Rigidbody rb, Vector3 upNow, Vector3 upWant, float support, float wnMax, float massShare)
    {
        Vector3 err = Vector3.Cross(upNow, upWant);   // sin(angle) * axis
        Vector3 w = rb.angularVelocity;
        Vector3 wTilt = w - Vector3.Dot(w, upNow) * upNow;
        float wn = Mathf.Min(9f, wnMax);
        Vector3 alpha = Vector3.ClampMagnitude(wn * wn * err - 2f * wn * wTilt, 200f) * support;
        rb.AddTorque(alpha * (massShare * 0.09f));
    }

    // ---------------------------------------------------------------- helpers

    private static Role RoleOf(string name)
    {
        string n = name.ToLowerInvariant();
        if (n.Contains("pelvis")) return Role.Pelvis;
        if (n.Contains("lthigh")) return Role.ThighL;
        if (n.Contains("rthigh")) return Role.ThighR;
        if (n.Contains("lcalf")) return Role.CalfL;
        if (n.Contains("rcalf")) return Role.CalfR;
        if (n.Contains("lfoot")) return Role.FootL;
        if (n.Contains("rfoot")) return Role.FootR;
        if (n.Contains("lupperarm")) return Role.UpperL;
        if (n.Contains("rupperarm")) return Role.UpperR;
        if (n.Contains("lforearm")) return Role.ForeL;
        if (n.Contains("rforearm")) return Role.ForeR;
        if (n.Contains("lpalm")) return Role.HandL;
        if (n.Contains("rpalm")) return Role.HandR;
        if (n.Contains("head")) return Role.Head;
        if (n.Contains("neck")) return Role.Neck;
        if (n.Contains("spine") || n.Contains("ribcage")) return Role.Spine;
        return Role.Other;
    }

    private static bool IsLeg(Role r, int side) =>
        side < 0 ? r is Role.ThighL or Role.CalfL or Role.FootL : r is Role.ThighR or Role.CalfR or Role.FootR;

    private static int ArmOf(Role r) => r switch
    {
        Role.UpperL or Role.ForeL => 0,
        Role.UpperR or Role.ForeR => 1,
        _ => -1
    };

    private int Depth(int i)
    {
        int d = 0;
        for (int guard = 0; i >= 0 && guard < 64; guard++, d++) i = _bodies[i].Parent;
        return d;
    }

    private Rigidbody Nearest(Vector3 p)
    {
        if (p == Vector3.zero) return null;
        Rigidbody best = null;
        float bd = 0.5f * 0.5f;
        foreach (var b in _bodies)
        {
            float dd = (b.Rb.worldCenterOfMass - p).sqrMagnitude;
            if (dd < bd) { bd = dd; best = b.Rb; }
        }
        return best;
    }

    private static Transform FindChild(Transform root, string token)
    {
        foreach (Transform c in root)
        {
            if (c.name.ToLowerInvariant().Contains(token)) return c;
            var deeper = FindChild(c, token);
            if (deeper != null) return deeper;
        }
        return null;
    }

    private static Vector3 RotErr(Quaternion des, Quaternion cur)
    {
        Quaternion q = des * Quaternion.Inverse(cur);
        if (q.w < 0f) q = new Quaternion(-q.x, -q.y, -q.z, -q.w);
        q.ToAngleAxis(out float ang, out Vector3 axis);
        if (ang < 1e-3f || float.IsNaN(axis.x) || float.IsInfinity(axis.x)) return Vector3.zero;
        if (ang > 180f) ang -= 360f;
        return axis * (ang * Mathf.Deg2Rad);
    }

    private static float Inertia(Rigidbody rb)
    {
        Vector3 i = rb.inertiaTensor;
        return Mathf.Max(0.002f, (i.x + i.y + i.z) / 3f);
    }

    private static Vector3 Flat(Vector3 v)
    {
        v.y = 0f;
        return v.sqrMagnitude < 1e-6f ? Vector3.zero : v.normalized;
    }

    private static Vector3 Flat3(Vector3 v)
    {
        v.y = 0f;
        return v;
    }

    private static float Smooth(float a, float b, float t) => b <= a ? (t >= b ? 1f : 0f) : Mathf.SmoothStep(0f, 1f, (t - a) / (b - a));

    // 0.12.0 (user: "more consistent"): Consistency pulls every random range toward its middle. 0.20.0: one Variety per section
    // (consistency = 1 - that section's Variety): a knockdown uses 4. Knockdowns', a death 2. Dying's
    private float Cons => 1f - Mathf.Clamp01((_knock ? Plugin.KnockdownVariety : Plugin.DyingVariety).Value);
    private float Rand(float a, float b)
    {
        float mid = 0.5f * (a + b), half = 0.5f * (b - a) * (1f - Cons);
        return mid + ((float)_rnd.NextDouble() * 2f - 1f) * half;
    }

    // a chance pulled toward "always" by low Variety
    private bool Often(float chance) => _rnd.NextDouble() < Mathf.Lerp(chance, 1f, Cons);
}
