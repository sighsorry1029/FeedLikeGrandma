using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;

// Production network source is linked unchanged. Fake RPC queues expose duplicate,
// delayed and reordered messages; they do not execute Unity, real ZDO replication,
// ValheimCuisine, world/character saves, or actual inventory delivery.
internal static class CuisineNetworkChecks
{
    private static int Main()
    {
        try { FeedLikeGrandma.FeedCuisineCompatibility.RunNetworkChecks(); return 0; }
        catch (Exception exception) { Console.Error.WriteLine(exception); return 1; }
    }
}

namespace FeedLikeGrandma
{
    internal static partial class FeedCuisineCompatibility
    {
        private static int _passed;
        internal static bool Ready => true;
        internal static float CooldownSeconds => 60f;
        internal static int MilkAmount => 5;
        internal static bool RequireBreeding => Fixture.RequireBreeding;
        internal static bool IsLox(Character character) => character.name.StartsWith("Lox", StringComparison.Ordinal);
        internal static bool HasBreedingEligibility(ZDO zdo) => zdo.Bred;
        internal static long ReadCooldownUntil(ZDO zdo) => Fixture.HoldReplication && Fixture.Peer == 1 ? zdo.ServerUntil : zdo.Until;
        internal static string ReadLastMilkRequest(ZDO zdo) => Fixture.HoldReplication && Fixture.Peer == 1 ? zdo.ServerRequest : zdo.Request;
        internal static void WriteMilkCooldown(ZDO zdo, long until, string id) { zdo.Until = until; zdo.Request = id; Fixture.Writes++; }
        internal static long WorldNowTicks() => Fixture.Now;
        internal static void EnsureMilkState(Character character, ZNetView view)
        {
            if (!view.IsOwner()) throw new InvalidOperationException("milk state requires target owner");
            if (view.GetZDO().Until == long.MaxValue) throw new InvalidOperationException("corrupt milk state");
        }
        internal static void GrantMilk(Player player, int amount, Vector3 position)
        {
            Fixture.Grants++;
            Fixture.Amount += amount;
            if (Fixture.ThrowOnGrant) throw new InvalidOperationException("uncertain inventory delivery");
        }
        internal static void ShowMessage(Player player, string message) => Fixture.Messages.Add(message);

        internal static void RunNetworkChecks()
        {
            CheckHappyAndReplay();
            CheckDelayedProof();
            CheckTimeoutAndReconnect();
            CheckCaptureAndConcurrentLocks();
            CheckValidation();
            CheckOwnerMigration();
            CheckMissingStateAndPartialDelivery();
            Console.WriteLine("Passed " + _passed + " source-linked Cuisine network checks.");
            Console.WriteLine("Scope: fake transport/state only; Unity, real dedicated-server replication, Cuisine hooks and independent saves remain unverified.");
        }

        private static void CheckHappyAndReplay()
        {
            NewFixture();
            Begin();
            Fixture.PumpAll();
            Require(Fixture.Grants == 1 && Fixture.Amount == 5 && Fixture.Writes == 1, "dedicated server without live Characters approves exactly one owner-backed grant");
            Require(_pendingMilk == null && MilkLocks.Count == 0, "successful request consumes client pending and releases target lock");
            Packet request = Fixture.History.First(packet => packet.Name == MilkRequestServerRpc);
            Packet apply = Fixture.History.First(packet => packet.Name == MilkApplyOwnerRpc);
            Packet result = Fixture.History.First(packet => packet.Name == MilkResultClientRpc);
            Fixture.Deliver(request); Fixture.Deliver(apply); Fixture.Deliver(result); Fixture.PumpAll();
            Require(Fixture.Writes == 1 && Fixture.Grants == 1, "request/apply/result duplicates neither extend cooldown nor grant twice");
            Fixture.Now += 120L * TimeSpan.TicksPerSecond;
            Fixture.Deliver(request); Fixture.PumpAll();
            Require(Fixture.Grants == 1 && Fixture.Writes == 1, "completed request replay after cooldown expiration cannot milk again");
            Begin(); Fixture.PumpAll();
            Require(Fixture.Grants == 2 && Fixture.Writes == 2, "fresh request after cooldown expiration can milk normally");
            long newerUntil = Fixture.Target.Until;
            Fixture.Deliver(apply); Fixture.Deliver(request); Fixture.PumpAll();
            Require(Fixture.Writes == 2 && Fixture.Target.Until == newerUntil && Fixture.Grants == 2,
                "older apply and sequence cannot overwrite newer cooldown or grant");
        }

