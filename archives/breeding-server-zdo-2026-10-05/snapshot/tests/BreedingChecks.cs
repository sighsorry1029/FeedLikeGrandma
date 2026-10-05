using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;

// Production FeedProcreationSystem is source-linked unchanged. These small containers
// provide controlled data, RPC queues and coroutine scheduling, not game simulation.
// In particular, ownership migration, actual sector delivery, Harmony and Unity are
// NOT executed. Separate game/API checks must validate those external contracts.
namespace UnityEngine
{
    internal class Object
    {
        internal bool Destroyed;
        public static bool operator ==(Object? a, Object? b) =>
            (ReferenceEquals(a, null) || a.Destroyed) && (ReferenceEquals(b, null) || b.Destroyed) || ReferenceEquals(a, b);
        public static bool operator !=(Object? a, Object? b) => !(a == b);
        public override bool Equals(object? other) => ReferenceEquals(this, other);
        public override int GetHashCode() => base.GetHashCode();
    }
    internal sealed class Transform { internal Vector3 position; }
    internal struct Vector3
    {
        internal float x, y, z;
        internal Vector3(float x, float y, float z) { this.x = x; this.y = y; this.z = z; }
    }
    internal struct Vector2i
    {
        internal int x, y;
        internal Vector2i(int x, int y) { this.x = x; this.y = y; }
    }
    internal sealed class GameObject : Object
    {
        internal string name;
        internal readonly Dictionary<Type, object> Components = new();
        internal GameObject(string name) { this.name = name; }
        public T GetComponent<T>() where T : class => Components.TryGetValue(typeof(T), out object value) ? (T)value : null!;
        internal void Set<T>(T value) where T : class => Components[typeof(T)] = value;
    }
    internal static class Time { internal static float realtimeSinceStartup; }
    internal static class Mathf { internal static int CeilToInt(float f) => (int)Math.Ceiling(f); }
}

