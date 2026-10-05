using System;
using System.Globalization;
using System.IO;
using System.Text;

namespace FeedLikeGrandma;

internal static partial class TamingOrbSystem
{
    private const string PendingOrbItemKey = "flg.tame.pending";

    private static bool HasPendingOrbItem(ItemDrop.ItemData item)
    {
        // A malformed marker must also block reuse; interpreting it as an empty orb is unsafe.
        return item != null && item.m_customData.ContainsKey(PendingOrbItemKey);
    }

    private static bool TryReadPendingOrbItem(ItemDrop.ItemData item, out OrbPendingItemState? state)
    {
        state = null;
        return item != null &&
               item.m_customData.TryGetValue(PendingOrbItemKey, out string text) &&
               OrbPendingItemState.TryDeserialize(text, out state);
    }

    private static OrbItemSnapshot CaptureOrbItemSnapshot(ItemDrop.ItemData item)
    {
        bool hadPayload = item.m_customData.TryGetValue(PayloadKey, out string payload);
        bool hadCreatureManagerState = item.m_customData.TryGetValue(CreatureManagerStateKey, out string state);
        return new OrbItemSnapshot
        {
            HadPayload = hadPayload,
            Payload = payload ?? string.Empty,
            HadCreatureManagerState = hadCreatureManagerState,
            CreatureManagerState = state ?? string.Empty,
            Variant = item.m_variant,
            Stack = item.m_stack
        };
    }

    private static bool TryBeginPendingOrbItem(ItemDrop.ItemData item, OrbPendingItemState state)
    {
        if (item == null || state == null || HasPendingOrbItem(item) ||
            state.Phase != OrbPendingPhase.Preparing ||
            !HasOrbItemIdentity(item, state.OrbId) ||
            !CaptureOrbItemSnapshot(item).Matches(state.Previous) ||
            !state.TrySerialize(out string text))
        {
            return false;
        }

        item.m_customData[PendingOrbItemKey] = text;
        return true;
    }

    private static bool TryUpdatePendingOrbItem(ItemDrop.ItemData item, OrbPendingItemState state)
    {
        if (state == null || !TryReadPendingOrbItem(item, out OrbPendingItemState? current) ||
            current == null || !current.MatchesIdentity(state) ||
            !HasOrbItemIdentity(item, state.OrbId) ||
            !current.Previous.Matches(state.Previous) ||
            !current.RequestData.Equals(state.RequestData, StringComparison.Ordinal) ||
            (current.Phase != OrbPendingPhase.Preparing && current.Phase != state.Phase) ||
            (!string.IsNullOrEmpty(current.PreparedData) &&
             !current.PreparedData.Equals(state.PreparedData, StringComparison.Ordinal)) ||
            (!string.IsNullOrEmpty(current.SpawnedId) &&
             !current.SpawnedId.Equals(state.SpawnedId, StringComparison.Ordinal)) ||
            !state.TrySerialize(out string text))
        {
            return false;
        }

        item.m_customData[PendingOrbItemKey] = text;
        return true;
    }

    private static ItemDrop.ItemData? FindPendingOrbItem(
        Inventory inventory,
        string transactionId,
        string orbId,
        long worldUid)
    {
        ItemDrop.ItemData? match = null;
        foreach (ItemDrop.ItemData candidate in inventory.GetAllItems())
        {
            if (!HasOrbItemIdentity(candidate, orbId))
            {
                continue;
            }

            // Even a second copy with a damaged/missing marker makes the identity ambiguous.
            if (match != null)
            {
                return null;
            }

            match = candidate;
        }

        if (match == null || !TryReadPendingOrbItem(match, out OrbPendingItemState? state) ||
            state == null || state.WorldUid != worldUid ||
            !state.TransactionId.Equals(transactionId, StringComparison.Ordinal) ||
            !state.OrbId.Equals(orbId, StringComparison.Ordinal))
        {
            return null;
        }

        return match;
    }