        private static void CheckDelayedProof()
        {
            NewFixture(); Fixture.HoldReplication = true;
            Begin(); Fixture.PumpAll();
            Require(Fixture.Writes == 1 && Fixture.Grants == 0 && MilkLocks.Count == 1, "owner ACK alone cannot grant before server ZDO proof");
            Fixture.Peer = 1;
            Require(IsMilkBusyOnServer(Fixture.Target.m_uid), "ambiguous owner write blocks capture");
            Fixture.HoldReplication = false;
            Require(!IsMilkBusyOnServer(Fixture.Target.m_uid), "observed persisted state releases capture lock");
            Fixture.PumpAll();
            Require(Fixture.Grants == 1, "delayed confirmed ZDO causes a single pending grant");
            Require(Fixture.ForceSends == 1, "owner final write requests ZDO synchronization once");
        }

        private static void CheckTimeoutAndReconnect()
        {
            NewFixture(); Fixture.HoldReplication = true;
            Begin(); Fixture.PumpAll(); Fixture.Advance(31f);
            Require(_pendingMilk == null && MilkLocks.Count == 1 && Fixture.Grants == 0, "client timeout clears pending without unlocking uncertain owner write");
            Fixture.HoldReplication = false; Fixture.Peer = 1;
            IsMilkBusyOnServer(Fixture.Target.m_uid); Fixture.PumpAll();
            Require(Fixture.Grants == 0 && MilkLocks.Count == 0, "late proof unlocks target but timed-out request never grants");

            NewFixture(); Begin();
            Fixture.PumpUntil(MilkResultClientRpc);
            Packet oldResult = Fixture.Queue.Dequeue();
            Packet oldRequest = Fixture.History.First(packet => packet.Name == MilkRequestServerRpc);
            // Simulate requester reconnect while the server keeps its request cache and world state.
            _pendingMilk = null; _milkCookie = string.Empty; _milkClientNonce = Guid.NewGuid().ToString("N"); _milkSequence = 0;
            Fixture.Now += 120L * TimeSpan.TicksPerSecond;
            Begin(); Fixture.PumpAll();
            Require(Fixture.Grants == 1, "new connection can obtain a fresh session and milk after cooldown");
            Fixture.Deliver(oldResult); Fixture.Deliver(oldRequest); Fixture.PumpAll();
            Require(Fixture.Grants == 1 && Fixture.Writes == 2, "old-cookie result/request replay is ignored after reconnect");
        }

        private static void CheckCaptureAndConcurrentLocks()
        {
            NewFixture(); Fixture.CaptureBlocked = true; Begin(); Fixture.PumpAll();
            Require(Fixture.Grants == 0 && Fixture.Writes == 0 && Fixture.Messages.Contains("$flg_cuisine_milk_busy"), "capture reservation prevents milking before owner mutation");
            NewFixture(); Fixture.HoldReplication = true; Begin(); Fixture.PumpAll();
            int writes = Fixture.Writes;
            _pendingMilk = null; // Simulate a second request while the first remains ambiguous on the server.
            Begin(); Fixture.PumpAll();
            Require(Fixture.Writes == writes && Fixture.Grants == 0 && Fixture.Messages.Contains("$flg_cuisine_milk_busy"), "same-target concurrent request cannot dispatch a second owner write");
            Fixture.Peer = 1;
            string nonce = Guid.NewGuid().ToString("N");
            ZPackage hello = new(); hello.Write(new ZDOID(30)); hello.Write(nonce);
            ReceiveMilkHelloOnServer(3, new ZPackage(hello.Bytes()));
            ZPackage otherPlayer = new(); otherPlayer.Write(new ZDOID(30)); otherPlayer.Write(nonce);
            otherPlayer.Write(MilkSessions[3].Cookie); otherPlayer.Write(1L); otherPlayer.Write(Guid.NewGuid().ToString("N")); otherPlayer.Write(Fixture.Target.m_uid);
            ReceiveMilkRequestOnServer(3, new ZPackage(otherPlayer.Bytes())); Fixture.PumpAll();
            Require(MilkSessions[3].LastRequest!.Complete && !MilkSessions[3].LastRequest!.Success && Fixture.Writes == writes,
                "different authenticated player cannot milk the same target during pending owner proof");
            Fixture.Peer = 1;
            Packet unauthorized = Fixture.History.First(packet => packet.Name == MilkResultServerRpc).WithSender(99);
            Fixture.Deliver(unauthorized);
            Require(MilkLocks.Count == 1, "unrelated sender cannot acknowledge another owner's transaction");
        }