internal static class StableHash
{
    internal static int GetStableHashCode(this string s)
    {
        unchecked { int hash = 23; foreach (char c in s) hash = hash * 31 + c; return hash; }
    }
}
internal static class Utils
{
    internal static string GetPrefabName(GameObject obj) => GetPrefabName(obj.name);
    internal static string GetPrefabName(string name) => name.Replace("(Clone)", "").Trim();
    internal static float DistanceXZ(Vector3 a, Vector3 b) => (float)Math.Sqrt((a.x - b.x) * (a.x - b.x) + (a.z - b.z) * (a.z - b.z));
}
internal struct ZDOID : IEquatable<ZDOID>
{
    internal long User;
    internal uint Id;
    internal ZDOID(long user, uint id) { User = user; Id = id; }
    public bool Equals(ZDOID other) => User == other.User && Id == other.Id;
    public override bool Equals(object? other) => other is ZDOID id && Equals(id);
    public override int GetHashCode() => User.GetHashCode() ^ Id.GetHashCode();
}
internal static class ZDOVars { internal const int s_tamed = 1; }
internal sealed class ZDO
{
    internal ZDOID m_uid;
    internal int Prefab;
    internal long Owner = 2;
    public uint OwnerRevision = 1;
    internal bool Tamed = true;
    internal bool Valid = true;
    internal Vector3 Position;
    internal bool IsValid() => Valid;
    internal int GetPrefab() => Prefab;
    internal long GetOwner() => Owner;
    internal Vector3 GetPosition() => Position;
    internal bool GetBool(int _) => Tamed;
}
internal sealed class ZNetView
{
    internal ZDO Data = null!;
    internal bool Valid = true;
    internal bool IsValid() => Valid && Data.Valid;
    internal bool IsOwner() => Data.Owner == ZRoutedRpc.LocalPeer;
    internal ZDO GetZDO() => Data;
}
internal sealed class Tameable
{
    internal bool Tamed = true;
    internal bool m_startsTamed;
    internal bool IsTamed() => Tamed;
}
internal sealed class Procreation : UnityEngine.Object
{
    internal readonly Transform transform = new();
    internal int m_maxCreatures = 4;
    internal float m_totalCheckRange = 10f;
    internal GameObject m_offspring = null!;
    internal ZNetView View = null!;
    internal Tameable Tameable = new();
    internal GameObject Own = null!;
    internal GameObject Child = null!;
    internal bool Pregnant;
    internal int OriginalRuns;
}
internal struct SimulationDistance
{
    internal readonly int Near;
    internal SimulationDistance(int near, int _, bool __) { Near = near; }
}
internal static class ZoneSystem
{
    internal static Vector2i GetZone(Vector3 p) => new((int)Math.Floor((p.x + 32f) / 64f), (int)Math.Floor((p.z + 32f) / 64f));
}
internal sealed class ZDOMan
{
    internal static ZDOMan instance = null!;
    internal readonly List<ZDO> World = new();
    internal readonly Dictionary<string, List<List<ZDO>>> IterativePages = new();
    internal int SectorCalls, IterativeCalls;
    internal static long GetSessionID() => 1;
    internal ZDO GetZDO(ZDOID id) => World.FirstOrDefault(z => z.m_uid.Equals(id))!;
    internal void FindSectorObjects(Vector2i center, SimulationDistance distance, List<ZDO> results)
    {
        SectorCalls++;
        foreach (ZDO z in World)
        {
            Vector2i sector = ZoneSystem.GetZone(z.Position);
            if (Math.Abs(sector.x - center.x) <= distance.Near && Math.Abs(sector.y - center.y) <= distance.Near) results.Add(z);
        }
    }
    internal bool GetAllZDOsWithPrefabIterative(string name, List<ZDO> results, ref int index)
    {
        IterativeCalls++;
        if (IterativePages.TryGetValue(name, out List<List<ZDO>> pages))
        {
            if (index < pages.Count) results.AddRange(pages[index]);
            index++;
            return index >= pages.Count;
        }
        results.AddRange(World.Where(z => z.Prefab == name.GetStableHashCode()));
        index++;
        return true;
    }
}
internal sealed class ZNetScene
{
    internal static ZNetScene instance = null!;
    internal readonly Dictionary<int, GameObject> Prefabs = new();
    internal GameObject GetPrefab(int hash) => Prefabs.TryGetValue(hash, out GameObject prefab) ? prefab : null!;
    internal GameObject GetPrefab(string name) => GetPrefab(name.GetStableHashCode());
}
internal sealed class ZNet
{
    internal static ZNet instance = null!;
    internal bool Server;
    internal readonly HashSet<long> Peers = new() { 2, 3 };
    internal bool IsServer() => Server;
    internal object? GetPeer(long id) => Peers.Contains(id) ? this : null;
}
internal sealed class ZPackage
{
    private readonly MemoryStream _stream;
    private readonly BinaryWriter _writer;
    private readonly BinaryReader _reader;
    internal ZPackage() : this(Array.Empty<byte>()) { }
    private ZPackage(byte[] bytes)
    {
        _stream = new MemoryStream();
        _stream.Write(bytes, 0, bytes.Length);
        _stream.Position = 0;
        _writer = new BinaryWriter(_stream);
        _reader = new BinaryReader(_stream);
    }
    internal ZPackage Copy() => new(_stream.ToArray());
    internal int Size() => (int)_stream.Length;
    internal void Write(ZDOID id) { _writer.Write(id.User); _writer.Write(id.Id); }
    internal void Write(string value) => _writer.Write(value);
    internal void Write(bool value) => _writer.Write(value);
    internal void Write(float value) => _writer.Write(value);
    internal void Write(int value) => _writer.Write(value);
    internal void Write(Vector3 value) { Write(value.x); Write(value.y); Write(value.z); }
    internal ZDOID ReadZDOID() => new(_reader.ReadInt64(), _reader.ReadUInt32());
    internal string ReadString() => _reader.ReadString();
    internal bool ReadBool() => _reader.ReadBoolean();
    internal float ReadSingle() => _reader.ReadSingle();
    internal int ReadInt() => _reader.ReadInt32();
    internal Vector3 ReadVector3() => new(ReadSingle(), ReadSingle(), ReadSingle());
}
internal sealed class ZRoutedRpc
{
    internal static ZRoutedRpc instance = null!;
    internal static long LocalPeer = 2;
    internal long ServerPeer = 1;
    internal bool ImmediateSelf;
    internal readonly Dictionary<int, Action<long, ZPackage>> Handlers = new();
    internal readonly List<Message> Queue = new();
    internal sealed class Message
    {
        internal long Sender, Target;
        internal string Name = "";
        internal ZPackage Data = null!;
        internal Message Copy() => new() { Sender = Sender, Target = Target, Name = Name, Data = Data.Copy() };
    }
    internal Dictionary<int, Action<long, ZPackage>> RpcHandlers() => Handlers;
    internal long GetServerPeerID() => ServerPeer;
    internal void Register<T>(string name, Action<long, T> handler) => Handlers[name.GetStableHashCode()] = (sender, p) => handler(sender, (T)(object)p);
    internal void InvokeRoutedRPC(long target, string name, ZPackage data)
    {
        Message message = new() { Sender = LocalPeer, Target = target, Name = name, Data = data.Copy() };
        if (ImmediateSelf && target == LocalPeer) Deliver(message);
        else Queue.Add(message);
    }
    internal Message Take()
    {
        if (Queue.Count == 0) throw new InvalidOperationException("Expected queued RPC");
        Message message = Queue[0];
        Queue.RemoveAt(0);
        return message;
    }
    internal void Deliver(Message message)
    {
        long previous = LocalPeer;
        bool server = ZNet.instance.Server;
        LocalPeer = message.Target;
        ZNet.instance.Server = message.Target == ServerPeer;
        try { Handlers[message.Name.GetStableHashCode()](message.Sender, message.Data.Copy()); }
        finally { LocalPeer = previous; ZNet.instance.Server = server; }
    }
}

