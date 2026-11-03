using System;
using System.Collections.Generic;
using EFT;
using EFT.Ballistics;
using UnityEngine;
using UnityEngine.LowLevel;
using UnityEngine.PlayerLoop;

namespace EuphoriaStylePhysics;

// Living hit flinch: each hit spins the spine chain (and a hit leg) on damped springs around the point the round pushed.
// The spring rotations are put on the bones after every LateUpdate, right before Unity skins the meshes, and taken off
// again at the very start of the next frame, so the game itself (AI, aiming, fireports, hit colliders, physics) never
// sees them: purely visual, on top of EFT's own HitReaction.
internal static class Flinch
{
    private struct ApplyStep { }
    private struct UndoStep { }

    private sealed class Bone
    {
        public Transform T;
        public int Parent = -1;     // index in the rig, -1 for the pelvis
        public float Wn;
        public float Max = 0.6f;     // largest bend, rad
        public Vector3 Theta, Omega; // rotation vector (rad, world space) and its rate
        public Vector3 Hold;         // 0.16.1: where the spring rests this frame (a held lean); zero = the animated pose
    }

    private sealed class Rig
    {
        public Player Player;
        public readonly List<Bone> Bones = new List<Bone>();
        public int Pelvis = -1, Spine1 = -1, Spine2 = -1, Spine3 = -1, Neck = -1, Head = -1, ThighL = -1, ThighR = -1, CalfL = -1, CalfR = -1;
        public int UpperL = -1, UpperR = -1, ForeL = -1, ForeR = -1;
        public float Quiet;
        public Vector3 LastPoint;
        public float LastHitAt;
    }

    private const float Zeta = 0.4f, SubStep = 1f / 120f;

    private static readonly Dictionary<Player, Rig> Rigs = new Dictionary<Player, Rig>();
    private static readonly List<KeyValuePair<Transform, Quaternion>> Saved = new List<KeyValuePair<Transform, Quaternion>>(64);
    private static readonly List<KeyValuePair<Transform, Vector3>> SavedPos = new List<KeyValuePair<Transform, Vector3>>(8);

    // For other visual-only writers in this step (Knockdown's blend back): remember a bone so Undo puts it back.
    internal static void SaveRot(Transform t) => Saved.Add(new KeyValuePair<Transform, Quaternion>(t, t.localRotation));
    internal static void SavePos(Transform t) => SavedPos.Add(new KeyValuePair<Transform, Vector3>(t, t.localPosition));
    private static readonly List<Player> Drop = new List<Player>();
    private static float _lastCheck;
    private static int _hits, _errors;

    // ---------------------------------------------------------------- player loop

    internal static void Install()
    {
        try
        {
            var root = PlayerLoop.GetCurrentPlayerLoop();
            if (Has(root, typeof(ApplyStep))) return;
            bool undo = Insert(ref root, typeof(EarlyUpdate), null, new PlayerLoopSystem { type = typeof(UndoStep), updateDelegate = Undo });
            bool apply = Insert(ref root, typeof(PostLateUpdate), typeof(PostLateUpdate.UpdateAllSkinnedMeshes),
                new PlayerLoopSystem { type = typeof(ApplyStep), updateDelegate = Apply });
            PlayerLoop.SetPlayerLoop(root);
            Plugin.Log.LogInfo($"[EuphoriaStylePhysics] flinch steps in the player loop: undo at EarlyUpdate start {undo}, apply before " +
                               $"UpdateAllSkinnedMeshes {apply}");
        }
        catch (Exception e)
        {
            Plugin.Log.LogError($"[EuphoriaStylePhysics] player loop install failed, no hit flinch: {e}");
            Plugin.FlinchEnabled.Value = false;
        }
    }

    // Another mod (or the game) may set its own player loop later: put ours back.
    internal static void KeepInstalled()
    {
        if (Time.unscaledTime - _lastCheck < 5f) return;
        _lastCheck = Time.unscaledTime;
        if (!Plugin.FlinchEnabled.Value || Has(PlayerLoop.GetCurrentPlayerLoop(), typeof(ApplyStep))) return;
        Undo();
        Plugin.Log.LogWarning("[EuphoriaStylePhysics] flinch steps were gone from the player loop (replaced by someone), putting them back");
        Install();
    }

