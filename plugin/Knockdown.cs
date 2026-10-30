using System;
using System.Collections.Generic;
using EFT;
using EFT.AssetsManager;
using EFT.Ballistics;
using UnityEngine;

namespace EuphoriaStylePhysics;

// 0.9.0 physics knockdown (user: "physics only"; local raids first, the headless host later). A living bot hit hard goes
// down on its own ragdoll and gets back up:
// - every living player already carries its ragdoll: EFT creates the RigidbodySpawner bodies (which are its hit colliders)
//   and their CharacterJoints at spawn and keeps the bodies kinematic (PlayerPoolObject.CreateRigidbodiesForRagdoll);
//   a knockdown makes them dynamic and drives them with the dying muscles (DeathMotion in knockdown mode: stumble, arms
//   out, fall, lie hurt), so rounds still hit the body where it lies;
// - nothing else may move the bones meanwhile: CustomAnimationsAreProcessing (EFT's own switch, used by the hideout gym)
//   skips VisualPass (IK, procedural aim), and our prefixes skip Player.BodyUpdate (body animator) and MovementUpdate;
// - the bot cannot fire: a prefix on FirearmController.SetTriggerPressed (the call vanilla bots and SAIN both end in);
// - 0.9.3 (user: "knocked down, then floated back up to his floating gun", and a scav that would not shoot after it):
//   the weapon code is left running (pausing ArmsUpdate/ArmsLateUpdate dropped its animation events and could leave the
//   gun unable to fire), the gun follows the right hand while down, and the bot gets up through EFT's own prone: the body
//   holds where it lies while EFT goes prone, blends into the prone pose on the ground, then stands up with EFT's own
//   stand-up animation. Where prone is refused (no room), the old blend from lying to standing is used.
// Only where the bot's AI runs (it has a BotOwner).
internal static class Knockdown
{
    private enum Phase { Down, ToProne, Standing, Blend, Downed }

    private sealed class State
    {
        public Player Player;
        public Phase Phase;
        public DeathMotion Motion;
        public readonly List<Rigidbody> Bodies = new List<Rigidbody>();
        public readonly List<(GameObject go, int layer)> Layers = new List<(GameObject, int)>();
        public readonly List<Collider> Ignored = new List<Collider>();
        public Collider Capsule;
        public bool PrevCustom, AnimatorWasOn, AnimatorTouched;
        public float DownAt, T, BlendFrom, BlendDur;
        // 0.9.5 (user: "just 1 or 2 misfires is fine"): a reflex pull as it goes down, then the trigger is let go
        public float MisfireUntil;
        public bool Released;
        public bool StandAsked;
        public bool Supported;          // our bodies registered with EFT's physics (only ours: a corpse registers its own)
        public bool WorldOn;            // holds a share of the hit-layer/world collision switch
        // 0.13.0 (user: a badly hurt leg -> stays down and fires back): lies in EFT's prone and fights a while
        public bool StayDown;
        public float DownedUntil;
        public bool SpeedTouched;
        public bool Sits;               // 0.14.0: sits up and fights instead of getting up at once
        public bool Remote;             // 0.15.0: a client's copy of a bot the host knocked down (visual only)
        public int OwnColliders;
        public float NextBurst, TriggerUntil;
        public bool TriggerHeld;
        public int Bursts;
        public float NextVoice;
        // the gun, kept in the right hand while down (EFT places it in VisualPass, which is off then)
        public Transform Weapon, Palm;
        public Vector3 WeaponOffPos;
        public Quaternion WeaponOffRot;
        public Quaternion AimFix = Quaternion.identity;   // weapon root rotation relative to where its muzzle points
        public Vector3 WeaponLayPos;
        public Quaternion WeaponLayRot;
        // the pose it lay in, for the blend back
        public Transform[] Bones;
        public Quaternion[] LayRot;
        public Vector3 LayPelvis;
        public Quaternion[] AnimRot;
        // 0.16.0 (user: "they just kinda warp there"): where the root was put to get up, to log how far the AI moved it after
        public Vector3 UpRoot;
        public bool Paused;
        public int Refused;   // AI trigger presses blocked while down
    }

    // 0.13.0 (user: get up more smoothly): EFT's prone transition is sped up, the blend into it is longer
    private const float ProneWaitMax = 1.6f, ProneBlend = 1.1f, StandWaitMax = 2.5f, ProneTransitionBoost = 3f;

    private static readonly Dictionary<Player, State> States = new Dictionary<Player, State>();
    private static readonly Dictionary<Player, (float sum, float at)> Impact = new Dictionary<Player, (float, float)>();
    private static readonly Dictionary<Player, float> LastDown = new Dictionary<Player, float>();
    private static readonly List<(Player p, DamageInfo info, EBodyPart part)> Pending = new List<(Player, DamageInfo, EBodyPart)>();
    private static readonly List<Player> Drop = new List<Player>();
    private static System.Random _rnd = new System.Random();
    private static int _count, _errors;
    private static bool _loggedLayers;
    // 0.14.1 (user: could not shoot them while down): the colliders keep EFT's hit layer (moving them to Deadbody may have kept
    // rounds from counting); instead the hit layer is made to collide with the world while any knockdown is on, then restored
    private static int _worldUsers;
    private static readonly List<(int a, int b)> _worldPairs = new List<(int, int)>();

