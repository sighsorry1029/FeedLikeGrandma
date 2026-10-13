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
