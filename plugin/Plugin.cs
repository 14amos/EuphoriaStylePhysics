using System;
using System.Collections.Generic;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using EFT;
using EFT.AssetsManager;
using EFT.Ballistics;
using EFT.Communications;
using Comfort.Common;
using EFT.Interactive;
using EFT.InventoryLogic;
using HarmonyLib;
using UnityEngine;

namespace EuphoriaStylePhysics;

// Euphoria-style bodies for SPT: a dying body keeps its muscles for a moment (stumbles the way the round pushed it, a hand
// goes to the wound, the knees give, then it drops and may twitch), and a living body hit by a round flinches with springs
// on top of EFT's own hit reaction. Visual only: no damage, AI or movement changes. Ideas and some tuning values from
// Project Real Limbs (Project Zomboid) by deewindler, https://www.youtube.com/@DwindleDwindle (CC BY-NC 4.0); none of its
// code is used here (its Java solver has no Unity counterpart).
[BepInDependency("com.fika.core", BepInDependency.DependencyFlags.SoftDependency)]
[BepInPlugin("amos.euphoriastylephysics", "Euphoria Style Physics", "0.21.1")]
public sealed class Plugin : BaseUnityPlugin
{
    internal static ManualLogSource Log;

    internal static ConfigEntry<bool> Enabled;
    internal static ConfigEntry<bool> VerboseLog;
    internal static ConfigEntry<KeyboardShortcut> ToggleKey;
    internal enum StylePreset { Custom, Euphoric }
    internal static ConfigEntry<StylePreset> Preset;
    internal static ConfigEntry<bool> PresetApplied;
    internal static ConfigEntry<int> PresetRevision;
    private static bool _patchFailed;
    internal static bool IsOn => Enabled.Value && !_patchFailed;   // the master switch, and patching worked this session
    internal static ConfigEntry<float> DyingVariety, KnockdownVariety;   // 0.20.0: one Variety per section, in place of Consistency
    internal static ConfigEntry<bool> RealTimePhysics;

    internal static ConfigEntry<bool> DeathEnabled;
    internal static ConfigEntry<float> DropDeadChance;
    internal static ConfigEntry<float> HeadshotLegsGoChance;
    internal static ConfigEntry<bool> HeadshotInstantDrop;
    internal static ConfigEntry<float> DyingTimeMin;
    internal static ConfigEntry<float> DyingTimeMax;
    internal static ConfigEntry<float> MuscleScale;
    internal static ConfigEntry<float> StumbleScale;
    internal static ConfigEntry<bool> HandOnWound;
    internal static ConfigEntry<bool> Stepping;
    internal static ConfigEntry<float> Throes;
    internal static ConfigEntry<float> ThroesNoneChance;
    internal static ConfigEntry<bool> ExplosionsVanilla, DropWeapon;
    internal static ConfigEntry<bool> RunTumble;
    internal static ConfigEntry<bool> GroundBrace;
    internal static ConfigEntry<bool> WallBrace;
    internal static ConfigEntry<float> WallReach;
    internal static ConfigEntry<int> MaxDying;
    internal static ConfigEntry<float> MaxDistance;
    internal static ConfigEntry<bool> CorpseHits;
    internal static ConfigEntry<bool> BurstKeepsUp;
    internal static ConfigEntry<float> LieWoundedChance;
    internal static ConfigEntry<bool> MeleeClutch;
    internal static ConfigEntry<float> CorpseHitScale;

    internal static ConfigEntry<bool> FlinchEnabled;
    internal static ConfigEntry<float> FlinchScale;
    internal static ConfigEntry<float> FlinchMaxDistance;
    internal static ConfigEntry<bool> FlinchLegDip;
    internal static ConfigEntry<bool> FlinchArms;
    internal static ConfigEntry<bool> BlastFlinch;

    internal static ConfigEntry<bool> KnockdownEnabled;
    internal static ConfigEntry<bool> BlastThrowWithRealTargets;
    internal static ConfigEntry<float> HoldUp;
    internal static ConfigEntry<bool> CorpseWakeOn, CorpseNeverFreeze, WalkPush;
    internal static ConfigEntry<float> WalkPushScale;
    internal static ConfigEntry<float> CorpseWakeDistance;
    internal static ConfigEntry<int> CorpseWakeMax;
    internal static ConfigEntry<bool> SmoothCollisions, SearchThroughFloor;
    internal static ConfigEntry<float> KnockdownImpact;
    internal static ConfigEntry<float> KnockdownChance;
    internal static ConfigEntry<float> KnockdownCooldown;
    internal static ConfigEntry<float> KnockdownLieMin;
    internal static ConfigEntry<float> KnockdownLieMax;
    internal static ConfigEntry<float> KnockdownRiseTime;
    internal static ConfigEntry<bool> KnockdownProneGetUp;
    internal static ConfigEntry<bool> KnockdownRollOver;
    internal static ConfigEntry<bool> KnockdownHoldStill;
    internal static ConfigEntry<float> KnockdownMisfireChance;
    internal static ConfigEntry<bool> BlastKnockdown;
    internal static ConfigEntry<bool> KnockdownVoice;
    internal static ConfigEntry<bool> KnockdownWrithe;
    internal static ConfigEntry<bool> StayDownEnabled;
    internal enum LegPose { Sit, Prone }
    internal static ConfigEntry<LegPose> BadLegPose;
    internal static ConfigEntry<float> StayDownChance;
    internal static ConfigEntry<float> StayDownLegHealth;
    internal static ConfigEntry<float> StayDownMin;
    internal static ConfigEntry<float> StayDownMax;
    internal static ConfigEntry<bool> BlastThrow;
    internal static ConfigEntry<float> BlastThrowScale;
    internal static ConfigEntry<float> BlastKnockdownChance;
    internal static ConfigEntry<bool> LookAtShooter;
    internal static ConfigEntry<bool> ReachToShooter;
    internal static ConfigEntry<bool> FlinchHeadSnap;
    internal static ConfigEntry<bool> FlinchWhiplash;

    // after every plugin's Awake: Smoothie may load after us (SmoothieCompat)
    private void Start() => SmoothieCompat.Install();