    internal static bool IsFrozen(Player p) => p != null && States.TryGetValue(p, out var s) && s.Phase == Phase.Down;
    internal static bool IsActive(Player p) => p != null && States.ContainsKey(p);
    internal static int Count => States.Count;
    internal static string PhaseOf(Player p) => p != null && States.TryGetValue(p, out var s) ? s.Phase.ToString() : "-";

    // Firing is blocked from the knockdown until it is fully up, except a short reflex window right after it goes down
    // ("Misfire chance": one or two rounds where the falling gun points).
    // 0.16.0 (user: they "continue to shoot at me while their head is buried in the ground"): sitting no longer lets the AI's
    // own trigger through (it fired wherever the slumped gun pointed); only SitFire's aimed bursts pass (OwnPress).
    internal static bool OwnPress;
    internal static bool BlocksFire(Player p)
    {
        if (p == null || OwnPress || !States.TryGetValue(p, out var s) || s.Phase == Phase.Downed || Time.time < s.MisfireUntil) return false;
        if (++s.Refused == 1 || Plugin.VerboseLog.Value)
            Plugin.Log.LogInfo($"[EuphoriaStylePhysics] {p.Profile?.Nickname}: AI trigger blocked while down ({s.Phase}{(s.Motion != null && s.Motion.Sitting ? ", sitting" : "")})");
        return true;
    }

    // 0.16.0: between lying and EFT's prone the AI may not turn or crawl the bot (its pose would slide away from the body)
    internal static bool HoldsStill(Player p) => p != null && Plugin.KnockdownHoldStill.Value && States.TryGetValue(p, out var s) && !s.Remote &&
                                                 (s.Phase == Phase.ToProne || s.Phase == Phase.Blend);

    // ---------------------------------------------------------------- trigger

    internal static void OnHit(Player player, DamageInfo shot, EBodyPart part)
    {
        if (!Plugin.KnockdownEnabled.Value || _errors > 20) return;
        if (player == null || player.IsYourPlayer || !player.IsAI || player.AIData?.BotOwner == null || IsActive(player)) return;
        if (player.HealthController == null || !player.HealthController.IsAlive) return;
        float now = Time.time;
        if (LastDown.TryGetValue(player, out float last) && now - last < Plugin.KnockdownCooldown.Value) return;
        // hits add up over 0.4 s (a burst, shotgun pellets); a leg hit counts half again
        float add = Mathf.Max(shot.Damage, 8f) * (part is EBodyPart.LeftLeg or EBodyPart.RightLeg ? 1.5f : 1f);
        if (Impact.TryGetValue(player, out var imp) && now - imp.at < 0.4f) add += imp.sum;
        else imp.at = now;
        Impact[player] = (add, imp.at);
        if (Impact.Count > 128) Impact.Clear();
        if (add < Plugin.KnockdownImpact.Value) return;
        Impact.Remove(player);
        LastDown[player] = now;   // a failed roll also waits for the cooldown, or every next round would roll again
        if (_rnd.NextDouble() >= Plugin.KnockdownChance.Value) return;
        // start next frame: the hit may still kill it this frame (then the corpse takes over)
        Pending.Add((player, shot, part));
    }

    // 0.10.0: an explosion close to a bot that survives it can knock it down (closeness 1 at the blast .. 0 at the edge).
    internal static bool OnBlast(Player player, Vector3 at, float closeness)
    {
        if (!Plugin.KnockdownEnabled.Value || !Plugin.BlastKnockdown.Value || _errors > 20) return false;
        if (player == null || player.IsYourPlayer || !player.IsAI || player.AIData?.BotOwner == null || IsActive(player)) return false;
        if (player.HealthController == null || !player.HealthController.IsAlive || closeness < 0.25f) return false;
        float now = Time.time;
        if (LastDown.TryGetValue(player, out float last) && now - last < Plugin.KnockdownCooldown.Value) return false;
        if (_rnd.NextDouble() >= Plugin.BlastKnockdownChance.Value) return false;
        LastDown[player] = now;
        Vector3 away = player.Position - at;
        away.y = 0f;
        if (away.sqrMagnitude < 1e-4f) away = player.Transform.rotation * Vector3.back;
        var info = new DamageInfo
        {
            DamageType = EDamageType.Explosion,
            Damage = 60f * closeness,
            HitPoint = player.Position + Vector3.up * 1.2f,
            Direction = (away.normalized + Vector3.up * 0.3f).normalized
        };
        Pending.Add((player, info, EBodyPart.Chest));
        return true;
    }

