using System;
using System.Collections.Generic;
using EFT;
using EFT.Ballistics;
using UnityEngine;

namespace EuphoriaStylePhysics;

// 0.8.0 (user: "when they get shot, stumble back for a second, like Euphoria"): a living bot hit by a round is pushed back
// the way the round travelled, fast at first and easing off over about a second. The push is added to the movement EFT
// itself applies at the end of the frame (MovementContext: ApplyMotion schedules, AnimatorStatesLateUpdate applies it
// through the character controller), so walls, slopes and steps stop it like any other step. Only where the bot's AI runs
// (it has a BotOwner): single player, a PC-hosted Fika raid, or the headless host; clients get the position from the host.
internal static class Stagger
{
    private sealed class Push
    {
        public Vector3 V0;     // m/s at the start
        public float T, Dur;
        public string Name;
        public Player Player;
        public float NextStep;
        public bool LeftNext;
        // 0.16.1: how far the root really went (logged at the end), the bot's own moving paused, its trigger held a moment
        public Vector3 From;
        public float Wanted, Peak, HoldFireUntil;
        public bool Paused;
    }

    private static readonly Dictionary<MovementContext, Push> Active = new Dictionary<MovementContext, Push>();
    private static int _count, _errors, _ended;
    private static readonly System.Random Rnd = new System.Random();

    internal static void OnHit(Player player, DamageInfo shot, EBodyPart part)
    {
        if (!Plugin.StaggerEnabled.Value || _errors > 20) return;
        if (player == null || player.IsYourPlayer || !player.IsAI || player.AIData?.BotOwner == null) return;
        if (player.HealthController == null || !player.HealthController.IsAlive || Knockdown.IsActive(player)) return;
        var mc = player.MovementContext;
        if (mc == null || mc.IsInPronePose) return;
        Vector3 dir = shot.Direction;
        dir.y = 0f;
        if (dir.sqrMagnitude < 1e-6f) return;
        dir.Normalize();

        bool melee = (shot.DamageType & (EDamageType.Melee | EDamageType.Blunt)) != 0;
        // a round stopped by armour still shoves; damage that got through shoves harder
        float speed = Mathf.Clamp(0.7f + shot.Damage / 50f, 0.7f, 2.2f) * (melee ? 0.7f : 1f) * Plugin.StaggerScale.Value;
        if (part is EBodyPart.LeftLeg or EBodyPart.RightLeg) speed *= 0.6f;   // a leg hit buckles more than it shoves
        if (speed <= 0.05f) return;
        // 0.9.1 (user: a stumble chance): each hit rolls; a bot already stumbling keeps going back with the burst
        if (!Active.ContainsKey(mc) && Rnd.NextDouble() >= Plugin.StaggerChance.Value) return;

        if (Active.TryGetValue(mc, out var p))
        {
            p.Wanted += speed * p.Dur / 3f;
            // a burst keeps it going back: add to what is left of the last push, capped
            float left = Ease(p);
            p.V0 = Vector3.ClampMagnitude(p.V0 * left + dir * speed, 3f);
            p.T = 0f;
        }
        else
        {
            if (Active.Count > 64) Active.Clear();
            Active[mc] = p = new Push { V0 = dir * speed, Name = player.Profile?.Nickname, Player = player, From = player.Position };
        }
        p.Dur = Mathf.Lerp(0.6f, 1.1f, Mathf.InverseLerp(0.7f, 2.2f, speed));
        if (p.Wanted <= 0f) p.Wanted = speed * p.Dur / 3f;   // distance of a (1 - t/dur)^2 push
        p.Peak = Mathf.Max(p.Peak, p.V0.magnitude);
        // 0.16.1 (user: "like no staggering"): the AI kept walking and shooting through it, so the push was eaten by its own
        // steps. A shot bot reels: its own moving stops for the push. It may keep shooting while it reels (EFT already
        // throws a hit bot's aim off, BotAimingData.GetHit); "Stagger: hold fire" can hold its trigger a moment instead.
        if (Plugin.StaggerHoldFire.Value > 0f)
            p.HoldFireUntil = Mathf.Max(p.HoldFireUntil, Time.time + Plugin.StaggerHoldFire.Value * Mathf.Lerp(0.5f, 1f, Mathf.InverseLerp(0.7f, 2.2f, speed)));
        if (Plugin.StaggerInterrupts.Value)
        {
            try
            {
                var mover = player.AIData.BotOwner.Mover;
                if (mover != null)
                {
                    mover.MovementPause(p.Dur, false);
                    mover.SprintPause(p.Dur + 0.5f);
                    p.Paused = true;
                }
            }
            catch (Exception e) { Plugin.Log.LogWarning($"[EuphoriaStylePhysics] stagger: bot movement not paused: {e.Message}"); }
        }

        _count++;
        if (Plugin.VerboseLog.Value || _count <= 5)
            Plugin.Log.LogInfo($"[EuphoriaStylePhysics] stagger #{_count}: {p.Name} {part}, {p.V0.magnitude:F2} m/s back for {p.Dur:F2} s" +
                               $"{(p.Paused ? ", its own moving paused" : "")}");
    }