    private void Awake()
    {
        Log = Logger;
        // 0.9.0 (user: every main feature toggleable with ease): the main switches all sit in "1. General", first in F12
        // 0.20.0 (user: settings regrouped, feature switches): one section per feature with its main switch first; the main switches
        // also side by side in 1. General (LinkFeatureSwitches)
        const string g = "1. General", d = "2. Dying", f = "3. Hit flinch", k = "4. Knockdowns", c = "5. Corpses";
        Enabled = Config.Bind(g, "Enabled", true, "Master switch.");
        VerboseLog = Config.Bind(g, "Verbose log", false, "More detail in the BepInEx log for every part of the mod: each flinch, corpse, knockdown and skipped death (deaths are always logged).");
        Preset = Config.Bind(g, "Style preset", StylePreset.Euphoric,
            "Euphoric: sets every value below for Euphoria-style bodies (consistent stagger, hand to the wound, knees give, "
            + "every death on our physics, none left to EFT's own ragdoll, survivors flinch and keep fighting). The experimental gameplay part (knockdowns) stays off; "
            + "turn it on yourself. Applied once when picked; change anything afterwards as you like. "
            + "Custom: nothing is set for you.");
        PresetApplied = Config.Bind(g, "Style preset applied", false, "Set by the mod once the preset's values have been written.");
        PresetRevision = Config.Bind(g, "Style preset revision", 0, "Set by the mod: which revision of the preset's values was written (a newer one is written once).");

        RealTimePhysics = Config.Bind(g, "Real-time ragdolls at low FPS", true,
            "EFT advances physics at most 0.03 s per frame, so below ~33 FPS every ragdoll falls in slow motion. While our bodies "
            + "move, one extra physics step per frame catches up with real time (costs a little CPU at low FPS).");
        ToggleKey = Config.Bind(g, "Toggle key", new KeyboardShortcut(KeyCode.E, KeyCode.LeftControl, KeyCode.LeftShift),
            "Turns the whole mod on or off in game (same as Enabled).");

        DeathEnabled = Config.Bind(d, "Dying with muscles", true, new ConfigDescription(
            "MAIN SWITCH (visual). A body killed on its feet keeps its muscles for a moment: stumbles, hand on the wound, knees give, "
            + "lies hurt. Off: EFT's own ragdoll. Details below.", null, new ConfigurationManagerAttributes { Order = 1000 }));
        FlinchEnabled = Config.Bind(f, "Hit flinch", true, new ConfigDescription(
            "MAIN SWITCH (visual). Bots and other players hit by a round flinch on top of EFT's own hit reaction: a short snap (about "
            + "a quarter second) of the spine, head, a knee or an arm. Never your own body. Details below.", null,
            new ConfigurationManagerAttributes { Order = 1000 }));
        BlastFlinch = Config.Bind(f, "Explosion flinch", true, new ConfigDescription(
            "MAIN SWITCH (visual). Bots and other players near an explosion they survive recoil away from it. Strength: Flinch strength.",
            null, new ConfigurationManagerAttributes { Order = 900 }));
        // 0.17.3 (user: ragdolls keep moving when you are near)
        CorpseWakeOn = Config.Bind(c, "Corpses stay physical near you", true, new ConfigDescription(
            "MAIN SWITCH (visual). EFT freezes a corpse once it lies still; near you it stays a ragdoll (or becomes one again), so "
            + "rounds, blasts and other bodies still move it. Walk away and EFT freezes it again. Only on this PC (not synced). "
            + "Details below.", null, new ConfigurationManagerAttributes { Order = 1000 }));
        KnockdownEnabled = Config.Bind(k, "Knockdowns", false, new ConfigDescription(
            "MAIN SWITCH, EXPERIMENTAL (gameplay: the bot cannot shoot while down). A bot hit hard goes down on its own ragdoll and gets back up. "
            + "Only where the bots' AI runs. Details below.", null, new ConfigurationManagerAttributes { Order = 1000 }));
        // 0.20.0 (user: "variety"): one Variety per section that has random ranges, right under its main switch (0 = alike, 1 = most varied)
        DyingVariety = Config.Bind(d, "Variety", 0.5f,
            new ConfigDescription("How different deaths are from each other. 0 = the same hit gives the same death (dying time in the middle of its range, "
                + "hand to the wound and arms out nearly always), 1 = every random range used fully. Your chance settings (drop dead, lying wounded, "
                + "twitches) are not changed by it.", new AcceptableValueRange<float>(0f, 1f), new ConfigurationManagerAttributes { Order = 990 }));
        KnockdownVariety = Config.Bind(k, "Variety", 0.5f,
            new ConfigDescription("How different knockdowns are: 0 = always the middle, 1 = the fall, the time on the ground and the bad-leg stay vary fully "
                + "(between their min and max). Chances (go down, misfire, stay down) are not changed by it.", new AcceptableValueRange<float>(0f, 1f),
                new ConfigurationManagerAttributes { Order = 990 }));
        DropDeadChance = Config.Bind(d, "Drop dead chance", 0.25f,
            new ConfigDescription("Share of the kills our dying would play that go limp on the spot instead (EFT's own ragdoll).", new AcceptableValueRange<float>(0f, 1f)));
        HeadshotLegsGoChance = Config.Bind(d, "Headshot: legs go at once", 0.25f,
            new ConfigDescription("Share of standing headshot kills whose legs give at once; the rest ripple: head snaps, upper body follows, legs last. "
                + "Crouched or running headshots slump or tumble instead.",
                new AcceptableValueRange<float>(0f, 1f)));
        HeadshotInstantDrop = Config.Bind(d, "Headshots drop instantly", false,
            "On: every bullet headshot kill drops on the spot, limp (EFT's own ragdoll). Off: headshots ripple or the legs go (see above).");
        DyingTimeMin = Config.Bind(d, "Dying time min (s)", 0.6f,
            new ConfigDescription("Shortest time a body shot in the torso or a limb stays on its feet.", new AcceptableValueRange<float>(0.2f, 6f)));
        DyingTimeMax = Config.Bind(d, "Dying time max (s)", 1.6f,
            new ConfigDescription("Longest time a body shot in the torso or a limb stays on its feet.", new AcceptableValueRange<float>(0.2f, 8f)));
        MuscleScale = Config.Bind(d, "Muscle strength", 1f,
            new ConfigDescription("How firmly a dying body holds its pose (lower: floppier).", new AcceptableValueRange<float>(0f, 2f)));
        // 0.17.2 (user: "still a little floaty when they die")
        HoldUp = Config.Bind(d, "Hold-up strength", 1f,
            new ConfigDescription("How much the dying body is held up against gravity while it is still on its feet (lower: heavier, "
                + "drops sooner; higher: floatier). 0 = no hold-up at all.", new AcceptableValueRange<float>(0f, 1.5f)));
        StumbleScale = Config.Bind(d, "Stumble", 1f,
            new ConfigDescription("How far a dying body stumbles the way the round pushed it.", new AcceptableValueRange<float>(0f, 3f)));
        HandOnWound = Config.Bind(d, "Hand on the wound", true, "A hand goes to a torso or leg wound while dying.");
        Stepping = Config.Bind(d, "Catch steps", true, "A stumbling body steps to catch itself.");
        Throes = Config.Bind(d, "Last twitches", 0.75f,
            new ConfigDescription("How hard a body on the ground twitches (0: never).", new AcceptableValueRange<float>(0f, 2f)));
        ThroesNoneChance = Config.Bind(d, "No twitch chance", 0.7f,
            new ConfigDescription("Share of deaths with no twitches at all.", new AcceptableValueRange<float>(0f, 1f)));
        // 0.20.0 (user: "we had a toggle for weapon being dropped during ragdoll"): as HollywoodFX does
        DropWeapon = Config.Bind(d, "Drop weapon on death", true,
            "A long gun's strap comes off and it falls from the body; a pistol, knife or grenade in the hand is hidden "
            + "(both as HollywoodFX does). Still looted from the body. Left to HollywoodFX when its own weapon drop is on.");
        ExplosionsVanilla = Config.Bind(d, "Explosions use EFT ragdoll", false,
            "On: grenades, mines, artillery and the like kill with EFT's own ragdoll. Off: the blast throw below.");
        BlastThrow = Config.Bind(d, "Blast throw", true,
            "A blast throws the body up and away (harder the closer it was): arms up to guard the head, legs tucked, a hard landing. "
            + "Kills go limp after it; bots knocked down by a blast (4. Knockdowns) lie hurt and get up.");
        // 0.17.0 (user: works with RealTargets?): its "Blast injuries" throws grenade kills within 6 m too
        CorpseWakeDistance = Config.Bind(c, "Corpses stay physical within (m)", 12f,
            new ConfigDescription("How close a corpse has to be to you to stay or become a ragdoll again.", new AcceptableValueRange<float>(2f, 50f)));
        CorpseWakeMax = Config.Bind(c, "Most physical corpses at once", 10,
            new ConfigDescription("Cap on corpses kept or made physical near you (performance).", new AcceptableValueRange<int>(1, 40)));
        // 0.20.0 (user: "i dont want frozen ragdolls at all if possible")
        CorpseNeverFreeze = Config.Bind(c, "Never freeze corpses", false,
            "Every corpse stays a ragdoll for the whole raid, however far away (distance and cap above ignored). A settled body "
            + "sleeps in the physics engine and costs little until something touches it; raids with very many bodies use more "
            + "memory and physics time. Needs Corpses stay physical near you.");
        // 0.21.0 (user: "why dont bodies move when we run into them?")
        WalkPush = Config.Bind(c, "Walking pushes corpses", true,
            "Walking or running into a body pushes it the way you go (legs only, at up to your speed). Only bodies that are physical "
            + "move: needs Corpses stay physical near you. Only on your PC.");
        WalkPushScale = Config.Bind(c, "Walk push strength", 0.5f, new ConfigDescription(   // 2026-11-03 (user): default 0.5
            "How hard walking into a body pushes it (1 = at your speed).", new AcceptableValueRange<float>(0f, 2f)));
        // 0.17.4 (user clip: rolling down stairs jitters, then the body cannot be searched from above)
        SmoothCollisions = Config.Bind(c, "Smooth body collisions", true,
            "Bodies use continuous (speculative) collisions, more solver steps and a soft push-out from geometry, so they roll down "
            + "stairs instead of sinking into the steps and popping out. Costs a little CPU while bodies move.");
        SearchThroughFloor = Config.Bind(c, "Search bodies lying on stairs", true,
            "A body resting on (or slightly in) a stair step or floor can still be searched from above. Only 25 cm of tolerance: "
            + "walls still block.");
        BlastThrowWithRealTargets = Config.Bind(d, "Blast throw with RealTargets", false,
            "Only matters with RealTargets installed and its Impacts > Blast injuries on: it already throws grenade kills within 6 m. "
            + "Off: our throw is skipped for those bodies (arms up and the landing stay), so they are not thrown twice. On: both throw.");
        BlastThrowScale = Config.Bind(d, "Blast throw strength", 1f, new ConfigDescription("", new AcceptableValueRange<float>(0f, 3f)));
        RunTumble = Config.Bind(d, "Running deaths tumble", true, "A body killed at a run keeps its run, pitches forward and goes down.");
        GroundBrace = Config.Bind(d, "Arms out when falling", true, "A body falling forward puts its arms out to catch the fall (most of the time).");
        WallBrace = Config.Bind(d, "Hand on walls", true, "A dying body next to a wall puts a hand on it, leans on it and slides down it.");
        // 0.16.0 (user: "there hasnt really been any environment touching"): walls further away count, and the body goes to them
        WallReach = Config.Bind(d, "Wall reach (m)", 1.4f,
            new ConfigDescription("How far a wall (or the top of a crate, table, low cover) may be for a dying body to stagger to it and put a hand on it.",
                new AcceptableValueRange<float>(0.5f, 3f)));
        MaxDying = Config.Bind(d, "Most bodies dying at once", 10,
            new ConfigDescription("More deaths than this playing at the same time: the rest use EFT's own ragdoll. Knockdowns do not count.", new AcceptableValueRange<int>(1, 40)));
        MaxDistance = Config.Bind(d, "Dying max distance (m)", 150f,
            new ConfigDescription("Farther from the camera: EFT's own ragdoll (saves time).", new AcceptableValueRange<float>(20f, 1000f)));
        CorpseHits = Config.Bind(d, "Rounds push dying bodies", true,
            "Our own push: a round into a body that is dying or still falling pushes the part it hits. Left to RealTargets when its "
            + "Impacts push corpses (so a body is not pushed twice). Either way a dying body gives way to the hit for a moment.");
        LieWoundedChance = Config.Bind(d, "Lying wounded chance", 0.4f,
            new ConfigDescription("Share of bodies that lie hurt for 2-5 s after the fall (curl up, a hand on the wound or reaching out) before going still. "
                + "Not after headshots or blasts.",
                new AcceptableValueRange<float>(0f, 1f)));
        MeleeClutch = Config.Bind(d, "Knife deaths clutch", true,
            "A knife or a blow to the chest or stomach that kills: both hands go to the wound and it folds onto its knees.");
        LookAtShooter = Config.Bind(d, "Head turns to the shooter", true, "A dying body turns its head toward whoever shot it.");
        ReachToShooter = Config.Bind(d, "Reach toward the shooter", true, "Lying hurt, about half reach their free hand toward the shooter.");
        BurstKeepsUp = Config.Bind(d, "Rounds keep a dying body up", true,
            "A dying body hit again stays on its feet a little longer (+0.08 s per round, at most +0.35 s): a burst keeps it up.");
        CorpseHitScale = Config.Bind(d, "Round push strength", 1f, new ConfigDescription("", new AcceptableValueRange<float>(0f, 3f)));

        FlinchScale = Config.Bind(f, "Flinch strength", 1f, new ConfigDescription("How hard a hit rocks a living bot (visual, needs Hit flinch). "
            + "Scales with the round, not the damage that got through, so an armour-stopped hit still rocks the body. 0 = no flinch, "
            + "1 = normal, 2-3 = big (the bend limit grows with it up to 2). Also scales the explosion flinch.", new AcceptableValueRange<float>(0f, 3f)));
        FlinchMaxDistance = Config.Bind(f, "Flinch max distance (m)", 120f,
            new ConfigDescription("Farther from the camera: no flinch (saves time).", new AcceptableValueRange<float>(10f, 1000f)));
        FlinchLegDip = Config.Bind(f, "Leg hit: knee dips", true, "A leg hit bends the knee for a moment.");
        FlinchHeadSnap = Config.Bind(f, "Head hit: head snaps", true, "A head hit snaps the head back harder than a body hit.");
        FlinchWhiplash = Config.Bind(f, "Torso hit: head whiplash", true, "The head lags behind a hit to the chest or stomach.");
        // 0.17.5 (user: "remove stagger entirely, just let hit flinch handle it"): the living stagger (reel back, lean, flailing
        // arm, steps, push, interrupt, hold fire) is gone with its settings; a hit is the flinch alone
        FlinchArms = Config.Bind(f, "Arm hit: arm jerks", true, "An arm hit knocks the arm back for a moment (the hand may leave the gun briefly).");

        KnockdownImpact = Config.Bind(k, "Hit strength to go down", 80f,
            new ConfigDescription("Damage that has to land within 0.4 s (a burst, shotgun pellets; leg hits count 1.5x; any hit counts at least 8).",
                new AcceptableValueRange<float>(10f, 400f)));
        KnockdownChance = Config.Bind(k, "Chance to go down", 0.5f,
            new ConfigDescription("Share of hits strong enough that really knock it down.", new AcceptableValueRange<float>(0f, 1f)));
        KnockdownCooldown = Config.Bind(k, "Cooldown (s)", 8f,
            new ConfigDescription("From a bot going down (or a failed roll), this long before it can go down again. A long stay-down can use it all up.", new AcceptableValueRange<float>(0f, 60f)));
        KnockdownLieMin = Config.Bind(k, "Time on the ground min (s)", 1.5f, new ConfigDescription("", new AcceptableValueRange<float>(0.3f, 10f)));
        KnockdownLieMax = Config.Bind(k, "Time on the ground max (s)", 3f, new ConfigDescription("", new AcceptableValueRange<float>(0.3f, 15f)));
        KnockdownMisfireChance = Config.Bind(k, "Misfire chance", 0.5f,
            new ConfigDescription("Share of knockdowns where the bot gets off a reflex shot or two as it goes down (wherever the falling gun points); "
                + "otherwise it cannot fire until it is back up.", new AcceptableValueRange<float>(0f, 1f)));
        BlastKnockdown = Config.Bind(k, "Explosions knock down", true,
            "A bot close to an explosion it survives (inner half of the blast reach) can be thrown down and get back up.");
        BlastKnockdownChance = Config.Bind(k, "Explosion knockdown chance", 0.7f, new ConfigDescription("", new AcceptableValueRange<float>(0f, 1f)));
        // 0.12.2 (user: "forget it, we have agony sounds anyway" - other mods already voice the hurt): off by default
        KnockdownVoice = Config.Bind(k, "Voice lines when down", false,
            "A knocked-down bot cries out as it falls and groans while it is down (EFT's own voice lines); the dead are silent.");
        KnockdownWrithe = Config.Bind(k, "Writhe while down", true,
            "A knocked-down bot rolls a little and draws a knee up now and then, so you can tell it from a dead one.");
        StayDownEnabled = Config.Bind(k, "Bad leg: stay down and fight", true,
            "A knocked-down bot with a destroyed or badly hurt leg may not get up for a while: it sits or lies prone (see Bad leg: "
            + "pose) and fires back, then struggles up. The Prone pose needs \"Get up through prone\".");
        BadLegPose = Config.Bind(k, "Bad leg: pose", LegPose.Sit,
            "Sit: sits up on its own ragdoll, turns to the shooter and fires short bursts at him (the mod aims and fires the gun). "
            + "Prone: lies in EFT's prone and fights with its own AI.");
        StayDownChance = Config.Bind(k, "Stay down chance", 0.7f, new ConfigDescription("", new AcceptableValueRange<float>(0f, 1f)));
        StayDownLegHealth = Config.Bind(k, "Leg health to stay down", 0.35f,
            new ConfigDescription("A leg below this share of its health (or destroyed) counts as badly hurt.", new AcceptableValueRange<float>(0f, 1f)));
        StayDownMin = Config.Bind(k, "Stay down min (s)", 10f, new ConfigDescription("", new AcceptableValueRange<float>(1f, 120f)));
        StayDownMax = Config.Bind(k, "Stay down max (s)", 25f, new ConfigDescription("", new AcceptableValueRange<float>(1f, 300f)));
        KnockdownProneGetUp = Config.Bind(k, "Get up through prone", true,
            "On: the body stays where it lies while the bot goes prone, then it stands up with EFT's own stand-up animation. "
            + "Off (or no room to go prone): the body blends straight from lying to standing over the getting up time.");
        // 0.16.0 (user: "they dont get up right. they just kinda warp there")
        KnockdownRollOver = Config.Bind(k, "Roll onto the front before getting up", true,
            "A bot lying on its back rolls onto its front with its own muscles before it gets up (EFT's prone lies face down; "
            + "off: the body turns over in the blend, which looks like a warp).");
        KnockdownHoldStill = Config.Bind(k, "Hold still while getting up", true,
            "While the body blends into EFT's prone, the bot's AI may not turn or crawl (else the pose slides away from where it lay).");
        KnockdownRiseTime = Config.Bind(k, "Getting up time (s)", 0.9f,
            new ConfigDescription("Blend from lying straight to standing (when not getting up through prone).", new AcceptableValueRange<float>(0.2f, 4f)));

        LinkFeatureSwitches();     // first: bound before the clean-up, or it removed (and rewrote) them every launch
        CarryOverMovedSettings();

        try
        {
            var h = new Harmony("amos.euphoriastylephysics");
            h.Patch(AccessTools.Method(typeof(Corpse), nameof(Corpse.TryToCreateRagdoll)),
                postfix: new HarmonyMethod(typeof(Plugin), nameof(RagdollCreatedPostfix)));
            h.Patch(AccessTools.Method(typeof(PlayerRigidbodySleepHierarchy), nameof(PlayerRigidbodySleepHierarchy.TryPutToSleep)),
                prefix: new HarmonyMethod(typeof(Plugin), nameof(TryPutToSleepPrefix)));
            // EFT steps physics from script (PhysicsExtensions.Simulation: per frame, sub-stepped or smoothed); forces are
            // cleared after each step, so the muscles are applied right before every one of them. EFT's own wrapper is small
            // enough to be inlined by the JIT, Unity's Physics.Simulate is not.
            h.Patch(AccessTools.Method(typeof(Physics), nameof(Physics.Simulate), new[] { typeof(float) }),
                prefix: new HarmonyMethod(typeof(Plugin), nameof(SimulatePrefix)));
            h.Patch(AccessTools.Method(typeof(BodyPartCollider), nameof(BodyPartCollider.ApplyHit)),
                postfix: new HarmonyMethod(typeof(Plugin), nameof(BodyPartHitPostfix)));
            // 0.17.4: a corpse woken near you is frozen before EFT takes its joints off (see CorpseWake)
            h.Patch(AccessTools.Method(typeof(GameWorld), nameof(GameWorld.FindInteractable)),
                postfix: new HarmonyMethod(typeof(CorpseWake), nameof(CorpseWake.FindInteractablePostfix)));
            h.Patch(AccessTools.Method(typeof(JointSpawner), nameof(JointSpawner.Remove)),
                prefix: new HarmonyMethod(typeof(CorpseWake), nameof(CorpseWake.BeforeJointRemove)));
            // knockdowns: no body animation, no movement, no firing while down
            h.Patch(AccessTools.Method(typeof(Player), nameof(Player.BodyUpdate)),
                prefix: new HarmonyMethod(typeof(Plugin), nameof(FrozenPrefix)));
            h.Patch(AccessTools.Method(typeof(Player), nameof(Player.MovementUpdate)),
                prefix: new HarmonyMethod(typeof(Plugin), nameof(FrozenPrefix)));
            h.Patch(AccessTools.Method(typeof(Player.FirearmController), nameof(Player.FirearmController.SetTriggerPressed), new[] { typeof(bool) }),
                prefix: new HarmonyMethod(typeof(Plugin), nameof(TriggerPrefix)));
            h.Patch(AccessTools.Method(typeof(BotSteering), nameof(BotSteering.ManualFixedUpdate)),
                prefix: new HarmonyMethod(typeof(Plugin), nameof(SteeringPrefix)));
            h.Patch(AccessTools.Method(typeof(ExplosionSharedMethods), nameof(ExplosionSharedMethods.Explosion)),
                prefix: new HarmonyMethod(typeof(Plugin), nameof(ExplosionPrefix)));
            // 0.16.2 (user: "cant search this guy"): log every step of a body search by the local player
            h.Patch(AccessTools.Method(typeof(Player), nameof(Player.Interact), new[] { typeof(IItemOwner), typeof(Callback) }),
                prefix: new HarmonyMethod(typeof(Plugin), nameof(LootInteractPrefix)));
            h.Patch(AccessTools.Method(AccessTools.TypeByName("EFT.EftGamePlayerOwner"), "ShowInventoryScreenLoot"),
                prefix: new HarmonyMethod(typeof(Plugin), nameof(LootScreenPrefix)));
            h.Patch(AccessTools.Method(typeof(Player), nameof(Player.ShotReactions)),
                postfix: new HarmonyMethod(typeof(Plugin), nameof(ShotReactionsPostfix)));
            // 0.17.5 (a bot killed while healing vanished): null body parts at corpse settle (on CorpseRagdoll, so Fika's observed
            // corpses too)
            h.Patch(AccessTools.Method(typeof(RigidbodySpawner), nameof(RigidbodySpawner.Create)),
                prefix: new HarmonyMethod(typeof(Plugin), nameof(SpawnerCreatePrefix)));
            h.Patch(AccessTools.Method(typeof(CorpseRagdoll), nameof(CorpseRagdoll.StopRigidbody)),
                prefix: new HarmonyMethod(typeof(Plugin), nameof(StopRigidbodyPrefix)));
            // 0.20.0: weapon drop on death (on CorpseRagdoll, so Fika's observed corpses too)
            h.Patch(AccessTools.Method(typeof(CorpseRagdoll), nameof(CorpseRagdoll.AttachWeapon)),
                postfix: new HarmonyMethod(typeof(Plugin), nameof(AttachWeaponPostfix)));
            // Fika's ObservedPlayer (bots and players on a client) may override ShotReactions without calling it; patch the
            // override too when it exists (a patch on the base method does not cover an override).
            var observed = AccessTools.TypeByName("Fika.Core.Main.Players.ObservedPlayer");
            var obsMethod = observed == null ? null : AccessTools.DeclaredMethod(observed, nameof(Player.ShotReactions));
            if (obsMethod != null)
                h.Patch(obsMethod, postfix: new HarmonyMethod(typeof(Plugin), nameof(ShotReactionsPostfix)));
            Log.LogInfo($"[EuphoriaStylePhysics] 0.21.1 loaded; toggle key {ToggleKey.Value}; patches on: Corpse.TryToCreateRagdoll, sleep hierarchy, Physics.Simulate, BodyPartCollider.ApplyHit, Player.BodyUpdate/MovementUpdate, FirearmController.SetTriggerPressed, BotSteering.ManualFixedUpdate, ExplosionSharedMethods.Explosion, Player.ShotReactions, CorpseRagdoll.StopRigidbody/AttachWeapon, loot search log" +
                        $"{(obsMethod != null ? " + ObservedPlayer.ShotReactions" : "")}; Fika {(observed != null ? "present" : "absent")}");
        }
        catch (Exception e)
        {
            Log.LogError($"[EuphoriaStylePhysics] patching failed, mod off for this session: {e}");
            _patchFailed = true;   // 0.17.5: was saved as Enabled = false, off on every later launch
        }

        Preset.SettingChanged += (_, __) => { if (Preset.Value == StylePreset.Euphoric) ApplyEuphoric("picked in the settings"); };
        if (Preset.Value == StylePreset.Euphoric && (!PresetApplied.Value || PresetRevision.Value < 4))
            ApplyEuphoric(PresetApplied.Value ? "preset revision 4" : "first start with the preset");

        // 0.15.0 Fika sync, only when Fika is installed
        if (BepInEx.Bootstrap.Chainloader.PluginInfos.ContainsKey("com.fika.core"))
        {
            // 0.17.1: the Fika code is a separate DLL next to this one, loaded only here (see Net)
            try
            {
                var path = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(Info.Location), "EuphoriaStylePhysics.Fika.dll");
                var init = System.Reflection.Assembly.LoadFrom(path).GetType("EuphoriaStylePhysics.FikaSync", true)
                    .GetMethod("Init", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public);
                init.Invoke(null, null);
                Net.Fika = true;
            }
            catch (Exception e)
            {
                Log.LogWarning($"[EuphoriaStylePhysics] Fika sync not started (EuphoriaStylePhysics.Fika.dll): {(e is System.Reflection.TargetInvocationException t && t.InnerException != null ? t.InnerException.Message : e.Message)}");
            }
        }
        else Log.LogInfo("[EuphoriaStylePhysics] Fika not installed: no sync needed");

