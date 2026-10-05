using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;

// Tests only managed persistence contracts in the built DLL. No Unity, RPC transport,
// network ownership, player save, or actual server is initialized by this executable.
internal static class OrbTransactionChecks
{
    private const BindingFlags AnyInstance = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
    private const BindingFlags AnyStatic = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
    private const string TransactionId = "00112233445566778899aabbccddeeff";
    private const string OrbId = "ffeeddccbbaa99887766554433221100";
    private static int passed;
    private static Type pendingType;
    private static Type snapshotType;
    private static Type journalType;
    private static Type entryType;

    private static int Main(string[] args)
    {
        if (args.Length != 1)
        {
            Console.Error.WriteLine("Usage: OrbTransactionChecks.exe <built FeedLikeGrandma.dll>");
            return 2;
        }

        string pluginPath = Path.GetFullPath(args[0]);
        AppDomain.CurrentDomain.AssemblyResolve += (_, eventArgs) =>
        {
            string path = Path.Combine(Path.GetDirectoryName(pluginPath), new AssemblyName(eventArgs.Name).Name + ".dll");
            return File.Exists(path) ? Assembly.LoadFrom(path) : null;
        };

        try
        {
            Assembly plugin = Assembly.LoadFrom(pluginPath);
            pendingType = plugin.GetType("FeedLikeGrandma.TamingOrbSystem+OrbPendingItemState", true);
            snapshotType = plugin.GetType("FeedLikeGrandma.TamingOrbSystem+OrbItemSnapshot", true);
            journalType = plugin.GetType("FeedLikeGrandma.TamingOrbJournal", true);
            entryType = journalType.GetNestedType("Entry", BindingFlags.NonPublic);
            CheckPendingPersistence();
            CheckJournal();
            string runtime = Type.GetType("Mono.Runtime") == null ? ".NET Framework" : "Mono";
            Console.WriteLine("Passed " + passed + " Orb persistence checks (" + runtime + " " + Environment.Version + ").");
            Console.WriteLine("Scope: managed saved-state/journal contracts only; Unity, RPC, and multiplayer execution are not tested.");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            return 1;
        }
    }