    // Plugin.Update: start pending knockdowns, walk the getting-up phases.
    internal static void Tick()
    {
        if (Pending.Count > 0)
        {
            var list = Pending.ToArray();
            Pending.Clear();
            foreach (var (p, info, part) in list)
            {
                try { Start(p, info, part); }
                catch (Exception e) { Fail(p, e); }
            }
        }
        if (States.Count == 0) return;
        Drop.Clear();
        foreach (var kv in States)
        {
            var s = kv.Value;
            if (kv.Key == null || s.Player == null) { Drop.Add(kv.Key); continue; }
            if (s.Phase == Phase.Down && s.Sits && !s.Remote) SitFire(s);
            if (s.Phase == Phase.Down && Time.time >= s.NextVoice)
            {
                s.NextVoice = Time.time + 2.5f + (float)_rnd.NextDouble() * 1.5f;
                Say(s.Player, EPhraseTrigger.OnAgony);
            }
            if (!s.Released && !s.Remote && Time.time >= s.MisfireUntil)
            {
                // let the trigger go: a held trigger (full auto) would keep firing without a new press
                s.Released = true;
                try { (s.Player.HandsController as Player.FirearmController)?.SetTriggerPressed(false); }
                catch (Exception e) { Plugin.Log.LogWarning($"[EuphoriaStylePhysics] trigger release failed: {e.Message}"); }
                if (s.MisfireUntil > 0f) Plugin.Log.LogInfo($"[EuphoriaStylePhysics] {s.Player.Profile?.Nickname}: misfire window over, trigger released");
            }
            try { StepGetUp(s, Time.deltaTime); }
            catch (Exception e) { Drop.Add(kv.Key); Plugin.Log.LogError($"[EuphoriaStylePhysics] getting up failed: {e}"); }
        }
        foreach (var p in Drop)
            if (States.TryGetValue(p, out var s) && (s.Phase == Phase.Standing || s.Phase == Phase.Blend || s.Player == null))
            {
                States.Remove(p);
                Resume(s);
                if (p != null && s.Player != null) Plugin.Log.LogInfo($"[EuphoriaStylePhysics] {p.Profile?.Nickname} is back on its feet ({Time.time - s.DownAt:F1} s after going down, " +
                                                                      $"root moved {(s.UpRoot != Vector3.zero ? Vector3.Distance(s.UpRoot, p.Position) : 0f):F2} m while getting up)");
            }
            else { if (States.TryGetValue(p, out var d)) Resume(d); States.Remove(p); }
    }

    private static void Resume(State s)
    {
        if (!s.Paused) return;
        s.Paused = false;
        try { s.Player?.AIData?.BotOwner?.Mover?.MovementResume(); }
        catch (Exception e) { Plugin.Log.LogWarning($"[EuphoriaStylePhysics] bot movement not resumed: {e.Message}"); }
    }

    // On a Fika client: the host knocked this bot down; play it on our copy (no AI here, the host moves it).
    internal static void StartRemote(Player p, Vector3 hitPoint, Vector3 dir, float damage, EBodyPart part, bool blast, float sitFor, Vector3? shooter)
    {
        if (!Plugin.Enabled.Value || !Plugin.KnockdownEnabled.Value) return;
        var info = new DamageInfo
        {
            DamageType = blast ? EDamageType.Explosion : EDamageType.Bullet,
            Damage = damage,
            HitPoint = hitPoint,
            Direction = dir
        };
        try { Start(p, info, part, true, sitFor, shooter); }
        catch (Exception e) { Fail(p, e); }
    }

    // On a Fika client: the host says this bot gets up now.
    internal static void RemoteGetUp(Player p)
    {
        if (p == null || !States.TryGetValue(p, out var s) || !s.Remote || s.Phase != Phase.Down) return;
        if (s.Motion != null) s.Motion.FinishNow();
        else GetUp(s);
    }

