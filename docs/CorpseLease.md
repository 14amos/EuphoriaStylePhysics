# CorpseLeaseV1: taking a corpse from Euphoria Style Physics

Since Euphoria Style Physics 0.21.1. One public static class in `EuphoriaStylePhysics.dll`:

```csharp
namespace EuphoriaStylePhysics.Api
{
    public static class CorpseLeaseV1
    {
        public const int Version = 1;
        public static bool TryAcquire(Corpse corpse, string owner);
        public static void Release(Corpse corpse, string owner);
        public static bool IsLeased(Corpse corpse);
    }
}
```

`Corpse` is `EFT.Interactive.Corpse`. Main thread only, local only: nothing is sent over the network.

## What a lease does

While a corpse is leased, Euphoria Style Physics leaves it alone:

- its dying motion on that body stops at once (and a body that dies already leased never gets one);
- "Corpses stay physical near you" does not wake, freeze or rebuild it, and does not freeze it before EFT takes it apart;
- walking into it does not push it;
- the corpse "is it still" check returns EFT's own answer, and EFT's sleep handling is not overridden.

## Calls

- `TryAcquire(corpse, owner)`: true when you hold it. False when Euphoria Style Physics does not know the corpse (it never
  handled it) or another owner holds it. Calling it again with the same owner returns true.
- `Release(corpse, owner)`: gives it back as it is. Its rigidbodies may be dynamic, kinematic or removed; Euphoria Style
  Physics treats it as not woken by us and decides again on its next tick. A release by an owner that does not hold it is
  ignored.
- `IsLeased(corpse)`: whether any mod holds it.

Acquire before you touch the ragdoll (on pickup), release when you are done (when the body is set down), every time.

## Soft dependency (no hard reference)

```csharp
var t = AccessTools.TypeByName("EuphoriaStylePhysics.Api.CorpseLeaseV1");
var acquire = t == null ? null : AccessTools.Method(t, "TryAcquire");
var release = t == null ? null : AccessTools.Method(t, "Release");
// acquire?.Invoke(null, new object[] { corpse, "com.you.yourmod" });
```