    private static bool Has(PlayerLoopSystem sys, Type t)
    {
        if (sys.type == t) return true;
        if (sys.subSystemList == null) return false;
        foreach (var s in sys.subSystemList)
            if (Has(s, t)) return true;
        return false;
    }

    // Puts `add` into the phase `phase`, before `before` (or first when null / not found).
    private static bool Insert(ref PlayerLoopSystem root, Type phase, Type before, PlayerLoopSystem add)
    {
        for (int i = 0; i < root.subSystemList.Length; i++)
        {
            if (root.subSystemList[i].type != phase) continue;
            var p = root.subSystemList[i];
            var list = new List<PlayerLoopSystem>(p.subSystemList ?? new PlayerLoopSystem[0]);
            int at = before == null ? 0 : list.FindIndex(s => s.type == before);
            bool found = before == null || at >= 0;
            list.Insert(at < 0 ? 0 : at, add);
            p.subSystemList = list.ToArray();
            root.subSystemList[i] = p;
            return found;
        }
        return false;
    }

    // ---------------------------------------------------------------- hits

    internal static void OnHit(Player player, DamageInfo shot, EBodyPart part)
    {
        if (player == null || player.IsYourPlayer || player.HealthController == null || !player.HealthController.IsAlive) return;
        if (_errors > 20 || Knockdown.IsActive(player)) return;
        var cam = Camera.main;
        if (cam != null && (cam.transform.position - player.Position).sqrMagnitude > Plugin.FlinchMaxDistance.Value * Plugin.FlinchMaxDistance.Value) return;
        Vector3 dir = shot.Direction;
        if (dir.sqrMagnitude < 1e-6f || shot.HitPoint == Vector3.zero) return;
        dir.Normalize();

        if (!Rigs.TryGetValue(player, out var rig))
        {
            rig = Build(player);
            if (rig == null) return;
            Rigs[player] = rig;
        }
        // Fika may report one hit twice (the shooter's own hit, then the host's damage packet)
        if (Time.time - rig.LastHitAt < 0.25f && (rig.LastPoint - shot.HitPoint).sqrMagnitude < 0.0025f) return;
        rig.LastHitAt = Time.time;
        rig.LastPoint = shot.HitPoint;

        int hit = part switch
        {
            EBodyPart.Head => rig.Head,
            EBodyPart.LeftArm when Plugin.FlinchArms.Value => Lower(rig, rig.UpperL, rig.ForeL, shot.HitPoint),
            EBodyPart.RightArm when Plugin.FlinchArms.Value => Lower(rig, rig.UpperR, rig.ForeR, shot.HitPoint),
            EBodyPart.Chest or EBodyPart.LeftArm or EBodyPart.RightArm => rig.Spine3,
            EBodyPart.Stomach => rig.Spine1,
            EBodyPart.LeftLeg => Lower(rig, rig.ThighL, rig.CalfL, shot.HitPoint),
            EBodyPart.RightLeg => Lower(rig, rig.ThighR, rig.CalfR, shot.HitPoint),
            _ => rig.Spine2
        };
        if (hit < 0) hit = rig.Spine3 >= 0 ? rig.Spine3 : rig.Pelvis;
        if (hit < 0) return;

        bool melee = (shot.DamageType & (EDamageType.Melee | EDamageType.Blunt)) != 0;
        float s = Plugin.FlinchScale.Value * Mathf.Clamp(0.35f + shot.Damage / 50f, 0.35f, 1.6f) * (melee ? 0.8f : 1f);
        float gain = 7f * s * (hit == rig.Head && Plugin.FlinchHeadSnap.Value ? 1.4f : 1f), fall = 1f;
        for (int i = hit, guard = 0; i >= 0 && guard < 16; i = rig.Bones[i].Parent, guard++)
        {
            var b = rig.Bones[i];
            Vector3 axis = Vector3.Cross(shot.HitPoint - b.T.position, dir);
            if (axis.sqrMagnitude > 1e-6f) b.Omega += axis.normalized * (gain * fall);
            fall *= 0.6f;
        }
        // whiplash: the head lags behind a torso hit
        if (Plugin.FlinchWhiplash.Value && rig.Head >= 0 && hit != rig.Head && part is EBodyPart.Chest or EBodyPart.Stomach)
        {
            var h = rig.Bones[rig.Head];
            Vector3 axis = Vector3.Cross(Vector3.up, dir);
            if (axis.sqrMagnitude > 1e-6f) h.Omega -= axis.normalized * (0.35f * gain);
        }
        // a leg hit: the knee gives for a moment (no hip drop: the feet hang off the pelvis and would sink into the floor)
        if (Plugin.FlinchLegDip.Value && part is EBodyPart.LeftLeg or EBodyPart.RightLeg)
        {
            Vector3 fwd = player.Transform.rotation * Vector3.forward;
            fwd.y = 0f;
            Vector3 right = fwd.sqrMagnitude > 1e-6f ? Vector3.Cross(Vector3.up, fwd.normalized) : Vector3.right;
            bool left = part == EBodyPart.LeftLeg;
            int calf = left ? rig.CalfL : rig.CalfR, thigh = left ? rig.ThighL : rig.ThighR;
            if (calf >= 0) rig.Bones[calf].Omega += right * (2.5f * s);    // foot back = knee bends
            if (thigh >= 0) rig.Bones[thigh].Omega -= right * (1.2f * s);  // knee forward
        }
        rig.Quiet = 0f;

        _hits++;
        if (Plugin.VerboseLog.Value || _hits <= 3)
            Plugin.Log.LogInfo($"[EuphoriaStylePhysics] flinch #{_hits}: {player.Profile?.Nickname} {part} on {rig.Bones[hit].T.name}, " +
                               $"dmg {shot.Damage:F0} {shot.DamageType}, strength {s:F2}");
    }

