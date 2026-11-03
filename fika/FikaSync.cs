using Comfort.Common;
using EFT;
using Fika.Core.Main.Components;
using Fika.Core.Main.Players;
using Fika.Core.Main.Utils;
using Fika.Core.Modding;
using Fika.Core.Modding.Events;
using Fika.Core.Networking;
using Fika.Core.Networking.LiteNetLib;
using Fika.Core.Networking.LiteNetLib.Utils;
using UnityEngine;

namespace EuphoriaStylePhysics;

// 0.15.0 Fika sync (user: "build the sync"; raids run on a Fika headless host, where the bots' AI lives).
// The host decides knockdowns (Knockdown.cs) and tells every client which bot went down, how, and when it gets up; the
// clients play the same knockdown on their copy of that bot (visual only, the host moves it). The same EuphoriaStylePhysics.dll must be on every machine
// (Fika matches packets by type). 0.17.1: this code lives in its own EuphoriaStylePhysics.Fika.dll, loaded by Plugin only when
// Fika is installed: a Fika type inside the main DLL made Assembly.GetTypes() throw without Fika, which broke every mod that
// scans all assemblies (WTT-ClientCommonLib stopped registering its item parents -> "No C# type for taxonomy node").
internal sealed class KnockPacket : INetSerializable
{
    public int NetId;
    public Vector3 HitPoint, Direction, Shooter;
    public float Damage, SitFor;
    public byte Part;
    public bool Blast, HasShooter;

    public void Serialize(NetDataWriter w)
    {
        w.Put(NetId);
        Put(w, HitPoint); Put(w, Direction); Put(w, Shooter);
        w.Put(Damage); w.Put(SitFor); w.Put(Part); w.Put(Blast); w.Put(HasShooter);
    }

    public void Deserialize(NetDataReader r)
    {
        NetId = r.GetInt();
        HitPoint = Get(r); Direction = Get(r); Shooter = Get(r);
        Damage = r.GetFloat(); SitFor = r.GetFloat(); Part = r.GetByte(); Blast = r.GetBool(); HasShooter = r.GetBool();
    }

    internal static void Put(NetDataWriter w, Vector3 v) { w.Put(v.x); w.Put(v.y); w.Put(v.z); }
    internal static Vector3 Get(NetDataReader r) => new Vector3(r.GetFloat(), r.GetFloat(), r.GetFloat());
}

internal sealed class GetUpPacket : INetSerializable
{
    public int NetId;
    public void Serialize(NetDataWriter w) => w.Put(NetId);
    public void Deserialize(NetDataReader r) => NetId = r.GetInt();
}

internal static class FikaSync
{
    private static int _sent, _received;

    // called by Plugin through reflection
    internal static void Init()
    {
        Net.ServerCheck = IsServer;
        Net.SendKnock = SendKnock;
        Net.SendGetUp = SendGetUp;
        FikaEventDispatcher.SubscribeEvent<FikaNetworkManagerCreatedEvent>(e => Register(e.Manager));
        Plugin.Log.LogInfo("[EuphoriaStylePhysics] Fika sync on (knockdowns sent from the host to every client)");
    }

    private static void Register(IFikaNetworkManager m)
    {
        m.RegisterPacket<KnockPacket>(OnKnock);
        m.RegisterPacket<GetUpPacket>(OnGetUp);
        Plugin.Log.LogInfo($"[EuphoriaStylePhysics] Fika packets registered (server={FikaBackendUtils.IsServer}, headless={FikaBackendUtils.IsHeadless})");
    }

    private static IFikaNetworkManager Manager => Singleton<IFikaNetworkManager>.Instantiated ? Singleton<IFikaNetworkManager>.Instance : null;

    internal static bool IsServer() => FikaBackendUtils.IsServer;

    internal static void SendKnock(Player p, Vector3 hitPoint, Vector3 dir, float damage, EBodyPart part, bool blast, float sitFor, Vector3? shooter)
    {
        if (!FikaBackendUtils.IsServer || Manager == null || !(p is FikaPlayer fp)) return;
        var pk = new KnockPacket
        {
            NetId = fp.NetId, HitPoint = hitPoint, Direction = dir, Damage = damage, Part = (byte)part, Blast = blast, SitFor = sitFor,
            HasShooter = shooter.HasValue, Shooter = shooter ?? Vector3.zero
        };
        Manager.SendData(ref pk, DeliveryMethod.ReliableOrdered);
        if (++_sent <= 5 || Plugin.VerboseLog.Value)
            Plugin.Log.LogInfo($"[EuphoriaStylePhysics] sync: sent knockdown of {p.Profile?.Nickname} (net {fp.NetId}){(sitFor > 0f ? $", sits {sitFor:F0} s" : "")}");
    }

    internal static void SendGetUp(Player p)
    {
        if (!FikaBackendUtils.IsServer || Manager == null || !(p is FikaPlayer fp)) return;
        var pk = new GetUpPacket { NetId = fp.NetId };
        Manager.SendData(ref pk, DeliveryMethod.ReliableOrdered);
    }

    private static Player Find(int netId)
    {
        if (!CoopHandler.TryGetCoopHandler(out var ch) || ch.Players == null) return null;
        return ch.Players.TryGetValue(netId, out var fp) ? fp : null;
    }

    private static void OnKnock(KnockPacket pk)
    {
        if (FikaBackendUtils.IsServer) return;
        var p = Find(pk.NetId);
        _received++;
        if (p == null)
        {
            Plugin.Log.LogWarning($"[EuphoriaStylePhysics] sync: knockdown for net {pk.NetId}, no such player here");
            return;
        }
        if (_received <= 5 || Plugin.VerboseLog.Value)
            Plugin.Log.LogInfo($"[EuphoriaStylePhysics] sync: host knocked down {p.Profile?.Nickname} (net {pk.NetId})");
        Knockdown.StartRemote(p, pk.HitPoint, pk.Direction, pk.Damage, (EBodyPart)pk.Part, pk.Blast, pk.SitFor,
            pk.HasShooter ? pk.Shooter : (Vector3?)null);
    }

    private static void OnGetUp(GetUpPacket pk)
    {
        if (FikaBackendUtils.IsServer) return;
        var p = Find(pk.NetId);
        if (p != null) Knockdown.RemoteGetUp(p);
    }
}
