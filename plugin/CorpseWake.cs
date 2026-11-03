using System;
using System.Collections.Generic;
using Comfort.Common;
using EFT;
using EFT.Interactive;
using UnityEngine;

namespace EuphoriaStylePhysics;

// 0.17.3 (user: "make the ragdolls continue to move, only when you're nearby they turn back on"): EFT freezes a corpse once it
// sleeps (or after 15 s): CorpseRagdoll.WorkingCycle makes every body kinematic, and once the corpse is out of sight it removes
// the joints and rigidbodies. Near the local player a corpse stays (or becomes again) a live ragdoll, so rounds, blasts and
// other bodies still move it; when the player walks away EFT's own freeze takes over again. Local only (nothing is sent to Fika).
//  - not frozen yet: CorpseRagdoll._checkCorpseIsStill is wrapped to say "not still" while the player is near
//  - frozen, bodies still there (waiting until it is out of sight): the bodies are made dynamic again by us, frozen by us later
//  - bodies removed: CorpseRagdoll.Start() builds them again (spawners are idempotent) with no start velocity
internal static class CorpseWake
{
    private sealed class Entry
    {
        public Corpse Corpse;
        public string Name;
        public bool WokenByUs;     // our dynamic bodies on a frozen corpse (we freeze them again)
        public bool Near;
        public Rigidbody Pelvis;   // 0.17.5: the body can lie metres from the corpse root
    }

    private static readonly List<Entry> Corpses = new List<Entry>();
    private static readonly Dictionary<Corpse, Entry> ByCorpse = new Dictionary<Corpse, Entry>();
    private static float _next;
    private static int _awake, _wakes, _rebuilds, _errors;

    internal static void Track(Corpse corpse, string name)
    {
        if (corpse == null || corpse.Ragdoll == null || ByCorpse.ContainsKey(corpse)) return;
        var e = new Entry { Corpse = corpse, Name = name };
        Corpses.Add(e);
        ByCorpse[corpse] = e;
        SmoothCollisions(corpse.Ragdoll);
        var orig = corpse.Ragdoll._checkCorpseIsStill;
        if (orig != null)
            corpse.Ragdoll._checkCorpseIsStill = (sleeping, t) => orig(sleeping, t) && !(Plugin.Enabled.Value && Plugin.CorpseWakeOn.Value && e.Near);
    }

    private static Vector3 BodyPos(Entry e)
    {
        if (e.Pelvis == null && e.Corpse.Ragdoll?._rigidbodySpawners != null)
            foreach (var sp in e.Corpse.Ragdoll._rigidbodySpawners)
                if (sp != null && sp.Rigidbody != null && sp.Rigidbody.name.IndexOf("pelvis", System.StringComparison.OrdinalIgnoreCase) >= 0) { e.Pelvis = sp.Rigidbody; break; }
        return e.Pelvis != null ? e.Pelvis.position : e.Corpse.transform.position;
    }

    // 0.17.5 (corpses moved, then froze): EFT's cycle puts each part to sleep once slow and marks it MustBeSleeping, then sleeps
    // it again every frame, so a corpse kept physical went stiff right after it settled. Not for corpses we keep live.
    internal static bool KeepsLive(Rigidbody rb)
    {
        if (rb == null || Corpses.Count == 0) return false;
        // EFT asks every corpse body to sleep every frame; a GetComponentInParent per ask added up. Looked up once.
        if (!CorpseOf.TryGetValue(rb, out var c) || c == null)
        {
            if (CorpseOf.Count > 4096) CorpseOf.Clear();
            c = rb.GetComponentInParent<Corpse>();
            CorpseOf[rb] = c;
        }
        return c != null && ByCorpse.TryGetValue(c, out var e) && e.Near && Plugin.Enabled.Value && Plugin.CorpseWakeOn.Value;
    }
    private static readonly Dictionary<Rigidbody, Corpse> CorpseOf = new Dictionary<Rigidbody, Corpse>();

