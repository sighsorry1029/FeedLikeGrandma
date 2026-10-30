#nullable disable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace FeedLikeGrandma.OrbDiagnostics
{
    // Independent, read-only wire inspection. Never calls FLG deserializers or game APIs.
    internal static class OrbDataReader
    {
        internal const int MaxDecodedBytes = 4 * 1024 * 1024;
        private const int SnapshotLimit = 1024 * 1024;
        private const int RequestLimit = 8 * 1024 * 1024;
        private const string PayloadKey = "flg.tame.payload";
        private const string CmKey = "flg.tame.creaturemanager";
        private const string OrbKey = "flg.tame.orbId";
        private const string PendingKey = "flg.tame.pending";
        private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

        internal static Dictionary<string, string> Read(Dictionary<string, string> customData)
        {
            var facts = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (string key in new[] { OrbKey, PayloadKey, CmKey, PendingKey })
            {
                bool present = customData.TryGetValue(key, out string value);
                facts["keys." + key + ".present"] = Bool(present);
                if (present) Summarize(facts, "keys." + key, value);
            }

            bool hasOrb = customData.TryGetValue(OrbKey, out string orbId);
            facts["orbId.value"] = hasOrb ? Display(orbId) : "<absent>";
            facts["orbId.validGuidN"] = Bool(hasOrb && Guid.TryParseExact(orbId, "N", out _));
            facts["inspection.decodedByteLimit"] = MaxDecodedBytes.ToString(Invariant);
            if (customData.TryGetValue(PayloadKey, out string payload)) ReadPayload(payload, facts, "payload");
            else facts["payload.status"] = "absent";
            if (customData.TryGetValue(PendingKey, out string pending)) ReadPending(pending, customData, facts);
            else facts["pending.status"] = "absent";
            return facts;
        }

        internal static Dictionary<string, string> ReadRecoveryReply(byte[] bytes)
        {
            var facts = new Dictionary<string, string>(StringComparer.Ordinal);
            Inspect(facts, "recovery", () =>
            {
                using (var reader = OpenBytes(bytes))
                {
                    facts["recovery.worldUid"] = reader.Reader.ReadInt64().ToString(Invariant);
                    string tx = reader.String(), orb = reader.String(), state = reader.String(), prepared = reader.String();
                    facts["recovery.transactionId"] = Display(tx);
                    facts["recovery.orbId"] = Display(orb);
                    facts["recovery.state"] = Display(state);
                    facts["recovery.knownState"] = Bool(state == "unknown" || state == "Reserved" || state == "Committing" ||
                        state == "CompletedSuccess" || state == "CompletedFailure");
                    facts["recovery.trailingBytes"] = reader.Remaining.ToString(Invariant);
                    Summarize(facts, "recovery.prepared", prepared);
                    if (prepared.Length == 0) facts["recovery.prepared.status"] = "empty";
                    else ReadPrepared(prepared, facts, "recovery.prepared", null, tx);
                    // Production TryRead does not reject trailing bytes or unknown state text.
                    facts["recovery.status"] = "parsed";
                }
            });
            return facts;
        }

        private static void ReadPayload(string text, Dictionary<string, string> facts, string prefix)
        {
            Inspect(facts, prefix, () =>
            {
                using (var reader = OpenBase64(text))
                {
                    int version = reader.Reader.ReadInt32();
                    facts[prefix + ".version"] = version.ToString(Invariant);
                    if (version != 3 && version != 4) throw new UnsupportedException("Only payload formats 3 and 4 are inspected.");
                    string prefab = reader.String();
                    facts[prefix + ".prefab"] = Display(prefab);
                    facts[prefix + ".name"] = Display(reader.String());
                    facts[prefix + ".level"] = reader.Reader.ReadInt32().ToString(Invariant);
                    facts[prefix + ".health"] = Number(reader.Reader.ReadSingle());
                    facts[prefix + ".maxHealth"] = Number(reader.Reader.ReadSingle());
                    facts[prefix + ".hasSaddle"] = Bool(reader.Reader.ReadBoolean());
                    string inventory = reader.String();
                    Summarize(facts, prefix + ".inventory", inventory);
                    // Inventory is opaque at the payload parser boundary; its own failure is separate.
                    Inspect(facts, prefix + ".inventory", () =>
                    {
                        if (inventory.Length == 0) facts[prefix + ".inventory.status"] = "empty";
                        else
                        {
                            byte[] decoded = Decode(inventory);
                            facts[prefix + ".inventory.decodedBytes"] = decoded.Length.ToString(Invariant);
                            facts[prefix + ".inventory.status"] = "base64_only_not_inventory_validated";
                        }
                    });
                    facts[prefix + ".lastFedAgeTicks"] = reader.Reader.ReadInt64().ToString(Invariant);
                    facts[prefix + ".spawnAgeTicks"] = reader.Reader.ReadInt64().ToString(Invariant);
                    facts[prefix + ".starProgress"] = Number(reader.Reader.ReadSingle());
                    facts[prefix + ".growthElapsed"] = Number(reader.Reader.ReadSingle());
                    facts[prefix + ".cllcEffect"] = reader.Reader.ReadInt32().ToString(Invariant);
                    facts[prefix + ".saddleStamina"] = Number(reader.Reader.ReadSingle());
                    facts[prefix + ".cllcInfusion"] = Display(reader.String());
                    facts[prefix + ".cllcExtraEffect"] = Display(reader.String());
                    facts[prefix + ".odinHorseHasArmor"] = Bool(reader.Reader.ReadBoolean());
                    if (version == 4)
                    {
                        string cuisine = reader.String();
                        bool validCuisine = ReadCuisine(cuisine, facts, prefix + ".cuisine");
                        if (!validCuisine) throw new InvalidDataException("Payload v4 Cuisine state is invalid.");
                        if (reader.Remaining != 0) throw new InvalidDataException("Payload v4 has trailing bytes.");
                    }
                    else facts[prefix + ".cuisine.status"] = "not_stored_in_v3";
                    facts[prefix + ".trailingBytes"] = reader.Remaining.ToString(Invariant);
                    if (string.IsNullOrWhiteSpace(prefab)) throw new InvalidDataException("Payload prefab is blank.");
                    facts[prefix + ".status"] = "valid";
                    facts[prefix + ".validationScope"] = "FLG payload envelope; prefab availability and inventory contents require runtime validation";
                }
            });
        }

        private static bool ReadCuisine(string text, Dictionary<string, string> facts, string prefix)
        {
            if (text.Length == 0)
            {
                facts[prefix + ".status"] = "empty_no_saved_state";
                return true;
            }
            if (text.Length > 80)
            {
                facts[prefix + ".status"] = "malformed";
                return false;
            }
            string[] fields = text.Split('|');
            long remaining;
            int births;
            bool valid = fields.Length == 4 && fields[0] == "1" &&
                long.TryParse(fields[1], NumberStyles.None, Invariant, out remaining) && remaining >= 0 && remaining <= 2 * TimeSpan.TicksPerHour &&
                int.TryParse(fields[2], NumberStyles.None, Invariant, out births) && births >= 0 && (fields[3] == "0" || fields[3] == "1");
            facts[prefix + ".status"] = valid ? "valid" : "malformed";
            if (valid)
            {
                facts[prefix + ".remainingTicks"] = fields[1];
                facts[prefix + ".birthCount"] = fields[2];
                facts[prefix + ".hasBeenPregnant"] = Bool(fields[3] == "1");
            }
            return valid;
        }

        private static void ReadPending(string text, Dictionary<string, string> current, Dictionary<string, string> facts)
        {
            Inspect(facts, "pending", () =>
            {
                if (text == null || text.Length > 32 * 1024 * 1024) throw new InvalidDataException("Pending text exceeds the FLG envelope limit or is null.");
                using (var reader = OpenBase64(text))
                {
                    int version = reader.Reader.ReadInt32();
                    facts["pending.version"] = version.ToString(Invariant);
                    if (version != 1) throw new UnsupportedException("Only pending format 1 is inspected.");
                    string tx = reader.String(32), orb = reader.String(32);
                    long world = reader.Reader.ReadInt64();
                    byte action = reader.Reader.ReadByte(), phase = reader.Reader.ReadByte();
                    string spawned = reader.String(32), request = reader.String(RequestLimit), prepared = reader.String(RequestLimit);
                    bool hadPayload = reader.Reader.ReadBoolean();
                    string previousPayload = reader.String(SnapshotLimit);
                    bool hadCm = reader.Reader.ReadBoolean();
                    string previousCm = reader.String(SnapshotLimit);
                    int variant = reader.Reader.ReadInt32(), stack = reader.Reader.ReadInt32();
                    facts["pending.transactionId"] = Display(tx);
                    facts["pending.orbId"] = Display(orb);
                    facts["pending.worldUid"] = world.ToString(Invariant);
                    facts["pending.action"] = action == 1 ? "Capture" : action == 2 ? "Release" : "unknown:" + action.ToString(Invariant);
                    facts["pending.phase"] = phase == 1 ? "Preparing" : phase == 2 ? "Committing" : phase == 3 ? "Spawning" : "unknown:" + phase.ToString(Invariant);
                    facts["pending.spawnedId"] = Display(spawned);
                    facts["pending.previous.hadPayload"] = Bool(hadPayload);
                    facts["pending.previous.hadCreatureManagerState"] = Bool(hadCm);
                    facts["pending.previous.variant"] = variant.ToString(Invariant);
                    facts["pending.previous.stack"] = stack.ToString(Invariant);
                    Summarize(facts, "pending.previous.payload", previousPayload);
                    Summarize(facts, "pending.previous.creatureManagerState", previousCm);
                    facts["pending.orbIdMatchesItem"] = Bool(current.TryGetValue(OrbKey, out string itemOrb) && itemOrb == orb);
                    facts["pending.currentPayloadMatchesPrevious"] = Bool(SnapshotMatches(current, PayloadKey, hadPayload, previousPayload));
                    facts["pending.currentCreatureManagerMatchesPrevious"] = Bool(SnapshotMatches(current, CmKey, hadCm, previousCm));
                    facts["pending.trailingBytes"] = reader.Remaining.ToString(Invariant);
                    var failures = new List<string>();
                    if (!Guid.TryParseExact(tx, "N", out _)) failures.Add("transactionId is not a GUID in N format");
                    if (!Guid.TryParseExact(orb, "N", out _)) failures.Add("orbId is not a GUID in N format");
                    if (world == 0) failures.Add("worldUid is zero");
                    if (stack <= 0 || variant < 0) failures.Add("previous stack/variant is invalid");
                    if ((!hadPayload && previousPayload.Length != 0) || (!hadCm && previousCm.Length != 0)) failures.Add("previous presence flags disagree with stored values");
                    if (!((action == 1 && (phase == 1 || phase == 2)) || (action == 2 && (phase == 1 || phase == 3)))) failures.Add("action/phase combination is invalid");
                    if (action == 1 && spawned.Length != 0) failures.Add("capture has a spawnedId");
                    if (action == 2 && prepared.Length != 0) failures.Add("release has capture prepared data");
                    if (spawned.Length != 0 && (phase != 3 || !ValidSpawnedId(spawned))) failures.Add("spawnedId is invalid for this phase");
                    if (reader.Remaining != 0) failures.Add("pending has trailing bytes");
                    facts["pending.status"] = failures.Count == 0 ? "valid" : "malformed";
                    if (failures.Count != 0) facts["pending.reason"] = string.Join("; ", failures);
                    facts["pending.validationScope"] = "FLG pending envelope; nested records are checked separately";
                    if (hadPayload) ReadPayload(previousPayload, facts, "pending.previous.payload");
                    else facts["pending.previous.payload.status"] = "absent";
                    Summarize(facts, "pending.request", request);
                    if (request.Length == 0) facts["pending.request.status"] = "empty";
                    else ReadRequest(request, action, tx, orb, previousPayload, previousCm, facts);
                    Summarize(facts, "pending.prepared", prepared);
                    if (prepared.Length == 0) facts["pending.prepared.status"] = "empty";
                    else ReadPrepared(prepared, facts, "pending.prepared", current, tx);
                }
            });
        }

        private static void ReadRequest(string text, byte action, string expectedTx, string expectedOrb, string previousPayload,
            string previousCm, Dictionary<string, string> facts)
        {
            const string prefix = "pending.request";
            Inspect(facts, prefix, () =>
            {
                if (action != 1 && action != 2) throw new UnsupportedException("Unknown action; request shape cannot be selected.");
                using (var reader = OpenBase64(text))
                {
                    string tx = reader.String();
                    facts[prefix + ".transactionId"] = Display(tx);
                    facts[prefix + ".transactionIdMatchesPending"] = Bool(tx == expectedTx);
                    facts[prefix + ".requesterId"] = reader.ZdoId();
                    if (action == 1) facts[prefix + ".targetId"] = reader.ZdoId();
                    string orb = reader.String();
                    facts[prefix + ".orbId"] = Display(orb);
                    facts[prefix + ".orbIdMatchesPending"] = Bool(orb == expectedOrb);
                    if (action == 1) facts[prefix + ".expectedPrefab"] = Display(reader.String());
                    else
                    {
                        string payload = reader.String(), cm = reader.String();
                        facts[prefix + ".payloadMatchesPrevious"] = Bool(payload == previousPayload);
                        facts[prefix + ".creatureManagerMatchesPrevious"] = Bool(cm == previousCm);
                        Summarize(facts, prefix + ".payload", payload);
                        Summarize(facts, prefix + ".creatureManagerState", cm);
                        ReadPayload(payload, facts, prefix + ".payload");
                        facts[prefix + ".spawnPosition"] = reader.Floats(3);
                        facts[prefix + ".spawnRotation"] = reader.Floats(4);
                    }
                    facts[prefix + ".trailingBytes"] = reader.Remaining.ToString(Invariant);
                    facts[prefix + ".status"] = "parsed";
                }
            });
        }

        private static void ReadPrepared(string text, Dictionary<string, string> facts, string prefix,
            Dictionary<string, string> current, string expectedTx)
        {
            Inspect(facts, prefix, () =>
            {
                using (var reader = OpenBase64(text))
                {
                    string tx = reader.String();
                    facts[prefix + ".transactionId"] = Display(tx);
                    facts[prefix + ".transactionIdMatchesEnvelope"] = Bool(tx == expectedTx);
                    facts[prefix + ".success"] = Bool(reader.Reader.ReadBoolean());
                    facts[prefix + ".retryOwner"] = Bool(reader.Reader.ReadBoolean());
                    facts[prefix + ".targetId"] = reader.ZdoId();
                    facts[prefix + ".expectedPrefab"] = Display(reader.String());
                    string payload = reader.String(), cm = reader.String();
                    facts[prefix + ".message"] = Display(reader.String());
                    Summarize(facts, prefix + ".payload", payload);
                    Summarize(facts, prefix + ".creatureManagerState", cm);
                    if (current != null)
                    {
                        facts[prefix + ".currentPayloadEqualsPrepared"] = Bool(current.TryGetValue(PayloadKey, out string itemPayload) && itemPayload == payload);
                        facts[prefix + ".currentCreatureManagerValueEqualsPrepared"] = Bool((current.TryGetValue(CmKey, out string itemCm) ? itemCm : "") == cm);
                    }
                    if (payload.Length == 0) facts[prefix + ".payload.status"] = "empty";
                    else ReadPayload(payload, facts, prefix + ".payload");
                    facts[prefix + ".trailingBytes"] = reader.Remaining.ToString(Invariant);
                    facts[prefix + ".status"] = "parsed";
                }
            });
        }

        private static bool SnapshotMatches(Dictionary<string, string> current, string key, bool had, string previous)
        {
            bool present = current.TryGetValue(key, out string value);
            return present == had && (!present || value == previous);
        }

        private static bool ValidSpawnedId(string text)
        {
            int colon = text.IndexOf(':');
            return colon > 0 && colon == text.LastIndexOf(':') && text.Length <= 32 &&
                long.TryParse(text.Substring(0, colon), NumberStyles.Integer, Invariant, out long user) &&
                uint.TryParse(text.Substring(colon + 1), NumberStyles.None, Invariant, out uint id) && (user != 0 || id != 0);
        }

        private static void Inspect(Dictionary<string, string> facts, string prefix, Action action)
        {
            try { action(); }
            catch (OversizeException ex) { facts[prefix + ".status"] = "uninspected_oversize"; facts[prefix + ".reason"] = ex.Message; }
            catch (UnsupportedException ex) { facts[prefix + ".status"] = "unsupported"; facts[prefix + ".reason"] = ex.Message; }
            catch (Exception ex) when (ex is FormatException || ex is IOException || ex is InvalidDataException || ex is ArgumentException || ex is OverflowException)
            {
                facts[prefix + ".status"] = "malformed";
                string reason = ex is InvalidDataException ? ex.Message : ex is EndOfStreamException ? "Unexpected end of input." :
                    ex is FormatException ? "Invalid base64 or encoded value." : "Invalid or truncated encoded data.";
                facts[prefix + ".reason"] = Display(ex.GetType().Name + ": " + reason);
            }
        }

        private static byte[] Decode(string text)
        {
            if (text == null) throw new InvalidDataException("Null encoded value.");
            // Bound the input before Convert allocates. Huge whitespace-only encodings are also uninspected.
            if (text.Length > ((MaxDecodedBytes + 2) / 3) * 4) throw new OversizeException("Encoded input exceeds the diagnostic inspection limit; validity is unknown.");
            byte[] bytes = Convert.FromBase64String(text);
            if (bytes.Length > MaxDecodedBytes) throw new OversizeException("Decoded input exceeds 4 MiB; validity is unknown.");
            return bytes;
        }

        private static BoundedReader OpenBase64(string text) { return OpenBytes(Decode(text)); }
        private static BoundedReader OpenBytes(byte[] bytes)
        {
            if (bytes == null) throw new InvalidDataException("Null byte array.");
            if (bytes.Length > MaxDecodedBytes) throw new OversizeException("Decoded input exceeds 4 MiB; validity is unknown.");
            return new BoundedReader(bytes);
        }

        private static void Summarize(Dictionary<string, string> facts, string prefix, string text)
        {
            facts[prefix + ".characters"] = text == null ? "null" : text.Length.ToString(Invariant);
            facts[prefix + ".sha256Utf8"] = text == null ? "<null>" : Hash(text);
        }

        private static string Hash(string text)
        {
            // Preserve exact UTF-8 hashing, including surrogate pairs across chunks, without a huge byte array.
            using (var sha = SHA256.Create())
            {
                var encoder = Encoding.UTF8.GetEncoder();
                var chars = new char[2048];
                var bytes = new byte[Encoding.UTF8.GetMaxByteCount(chars.Length)];
                int position = 0;
                do
                {
                    int count = Math.Min(chars.Length, text.Length - position);
                    text.CopyTo(position, chars, 0, count);
                    position += count;
                    int written = encoder.GetBytes(chars, 0, count, bytes, 0, position == text.Length);
                    sha.TransformBlock(bytes, 0, written, bytes, 0);
                } while (position < text.Length);
                sha.TransformFinalBlock(new byte[0], 0, 0);
                return BitConverter.ToString(sha.Hash).Replace("-", "").ToLowerInvariant();
            }
        }

        private static string Display(string value)
        {
            if (value == null) return "<null>";
            int count = Math.Min(value.Length, 256);
            var result = new StringBuilder(count);
            for (int i = 0; i < count; i++)
            {
                char c = value[i];
                if (char.IsControl(c)) result.Append("\\u").Append(((int)c).ToString("x4", Invariant));
                else result.Append(c);
            }
            if (count != value.Length) result.Append("... [").Append(value.Length.ToString(Invariant)).Append(" chars]");
            return result.ToString();
        }

        private static string Bool(bool value) { return value ? "true" : "false"; }
        private static string Number(float value) { return value.ToString("R", Invariant); }
        private sealed class UnsupportedException : Exception { internal UnsupportedException(string message) : base(message) { } }
        private sealed class OversizeException : Exception { internal OversizeException(string message) : base(message) { } }

        private sealed class BoundedReader : IDisposable
        {
            private readonly MemoryStream stream;
            internal readonly BinaryReader Reader;
            internal BoundedReader(byte[] bytes)
            {
                stream = new MemoryStream(bytes, false);
                Reader = new BinaryReader(stream, Encoding.UTF8);
            }
            internal long Remaining { get { return stream.Length - stream.Position; } }
            internal string String(int maximumCharacters = MaxDecodedBytes)
            {
                uint byteCount = 0;
                for (int shift = 0; ; shift += 7)
                {
                    byte next = Reader.ReadByte();
                    if (shift == 28 && (next & 0xf0) != 0) throw new InvalidDataException("Invalid string length prefix.");
                    byteCount |= (uint)(next & 0x7f) << shift;
                    if ((next & 0x80) == 0) break;
                    if (shift == 28) throw new InvalidDataException("Invalid string length prefix.");
                }
                if (byteCount > int.MaxValue || byteCount > Remaining) throw new InvalidDataException("String length exceeds the remaining packet.");
                if (byteCount > (long)maximumCharacters * 4) throw new InvalidDataException("String exceeds the FLG field limit.");
                if (byteCount > MaxDecodedBytes) throw new OversizeException("String exceeds the diagnostic inspection limit.");
                string value = Encoding.UTF8.GetString(Reader.ReadBytes((int)byteCount));
                if (value.Length > maximumCharacters) throw new InvalidDataException("String exceeds the FLG field limit.");
                return value;
            }
            internal string ZdoId() { return Reader.ReadInt64().ToString(Invariant) + ":" + Reader.ReadUInt32().ToString(Invariant); }
            internal string Floats(int count)
            {
                var parts = new string[count];
                for (int i = 0; i < count; i++) parts[i] = Number(Reader.ReadSingle());
                return string.Join(",", parts);
            }
            public void Dispose() { Reader.Dispose(); stream.Dispose(); }
        }
    }
}