namespace FeedLikeGrandma
{
    internal static class GameAccess
    {
        internal static ZNetView NetworkView(this Procreation p) => p.View;
        internal static Tameable TameableComponent(this Procreation p) => p.Tameable;
        internal static bool IsPregnant(this Procreation p) => p.Pregnant;
        internal static ref GameObject OwnPrefab(this Procreation p) => ref p.Own;
        internal static ref GameObject OffspringPrefab(this Procreation p) => ref p.Child;
        internal static void RunProcreation(this Procreation p)
        {
            // Model the Harmony re-entry contract; do not call an unpatched original.
            if (FeedProcreationSystem.ShouldRunProcreation(p)) p.OriginalRuns++;
        }
    }
    internal sealed class FeedLikeGrandmaPlugin
    {
        internal enum Toggle { Off, On }
        internal sealed class Setting { internal Toggle Value = Toggle.On; }
        internal sealed class Logger
        {
            internal readonly List<string> Warnings = new();
            internal void LogWarning(string text) => Warnings.Add(text);
        }
        internal static Setting HorizontalProcreationLimit = new();
        internal static FeedLikeGrandmaPlugin Instance = new();
        internal static Logger Log = new();
        internal readonly List<IEnumerator> Coroutines = new();
        internal void StartCoroutine(IEnumerator routine)
        {
            if (routine.MoveNext()) Coroutines.Add(routine);
        }
        internal void Step()
        {
            long previous = ZRoutedRpc.LocalPeer;
            bool server = ZNet.instance.Server;
            ZRoutedRpc.LocalPeer = 1;
            ZNet.instance.Server = true;
            try
            {
                foreach (IEnumerator routine in Coroutines.ToArray())
                    if (!routine.MoveNext()) { Coroutines.Remove(routine); (routine as IDisposable)?.Dispose(); }
            }
            finally { ZRoutedRpc.LocalPeer = previous; ZNet.instance.Server = server; }
        }
    }