    private static void Start(Player p, DamageInfo info, EBodyPart part, bool remote = false, float remoteSit = 0f, Vector3? shooter = null)
    {
        if (p == null || IsActive(p) || p.HealthController == null || !p.HealthController.IsAlive) return;
        var mc = p.MovementContext;
        if (mc == null) return;
        if (!remote && (mc.IsInPronePose || mc.StationaryWeapon != null || mc.IsInMountedState)) return;
        string state = mc.CurrentState != null ? mc.CurrentState.Name.ToString() : "";
        foreach (var busy in new[] { "Ladder", "Climb", "Vault", "Jump", "Fall", "Transit", "Breach", "Sit" })
            if (!remote && state.IndexOf(busy, StringComparison.OrdinalIgnoreCase) >= 0)
            {
                Plugin.Log.LogInfo($"[EuphoriaStylePhysics] knockdown of {p.Profile?.Nickname} skipped: busy ({state})");
                return;
            }
        var pool = p.GetComponent<PlayerPoolObject>();
        if (pool == null || pool.RigidbodySpawners == null) return;

        var s = new State { Player = p, Phase = Phase.Down, DownAt = Time.time, Remote = remote };
        // a misfire: the trigger may be pulled for 0.12-0.22 s (one round semi-auto, about two full-auto)
        s.MisfireUntil = _rnd.NextDouble() < Plugin.KnockdownMisfireChance.Value ? Time.time + 0.12f + (float)_rnd.NextDouble() * 0.1f : 0f;
        foreach (var sp in pool.RigidbodySpawners)
        {
            var rb = sp != null ? sp.Rigidbody : null;
            if (rb == null)
            {
                Plugin.Log.LogWarning($"[EuphoriaStylePhysics] knockdown of {p.Profile?.Nickname} skipped: a ragdoll body is missing");
                return;
            }
            s.Bodies.Add(rb);
        }

        // ragdoll colliders must hit the world: EFT's hit-collider layer does not, so that layer pair is switched on while bots are down
        int terrain = LayerMask.NameToLayer("Terrain"), highPoly = LayerMask.NameToLayer("HighPolyCollider");
        s.Capsule = p.CharacterController?.GetCollider();
        foreach (var rb in s.Bodies)
        {
            foreach (var c in rb.GetComponents<Collider>())
            {
                if (c == null || c.isTrigger) continue;
                int layer = c.gameObject.layer;
                bool noWorld = (terrain >= 0 && Physics.GetIgnoreLayerCollision(layer, terrain)) ||
                               (highPoly >= 0 && Physics.GetIgnoreLayerCollision(layer, highPoly));
                if (!_loggedLayers)
                {
                    _loggedLayers = true;
                    Plugin.Log.LogInfo($"[EuphoriaStylePhysics] knockdown: hit collider layer {LayerMask.LayerToName(layer)} " +
                                       $"{(noWorld ? "does not collide with the world -> made to while knockdowns are on (colliders stay hittable)" : "collides with the world")}");
                }
                if (noWorld && !s.WorldOn) WorldOn(s, layer);
                if (s.Capsule != null)
                {
                    Physics.IgnoreCollision(c, s.Capsule, true);
                    s.Ignored.Add(c);
                }
            }
        }

        // the gun rides in the right hand while down
        var pb = p.PlayerBones;
        var pelvisBone = pb?.Pelvis?.Original;
        var weapon = pb?.WeaponRoot?.Original;
        var palm = pb?.RightPalm;
        if (weapon != null && palm != null && (pelvisBone == null || !weapon.IsChildOf(pelvisBone)))
        {
            s.Weapon = weapon;
            s.Palm = palm;
            s.WeaponOffRot = Quaternion.Inverse(palm.rotation) * weapon.rotation;
            s.WeaponOffPos = Quaternion.Inverse(palm.rotation) * (weapon.position - palm.position);
            // which way the muzzle points in the weapon root's frame (EFT's fireport axes are not the root's forward)
            var fp = pb.Fireport?.Original;
            Vector3 muzzle = fp != null ? fp.position - weapon.position : weapon.forward;
            if (fp != null && muzzle.sqrMagnitude > 1e-4f)
                s.AimFix = Quaternion.Inverse(Quaternion.LookRotation(muzzle.normalized, weapon.up)) * weapon.rotation;
        }

        // a body's own hit colliders (and armour plates) overlap each other: they must not push each other apart
        var bodySet = new HashSet<Rigidbody>(s.Bodies);
        var own = new List<Collider>();
        foreach (var c in p.GetComponentsInChildren<Collider>())
            if (c != null && !c.isTrigger && c.attachedRigidbody != null && bodySet.Contains(c.attachedRigidbody)) own.Add(c);
        for (int i = 0; i < own.Count; i++)
            for (int j = i + 1; j < own.Count; j++)
                Physics.IgnoreCollision(own[i], own[j], true);
        s.OwnColliders = own.Count;

        // nothing but physics moves the bones while down (the weapon code keeps running: its trigger is blocked instead)
        s.PrevCustom = p.CustomAnimationsAreProcessing;
        p.CustomAnimationsAreProcessing = true;
        if (p.BodyUpdateMode == Player.EUpdateMode.Auto && p.BodyAnimatorCommon != null)
        {
            s.AnimatorTouched = true;
            s.AnimatorWasOn = p.BodyAnimatorCommon.enabled;
            p.BodyAnimatorCommon.enabled = false;
        }
        States[p] = s;

        Vector3 vel = Vector3.zero;
        try { vel = p.Velocity; } catch { }
        foreach (var rb in s.Bodies)
        {
            rb.isKinematic = false;
            rb.velocity = vel;
            // 0.15.1 (log: a knocked-down bot got up 17 m from where it stood; user: bodies elsewhere, can't loot): the same
            // push-apart cap EFT gives its corpses, or overlapping hit colliders blow the body apart
            rb.maxDepenetrationVelocity = EFTHardSettings.Instance.CorpseMaxDepenetrationVelocity;
            rb.WakeUp();
            // 0.12.1 (user: ragdolls away from where the bot is, limbs doing nothing, 15-21 s knockdowns): EFT steps physics only
            // while it has registered moving bodies (corpses register theirs); unregistered, a knocked-down body froze
            PhysicsExtensions.UpdateController.SupportRigidbody(rb, 0f);
        }
        s.Supported = true;
        // down but alive: it says so (EFT's own pain lines); a dead body is silent
        Say(p, EPhraseTrigger.OnBeingHurt);
        s.NextVoice = Time.time + 2f + (float)_rnd.NextDouble() * 1.5f;
        if (info.HitPoint != Vector3.zero && info.Direction.sqrMagnitude > 1e-6f)
        {
            var hitRb = info.HitCollider != null ? info.HitCollider.attachedRigidbody : null;
            if (hitRb == null || !s.Bodies.Contains(hitRb)) hitRb = s.Bodies[0];
            hitRb.AddForceAtPosition(info.Direction.normalized * Mathf.Clamp(10f + info.Damage * 0.2f, 10f, 30f), info.HitPoint, ForceMode.Impulse);
        }

        // a badly hurt leg: sit and fight (or stay prone, see GetUp)
        float sitFor = remote ? remoteSit : 0f;
        if (remote) s.Sits = sitFor > 0f;
        else if (Plugin.StayDownEnabled.Value && Plugin.BadLegPose.Value == Plugin.LegPose.Sit && LegBad(p) && _rnd.NextDouble() < Plugin.StayDownChance.Value)
        {
            float min = Plugin.StayDownMin.Value, max = Mathf.Max(min, Plugin.StayDownMax.Value);
            sitFor = min + (float)_rnd.NextDouble() * (max - min);
            s.Sits = true;
        }
        s.Motion = DeathMotion.BeginKnockdown(p, pool, info, part, ok => OnDown(p, ok), sitFor, remote, shooter);
        if (s.Motion == null)
        {
            Plugin.Log.LogWarning($"[EuphoriaStylePhysics] knockdown of {p.Profile?.Nickname}: muscles could not start, getting up at once");
            GetUp(s);
            return;
        }
        _count++;
        // the host tells every client (the shooter's position too: a client's DamageInfo has no player in it)
        if (!remote && Net.IsServer())
            Net.Knock(p, info.HitPoint, info.Direction, info.Damage, part, (info.DamageType & EDamageType.Explosion) != 0, sitFor, ShooterOf(info));
        Plugin.Log.LogInfo($"[EuphoriaStylePhysics] knockdown #{_count}{(remote ? " (from the host)" : "")}: {p.Profile?.Nickname} ({info.DamageType}/{part}, dmg {info.Damage:F0}), " +
                           $"{s.Bodies.Count} bodies, {s.OwnColliders} own colliders kept apart, body animator {(s.AnimatorTouched ? "off" : "manual (skipped)")}, " +
                           $"gun {(s.Weapon != null ? "held in the right hand" : "not moved")}{(s.Sits ? ", leg too hurt: sits up and fights" : "")}");
    }