    // An explosion the player survives: the spine and head recoil away from it, the knees give a little.
    internal static bool OnBlast(Player player, Vector3 at, float strength)
    {
        if (player == null || player.IsYourPlayer || player.HealthController == null || !player.HealthController.IsAlive || strength <= 0.05f) return false;
        if (Knockdown.IsActive(player)) return false;
        if (_errors > 20) return false;
        var cam = Camera.main;
        if (cam != null && (cam.transform.position - player.Position).sqrMagnitude > Plugin.FlinchMaxDistance.Value * Plugin.FlinchMaxDistance.Value) return false;
        if (!Rigs.TryGetValue(player, out var rig))
        {
            rig = Build(player);
            if (rig == null) return false;
            Rigs[player] = rig;
        }
        Vector3 away = player.Position - at;
        away.y = 0f;
        if (away.sqrMagnitude < 1e-4f) away = player.Transform.rotation * Vector3.back;
        away.Normalize();
        Vector3 axis = Vector3.Cross(Vector3.up, away);   // turning about this tips "up" toward "away"
        float s = strength * Plugin.FlinchScale.Value;
        void Spin(int i, float rad) { if (i >= 0) rig.Bones[i].Omega += axis * rad; }
        Spin(rig.Spine1, 3f * s);
        Spin(rig.Spine2, 3f * s);
        Spin(rig.Spine3, 4f * s);
        Spin(rig.Head, 6f * s);
        Vector3 fwd = player.Transform.rotation * Vector3.forward;
        fwd.y = 0f;
        Vector3 right = fwd.sqrMagnitude > 1e-6f ? Vector3.Cross(Vector3.up, fwd.normalized) : Vector3.right;
        if (rig.CalfL >= 0) rig.Bones[rig.CalfL].Omega += right * (1.5f * s);
        if (rig.CalfR >= 0) rig.Bones[rig.CalfR].Omega += right * (1.5f * s);
        rig.Quiet = 0f;
        if (Plugin.VerboseLog.Value)
            Plugin.Log.LogInfo($"[EuphoriaStylePhysics] blast flinch: {player.Profile?.Nickname} strength {s:F2}");
        return true;
    }