        private static void CheckValidation()
        {
            NewFixture(); Fixture.Player2.Dead = true; Begin(); Fixture.PumpAll();
            Require(Fixture.Queue.Count == 0 && Fixture.Grants == 0 && _pendingMilk == null, "dead local requester cannot initiate milking");
            NewFixture(); Fixture.Player2.RightItem = null; Begin(); Fixture.PumpAll();
            Require(Fixture.Writes == 0 && _pendingMilk == null, "local bucket requirement is preserved");
            NewFixture(); Fixture.PlayerZdo.RightItem = 0; Begin(); Fixture.PumpAll();
            Require(Fixture.Writes == 0 && Fixture.Grants == 0, "server rejects unsynchronized or absent right-hand bucket");
            NewFixture(); Fixture.Target.Tamed = false; Begin(); Fixture.PumpAll();
            Require(Fixture.Writes == 0, "server rejects untamed target ZDO");
            NewFixture(); Fixture.Target.Health = 0f; Begin(); Fixture.PumpAll();
            Require(Fixture.Writes == 0, "server rejects dead target from ZDO health without a live Character");
            NewFixture(); Fixture.PlayerZdo.Owner = 3; Begin(); Fixture.PumpAll();
            Require(Fixture.Writes == 0 && MilkSessions.Count == 0, "peer cannot authorize a player ZDO it does not own");
            NewFixture(); Fixture.RequireBreeding = true; Fixture.Target.Bred = false; Begin(); Fixture.PumpAll();
            Require(Fixture.Writes == 0 && Fixture.Messages.Contains("$flg_cuisine_milk_breeding"), "server breeding policy is checked before owner apply");
            NewFixture(); Fixture.Target.Until = Fixture.Now + TimeSpan.TicksPerMinute; Begin(); Fixture.PumpAll();
            Require(Fixture.Writes == 0 && Fixture.Messages.Contains("$flg_cuisine_milk_cooldown"), "existing cooldown cannot be reset by another request");
            NewFixture(); Fixture.PlayerZdo.Position = new Vector3(10f, 0f, 0f); Begin(); Fixture.PumpAll();
            Require(Fixture.Writes == 0 && Fixture.Messages.Contains("$flg_cuisine_milk_too_far"), "server rejects distant requester ZDO even with local nearby transform");
            NewFixture(); Begin(); Fixture.PumpUntil(MilkApplyOwnerRpc);
            Packet legitimate = Fixture.Queue.Peek(); Fixture.Deliver(legitimate.WithSender(99));
            Require(Fixture.Writes == 0, "owner rejects mutation RPC from non-server sender");
            Fixture.Player2.transform.position = new Vector3(3.1f, 0f, 0f); Fixture.PumpAll();
            Require(Fixture.Writes == 0 && MilkLocks.Count == 0, "owner keeps original three-meter limit and explicit refusal unlocks unchanged owner");
        }

        private static void CheckOwnerMigration()
        {
            NewFixture(); Begin(); Fixture.PumpUntil(MilkApplyOwnerRpc);
            Fixture.Target.Owner = 4; Fixture.PumpAll();
            Require(Fixture.Writes == 0 && MilkLocks.Count == 1, "old owner's refusal after ownership transfer cannot unlock ambiguous target");
            Fixture.Advance(31f);
            Require(MilkLocks.Count == 1 && Fixture.Grants == 0, "owner-transfer ambiguity is not cleared by timeout");
            NewFixture(); Fixture.HoldReplication = true; Begin(); Fixture.PumpAll();
            Fixture.Target.Owner = 4; Fixture.HoldReplication = false; Fixture.Peer = 1;
            Require(!IsMilkBusyOnServer(Fixture.Target.m_uid), "persisted proof remains valid after owner moves");
            Fixture.PumpAll();
            Require(Fixture.Grants == 1, "confirmed prior-owner write grants once after ownership transfer");
        }