    // ---------------------------------------------------------------- down -> up

    // DeathMotion (knockdown mode) is over: true = it lay its time, false = interrupted (turned off, a part gone).
    private static void OnDown(Player p, bool lay)
    {
        if (p == null || !States.TryGetValue(p, out var s) || s.Phase != Phase.Down) return;
        if (p.HealthController == null || !p.HealthController.IsAlive) return;   // OnDeath handles it
        try { GetUp(s); }
        catch (Exception e) { Fail(p, e); }
    }

    private static void GetUp(State s)
    {
        var p = s.Player;
        var pb = p.PlayerBones;
        var pelvis = pb?.Pelvis?.Original;

        // the pose it lay in
        if (pelvis != null)
        {
            var bones = new List<Transform>();
            Collect(pelvis, bones);
            s.Bones = bones.ToArray();
            s.LayRot = new Quaternion[s.Bones.Length];
            s.AnimRot = new Quaternion[s.Bones.Length];
            for (int i = 0; i < s.Bones.Length; i++) s.LayRot[i] = s.Bones[i].rotation;
            s.LayPelvis = pelvis.position;
        }
        if (s.Weapon != null)
        {
            s.WeaponLayPos = s.Weapon.position;
            s.WeaponLayRot = s.Weapon.rotation;
        }

        Unsupport(s);
        foreach (var rb in s.Bodies)
            if (rb != null) rb.isKinematic = true;
        WorldOff(s);
        Restore(s);

        Vector3 headDir = Vector3.zero;
        if (pelvis != null)
        {
            var head = pb.Head?.Original;
            if (head != null)
            {
                headDir = head.position - pelvis.position;
                headDir.y = 0f;
            }
            // stand up where it lies: on the ground under the pelvis
            Vector3 at = pelvis.position;
            Vector3 ground = new Vector3(at.x, p.Position.y, at.z);
            if (Physics.Raycast(at + Vector3.up * 0.5f, Vector3.down, out var hit, 3f,
                    LayerMask.GetMask("HighPolyCollider", "LowPolyCollider", "Terrain"), QueryTriggerInteraction.Ignore))
                ground = hit.point;
            float moved = Vector3.Distance(ground, p.Position);
            if (!s.Remote) p.Teleport(ground + Vector3.up * 0.05f);   // a hair above: exactly on the ground it counted as falling (Jump)
            if (!s.Remote) s.UpRoot = ground;
            if (!s.Remote && Plugin.KnockdownHoldStill.Value)
            {
                // the AI keeps its path while down; without a pause it crawls off along it as soon as it is prone
                try
                {
                    var mover = p.AIData?.BotOwner?.Mover;
                    if (mover != null)
                    {
                        mover.Stop();
                        mover.MovementPause(ProneWaitMax + ProneBlend + 0.5f, false);
                        s.Paused = true;
                    }
                }
                catch (Exception e) { Plugin.Log.LogWarning($"[EuphoriaStylePhysics] bot movement not paused: {e.Message}"); }
            }
            else moved = 0f;                                            // the host moves it; Fika brings the position
            // the bones hang off the root that just moved: put the body back where it lay for this frame
            if (s.Bones != null)
            {
                pelvis.position = s.LayPelvis;
                for (int i = 0; i < s.Bones.Length; i++)
                    if (s.Bones[i] != null) s.Bones[i].rotation = s.LayRot[i];
            }
            if (s.Weapon != null) s.Weapon.SetPositionAndRotation(s.WeaponLayPos, s.WeaponLayRot);
            Plugin.Log.LogInfo($"[EuphoriaStylePhysics] {p.Profile?.Nickname} gets up after {Time.time - s.DownAt:F2} s, {moved:F2} m from where it stood");
        }
        if (!s.Remote) Net.GetUp(p);
        if (s.Bones == null)
        {
            States.Remove(p);
            return;
        }

        // face along the body, so EFT's prone lies the way the body lies, then go prone the EFT way
        if (headDir.sqrMagnitude > 0.01f && !s.Remote)
        {
            float yaw = Mathf.Atan2(headDir.x, headDir.z) * Mathf.Rad2Deg;
            p.Rotation = new Vector2(yaw, p.Rotation.y);
        }
        s.T = 0f;
        s.StandAsked = false;
        var mc = p.MovementContext;
        if (Plugin.KnockdownProneGetUp.Value && mc != null && mc.StationaryWeapon == null)
        {
            s.StayDown = !s.Sits && Plugin.StayDownEnabled.Value && Plugin.BadLegPose.Value == Plugin.LegPose.Prone && LegBad(p) &&
                         _rnd.NextDouble() < Plugin.StayDownChance.Value;
            if (!s.Remote) mc.IsInPronePose = true;   // a client waits for the host's prone to arrive
            if (!s.Remote)
            try { mc.PlayerAnimatorTransitionSpeed = mc.TransitionSpeed * ProneTransitionBoost; s.SpeedTouched = true; }
            catch (Exception e) { Plugin.Log.LogWarning($"[EuphoriaStylePhysics] prone transition speed not set: {e.Message}"); }
            s.Phase = Phase.ToProne;
        }
        else
        {
            s.Phase = Phase.Blend;
            s.BlendFrom = 0f;
            s.BlendDur = Plugin.KnockdownRiseTime.Value;
        }
    }

