using System;
using System.Collections.Generic;

// These containers intentionally have no game behavior. The production item-state
// source is linked unchanged; tests simulate only the new ItemData identity created
// by an inventory move and the custom-data snapshot copied by that operation.
internal static class ItemDrop
{
    internal sealed class ItemData
    {
        public Dictionary<string, string> m_customData = new();
        public int m_variant;
        public int m_stack = 1;
    }
}

internal sealed class Inventory
{
    internal readonly List<ItemDrop.ItemData> Items = new();
    public List<ItemDrop.ItemData> GetAllItems() => Items;
}

namespace FeedLikeGrandma
{
    internal static partial class TamingOrbSystem
    {
        private const string PayloadKey = "flg.tame.payload";
        private const string CreatureManagerStateKey = "flg.tame.creaturemanager";
        private const string OrbIdentityKey = "flg.tame.orbId";
        private static int _passed;

        private static int Main()
        {
            try
            {
                CheckReleaseMove();
                CheckDeniedRelease();
                CheckCaptureRollback();
                CheckIsolationAndAmbiguity();
                CheckMutationGuards();
                CheckEmptyOrbRenewal();
                CheckEmptyOrbRenewalGuards();
                Console.WriteLine("Passed " + _passed + " source-linked Orb item-state checks.");
                Console.WriteLine("Scope: production state helper with plain ItemData/Inventory fixtures; game cloning, Unity, inventory events, and RPC are not executed.");
                return 0;
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine(exception);
                return 1;
            }
        }

        private static void CheckReleaseMove()
        {
            ItemDrop.ItemData original = NewOrb(filled: true);
            OrbPendingItemState state = NewState(original, OrbPendingAction.Release);
            Inventory inventory = new();
            inventory.Items.Add(original);
            Require(TryBeginPendingOrbItem(original, state), "begin release persists marker");
            Require(!TryBeginPendingOrbItem(original, state), "same item cannot begin two transactions");
            state.Phase = OrbPendingPhase.Spawning;
            Require(TryUpdatePendingOrbItem(original, state), "persist spawning phase before creating creature");
            state.SpawnedId = "42:12";
            Require(TryUpdatePendingOrbItem(original, state), "persist spawned creature identity");

            ItemDrop.ItemData moved = MoveFixture(original);
            inventory.Items.Clear();
            inventory.Items.Add(moved);
            Require(ReferenceEquals(FindPendingOrbItem(inventory, state.TransactionId, state.OrbId, state.WorldUid), moved),
                "slot move resolves live clone instead of stale original reference");
            Require(TryFinishPendingOrbItem(moved, state, restorePrevious: false, clearContents: true), "release result clears live moved orb");
            Require(!moved.m_customData.ContainsKey(PayloadKey) && !moved.m_customData.ContainsKey(CreatureManagerStateKey) &&
                !HasPendingOrbItem(moved) && moved.m_variant == 0, "release completion clears creature payload, modifier payload, marker, and variant");
            Require(moved.m_customData[OrbIdentityKey] != state.OrbId && Guid.TryParseExact(moved.m_customData[OrbIdentityKey], "N", out _),
                "empty orb receives fresh identity after successful release");
            Require(original.m_customData.ContainsKey(PayloadKey) && HasPendingOrbItem(original), "stale original reference is not mistaken for inventory item");
            Require(moved.m_customData["another-mod"] == "preserve", "release retains unrelated custom data");
            Require(!TryFinishPendingOrbItem(moved, state, restorePrevious: false, clearContents: true), "duplicate success cannot mutate completed orb");
        }