    internal static void Tick()
    {
        if (Corpses.Count == 0 || Time.time < _next || _errors > 20) return;
        _next = Time.time + 0.25f;
        try
        {
            Player me = Singleton<GameWorld>.Instantiated ? Singleton<GameWorld>.Instance.MainPlayer : null;
            bool on = Plugin.Enabled.Value && Plugin.CorpseWakeOn.Value && me != null;
            float r = Plugin.CorpseWakeDistance.Value;
            Vector3 at = me != null ? me.Position : Vector3.zero;
            _awake = 0;
            for (int i = Corpses.Count - 1; i >= 0; i--)
            {
                var e = Corpses[i];
                if (e.Corpse == null || e.Corpse.Ragdoll == null)
                {
                    ByCorpse.Remove(e.Corpse);
                    Corpses.RemoveAt(i);
                    continue;
                }
                var rag = e.Corpse.Ragdoll;
                e.Near = on && (BodyPos(e) - at).sqrMagnitude < r * r;   // 0.17.5: where the body lies, not where it died
                if (e.WokenByUs)
                {
                    // EFT's first cycle removes the bodies once the corpse is out of sight, awake or not: build them again next time
                    if (AllRemoved(rag)) { e.WokenByUs = false; continue; }
                    if (!e.Near) Freeze(e);
                    else _awake++;
                    continue;
                }
                if (!rag._isPhysicsDone) { if (e.Near) _awake++; continue; }   // EFT's cycle still runs (kept going by the wrapper)
                if (!e.Near || _awake >= Plugin.CorpseWakeMax.Value) continue;
                if (AllRemoved(rag)) Rebuild(e);
                else Wake(e);
                _awake++;
            }
        }
        catch (Exception ex)
        {
            _errors++;
            Plugin.Log.LogError($"[EuphoriaStylePhysics] corpse wake failed ({_errors}{(_errors > 20 ? ", off for this session" : "")}): {ex}");
        }
    }

    // 0.17.4 (user clip: a body rolling down metal stairs jitters; logs: parts at 9-12 m/s): EFT runs corpses with Discrete
    // collisions, so a falling part sinks into a thin stair tread between steps and is popped out at up to 4 m/s
    // (CorpseMaxDepenetrationVelocity), step after step. Speculative contacts, more solver iterations and a softer push-out
    // keep it on the surface. EFT sets Discrete again when it freezes the corpse.
    internal static void SmoothCollisions(CorpseRagdoll rag)
    {
        if (rag == null || !Plugin.SmoothCollisions.Value || !Plugin.Enabled.Value) return;   // 0.17.5: master switch too
        foreach (var s in rag._rigidbodySpawners)
            if (s != null && s.Rigidbody != null && !s.Rigidbody.isKinematic) Smooth(s.Rigidbody);
    }

    private static void Smooth(Rigidbody rb)
    {
        if (!Plugin.SmoothCollisions.Value || !Plugin.Enabled.Value) return;
        rb.collisionDetectionMode = CollisionDetectionMode.ContinuousSpeculative;
        rb.solverIterations = Mathf.Max(rb.solverIterations, 12);
        rb.solverVelocityIterations = Mathf.Max(rb.solverVelocityIterations, 4);
        rb.maxDepenetrationVelocity = Mathf.Min(rb.maxDepenetrationVelocity, 1.5f);
    }

    // Postfix on GameWorld.FindInteractable (user: "cant search him until i move to the other side"): EFT drops a hit when the
    // line from the eye to the hit point crosses HighPolyCollider/TransparentCollider. A body resting on (or a little in) a
    // stair tread fails that from above. A body part hit within 0.25 m behind such a surface still counts; walls stay walls.
    internal static void FindInteractablePostfix(Ray ray, ref RaycastHit hit, ref GameObject __result)
    {
        if (__result != null || !Plugin.Enabled.Value || !Plugin.SearchThroughFloor.Value) return;
        try
        {
            float dist = Mathf.Max(EFTHardSettings.Instance.LOOT_RAYCAST_DISTANCE, EFTHardSettings.Instance.PLAYER_RAYCAST_DISTANCE + EFTHardSettings.Instance.BEHIND_CAST);
            if (!Physics.Raycast(ray, out var h, dist, 1 << LayersMaskController.DeadbodyLayer, QueryTriggerInteraction.Ignore)) return;
            if (h.collider.GetComponentInParent<Corpse>() == null) return;
            Vector3 near = h.point - ray.direction * 0.25f;
            if (Vector3.Dot(near - ray.origin, ray.direction) <= 0f) return;
            if (Physics.Linecast(ray.origin, near, GameWorld.LootMaskObstruction, QueryTriggerInteraction.Ignore)) return;
            __result = h.collider.gameObject;
            hit = h;
            _searchFixes++;
            if (_searchFixes == 1 || Plugin.VerboseLog.Value && _searchFixes % 200 == 0)
                Plugin.Log.LogInfo($"[EuphoriaStylePhysics] search: body found through a floor/stair surface ({h.collider.name}, {h.distance:F2} m)");
        }
        catch (Exception ex)
        {
            _errors++;
            Plugin.Log.LogError($"[EuphoriaStylePhysics] search fix failed ({_errors}): {ex.Message}");
        }
    }