    // Plugin.Update: the phases after the body lay its time.
    private static void StepGetUp(State s, float dt)
    {
        if (s.Phase == Phase.Down) return;
        s.T += dt;
        var p = s.Player;
        var mc = p.MovementContext;
        if (mc == null) { Drop.Add(p); return; }
        string state = mc.CurrentState != null ? mc.CurrentState.Name.ToString() : "";
        switch (s.Phase)
        {
            case Phase.ToProne:
                if (!mc.IsInPronePose && s.T < 1.2f && !s.Remote)
                {
                    // just teleported it may still count as airborne (Jump): keep asking for prone a moment
                    mc.IsInPronePose = true;
                    return;
                }
                if (!mc.IsInPronePose && (!s.Remote || s.T > ProneWaitMax))
                {
                    RestoreSpeed(s, mc);
                    // EFT refused prone here (no room): the old blend from lying to standing
                    Plugin.Log.LogInfo($"[EuphoriaStylePhysics] {p.Profile?.Nickname}: no room to go prone ({state}), blending up instead");
                    s.Phase = Phase.Blend;
                    s.BlendFrom = s.T;
                    s.BlendDur = Plugin.KnockdownRiseTime.Value;
                    return;
                }
                bool proneNow = state == "ProneIdle" || state == "ProneMove";
                if (s.BlendFrom <= 0f && (proneNow || s.T > ProneWaitMax))
                {
                    s.BlendFrom = s.T;
                    s.BlendDur = ProneBlend;
                    if (!proneNow) Plugin.Log.LogInfo($"[EuphoriaStylePhysics] {p.Profile?.Nickname}: not prone after {ProneWaitMax} s ({state}), blending anyway");
                }
                if (s.BlendFrom > 0f && s.T >= s.BlendFrom + s.BlendDur && !s.StandAsked && s.StayDown)
                {
                    RestoreSpeed(s, mc);
                    s.Phase = Phase.Downed;
                    float min = Plugin.StayDownMin.Value, max = Mathf.Max(min, Plugin.StayDownMax.Value);
                    s.DownedUntil = Time.time + min + (float)_rnd.NextDouble() * (max - min);
                    Plugin.Log.LogInfo($"[EuphoriaStylePhysics] {p.Profile?.Nickname}: leg too hurt, stays down and fights for {s.DownedUntil - Time.time:F0} s");
                    break;
                }
                if (s.BlendFrom > 0f && s.T >= s.BlendFrom + s.BlendDur && !s.StandAsked)
                {
                    RestoreSpeed(s, mc);
                    // lying in EFT's prone now: stand up with EFT's own animation
                    s.StandAsked = true;
                    if (!s.Remote)
                    {
                        mc.IsInPronePose = false;
                        mc.UpdatePoseAfterProne();
                    }
                    s.Phase = Phase.Standing;
                    s.T = 0f;
                    Plugin.Log.LogInfo($"[EuphoriaStylePhysics] {p.Profile?.Nickname}: prone, standing up ({state})");
                }
                break;
            case Phase.Downed:
                // the AI may try to stand: keep it on the ground until its time is up, then it struggles up
                if (Time.time >= s.DownedUntil || !Plugin.StayDownEnabled.Value)
                {
                    if (!s.Remote)
                    {
                        mc.IsInPronePose = false;
                        mc.UpdatePoseAfterProne();
                    }
                    s.Phase = Phase.Standing;
                    s.T = 0f;
                    Plugin.Log.LogInfo($"[EuphoriaStylePhysics] {p.Profile?.Nickname}: struggles back up");
                }
                else if (!mc.IsInPronePose && !s.Remote) mc.IsInPronePose = true;
                break;
            case Phase.Standing:
                bool up = !mc.IsInPronePose && state.IndexOf("Prone", StringComparison.OrdinalIgnoreCase) < 0 &&
                          state.IndexOf("Transit", StringComparison.OrdinalIgnoreCase) < 0;
                if ((up && s.T > 0.2f) || s.T > StandWaitMax) Drop.Add(p);
                break;
            case Phase.Blend:
                if (s.T >= s.BlendFrom + s.BlendDur) Drop.Add(p);
                break;
        }
    }