        private static void CheckMissingStateAndPartialDelivery()
        {
            NewFixture();
            Require(ReadCooldownUntil(Fixture.Target) == 0L && ReadLastMilkRequest(Fixture.Target) == string.Empty,
                "absent milk record is ready without a fabricated prior receipt");
            Fixture.Peer = Fixture.Target.Owner; EnsureMilkState(Fixture.Lox, Fixture.Lox.View);
            Require(Fixture.Writes == 0 && Fixture.ForceSends == 0,
                "first untracked-state validation neither writes nor starts an artificial cooldown");
            Begin(); Fixture.PumpAll();
            Require(Fixture.Grants == 1 && Fixture.Writes == 1 && Fixture.Target.Until == Fixture.Now + TimeSpan.TicksPerMinute &&
                !string.IsNullOrEmpty(Fixture.Target.Request), "first eligible untracked Lox request grants milk and persists only the normal cooldown");
            Fixture.Deliver(Fixture.History.First(packet => packet.Name == MilkRequestServerRpc)); Fixture.PumpAll();
            Begin(); Fixture.PumpAll();
            Require(Fixture.Grants == 1 && Fixture.Writes == 1 && MilkLocks.Count == 0 &&
                Fixture.Messages.Contains("$flg_cuisine_milk_cooldown"), "duplicate and fresh requests during first normal cooldown cannot grant or write twice");
            NewFixture(); Fixture.Target.Until = long.MaxValue; Fixture.Target.ServerUntil = long.MaxValue;
            Begin(); Fixture.PumpAll();
            Require(Fixture.Writes == 0 && Fixture.Grants == 0 && MilkLocks.Count == 0 &&
                !Fixture.History.Any(packet => packet.Name == MilkApplyOwnerRpc), "corrupt record sentinel is denied before owner mutation and is never treated as absent");
            NewFixture(); Fixture.ThrowOnGrant = true; Begin(); Fixture.PumpAll();
            Require(Fixture.Grants == 1 && _pendingMilk == null, "pending is consumed even when local grant throws after partial delivery");
            Fixture.Deliver(Fixture.History.First(packet => packet.Name == MilkResultClientRpc)); Fixture.PumpAll();
            Require(Fixture.Grants == 1, "uncertain local delivery is never repeated by result replay");
            EndNetworkSession();
            Require(_pendingMilk == null && MilkSessions.Count == 0 && MilkLocks.Count == 0, "network teardown clears all session-owned state");
        }

        private static void Begin() { Fixture.Peer = 2; RequestMilk(Fixture.Player2, Fixture.Lox); }
        private static void NewFixture() { EndNetworkSession(); Fixture.Reset(); Fixture.Peer = 2; RegisterNetworkRpcs(); }
        private static void Require(bool condition, string description)
        {
            if (!condition) throw new InvalidOperationException("FAILED: " + description);
            _passed++; Console.WriteLine("PASS " + description);
        }
    }

    internal static class TamingOrbSystem { internal static bool IsCuisineCaptureBlocked(ZDOID target) => Fixture.CaptureBlocked; }
    internal sealed class FeedLikeGrandmaPlugin
    {
        internal static readonly FeedLikeGrandmaPlugin Instance = new();
        internal void StartCoroutine(IEnumerator iterator)
        {
            if (iterator.MoveNext()) Fixture.Coroutines.Add(Tuple.Create(Fixture.Peer, iterator));
        }
    }
}

