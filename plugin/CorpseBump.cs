using System.Collections.Generic;
using Comfort.Common;
using EFT;
using UnityEngine;

namespace EuphoriaStylePhysics;

// 0.21.0 (user: "why dont bodies move when we run into them?"): EFT's character controller never pushes physics bodies, and
// corpses sit on their own layer, so you walked through them. Body parts within your legs' reach (ankle to hip, 0.3 m around
// you) are now pushed the way you walk, a little out of your path, at up to your speed. Only bodies that are already physical
// move (5. Corpses: "Corpses stay physical near you"); a frozen corpse stays put. Local only, like the rest of 5. Corpses.
internal static class CorpseBump
{
    private static readonly Collider[] Hits = new Collider[32];
    private static readonly HashSet<Rigidbody> Done = new HashSet<Rigidbody>();
    private static Vector3 _last;
    private static bool _has;
    private static int _bumps;

    internal static void Tick()
    {
        float dt = Time.deltaTime;
        if (!Plugin.IsOn || !Plugin.WalkPush.Value || dt <= 0f || !Singleton<GameWorld>.Instantiated) { _has = false; return; }
        var me = Singleton<GameWorld>.Instance.MainPlayer;
        if (me == null || me.HealthController == null || !me.HealthController.IsAlive) { _has = false; return; }

        Vector3 pos = me.Position;
        Vector3 v = _has ? (pos - _last) / dt : Vector3.zero;   // how you really moved (walk, sprint, crouch, slide)
        _last = pos;
        _has = true;
        v.y = 0f;
        float speed = v.magnitude;
        if (speed < 0.4f || speed > 12f) return;   // standing still, or a teleport / vehicle jump
        Vector3 dir = v / speed;

        int n = Physics.OverlapCapsuleNonAlloc(pos + Vector3.up * 0.1f, pos + Vector3.up * 0.8f, 0.3f, Hits,
            1 << LayersMaskController.DeadbodyLayer, QueryTriggerInteraction.Ignore);
        if (n == 0) return;
        Done.Clear();
        float want = speed * Plugin.WalkPushScale.Value;
        for (int i = 0; i < n; i++)
        {
            var rb = Hits[i] != null ? Hits[i].attachedRigidbody : null;
            if (rb == null || rb.isKinematic || !Done.Add(rb) || DeathMotion.IsDriven(rb) || Plugin.DroppedWeapons.Contains(rb)
                || CorpseLeases.IsLeased(CorpseWake.CorpseOfBody(rb))) continue;   // frozen, still dying on our muscles, a dropped gun, or a body another mod holds
            Vector3 side = Vector3.ProjectOnPlane(rb.worldCenterOfMass - pos, Vector3.up);
            side = Vector3.ProjectOnPlane(side, dir);
            Vector3 push = side.sqrMagnitude > 1e-4f ? (dir + 0.4f * side.normalized).normalized : dir;
            float add = Mathf.Clamp(want - Vector3.Dot(rb.velocity, push), 0f, 3f) * 0.5f;
            if (add <= 0.01f) continue;
            rb.AddForce(push * add + Vector3.up * (0.15f * add), ForceMode.VelocityChange);   // a little lift so it slides, not digs in
            _bumps++;
            if (_bumps == 1 || Plugin.VerboseLog.Value && _bumps % 100 == 0)
                Plugin.Log.LogInfo($"[EuphoriaStylePhysics] walked into a body ({rb.name}) at {speed:F1} m/s: pushed ({_bumps} so far)");
        }
    }
}
