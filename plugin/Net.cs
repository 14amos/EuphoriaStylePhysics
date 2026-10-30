using System;
using System.Runtime.CompilerServices;
using EFT;
using UnityEngine;

[assembly: InternalsVisibleTo("EuphoriaStylePhysics.Fika")]

namespace EuphoriaStylePhysics;

// Fika sync calls. The Fika code is in EuphoriaStylePhysics.Fika.dll (FikaSync), loaded only when Fika is installed; it fills
// these delegates in its Init. This DLL names no Fika type, so Assembly.GetTypes() works without Fika (0.17.1).
internal static class Net
{
    internal static bool Fika;
    internal static Func<bool> ServerCheck;
    internal static Action<Player, Vector3, Vector3, float, EBodyPart, bool, float, Vector3?> SendKnock;
    internal static Action<Player> SendGetUp;

    internal static bool IsServer()
    {
        if (!Fika || ServerCheck == null) return true;   // no Fika: this machine runs everything
        try { return ServerCheck(); }
        catch { return true; }
    }

    internal static void Knock(Player p, Vector3 hitPoint, Vector3 dir, float damage, EBodyPart part, bool blast, float sitFor, Vector3? shooter)
    {
        if (!Fika || SendKnock == null) return;
        try { SendKnock(p, hitPoint, dir, damage, part, blast, sitFor, shooter); }
        catch (Exception e) { Plugin.Log.LogWarning($"[EuphoriaStylePhysics] sync: knockdown not sent: {e.Message}"); }
    }

    internal static void GetUp(Player p)
    {
        if (!Fika || SendGetUp == null) return;
        try { SendGetUp(p); }
        catch (Exception e) { Plugin.Log.LogWarning($"[EuphoriaStylePhysics] sync: get-up not sent: {e.Message}"); }
    }
}