    private static bool TryFinishPendingOrbItem(
        ItemDrop.ItemData item,
        OrbPendingItemState expected,
        bool restorePrevious,
        bool clearContents)
    {
        if (expected == null || (restorePrevious && clearContents) ||
            !TryReadPendingOrbItem(item, out OrbPendingItemState? current) || current == null ||
            !current.MatchesIdentity(expected) || !HasOrbItemIdentity(item, expected.OrbId) ||
            !current.Previous.Matches(expected.Previous) ||
            !current.RequestData.Equals(expected.RequestData, StringComparison.Ordinal) ||
            (clearContents && current.Action != OrbPendingAction.Release))
        {
            return false;
        }

        if (restorePrevious)
        {
            OrbItemSnapshot previous = current.Previous;
            RestoreCustomData(item, PayloadKey, previous.HadPayload, previous.Payload);
            RestoreCustomData(item, CreatureManagerStateKey, previous.HadCreatureManagerState, previous.CreatureManagerState);
            item.m_variant = previous.Variant;
            item.m_stack = previous.Stack;
        }
        else if (clearContents)
        {
            item.m_customData.Remove(PayloadKey);
            item.m_customData.Remove(CreatureManagerStateKey);
            item.m_customData[OrbIdentityKey] = Guid.NewGuid().ToString("N");
            item.m_variant = 0;
        }

        item.m_customData.Remove(PendingOrbItemKey);
        // The caller must notify the inventory that actually owns this item after success.
        return true;
    }

    private static bool HasOrbItemIdentity(ItemDrop.ItemData item, string orbId)
    {
        return item != null && !string.IsNullOrEmpty(orbId) &&
               item.m_customData.TryGetValue(OrbIdentityKey, out string currentOrbId) &&
               currentOrbId.Equals(orbId, StringComparison.Ordinal);
    }

    private enum OrbPendingAction : byte
    {
        Capture = 1,
        Release = 2
    }

    private enum OrbPendingPhase : byte
    {
        Preparing = 1,
        Committing = 2,
        Spawning = 3
    }

    private sealed class OrbItemSnapshot
    {
        public bool HadPayload { get; init; }
        public string Payload { get; init; } = string.Empty;
        public bool HadCreatureManagerState { get; init; }
        public string CreatureManagerState { get; init; } = string.Empty;
        public int Variant { get; init; }
        public int Stack { get; init; }

        public bool Matches(OrbItemSnapshot? other)
        {
            return other != null && HadPayload == other.HadPayload &&
                   Payload.Equals(other.Payload, StringComparison.Ordinal) &&
                   HadCreatureManagerState == other.HadCreatureManagerState &&
                   CreatureManagerState.Equals(other.CreatureManagerState, StringComparison.Ordinal) &&
                   Variant == other.Variant && Stack == other.Stack;
        }
    }

    private sealed class OrbPendingItemState
    {
        private const int FormatVersion = 1;
        private const int MaxSnapshotFieldLength = 1024 * 1024;
        private const int MaxRequestFieldLength = 8 * 1024 * 1024;
        private const int MaxEncodedLength = 32 * 1024 * 1024;

        public string TransactionId { get; set; } = string.Empty;
        public string OrbId { get; set; } = string.Empty;
        public long WorldUid { get; set; }
        public OrbPendingAction Action { get; set; }
        public OrbPendingPhase Phase { get; set; }
        public string SpawnedId { get; set; } = string.Empty;
        public string RequestData { get; set; } = string.Empty;
        public string PreparedData { get; set; } = string.Empty;
        public OrbItemSnapshot Previous { get; set; } = new();

        public bool MatchesIdentity(OrbPendingItemState? other)
        {
            return other != null && WorldUid == other.WorldUid && Action == other.Action &&
                   TransactionId.Equals(other.TransactionId, StringComparison.Ordinal) &&
                   OrbId.Equals(other.OrbId, StringComparison.Ordinal);
        }

        public bool TrySerialize(out string text)
        {
            text = string.Empty;
            if (!IsValid())
            {
                return false;
            }

            using MemoryStream stream = new();
            using (BinaryWriter writer = new(stream, Encoding.UTF8, leaveOpen: true))
            {
                writer.Write(FormatVersion);
                writer.Write(TransactionId);
                writer.Write(OrbId);
                writer.Write(WorldUid);
                writer.Write((byte)Action);
                writer.Write((byte)Phase);
                writer.Write(SpawnedId);
                writer.Write(RequestData);
                writer.Write(PreparedData);
                writer.Write(Previous.HadPayload);
                writer.Write(Previous.Payload);
                writer.Write(Previous.HadCreatureManagerState);
                writer.Write(Previous.CreatureManagerState);
                writer.Write(Previous.Variant);
                writer.Write(Previous.Stack);
            }

            text = Convert.ToBase64String(stream.ToArray());
            if (text.Length > MaxEncodedLength)
            {
                text = string.Empty;
                return false;
            }

            return true;
        }

