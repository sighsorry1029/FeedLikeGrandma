using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace FeedLikeGrandma;

// Durable receipts for one world. Network handlers decide what a receipt means for
// the current world; loading a receipt never authorizes another spawn or deletion.
// Completed receipts do not expire. A checksum chain detects damaged/incomplete
// writes, but cannot detect restoring an older intact journal with a world backup.
internal sealed class TamingOrbJournal : IDisposable
{
    internal const string Capture = "capture";
    internal const string Release = "release";
    internal const string Reserved = "Reserved";
    internal const string Committing = "Committing";
    internal const string CompletedSuccess = "CompletedSuccess";
    internal const string CompletedFailure = "CompletedFailure";

    private const int DigestLength = 32;
    private const int MaximumEntryBytes = 16 * 1024 * 1024;
    private static readonly byte[] Magic = Encoding.ASCII.GetBytes("FLGOrbJournal1\0");
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private readonly object _gate = new();
    private readonly FileStream _stream;
    private FileStream? _sharedLease;
    private FileStream? _legacyGuard;
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Entry> _blockingReleases = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Entry> _pendingCaptures = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Entry> _capturedOrbs = new(StringComparer.Ordinal);
    private byte[] _lastDigest = Array.Empty<byte>();
    private bool _healthy = true;
    private string _error = string.Empty;

    internal sealed class Entry
    {
        public string OrbId { get; set; } = string.Empty;
        public string TransactionId { get; set; } = string.Empty;
        public string Kind { get; set; } = string.Empty;
        public string State { get; set; } = string.Empty;
        public string RequestData { get; set; } = string.Empty;
        public string PreparedData { get; set; } = string.Empty;
        public string ResultData { get; set; } = string.Empty;

        internal Entry Copy() => (Entry)MemberwiseClone();
    }

    private TamingOrbJournal(FileStream stream, long worldUid, bool created)
    {
        _stream = stream;
        WorldUid = worldUid;
        Load(created);
    }

    internal long WorldUid { get; }
    internal string FilePath => _stream.Name;

    internal bool IsHealthy
    {
        get { lock (_gate) { return _healthy; } }
    }

    internal string Error
    {
        get { lock (_gate) { return _error; } }
    }

    internal IEnumerable<Entry> Entries
    {
        get
        {
            lock (_gate)
            {
                List<Entry> entries = new(_entries.Count);
                foreach (Entry entry in _entries.Values)
                {
                    entries.Add(entry.Copy());
                }

                return entries;
            }
        }
    }