    private static int Lower(Rig rig, int thigh, int calf, Vector3 p)
    {
        if (thigh < 0) return calf;
        if (calf < 0) return thigh;
        return (p - rig.Bones[calf].T.position).sqrMagnitude < (p - rig.Bones[thigh].T.position).sqrMagnitude ? calf : thigh;
    }

    private static Rig Build(Player player)
    {
        var pb = player.PlayerBones;
        if (pb == null) return null;
        var rig = new Rig { Player = player };
        Transform pelvis = pb.Pelvis?.Original;
        Transform spine1 = pb.Spine1;
        Transform spine3 = pb.Spine3?.Original;
        Transform spine2 = spine1 != null ? Find(spine1, "spine2") : null;
        Transform neck = pb.Neck;
        Transform head = pb.Head?.Original;
        Transform thighL = pb.LeftThigh1?.Original, thighR = pb.RightThigh1?.Original;
        if (pelvis == null || spine3 == null)
        {
            Plugin.Log.LogWarning($"[EuphoriaStylePhysics] {player.Profile?.Nickname}: pelvis/spine bones not found, no flinch");
            return null;
        }
        rig.Pelvis = Add(rig, pelvis, -1, 12f);
        rig.Spine1 = Add(rig, spine1, rig.Pelvis, 13f);
        rig.Spine2 = Add(rig, spine2, Last(rig.Spine1, rig.Pelvis), 13f);
        rig.Spine3 = Add(rig, spine3, Last(rig.Spine2, rig.Spine1, rig.Pelvis), 13f);
        rig.Neck = Add(rig, neck, rig.Spine3, 11f);
        rig.Head = Add(rig, head, Last(rig.Neck, rig.Spine3), 11f);
        rig.ThighL = Add(rig, thighL, rig.Pelvis, 16f);
        rig.ThighR = Add(rig, thighR, rig.Pelvis, 16f);
        rig.CalfL = Add(rig, thighL != null ? Find(thighL, "calf") : null, rig.ThighL, 16f);
        rig.CalfR = Add(rig, thighR != null ? Find(thighR, "calf") : null, rig.ThighR, 16f);
        // arms last: a child bone comes after its parents in the list, and the arms hang off the upper spine
        Transform upL = null, upR = null, foL = null, foR = null;
        foreach (var t in pb.Upperarms ?? new Transform[0])
            if (t != null) { string n = t.name.ToLowerInvariant(); if (n.Contains("lupperarm")) upL = t; else if (n.Contains("rupperarm")) upR = t; }
        foreach (var t in pb.Forearms ?? new Transform[0])
            if (t != null) { string n = t.name.ToLowerInvariant(); if (n.Contains("lforearm")) foL = t; else if (n.Contains("rforearm")) foR = t; }
        rig.UpperL = Add(rig, upL, rig.Spine3, 14f, 0.3f);
        rig.ForeL = Add(rig, foL, rig.UpperL, 14f, 0.3f);
        rig.UpperR = Add(rig, upR, rig.Spine3, 14f, 0.3f);
        rig.ForeR = Add(rig, foR, rig.UpperR, 14f, 0.3f);
        if (_hits == 0)
            Plugin.Log.LogInfo($"[EuphoriaStylePhysics] flinch rig: {string.Join(", ", rig.Bones.ConvertAll(b => b.T.name))}");
        return rig;
    }

    private static int Add(Rig rig, Transform t, int parent, float wn, float max = 0.6f)
    {
        if (t == null) return -1;
        rig.Bones.Add(new Bone { T = t, Parent = parent, Wn = wn, Max = max });
        return rig.Bones.Count - 1;
    }

    private static int Last(params int[] candidates)
    {
        foreach (int c in candidates)
            if (c >= 0) return c;
        return -1;
    }