    private static int _searchFixes;

    private static bool AllRemoved(CorpseRagdoll rag)
    {
        foreach (var s in rag._rigidbodySpawners)
            if (s != null && s.Rigidbody != null) return false;
        return true;
    }

    // frozen but its bodies still exist: make them dynamic again, EFT's physics stepping included
    private static void Wake(Entry e)
    {
        var rag = e.Corpse.Ragdoll;
        foreach (var s in rag._rigidbodySpawners)
        {
            var rb = s != null ? s.Rigidbody : null;
            if (rb == null) continue;
            rb.isKinematic = false;
            rb.collisionDetectionMode = rag._collisionDetectionMode;
            Smooth(rb);
            rb.velocity = Vector3.zero;
            rb.angularVelocity = Vector3.zero;
            PhysicsExtensions.UpdateController.SupportRigidbody(rb, 0f);
        }
        Hush(rag);
        e.WokenByUs = true;
        _wakes++;
        if (Plugin.VerboseLog.Value || _wakes <= 5)
            Plugin.Log.LogInfo($"[EuphoriaStylePhysics] corpse of {e.Name} awake again (you are near; {_wakes} wakes so far)");
    }

    // Prefix on JointSpawner.Remove: EFT's first cycle of a corpse we woke takes it apart once it is out of sight (joints one per
    // frame, then the bodies). Dynamic bodies without joints would fall apart, so the corpse is frozen first, as EFT expects.
    internal static void BeforeJointRemove(JointSpawner __instance)
    {
        if (ByCorpse.Count == 0 || __instance == null) return;
        try
        {
            var c = __instance.GetComponentInParent<Corpse>();
            if (c != null && ByCorpse.TryGetValue(c, out var e) && e.WokenByUs) Freeze(e);
        }
        catch (Exception ex) { Plugin.Log.LogWarning($"[EuphoriaStylePhysics] corpse freeze before joint removal failed: {ex.Message}"); }
    }

    private static void Freeze(Entry e)
    {
        foreach (var s in e.Corpse.Ragdoll._rigidbodySpawners)
        {
            var rb = s != null ? s.Rigidbody : null;
            if (rb != null && !rb.isKinematic) e.Corpse.Ragdoll.StopRigidbody(rb);
        }
        e.WokenByUs = false;
        if (Plugin.VerboseLog.Value) Plugin.Log.LogInfo($"[EuphoriaStylePhysics] corpse of {e.Name} frozen again (you left)");
    }

    // 0.17.5, GitHub #1 ("Bodies Move After Dead"): the frozen pose is not quite a physics rest pose (joints a little off, parts
    // a little in the ground), so a body made physical again settled with a twitch when you walked up to it. It comes back asleep
    // instead: it lies still until a round, a blast or another body touches it, which wakes it as usual.
    private static void Hush(CorpseRagdoll rag)
    {
        foreach (var s in rag._rigidbodySpawners)
        {
            var rb = s != null ? s.Rigidbody : null;
            if (rb == null || rb.isKinematic) continue;
            rb.velocity = Vector3.zero;
            rb.angularVelocity = Vector3.zero;
            rb.Sleep();
        }
    }

    // EFT already removed its bodies: build them again, no start velocity (EFT would give it the death velocity again)
    private static void Rebuild(Entry e)
    {
        var rag = e.Corpse.Ragdoll;
        rag._velocity = Vector3.zero;
        rag.Start();
        SmoothCollisions(rag);
        Hush(rag);
        _rebuilds++;
        if (Plugin.VerboseLog.Value || _rebuilds <= 5)
            Plugin.Log.LogInfo($"[EuphoriaStylePhysics] corpse of {e.Name} rebuilt as a ragdoll (you are near; {_rebuilds} so far)");
    }
}