    private static void CheckPendingPersistence()
    {
        foreach (int action in new[] { 1, 2 })
        {
            foreach (int phase in new[] { 1, action == 1 ? 2 : 3 })
            {
                object state = NewPending(action, phase);
                string encoded;
                Require(SerializePending(state, out encoded), "serialize pending action/phase " + action + "/" + phase);
                object restored;
                Require(DeserializePending(encoded, out restored), "reload pending action/phase " + action + "/" + phase);
                Require((bool)Invoke(state, "MatchesIdentity", restored), "retain pending transaction/world/orb identity");
                Require((bool)Invoke(Get(state, "Previous"), "Matches", Get(restored, "Previous")), "retain rollback payload/CM state/stack/variant");
                Require((string)Get(restored, "RequestData") == "opaque/request=+/한글" &&
                        (string)Get(restored, "PreparedData") == (action == 1 ? "prepared/payload=+/" : "") &&
                        (string)Get(restored, "SpawnedId") == (action == 2 && phase == 3 ? "-1234567890123456789:4294967295" : ""),
                        "retain request/prepared/spawn recovery data");
                string roundTrip;
                Require(SerializePending(restored, out roundTrip) && roundTrip == encoded, "stable pending serialized bytes");
            }
        }

        string valid;
        Require(SerializePending(NewPending(2, 3), out valid), "prepare pending release fixture");
        byte[] bytes = Convert.FromBase64String(valid);
        foreach (string invalid in new[] { "", "not-base64!", Convert.ToBase64String(bytes.Take(bytes.Length - 1).ToArray()),
            Convert.ToBase64String(bytes.Concat(new byte[] { 0 }).ToArray()) })
        {
            object rejected;
            Require(!DeserializePending(invalid, out rejected) && rejected == null, "reject damaged/truncated/trailing pending state");
        }
        bytes[0] = 2;
        object parsed;
        Require(!DeserializePending(Convert.ToBase64String(bytes), out parsed) && parsed == null, "reject unsupported pending version");

        foreach (Action<object> invalidate in new Action<object>[]
        {
            state => Set(state, "WorldUid", 0L),
            state => Set(state, "TransactionId", "not-a-transaction"),
            state => Set(state, "OrbId", "not-an-orb"),
            state => SetEnum(state, "Action", 3),
            state => SetEnum(state, "Phase", 2),
            state => Set(state, "SpawnedId", "0:0"),
            state => Set(state, "SpawnedId", "1:4294967296"),
            state => Set(state, "SpawnedId", "1:2:3"),
            state => Set(state, "PreparedData", "capture-data-on-release"),
            state => Set(Get(state, "Previous"), "Stack", 0),
            state => Set(Get(state, "Previous"), "Variant", -1),
            state => Set(Get(state, "Previous"), "HadPayload", false)
        })
        {
            object invalid = NewPending(2, 3);
            invalidate(invalid);
            string ignored;
            Require(!SerializePending(invalid, out ignored) && ignored == "", "reject invalid pending contract before persistence");
        }

        object capture = NewPending(1, 2);
        SetEnum(capture, "Phase", 3);
        string output;
        Require(!SerializePending(capture, out output), "capture cannot enter release spawning phase");
        capture = NewPending(1, 2);
        Set(capture, "SpawnedId", "1:2");
        Require(!SerializePending(capture, out output), "capture cannot carry released creature ID");

        object identity = NewPending(2, 1);
        object other = NewPending(2, 1);
        Set(other, "WorldUid", 1122334456L);
        Require(!(bool)Invoke(identity, "MatchesIdentity", other), "pending identity isolates worlds");
        other = NewPending(1, 1);
        Require(!(bool)Invoke(identity, "MatchesIdentity", other), "pending identity isolates action kind");
        other = NewPending(2, 1);
        Set(other, "TransactionId", Guid.NewGuid().ToString("N"));
        Require(!(bool)Invoke(identity, "MatchesIdentity", other), "pending identity isolates transaction generations");
        other = NewPending(2, 1);
        Set(other, "OrbId", Guid.NewGuid().ToString("N"));
        Require(!(bool)Invoke(identity, "MatchesIdentity", other), "pending identity isolates orbs");
    }

    private static object NewPending(int action, int phase)
    {
        object snapshot = Activator.CreateInstance(snapshotType, true);
        Set(snapshot, "HadPayload", true);
        Set(snapshot, "Payload", "original/creature=+/한글");
        Set(snapshot, "HadCreatureManagerState", true);
        Set(snapshot, "CreatureManagerState", "{\"modifier\":\"Armored\"}");
        Set(snapshot, "Variant", 1);
        Set(snapshot, "Stack", 1);
        object state = Activator.CreateInstance(pendingType, true);
        Set(state, "TransactionId", TransactionId);
        Set(state, "OrbId", OrbId);
        Set(state, "WorldUid", 1122334455L);
        SetEnum(state, "Action", action);
        SetEnum(state, "Phase", phase);
        Set(state, "SpawnedId", action == 2 && phase == 3 ? "-1234567890123456789:4294967295" : "");
        Set(state, "RequestData", "opaque/request=+/한글");
        Set(state, "PreparedData", action == 1 ? "prepared/payload=+/" : "");
        Set(state, "Previous", snapshot);
        return state;
    }

    private static void CheckJournal()
    {
        string directory = Path.Combine(Path.GetTempPath(), "FeedLikeGrandma-OrbTests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            CheckJournalStateMachine(directory);
            CheckJournalDamagedFiles(directory);
            CheckJournalWriteFailure(directory);
        }
        finally
        {
            // The only recursive removal is this runner's freshly-created, exact temp directory.
            Directory.Delete(directory, true);
        }
    }

