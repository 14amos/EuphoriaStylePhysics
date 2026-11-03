using System;
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
[BepInPlugin("amos.euphoriastylephysics", "Euphoria Style Physics", "0.17.5")]
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
    internal static ConfigEntry<float> Consistency;
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
    internal static ConfigEntry<bool> ExplosionsVanilla;
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
    internal static ConfigEntry<bool> CorpseWakeOn;
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

    private void Awake()
    {
        Log = Logger;
        // 0.9.0 (user: every main feature toggleable with ease): the main switches all sit in "1. General", first in F12
        const string g = "1. General", d = "2. Dying", f = "3. Living hits", k = "4. Knockdowns";
        Enabled = Config.Bind(g, "Enabled", true, "Master switch.");
        VerboseLog = Config.Bind(g, "Verbose log", false, "More detail in the BepInEx log for every part of the mod: each flinch, corpse, knockdown and skipped death (deaths are always logged).");
        Preset = Config.Bind(g, "Style preset", StylePreset.Euphoric,
            "Euphoric: sets every value below for Euphoria-style bodies (consistent stagger, hand to the wound, knees give, "
            + "headshots drop at once, survivors flinch and keep fighting). The experimental gameplay part (knockdowns) stays off; "
            + "turn it on yourself. Applied once when picked; change anything afterwards as you like. "
            + "Custom: nothing is set for you.");
        PresetApplied = Config.Bind(g, "Style preset applied", false, "Set by the mod once the preset's values have been written.");
        PresetRevision = Config.Bind(g, "Style preset revision", 0, "Set by the mod: which revision of the preset's values was written (a newer one is written once).");
        Consistency = Config.Bind(g, "Consistency", 0.5f,
            new ConfigDescription("How alike reactions are: 0 = every random range used fully, 1 = the same hit gives the same reaction.",
                new AcceptableValueRange<float>(0f, 1f)));
        RealTimePhysics = Config.Bind(g, "Real-time ragdolls at low FPS", true,
            "EFT advances physics at most 0.03 s per frame, so below ~33 FPS every ragdoll falls in slow motion. While our bodies "
            + "move, one extra physics step per frame catches up with real time (costs a little CPU at low FPS).");
        ToggleKey = Config.Bind(g, "Toggle key", new KeyboardShortcut(KeyCode.E, KeyCode.LeftControl, KeyCode.LeftShift),
            "Turns the whole mod on or off in game (same as Enabled).");

        DeathEnabled = Config.Bind(g, "Dying with muscles", true,
            "MAIN SWITCH (visual). A body killed on its feet keeps its muscles for a moment: stumbles, hand on the wound, knees give, "
            + "lies hurt. Off: EFT's own ragdoll. Details in 2. Dying.");
        FlinchEnabled = Config.Bind(g, "Hit flinch", true,
            "MAIN SWITCH (visual). Bots and other players hit by a round flinch on top of EFT's own hit reaction: a short snap (about "
            + "a quarter second) of the spine, head, a knee or an arm. Never your own body. Details in 3. Living hits.");
        BlastFlinch = Config.Bind(g, "Explosion flinch", true,
            "MAIN SWITCH (visual). Bots and other players near an explosion they survive recoil away from it. Strength: Flinch strength.");
        // 0.17.3 (user: ragdolls keep moving when you are near)
        CorpseWakeOn = Config.Bind(g, "Corpses stay physical near you", true,
            "MAIN SWITCH (visual). EFT freezes a corpse once it lies still; near you it stays a ragdoll (or becomes one again), so "
            + "rounds, blasts and other bodies still move it. Walk away and EFT freezes it again. Only on this PC (not synced). "
            + "Details in 2. Dying.");
        KnockdownEnabled = Config.Bind(g, "Knockdowns", false,
            "MAIN SWITCH, EXPERIMENTAL (gameplay: the bot cannot shoot while down). A bot hit hard goes down on its own ragdoll and gets back up. "
            + "Only where the bots' AI runs. Details in 4. Knockdowns.");
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
        ExplosionsVanilla = Config.Bind(d, "Explosions use EFT ragdoll", false,
            "On: grenades, mines, artillery and the like kill with EFT's own ragdoll. Off: the blast throw below.");
        BlastThrow = Config.Bind(d, "Blast throw", true,
            "A blast throws the body up and away (harder the closer it was): arms up to guard the head, legs tucked, a hard landing. "
            + "Kills go limp after it; bots knocked down by a blast (4. Knockdowns) lie hurt and get up.");
        // 0.17.0 (user: works with RealTargets?): its "Blast injuries" throws grenade kills within 6 m too
        CorpseWakeDistance = Config.Bind(d, "Corpses stay physical within (m)", 12f,
            new ConfigDescription("How close a corpse has to be to you to stay or become a ragdoll again.", new AcceptableValueRange<float>(2f, 50f)));
        CorpseWakeMax = Config.Bind(d, "Most physical corpses at once", 10,
            new ConfigDescription("Cap on corpses kept or made physical near you (performance).", new AcceptableValueRange<int>(1, 40)));
        // 0.17.4 (user clip: rolling down stairs jitters, then the body cannot be searched from above)
        SmoothCollisions = Config.Bind(d, "Smooth body collisions", true,
            "Bodies use continuous (speculative) collisions, more solver steps and a soft push-out from geometry, so they roll down "
            + "stairs instead of sinking into the steps and popping out. Costs a little CPU while bodies move.");
        SearchThroughFloor = Config.Bind(d, "Search bodies lying on stairs", true,
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
            h.Patch(AccessTools.Method(typeof(CorpseRagdoll), nameof(CorpseRagdoll.StopRigidbody)),
                prefix: new HarmonyMethod(typeof(Plugin), nameof(StopRigidbodyPrefix)));
            // Fika's ObservedPlayer (bots and players on a client) may override ShotReactions without calling it; patch the
            // override too when it exists (a patch on the base method does not cover an override).
            var observed = AccessTools.TypeByName("Fika.Core.Main.Players.ObservedPlayer");
            var obsMethod = observed == null ? null : AccessTools.DeclaredMethod(observed, nameof(Player.ShotReactions));
            if (obsMethod != null)
                h.Patch(obsMethod, postfix: new HarmonyMethod(typeof(Plugin), nameof(ShotReactionsPostfix)));
            Log.LogInfo($"[EuphoriaStylePhysics] 0.17.5 loaded; toggle key {ToggleKey.Value}; patches on: Corpse.TryToCreateRagdoll, sleep hierarchy, Physics.Simulate, BodyPartCollider.ApplyHit, Player.BodyUpdate/MovementUpdate, FirearmController.SetTriggerPressed, BotSteering.ManualFixedUpdate, ExplosionSharedMethods.Explosion, Player.ShotReactions, CorpseRagdoll.StopRigidbody, loot search log" +
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
        // EFT's own explosion event (position, the explosive's max explosion distance), raised for grenades, mines and shells
        ExplosionSharedMethods.OnExplosion += OnExplosion;
    }

    // the Euphoric preset (picked by Claude 2026-10-28; renamed 0.17.0).
    // Revision 4 (0.17.0 test release, user: buggy and gameplay parts off, bodies launched too far): knockdowns off; stumble,
    // dying time and blast throw turned down (0.17.5: the stagger settings are gone). Written once over older revisions.
    private static void ApplyEuphoric(string why)
    {
        Consistency.Value = 0.85f;
        // dying: a short consistent stagger, hand to the wound, knees give; headshots drop at once; writhing, little twitching
        DeathEnabled.Value = true;
        DropDeadChance.Value = 0.1f;
        HeadshotInstantDrop.Value = true;
        DyingTimeMin.Value = 1.0f;
        DyingTimeMax.Value = 2.0f;
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
        BurstKeepsUp.Value = true;
        LookAtShooter.Value = true;
        ReachToShooter.Value = true;
        ExplosionsVanilla.Value = false;
        BlastThrow.Value = true;
        BlastThrowScale.Value = 0.6f;
        // living: every hit flinches and they keep fighting; heavy hits, leg hits and blasts put them down (knockdowns, off)
        FlinchEnabled.Value = true;
        FlinchScale.Value = 0.8f;
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
        Log.LogInfo($"[EuphoriaStylePhysics] Euphoric preset applied ({why}, revision 4): consistency 0.85, dying 1.0-2.0 s, stumble 0.5, "
                    + "headshots drop at once, blast throw 0.6; knockdowns OFF");
    }

    private void Update()
    {
        Flinch.KeepInstalled();
        Knockdown.Tick();
        CorpseWake.Tick();
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
    private static bool StopRigidbodyPrefix(CorpseRagdoll __instance, Rigidbody rigidbody)
    {
        if (rigidbody != null) return true;
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
        if (lag < 0.004f || !PhysicsExtensions.Simulation.CanRunSimulate) return;
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