        public static bool TryDeserialize(string text, out OrbPendingItemState? state)
        {
            state = null;
            if (string.IsNullOrEmpty(text) || text.Length > MaxEncodedLength)
            {
                return false;
            }

            try
            {
                using MemoryStream stream = new(Convert.FromBase64String(text), writable: false);
                using BinaryReader reader = new(stream, Encoding.UTF8);
                if (reader.ReadInt32() != FormatVersion)
                {
                    return false;
                }

                OrbPendingItemState parsed = new()
                {
                    TransactionId = ReadBoundedString(reader, 32),
                    OrbId = ReadBoundedString(reader, 32),
                    WorldUid = reader.ReadInt64(),
                    Action = (OrbPendingAction)reader.ReadByte(),
                    Phase = (OrbPendingPhase)reader.ReadByte(),
                    SpawnedId = ReadBoundedString(reader, 32),
                    RequestData = ReadBoundedString(reader, MaxRequestFieldLength),
                    PreparedData = ReadBoundedString(reader, MaxRequestFieldLength),
                    Previous = new OrbItemSnapshot
                    {
                        HadPayload = reader.ReadBoolean(),
                        Payload = ReadBoundedString(reader, MaxSnapshotFieldLength),
                        HadCreatureManagerState = reader.ReadBoolean(),
                        CreatureManagerState = ReadBoundedString(reader, MaxSnapshotFieldLength),
                        Variant = reader.ReadInt32(),
                        Stack = reader.ReadInt32()
                    }
                };
                if (stream.Position != stream.Length || !parsed.IsValid())
                {
                    return false;
                }

                state = parsed;
                return true;
            }
            catch (Exception ex) when (ex is FormatException || ex is IOException || ex is ArgumentException)
            {
                return false;
            }
        }

        private bool IsValid()
        {
            if (!Guid.TryParseExact(TransactionId, "N", out _) ||
                !Guid.TryParseExact(OrbId, "N", out _) || WorldUid == 0L || Previous == null ||
                Previous.Stack <= 0 || Previous.Variant < 0 ||
                Previous.Payload == null || Previous.Payload.Length > MaxSnapshotFieldLength ||
                Previous.CreatureManagerState == null || Previous.CreatureManagerState.Length > MaxSnapshotFieldLength ||
                (!Previous.HadPayload && Previous.Payload.Length != 0) ||
                (!Previous.HadCreatureManagerState && Previous.CreatureManagerState.Length != 0) ||
                RequestData == null || RequestData.Length > MaxRequestFieldLength ||
                PreparedData == null || PreparedData.Length > MaxRequestFieldLength || SpawnedId == null)
            {
                return false;
            }

            bool validPhase = Action switch
            {
                OrbPendingAction.Capture => Phase is OrbPendingPhase.Preparing or OrbPendingPhase.Committing,
                OrbPendingAction.Release => Phase is OrbPendingPhase.Preparing or OrbPendingPhase.Spawning,
                _ => false
            };
            if (!validPhase || (Action == OrbPendingAction.Capture && SpawnedId.Length != 0) ||
                (Action == OrbPendingAction.Release && PreparedData.Length != 0))
            {
                return false;
            }

            return SpawnedId.Length == 0 ||
                   (Phase == OrbPendingPhase.Spawning && IsValidSpawnedId(SpawnedId));
        }

        private static bool IsValidSpawnedId(string text)
        {
            int separator = text.IndexOf(':');
            return separator > 0 && separator == text.LastIndexOf(':') && text.Length <= 32 &&
                   long.TryParse(text.Substring(0, separator), NumberStyles.Integer, CultureInfo.InvariantCulture, out long userId) &&
                   uint.TryParse(text.Substring(separator + 1), NumberStyles.None, CultureInfo.InvariantCulture, out uint objectId) &&
                   (userId != 0L || objectId != 0U);
        }

        private static string ReadBoundedString(BinaryReader reader, int maxLength)
        {
            string value = reader.ReadString();
            if (value.Length > maxLength)
            {
                throw new InvalidDataException("Orb pending-state field is too long.");
            }

            return value;
        }
    }
}
