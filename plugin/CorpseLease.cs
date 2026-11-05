using System.Collections.Generic;
using EFT.Interactive;

namespace EuphoriaStylePhysics
{
    // 2026-11-04 (KMA - Keep Me Alive's author, via the user: dragging a body fought "Corpses stay physical near you" and the dying motion):
    // another mod can take a corpse for a while. While it is leased we leave it alone: no dying motion on it, CorpseWake does not
    // wake, freeze or rebuild it, the walk push skips it, and EFT's own "is it still" answer stands. Main thread, local only.
    internal static class CorpseLeases
    {
        private static readonly Dictionary<Corpse, string> Holders = new Dictionary<Corpse, string>();
        private static readonly List<Corpse> Dead = new List<Corpse>();

        internal static bool IsLeased(Corpse corpse) => corpse != null && Holders.Count > 0 && Holders.ContainsKey(corpse);

        internal static bool TryAcquire(Corpse corpse, string owner)
        {
            if (corpse == null || string.IsNullOrEmpty(owner)) return false;
            if (Holders.TryGetValue(corpse, out var by)) return by == owner;   // already yours: fine; someone else's: no
            var dying = corpse.GetComponent<DeathMotion>();
            if (!CorpseWake.Knows(corpse) && dying == null) return false;     // not a corpse of ours
            Prune();
            Holders[corpse] = owner;
            if (dying != null) dying.Abort();                                 // the dying motion lets go at once
            CorpseWake.Forget(corpse);                                        // not woken by us any more: never frozen by us
            if (Plugin.VerboseLog.Value) Plugin.Log.LogInfo($"[EuphoriaStylePhysics] corpse {corpse.name} leased by {owner}{(dying != null ? " (dying motion stopped)" : "")}");
            return true;
        }

        internal static void Release(Corpse corpse, string owner)
        {
            if (corpse == null || !Holders.TryGetValue(corpse, out var by) || by != owner) return;
            Holders.Remove(corpse);
            CorpseWake.Forget(corpse);   // back as it is: not woken, decided again next tick
            if (Plugin.VerboseLog.Value) Plugin.Log.LogInfo($"[EuphoriaStylePhysics] corpse {corpse.name} released by {owner}");
        }

        private static void Prune()
        {
            if (Holders.Count == 0) return;   // every new lease drops those of bodies gone since (earlier raids too)
            Dead.Clear();
            foreach (var c in Holders.Keys) if (c == null) Dead.Add(c);
            foreach (var c in Dead) Holders.Remove(c);
        }
    }
}

namespace EuphoriaStylePhysics.Api
{
    /// <summary>
    /// Lets another mod take a corpse from Euphoria Style Physics for a while (dragging, carrying, inspecting a body).
    /// While a corpse is leased, Euphoria Style Physics leaves it completely alone: its dying motion on it is stopped, "Corpses stay
    /// physical near you" does not wake, freeze or rebuild it, walking into it does not push it, and EFT's own corpse-still check
    /// is used unchanged. Main thread only, local only (nothing is sent over the network).
    /// </summary>
    public static class CorpseLeaseV1
    {
        public const int Version = 1;

        /// <summary>Take the corpse. False if Euphoria Style Physics does not know it, or another owner holds it. Calling it
        /// again with the same owner returns true.</summary>
        public static bool TryAcquire(Corpse corpse, string owner) => CorpseLeases.TryAcquire(corpse, owner);

        /// <summary>Give it back as it is (its rigidbodies may be dynamic, kinematic or removed): it is treated as not woken by
        /// us and decided again on our next tick. Ignored if this owner does not hold it.</summary>
        public static void Release(Corpse corpse, string owner) => CorpseLeases.Release(corpse, owner);

        /// <summary>Whether any mod holds this corpse.</summary>
        public static bool IsLeased(Corpse corpse) => CorpseLeases.IsLeased(corpse);
    }
}