    private static void Restore(State s)
    {
        var p = s.Player;
        foreach (var (go, layer) in s.Layers)
            if (go != null) go.layer = layer;
        s.Layers.Clear();
        if (s.Capsule != null)
            foreach (var c in s.Ignored)
                if (c != null) Physics.IgnoreCollision(c, s.Capsule, false);
        s.Ignored.Clear();
        if (p != null)
        {
            p.CustomAnimationsAreProcessing = s.PrevCustom;
            if (s.AnimatorTouched && p.BodyAnimatorCommon != null) p.BodyAnimatorCommon.enabled = s.AnimatorWasOn;
        }
        s.AnimatorTouched = false;
    }

    // The bot died while down or getting up: EFT's corpse takes the bodies over as they are.
    internal static void OnDeath(Player p)
    {
        if (p == null || !States.TryGetValue(p, out var s)) return;
        States.Remove(p);
        if (s.Motion != null) s.Motion.Abort();
        Unsupport(s);   // our registrations only; the corpse registered the same bodies itself
        WorldOff(s);
        // no Restore: EFT has already turned the animators off and moved every collider to Deadbody for the corpse;
        // turning them back would fight the corpse. Only the capsule exceptions are dropped (the capsule is off anyway).
        if (s.Capsule != null)
            foreach (var c in s.Ignored)
                if (c != null) Physics.IgnoreCollision(c, s.Capsule, false);
        s.Ignored.Clear();
        s.Layers.Clear();
        Plugin.Log.LogInfo($"[EuphoriaStylePhysics] {p.Profile?.Nickname} died {(s.Phase == Phase.Down ? "while down" : "getting up")}");
    }

    // ---------------------------------------------------------------- visuals (Flinch's after-LateUpdate step)

    internal static void ApplyRise(float dt)
    {
        if (States.Count == 0) return;
        foreach (var kv in States)
        {
            var s = kv.Value;
            if (s.Player == null) continue;
            if (s.Phase == Phase.Down)
            {
                // the gun stays in the right hand (persistent: nothing else places it while down)
                if (s.Weapon != null && s.Palm != null && s.Motion != null && s.Motion.Sitting && s.Motion.HasShooter)
                {
                    // sitting: the gun at the right hand, pointed at the shooter (its muzzle is where rounds leave)
                    Vector3 at = s.Palm.position;
                    Vector3 aim = s.Motion.ShooterPos - at;
                    if (aim.sqrMagnitude > 0.01f)
                        s.Weapon.SetPositionAndRotation(Vector3.Lerp(s.Weapon.position, at, 0.5f),
                            Quaternion.Slerp(s.Weapon.rotation, Quaternion.LookRotation(aim.normalized, Vector3.up) * s.AimFix, 0.3f));
                    continue;
                }
                if (s.Weapon != null && s.Palm != null)
                {
                    // half way to the hand each frame: the ragdoll hand jitters, the gun should not
                    Vector3 pos = s.Palm.position + s.Palm.rotation * s.WeaponOffPos;
                    Quaternion rot = s.Palm.rotation * s.WeaponOffRot;
                    s.Weapon.SetPositionAndRotation(Vector3.Lerp(s.Weapon.position, pos, 0.5f), Quaternion.Slerp(s.Weapon.rotation, rot, 0.5f));
                }
                continue;
            }
            if (s.Bones == null || s.Phase == Phase.Standing) continue;

            // how far from where it lay toward the animated pose: 0 while EFT is still going prone, then up to 1
            float w = s.BlendFrom > 0f || s.Phase == Phase.Blend
                ? Mathf.SmoothStep(0f, 1f, Mathf.Clamp01((s.T - s.BlendFrom) / Mathf.Max(0.05f, s.BlendDur)))
                : 0f;
            if (w >= 1f) continue;

            // read the whole animated pose first: writing a parent moves its children's world rotations
            for (int i = 0; i < s.Bones.Length; i++) s.AnimRot[i] = s.Bones[i] != null ? s.Bones[i].rotation : Quaternion.identity;
            var pelvis = s.Bones[0];
            Vector3 animWeaponPos = s.Weapon != null ? s.Weapon.position : Vector3.zero;
            Quaternion animWeaponRot = s.Weapon != null ? s.Weapon.rotation : Quaternion.identity;
            if (pelvis != null)
            {
                Flinch.SavePos(pelvis);
                pelvis.position = Vector3.Lerp(s.LayPelvis, pelvis.position, w);
            }
            for (int i = 0; i < s.Bones.Length; i++)
            {
                var t = s.Bones[i];
                if (t == null) continue;
                Flinch.SaveRot(t);
                t.rotation = Quaternion.Slerp(s.LayRot[i], s.AnimRot[i], w);
            }
            if (s.Weapon != null)
            {
                Flinch.SavePos(s.Weapon);
                Flinch.SaveRot(s.Weapon);
                s.Weapon.SetPositionAndRotation(Vector3.Lerp(s.WeaponLayPos, animWeaponPos, w), Quaternion.Slerp(s.WeaponLayRot, animWeaponRot, w));
            }
        }
    }