    internal static bool HoldsFire(Player p) => p != null && p.MovementContext != null && Active.TryGetValue(p.MovementContext, out var s) && Time.time < s.HoldFireUntil;

    private static void End(MovementContext mc, Push p)
    {
        Active.Remove(mc);
        if (p.Paused)
            try { p.Player?.AIData?.BotOwner?.Mover?.MovementResume(); } catch { }
        _ended++;
        if (p.Player != null && (Plugin.VerboseLog.Value || _ended <= 8))
            Plugin.Log.LogInfo($"[EuphoriaStylePhysics] stagger of {p.Name} over: root moved {Vector3.Distance(Flat(p.From), Flat(p.Player.Position)):F2} m " +
                               $"(push alone {p.Wanted:F2} m, top speed {p.Peak:F2} m/s)");
    }

    private static Vector3 Flat(Vector3 v) => new Vector3(v.x, 0f, v.z);

    // Prefix on MovementContext.AnimatorStatesLateUpdate: put this frame's share of the push into the motion EFT applies.
    internal static void BeforeApply(MovementContext mc)
    {
        if (Active.Count == 0 || mc == null || !Active.TryGetValue(mc, out var p)) return;
        try
        {
            float dt = Time.deltaTime;
            p.T += dt;
            if (p.T >= p.Dur || !Plugin.Enabled.Value || !Plugin.StaggerEnabled.Value || mc.IsInPronePose || Knockdown.IsActive(p.Player))
            {
                End(mc, p);
                return;
            }
            Vector3 step = p.V0 * (Ease(p) * dt);
            // the body reels back with the push: strongest at the start, gone as the push eases off
            Flinch.Lean(p.Player, p.V0, Mathf.Clamp01(p.V0.magnitude / 2.2f) * Mathf.Sqrt(Ease(p)), p.T);
            if (p.T >= p.NextStep && Ease(p) > 0.2f)
            {
                p.NextStep = p.T + 0.28f;
                Flinch.StepKick(p.Player, p.V0, p.LeftNext);
                p.LeftNext = !p.LeftNext;
            }
            if (!Plugin.StaggerMoves.Value) return;   // 0.17.0: visual reel only
            if (mc._scheduleDirectApplyMotion)
            {
                mc._scheduledMotion += step;
            }
            else
            {
                mc._scheduledMotion = step;
                mc._scheduledDeltaTime = dt;
                mc._scheduleDirectApplyMotion = true;
            }
            _pushMc = mc;
            _pushStep = step;
        }
        catch (Exception e)
        {
            _errors++;
            End(mc, p);
            Plugin.Log.LogError($"[EuphoriaStylePhysics] stagger failed ({_errors}{(_errors > 20 ? ", stagger off for this session" : "")}): {e}");
        }
    }

    // 0.16.3 (log: "root moved 0.00 m (push alone 0.73)"): BotAISteeringImpostorCharacterController.Move keeps only the part
    // of a motion along the AI's steer direction and zeroes it when it points backwards, so a push away from where the bot
    // walks (or a paused bot) never moved. The scheduled motion is applied synchronously inside AnimatorStatesLateUpdate
    // (DirectApplyMotion -> CharacterController.Move), so this frame's push step is known there: the bot's own motion still
    // goes through EFT's projection, the push is added after it. Walls still stop it (the controller's move check).
    private static MovementContext _pushMc;
    private static Vector3 _pushStep;
    private static int _throughCount;

    internal static void AfterApply()
    {
        _pushMc = null;
    }

    internal static void SteerMovePrefix(BotAISteeringImpostorCharacterController __instance, ref Vector3 motion, out Vector3 __state)
    {
        __state = __instance._steerDirection;
        var mc = _pushMc;
        if (mc == null) return;
        try
        {
            if (!ReferenceEquals(mc.CharacterController, __instance)) return;
            _pushMc = null;
            Vector3 own = motion - _pushStep;
            float along = Vector3.Dot(own, __state);
            own = along > 0f ? __state * along : Vector3.zero;   // what EFT would have kept of the bot's own motion
            Vector3 total = own + _pushStep;
            if (total.sqrMagnitude < 1e-10f) return;
            motion = total;
            __instance._steerDirection = total.normalized;     // the projection now keeps the whole motion
            _throughCount++;
            if (_throughCount == 1 || (Plugin.VerboseLog.Value && _throughCount % 60 == 0))
                Plugin.Log.LogInfo($"[EuphoriaStylePhysics] stagger push past the bot's steering ({_throughCount} frames so far): " +
                                   $"push {_pushStep.magnitude * 1000f:F1} mm, own {own.magnitude * 1000f:F1} mm this frame");
        }
        catch (Exception e)
        {
            _errors++;
            Plugin.Log.LogError($"[EuphoriaStylePhysics] stagger push failed ({_errors}): {e.Message}");
        }
    }

    internal static void SteerMovePostfix(BotAISteeringImpostorCharacterController __instance, Vector3 __state)
    {
        __instance._steerDirection = __state;
    }

    // fast at first, easing to nothing: (1 - t/dur)^2
    private static float Ease(Push p)
    {
        float k = 1f - Mathf.Clamp01(p.T / p.Dur);
        return k * k;
    }
}