        private static void CheckDeniedRelease()
        {
            ItemDrop.ItemData original = NewOrb(filled: true);
            OrbPendingItemState state = NewState(original, OrbPendingAction.Release);
            Require(TryBeginPendingOrbItem(original, state), "begin approved release before ward denial");
            ItemDrop.ItemData moved = MoveFixture(original);
            Require(TryFinishPendingOrbItem(moved, state, restorePrevious: false, clearContents: false), "ward-denied release resolves moved item without clearing contents");
            Require(!HasPendingOrbItem(moved) && moved.m_variant == original.m_variant && moved.m_stack == original.m_stack &&
                moved.m_customData[PayloadKey] == original.m_customData[PayloadKey] &&
                moved.m_customData[CreatureManagerStateKey] == original.m_customData[CreatureManagerStateKey] &&
                moved.m_customData[OrbIdentityKey] == state.OrbId, "ward-denied release keeps exact filled orb and identity");
            Require(!TryFinishPendingOrbItem(moved, state, restorePrevious: false, clearContents: true), "late success cannot consume an already denied release");
        }

        private static void CheckCaptureRollback()
        {
            ItemDrop.ItemData original = NewOrb(filled: false);
            OrbPendingItemState state = NewState(original, OrbPendingAction.Capture);
            Require(TryBeginPendingOrbItem(original, state), "begin capture records original empty orb");
            state.Phase = OrbPendingPhase.Committing;
            state.PreparedData = "prepared-creature-data";
            Require(TryUpdatePendingOrbItem(original, state), "freeze prepared capture state");
            original.m_customData[PayloadKey] = "staged-captured-creature";
            original.m_customData[CreatureManagerStateKey] = "staged-modifiers";
            original.m_variant = 1;
            ItemDrop.ItemData moved = MoveFixture(original);
            Require(TryFinishPendingOrbItem(moved, state, restorePrevious: true, clearContents: false), "failed capture rolls back moved clone");
            Require(!moved.m_customData.ContainsKey(PayloadKey) && !moved.m_customData.ContainsKey(CreatureManagerStateKey) &&
                !HasPendingOrbItem(moved) && moved.m_variant == 0 && moved.m_stack == 1,
                "capture rollback restores exact empty payload, modifier state, variant, and stack");
            Require(moved.m_customData[OrbIdentityKey] == state.OrbId && moved.m_customData["another-mod"] == "preserve",
                "capture rollback preserves orb identity and unrelated metadata");

            ItemDrop.ItemData succeeded = NewOrb(filled: false);
            OrbPendingItemState successful = NewState(succeeded, OrbPendingAction.Capture);
            Require(TryBeginPendingOrbItem(succeeded, successful), "begin capture success fixture");
            successful.Phase = OrbPendingPhase.Committing;
            Require(TryUpdatePendingOrbItem(succeeded, successful), "persist capture commit fixture");
            succeeded.m_customData[PayloadKey] = "captured-creature";
            succeeded.m_variant = 1;
            Require(TryFinishPendingOrbItem(succeeded, successful, restorePrevious: false, clearContents: false) &&
                succeeded.m_customData[PayloadKey] == "captured-creature" && succeeded.m_variant == 1 && !HasPendingOrbItem(succeeded),
                "capture success keeps staged creature and clears pending marker");
        }