    private static Transform Find(Transform root, string token)
    {
        foreach (Transform c in root)
        {
            if (c.name.ToLowerInvariant().Contains(token)) return c;
            var d = Find(c, token);
            if (d != null) return d;
        }
        return null;
    }

    // ---------------------------------------------------------------- per frame

    private static void Apply()
    {
        try
        {
            float dt = Mathf.Min(Time.deltaTime, 0.1f);
            if (dt <= 0f) return;
            Knockdown.ApplyRise(dt);
            if (Rigs.Count == 0) return;
            var cam = Camera.main;
            float maxD2 = Plugin.FlinchMaxDistance.Value * Plugin.FlinchMaxDistance.Value;
            Drop.Clear();
            foreach (var kv in Rigs)
            {
                var rig = kv.Value;
                var p = rig.Player;
                if (p == null || p.HealthController == null || !p.HealthController.IsAlive || !Plugin.FlinchEnabled.Value || !Plugin.Enabled.Value ||
                    Knockdown.IsActive(p))
                {
                    Drop.Add(kv.Key);
                    continue;
                }
                if (!Simulate(rig, dt))
                {
                    Drop.Add(kv.Key);
                    continue;
                }
                if (cam != null && (cam.transform.position - p.Position).sqrMagnitude > maxD2) continue;
                foreach (var b in rig.Bones)   // parents first: a child adds its own spin on top of its parent's
                {
                    if (b.T == null) continue;
                    float ang = b.Theta.magnitude;
                    if (ang < 1e-4f) continue;
                    Saved.Add(new KeyValuePair<Transform, Quaternion>(b.T, b.T.localRotation));
                    b.T.rotation = Quaternion.AngleAxis(ang * Mathf.Rad2Deg, b.Theta / ang) * b.T.rotation;
                }
            }
            foreach (var k in Drop) Rigs.Remove(k);
        }
        catch (Exception e)
        {
            Fail(e);
        }
    }

    // Steps every spring of a rig; false when it has been at rest for a while (the rig is dropped).
    private static bool Simulate(Rig rig, float dt)
    {
        int n = Mathf.Max(1, Mathf.CeilToInt(dt / SubStep));
        float h = dt / n;
        bool moving = false;
        foreach (var b in rig.Bones)
        {
            for (int s = 0; s < n; s++)
            {
                b.Omega += (-b.Wn * b.Wn * (b.Theta - b.Hold) - 2f * Zeta * b.Wn * b.Omega) * h;
                b.Theta += b.Omega * h;
                float max = Mathf.Max(b.Max, b.Hold.magnitude + 0.15f);   // a held pose (a flailing arm) may go past the flinch limit
                if (b.Theta.sqrMagnitude > max * max)
                {
                    b.Theta = b.Theta.normalized * max;
                    b.Omega -= Vector3.Project(b.Omega, b.Theta);
                }
            }
            if (b.Theta.sqrMagnitude > 4e-6f || b.Omega.sqrMagnitude > 4e-4f || b.Hold.sqrMagnitude > 1e-6f) moving = true;
            b.Hold = Vector3.zero;   // a held pose is set again every frame it lasts
        }
        if (moving) rig.Quiet = 0f;
        else rig.Quiet += dt;
        return rig.Quiet < 0.5f;
    }

    private static void Undo()
    {
        for (int i = Saved.Count - 1; i >= 0; i--)
        {
            var t = Saved[i].Key;
            if (t != null) t.localRotation = Saved[i].Value;
        }
        Saved.Clear();
        for (int i = SavedPos.Count - 1; i >= 0; i--)
        {
            var t = SavedPos[i].Key;
            if (t != null) t.localPosition = SavedPos[i].Value;
        }
        SavedPos.Clear();
    }

    internal static void Fail(Exception e)
    {
        _errors++;
        Plugin.Log.LogError($"[EuphoriaStylePhysics] flinch failed ({_errors}{(_errors > 20 ? ", flinch off for this session" : "")}): {e}");
        Rigs.Clear();
    }
}