internal static class Fixture
{
    internal static long Peer;
    internal static long Now;
    internal static bool HoldReplication, RequireBreeding, CaptureBlocked, ThrowOnGrant;
    internal static int Writes, Grants, Amount, ForceSends;
    internal static ZDO Target = null!, PlayerZdo = null!;
    internal static Player Player2 = null!;
    internal static Character Lox = null!;
    internal static readonly Queue<Packet> Queue = new();
    internal static readonly List<Packet> History = new();
    internal static readonly List<string> Messages = new();
    internal static readonly List<Tuple<long, IEnumerator>> Coroutines = new();
    internal static void Reset()
    {
        Peer = 2; Now = 1000L * TimeSpan.TicksPerSecond; Time.realtimeSinceStartup = 0f;
        HoldReplication = RequireBreeding = CaptureBlocked = ThrowOnGrant = false;
        Writes = Grants = Amount = ForceSends = 0;
        Queue.Clear(); History.Clear(); Messages.Clear(); Coroutines.Clear();
        ZRoutedRpc.instance = new ZRoutedRpc(); ZNetScene.Objects.Clear(); ZDOMan.Objects.Clear();
        Target = new ZDO { m_uid = new ZDOID(10), Owner = 3, Tamed = true, Bred = true, Prefab = "Lox".GetStableHashCode() };
        PlayerZdo = new ZDO { m_uid = new ZDOID(20), Owner = 2, RightItem = "VC_MilkinBucket".GetStableHashCode() };
        ZDOMan.Objects[Target.m_uid] = Target; ZDOMan.Objects[PlayerZdo.m_uid] = PlayerZdo;
        ZDOMan.Objects[new ZDOID(30)] = new ZDO { m_uid = new ZDOID(30), Owner = 3, RightItem = "VC_MilkinBucket".GetStableHashCode() };
        Lox = new Character { name = "Lox", View = new ZNetView(Target) };
        Player2 = new Player { name = "Player", View = new ZNetView(PlayerZdo), RightItem = new ItemDrop.ItemData { m_dropPrefab = new GameObject { name = "VC_MilkinBucket" } } };
        GameObject target = new() { Character = Lox, View = Lox.View };
        GameObject player = new() { Character = Player2, View = Player2.View };
        ZNetScene.Objects[Target.m_uid] = target; ZNetScene.Objects[PlayerZdo.m_uid] = player;
        ZNetScene.Prefab = new GameObject { Character = new Character { name = "Lox" } };
    }
    internal static void Deliver(Packet packet)
    {
        long previous = Peer; Peer = packet.Recipient; History.Add(packet);
        ((Action<long, ZPackage>)ZRoutedRpc.instance.Handlers[packet.Name.GetStableHashCode()]!)(packet.Sender, new ZPackage(packet.Bytes));
        Peer = previous;
    }
    internal static void PumpAll()
    {
        int budget = 100;
        while (Queue.Count != 0 && budget-- > 0) Deliver(Queue.Dequeue());
        if (budget <= 0) throw new InvalidOperationException("transport did not quiesce");
    }
    internal static void PumpUntil(string name)
    {
        while (Queue.Count > 0 && Queue.Peek().Name != name) Deliver(Queue.Dequeue());
        if (Queue.Count == 0) throw new InvalidOperationException("missing expected packet " + name);
    }
    internal static void Advance(float seconds)
    {
        Time.realtimeSinceStartup += seconds; Now += (long)(seconds * TimeSpan.TicksPerSecond);
        foreach (Tuple<long, IEnumerator> coroutine in Coroutines.ToArray())
        {
            long previous = Peer; Peer = coroutine.Item1;
            if (!coroutine.Item2.MoveNext()) Coroutines.Remove(coroutine);
            Peer = previous;
        }
    }
}

internal sealed class Packet
{
    internal long Sender, Recipient;
    internal string Name = string.Empty;
    internal byte[] Bytes = Array.Empty<byte>();
    internal Packet WithSender(long sender) => new() { Sender = sender, Recipient = Recipient, Name = Name, Bytes = Bytes };
}