        private static void CheckIsolationAndAmbiguity()
        {
            ItemDrop.ItemData item = NewOrb(filled: true);
            OrbPendingItemState state = NewState(item, OrbPendingAction.Release);
            Require(TryBeginPendingOrbItem(item, state), "begin lookup fixture");
            Inventory inventory = new();
            inventory.Items.Add(item);
            Require(FindPendingOrbItem(inventory, state.TransactionId, state.OrbId, state.WorldUid + 1) == null, "another world cannot resolve pending item");
            Require(FindPendingOrbItem(inventory, Guid.NewGuid().ToString("N"), state.OrbId, state.WorldUid) == null, "late unrelated transaction cannot resolve pending item");
            ItemDrop.ItemData duplicate = MoveFixture(item);
            duplicate.m_customData.Remove(PendingOrbItemKey);
            inventory.Items.Add(duplicate);
            Require(FindPendingOrbItem(inventory, state.TransactionId, state.OrbId, state.WorldUid) == null,
                "duplicate orb identities fail closed even when second copy lacks pending marker");
            inventory.Items.Remove(duplicate);
            Inventory container = new();
            ItemDrop.ItemData transferred = MoveFixture(item);
            inventory.Items.Clear();
            container.Items.Add(transferred);
            Require(FindPendingOrbItem(inventory, state.TransactionId, state.OrbId, state.WorldUid) == null &&
                ReferenceEquals(FindPendingOrbItem(container, state.TransactionId, state.OrbId, state.WorldUid), transferred),
                "lookup follows actual inventory contents after transfer");

            transferred.m_customData[PendingOrbItemKey] = "corrupted-pending-marker";
            Require(HasPendingOrbItem(transferred) && !TryReadPendingOrbItem(transferred, out _) && !TryBeginPendingOrbItem(transferred, state),
                "malformed pending data blocks reuse instead of becoming an empty orb");
        }

        private static void CheckMutationGuards()
        {
            ItemDrop.ItemData item = NewOrb(filled: true);
            OrbPendingItemState state = NewState(item, OrbPendingAction.Release);
            Require(TryBeginPendingOrbItem(item, state), "begin transition guard fixture");
            string marker = item.m_customData[PendingOrbItemKey];
            state.RequestData = "different-request";
            Require(!TryUpdatePendingOrbItem(item, state) && item.m_customData[PendingOrbItemKey] == marker,
                "changed request cannot overwrite persisted pending state");
            state.RequestData = "request";
            state.Phase = OrbPendingPhase.Spawning;
            state.SpawnedId = "42:12";
            Require(TryUpdatePendingOrbItem(item, state), "advance guard fixture to spawned state");
            marker = item.m_customData[PendingOrbItemKey];
            state.SpawnedId = "42:13";
            Require(!TryUpdatePendingOrbItem(item, state) && item.m_customData[PendingOrbItemKey] == marker,
                "second spawned identity cannot replace existing creature evidence");
            state.SpawnedId = "42:12";
            state.Phase = OrbPendingPhase.Preparing;
            Require(!TryUpdatePendingOrbItem(item, state), "spawning cannot move back to preparing");
            state.Phase = OrbPendingPhase.Spawning;
            state.TransactionId = Guid.NewGuid().ToString("N");
            Require(!TryFinishPendingOrbItem(item, state, restorePrevious: false, clearContents: true) &&
                item.m_customData[PayloadKey] == "captured-creature", "late different transaction cannot clear current contents");
            state.TransactionId = ReadState(item).TransactionId;
            Require(!TryFinishPendingOrbItem(item, state, restorePrevious: true, clearContents: true), "contradictory rollback and clear is rejected");
            item.m_customData[OrbIdentityKey] = Guid.NewGuid().ToString("N");
            Require(!TryFinishPendingOrbItem(item, state, restorePrevious: false, clearContents: true), "changed orb identity protects unrelated replacement contents");
        }

        private static void CheckEmptyOrbRenewal()
        {
            foreach (bool move in new[] { false, true })
            {
                ItemDrop.ItemData original = NewOrb(filled: false);
                OrbPendingItemState state = NewState(original, OrbPendingAction.Capture);
                Require(TryBeginPendingOrbItem(original, state), "begin rejected capture before empty-ID recovery");
                Inventory inventory = new();
                ItemDrop.ItemData current = move ? MoveFixture(original) : original;
                inventory.Items.Add(current);
                Require(TryRenewEmptyOrbIdentity(inventory, state.TransactionId, state.OrbId, state.WorldUid, state.RequestData),
                    move ? "empty-ID recovery follows moved live clone" : "empty-ID recovery renews current inventory item");
                Require(Guid.TryParseExact(current.m_customData[OrbIdentityKey], "N", out _) &&
                    current.m_customData[OrbIdentityKey] != state.OrbId && !HasPendingOrbItem(current),
                    "recovery assigns fresh ID and removes only rejected capture marker");
                Require(current.m_customData.Count == 2 && current.m_customData["another-mod"] == "preserve" &&
                    current.m_stack == 1 && current.m_variant == 0 && inventory.Items.Count == 1,
                    "recovery preserves unrelated metadata, empty contents, stack, and item count");
                if (move)
                    Require(original.m_customData[OrbIdentityKey] == state.OrbId && HasPendingOrbItem(original),
                        "empty-ID recovery leaves stale moved-out reference unchanged");
                RequireRejectedRenewalUnchanged(inventory, current, state, "duplicate recovery reply is ignored");
            }
        }