        Flinch.Install();
        PatchInventoryWalker();
        // EFT's own explosion event (position, the explosive's max explosion distance), raised for grenades, mines and shells
        ExplosionSharedMethods.OnExplosion += OnExplosion;
    }

    // the Euphoric preset (picked by Claude 2026-10-28; renamed 0.17.0). 2026-11-03: set to the user's tested settings (hold-up 0.4,
    // rounds no longer keep a dying body up, flinch 0.3 without arm jerks); revision unchanged, so nobody's file is rewritten.
    // Revision 4 (0.17.0 test release, user: buggy and gameplay parts off, bodies launched too far): knockdowns off; stumble,
    // dying time and blast throw turned down (0.17.5: the stagger settings are gone). Written once over older revisions.
    private static void ApplyEuphoric(string why)
    {
        DyingVariety.Value = 0.15f;
        KnockdownVariety.Value = 0.15f;
        // dying: a short consistent stagger, hand to the wound, knees give; headshots drop at once; writhing, little twitching
        DeathEnabled.Value = true;
        // 0.21.0 (user: "make it by default turn eft ragdoll settings off"): no kill is handed to EFT's own ragdoll
        DropDeadChance.Value = 0f;
        HeadshotInstantDrop.Value = false;
        DyingTimeMin.Value = 1.0f;
        DyingTimeMax.Value = 2.0f;
        HoldUp.Value = 0.4f;   // 2026-11-03 (user: "make the euphoria preset what i have now"): a lighter hold, it drops sooner
        StumbleScale.Value = 0.5f;
        HandOnWound.Value = true;
        Stepping.Value = true;
        Throes.Value = 0.3f;
        ThroesNoneChance.Value = 0.85f;
        RunTumble.Value = true;
        GroundBrace.Value = true;
        WallBrace.Value = true;
        LieWoundedChance.Value = 0.5f;
        MeleeClutch.Value = true;
        BurstKeepsUp.Value = false;   // 2026-11-03 (user's settings): rounds do not keep a dying body up
        LookAtShooter.Value = true;
        ReachToShooter.Value = true;
        ExplosionsVanilla.Value = false;
        BlastThrow.Value = true;
        BlastThrowScale.Value = 0.6f;
        // living: every hit flinches and they keep fighting; heavy hits, leg hits and blasts put them down (knockdowns, off)
        FlinchEnabled.Value = true;
        FlinchScale.Value = 0.3f;   // 2026-11-03 (user: "set flinch strength to 0.3")
        FlinchArms.Value = false;
        BlastFlinch.Value = true;
        KnockdownEnabled.Value = false;
        KnockdownImpact.Value = 90f;
        KnockdownChance.Value = 0.6f;
        KnockdownMisfireChance.Value = 0.4f;
        BlastKnockdown.Value = true;
        BlastKnockdownChance.Value = 0.8f;
        KnockdownProneGetUp.Value = true;
        KnockdownVoice.Value = false;
        KnockdownRiseTime.Value = 1.3f;
        StayDownEnabled.Value = true;
        BadLegPose.Value = LegPose.Sit;
        StayDownChance.Value = 0.7f;
        StayDownLegHealth.Value = 0.35f;
        StayDownMin.Value = 10f;
        StayDownMax.Value = 25f;
        KnockdownWrithe.Value = true;
        WallReach.Value = 1.4f;
        KnockdownRollOver.Value = true;
        KnockdownHoldStill.Value = true;
        PresetApplied.Value = true;
        PresetRevision.Value = 4;
        Log.LogInfo($"[EuphoriaStylePhysics] Euphoric preset applied ({why}, revision 4): variety 0.15, dying 1.0-2.0 s, hold-up 0.4, stumble 0.5, flinch 0.3, "
                    + "no EFT ragdoll (drop dead 0, headshots not instant, explosions ours), blast throw 0.6; knockdowns OFF");
    }

    private void Update()
    {
        Flinch.KeepInstalled();
        Knockdown.Tick();
        CorpseWake.Tick();
        CorpseBump.Tick();
        if (ToggleKey.Value.IsDown())
        {
            Enabled.Value = !Enabled.Value;
            string msg = $"Euphoria Style Physics {(Enabled.Value ? "ON" : "OFF")}";
            Log.LogInfo($"[EuphoriaStylePhysics] toggle key: {msg}");
            try { NotificationManager.DisplayMessageNotification(msg); }
            catch (Exception e) { Log.LogWarning($"[EuphoriaStylePhysics] no in-game message: {e.Message}"); }
        }
    }

    // 0.17.5 (opening inventories spiked the frame time to ~6 ms): the search log looked the body up with FindObjectsOfType<Corpse>
    // (a scan of the whole scene) on every loot interaction. Corpses are remembered as they are made.
    private static readonly System.Collections.Generic.List<Corpse> KnownCorpses = new System.Collections.Generic.List<Corpse>();

    // 0.20.0 (user: "feature switches", "settings regrouped"): every feature's main switch twice: side by side in 1. General, and at the
    // top of its own section. The two are one switch: flipping either flips the other (the one in the feature's section wins at
    // start, so a preset or old cfg value holds).
    private void LinkFeatureSwitches()
    {
        var list = new (ConfigEntry<bool> main, string name, string what)[]
        {
            (DeathEnabled, "Dying with muscles", "Bodies killed on their feet stumble, reach for the wound and drop under muscle physics."),
            (FlinchEnabled, "Hit flinch", "A quick snap (about 0.25 s) of the part that was hit."),
            (BlastFlinch, "Explosion flinch", "Survivors near an explosion recoil away from it."),
            (KnockdownEnabled, "Knockdowns", "Bots hit hard can go down and get back up. Changes fights."),
            (CorpseWakeOn, "Corpses stay physical near you", "Corpses near you stay ragdolls instead of freezing."),
        };
        int order = 990;
        foreach (var (main, name, what) in list)
        {
            if (main == null) continue;
            var copy = Config.Bind("1. General", "Feature switch: " + name, main.Value,
                new ConfigDescription($"Same switch as \"{name}\" in {main.Definition.Section}: flip either one. {what}", null,
                    new ConfigurationManagerAttributes { Order = order--, DispName = name }));
            copy.Value = main.Value;
            bool busy = false;
            main.SettingChanged += (_, __) => { if (busy) return; busy = true; try { copy.Value = main.Value; } finally { busy = false; } };
            copy.SettingChanged += (_, __) => { if (busy) return; busy = true; try { main.Value = copy.Value; } finally { busy = false; } };
        }
    }

    // 0.20.0: settings moved to new sections keep their value. BepInEx keeps a line it no longer reads as an orphan; one with the same
    // name as a setting now bound in another section is that setting's old value. Lines of settings this version no longer has
    // (the stagger's) are dropped from the file. Runs after every setting is bound.
    private void CarryOverMovedSettings()
    {
        try
        {
            var prop = typeof(ConfigFile).GetProperty("OrphanedEntries", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public);
            if (!(prop?.GetValue(Config) is System.Collections.Generic.Dictionary<ConfigDefinition, string> orphans) || orphans.Count == 0) return;
            int moved = 0;
            // the one Consistency in 1. General became a Variety per section (variety = 1 - consistency): it goes to both
            var oldConsistency = new ConfigDefinition("1. General", "Consistency");
            if (orphans.TryGetValue(oldConsistency, out var cons))
            {
                float variety = float.TryParse(cons, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var cv) ? Mathf.Clamp01(1f - cv) : 0.5f;
                DyingVariety.Value = variety;
                KnockdownVariety.Value = variety;
                orphans.Remove(oldConsistency);
                moved += 2;
            }
            // the same name in a section of the same feature first ("6. Knockdowns" -> "4. Knockdowns"); a name bound in one place only
            // otherwise (a name like "Variety" sits in two sections and is only carried to the same feature's)
            static string Bare(string s) { int i = s.IndexOf(". ", StringComparison.Ordinal); return i >= 0 ? s.Substring(i + 2) : s; }
            foreach (var old in new System.Collections.Generic.List<ConfigDefinition>(orphans.Keys))
            {
                ConfigDefinition same = null, only = null;
                int n = 0;
                foreach (var def in Config.Keys)
                {
                    if (def.Key != old.Key || def.Section == old.Section) continue;
                    n++;
                    only = def;
                    if (Bare(def.Section) == Bare(old.Section)) same = def;
                }
                var to = same ?? (n == 1 ? only : null);
                if (to == null) continue;
                Config[to].SetSerializedValue(orphans[old]);
                orphans.Remove(old);
                moved++;
            }
            int dropped = orphans.Count;
            orphans.Clear();
            if (moved > 0 || dropped > 0)
            {
                Config.Save();
                Log.LogInfo($"[EuphoriaStylePhysics] {moved} settings moved to their new sections with your values, {dropped} old settings removed from the file");
            }
        }
        catch (Exception e) { Log.LogWarning($"[EuphoriaStylePhysics] settings not carried over to the new sections: {e.Message}"); }
    }

    private static void RagdollCreatedPostfix(Corpse __instance, bool forceStill)
    {
        if (__instance != null)
        {
            KnownCorpses.RemoveAll(c => c == null);
            KnownCorpses.Add(__instance);
        }
        if (__instance != null && __instance.Ragdoll != null)
            try { CorpseWake.Track(__instance, __instance.GetComponent<Player>()?.Profile?.Nickname ?? __instance.name); }
            catch (Exception e) { Log.LogWarning($"[EuphoriaStylePhysics] corpse not tracked for waking: {e.Message}"); }
        // 0.17.5: this ran after the gate below, so with "Dying with muscles" off, the mod toggled off or a still corpse (forceStill)
        // a bot that died knocked down kept its knockdown state: world collisions on, extra physics steps every frame, its gun
        // dragged to its palm, agony lines from the corpse
        try { var dead = __instance != null ? __instance.GetComponent<Player>() : null; if (dead != null) Knockdown.OnDeath(dead); }
        catch (Exception e) { Log.LogWarning($"[EuphoriaStylePhysics] knockdown not ended at death: {e.Message}"); }
        if (!IsOn || !DeathEnabled.Value || forceStill || __instance == null || __instance.Ragdoll == null) return;
        try
        {
            var player = __instance.GetComponent<Player>();
            if (player == null) return;   // map corpses and late-join corpses have no living body to die from
            DeathMotion.Begin(__instance, player);
        }
        catch (Exception e)
        {
            Log.LogError($"[EuphoriaStylePhysics] death start failed: {e}");
        }
    }

    private static void BodyPartHitPostfix(BodyPartCollider __instance, DamageInfo damageInfo)
    {
        if (!IsOn || !damageInfo.IsForwardHit) return;
        try
        {
            var rb = damageInfo.HitCollider != null ? damageInfo.HitCollider.attachedRigidbody : null;
            if (rb == null && __instance != null)
            {
                var c = __instance.GetComponent<Collider>();
                if (c != null) rb = c.attachedRigidbody;
            }
            if (rb != null) DeathMotion.OnRound(rb, damageInfo.HitPoint, damageInfo.Direction, damageInfo.Damage);
        }
        catch (Exception e)
        {
            Log.LogError($"[EuphoriaStylePhysics] round into a body failed: {e}");
        }
    }

    private static void OnExplosion(Vector3 position, float maxDistance)
    {
        if (!IsOn) return;
        try
        {
            if (!Singleton<GameWorld>.Instantiated) return;
            float reach = Mathf.Clamp(Mathf.Max(maxDistance, 3f) * 1.5f, 3f, 20f);
            int n = 0, down = 0;
            foreach (var p in Singleton<GameWorld>.Instance.AllAlivePlayersList)
            {
                if (p == null) continue;
                float dist = Vector3.Distance(p.Position, position);
                if (dist > reach) continue;
                if (Knockdown.OnBlast(p, position, 1f - dist / reach)) { down++; continue; }
                if (BlastFlinch.Value && Flinch.OnBlast(p, position, 2f * (1f - dist / reach))) n++;   // 0.17.5: its own switch, not gated by Hit flinch
            }
            if (n > 0 || down > 0 || VerboseLog.Value)
                Log.LogInfo($"[EuphoriaStylePhysics] explosion (reach {reach:F1} m): {down} thrown down, {n} survivor(s) recoil");
        }
        catch (Exception e)
        {
            Flinch.Fail(e);
        }
    }

    // 0.17.0: RealTargets (com.stompa.realtargets, read from its settings only) throws dead bodies within 6 m (chest) of a grenade
    // when its General > Enabled and Impacts > Blast injuries are on (checked live: it switches live).
    private static ConfigEntry<bool> _rtEnabled, _rtBlast, _rtImpacts, _rtPushCorpses;
    private static ConfigEntry<float> _rtPushStrength;
    private static bool _rtLooked;

    // 0.17.5: RealTargets pushes rounds into dead bodies when its General > Enabled, Impacts > Enabled and Push corpses are on and
    // its Push strength is above 0 (checked live). Then our own push stays off, whatever "Rounds push dying bodies" says.
    internal static bool RealTargetsPushesCorpses()
    {
        RealTargetsThrowsCorpses();   // reads its settings once
        return _rtImpacts != null && _rtImpacts.Value && (_rtPushCorpses == null || _rtPushCorpses.Value) &&
               (_rtPushStrength == null || _rtPushStrength.Value > 0f) && (_rtEnabled == null || _rtEnabled.Value);
    }

    internal static bool RealTargetsThrowsCorpses()
    {
        if (!_rtLooked)
        {
            _rtLooked = true;
            try
            {
                if (BepInEx.Bootstrap.Chainloader.PluginInfos.TryGetValue("com.stompa.realtargets", out var info) && info.Instance != null)
                {
                    info.Instance.Config.TryGetEntry("General", "Enabled", out _rtEnabled);
                    info.Instance.Config.TryGetEntry("Impacts", "Blast injuries", out _rtBlast);
                    info.Instance.Config.TryGetEntry("Impacts", "Enabled", out _rtImpacts);
                    info.Instance.Config.TryGetEntry("Impacts", "Push corpses", out _rtPushCorpses);
                    info.Instance.Config.TryGetEntry("Impacts", "Push strength", out _rtPushStrength);
                    Log.LogInfo($"[EuphoriaStylePhysics] RealTargets found: Enabled {(_rtEnabled != null ? _rtEnabled.Value.ToString() : "not found")}, " +
                                $"Blast injuries {(_rtBlast != null ? _rtBlast.Value.ToString() : "not found")}, " +
                                $"Impacts {(_rtImpacts != null ? _rtImpacts.Value.ToString() : "not found")}, " +
                                $"Push corpses {(_rtPushCorpses != null ? _rtPushCorpses.Value.ToString() : "not found")}, " +
                                $"Push strength {(_rtPushStrength != null ? _rtPushStrength.Value.ToString("F2") : "not found")}");
                }
            }
            catch (Exception e) { Log.LogWarning($"[EuphoriaStylePhysics] RealTargets settings not read: {e.Message}"); }
        }
        return _rtBlast != null && _rtBlast.Value && (_rtEnabled == null || _rtEnabled.Value);
    }

    private static bool FrozenPrefix(Player __instance) => !Knockdown.IsFrozen(__instance);

    private static void ExplosionPrefix(IExplosiveItem grenadeItem, Vector3 explosionPosition)
    {
        try { DeathMotion.RecordBlast(explosionPosition, grenadeItem != null ? grenadeItem.MaxExplosionDistance : 5f, grenadeItem != null); }
        catch (Exception e) { Log.LogWarning($"[EuphoriaStylePhysics] blast position not recorded: {e.Message}"); }
    }

    // 0.20.0: EFT joins the hand item to the corpse: SpringJoint strap (long guns) or FixedJoint to the forearm (pistol, knife,
    // grenade; Player.DropItemDead). Same as HollywoodFX: strap off (falls), hand item hidden.
    // user 2026-11-04 ("making sure it works fine with hollywoodfxs drop on"): with another mod's drop on (HollywoodFX's "Drop Weapon
    // on Death"), the gun still falls through its patch, and ours still fits and settles it (its own drop only cuts the strap, so its
    // guns floated on their oversized colliders). Both cutting the same strap is harmless: Destroy only lands at the frame's end.
    private static int _otherWeaponDrop = -1;
    private static void AttachWeaponPostfix(CorpseRagdoll __instance, Rigidbody weaponRigidbody)
    {
        if (!IsOn || weaponRigidbody == null) return;
        try
        {
            if (_otherWeaponDrop < 0)
            {
                var info = Harmony.GetPatchInfo(AccessTools.Method(typeof(CorpseRagdoll), nameof(CorpseRagdoll.AttachWeapon)));
                Patch other = null;
                if (info != null) foreach (var p in info.Postfixes) if (p.owner != "amos.euphoriastylephysics") { other = p; break; }
                _otherWeaponDrop = other != null ? 1 : 0;
                Log.LogInfo(other != null
                    ? $"[EuphoriaStylePhysics] {other.owner} ({other.PatchMethod.DeclaringType?.FullName}) also drops weapons: ours fits and settles them"
                    : "[EuphoriaStylePhysics] weapon drop on (no other mod drops weapons)");
            }
            // ours off and no other mod dropping: EFT's strap stays
            if (!DropWeapon.Value && _otherWeaponDrop != 1) return;
            var spring = weaponRigidbody.GetComponent<SpringJoint>();
            if (spring != null)
            {
                UnityEngine.Object.Destroy(spring);
                // 0.20.0 (user: bots flew up while dying, then back; log: heads at 10-17 m/s right after the drop): EFT puts the weapon
                // in the right palm and its strap lets it hit the body. Loose, it sat inside the hand and chest colliders and shoved
                // them apart. The dropped weapon no longer touches its own body (it still lands on the ground).
                // 2026-11-03 (user screenshots: dropped guns floating, resting on "an invisible wall" that moves with the gun): its
                // colliders reach well past the model, so on the floor it stood on them. The dropped gun now collides with one box
                // fitted to what is drawn, and ignores the whole dead player (body parts and its movement capsule).
                var fitted = FitDroppedWeapon(weaponRigidbody);
                // 2026-11-03 (user: "cant you just make the gun not move?"): it falls and lands, then stays where it lies (walking
                // into it does not push it, see CorpseBump; once at rest it is locked in place)
                DroppedWeapons.Add(weaponRigidbody);
                weaponRigidbody.gameObject.AddComponent<WeaponSettle>();
                var owner = __instance._playerBody != null ? __instance._playerBody.GetComponentInParent<Player>() : null;
                var bodyRoot = owner != null ? ((Component)owner).transform : __instance._playerBody != null ? __instance._playerBody.transform : null;
                if (bodyRoot != null && fitted != null)
                {
                    var mine = fitted.GetComponents<BoxCollider>();
                    foreach (var bc in bodyRoot.GetComponentsInChildren<Collider>(true))
                        if (bc.gameObject != fitted.gameObject)
                            foreach (var m in mine) Physics.IgnoreCollision(m, bc, true);
                }
            }
            else weaponRigidbody.gameObject.SetActive(false);
            Log.LogInfo($"[EuphoriaStylePhysics] corpse of {CorpseName(__instance)}: weapon {weaponRigidbody.name} {(spring != null ? "dropped (strap removed)" : "hidden (held in hand)")}");
        }
        catch (Exception e) { Log.LogWarning($"[EuphoriaStylePhysics] weapon drop failed: {e.Message}"); }
    }

    internal static readonly HashSet<Rigidbody> DroppedWeapons = new HashSet<Rigidbody>();

    // A dropped gun locks in place once it has come to rest (asleep, or slow for half a second; 15 s at most)
    // user 2026-11-03 ("the game seems to break when you pick up a weapon that was dropped"; arms twisted around the gun): EFT puts
    // the same weapon object in the hands of whoever picks it up, and our drop changes (its colliders off, a fitted box, the lock)
    // went with it. They are undone the moment it leaves the corpse: moved to another parent (into hands), switched off (back to
    // EFT's pool), or its rigidbody removed (EFT's ClearWeapon).
    private sealed class DroppedWeaponState : MonoBehaviour
    {
        public readonly List<Collider> Off = new List<Collider>();
        public BoxCollider Box;
        public Rigidbody Rb;
        public Transform Parent;
        private bool _restored;
        private static int _logs;

        // user 2026-11-04 (movement still broke after picking up a gun): checked here, a frame after it moved, not the moment it
        // moved: EFT turns a gun's colliders off for the hands around that move, and restoring right then turned them back on
        private void LateUpdate()
        {
            if (transform.parent != Parent) { Restore("taken"); return; }
            // user 2026-11-04 (screenshot: AI stuck on dropped guns in a doorway): EFT removes the gun's rigidbody once the body is
            // out of sight (ClearWeapon). Restoring then switched its own colliders back on, and they reach well past the model.
            // A gun still lying where it fell keeps our fitted box (corpse layer) until someone takes it or it goes back to the pool.
            if (Rb == null && !_bodyGone)
            {
                _bodyGone = true;
                DroppedWeapons.RemoveWhere(r => r == null);
                var settle = GetComponent<WeaponSettle>();
                if (settle != null) Destroy(settle);
                if (VerboseLog.Value) Log.LogInfo($"[EuphoriaStylePhysics] dropped weapon {name}: physics body removed by EFT, fitted box kept");
            }
        }
        private bool _bodyGone;

        private void OnDisable() => Restore("put away");

        private void Restore(string why)
        {
            if (_restored) return;
            _restored = true;
            try
            {
                // user 2026-11-04 (a picked-up gun "spazzing out and slowly sliding me"): EFT's DropItemDead recorded each collider's
                // in-hands state (AssetPoolObject.StoreCollider) before switching it on for the ground, and puts that state back when the
                // gun leaves the corpse. Switching them on here undid that and the held gun collided with its holder. So the gun's own
                // colliders come back only while it still lies where it fell; once it has moved or gone, EFT's record decides.
                bool lying = why != "put away" && transform.parent == Parent && gameObject.activeInHierarchy;
                if (lying) foreach (var c in Off) if (c != null) c.enabled = true;
                if (Box != null) { Box.gameObject.SetActive(false); Destroy(Box.gameObject); }   // all its boxes off now   // off now: Destroy only lands at the frame's end
                var settle = GetComponent<WeaponSettle>();
                if (settle != null) Destroy(settle);
                bool body = false;
                if (Rb != null)
                {
                    DroppedWeapons.Remove(Rb);
                    // user 2026-11-04 ("whenever i pick up a weapon that was dropped on the ground my movement breaks severely"): a gun
                    // we had locked kept its (kinematic, unsupported) rigidbody into the hands, where it collided with its holder. A gun
                    // that leaves the corpse never takes a physics body along: off at once, removed with its joints.
                    if (why == "taken")
                    {
                        body = true;
                        Rb.detectCollisions = false;
                        Rb.isKinematic = true;
                        foreach (var j in GetComponents<Joint>()) Destroy(j);
                        Destroy(Rb);
                    }
                }
                _logs++;
                if (VerboseLog.Value || _logs <= 3)
                    Log.LogInfo($"[EuphoriaStylePhysics] dropped weapon {name} {why}: {(lying ? $"its own colliders back ({Off.Count})" : "its colliders left to EFT")}{(body ? ", its physics body removed" : "")}");
            }
            catch (Exception e) { Log.LogWarning($"[EuphoriaStylePhysics] dropped weapon not restored: {e.Message}"); }
            Destroy(this);
        }
    }

    private sealed class WeaponSettle : MonoBehaviour
    {
        private Rigidbody _rb;
        private Vector3 _long = Vector3.forward;   // the gun's long side, in its own frame (the fitted box's longest edge)
        private float _since, _slow;
        private int _nudges;

        private void Start()
        {
            _rb = GetComponent<Rigidbody>();
            _since = Time.time;
            var box = GetComponent<DroppedWeaponState>()?.Box;
            if (box != null)
            {
                Vector3 z = box.size;
                _long = z.x >= z.y && z.x >= z.z ? Vector3.right : z.y >= z.z ? Vector3.up : Vector3.forward;
            }
        }

        private void FixedUpdate()
        {
            if (_rb == null || _rb.isKinematic) { Done(); return; }
            _slow = _rb.velocity.sqrMagnitude < 0.01f && _rb.angularVelocity.sqrMagnitude < 0.04f ? _slow + Time.fixedDeltaTime : 0f;
            Vector3 along = transform.TransformDirection(_long);
            // user 2026-11-03 ("it fell vertically. 3 times out of 4"): a gun landing on its muzzle or stock pauses balanced before
            // it tips over, and was locked standing in that pause. It locks only lying down (its long side within ~30 deg of flat);
            // still standing when it stops, it gets a small push to tip over.
            bool lying = Mathf.Abs(Vector3.Dot(along, Vector3.up)) < 0.5f;
            bool rest = _rb.IsSleeping() || _slow > 0.5f;
            if (rest && !lying && _nudges < 3)
            {
                Vector3 tip = Vector3.Cross(Vector3.up, along);
                if (tip.sqrMagnitude < 1e-4f) tip = Vector3.Cross(Vector3.up, transform.right);
                _rb.WakeUp();
                _rb.AddTorque(tip.normalized * 0.6f, ForceMode.VelocityChange);
                _slow = 0f;
                _nudges++;
                return;
            }
            // only at rest and lying down (or given up after three pushes); 15 s is a safety cap
            if (rest || Time.time - _since > 15f) FlattenAndLock();
        }

        // user 2026-11-04 (screenshot: a rifle standing on its end beside the body): pushes did not always tip it, and EFT's own
        // StopWeapon froze guns that were still standing. Whatever locks it (us, or EFT through StopRigidbody), a gun that is not
        // lying flat is laid on its side on the floor under it first.
        internal void FlattenAndLock()
        {
            if (_rb == null) { Done(); return; }
            LayFlat(_rb, _long);
            _rb.velocity = Vector3.zero;
            _rb.angularVelocity = Vector3.zero;
            if (!_rb.isKinematic)
            {
                PhysicsExtensions.UpdateController.UnsupportRigidbody(_rb);   // as EFT's own StopRigidbody does before freezing
                _rb.isKinematic = true;
            }
            Done();
        }

        private static void LayFlat(Rigidbody rb, Vector3 longLocal)
        {
            var t = rb.transform;
            Vector3 along = t.TransformDirection(longLocal);
            if (Mathf.Abs(Vector3.Dot(along, Vector3.up)) < 0.5f) return;   // already lying
            Vector3 flat = Vector3.ProjectOnPlane(along, Vector3.up);
            if (flat.sqrMagnitude < 1e-4f) flat = Vector3.ProjectOnPlane(t.right, Vector3.up);
            if (flat.sqrMagnitude < 1e-4f) flat = Vector3.forward;
            Quaternion rot = Quaternion.FromToRotation(along, flat.normalized) * t.rotation;
            Vector3 pos = t.position;
            var box = rb.GetComponent<DroppedWeaponState>()?.Box;
            if (box != null && Physics.Raycast(rb.worldCenterOfMass + Vector3.up * 0.3f, Vector3.down, out var hit, 3f,
                    LayerMask.GetMask("HighPolyCollider", "LowPolyCollider", "Terrain"), QueryTriggerInteraction.Ignore))
            {
                Vector3 size = Vector3.Scale(box.size, t.lossyScale);
                float halfUp = 0.5f * (Mathf.Abs(Vector3.Dot(rot * Vector3.right, Vector3.up)) * size.x
                                     + Mathf.Abs(Vector3.Dot(rot * Vector3.up, Vector3.up)) * size.y
                                     + Mathf.Abs(Vector3.Dot(rot * Vector3.forward, Vector3.up)) * size.z);
                Vector3 centre = pos + rot * Vector3.Scale(box.center, t.lossyScale);
                pos += Vector3.up * (hit.point.y + halfUp + 0.005f - centre.y);
            }
            t.SetPositionAndRotation(pos, rot);
            rb.position = pos;
            rb.rotation = rot;
            if (VerboseLog.Value) Log.LogInfo($"[EuphoriaStylePhysics] dropped weapon {rb.name} was standing on its end: laid flat");
        }

        private void Done()
        {
            if (_rb != null) DroppedWeapons.Remove(_rb);
            DroppedWeapons.RemoveWhere(r => r == null);
            Destroy(this);
        }
    }

    // The dropped gun's collision: its own solid colliders off, one box around its drawn meshes (in the gun's frame) on instead.
    // Null if it has nothing drawn (then it keeps its colliders).
    private static BoxCollider FitDroppedWeapon(Rigidbody gun)
    {
        var t = gun.transform;
        bool any = false;
        Vector3 min = Vector3.positiveInfinity, max = Vector3.negativeInfinity;
        var parts = new List<(Vector3 min, Vector3 max)>();
        // only the gun's solid, nearest-detail meshes: lower LOD copies, laser beams, light cones and flash effects (transparent or
        // named so) made the box far bigger than the gun (user 2026-11-04: "their hitbox is too big")
        var skip = new HashSet<Renderer>();
        foreach (var lg in gun.GetComponentsInChildren<LODGroup>(false))
        {
            var lods = lg.GetLODs();
            for (int i = 1; i < lods.Length; i++) foreach (var lr in lods[i].renderers) if (lr != null) skip.Add(lr);
        }
        foreach (var r in gun.GetComponentsInChildren<Renderer>(false))
        {
            if (r == null || !r.enabled || !(r is MeshRenderer || r is SkinnedMeshRenderer) || skip.Contains(r)) continue;
            var mat = r.sharedMaterial;
            if (mat != null && mat.renderQueue >= 2500) continue;   // transparent: beams, glass, flashes
            string rn = r.name.ToLowerInvariant();
            if (rn.Contains("beam") || rn.Contains("laser") || rn.Contains("light") || rn.Contains("flash") || rn.Contains("fx") ||
                rn.Contains("glow") || rn.Contains("cone")) continue;
            var lb = r.localBounds;
            if (lb.size.sqrMagnitude < 1e-8f) continue;
            Vector3 pmin = Vector3.positiveInfinity, pmax = Vector3.negativeInfinity;
            for (int i = 0; i < 8; i++)
            {
                Vector3 corner = lb.center + Vector3.Scale(lb.extents, new Vector3((i & 1) == 0 ? -1 : 1, (i & 2) == 0 ? -1 : 1, (i & 4) == 0 ? -1 : 1));
                Vector3 p = t.InverseTransformPoint(r.transform.TransformPoint(corner));
                pmin = Vector3.Min(pmin, p);
                pmax = Vector3.Max(pmax, p);
            }
            min = Vector3.Min(min, pmin);
            max = Vector3.Max(max, pmax);
            parts.Add((pmin, pmax));
            any = true;
        }
        if (!any) return null;
        int off = 0;
        var state = gun.gameObject.AddComponent<DroppedWeaponState>();
        foreach (var c in gun.GetComponentsInChildren<Collider>(true))
            if (c != null && !c.isTrigger && c.enabled && c.attachedRigidbody == gun) { c.enabled = false; state.Off.Add(c); off++; }
        // on its own child on the corpse layer, as EFT puts a dead player's gun colliders: characters do not walk into it
        var holder = new GameObject("ESP dropped weapon box") { layer = LayerMask.NameToLayer("Deadbody") };
        holder.transform.SetParent(t, false);
        // the whole gun's box only measures it (its long side, its height when laid flat); it does not collide
        var box = holder.AddComponent<BoxCollider>();
        state.Box = box;
        state.Rb = gun;
        state.Parent = gun.transform.parent;
        box.center = (min + max) * 0.5f;
        box.size = Vector3.Max(max - min, new Vector3(0.02f, 0.02f, 0.02f));
        box.enabled = false;
        // user 2026-11-04 (screenshot: a rifle floating above the floor): one box around a whole rifle rests on its corners (magazine
        // and suppressor), with the rest of the gun in the air. It collides with one small box per solid part instead, so it lies on
        // its real shape: the largest parts first, parts inside another dropped, at most 24
        parts.Sort((a, b) => Vol(b).CompareTo(Vol(a)));
        var kept = new List<(Vector3 min, Vector3 max)>();
        foreach (var part in parts)
        {
            if (kept.Count >= 24) break;
            Vector3 sz = part.max - part.min;
            if (sz.x + sz.y + sz.z < 0.03f) continue;   // screws, pins, sight dots
            bool inside = false;
            foreach (var k in kept)
                if (k.min.x <= part.min.x + 0.003f && k.min.y <= part.min.y + 0.003f && k.min.z <= part.min.z + 0.003f &&
                    k.max.x >= part.max.x - 0.003f && k.max.y >= part.max.y - 0.003f && k.max.z >= part.max.z - 0.003f) { inside = true; break; }
            if (inside) continue;
            kept.Add(part);
            var pb = holder.AddComponent<BoxCollider>();
            pb.center = (part.min + part.max) * 0.5f;
            pb.size = Vector3.Max(sz, new Vector3(0.015f, 0.015f, 0.015f));
        }
        if (kept.Count == 0) box.enabled = true;   // nothing usable: the whole box collides
        gun.ResetCenterOfMass();
        gun.ResetInertiaTensor();
        if (VerboseLog.Value)
            Log.LogInfo($"[EuphoriaStylePhysics] dropped weapon {gun.name}: {off} colliders off, {kept.Count} part boxes, whole gun {box.size.x:F2} x {box.size.y:F2} x {box.size.z:F2} m");
        return box;
    }

    private static float Vol((Vector3 min, Vector3 max) p) { Vector3 s = p.max - p.min; return s.x * s.y * s.z; }

    // 0.21.0 (user: "now you cant loot them when you push them away from where they died"; log: Inventory Walker "Walked 4.8 m from
    // the loot (range 3.0 m); closing it" with the body 1.2 m away): Inventory Walker measures its loot range from the corpse's root,
    // where the bot died, not from where the body lies now. Its anchor for a corpse becomes the body's pelvis (it re-reads it every frame).
    private void PatchInventoryWalker()
    {
        if (!BepInEx.Bootstrap.Chainloader.PluginInfos.ContainsKey("com.mybutthasarash.inventorywalker")) return;
        try
        {
            var lootRange = AccessTools.TypeByName("InventoryWalker.LootRange");
            var prefix = lootRange == null ? null : AccessTools.Method(lootRange, "Prefix");
            _iwAnchor = lootRange == null ? null : AccessTools.Field(lootRange, "_anchor");
            _iwAnchorPos = lootRange == null ? null : AccessTools.Field(lootRange, "_anchorPosition");
            if (prefix == null || _iwAnchor == null || _iwAnchorPos == null)
            {
                Log.LogWarning("[EuphoriaStylePhysics] Inventory Walker found, but not the parts we expected: loot range still measured from where a body died");
                return;
            }
            new Harmony("amos.euphoriastylephysics.inventorywalker").Patch(prefix, postfix: new HarmonyMethod(typeof(Plugin), nameof(InventoryWalkerPostfix)));
            Log.LogInfo("[EuphoriaStylePhysics] Inventory Walker: loot range on a body measured from where the body lies");
        }
        catch (Exception e) { Log.LogWarning($"[EuphoriaStylePhysics] Inventory Walker patch failed: {e.Message}"); }
    }
    private static System.Reflection.FieldInfo _iwAnchor, _iwAnchorPos, _corpsePelvis;
    private static void InventoryWalkerPostfix()
    {
        try
        {
            if (!(_iwAnchor.GetValue(null) is Transform anchor) || anchor == null) return;
            var corpse = anchor.GetComponent<Corpse>();
            if (corpse == null) return;
            _corpsePelvis ??= AccessTools.Field(typeof(Corpse), "_pelvis");
            if (!(_corpsePelvis?.GetValue(corpse) is Transform pelvis) || pelvis == null) return;
            _iwAnchor.SetValue(null, pelvis);
            _iwAnchorPos.SetValue(null, pelvis.position);
        }
        catch (Exception e) { Log.LogWarning($"[EuphoriaStylePhysics] Inventory Walker anchor: {e.Message}"); }
    }

    private static string CorpseName(CorpseRagdoll rag)
    {
        var body = rag?._playerBody;
        if (body == null) return "?";
        var p = body.GetComponentInParent<Player>();
        return p?.Profile?.Nickname ?? body.transform.root.name;
    }

    // 0.17.5: EFT's StopRigidbody uses the body without a null check (CorpseRagdoll.StopRigidbody / its settle loop), and the corpse
    // vanished. When another mod (HollywoodFX's ragdoll section) has its own prefix there, the null body is left to it.
    private static int _otherNullFix = -1;
    // 2026-11-04 (user: a body that could not be dragged; TraumaCore's wound inspection and our rebuilds threw the same): EFT's
    // RigidbodySpawner.Create adds a Rigidbody only when it has none of its own, and Unity refuses a second one, so a body part
    // still carrying a rigidbody the spawner had lost track of made AddComponent return null and Create throw. Here that rigidbody
    // is taken back (a joint still hangs on it) or removed first, so Create works for every mod that rebuilds a ragdoll.
    private static readonly AccessTools.FieldRef<RigidbodySpawner, Rigidbody> SpawnerBody =
        AccessTools.FieldRefAccess<RigidbodySpawner, Rigidbody>("_rigidbody");
    private static int _spawnerRepairs;

    private static void SpawnerCreatePrefix(RigidbodySpawner __instance)
    {
        try
        {
            if (__instance == null || SpawnerBody(__instance) != null) return;
            var stray = __instance.GetComponent<Rigidbody>();
            if (stray == null) return;
            if (__instance.GetComponent<Joint>() != null)
                SpawnerBody(__instance) = stray;   // still holding a joint: it is this part's body, keep it
            else
                UnityEngine.Object.DestroyImmediate(stray);   // left over from a teardown: gone now, Create adds a fresh one
            _spawnerRepairs++;
            if (_spawnerRepairs <= 5 || VerboseLog.Value && _spawnerRepairs % 50 == 0)
                Log.LogInfo($"[EuphoriaStylePhysics] ragdoll part {__instance.name}: a leftover rigidbody {(SpawnerBody(__instance) != null ? "taken back" : "removed")} before EFT built it ({_spawnerRepairs} so far)");
        }
        catch (Exception e) { Log.LogWarning($"[EuphoriaStylePhysics] ragdoll part repair failed: {e.Message}"); }
    }

    private static bool StopRigidbodyPrefix(CorpseRagdoll __instance, Rigidbody rigidbody)
    {
        if (rigidbody != null)
        {
            // EFT freezing a dropped gun (StopWeapon) while we still settle it: lay it flat first
            var settle = rigidbody.GetComponent<WeaponSettle>();
            if (settle != null) settle.FlattenAndLock();
            return true;
        }
        try
        {
            if (_otherNullFix < 0)
            {
                var info = Harmony.GetPatchInfo(AccessTools.Method(typeof(CorpseRagdoll), nameof(CorpseRagdoll.StopRigidbody)));
                Patch other = null;
                if (info != null) foreach (var p in info.Prefixes) if (p.owner != "amos.euphoriastylephysics") { other = p; break; }
                _otherNullFix = other != null ? 1 : 0;
                Log.LogInfo(other != null
                    ? $"[EuphoriaStylePhysics] vanishing corpse fix left to {other.owner} ({other.PatchMethod.DeclaringType?.FullName})"
                    : "[EuphoriaStylePhysics] vanishing corpse fix: ours (no other mod fixes it)");
            }
            if (_otherNullFix == 1) return true;
            Log.LogInfo($"[EuphoriaStylePhysics] corpse of {CorpseName(__instance)}: body part with no physics body skipped at settle (vanishing corpse fix)");
        }
        catch { }
        return false;
    }

    // 0.16.2: which body, how far from the player its root and its ragdoll are, and whether EFT's battle screen lets it open
    private static void LootInteractPrefix(Player __instance, IItemOwner loot)
    {
        try
        {
            if (__instance == null || __instance.IsAI) return;
            Corpse corpse = null;
            foreach (var c in KnownCorpses)
                if (c != null && ReferenceEquals(c.ItemOwner, loot)) { corpse = c; break; }
            if (corpse == null)
            {
                Log.LogInfo($"[EuphoriaStylePhysics] search pressed: {loot?.RootItem?.ShortName} (not a body)");
                return;
            }
            var pelvis = CorpsePelvis?.GetValue(corpse) as Transform;
            Vector3 me = __instance.Position;
            Log.LogInfo($"[EuphoriaStylePhysics] search pressed: body {corpse.name}, root {Vector3.Distance(me, corpse.transform.position):F2} m away, " +
                        $"ragdoll pelvis {(pelvis != null ? Vector3.Distance(me, pelvis.position).ToString("F2") : "?")} m away, " +
                        $"our motion on it {(corpse.GetComponent<DeathMotion>() != null)}, ragdoll {(corpse.Ragdoll != null ? "on" : "none")}");
        }
        catch (Exception e) { Log.LogWarning($"[EuphoriaStylePhysics] search log failed: {e.Message}"); }
    }

    private static readonly System.Reflection.FieldInfo CorpsePelvis = AccessTools.Field(typeof(Corpse), "_pelvis");
    // EFT's screen types live behind Sirenix (not referenced here): reached by reflection. 0.17.5: looked up once (TypeByName scans
    // every loaded assembly; it ran on every loot screen and was most of the inventory spike), not on each opening.
    private static bool _screenLookedUp;
    private static System.Reflection.PropertyInfo _screenInstance;
    private static System.Reflection.MethodInfo _screenCheck;
    private static object[] _battleUiArg;

    private static void LootScreenPrefix(CompoundItem loot)
    {
        try
        {
            if (!_screenLookedUp)
            {
                _screenLookedUp = true;
                var mgr = AccessTools.TypeByName("EFT.UI.Screens.EftScreenManager");
                var kind = AccessTools.TypeByName("EFT.UI.Screens.EEftScreenType");
                _screenInstance = mgr == null ? null : AccessTools.Property(mgr, "Instance");
                _screenCheck = mgr == null ? null : AccessTools.Method(mgr, "CheckCurrentScreen");
                if (kind != null) _battleUiArg = new[] { Enum.Parse(kind, "BattleUI") };
            }
            string battle = "?";
            var inst = _screenInstance?.GetValue(null);
            if (inst != null && _screenCheck != null && _battleUiArg != null)
                battle = _screenCheck.Invoke(inst, _battleUiArg).ToString();
            Log.LogInfo($"[EuphoriaStylePhysics] search: opening the loot screen for {loot?.ShortName} (battle screen current: {battle})");
        }
        catch (Exception e) { Log.LogWarning($"[EuphoriaStylePhysics] search screen log failed: {e.Message}"); }
    }

    // 0.16.0: the AI does not turn a bot that is blending from its ragdoll into EFT's prone
    private static bool SteeringPrefix(BotSteering __instance) => !Knockdown.HoldsStill(__instance?._player);

    private static void TriggerPrefix(Player.FirearmController __instance, ref bool pressed)
    {
        if (pressed && __instance != null && Knockdown.BlocksFire(__instance._player)) pressed = false;
    }

    private static float _simTime, _realTime;
    private static int _catchUps;

    private static void SimulatePrefix(float step)
    {
        if (Physics.simulationMode != SimulationMode.Script) return;
        _simTime += step;
        DeathMotion.StepAll(step);
    }

    // 0.15.2 (user: they fall in slow motion sometimes): EFT's smoothed stepping caps a frame's physics at 0.03 s, so at low
    // FPS physics runs behind real time. While our bodies move, step the rest of the way once per frame.
    private void LateUpdate()
    {
        if (!IsOn || !RealTimePhysics.Value || Physics.simulationMode != SimulationMode.Script ||
            DeathMotion.ActiveCount + Knockdown.Count == 0)
        {
            _simTime = _realTime = 0f;
            return;
        }
        _realTime += Time.deltaTime;
        float lag = _realTime - _simTime;
        if (lag > 0.1f) { _simTime = _realTime - 0.1f; lag = 0.1f; }   // never try to catch up more than this
        // 2026-11-03 (issues with Smoothie only): Smoothie's "Run physics at most 60 times a second" skips frames and pays the
        // time back in one step; at 0.004 s we stepped the skipped time ourselves and it was then simulated twice (bodies faster
        // than real time, extra map-wide physics). Only a real shortfall, more than a 60 fps frame's worth, is stepped here.
        if (lag < 0.02f || !PhysicsExtensions.Simulation.CanRunSimulate) return;
        float step = Mathf.Min(lag, 0.03f);
        Physics.Simulate(step);   // our prefix steps the muscles and counts it
        if (++_catchUps == 1 || _catchUps % 500 == 0)
            Log.LogInfo($"[EuphoriaStylePhysics] physics behind real time by {lag * 1000f:F0} ms: extra step {step * 1000f:F0} ms ({_catchUps} so far, FPS {1f / Mathf.Max(0.001f, Time.deltaTime):F0})");
    }

    private static bool TryPutToSleepPrefix(PlayerRigidbodySleepHierarchy __instance, ref bool __result)
    {
        // A dying body standing still for a moment must not be put to sleep (EFT then freezes the whole corpse as it is).
        var rb = __instance?.RigidbodySpawner != null ? __instance.RigidbodySpawner.Rigidbody : null;
        if (rb != null && DeathMotion.IsDriven(rb))
        {
            __result = false;
            return false;
        }
        if (rb != null && CorpseWake.KeepsLive(rb))   // 0.17.5: a corpse kept physical is not force-slept every frame
        {
            __instance.MustBeSleeping = false;
            __result = false;
            return false;
        }
        return true;
    }

    private static void ShotReactionsPostfix(Player __instance, DamageInfo shot, EBodyPart bodyPart)
    {
        if (!IsOn) return;
        DeathMotion.RecordHit(__instance, shot, bodyPart);
        if (Knockdown.IsActive(__instance))
            Log.LogInfo($"[EuphoriaStylePhysics] hit while down: {__instance.Profile?.Nickname} {bodyPart} dmg {shot.Damage:F0} "
                        + $"({(shot.HitCollider != null ? LayerMask.LayerToName(shot.HitCollider.gameObject.layer) : "no collider")}, {Knockdown.PhaseOf(__instance)})");
        try { Knockdown.OnHit(__instance, shot, bodyPart); }
        catch (Exception e) { Log.LogError($"[EuphoriaStylePhysics] knockdown check failed: {e}"); }
        if (!FlinchEnabled.Value) return;
        try
        {
            Flinch.OnHit(__instance, shot, bodyPart);
        }
        catch (Exception e)
        {
            Flinch.Fail(e);
        }
    }
}

// 0.20.0: read by BepInEx's Configuration Manager (F12) by name: main switches first in their section, a display name for the copies
internal sealed class ConfigurationManagerAttributes
{
    public int? Order;
    public string DispName;
}
