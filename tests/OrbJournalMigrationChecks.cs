using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using FeedLikeGrandma;

// Source-linked production journal + real isolated filesystem. No Unity, BepInEx,
// live profile/world, player save, RPC or user-owned journal is opened by this runner.
internal static class OrbJournalMigrationChecks
{
    private const long World = 2183557142L;
    private static int _checks;
    private static string _root = "";

    private static int Main(string[] args)
    {
        if (args.Length > 0) return HoldShared(args);
        string temp = Path.GetFullPath(Path.GetTempPath());
        _root = Path.GetFullPath(Path.Combine(temp, "flg-orb-migration-check-" + Guid.NewGuid().ToString("N")));
        if (!_root.StartsWith(temp.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Test output must remain inside the system temporary directory.");
        Directory.CreateDirectory(_root);
        try
        {
            CheckMigrationAndReuse();
            CheckSharedAndFresh();
            CheckConflicts();
            CheckCorruption();
            CheckBusyFilesAndGuards();
            CheckPathFailures();
            CheckConcurrentOpen();
            CheckCrossProcessGuards();
            Console.WriteLine($"PASS: {_checks} production Orb journal migration/filesystem checks.");
            Console.WriteLine("Scope: real temporary files, checksums, world identity, record history, atomic publication, sharing locks and failure cleanup. No actual Unity/profile lifecycle, crash/power-loss durability or multiplayer execution.");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        finally
        {
            // Only our newly-created, absolute, checked directory is removed.
            foreach (string file in Directory.GetFiles(_root, "*", SearchOption.AllDirectories))
                File.SetAttributes(file, FileAttributes.Normal);
            Directory.Delete(_root, true);
        }
    }

    private static void CheckMigrationAndReuse()
    {
        Pair pair = Directories("migration");
        TamingOrbJournal.Entry prior = Entry("prior", TamingOrbJournal.CompletedSuccess);
        Seed(pair.Legacy, World, prior);
        Seed(pair.Legacy, World + 1, Entry("another-world"));
        string config = Path.Combine(pair.Legacy, "..", "feed.yml");
        string icon = Path.Combine(pair.Legacy, "..", "cache", "icons", "Wolf.flgicon");
        Directory.CreateDirectory(Path.GetDirectoryName(icon)!);
        File.WriteAllText(config, "untouched config"); File.WriteAllText(icon, "untouched cache");
        byte[] before = ReadBytes(PathFor(pair.Legacy));
        byte[] otherBefore = ReadBytes(PathFor(pair.Legacy, World + 1));
        using (TamingOrbJournal journal = Open(pair))
        {
            Check(Path.GetFullPath(journal.FilePath) == Path.GetFullPath(PathFor(pair.Shared)), "active journal is the shared file");
            Check(ReadBytes(PathFor(pair.Shared)).SequenceEqual(before), "migration copies exact serialized bytes without rewriting receipts");
            Check(ReadBytes(PathFor(pair.Legacy)).SequenceEqual(before), "legacy journal remains as an unchanged backup");
            Check(journal.TryGet(prior.TransactionId, out var recovered) && recovered!.State == TamingOrbJournal.CompletedSuccess,
                "completed release receipt remains readable after migration");
            Check(journal.TryGetBlockingAction(prior.OrbId, TamingOrbJournal.Release, out _), "consumed Orb remains blocked after migration");
            Check(!journal.TrySave(EntryFor(prior.OrbId, "duplicate"), out _), "migration does not allow another release of consumed Orb");
            Check(!File.Exists(PathFor(pair.Shared, World + 1)), "migration touches only the current world's journal");
            Check(ReadBytes(PathFor(pair.Legacy, World + 1)).SequenceEqual(otherBefore), "other world journal stays unchanged");
            Check(File.ReadAllText(config) == "untouched config" && File.ReadAllText(icon) == "untouched cache", "config and icon cache remain in profile");
        }
        using (var identical = Open(pair))
            Check(identical.IsHealthy, "exact shared and legacy copies reopen without conflict");
        using (var continued = Open(pair)) Save(continued, Entry("new-shared-record"));
        byte[] advanced = ReadBytes(PathFor(pair.Shared));
        Check(advanced.Length > before.Length && advanced.Take(before.Length).SequenceEqual(before), "shared journal extends preserved legacy history");
        using (var again = Open(pair))
        {
            Check(again.Entries.Count() == 2, "unchanged legacy prefix does not block later shared records on restart");
            Save(again, Entry("append-after-prefix-comparison"));
        }
        using (var appended = Open(pair))
            Check(appended.Entries.Count() == 3, "append after prefix comparison writes at shared EOF and preserves full checksum chain");
        Check(ReadBytes(PathFor(pair.Legacy)).SequenceEqual(before), "continued shared use never appends into preserved legacy journal");
        string newProfile = Path.Combine(_root, "new-profile", "orb-transactions");
        using (var switched = Open(new Pair(pair.Shared, newProfile)))
            Check(switched.Entries.Count() == 3, "new profile with no legacy journal reuses common history");
        Check(!Directory.Exists(newProfile), "opening through new profile does not create a misleading legacy journal directory");
    }

    private static void CheckSharedAndFresh()
    {
        Pair shared = Directories("shared-only");
        Seed(shared.Shared, World, Entry("shared-existing"));
        byte[] bytes = ReadBytes(PathFor(shared.Shared));
        using (var journal = Open(shared)) Check(journal.Entries.Count() == 1, "shared-only existing journal loads");
        Check(ReadBytes(PathFor(shared.Shared)).SequenceEqual(bytes), "shared-only open preserves journal bytes");
        Pair fresh = Directories("fresh");
        using (var journal = Open(fresh))
        {
            Check(journal.WorldUid == World && !journal.Entries.Any(), "no legacy/shared files creates an empty journal for requested world");
            Save(journal, Entry("fresh-action"));
        }
        using (var journal = Open(fresh)) Check(journal.Entries.Count() == 1, "newly created common journal survives reopen");
        string aliasDir = Path.Combine(_root, "same-directory");
        Seed(aliasDir, World, Entry("same-path"));
        using (var alias = Open(new Pair(aliasDir, Path.Combine(aliasDir, "."))))
            Check(alias.Entries.Count() == 1, "identical normalized shared/legacy path uses one journal without self-lock failure");
    }

    private static void CheckConflicts()
    {
        Pair longer = Directories("legacy-longer");
        Seed(longer.Shared, World, Entry("base"));
        Directory.CreateDirectory(longer.Legacy);
        File.Copy(PathFor(longer.Shared), PathFor(longer.Legacy));
        Append(longer.Legacy, Entry("legacy-new-action"));
        RejectPreserving(longer, "legacy extending shared history is not automatically merged or preferred");
        Pair branch = Directories("divergent");
        Seed(branch.Shared, World, Entry("common-branch"));
        Seed(branch.Legacy, World, Entry("profile-branch"));
        File.SetLastWriteTimeUtc(PathFor(branch.Legacy), DateTime.UtcNow.AddHours(2));
        RejectPreserving(branch, "newer timestamp never chooses between divergent valid journals");
        Pair fork = Directories("same-transaction-divergence");
        TamingOrbJournal.Entry common = Entry("fork-common");
        Seed(fork.Shared, World, common);
        var different = common.Copy(); different.State = TamingOrbJournal.CompletedFailure;
        Seed(fork.Legacy, World, different);
        RejectPreserving(fork, "same transaction identity with different outcome remains a conflict");
        // A failed check must release both streams/lock so a corrected import can proceed.
        File.Copy(PathFor(branch.Shared), PathFor(branch.Legacy), true);
        using (var repaired = Open(branch)) Check(repaired.IsHealthy, "conflict failure releases migration guards for later valid open");
    }

    private static void CheckCorruption()
    {
        foreach (string name in new[] { "empty", "truncated", "checksum", "wrong-world", "trailing" })
        {
            Pair source = Directories("bad-source-" + name);
            Seed(source.Legacy, name == "wrong-world" ? World + 1 : World, Entry("source"));
            string path = PathFor(source.Legacy);
            if (name == "wrong-world") File.Copy(PathFor(source.Legacy, World + 1), path);
            else Corrupt(path, name);
            RejectPreserving(source, "invalid legacy " + name + " is preserved and never publishes an empty/shared replacement");
            Check(!File.Exists(PathFor(source.Shared)), "invalid source leaves destination journal absent: " + name);

            Pair target = Directories("bad-target-" + name);
            Seed(target.Legacy, World, Entry("legacy-valid"));
            Seed(target.Shared, name == "wrong-world" ? World + 1 : World, Entry("target"));
            path = PathFor(target.Shared);
            if (name == "wrong-world") File.Copy(PathFor(target.Shared, World + 1), path);
            else Corrupt(path, name);
            RejectPreserving(target, "invalid shared " + name + " is never replaced by valid legacy backup");
        }
    }

    private static void CheckBusyFilesAndGuards()
    {
        Pair pair = Directories("busy-source"); Seed(pair.Legacy, World, Entry("busy"));
        using (var writer = new FileStream(PathFor(pair.Legacy), FileMode.Open, FileAccess.ReadWrite, FileShare.Read))
        {
            Reject(pair, "active old-profile writer prevents migration");
            Check(!File.Exists(PathFor(pair.Shared)), "source lock failure never creates shared journal");
        }
        using (var journal = Open(pair))
        {
            Check(OpenForWriteFails(PathFor(pair.Legacy)), "active shared journal holds original legacy read guard against old-version writes");
            Check(OpenForWriteFails(PathFor(pair.Shared)), "active shared journal keeps single-writer protection");
            Reject(pair, "second shared open is blocked for entire journal lifetime");
            var noLegacy = new Pair(pair.Shared, Path.Combine(_root, "other-profile-no-legacy"));
            Reject(noLegacy, "another profile cannot bypass common single-writer guard");
        }
        using (new FileStream(PathFor(pair.Legacy), FileMode.Open, FileAccess.ReadWrite, FileShare.Read))
            Check(true, "disposing shared journal releases preserved legacy guard");
        using (var again = Open(pair)) Check(again.IsHealthy, "disposing shared journal releases common guard");
        Pair busyTarget = Directories("busy-target"); Seed(busyTarget.Shared, World, Entry("active-common"));
        using (var writer = new FileStream(PathFor(busyTarget.Shared), FileMode.Open, FileAccess.ReadWrite, FileShare.Read))
            Reject(busyTarget, "existing target writer cannot be bypassed by migration lock");
        using (var again = Open(busyTarget)) Check(again.IsHealthy, "target lock failure releases migration guard");
    }

    private static void CheckPathFailures()
    {
        Pair directorySource = Directories("source-is-directory");
        Directory.CreateDirectory(PathFor(directorySource.Legacy));
        Reject(directorySource, "directory at legacy journal path is an error, not missing history");
        Check(!File.Exists(PathFor(directorySource.Shared)), "directory source failure does not create blank common history");
        Pair blockedSource = Directories("source-parent-is-file");
        Directory.CreateDirectory(Path.GetDirectoryName(blockedSource.Legacy)!);
        File.WriteAllText(blockedSource.Legacy, "not a directory");
        Reject(blockedSource, "legacy parent occupied by file is not treated as absent profile history");
        Check(!File.Exists(PathFor(blockedSource.Shared)), "invalid source parent does not create blank common history");
        Pair directoryTarget = Directories("target-is-directory");
        Seed(directoryTarget.Legacy, World, Entry("source-valid"));
        Directory.CreateDirectory(PathFor(directoryTarget.Shared));
        Reject(directoryTarget, "directory at target journal path never overwritten");
        Pair blockedTarget = Directories("target-parent-is-file");
        Seed(blockedTarget.Legacy, World, Entry("source-valid"));
        Directory.CreateDirectory(Path.GetDirectoryName(blockedTarget.Shared)!);
        File.WriteAllText(blockedTarget.Shared, "not a directory");
        Reject(blockedTarget, "target creation failure does not fall back to writing old profile");
        Pair readOnly = Directories("readonly-source"); Seed(readOnly.Legacy, World, Entry("readonly"));
        byte[] before = ReadBytes(PathFor(readOnly.Legacy));
        File.SetAttributes(PathFor(readOnly.Legacy), FileAttributes.ReadOnly);
        try
        {
            using (var migrated = Open(readOnly)) Check(migrated.Entries.Count() == 1, "read-only legacy can be imported without write access");
            Check(ReadBytes(PathFor(readOnly.Legacy)).SequenceEqual(before), "read-only legacy contents remain unchanged");
        }
        finally { File.SetAttributes(PathFor(readOnly.Legacy), FileAttributes.Normal); }
        Pair deniedTarget = Directories("readonly-target"); Seed(deniedTarget.Shared, World, Entry("readonly-target"));
        File.SetAttributes(PathFor(deniedTarget.Shared), FileAttributes.ReadOnly);
        try { RejectPreserving(deniedTarget, "read-only shared journal fails instead of silently switching storage location"); }
        finally { File.SetAttributes(PathFor(deniedTarget.Shared), FileAttributes.Normal); }
    }

    private static void CheckConcurrentOpen()
    {
        Pair pair = Directories("concurrent-migration"); Seed(pair.Legacy, World, Entry("concurrent-source"));
        byte[] original = ReadBytes(PathFor(pair.Legacy));
        TamingOrbJournal?[] journals = new TamingOrbJournal?[2];
        bool[] opened = new bool[2]; string[] errors = new string[2];
        using (Barrier start = new(2))
        {
            Task[] workers = Enumerable.Range(0, 2).Select(index => Task.Run(() =>
            {
                if (!start.SignalAndWait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Fixture start barrier");
                opened[index] = TamingOrbJournal.TryOpenShared(pair.Shared, pair.Legacy, World, out journals[index], out _, out errors[index]);
            })).ToArray();
            try
            {
                if (!Task.WaitAll(workers, TimeSpan.FromSeconds(15))) throw new TimeoutException("Concurrent fixture did not complete");
                Check(opened.Count(value => value) == 1, "concurrent migration grants exactly one live writer");
                Check(errors.Where((_, index) => !opened[index]).All(error => !string.IsNullOrEmpty(error)), "concurrent losing opener receives an explicit failure");
                Check(ReadBytes(PathFor(pair.Shared)).SequenceEqual(original), "concurrent publication preserves exact source history");
                Check(ReadBytes(PathFor(pair.Legacy)).SequenceEqual(original), "concurrent migration preserves profile source");
            }
            finally { foreach (var journal in journals) journal?.Dispose(); }
        }
        using (var final = Open(pair)) Check(final.IsHealthy, "shared journal is usable after concurrent winner closes");
    }

    private static int HoldShared(string[] args)
    {
        if (args.Length != 5 || args[0] != "hold-shared") throw new ArgumentException("Unknown test child mode.");
        if (!TamingOrbJournal.TryOpenShared(args[1], args[2], World, out var journal, out _, out string error) || journal == null)
            throw new InvalidOperationException("Child shared open failed: " + error);
        using (journal)
        {
            File.WriteAllText(args[3], "ready");
            Stopwatch deadline = Stopwatch.StartNew();
            while (!File.Exists(args[4]))
            {
                if (deadline.Elapsed > TimeSpan.FromSeconds(20)) throw new TimeoutException("Parent did not release child fixture.");
                Thread.Sleep(20);
            }
        }
        return 0;
    }

    private static void CheckCrossProcessGuards()
    {
        Pair pair = Directories("separate-process"); Seed(pair.Legacy, World, Entry("process-source"));
        string ready = Path.Combine(_root, "child-ready"), release = Path.Combine(_root, "child-release");
        string[] arguments = { "hold-shared", pair.Shared, pair.Legacy, ready, release };
        string? python = Environment.GetEnvironmentVariable("FLG_ORB_MIGRATION_PYTHON");
        var start = new ProcessStartInfo { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        if (Type.GetType("Mono.Runtime") != null)
        {
            if (string.IsNullOrEmpty(python)) throw new InvalidOperationException("Original Mono cross-process check requires the supplied Python host.");
            start.FileName = python;
            start.Arguments = string.Join(" ", new[] { Environment.GetEnvironmentVariable("FLG_ORB_MIGRATION_RUNNER")!, Environment.GetEnvironmentVariable("FLG_ORB_MIGRATION_GAME")! }.Concat(arguments).Select(Quote));
        }
        else
        {
            start.FileName = Assembly.GetExecutingAssembly().Location;
            start.Arguments = string.Join(" ", arguments.Select(Quote));
        }
        using Process child = Process.Start(start) ?? throw new InvalidOperationException("Could not start lock fixture child.");
        try
        {
            Stopwatch deadline = Stopwatch.StartNew();
            while (!File.Exists(ready))
            {
                if (child.HasExited) throw new InvalidOperationException("Child failed before lock: " + child.StandardError.ReadToEnd());
                if (deadline.Elapsed > TimeSpan.FromSeconds(10)) throw new TimeoutException("Child never acquired lock.");
                Thread.Sleep(20);
            }
            Reject(pair, "a separate process cannot acquire an active shared lease");
            Check(OpenForWriteFails(PathFor(pair.Legacy)), "legacy read guard excludes an old-version writer in a separate process");
            Check(OpenForWriteFails(PathFor(pair.Shared)), "shared writer excludes direct writers in a separate process");
            File.WriteAllText(release, "release");
            if (!child.WaitForExit(10000)) throw new TimeoutException("Child did not close shared journal.");
            Check(child.ExitCode == 0, "cross-process journal owner closes successfully");
            using var reopened = Open(pair);
            Check(reopened.IsHealthy, "journal reopens after separate owner process closes");
        }
        finally
        {
            if (!child.HasExited)
            {
                File.WriteAllText(release, "release");
                if (!child.WaitForExit(5000)) { child.Kill(); child.WaitForExit(5000); }
            }
        }
    }

    private static string Quote(string value)
    {
        // ProcessStartInfo uses the Windows argv escaping convention, not a shell.
        StringBuilder text = new("\"");
        int slashes = 0;
        foreach (char character in value)
        {
            if (character == '\\') { slashes++; continue; }
            text.Append('\\', character == '"' ? slashes * 2 + 1 : slashes);
            text.Append(character); slashes = 0;
        }
        return text.Append('\\', slashes * 2).Append('"').ToString();
    }

    private static void Corrupt(string path, string kind)
    {
        byte[] bytes = ReadBytes(path);
        if (kind == "empty") bytes = Array.Empty<byte>();
        else if (kind == "truncated") bytes = bytes.Take(bytes.Length - 1).ToArray();
        else if (kind == "checksum") bytes[bytes.Length - 1] ^= 0x7F;
        else if (kind == "trailing") bytes = bytes.Concat(new byte[] { 0x01 }).ToArray();
        File.WriteAllBytes(path, bytes);
    }

    private static bool OpenForWriteFails(string path)
    {
        try { using var ignored = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read); return false; }
        catch (IOException) { return true; }
    }

    private static byte[] ReadBytes(string path)
    {
        // A journal writer allows readers. The reader must reciprocally allow the
        // already-open writer; File.ReadAllBytes' FileShare.Read does not do that.
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var copy = new MemoryStream();
        stream.CopyTo(copy);
        return copy.ToArray();
    }

    private static void RejectPreserving(Pair pair, string description)
    {
        string source = PathFor(pair.Legacy), target = PathFor(pair.Shared);
        byte[]? beforeSource = File.Exists(source) ? ReadBytes(source) : null;
        byte[]? beforeTarget = File.Exists(target) ? ReadBytes(target) : null;
        Reject(pair, description);
        Check(beforeSource == null ? !File.Exists(source) : ReadBytes(source).SequenceEqual(beforeSource), description + ": legacy preserved");
        Check(beforeTarget == null ? !File.Exists(target) : ReadBytes(target).SequenceEqual(beforeTarget), description + ": shared preserved");
    }

    private static void Reject(Pair pair, string description)
    {
        bool opened = TamingOrbJournal.TryOpenShared(pair.Shared, pair.Legacy, World, out var journal, out _, out string error);
        journal?.Dispose();
        Check(!opened && journal == null && !string.IsNullOrWhiteSpace(error), description);
    }

    private static TamingOrbJournal Open(Pair pair)
    {
        if (!TamingOrbJournal.TryOpenShared(pair.Shared, pair.Legacy, World, out var journal, out string status, out string error) || journal == null)
            throw new InvalidOperationException("Expected valid shared journal: " + error);
        Check(journal.IsHealthy && !string.IsNullOrWhiteSpace(status) && error.Length == 0, "open shared journal: " + status);
        return journal;
    }

    private static void Seed(string directory, long world, TamingOrbJournal.Entry entry)
    {
        if (!TamingOrbJournal.TryOpen(directory, world, out var journal, out string error) || journal == null) throw new InvalidOperationException(error);
        using (journal) Save(journal, entry);
    }

    private static void Append(string directory, TamingOrbJournal.Entry entry) => Seed(directory, World, entry);
    private static void Save(TamingOrbJournal journal, TamingOrbJournal.Entry entry)
    {
        if (!journal.TrySave(entry, out string error)) throw new InvalidOperationException("Fixture receipt rejected: " + error);
    }
    private static TamingOrbJournal.Entry Entry(string data, string state = TamingOrbJournal.Reserved) => new()
    {
        TransactionId = Guid.NewGuid().ToString("N"), OrbId = Guid.NewGuid().ToString("N"),
        Kind = TamingOrbJournal.Release, State = state,
        RequestData = Convert.ToBase64String(Encoding.UTF8.GetBytes(data)),
        ResultData = state == TamingOrbJournal.CompletedSuccess ? Convert.ToBase64String(new byte[] { 1, 2, 3 }) : ""
    };
    private static TamingOrbJournal.Entry EntryFor(string orb, string data) { var entry = Entry(data); entry.OrbId = orb; return entry; }
    private static Pair Directories(string name) => new(Path.Combine(_root, name, "common"), Path.Combine(_root, name, "profile", "orb-transactions"));
    private static string PathFor(string directory, long world = World) => Path.Combine(directory, world.ToString(CultureInfo.InvariantCulture) + ".journal");
    private static void Check(bool condition, string name) { if (!condition) throw new InvalidOperationException("FAIL: " + name); _checks++; }
    private readonly struct Pair
    {
        internal readonly string Shared, Legacy;
        internal Pair(string shared, string legacy) { Shared = shared; Legacy = legacy; }
    }
}