        private static void CheckEmptyOrbRenewalGuards()
        {
            ItemDrop.ItemData item = NewOrb(filled: false);
            OrbPendingItemState state = NewState(item, OrbPendingAction.Capture);
            Require(TryBeginPendingOrbItem(item, state), "begin isolated empty-ID recovery fixture");
            Inventory inventory = new();
            inventory.Items.Add(item);
            RequireRejectedRenewalUnchanged(inventory, item, state, "wrong world cannot renew empty ID", worldUid: state.WorldUid + 1);
            RequireRejectedRenewalUnchanged(inventory, item, state, "different request cannot renew empty ID", requestData: "different-request");
            RequireRejectedRenewalUnchanged(inventory, item, state, "different transaction cannot renew empty ID", transactionId: Guid.NewGuid().ToString("N"));
            RequireRejectedRenewalUnchanged(inventory, item, state, "different orb ID cannot renew empty ID", orbId: Guid.NewGuid().ToString("N"));

            ItemDrop.ItemData duplicate = MoveFixture(item);
            duplicate.m_customData.Remove(PendingOrbItemKey);
            inventory.Items.Add(duplicate);
            RequireRejectedRenewalUnchanged(inventory, item, state, "duplicate empty ID without marker blocks renewal");
            Require(duplicate.m_customData[OrbIdentityKey] == state.OrbId && !HasPendingOrbItem(duplicate),
                "duplicate item is not rewritten by rejected recovery");
            inventory.Items.Remove(duplicate);
            inventory.Items.Clear();
            RequireRejectedRenewalUnchanged(inventory, item, state, "transferred-out orb cannot be renewed through stale reference");
            inventory.Items.Add(item);

            state.PreparedData = "prepared-capture";
            Require(TryUpdatePendingOrbItem(item, state), "prepare nonempty captured-state guard");
            RequireRejectedRenewalUnchanged(inventory, item, state, "prepared capture data blocks empty-ID renewal");
            state.Phase = OrbPendingPhase.Committing;
            Require(TryUpdatePendingOrbItem(item, state), "advance recovery fixture into commit");
            RequireRejectedRenewalUnchanged(inventory, item, state, "committing capture cannot be retired as empty");
            item.m_customData[PendingOrbItemKey] = "corrupt-pending";
            RequireRejectedRenewalUnchanged(inventory, item, state, "malformed marker is preserved rather than cleared");
            item.m_customData.Remove(PendingOrbItemKey);
            RequireRejectedRenewalUnchanged(inventory, item, state, "unsolicited recovery without matching marker is ignored");

            foreach (bool mutateBeforeBegin in new[] { true, false })
            {
                foreach (var mutation in new (string Name, Action<ItemDrop.ItemData> Change)[]
                {
                    ("filled payload", value => value.m_customData[PayloadKey] = "captured-creature"),
                    ("empty but present payload key", value => value.m_customData[PayloadKey] = ""),
                    ("creature modifiers", value => value.m_customData[CreatureManagerStateKey] = "modifiers"),
                    ("empty but present modifier key", value => value.m_customData[CreatureManagerStateKey] = ""),
                    ("stack count", value => value.m_stack = 2),
                    ("nonempty icon variant", value => value.m_variant = 1)
                })
                {
                    item = NewOrb(filled: false);
                    if (mutateBeforeBegin) mutation.Change(item);
                    state = NewState(item, OrbPendingAction.Capture);
                    Require(TryBeginPendingOrbItem(item, state), "begin empty recovery content guard: " + mutation.Name);
                    if (!mutateBeforeBegin) mutation.Change(item);
                    inventory.Items.Clear();
                    inventory.Items.Add(item);
                    RequireRejectedRenewalUnchanged(inventory, item, state,
                        (mutateBeforeBegin ? "original " : "changed ") + mutation.Name + " blocks renewal without changing item");
                }
            }

            item = NewOrb(filled: false);
            state = NewState(item, OrbPendingAction.Release);
            Require(TryBeginPendingOrbItem(item, state), "begin release-action recovery guard");
            inventory.Items.Clear();
            inventory.Items.Add(item);
            RequireRejectedRenewalUnchanged(inventory, item, state, "pending release cannot be treated as rejected capture");
        }