public struct ZDOID : IEquatable<ZDOID>
{
    public long Value;
    public ZDOID(long value) { Value = value; }
    public static ZDOID None => new(0);
    public bool IsNone() => Value == 0;
    public bool Equals(ZDOID other) => Value == other.Value;
    public override bool Equals(object? other) => other is ZDOID id && Equals(id);
    public override int GetHashCode() => Value.GetHashCode();
}
public sealed class ZDO
{
    public ZDOID m_uid;
    public long Owner, Until, ServerUntil;
    public string Request = string.Empty, ServerRequest = string.Empty;
    public int RightItem, Prefab;
    public bool Tamed, Bred;
    public float Health = 100f;
    public Vector3 Position;
    public long GetOwner() => Owner;
    public int GetPrefab() => Prefab;
    public int GetInt(int key) => RightItem;
    public bool GetBool(int key) => Tamed;
    public float GetFloat(int key, float fallback) => Health;
    public Vector3 GetPosition() => Position;
}
public sealed class ZDOMan
{
    public static readonly ZDOMan instance = new();
    internal static readonly Dictionary<ZDOID, ZDO> Objects = new();
    public ZDO? GetZDO(ZDOID id) => Objects.TryGetValue(id, out ZDO? zdo) ? zdo : null;
    public void ForceSendZDO(ZDOID id) { Fixture.ForceSends++; }
}
public sealed class ZNetView
{
    private readonly ZDO _zdo;
    public ZNetView(ZDO zdo) { _zdo = zdo; }
    public bool IsValid() => true;
    public bool IsOwner() => _zdo.Owner == Fixture.Peer;
    public ZDO GetZDO() => _zdo;
}
public class Character
{
    public string name = string.Empty;
    public Transform transform = new();
    public ZNetView View = null!;
    public bool Dead;
    public bool IsDead() => Dead;
    public bool IsTamed() => true;
    public ZDOID GetZDOID() => View.GetZDO().m_uid;
}
public sealed class Player : Character
{
    public static Player? m_localPlayer => Fixture.Peer == 2 ? Fixture.Player2 : null;
    public ItemDrop.ItemData? RightItem;
}
public static class ItemDrop { public sealed class ItemData { public GameObject? m_dropPrefab; } }
public static class ZDOVars { public const int s_rightItem = 1, s_tamed = 2, s_health = 3; }
public sealed class ZNetPeer { public ZDOID m_characterID; }
public sealed class ZNet
{
    public static readonly ZNet instance = new();
    public bool IsServer() => Fixture.Peer == 1;
    public ZNetPeer? GetPeer(long peer) => peer == 2 || peer == 3 ? new ZNetPeer { m_characterID = new ZDOID(peer * 10) } : null;
}
public sealed class ZNetScene
{
    public static readonly ZNetScene instance = new();
    internal static readonly Dictionary<ZDOID, GameObject> Objects = new();
    internal static GameObject Prefab = null!;
    // Model remote dedicated-server zones: state exists, server has no live Character instances.
    public GameObject? FindInstance(ZDOID id) => Fixture.Peer != 1 && Objects.TryGetValue(id, out GameObject? value) ? value : null;
    public GameObject? GetPrefab(int hash) => hash == "Lox".GetStableHashCode() ? Prefab : null;
}
public sealed class ZRoutedRpc
{
    public static ZRoutedRpc instance = new();
    internal readonly Hashtable Handlers = new();
    public IDictionary RpcHandlers() => Handlers;
    public long PeerId() => Fixture.Peer;
    public long GetServerPeerID() => 1;
    public void Register(string name, Action<long, ZPackage> handler) => Handlers[name.GetStableHashCode()] = handler;
    public void InvokeRoutedRPC(long peer, string name, ZPackage pkg) => Fixture.Queue.Enqueue(new Packet { Sender = Fixture.Peer, Recipient = peer, Name = name, Bytes = pkg.Bytes() });
}
public sealed class ZPackage
{
    private readonly MemoryStream _stream;
    private readonly BinaryReader _reader;
    private readonly BinaryWriter _writer;
    public ZPackage() : this(Array.Empty<byte>()) { }
    public ZPackage(byte[] bytes) { _stream = new MemoryStream(); _stream.Write(bytes, 0, bytes.Length); _stream.Position = 0; _reader = new BinaryReader(_stream); _writer = new BinaryWriter(_stream); }
    public int Size() => (int)_stream.Length;
    public byte[] Bytes() => _stream.ToArray();
    public void Write(string value) => _writer.Write(value);
    public void Write(long value) => _writer.Write(value);
    public void Write(int value) => _writer.Write(value);
    public void Write(bool value) => _writer.Write(value);
    public void Write(ZDOID value) => Write(value.Value);
    public void Write(Vector3 value) { _writer.Write(value.x); _writer.Write(value.y); _writer.Write(value.z); }
    public string ReadString() => _reader.ReadString();
    public long ReadLong() => _reader.ReadInt64();
    public int ReadInt() => _reader.ReadInt32();
    public bool ReadBool() => _reader.ReadBoolean();
    public ZDOID ReadZDOID() => new(ReadLong());
    public Vector3 ReadVector3() => new(_reader.ReadSingle(), _reader.ReadSingle(), _reader.ReadSingle());
}
public static class HashFixture
{
    public static int GetStableHashCode(this string text) { unchecked { int hash = 17; foreach (char c in text) hash = hash * 31 + c; return hash; } }
}
namespace UnityEngine
{
    public struct Vector3
    {
        public float x, y, z;
        public Vector3(float x, float y, float z) { this.x = x; this.y = y; this.z = z; }
        public static float Distance(Vector3 a, Vector3 b) => (float)Math.Sqrt((a.x - b.x) * (a.x - b.x) + (a.y - b.y) * (a.y - b.y) + (a.z - b.z) * (a.z - b.z));
    }
    public sealed class Transform { public Vector3 position; }
    public sealed class GameObject
    {
        public string name = string.Empty;
        public Character? Character;
        public ZNetView? View;
        public T? GetComponent<T>() where T : class => typeof(T) == typeof(ZNetView) ? View as T : Character as T;
    }
    public static class Time { public static float realtimeSinceStartup; }
    public sealed class WaitForSecondsRealtime { public WaitForSecondsRealtime(float seconds) { } }
}