    internal static class BreedingChecks
    {
        private static int _passed;
        private sealed class Fixture
        {
            internal readonly ZRoutedRpc Rpc = new();
            internal readonly GameObject Adult = new("Boar");
            internal readonly GameObject Child = new("Piggy");
            internal readonly ZDO Parent;
            internal readonly Procreation Rule;
            internal readonly Procreation Instance;
            private uint _next = 10;
            internal Fixture(int limit = 2, float range = 10f, bool samePrefab = false)
            {
                FeedProcreationSystem.Shutdown();
                Time.realtimeSinceStartup = 10;
                FeedLikeGrandmaPlugin.Instance = new();
                FeedLikeGrandmaPlugin.Log = new();
                FeedLikeGrandmaPlugin.HorizontalProcreationLimit.Value = FeedLikeGrandmaPlugin.Toggle.On;
                ZRoutedRpc.instance = Rpc;
                ZRoutedRpc.LocalPeer = 2;
                ZNet.instance = new ZNet();
                ZDOMan.instance = new ZDOMan();
                ZNetScene.instance = new ZNetScene();
                ZNetScene.instance.Prefabs.Add(Adult.name.GetStableHashCode(), Adult);
                ZNetScene.instance.Prefabs.Add(Child.name.GetStableHashCode(), Child);
                Rule = new Procreation { m_offspring = samePrefab ? Adult : Child, m_maxCreatures = limit, m_totalCheckRange = range };
                Adult.Set(Rule);
                Adult.Set(new Tameable());
                Parent = Add(Adult, 95, 20, 0);
                Instance = new Procreation
                {
                    View = new ZNetView { Data = Parent }, m_offspring = Rule.m_offspring,
                    m_maxCreatures = limit, m_totalCheckRange = range
                };
                Instance.transform.position = Parent.Position;
            }
            internal ZDO Add(GameObject prefab, float x, float y = 0, float z = 0, long owner = 2)
            {
                ZDO data = new() { m_uid = new ZDOID(1, _next++), Prefab = prefab.name.GetStableHashCode(), Position = new Vector3(x, y, z), Owner = owner };
                ZDOMan.instance.World.Add(data);
                return data;
            }
            internal ZRoutedRpc.Message Begin()
            {
                Require(!FeedProcreationSystem.ShouldRunProcreation(Instance), "initial prefix defers original for server count");
                return Rpc.Take();
            }
            internal ZRoutedRpc.Message? Scan(ZRoutedRpc.Message request)
            {
                Rpc.Deliver(request);
                for (int i = 0; FeedLikeGrandmaPlugin.Instance.Coroutines.Count > 0 && i < 20; i++) FeedLikeGrandmaPlugin.Instance.Step();
                Require(FeedLikeGrandmaPlugin.Instance.Coroutines.Count == 0, "bounded fixture coroutine completes");
                return Rpc.Queue.Count == 0 ? null : Rpc.Take();
            }
            internal void Resolve()
            {
                ZRoutedRpc.Message? reply = Scan(Begin());
                Require(reply != null, "valid owner request receives server result");
                Rpc.Deliver(reply!);
            }
        }
        private sealed class CheckWire
        {
            internal ZDOID Parent;
            internal string Token = "";
            internal Vector3 Center;
            internal float Range;
            internal int Limit, Child;
            internal CheckWire(ZRoutedRpc.Message message)
            {
                ZPackage p = message.Data.Copy();
                Parent = p.ReadZDOID(); Token = p.ReadString(); Center = p.ReadVector3();
                Range = p.ReadSingle(); Limit = p.ReadInt(); Child = p.ReadInt();
            }
            internal ZPackage Write()
            {
                ZPackage p = new();
                p.Write(Parent); p.Write(Token); p.Write(Center); p.Write(Range); p.Write(Limit); p.Write(Child);
                return p;
            }
        }
        private static int Main()
        {
            try
            {
                CheckSpatialPopulation();
                CheckPolicyPreservation();
                CheckRequestValidation();
                CheckReplyLifetime();
                CheckSynchronousHost();
                CheckCancellation();
                CheckWorldQueries();
                Console.WriteLine("Passed " + _passed + " source-linked breeding checks.");
                Console.WriteLine("Scope: production prefix/RPC/count/lifetime logic with controlled plain fixtures. No Unity, real transport, Harmony installation, or dedicated-server gameplay execution.");
                return 0;
            }
            catch (Exception error) { Console.Error.WriteLine(error); return 1; }
            finally { FeedProcreationSystem.Shutdown(); }
        }
        private static void CheckSpatialPopulation()
        {
            Fixture f = new();
            f.Add(f.Child, 97, -40, owner: 3);
            Require(ZoneSystem.GetZone(f.Parent.Position).x != ZoneSystem.GetZone(new Vector3(97, 0, 0)).x, "fixture straddles the 96m sector boundary");
            f.Resolve();
            Require(f.Instance.OriginalRuns == 0, "server-only child across zone boundary counts despite different owner and height");
            Require(ZDOMan.instance.SectorCalls == 1 && ZDOMan.instance.IterativeCalls == 0, "ordinary radius uses one bounded sector query");

            f = new(); f.Add(f.Child, 97, 20); f.Resolve();
            Require(f.Instance.OriginalRuns == 0, "same-height child across boundary also blocks at cap");
            f = new(); f.Add(f.Child, 105, -1000); f.Resolve();
            Require(f.Instance.OriginalRuns == 0, "horizontal radius boundary is inclusive regardless of vertical separation");
            f = new(); f.Add(f.Child, 105.01f); f.Resolve();
            Require(f.Instance.OriginalRuns == 1, "outside horizontal radius does not count");
            f = new(); f.Add(f.Child, 95, 0, 10.01f); f.Resolve();
            Require(f.Instance.OriginalRuns == 1, "z distance participates in horizontal range");
            f = new(); f.Add(new GameObject("Deer"), 95); f.Resolve();
            Require(f.Instance.OriginalRuns == 1, "different prefab does not affect breeder cap");
            f = new(); ZDO invalid = f.Add(f.Child, 97); invalid.Valid = false; f.Resolve();
            Require(f.Instance.OriginalRuns == 1, "invalid destroyed ZDO does not count");
            f = new(3); f.Add(f.Child, 97); f.Resolve();
            Require(f.Instance.OriginalRuns == 1, "below cap re-enters patched original exactly once");
            Require(f.Rpc.Queue.Count == 0, "approval consumption does not recursively issue another count");
            f = new(2, samePrefab: true); f.Resolve();
            Require(f.Instance.OriginalRuns == 0, "same adult and offspring prefab preserves two vanilla count contributions");
            f = new(3, samePrefab: true); f.Resolve();
            Require(f.Instance.OriginalRuns == 1, "same prefab is weighted twice without unconditional rejection");
        }
        private static void CheckPolicyPreservation()
        {
            Fixture f = new();
            FeedLikeGrandmaPlugin.HorizontalProcreationLimit.Value = FeedLikeGrandmaPlugin.Toggle.Off;
            Require(FeedProcreationSystem.ShouldRunProcreation(f.Instance) && f.Rpc.Queue.Count == 0, "option off keeps vanilla path synchronous");
            f = new(); f.Instance.Pregnant = true;
            Require(FeedProcreationSystem.ShouldRunProcreation(f.Instance) && f.Rpc.Queue.Count == 0, "existing pregnancy bypass preserves birth policy");
            f = new(); f.Instance.Tameable.Tamed = false;
            Require(FeedProcreationSystem.ShouldRunProcreation(f.Instance), "untamed instance retains vanilla handling");
            f = new(); f.Parent.Owner = 3;
            Require(FeedProcreationSystem.ShouldRunProcreation(f.Instance) && f.Rpc.Queue.Count == 0, "nonowner leaves decision to vanilla owner gate");
            f = new(); f.Parent.Tamed = false; f.Adult.GetComponent<Tameable>().m_startsTamed = true; f.Resolve();
            Require(f.Instance.OriginalRuns == 1, "startsTamed prefab is supported without saved tamed bit");
            f = new(3); f.Add(f.Child, 97); f.Rule.m_maxCreatures = 1; f.Rule.m_totalCheckRange = 1; f.Resolve();
            Require(f.Instance.OriginalRuns == 1, "existing owner instance limit survives later prefab config reload");
            f = new(); f.Add(f.Child, 97); f.Rule.m_totalCheckRange = 1; f.Resolve();
            Require(f.Instance.OriginalRuns == 0, "existing owner instance range remains effective after prefab config reload");
            f = new(0);
            Require(!FeedProcreationSystem.ShouldRunProcreation(f.Instance) && f.Rpc.Queue.Count == 0, "nonpositive limit cannot authorize breeding");
            f = new(range: float.NaN);
            Require(!FeedProcreationSystem.ShouldRunProcreation(f.Instance) && f.Rpc.Queue.Count == 0, "nonfinite local radius rejects before RPC");
            f = new(); f.Rpc.ServerPeer = 0;
            Require(!FeedProcreationSystem.ShouldRunProcreation(f.Instance), "missing server identity fails closed");
        }
        private static void CheckRequestValidation()
        {
            InvalidRequest("unconnected sender", (f, m, w) => m.Sender = 99);
            InvalidRequest("connected nonowner sender", (f, m, w) => m.Sender = 3);
            InvalidRequest("wrong offspring prefab", (f, m, w) => w.Child = "Deer".GetStableHashCode());
            InvalidRequest("nonfinite range", (f, m, w) => w.Range = float.NaN);
            InvalidRequest("nonfinite position", (f, m, w) => w.Center.y = float.PositiveInfinity);
            InvalidRequest("forged far center", (f, m, w) => w.Center.x += 3);
            InvalidRequest("nonpositive limit", (f, m, w) => w.Limit = 0);
            InvalidRequest("malformed request token", (f, m, w) => w.Token = new string('g', 32));
            InvalidRequest("untamed parent ZDO", (f, m, w) => f.Parent.Tamed = false);
            InvalidRequest("missing parent ZDO", (f, m, w) => w.Parent = new ZDOID(9, 999));
            InvalidRequest("invalid parent ZDO", (f, m, w) => f.Parent.Valid = false);
            Fixture fixture = new();
            ZRoutedRpc.Message request = fixture.Begin();
            request.Data = new ZPackage();
            fixture.Rpc.Deliver(request);
            Require(fixture.Rpc.Queue.Count == 0 && FeedLikeGrandmaPlugin.Log.Warnings.Count == 1, "truncated request is contained and logged");
        }
        private static void InvalidRequest(string label, Action<Fixture, ZRoutedRpc.Message, CheckWire> mutate)
        {
            Fixture f = new();
            ZRoutedRpc.Message message = f.Begin();
            CheckWire wire = new(message);
            mutate(f, message, wire);
            message.Data = wire.Write();
            f.Rpc.Deliver(message);
            Require(f.Rpc.Queue.Count == 0 && FeedLikeGrandmaPlugin.Instance.Coroutines.Count == 0 && f.Instance.OriginalRuns == 0, label + " is rejected before scanning");
        }
        private static void CheckReplyLifetime()
        {
            Fixture f = new();
            ZRoutedRpc.Message request = f.Begin();
            Require(!FeedProcreationSystem.ShouldRunProcreation(f.Instance) && f.Rpc.Queue.Count == 0, "second tick while pending issues no duplicate request");
            f.Rpc.Deliver(request);
            f.Rpc.Deliver(request);
            Require(FeedLikeGrandmaPlugin.Instance.Coroutines.Count == 1, "replayed request is rate limited per parent");
            FeedLikeGrandmaPlugin.Instance.Step();
            ZRoutedRpc.Message reply = f.Rpc.Take();
            ZRoutedRpc.Message spoof = reply.Copy(); spoof.Sender = 3;
            f.Rpc.Deliver(spoof);
            Require(f.Instance.OriginalRuns == 0, "nonserver reply cannot approve breeding");
            ZRoutedRpc.Message mismatched = reply.Copy();
            ZPackage payload = new(); payload.Write(f.Parent.m_uid); payload.Write(Guid.NewGuid().ToString("N")); payload.Write(true);
            mismatched.Data = payload;
            f.Rpc.Deliver(mismatched);
            Require(f.Instance.OriginalRuns == 0, "wrong token cannot consume pending approval");
            f.Rpc.Deliver(reply); f.Rpc.Deliver(reply);
            Require(f.Instance.OriginalRuns == 1, "authentic result survives spoof attempts and duplicate reply executes once");

            f = new(); ZRoutedRpc.Message old = f.Scan(f.Begin())!;
            Time.realtimeSinceStartup += 6;
            f.Rpc.Deliver(old);
            Require(f.Instance.OriginalRuns == 0, "result past request lifetime cannot run original");
            ZRoutedRpc.Message current = f.Begin();
            f.Rpc.Deliver(old);
            Require(f.Instance.OriginalRuns == 0, "old reordered result cannot consume fresh request");
            f.Rpc.Deliver(f.Scan(current)!);
            Require(f.Instance.OriginalRuns == 1, "new request recovers after timeout and stale reply");

            f = new(); request = f.Begin();
            Time.realtimeSinceStartup += 6;
            ZRoutedRpc.Message retry = f.Begin();
            Require(new CheckWire(request).Token != new CheckWire(retry).Token, "timeout without reply allows fresh unique token");
            f.Rpc.Deliver(retry); Time.realtimeSinceStartup += 6; FeedLikeGrandmaPlugin.Instance.Step();
            Require(f.Rpc.Queue.Count == 0, "server scan expiration sends no stale approval");
        }
        private static void CheckCancellation()
        {
            CancelReply("owner lost", f => f.Parent.Owner = 3);
            CancelReply("owner left and returned with newer revision", f => { f.Parent.Owner = 3; f.Parent.OwnerRevision++; f.Parent.Owner = 2; f.Parent.OwnerRevision++; });
            CancelReply("scene object destroyed", f => f.Instance.Destroyed = true);
            CancelReply("view invalidated", f => f.Instance.View.Valid = false);
            CancelReply("parent moved beyond approval drift", f => f.Instance.transform.position.x += 1.01f);
            CancelReply("range changed", f => f.Instance.m_totalCheckRange++);
            CancelReply("limit changed", f => f.Instance.m_maxCreatures++);
            CancelReply("offspring changed", f => f.Instance.Child = f.Adult);
            CancelReply("became pregnant", f => f.Instance.Pregnant = true);
            CancelReply("option turned off", f => FeedLikeGrandmaPlugin.HorizontalProcreationLimit.Value = FeedLikeGrandmaPlugin.Toggle.Off);
            CancelReply("configuration cache reset", f => FeedProcreationSystem.ResetRuntimeCache());
            Fixture fixture = new(); ZRoutedRpc.Message message = fixture.Begin(); fixture.Rpc.Deliver(message);
            FeedProcreationSystem.ResetRuntimeCache(); FeedLikeGrandmaPlugin.Instance.Step();
            Require(fixture.Rpc.Queue.Count == 0, "reset invalidates in-flight server coroutine generation");
            fixture = new(); message = fixture.Begin(); fixture.Rpc.Deliver(message); fixture.Parent.Owner = 3;
            FeedLikeGrandmaPlugin.Instance.Step();
            Require(fixture.Rpc.Queue.Count == 0, "server revalidates owner before returning result");
            fixture = new(); message = fixture.Begin(); fixture.Rpc.Deliver(message); fixture.Parent.OwnerRevision += 2;
            FeedLikeGrandmaPlugin.Instance.Step();
            Require(fixture.Rpc.Queue.Count == 0, "server rejects intervening ownership round trip even after owner returns");
            fixture = new(); message = fixture.Begin(); fixture.Rpc.Deliver(message); ZNetScene.instance = null!;
            FeedLikeGrandmaPlugin.Instance.Step();
            Require(fixture.Rpc.Queue.Count == 0, "scene shutdown prevents scan reply");
            fixture = new(); fixture.Begin(); FeedProcreationSystem.Shutdown();
            Require(fixture.Rpc.Handlers.Count == 0, "shutdown unregisters only breeding RPC handlers");
        }
        private static void CheckSynchronousHost()
        {
            Fixture f = new();
            f.Parent.Owner = 1;
            ZRoutedRpc.LocalPeer = 1;
            ZNet.instance.Server = true;
            f.Rpc.ImmediateSelf = true;
            Require(!FeedProcreationSystem.ShouldRunProcreation(f.Instance), "host initial prefix defers even with synchronous self RPC");
            Require(f.Instance.OriginalRuns == 0 && FeedLikeGrandmaPlugin.Instance.Coroutines.Count == 1 && f.Rpc.Queue.Count == 0,
                "first coroutine yield prevents host result re-entering initial prefix stack");
            FeedLikeGrandmaPlugin.Instance.Step();
            Require(f.Instance.OriginalRuns == 1 && FeedLikeGrandmaPlugin.Instance.Coroutines.Count == 0 && f.Rpc.Queue.Count == 0,
                "host next step synchronously approves exactly one patched original call");
            f = new(); f.Parent.Owner = 1; f.Add(f.Child, 97, -50, owner: 3);
            ZRoutedRpc.LocalPeer = 1; ZNet.instance.Server = true; f.Rpc.ImmediateSelf = true;
            Require(!FeedProcreationSystem.ShouldRunProcreation(f.Instance), "host at-cap prefix also defers");
            FeedLikeGrandmaPlugin.Instance.Step();
            Require(f.Instance.OriginalRuns == 0 && FeedLikeGrandmaPlugin.Instance.Coroutines.Count == 0,
                "host synchronous denial cannot invoke original");
        }
        private static void CancelReply(string label, Action<Fixture> mutate)
        {
            Fixture f = new(); ZRoutedRpc.Message reply = f.Scan(f.Begin())!;
            mutate(f); f.Rpc.Deliver(reply);
            Require(f.Instance.OriginalRuns == 0, label + " invalidates existing approval");
        }
        private static void CheckWorldQueries()
        {
            Fixture f = new(range: 0); f.Add(f.Child, 20000, -1000); f.Resolve();
            Require(f.Instance.OriginalRuns == 0 && ZDOMan.instance.SectorCalls == 0, "zero radius preserves unlimited world count semantics");
            f = new(range: -1); f.Add(f.Child, -20000); f.Resolve();
            Require(f.Instance.OriginalRuns == 0, "negative radius preserves prior unlimited semantics");
            f = new(range: 257); f.Add(f.Child, 95 + 257.01f); f.Resolve();
            Require(f.Instance.OriginalRuns == 1 && ZDOMan.instance.IterativeCalls == 2, "large radius queries both prefabs and still filters XZ");
            f = new(3, 0); ZDO child = f.Add(f.Child, 10000);
            ZDOMan.instance.IterativePages[f.Adult.name] = new() { new() { f.Parent }, new() { f.Parent } };
            ZDOMan.instance.IterativePages[f.Child.name] = new() { new() { child }, new() { child } };
            f.Resolve();
            Require(f.Instance.OriginalRuns == 1 && ZDOMan.instance.IterativeCalls == 4, "iterative repeated boundaries do not double count ZDOs");
            f = new(1, 0);
            ZDOMan.instance.IterativePages[f.Adult.name] = new() { new() { f.Parent }, new() { f.Parent }, new() { f.Parent } };
            f.Resolve();
            Require(f.Instance.OriginalRuns == 0 && ZDOMan.instance.IterativeCalls == 1, "world scan stops immediately once cap is reached");
            f = new(3, 0, samePrefab: true);
            ZDOMan.instance.IterativePages[f.Adult.name] = new() { new() { f.Parent }, new() { f.Parent } };
            f.Resolve();
            Require(f.Instance.OriginalRuns == 1 && ZDOMan.instance.IterativeCalls == 2, "same-prefab scan runs once and deduplicates before weighted count");
            f = new(3, 0);
            ZDOMan.instance.IterativePages[f.Adult.name] = new() { new() { f.Parent }, new() { f.Parent } };
            ZRoutedRpc.Message request = f.Begin(); f.Rpc.Deliver(request); FeedLikeGrandmaPlugin.Instance.Step();
            Require(FeedLikeGrandmaPlugin.Instance.Coroutines.Count == 1 && f.Rpc.Queue.Count == 0, "incomplete world batch yields without result");
            FeedProcreationSystem.ResetRuntimeCache(); FeedLikeGrandmaPlugin.Instance.Step();
            Require(FeedLikeGrandmaPlugin.Instance.Coroutines.Count == 0 && f.Rpc.Queue.Count == 0, "generation reset cancels between iterative batches");
        }
        private static void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException("FAIL: " + message);
            _passed++;
        }
    }
}