    internal static bool TryOpen(
        string directory,
        long worldUid,
        out TamingOrbJournal? journal,
        out string error)
    {
        journal = null;
        FileStream? stream = null;
        try
        {
            Directory.CreateDirectory(directory);
            string path = Path.Combine(directory, worldUid.ToString(CultureInfo.InvariantCulture) + ".journal");
            bool created = !File.Exists(path);
            // A second server/process may read diagnostics but cannot write the same journal.
            stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read);
            journal = new TamingOrbJournal(stream, worldUid, created);
            error = string.Empty;
            return true;
        }
        catch (Exception ex)
        {
            stream?.Dispose();
            error = "Orb journal could not be opened: " + ex.Message;
            return false;
        }
    }

    // A profile's old journal remains a backup. Once the shared journal advances,
    // that backup must be an exact, fully validated prefix, never a separate branch.
    // The lease serializes import/open and remains held while this world is active.
    internal static bool TryOpenShared(
        string sharedDirectory,
        string legacyDirectory,
        long worldUid,
        out TamingOrbJournal? journal,
        out string status,
        out string error)
    {
        journal = null;
        status = "blocked";
        error = string.Empty;
        FileStream? lease = null, legacy = null, shared = null;
        string? temporaryPath = null;
        bool ownsTemporary = false;
        try
        {
            string filename = worldUid.ToString(CultureInfo.InvariantCulture) + ".journal";
            string sharedPath = Path.GetFullPath(Path.Combine(sharedDirectory, filename));
            string legacyPath = Path.GetFullPath(Path.Combine(legacyDirectory, filename));
            StringComparison comparison = Path.DirectorySeparatorChar == '\\'
                ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            bool samePath = sharedPath.Equals(legacyPath, comparison);
            Directory.CreateDirectory(Path.GetDirectoryName(sharedPath)!);
            // Keep the lock file after closing: deleting it would let another
            // process lock a new inode while an existing lease still uses the old one.
            lease = new FileStream(sharedPath + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            shared = OpenExistingJournal(sharedPath, FileAccess.ReadWrite);
            if (!samePath) legacy = OpenExistingJournal(legacyPath, FileAccess.Read);

            // Reuse the journal's full format, world, checksum and transition checks.
            // This validator owns no lease and is discarded while its stream is
            // retained below to prevent a parallel legacy writer in this profile.
            if (legacy != null) _ = new TamingOrbJournal(legacy, worldUid, created: false);
            if (shared != null)
            {
                var opened = new TamingOrbJournal(shared, worldUid, created: false);
                if (legacy != null && !IsExactPrefix(legacy, shared))
                    throw new InvalidDataException("Legacy and shared Orb journals conflict. The legacy file is not a prefix of the shared history; neither file was replaced or merged.");
                journal = opened;
                status = samePath ? "same_path" : legacy == null ? "shared_existing" : "shared_with_legacy_prefix";
            }
            else if (legacy != null)
            {
                temporaryPath = sharedPath + ".import-" + Guid.NewGuid().ToString("N") + ".tmp";
                using (var temporary = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
                {
                    ownsTemporary = true;
                    legacy.Position = 0;
                    legacy.CopyTo(temporary);
                    temporary.Flush(flushToDisk: true);
                    temporary.Position = 0;
                    _ = new TamingOrbJournal(temporary, worldUid, created: false);
                }
                // Same-directory publication is atomic and refuses to overwrite a
                // target that appeared after the initial open attempt.
                File.Move(temporaryPath, sharedPath);
                ownsTemporary = false;
                shared = new FileStream(sharedPath, FileMode.Open, FileAccess.ReadWrite, FileShare.Read);
                journal = new TamingOrbJournal(shared, worldUid, created: false);
                status = "migrated";
            }
            else
            {
                // Only a genuinely absent journal starts a new history. An empty,
                // malformed, inaccessible or conflicting existing file never does.
                shared = new FileStream(sharedPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read);
                journal = new TamingOrbJournal(shared, worldUid, created: true);
                status = samePath ? "same_path" : "created_new";
            }

            journal._sharedLease = lease;
            journal._legacyGuard = legacy;
            shared = null; legacy = null; lease = null;
            return true;
        }
        catch (Exception ex)
        {
            journal = null;
            status = "blocked";
            error = "Orb journal storage could not be opened: " + ex.Message;
            return false;
        }
        finally
        {
            CloseQuietly(shared);
            CloseQuietly(legacy);
            CloseQuietly(lease);
            if (ownsTemporary && temporaryPath != null)
            {
                try { File.Delete(temporaryPath); }
                catch (Exception) { /* Only this invocation's unpublished copy; never delete a journal. */ }
            }
        }
    }

    private static FileStream? OpenExistingJournal(string path, FileAccess access)
    {
        try { return new FileStream(path, FileMode.Open, access, FileShare.Read); }
        catch (FileNotFoundException) { RequireMissingPath(path); return null; }
        catch (DirectoryNotFoundException) { RequireMissingPath(path); return null; }
    }

    private static void RequireMissingPath(string path)
    {
        // File.Exists masks access/I/O errors. Also, FileStream may report a
        // missing directory when an ancestor is actually a regular file.
        string? current = path;
        while (current != null)
        {
            try
            {
                FileAttributes attributes = File.GetAttributes(current);
                if (current == path || (attributes & FileAttributes.Directory) == 0)
                    throw new IOException("Orb journal path exists but cannot be opened as a file: " + path);
                return;
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
            current = Path.GetDirectoryName(current);
        }
        throw new DirectoryNotFoundException("Orb journal filesystem root is unavailable: " + path);
    }

    private static bool IsExactPrefix(FileStream legacy, FileStream shared)
    {
        if (legacy.Length > shared.Length) return false;
        byte[] left = new byte[64 * 1024], right = new byte[64 * 1024];
        legacy.Position = 0;
        shared.Position = 0;
        try
        {
            long remaining = legacy.Length;
            while (remaining > 0)
            {
                int count = legacy.Read(left, 0, (int)Math.Min(left.Length, remaining));
                if (count == 0) throw new EndOfStreamException("Legacy Orb journal changed during comparison.");
                int read = 0;
                while (read < count)
                {
                    int part = shared.Read(right, read, count - read);
                    if (part == 0) throw new EndOfStreamException("Shared Orb journal changed during comparison.");
                    read += part;
                }
                for (int i = 0; i < count; i++) if (left[i] != right[i]) return false;
                remaining -= count;
            }
            return true;
        }
        finally
        {
            // TrySave appends at the current stream position. Never leave it at
            // the end of the shorter legacy prefix and overwrite newer receipts.
            legacy.Position = legacy.Length;
            shared.Position = shared.Length;
        }
    }

    private static void CloseQuietly(FileStream? stream)
    {
        try { stream?.Dispose(); }
        catch (Exception) { }
    }

    internal bool TryGet(string transactionId, out Entry? entry)
    {
        lock (_gate)
        {
            if (_entries.TryGetValue(transactionId, out Entry? stored))
            {
                entry = stored.Copy();
                return true;
            }

            entry = null;
            return false;
        }
    }

    internal bool TryGetBlockingRelease(string orbId, out Entry? entry)
    {
        lock (_gate)
        {
            if (_blockingReleases.TryGetValue(orbId, out Entry? stored))
            {
                entry = stored.Copy();
                return true;
            }

            entry = null;
            return false;
        }
    }

    internal bool TryGetBlockingAction(string orbId, string nextKind, out Entry? entry)
    {
        lock (_gate)
        {
            bool found = TryGetBlockingActionCore(orbId, nextKind, out Entry? stored);
            entry = stored?.Copy();
            return found;
        }
    }

    internal bool TrySave(Entry entry, out string error)
    {
        lock (_gate)
        {
            if (!_healthy)
            {
                error = _error;
                return false;
            }

            Entry copy;
            byte[] bytes;
            try
            {
                copy = entry.Copy();
                Validate(copy);
                ValidateUpdate(copy);
                if (_entries.TryGetValue(copy.TransactionId, out Entry? previous) && Equal(previous, copy))
                {
                    error = string.Empty;
                    return true;
                }

                bytes = Serialize(copy);
            }
            catch (Exception ex)
            {
                error = "Orb journal rejected the receipt: " + ex.Message;
                return false;
            }

            try
            {
                byte[] digest = HashRecord(_lastDigest, bytes);
                using (BinaryWriter writer = new(_stream, StrictUtf8, leaveOpen: true))
                {
                    writer.Write(bytes.Length);
                    writer.Write(bytes);
                    writer.Write(digest);
                    writer.Flush();
                }

                // Acknowledging a write before this succeeds could authorize a second
                // creature after a restart. Never update the in-memory index first.
                _stream.Flush(flushToDisk: true);
                Apply(copy);
                _lastDigest = digest;
                error = string.Empty;
                return true;
            }
            catch (Exception ex)
            {
                _healthy = false;
                _error = "Orb journal write failed; further actions are blocked: " + ex.Message;
                error = _error;
                return false;
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _healthy = false;
            if (string.IsNullOrEmpty(_error))
            {
                _error = "Orb journal is closed.";
            }

            try { _stream.Dispose(); }
            finally
            {
                try { _legacyGuard?.Dispose(); }
                finally
                {
                    _legacyGuard = null;
                    _sharedLease?.Dispose();
                    _sharedLease = null;
                }
            }
        }
    }

    private void Load(bool created)
    {
        byte[] header;
        using (MemoryStream memory = new())
        {
            using (BinaryWriter writer = new(memory, StrictUtf8, leaveOpen: true))
            {
                writer.Write(Magic);
                writer.Write(WorldUid);
            }

            header = memory.ToArray();
        }

        byte[] headerDigest = Hash(header);
        if (created && _stream.Length == 0)
        {
            _stream.Write(header, 0, header.Length);
            _stream.Write(headerDigest, 0, headerDigest.Length);
            _stream.Flush(flushToDisk: true);
            _lastDigest = headerDigest;
            return;
        }

        using BinaryReader reader = new(_stream, StrictUtf8, leaveOpen: true);
        if (!EqualBytes(ReadExactly(reader, header.Length), header) ||
            !EqualBytes(ReadExactly(reader, DigestLength), headerDigest))
        {
            throw new InvalidDataException("Unsupported, damaged, or wrong-world journal header.");
        }

        _lastDigest = headerDigest;
        while (_stream.Position < _stream.Length)
        {
            int length = reader.ReadInt32();
            if (length <= 0 || length > MaximumEntryBytes)
            {
                throw new InvalidDataException("Invalid journal receipt length.");
            }

            byte[] bytes = ReadExactly(reader, length);
            byte[] digest = ReadExactly(reader, DigestLength);
            if (!EqualBytes(HashRecord(_lastDigest, bytes), digest))
            {
                throw new InvalidDataException("Journal checksum mismatch; automatic recovery is disabled.");
            }

            Entry entry = Deserialize(bytes);
            Validate(entry);
            ValidateUpdate(entry);
            Apply(entry);
            _lastDigest = digest;
        }
    }

    private bool TryGetBlockingActionCore(string orbId, string nextKind, out Entry? entry)
    {
        if (nextKind != Capture && nextKind != Release)
        {
            throw new ArgumentException("Unknown orb action kind.", nameof(nextKind));
        }

        return _blockingReleases.TryGetValue(orbId, out entry) ||
               _pendingCaptures.TryGetValue(orbId, out entry) ||
               (nextKind == Capture && _capturedOrbs.TryGetValue(orbId, out entry));
    }

    private void ValidateUpdate(Entry entry)
    {
        if (!_entries.TryGetValue(entry.TransactionId, out Entry? previous))
        {
            if (TryGetBlockingActionCore(entry.OrbId, entry.Kind, out _))
            {
                throw new InvalidDataException("Another receipt already reserves or consumes this orb identity.");
            }

            return;
        }

        if (previous.OrbId != entry.OrbId || previous.Kind != entry.Kind || previous.RequestData != entry.RequestData)
        {
            throw new InvalidDataException("A transaction's identity and request cannot change.");
        }

        if ((previous.State == CompletedSuccess || previous.State == CompletedFailure) && !Equal(previous, entry))
        {
            throw new InvalidDataException("A completed transaction cannot be rewritten.");
        }

        if (previous.State == Committing && entry.State == Reserved)
        {
            throw new InvalidDataException("A committing transaction cannot return to a reservation.");
        }

        if (!string.IsNullOrEmpty(previous.PreparedData) && previous.PreparedData != entry.PreparedData)
        {
            throw new InvalidDataException("Prepared creature data cannot change within a transaction.");
        }
    }

    private void Apply(Entry entry)
    {
        _entries[entry.TransactionId] = entry;
        if (entry.Kind == Release)
        {
            if (entry.State == CompletedFailure)
            {
                _blockingReleases.Remove(entry.OrbId);
            }
            else
            {
                _blockingReleases[entry.OrbId] = entry;
            }
        }
        else if (entry.State == Reserved || entry.State == Committing)
        {
            _pendingCaptures[entry.OrbId] = entry;
        }
        else
        {
            _pendingCaptures.Remove(entry.OrbId);
            if (entry.State == CompletedSuccess)
            {
                _capturedOrbs[entry.OrbId] = entry;
            }
        }
    }

    private static void Validate(Entry entry)
    {
        if (!Guid.TryParseExact(entry.OrbId, "N", out _) ||
            !Guid.TryParseExact(entry.TransactionId, "N", out _) ||
            (entry.Kind != Capture && entry.Kind != Release) ||
            (entry.State != Reserved && entry.State != Committing &&
             entry.State != CompletedSuccess && entry.State != CompletedFailure))
        {
            throw new InvalidDataException("Invalid orb receipt identity, kind, or state.");
        }

        ValidateData(entry.RequestData, required: true);
        ValidateData(entry.PreparedData, required: false);
        ValidateData(entry.ResultData, required: false);
    }

    private static void ValidateData(string value, bool required)
    {
        if (value == null || (required && value.Length == 0) || value.Length > MaximumEntryBytes)
        {
            throw new InvalidDataException("Invalid receipt package size.");
        }

        if (value.Length != 0)
        {
            _ = Convert.FromBase64String(value);
        }
    }

    private static byte[] Serialize(Entry entry)
    {
        using MemoryStream memory = new();
        using (BinaryWriter writer = new(memory, StrictUtf8, leaveOpen: true))
        {
            writer.Write((byte)1);
            writer.Write(entry.OrbId);
            writer.Write(entry.TransactionId);
            writer.Write(entry.Kind);
            writer.Write(entry.State);
            writer.Write(entry.RequestData);
            writer.Write(entry.PreparedData);
            writer.Write(entry.ResultData);
        }

        if (memory.Length > MaximumEntryBytes)
        {
            throw new InvalidDataException("Orb receipt is too large.");
        }

        return memory.ToArray();
    }

    private static Entry Deserialize(byte[] bytes)
    {
        using MemoryStream memory = new(bytes, writable: false);
        using BinaryReader reader = new(memory, StrictUtf8);
        if (reader.ReadByte() != 1)
        {
            throw new InvalidDataException("Unsupported orb receipt version.");
        }

        Entry entry = new()
        {
            OrbId = reader.ReadString(),
            TransactionId = reader.ReadString(),
            Kind = reader.ReadString(),
            State = reader.ReadString(),
            RequestData = reader.ReadString(),
            PreparedData = reader.ReadString(),
            ResultData = reader.ReadString()
        };
        if (memory.Position != memory.Length)
        {
            throw new InvalidDataException("Unexpected data at the end of an orb receipt.");
        }

        return entry;
    }

    private static byte[] ReadExactly(BinaryReader reader, int length)
    {
        byte[] bytes = reader.ReadBytes(length);
        if (bytes.Length != length)
        {
            throw new InvalidDataException("Truncated orb journal; automatic recovery is disabled.");
        }

        return bytes;
    }

    private static byte[] HashRecord(byte[] previous, byte[] bytes)
    {
        using MemoryStream memory = new(previous.Length + sizeof(int) + bytes.Length);
        using (BinaryWriter writer = new(memory, StrictUtf8, leaveOpen: true))
        {
            writer.Write(previous);
            writer.Write(bytes.Length);
            writer.Write(bytes);
        }

        return Hash(memory.ToArray());
    }

    private static byte[] Hash(byte[] bytes)
    {
        using SHA256 hash = SHA256.Create();
        return hash.ComputeHash(bytes);
    }

    private static bool EqualBytes(byte[] left, byte[] right)
    {
        if (left.Length != right.Length)
        {
            return false;
        }

        for (int i = 0; i < left.Length; i++)
        {
            if (left[i] != right[i])
            {
                return false;
            }
        }

        return true;
    }

    private static bool Equal(Entry left, Entry right)
    {
        return left.OrbId == right.OrbId && left.TransactionId == right.TransactionId &&
               left.Kind == right.Kind && left.State == right.State &&
               left.RequestData == right.RequestData && left.PreparedData == right.PreparedData &&
               left.ResultData == right.ResultData;
    }
}
