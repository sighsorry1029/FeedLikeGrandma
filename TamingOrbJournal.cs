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

            _stream.Dispose();
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