        private static void RequireRejectedRenewalUnchanged(Inventory inventory, ItemDrop.ItemData item,
            OrbPendingItemState state, string name, long? worldUid = null, string? requestData = null,
            string? transactionId = null, string? orbId = null)
        {
            ItemDrop.ItemData before = MoveFixture(item);
            bool renewed = TryRenewEmptyOrbIdentity(inventory, transactionId ?? state.TransactionId,
                orbId ?? state.OrbId, worldUid ?? state.WorldUid, requestData ?? state.RequestData);
            bool unchanged = before.m_stack == item.m_stack && before.m_variant == item.m_variant &&
                before.m_customData.Count == item.m_customData.Count;
            foreach (KeyValuePair<string, string> pair in before.m_customData)
                unchanged &= item.m_customData.TryGetValue(pair.Key, out string? current) && current == pair.Value;
            Require(!renewed && unchanged, name);
        }

        private static ItemDrop.ItemData NewOrb(bool filled)
        {
            ItemDrop.ItemData item = new();
            item.m_customData[OrbIdentityKey] = Guid.NewGuid().ToString("N");
            item.m_customData["another-mod"] = "preserve";
            if (filled)
            {
                item.m_customData[PayloadKey] = "captured-creature";
                item.m_customData[CreatureManagerStateKey] = "creature-modifiers";
                item.m_variant = 1;
            }
            return item;
        }

        private static ItemDrop.ItemData MoveFixture(ItemDrop.ItemData source) => new()
        {
            m_customData = new Dictionary<string, string>(source.m_customData),
            m_variant = source.m_variant,
            m_stack = source.m_stack
        };

        private static OrbPendingItemState NewState(ItemDrop.ItemData item, OrbPendingAction action) => new()
        {
            TransactionId = Guid.NewGuid().ToString("N"),
            OrbId = item.m_customData[OrbIdentityKey],
            WorldUid = 314159L,
            Action = action,
            Phase = OrbPendingPhase.Preparing,
            RequestData = "request",
            Previous = CaptureOrbItemSnapshot(item)
        };

        private static OrbPendingItemState ReadState(ItemDrop.ItemData item)
        {
            if (!TryReadPendingOrbItem(item, out OrbPendingItemState? state) || state == null) throw new InvalidOperationException("Fixture state is missing.");
            return state;
        }

        // Existing caller-owned primitive from TamingOrbNetworkSystem; only this
        // dictionary operation is supplied alongside the unchanged helper source.
        private static void RestoreCustomData(ItemDrop.ItemData item, string key, bool hadValue, string? value)
        {
            if (hadValue) item.m_customData[key] = value ?? string.Empty;
            else item.m_customData.Remove(key);
        }

        private static void Require(bool condition, string name)
        {
            if (!condition) throw new InvalidOperationException("FAILED: " + name);
            _passed++;
            Console.WriteLine("PASS " + name);
        }
    }
}