    private static void CheckJournalStateMachine(string directory)
    {
        const long world = 1234567L;
        string path = JournalPath(directory, world);
        object journal = OpenJournal(directory, world);
        string captureId = Guid.NewGuid().ToString("N");
        string releaseId = Guid.NewGuid().ToString("N");
        try
        {
            Require((long)Get(journal, "WorldUid") == world && (bool)Get(journal, "IsHealthy"), "open healthy world-specific journal");
            object second;
            string error;
            Require(!TryOpenJournal(directory, world, out second, out error) && second == null && error.Length != 0,
                    "second writer cannot open an active world journal");

            object capture = NewEntry(captureId, OrbId, "capture", "Reserved");
            Require(SaveJournal(journal, capture, out error), "persist capture reservation before destruction");
            Require(IsBlocked(journal, OrbId, "capture") && IsBlocked(journal, OrbId, "release"), "capture reservation blocks both new actions");
            long initialLength = new FileInfo(path).Length;
            Require(SaveJournal(journal, capture, out error) && new FileInfo(path).Length == initialLength, "identical receipt retry does not append duplicate records");
            object contender = NewEntry(Guid.NewGuid().ToString("N"), OrbId, "release", "Reserved");
            Require(!SaveJournal(journal, contender, out error), "reservation rejects competing transaction for same orb");

            Set(capture, "State", "Committing");
            Set(capture, "PreparedData", Encode("captured-creature"));
            Require(SaveJournal(journal, capture, out error), "persist capture commit with frozen creature data");
            Require(IsBlocked(journal, OrbId, "capture") && IsBlocked(journal, OrbId, "release"), "capture commit blocks both new actions");
            Set(capture, "State", "Reserved");
            Require(!SaveJournal(journal, capture, out error), "commit cannot move back to reservation");
            Set(capture, "State", "Committing");
            Set(capture, "PreparedData", Encode("different-creature"));
            Require(!SaveJournal(journal, capture, out error), "prepared capture payload cannot be replaced");
            Set(capture, "PreparedData", Encode("captured-creature"));
            Set(capture, "State", "CompletedSuccess");
            Set(capture, "ResultData", Encode("capture-success"));
            Require(SaveJournal(journal, capture, out error), "persist successful capture receipt");
            Require(IsBlocked(journal, OrbId, "capture") && !IsBlocked(journal, OrbId, "release"), "successful capture permits release but rejects duplicate capture");
            Set(capture, "State", "CompletedFailure");
            Require(!SaveJournal(journal, capture, out error), "successful terminal receipt cannot become failure");

            object release = NewEntry(releaseId, OrbId, "release", "Reserved");
            Require(SaveJournal(journal, release, out error), "same orb may release its successfully captured creature");
            Require(IsBlocked(journal, OrbId, "capture") && IsBlocked(journal, OrbId, "release"), "release reservation blocks duplicate use");
            Set(release, "State", "Committing");
            Require(SaveJournal(journal, release, out error), "persist release commit");
            Set(release, "State", "CompletedSuccess");
            Set(release, "ResultData", Encode("released-creature-id"));
            Require(SaveJournal(journal, release, out error), "persist release completion");
            Require(IsBlocked(journal, OrbId, "capture") && IsBlocked(journal, OrbId, "release"), "consumed orb identity remains blocked after release completion");
            Require(!SaveJournal(journal, contender, out error), "completed release rejects another transaction for consumed identity");

            Set(release, "RequestData", Encode("changed-request"));
            Require(!SaveJournal(journal, release, out error), "transaction request is immutable");
            Set(release, "RequestData", Encode("request-" + releaseId));
            Set(release, "ResultData", Encode("another-creature-id"));
            Require(!SaveJournal(journal, release, out error), "terminal creature result is immutable");
            Set(release, "State", "Reserved");
            Require(!SaveJournal(journal, release, out error), "completed release cannot return to pending");

            object stored;
            Require(GetReceipt(journal, releaseId, out stored) && (string)Get(stored, "State") == "CompletedSuccess", "caller mutation cannot alter stored receipt");
            Set(stored, "State", "CompletedFailure");
            Require(GetReceipt(journal, releaseId, out stored) && (string)Get(stored, "State") == "CompletedSuccess", "TryGet exposes detached receipt copy");
            foreach (object copy in (IEnumerable)Get(journal, "Entries")) Set(copy, "State", "CompletedFailure");
            Require(IsBlocked(journal, OrbId, "release"), "Entries snapshots cannot unlock consumed orb");

            string failedOrb = Guid.NewGuid().ToString("N");
            object failed = NewEntry(Guid.NewGuid().ToString("N"), failedOrb, "release", "Reserved");
            Require(SaveJournal(journal, failed, out error), "reserve separate release failure fixture");
            Set(failed, "State", "CompletedFailure");
            Set(failed, "ResultData", Encode("release-failed-before-spawn"));
            Require(SaveJournal(journal, failed, out error), "record conclusive release failure");
            Require(!IsBlocked(journal, failedOrb, "capture") && !IsBlocked(journal, failedOrb, "release"), "conclusive failure releases orb for a new transaction");
            Set(failed, "State", "CompletedSuccess");
            Require(!SaveJournal(journal, failed, out error), "failure terminal receipt is immutable");
            Require(SaveJournal(journal, NewEntry(Guid.NewGuid().ToString("N"), failedOrb, "release", "Reserved"), out error), "new request after conclusive failure can reserve orb");
            Require((bool)Get(journal, "IsHealthy"), "validation rejections do not poison healthy journal");
        }
        finally { ((IDisposable)journal).Dispose(); }

        // File age must not expire completion receipts on reopen. This is not a
        // simulation of the separate in-memory RPC retry/retention timer.
        File.SetLastWriteTimeUtc(path, new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        journal = OpenJournal(directory, world);
        try
        {
            object restored;
            Require(GetReceipt(journal, captureId, out restored) && (string)Get(restored, "State") == "CompletedSuccess", "restart reloads completed capture");
            Require(GetReceipt(journal, releaseId, out restored) && (string)Get(restored, "ResultData") == Encode("released-creature-id"), "restart reloads exact final release result");
            Require(IsBlocked(journal, OrbId, "capture") && IsBlocked(journal, OrbId, "release"), "old persisted completion does not expire on reopen");
        }
        finally { ((IDisposable)journal).Dispose(); }

        object otherWorld = OpenJournal(directory, world + 1);
        try
        {
            object absent;
            Require(!GetReceipt(otherWorld, releaseId, out absent) && !IsBlocked(otherWorld, OrbId, "release"), "world journals isolate transaction and orb identities");
        }
        finally { ((IDisposable)otherWorld).Dispose(); }
    }

    private static void CheckJournalDamagedFiles(string directory)
    {
        const long world = 654321L;
        string path = JournalPath(directory, world);
        object journal = OpenJournal(directory, world);
        try
        {
            string error;
            Require(SaveJournal(journal, NewEntry(TransactionId, OrbId, "release", "CompletedSuccess"), out error), "write corruption detection fixture");
        }
        finally { ((IDisposable)journal).Dispose(); }
        byte[] original = File.ReadAllBytes(path);
        byte[] corrupt = (byte[])original.Clone();
        corrupt[corrupt.Length - 1] ^= 0x40;
        byte[] corruptHeader = (byte[])original.Clone();
        corruptHeader[0] ^= 0x20;
        foreach (byte[] invalid in new[] { new byte[0], original.Take(3).ToArray(), original.Take(original.Length - 1).ToArray(),
            original.Concat(new byte[] { 1 }).ToArray(), corrupt, corruptHeader })
        {
            File.WriteAllBytes(path, invalid);
            object opened;
            string error;
            Require(!TryOpenJournal(directory, world, out opened, out error) && opened == null && error.Length != 0,
                    "damaged/empty/truncated/checksum journal fails closed");
            Require(File.ReadAllBytes(path).SequenceEqual(invalid), "failed recovery preserves damaged journal evidence");
        }

        File.WriteAllBytes(JournalPath(directory, world + 1), original);
        object wrongWorld;
        string wrongWorldError;
        Require(!TryOpenJournal(directory, world + 1, out wrongWorld, out wrongWorldError) && wrongWorld == null,
                "journal copied under another world name is rejected");
        File.WriteAllBytes(path, original);
        journal = OpenJournal(directory, world);
        ((IDisposable)journal).Dispose();
        Require(true, "restoring intact same-world fixture permits journal reopen");
    }

    private static void CheckJournalWriteFailure(string directory)
    {
        const long world = 987654L;
        object journal = OpenJournal(directory, world);
        try
        {
            // Deliberately close only the backing file to exercise the actual failed-write
            // path (not just the public Dispose guard). It emulates unavailable storage,
            // not OS power loss or a partially completed disk flush.
            ((Stream)journalType.GetField("_stream", AnyInstance).GetValue(journal)).Dispose();
            string error;
            Require(!SaveJournal(journal, NewEntry(TransactionId, OrbId, "release", "Reserved"), out error) && error.Length != 0,
                    "failed durable write rejects reservation");
            object absent;
            Require(!GetReceipt(journal, TransactionId, out absent) && !(bool)Get(journal, "IsHealthy"), "failed write never publishes in-memory receipt and marks journal unhealthy");
            Require(!SaveJournal(journal, NewEntry(Guid.NewGuid().ToString("N"), Guid.NewGuid().ToString("N"), "capture", "Reserved"), out error),
                    "unhealthy journal blocks subsequent actions");
        }
        finally { ((IDisposable)journal).Dispose(); }

        journal = OpenJournal(directory, world);
        try
        {
            object absent;
            Require(!GetReceipt(journal, TransactionId, out absent), "reopen does not invent failed write receipt");
        }
        finally { ((IDisposable)journal).Dispose(); }
    }

    private static string JournalPath(string directory, long world) => Path.Combine(directory, world.ToString(System.Globalization.CultureInfo.InvariantCulture) + ".journal");
    private static string Encode(string value) => Convert.ToBase64String(Encoding.UTF8.GetBytes(value));

    private static object NewEntry(string transactionId, string orbId, string kind, string state)
    {
        object entry = Activator.CreateInstance(entryType, true);
        Set(entry, "TransactionId", transactionId);
        Set(entry, "OrbId", orbId);
        Set(entry, "Kind", kind);
        Set(entry, "State", state);
        Set(entry, "RequestData", Encode("request-" + transactionId));
        return entry;
    }

    private static object OpenJournal(string directory, long world)
    {
        object journal;
        string error;
        if (!TryOpenJournal(directory, world, out journal, out error)) throw new InvalidOperationException(error);
        return journal;
    }

    private static bool TryOpenJournal(string directory, long world, out object journal, out string error)
    {
        object[] args = { directory, world, null, null };
        bool result = (bool)journalType.GetMethod("TryOpen", AnyStatic).Invoke(null, args);
        journal = args[2];
        error = (string)args[3];
        return result;
    }

    private static bool SaveJournal(object journal, object entry, out string error)
    {
        object[] args = { entry, null };
        bool result = (bool)journalType.GetMethod("TrySave", AnyInstance).Invoke(journal, args);
        error = (string)args[1];
        return result;
    }

    private static bool GetReceipt(object journal, string transactionId, out object entry)
    {
        object[] args = { transactionId, null };
        bool result = (bool)journalType.GetMethod("TryGet", AnyInstance).Invoke(journal, args);
        entry = args[1];
        return result;
    }

    private static bool IsBlocked(object journal, string orbId, string action)
    {
        object[] args = { orbId, action, null };
        return (bool)journalType.GetMethod("TryGetBlockingAction", AnyInstance).Invoke(journal, args);
    }

    private static bool SerializePending(object state, out string text)
    {
        object[] args = { null };
        bool result = (bool)state.GetType().GetMethod("TrySerialize", AnyInstance).Invoke(state, args);
        text = (string)args[0];
        return result;
    }

    private static bool DeserializePending(string text, out object state)
    {
        object[] args = { text, null };
        bool result = (bool)pendingType.GetMethod("TryDeserialize", AnyStatic).Invoke(null, args);
        state = args[1];
        return result;
    }

    private static object Get(object value, string name) => value.GetType().GetProperty(name, AnyInstance).GetValue(value);
    private static void Set(object value, string name, object propertyValue) => value.GetType().GetProperty(name, AnyInstance).SetValue(value, propertyValue);
    private static void SetEnum(object value, string name, int number) => Set(value, name, Enum.ToObject(value.GetType().GetProperty(name, AnyInstance).PropertyType, number));
    private static object Invoke(object value, string method, params object[] args) => value.GetType().GetMethod(method, AnyInstance).Invoke(value, args);

    private static void Require(bool condition, string name)
    {
        if (!condition) throw new InvalidOperationException("FAILED: " + name);
        passed++;
        Console.WriteLine("PASS " + name);
    }
}