    // Sitting: short bursts at the shooter while there is a clear line from the gun to him.
    private static void SitFire(State s)
    {
        var fc = s.Player.HandsController as Player.FirearmController;
        if (fc == null || s.Motion == null) return;
        float now = Time.time;
        if (s.TriggerHeld && now >= s.TriggerUntil)
        {
            s.TriggerHeld = false;
            fc.SetTriggerPressed(false);
        }
        if (!s.Motion.Sitting || !s.Motion.HasShooter || s.TriggerHeld || now < s.NextBurst || s.Weapon == null) return;
        s.NextBurst = now + 0.7f + (float)_rnd.NextDouble() * 0.7f;
        Vector3 from = s.Weapon.position, to = s.Motion.ShooterPos;
        // 0.16.0: only sitting up (not slumped over, head not on the ground) with the muzzle on the shooter
        if (!s.Motion.UprightToFire(out string why))
        {
            if (Plugin.VerboseLog.Value) Plugin.Log.LogInfo($"[EuphoriaStylePhysics] {s.Player.Profile?.Nickname}: holds fire, {why}");
            return;
        }
        Vector3 muzzle = s.Weapon.rotation * Quaternion.Inverse(s.AimFix) * Vector3.forward;
        float off = Vector3.Angle(muzzle, to - from);
        if (off > 20f)
        {
            if (Plugin.VerboseLog.Value) Plugin.Log.LogInfo($"[EuphoriaStylePhysics] {s.Player.Profile?.Nickname}: holds fire, gun {off:F0} deg off the shooter");
            return;
        }
        if (Physics.Linecast(from, to, LayerMask.GetMask("HighPolyCollider", "LowPolyCollider", "Terrain"), QueryTriggerInteraction.Ignore)) return;
        s.TriggerHeld = true;
        s.TriggerUntil = now + 0.12f + (float)_rnd.NextDouble() * 0.13f;
        OwnPress = true;
        try { fc.SetTriggerPressed(true); }
        finally { OwnPress = false; }
        s.Bursts++;
        if (s.Bursts <= 3 || Plugin.VerboseLog.Value)
            Plugin.Log.LogInfo($"[EuphoriaStylePhysics] {s.Player.Profile?.Nickname}: firing from the ground (burst {s.Bursts})");
    }

    private static void WorldOn(State s, int hitLayer)
    {
        s.WorldOn = true;
        if (_worldUsers++ > 0) return;
        _worldPairs.Clear();
        foreach (var name in new[] { "Terrain", "HighPolyCollider", "LowPolyCollider" })
        {
            int w = LayerMask.NameToLayer(name);
            if (w < 0 || !Physics.GetIgnoreLayerCollision(hitLayer, w)) continue;
            Physics.IgnoreLayerCollision(hitLayer, w, false);
            _worldPairs.Add((hitLayer, w));
        }
        Plugin.Log.LogInfo($"[EuphoriaStylePhysics] knockdown: {LayerMask.LayerToName(hitLayer)} now collides with {_worldPairs.Count} world layer(s) while bots are down");
    }

    private static void WorldOff(State s)
    {
        if (!s.WorldOn) return;
        s.WorldOn = false;
        if (--_worldUsers > 0) return;
        _worldUsers = 0;
        foreach (var (a, b) in _worldPairs) Physics.IgnoreLayerCollision(a, b, true);
        _worldPairs.Clear();
    }

    private static Vector3? ShooterOf(DamageInfo info)
    {
        try
        {
            var who = info.Player?.iPlayer;
            if (who != null) return who.Position + Vector3.up * 1.4f;
        }
        catch { }
        return null;
    }

    private static void Unsupport(State s)
    {
        if (!s.Supported) return;   // once only: a second call would take a corpse's own registrations
        s.Supported = false;
        foreach (var rb in s.Bodies)
            if (rb != null) PhysicsExtensions.UpdateController.UnsupportRigidbody(rb);
    }

    private static void RestoreSpeed(State s, MovementContext mc)
    {
        if (!s.SpeedTouched || mc == null) return;
        s.SpeedTouched = false;
        try { mc.PlayerAnimatorTransitionSpeed = mc.TransitionSpeed; } catch { }
    }

    // a leg destroyed, or below the set share of its health
    private static bool LegBad(Player p)
    {
        var hc = p.HealthController;
        if (hc == null) return false;
        foreach (var leg in new[] { EBodyPart.LeftLeg, EBodyPart.RightLeg })
        {
            if (hc.IsBodyPartDestroyed(leg)) return true;
            var h = hc.GetBodyPartHealth(leg);
            if (h.Maximum > 0f && h.Current / h.Maximum < Plugin.StayDownLegHealth.Value) return true;
        }
        return false;
    }

    private static void Say(Player p, EPhraseTrigger what)
    {
        if (!Plugin.KnockdownVoice.Value || p == null || p.Speaker == null) return;
        try { p.Speaker.Play(what, p.HealthStatus, true); }
        catch (Exception e) { Plugin.Log.LogWarning($"[EuphoriaStylePhysics] voice line failed: {e.Message}"); }
    }

    private static void Collect(Transform t, List<Transform> into)
    {
        into.Add(t);
        foreach (Transform c in t) Collect(c, into);
    }

    private static void Fail(Player p, Exception e)
    {
        _errors++;
        Plugin.Log.LogError($"[EuphoriaStylePhysics] knockdown failed ({_errors}{(_errors > 20 ? ", knockdowns off for this session" : "")}): {e}");
        if (p != null && States.TryGetValue(p, out var s))
        {
            States.Remove(p);
            try
            {
                if (s.Motion != null) s.Motion.Abort();
                Unsupport(s);
                foreach (var rb in s.Bodies)
                    if (rb != null) rb.isKinematic = true;
                WorldOff(s);
                Restore(s);
            }
            catch (Exception e2) { Plugin.Log.LogError($"[EuphoriaStylePhysics] knockdown clean-up failed: {e2}"); }
        }
    }
}
