using System;
using System.Reflection;
using BepInEx.Bootstrap;
using HarmonyLib;
using EFT.Interactive;
using UnityEngine;

namespace EuphoriaStylePhysics;

// 2026-11-04 (players: deaths "spazzing out, very robotic, like a strobe light" with Smoothie, smooth again once it was removed;
// user: "could we fix it on our end?"). Two of Smoothie's patches step aside while our bodies move; the rest of Smoothie is
// untouched and keeps saving frame time.
//  - "Run physics at most 60 times a second" (HotPaths.PhysicsRatePatch): above 60 FPS it skips physics steps and pays the time
//    back in one step, so a body driven by our muscles each step moved in visible jumps. While we drive a body (dying, stumbling,
//    knocked down) EFT's own smoothed stepping runs instead, every frame.
//  - "Corpses stop moving after (s)" on Fika clients (HotPaths.ObservedCorpseStillPatch): it set the corpse's still flag itself,
//    freezing bodies we were still moving. Skipped for a corpse our motion drives (single player is handled in CorpseWake).
internal static class SmoothieCompat
{
    private static FieldInfo _owed;
    private static int _lastFrame = -1000, _said;

    internal static void Install()
    {
        if (!Chainloader.PluginInfos.TryGetValue("com.tominocz.smoothie", out var info) || info.Instance == null) return;
        var asm = info.Instance.GetType().Assembly;
        var h = new Harmony("amos.euphoriastylephysics.smoothie");
        // each on its own: one failing never takes the other down (2026-11-04 log: "IL Compile Error" set up neither)
        string cap, still;
        try
        {
            var rate = AccessTools.Method(asm.GetType("Smoothie.HotPaths+PhysicsRatePatch"), "Prefix");
            var hot = asm.GetType("Smoothie.HotPaths");
            _owed = hot != null ? AccessTools.Field(hot, "_physicsOwed") : null;
            if (rate == null) cap = "NOT FOUND, stays on";
            else { h.Patch(rate, prefix: new HarmonyMethod(typeof(SmoothieCompat), nameof(BeforeRateCap))); cap = "pauses"; }
        }
        catch (Exception e) { cap = "could not be paused (" + e.Message + ")"; }
        try
        {
            // not Smoothie's own postfix (its parameters are named __instance and __result, which Harmony reads as its own): EFT's
            // ObservedCorpse.CheckCorpseIsStill, after every other patch, undoes the forced "still" for a body we move
            var check = AccessTools.Method(typeof(ObservedCorpse), nameof(ObservedCorpse.CheckCorpseIsStill));
            var post = new HarmonyMethod(typeof(SmoothieCompat), nameof(AfterObservedStill)) { priority = Priority.Last };
            h.Patch(check, postfix: post);
            still = "skips";
        }
        catch (Exception e) { still = "still applies to (" + e.Message + ")"; }
        Plugin.Log.LogInfo($"[EuphoriaStylePhysics] Smoothie {info.Metadata.Version} found: its physics cap {cap} while our bodies move, " +
                           $"its Fika corpse freeze {still} bodies we move");
    }

    private static readonly AccessTools.FieldRef<ObservedCorpse, bool> IsStill = AccessTools.FieldRefAccess<ObservedCorpse, bool>("_isStill");

    // after Smoothie (and EFT): a Fika client's copy of a body our motion still drives is not still
    private static void AfterObservedStill(ObservedCorpse __instance, ref bool __result)
    {
        if (!__result || !Plugin.IsOn || __instance == null || __instance.GetComponent<DeathMotion>() == null) return;
        __result = false;
        try { IsStill(__instance) = false; } catch { /* the flag stays; the result is what EFT's cycle reads first */ }
    }

    // ahead of Smoothie's prefix on Simulation.SmoothSimulate: true runs EFT's own stepping this frame (Smoothie's prefix skipped)
    private static bool BeforeRateCap(ref bool __result)
    {
        if (!Plugin.IsOn || DeathMotion.ActiveCount + Knockdown.Count == 0) return true;   // nothing of ours moving: Smoothie decides
        try { _owed?.SetValue(null, 0f); } catch { /* its owed time just carries over */ }
        __result = true;
        if (Time.frameCount - _lastFrame > 120 && _said < 5)   // once per stretch: proof in the log that the cap paused
        {
            _said++;
            Plugin.Log.LogInfo($"[EuphoriaStylePhysics] Smoothie physics cap paused: {DeathMotion.ActiveCount + Knockdown.Count} body(s) moving, {1f / Mathf.Max(Time.unscaledDeltaTime, 1e-4f):0} FPS");
        }
        _lastFrame = Time.frameCount;
        return false;
    }
}
