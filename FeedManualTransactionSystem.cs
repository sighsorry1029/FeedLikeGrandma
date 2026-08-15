using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using UnityEngine;

namespace FeedLikeGrandma;

internal static partial class FeedSystem
{
    // Inventory stays client-owned; the server sequences prepare/commit and the current target owner applies effects.
    private const string FeedPrepareServerRpc = "FLG_FeedPrepareServer";
    private const string FeedPrepareOwnerRpc = "FLG_FeedPrepareOwner";
    private const string FeedPreparedServerRpc = "FLG_FeedPreparedServer";
    private const string FeedPreparedClientRpc = "FLG_FeedPreparedClient";
    private const string FeedCommitServerRpc = "FLG_FeedCommitServer";
    private const string FeedCommitOwnerRpc = "FLG_FeedCommitOwner";
    private const string FeedResultServerRpc = "FLG_FeedResultServer";
    private const string FeedResultClientRpc = "FLG_FeedResultClient";
    private const float FeedRetryIntervalSeconds = 1f;
    private const float FeedPrepareTimeoutSeconds = 30f;
    private const long ServerFeedPrepareRetentionTicks = TimeSpan.TicksPerMinute * 2;
    private const long ServerFeedCompletedRetentionTicks = TimeSpan.TicksPerMinute * 5;
    private const int MaxServerFeedTransactions = 1024;

    private static readonly int FeedTransactionHash = "flg_manual_feed_tx".GetStableHashCode();
    private static readonly int FeedTransactionResultHash = "flg_manual_feed_result".GetStableHashCode();
    private static readonly Dictionary<string, PendingManualFeedTransaction> PendingManualFeedTransactions = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, ServerManualFeedTransaction> ServerManualFeedTransactions = new(StringComparer.Ordinal);
    private static readonly Dictionary<ZDOID, string> ServerManualFeedLocks = new();
    private static readonly Dictionary<string, OwnerCompletedFeedTransaction> OwnerCompletedFeedTransactions = new(StringComparer.Ordinal);
    private static ZRoutedRpc? _manualFeedRpcInstance;

    internal static void RegisterManualFeedNetworkRpcs()
    {
        ZRoutedRpc? routedRpc = ZRoutedRpc.instance;
        if (routedRpc == null || ReferenceEquals(_manualFeedRpcInstance, routedRpc))
        {
            return;
        }

        RegisterManualFeedRpc(routedRpc, FeedPrepareServerRpc, ReceiveFeedPrepareOnServer);
        RegisterManualFeedRpc(routedRpc, FeedPrepareOwnerRpc, ReceiveFeedPrepareOnOwner);
        RegisterManualFeedRpc(routedRpc, FeedPreparedServerRpc, ReceiveFeedPreparedOnServer);
        RegisterManualFeedRpc(routedRpc, FeedPreparedClientRpc, ReceiveFeedPreparedOnClient);
        RegisterManualFeedRpc(routedRpc, FeedCommitServerRpc, ReceiveFeedCommitOnServer);
        RegisterManualFeedRpc(routedRpc, FeedCommitOwnerRpc, ReceiveFeedCommitOnOwner);
        RegisterManualFeedRpc(routedRpc, FeedResultServerRpc, ReceiveFeedResultOnServer);
        RegisterManualFeedRpc(routedRpc, FeedResultClientRpc, ReceiveFeedResultOnClient);
        _manualFeedRpcInstance = routedRpc;
    }

    private static void RegisterManualFeedRpc(
        ZRoutedRpc routedRpc,
        string name,
        Action<long, ZPackage> handler)
    {
        if (!routedRpc.m_functions.ContainsKey(name.GetStableHashCode()))
        {
            routedRpc.Register(name, handler);
        }
    }

    private static void ShutdownManualFeedTransactions()
    {
        Player? localPlayer = Player.m_localPlayer;
        foreach (PendingManualFeedTransaction pending in PendingManualFeedTransactions.Values)
        {
            if (pending.Phase == ManualFeedClientPhase.Preparing && (object?)localPlayer != null)
            {
                ReturnEscrowItems(localPlayer, pending, CountEscrowItems(pending));
            }
        }

        PendingManualFeedTransactions.Clear();
        ServerManualFeedTransactions.Clear();
        ServerManualFeedLocks.Clear();
        OwnerCompletedFeedTransactions.Clear();
        _manualFeedRpcInstance = null;
    }

    private static bool TryStartManualFeedTransaction(Player player, GameObject targetObject, ItemDrop.ItemData item)
    {
        if (!TryBuildContext(targetObject, out FeedTargetContext context) ||
            !TryGetItemPrefabName(item, out string itemPrefabName))
        {
            return false;
        }

        int itemQuality = Mathf.Max(1, item.m_quality);
        bool matchesDailyFood = FindMatchingFood(context.Rule.Foods, itemPrefabName, itemQuality) != null;
        bool matchesLevelUpFood = TryGetLevelUpItems(context, out IReadOnlyList<ResolvedFoodEntry>? levelUpFoods) &&
                                  levelUpFoods != null &&
                                  FindMatchingFood(levelUpFoods, itemPrefabName, itemQuality) != null;
        if (!matchesDailyFood && !matchesLevelUpFood)
        {
            return false;
        }

        if ((object?)context.BaseAI != null && context.BaseAI.IsAlerted())
        {
            FeedLocalization.Message(player, "$flg_feed_target_alerted", context.Character.GetHoverName());
            return true;
        }

        long nowTicks = ZNet.instance.GetTime().Ticks;
        long cooldownTicks = context.NView.GetZDO().GetLong(FeedCooldownHash, 0L);
        if (cooldownTicks > nowTicks)
        {
            TimeSpan remaining = new(cooldownTicks - nowTicks);
            FeedLocalization.Message(player, "$flg_feed_cooldown", remaining.TotalSeconds.ToString("0.#", CultureInfo.InvariantCulture));
            return true;
        }

        ZRoutedRpc? routedRpc = ZRoutedRpc.instance;
        long serverPeerId = routedRpc?.GetServerPeerID() ?? 0L;
        if (routedRpc == null || serverPeerId == 0L)
        {
            FeedLocalization.Message(player, "$flg_feed_network_unavailable");
            return true;
        }

        int requestedAmount = Mathf.Min(
            CountItemsByPrefab(player.GetInventory(), itemPrefabName, itemQuality),
            FeedLikeGrandmaPlugin.MaxItemsPerFeed.Value);
        if (requestedAmount <= 0)
        {
            FeedLocalization.Message(player, "$flg_feed_no_matching_item");
            return true;
        }

        List<ItemDrop.ItemData> escrowItems = new();
        if (!TryEscrowItems(player, itemPrefabName, itemQuality, requestedAmount, escrowItems))
        {
            FeedLocalization.Message(player, "$flg_feed_no_matching_item");
            return true;
        }

        string transactionId = Guid.NewGuid().ToString("N");
        FeedRequestMessage request = new()
        {
            TransactionId = transactionId,
            RequesterId = player.GetZDOID(),
            TargetId = context.NView.GetZDO().m_uid,
            ItemPrefabName = itemPrefabName,
            ItemQuality = itemQuality,
            RequestedAmount = requestedAmount
        };
        PendingManualFeedTransactions[transactionId] = new PendingManualFeedTransaction
        {
            Request = request,
            EscrowItems = escrowItems,
            Phase = ManualFeedClientPhase.Preparing,
            StartedRealtime = Time.realtimeSinceStartup,
            LastSentRealtime = Time.realtimeSinceStartup
        };

        routedRpc.InvokeRoutedRPC(serverPeerId, FeedPrepareServerRpc, request.Write());
        FeedLikeGrandmaPlugin.Instance.StartCoroutine(RetryManualFeedTransaction(transactionId));
        return true;
    }

    private static IEnumerator RetryManualFeedTransaction(string transactionId)
    {
        WaitForSecondsRealtime wait = new(FeedRetryIntervalSeconds);
        while (PendingManualFeedTransactions.TryGetValue(transactionId, out PendingManualFeedTransaction? pending))
        {
            yield return wait;
            if (!PendingManualFeedTransactions.TryGetValue(transactionId, out pending))
            {
                yield break;
            }

            Player? player = Player.m_localPlayer;
            if ((object?)player == null || !player.GetZDOID().Equals(pending.Request.RequesterId))
            {
                yield break;
            }

            float now = Time.realtimeSinceStartup;
            if (pending.Phase == ManualFeedClientPhase.Preparing &&
                now - pending.StartedRealtime >= FeedPrepareTimeoutSeconds)
            {
                ReturnEscrowItems(player, pending, CountEscrowItems(pending));
                PendingManualFeedTransactions.Remove(transactionId);
                FeedLocalization.Message(player, "$flg_feed_prepare_timeout");
                yield break;
            }

            if (now - pending.LastSentRealtime < FeedRetryIntervalSeconds)
            {
                continue;
            }

            pending.LastSentRealtime = now;
            SendPendingManualFeedTransaction(pending);
        }
    }

    private static void SendPendingManualFeedTransaction(PendingManualFeedTransaction pending)
    {
        ZRoutedRpc? routedRpc = ZRoutedRpc.instance;
        long serverPeerId = routedRpc?.GetServerPeerID() ?? 0L;
        if (routedRpc == null || serverPeerId == 0L)
        {
            return;
        }

        string rpc = pending.Phase == ManualFeedClientPhase.Preparing
            ? FeedPrepareServerRpc
            : FeedCommitServerRpc;
        routedRpc.InvokeRoutedRPC(serverPeerId, rpc, pending.Request.Write());
    }

    private static void ReceiveFeedPrepareOnServer(long sender, ZPackage pkg)
    {
        if (!IsLocalNetworkServer() ||
            !FeedRequestMessage.TryRead(pkg, out FeedRequestMessage? request) ||
            request == null ||
            !IsValidFeedRequest(request) ||
            !TryAuthenticateNetworkPlayer(sender, request.RequesterId))
        {
            return;
        }

        CleanupServerFeedTransactions();
        if (ServerManualFeedTransactions.TryGetValue(request.TransactionId, out ServerManualFeedTransaction? existing))
        {
            if (!existing.Matches(request))
            {
                FeedLikeGrandmaPlugin.Log.LogWarning($"Rejected reused manual-feed transaction '{request.TransactionId}' with different data.");
                return;
            }

            existing.RequesterPeerId = sender;
            if (existing.Phase == ManualFeedServerPhase.Completed && existing.Result != null)
            {
                SendFeedResultToClient(existing);
            }
            else if (existing.Phase is ManualFeedServerPhase.Prepared or ManualFeedServerPhase.Committing && existing.Prepared != null)
            {
                SendFeedPreparedToClient(existing);
            }
            else
            {
                ForwardFeedPrepareToOwner(existing);
            }

            return;
        }

        if (ServerManualFeedTransactions.Count >= MaxServerFeedTransactions)
        {
            SendFeedPreparedToPeer(sender, new FeedPrepareMessage
            {
                TransactionId = request.TransactionId,
                TargetId = request.TargetId,
                ItemPrefabName = request.ItemPrefabName,
                Message = FeedLocalization.Text("$flg_feed_target_busy")
            });
            return;
        }

        if (ServerManualFeedLocks.TryGetValue(request.TargetId, out string lockedBy) &&
            !lockedBy.Equals(request.TransactionId, StringComparison.Ordinal))
        {
            SendFeedPreparedToPeer(sender, new FeedPrepareMessage
            {
                TransactionId = request.TransactionId,
                TargetId = request.TargetId,
                ItemPrefabName = request.ItemPrefabName,
                Message = FeedLocalization.Text("$flg_feed_target_busy")
            });
            return;
        }

        ServerManualFeedTransaction transaction = new()
        {
            Request = request,
            InitialRequestedAmount = request.RequestedAmount,
            RequesterPeerId = sender,
            Phase = ManualFeedServerPhase.Preparing,
            UpdatedAtTicks = GetNetworkTimeTicks()
        };
        ServerManualFeedTransactions[request.TransactionId] = transaction;
        ServerManualFeedLocks[request.TargetId] = request.TransactionId;
        ForwardFeedPrepareToOwner(transaction);
    }

    private static void ForwardFeedPrepareToOwner(ServerManualFeedTransaction transaction)
    {
        if (!TryResolveTargetOwner(transaction.Request.TargetId, out long ownerPeerId))
        {
            FailFeedBeforeCommit(transaction, FeedLocalization.Text("$flg_feed_target_not_configured"));
            return;
        }

        transaction.OwnerPeerId = ownerPeerId;
        transaction.PrepareOwnerPeerIds.Add(ownerPeerId);
        transaction.UpdatedAtTicks = GetNetworkTimeTicks();
        ZRoutedRpc.instance.InvokeRoutedRPC(ownerPeerId, FeedPrepareOwnerRpc, transaction.Request.Write());
    }

    private static void ReceiveFeedPrepareOnOwner(long sender, ZPackage pkg)
    {
        if (!IsMessageFromServer(sender) ||
            !FeedRequestMessage.TryRead(pkg, out FeedRequestMessage? request) ||
            request == null)
        {
            return;
        }

        FeedPrepareMessage prepared = new()
        {
            TransactionId = request.TransactionId,
            TargetId = request.TargetId,
            ItemPrefabName = request.ItemPrefabName
        };
        if (!TryFindOwnedFeedTarget(request.TargetId, out GameObject? targetObject))
        {
            prepared.RetryOwner = true;
        }
        else
        {
            Player? requester = FindPlayer(request.RequesterId);
            if ((object?)requester == null || !IsRequesterInFeedRange(requester, targetObject!))
            {
                prepared.Message = FeedLocalization.Text("$flg_feed_too_far");
            }
            else if (TryBuildManualFeedPlan(
                         targetObject!,
                         request,
                         requester,
                         out _,
                         out FeedEffectPlan plan,
                         out FeedResponseMessage response))
            {
                prepared.Success = true;
                prepared.AcceptedAmount = plan.Amount;
            }
            else
            {
                prepared.Message = response.Message;
            }
        }

        ZRoutedRpc.instance.InvokeRoutedRPC(
            ZRoutedRpc.instance.GetServerPeerID(),
            FeedPreparedServerRpc,
            prepared.Write());
    }

    private static void ReceiveFeedPreparedOnServer(long sender, ZPackage pkg)
    {
        if (!IsLocalNetworkServer() ||
            !FeedPrepareMessage.TryRead(pkg, out FeedPrepareMessage? prepared) ||
            prepared == null ||
            !ServerManualFeedTransactions.TryGetValue(prepared.TransactionId, out ServerManualFeedTransaction? transaction) ||
            transaction.Phase != ManualFeedServerPhase.Preparing ||
            !transaction.PrepareOwnerPeerIds.Contains(sender) ||
            !prepared.TargetId.Equals(transaction.Request.TargetId) ||
            !prepared.ItemPrefabName.Equals(transaction.Request.ItemPrefabName, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (prepared.RetryOwner)
        {
            if (transaction.OwnerPeerId == sender)
            {
                transaction.OwnerPeerId = 0L;
            }

            transaction.UpdatedAtTicks = GetNetworkTimeTicks();
            return;
        }

        if (!prepared.Success || prepared.AcceptedAmount <= 0 || prepared.AcceptedAmount > transaction.Request.RequestedAmount)
        {
            FailFeedBeforeCommit(transaction, prepared.Message);
            return;
        }

        transaction.Prepared = prepared;
        transaction.Phase = ManualFeedServerPhase.Prepared;
        transaction.UpdatedAtTicks = GetNetworkTimeTicks();
        SendFeedPreparedToClient(transaction);
    }

    private static void ReceiveFeedPreparedOnClient(long sender, ZPackage pkg)
    {
        if (!IsMessageFromServer(sender) ||
            !FeedPrepareMessage.TryRead(pkg, out FeedPrepareMessage? prepared) ||
            prepared == null ||
            !PendingManualFeedTransactions.TryGetValue(prepared.TransactionId, out PendingManualFeedTransaction? pending) ||
            !prepared.TargetId.Equals(pending.Request.TargetId) ||
            !prepared.ItemPrefabName.Equals(pending.Request.ItemPrefabName, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        Player? player = Player.m_localPlayer;
        if ((object?)player == null)
        {
            return;
        }

        if (!prepared.Success)
        {
            ReturnEscrowItems(player, pending, CountEscrowItems(pending));
            PendingManualFeedTransactions.Remove(prepared.TransactionId);
            if (!string.IsNullOrWhiteSpace(prepared.Message))
            {
                player.Message(MessageHud.MessageType.Center, prepared.Message);
            }

            return;
        }

        int escrowCount = CountEscrowItems(pending);
        if (prepared.AcceptedAmount <= 0 || prepared.AcceptedAmount > escrowCount)
        {
            ReturnEscrowItems(player, pending, escrowCount);
            PendingManualFeedTransactions.Remove(prepared.TransactionId);
            FeedLocalization.Message(player, "$flg_feed_transaction_invalid");
            return;
        }

        ReturnEscrowItems(player, pending, escrowCount - prepared.AcceptedAmount);
        pending.Request = pending.Request.WithAmount(prepared.AcceptedAmount);
        pending.Phase = ManualFeedClientPhase.Committing;
        pending.LastSentRealtime = Time.realtimeSinceStartup;
        SendPendingManualFeedTransaction(pending);
    }

    private static void ReceiveFeedCommitOnServer(long sender, ZPackage pkg)
    {
        if (!IsLocalNetworkServer() ||
            !FeedRequestMessage.TryRead(pkg, out FeedRequestMessage? request) ||
            request == null ||
            !TryAuthenticateNetworkPlayer(sender, request.RequesterId) ||
            !ServerManualFeedTransactions.TryGetValue(request.TransactionId, out ServerManualFeedTransaction? transaction) ||
            !transaction.MatchesExceptAmount(request))
        {
            return;
        }

        transaction.RequesterPeerId = sender;
        if (transaction.Phase == ManualFeedServerPhase.Completed && transaction.Result != null)
        {
            SendFeedResultToClient(transaction);
            return;
        }

        if (transaction.Prepared == null ||
            request.RequestedAmount != transaction.Prepared.AcceptedAmount ||
            transaction.Phase is not (ManualFeedServerPhase.Prepared or ManualFeedServerPhase.Committing))
        {
            FailFeedBeforeCommit(transaction, FeedLocalization.Text("$flg_feed_transaction_invalid"));
            return;
        }

        transaction.Request = request;
        transaction.Phase = ManualFeedServerPhase.Committing;
        ForwardFeedCommitToOwner(transaction);
    }

    private static void ForwardFeedCommitToOwner(ServerManualFeedTransaction transaction)
    {
        if (!TryResolveTargetOwner(transaction.Request.TargetId, out long ownerPeerId))
        {
            if (!transaction.CommitForwarded && ZDOMan.instance?.GetZDO(transaction.Request.TargetId) == null)
            {
                CompleteUndispatchedFeedFailure(
                    transaction,
                    FeedLocalization.Text("$flg_feed_target_not_configured"));
            }

            // Once dispatched, a missing target is ambiguous; retry the same transaction without refunding.
            return;
        }

        transaction.OwnerPeerId = ownerPeerId;
        transaction.CommitOwnerPeerIds.Add(ownerPeerId);
        transaction.CommitForwarded = true;
        transaction.UpdatedAtTicks = GetNetworkTimeTicks();
        ZRoutedRpc.instance.InvokeRoutedRPC(ownerPeerId, FeedCommitOwnerRpc, transaction.Request.Write());
    }

    private static void ReceiveFeedCommitOnOwner(long sender, ZPackage pkg)
    {
        if (!IsMessageFromServer(sender) ||
            !FeedRequestMessage.TryRead(pkg, out FeedRequestMessage? request) ||
            request == null)
        {
            return;
        }

        CleanupOwnerCompletedFeedTransactions();
        if (OwnerCompletedFeedTransactions.TryGetValue(request.TransactionId, out OwnerCompletedFeedTransaction? completed))
        {
            SendFeedResultToServer(completed.Response);
            return;
        }

        if (!TryFindOwnedFeedTarget(request.TargetId, out GameObject? targetObject))
        {
            SendFeedResultToServer(new FeedResponseMessage
            {
                TransactionId = request.TransactionId,
                TargetId = request.TargetId,
                ItemPrefabName = request.ItemPrefabName,
                RetryOwner = true
            });
            return;
        }

        ZNetView nview = targetObject!.GetComponent<ZNetView>();
        ZDO targetZdo = nview.GetZDO();
        if (targetZdo.GetString(FeedTransactionHash, string.Empty).Equals(request.TransactionId, StringComparison.Ordinal) &&
            FeedResponseMessage.TryReadBase64(targetZdo.GetString(FeedTransactionResultHash, string.Empty), out FeedResponseMessage? stored) &&
            stored != null)
        {
            SendFeedResultToServer(stored);
            return;
        }

        Player? requester = FindPlayer(request.RequesterId);
        FeedResponseMessage response;
        if ((object?)requester == null || !IsRequesterInFeedRange(requester, targetObject))
        {
            response = new FeedResponseMessage
            {
                TransactionId = request.TransactionId,
                TargetId = request.TargetId,
                ItemPrefabName = request.ItemPrefabName,
                Message = FeedLocalization.Text("$flg_feed_too_far")
            };
        }
        else
        {
            response = ProcessFeedRequest(targetObject, request, requester);
        }

        if (nview.IsValid() && nview.IsOwner())
        {
            targetZdo.Set(FeedTransactionResultHash, response.ToBase64());
            targetZdo.Set(FeedTransactionHash, request.TransactionId);
        }

        OwnerCompletedFeedTransactions[request.TransactionId] = new OwnerCompletedFeedTransaction
        {
            Response = response,
            CompletedAtTicks = GetNetworkTimeTicks()
        };
        SendFeedResultToServer(response);
    }

    private static void ReceiveFeedResultOnServer(long sender, ZPackage pkg)
    {
        if (!IsLocalNetworkServer() ||
            !FeedResponseMessage.TryRead(pkg, out FeedResponseMessage? response) ||
            response == null ||
            !ServerManualFeedTransactions.TryGetValue(response.TransactionId, out ServerManualFeedTransaction? transaction) ||
            transaction.Phase != ManualFeedServerPhase.Committing ||
            !transaction.CommitOwnerPeerIds.Contains(sender) ||
            !response.TargetId.Equals(transaction.Request.TargetId) ||
            !response.ItemPrefabName.Equals(transaction.Request.ItemPrefabName, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (response.RetryOwner)
        {
            if (transaction.OwnerPeerId == sender)
            {
                transaction.OwnerPeerId = 0L;
                if (transaction.CommitOwnerPeerIds.Count == 1)
                {
                    transaction.CommitForwarded = false;
                }
            }

            transaction.UpdatedAtTicks = GetNetworkTimeTicks();
            return;
        }

        transaction.Result = response;
        transaction.Phase = ManualFeedServerPhase.Completed;
        transaction.UpdatedAtTicks = GetNetworkTimeTicks();
        ReleaseServerFeedLock(transaction);
        SendFeedResultToClient(transaction);
    }

    private static void ReceiveFeedResultOnClient(long sender, ZPackage pkg)
    {
        if (!IsMessageFromServer(sender) ||
            !FeedResponseMessage.TryRead(pkg, out FeedResponseMessage? response) ||
            response == null ||
            !PendingManualFeedTransactions.TryGetValue(response.TransactionId, out PendingManualFeedTransaction? pending) ||
            !response.TargetId.Equals(pending.Request.TargetId) ||
            !response.ItemPrefabName.Equals(pending.Request.ItemPrefabName, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        Player? player = Player.m_localPlayer;
        if ((object?)player == null)
        {
            return;
        }

        int escrowCount = CountEscrowItems(pending);
        if (!response.Success || response.ConsumedAmount <= 0 || response.ConsumedAmount > escrowCount)
        {
            ReturnEscrowItems(player, pending, escrowCount);
            PendingManualFeedTransactions.Remove(response.TransactionId);
            if (!string.IsNullOrWhiteSpace(response.Message))
            {
                player.Message(MessageHud.MessageType.Center, response.Message);
            }

            return;
        }

        ReturnEscrowItems(player, pending, escrowCount - response.ConsumedAmount);
        pending.EscrowItems.Clear();
        PendingManualFeedTransactions.Remove(response.TransactionId);
        ShowManualFeedResult(player, response);
    }

    private static void ShowManualFeedResult(Player player, FeedResponseMessage response)
    {
        Vector3 basePosition = response.DisplayPosition;
        int line = 0;
        if (response.TameDelta > 0)
        {
            ShowFloatingText(basePosition + Vector3.up * (0.35f * line++), $"+{response.TameDelta}s", DamageText.TextType.Bonus);
        }

        if (response.GrowthDelta > 0)
        {
            ShowFloatingText(basePosition + Vector3.up * (0.35f * line++), $"+{response.GrowthDelta}s", DamageText.TextType.Bonus);
        }

        if (response.FedDelta > 0f)
        {
            ShowFloatingText(basePosition + Vector3.up * (0.35f * line++), FeedLocalization.Format("$flg_floating_fed", FormatDurationClock(response.FedDelta)), DamageText.TextType.Bonus);
        }

        if (response.StarDelta > 0)
        {
            ShowFloatingText(basePosition + Vector3.up * (0.35f * line++), $"+{response.StarDelta}", DamageText.TextType.Bonus);
        }

        if (response.HealPercent > 0f)
        {
            ShowFloatingText(basePosition + Vector3.up * (0.35f * line), FeedLocalization.Format("$flg_floating_hp", response.HealPercent.ToString("0.#", CultureInfo.InvariantCulture)), DamageText.TextType.Heal);
        }

        if (!string.IsNullOrWhiteSpace(response.Message))
        {
            player.Message(MessageHud.MessageType.Center, response.Message);
        }
    }

    private static bool TryEscrowItems(
        Player player,
        string prefabName,
        int itemQuality,
        int amount,
        ICollection<ItemDrop.ItemData> escrowItems)
    {
        Inventory inventory = player.GetInventory();
        int remaining = amount;
        foreach (ItemDrop.ItemData item in inventory.GetAllItems().ToList())
        {
            if (!ItemMatches(item, prefabName, itemQuality))
            {
                continue;
            }

            int take = Mathf.Min(remaining, item.m_stack);
            ItemDrop.ItemData escrow = item.Clone();
            escrow.m_stack = take;
            escrowItems.Add(escrow);
            inventory.RemoveItem(item, take);
            remaining -= take;
            if (remaining <= 0)
            {
                return true;
            }
        }

        PendingManualFeedTransaction rollback = new() { EscrowItems = escrowItems.ToList() };
        ReturnEscrowItems(player, rollback, CountEscrowItems(rollback));

        escrowItems.Clear();
        return false;
    }

    private static int CountEscrowItems(PendingManualFeedTransaction pending)
    {
        return pending.EscrowItems.Sum(item => Mathf.Max(0, item.m_stack));
    }

    private static void ReturnEscrowItems(Player player, PendingManualFeedTransaction pending, int amount)
    {
        Inventory inventory = player.GetInventory();
        for (int i = pending.EscrowItems.Count - 1; i >= 0 && amount > 0; i--)
        {
            ItemDrop.ItemData escrow = pending.EscrowItems[i];
            while (escrow.m_stack > 0 && amount > 0)
            {
                ItemDrop.ItemData refund = escrow.Clone();
                refund.m_stack = 1;
                if (!inventory.AddItem(refund))
                {
                    ItemDrop.DropItem(
                        refund,
                        1,
                        player.transform.position + player.transform.forward + Vector3.up,
                        player.transform.rotation);
                }

                escrow.m_stack--;
                amount--;
            }

            if (escrow.m_stack <= 0)
            {
                pending.EscrowItems.RemoveAt(i);
            }
        }
    }

    private static bool TryAuthenticateNetworkPlayer(long sender, ZDOID playerId)
    {
        if (!IsLocalNetworkServer() || sender == 0L || playerId.IsNone())
        {
            return false;
        }

        if (sender == ZRoutedRpc.instance.m_id)
        {
            Player? localPlayer = Player.m_localPlayer;
            return (object?)localPlayer != null && localPlayer.GetZDOID().Equals(playerId);
        }

        ZNetPeer? peer = ZNet.instance.GetPeer(sender);
        return peer != null && peer.m_characterID.Equals(playerId);
    }

    private static bool TryResolveTargetOwner(ZDOID targetId, out long ownerPeerId)
    {
        ownerPeerId = 0L;
        ZDO? targetZdo = ZDOMan.instance?.GetZDO(targetId);
        if (targetZdo == null)
        {
            return false;
        }

        ownerPeerId = targetZdo.GetOwner();
        if (ownerPeerId != 0L)
        {
            return true;
        }

        GameObject? targetObject = ZNetScene.instance?.FindInstance(targetId);
        ZNetView? nview = targetObject?.GetComponent<ZNetView>();
        if ((object?)nview != null && nview.IsValid() && nview.IsOwner())
        {
            ownerPeerId = ZRoutedRpc.instance.m_id;
        }

        return ownerPeerId != 0L;
    }

    private static bool TryFindOwnedFeedTarget(ZDOID targetId, out GameObject? targetObject)
    {
        targetObject = ZNetScene.instance?.FindInstance(targetId);
        ZNetView? nview = targetObject?.GetComponent<ZNetView>();
        return (object?)targetObject != null &&
               (object?)nview != null &&
               nview.IsValid() &&
               nview.IsOwner();
    }

    private static bool IsLocalNetworkServer()
    {
        return ZNet.instance != null && ZNet.instance.IsServer() && ZRoutedRpc.instance != null;
    }

    private static bool IsMessageFromServer(long sender)
    {
        ZRoutedRpc? routedRpc = ZRoutedRpc.instance;
        return routedRpc != null && sender != 0L && sender == routedRpc.GetServerPeerID();
    }

    private static long GetNetworkTimeTicks()
    {
        return ZNet.instance != null ? ZNet.instance.GetTime().Ticks : DateTime.UtcNow.Ticks;
    }

    private static bool IsValidFeedRequest(FeedRequestMessage request)
    {
        return Guid.TryParseExact(request.TransactionId, "N", out _) &&
               !request.RequesterId.IsNone() &&
               !request.TargetId.IsNone() &&
               !string.IsNullOrWhiteSpace(request.ItemPrefabName) &&
               request.ItemPrefabName.Length <= 256 &&
               request.ItemQuality > 0 &&
               request.RequestedAmount > 0 &&
               request.RequestedAmount <= FeedLikeGrandmaPlugin.MaxItemsPerFeed.Value;
    }

    private static void FailFeedBeforeCommit(ServerManualFeedTransaction transaction, string message)
    {
        transaction.Prepared = new FeedPrepareMessage
        {
            TransactionId = transaction.Request.TransactionId,
            TargetId = transaction.Request.TargetId,
            ItemPrefabName = transaction.Request.ItemPrefabName,
            Message = message
        };
        transaction.UpdatedAtTicks = GetNetworkTimeTicks();
        SendFeedPreparedToClient(transaction);
        ReleaseServerFeedLock(transaction);
        ServerManualFeedTransactions.Remove(transaction.Request.TransactionId);
    }

    private static void SendFeedPreparedToClient(ServerManualFeedTransaction transaction)
    {
        if (transaction.Prepared != null)
        {
            SendFeedPreparedToPeer(transaction.RequesterPeerId, transaction.Prepared);
        }
    }

    private static void SendFeedPreparedToPeer(long peerId, FeedPrepareMessage prepared)
    {
        ZRoutedRpc.instance.InvokeRoutedRPC(peerId, FeedPreparedClientRpc, prepared.Write());
    }

    private static void SendFeedResultToServer(FeedResponseMessage response)
    {
        ZRoutedRpc.instance.InvokeRoutedRPC(
            ZRoutedRpc.instance.GetServerPeerID(),
            FeedResultServerRpc,
            response.Write());
    }

    private static void SendFeedResultToClient(ServerManualFeedTransaction transaction)
    {
        if (transaction.Result != null)
        {
            ZRoutedRpc.instance.InvokeRoutedRPC(
                transaction.RequesterPeerId,
                FeedResultClientRpc,
                transaction.Result.Write());
        }
    }

    private static void CompleteUndispatchedFeedFailure(
        ServerManualFeedTransaction transaction,
        string message)
    {
        transaction.Result = new FeedResponseMessage
        {
            TransactionId = transaction.Request.TransactionId,
            TargetId = transaction.Request.TargetId,
            ItemPrefabName = transaction.Request.ItemPrefabName,
            Message = message
        };
        transaction.Phase = ManualFeedServerPhase.Completed;
        transaction.UpdatedAtTicks = GetNetworkTimeTicks();
        ReleaseServerFeedLock(transaction);
        SendFeedResultToClient(transaction);
    }

    private static void ReleaseServerFeedLock(ServerManualFeedTransaction transaction)
    {
        if (ServerManualFeedLocks.TryGetValue(transaction.Request.TargetId, out string transactionId) &&
            transactionId.Equals(transaction.Request.TransactionId, StringComparison.Ordinal))
        {
            ServerManualFeedLocks.Remove(transaction.Request.TargetId);
        }
    }

    private static void CleanupServerFeedTransactions()
    {
        long nowTicks = GetNetworkTimeTicks();
        List<string>? expired = null;
        foreach (KeyValuePair<string, ServerManualFeedTransaction> entry in ServerManualFeedTransactions)
        {
            ServerManualFeedTransaction transaction = entry.Value;
            long retention = transaction.Phase == ManualFeedServerPhase.Completed
                ? ServerFeedCompletedRetentionTicks
                : ServerFeedPrepareRetentionTicks;
            if (transaction.Phase == ManualFeedServerPhase.Committing || nowTicks - transaction.UpdatedAtTicks <= retention)
            {
                continue;
            }

            expired ??= new List<string>();
            expired.Add(entry.Key);
        }

        if (expired != null)
        {
            foreach (string transactionId in expired)
            {
                ServerManualFeedTransaction transaction = ServerManualFeedTransactions[transactionId];
                ReleaseServerFeedLock(transaction);
                ServerManualFeedTransactions.Remove(transactionId);
            }
        }

        while (ServerManualFeedTransactions.Count >= MaxServerFeedTransactions)
        {
            string? oldestId = null;
            long oldestTicks = long.MaxValue;
            foreach (KeyValuePair<string, ServerManualFeedTransaction> entry in ServerManualFeedTransactions)
            {
                if (entry.Value.Phase == ManualFeedServerPhase.Completed &&
                    entry.Value.UpdatedAtTicks < oldestTicks)
                {
                    oldestId = entry.Key;
                    oldestTicks = entry.Value.UpdatedAtTicks;
                }
            }

            if (oldestId == null)
            {
                break;
            }

            ServerManualFeedTransactions.Remove(oldestId);
        }
    }

    private static void CleanupOwnerCompletedFeedTransactions()
    {
        long minimumTicks = GetNetworkTimeTicks() - ServerFeedCompletedRetentionTicks;
        List<string>? expired = null;
        foreach (KeyValuePair<string, OwnerCompletedFeedTransaction> entry in OwnerCompletedFeedTransactions)
        {
            if (entry.Value.CompletedAtTicks >= minimumTicks)
            {
                continue;
            }

            expired ??= new List<string>();
            expired.Add(entry.Key);
        }

        if (expired != null)
        {
            foreach (string transactionId in expired)
            {
                OwnerCompletedFeedTransactions.Remove(transactionId);
            }
        }
    }

    private enum ManualFeedClientPhase
    {
        Preparing,
        Committing
    }

    private enum ManualFeedServerPhase
    {
        Preparing,
        Prepared,
        Committing,
        Completed
    }

    private sealed class PendingManualFeedTransaction
    {
        public FeedRequestMessage Request { get; set; } = null!;
        public List<ItemDrop.ItemData> EscrowItems { get; init; } = new();
        public ManualFeedClientPhase Phase { get; set; }
        public float StartedRealtime { get; init; }
        public float LastSentRealtime { get; set; }
    }

    private sealed class ServerManualFeedTransaction
    {
        public FeedRequestMessage Request { get; set; } = null!;
        public int InitialRequestedAmount { get; init; }
        public long RequesterPeerId { get; set; }
        public long OwnerPeerId { get; set; }
        public HashSet<long> PrepareOwnerPeerIds { get; } = new();
        public HashSet<long> CommitOwnerPeerIds { get; } = new();
        public bool CommitForwarded { get; set; }
        public ManualFeedServerPhase Phase { get; set; }
        public FeedPrepareMessage? Prepared { get; set; }
        public FeedResponseMessage? Result { get; set; }
        public long UpdatedAtTicks { get; set; }

        public bool Matches(FeedRequestMessage request)
        {
            return Request.RequesterId.Equals(request.RequesterId) &&
                   Request.TargetId.Equals(request.TargetId) &&
                   Request.ItemQuality == request.ItemQuality &&
                   InitialRequestedAmount == request.RequestedAmount &&
                   Request.ItemPrefabName.Equals(request.ItemPrefabName, StringComparison.OrdinalIgnoreCase);
        }

        public bool MatchesExceptAmount(FeedRequestMessage request)
        {
            return Request.RequesterId.Equals(request.RequesterId) &&
                   Request.TargetId.Equals(request.TargetId) &&
                   Request.ItemQuality == request.ItemQuality &&
                   Request.ItemPrefabName.Equals(request.ItemPrefabName, StringComparison.OrdinalIgnoreCase);
        }
    }

    private sealed class OwnerCompletedFeedTransaction
    {
        public FeedResponseMessage Response { get; init; } = null!;
        public long CompletedAtTicks { get; init; }
    }

    private sealed class FeedRequestMessage
    {
        public string TransactionId { get; init; } = string.Empty;
        public ZDOID RequesterId { get; init; }
        public ZDOID TargetId { get; init; }
        public string ItemPrefabName { get; init; } = string.Empty;
        public int ItemQuality { get; init; } = 1;
        public int RequestedAmount { get; init; }

        public FeedRequestMessage WithAmount(int amount)
        {
            return new FeedRequestMessage
            {
                TransactionId = TransactionId,
                RequesterId = RequesterId,
                TargetId = TargetId,
                ItemPrefabName = ItemPrefabName,
                ItemQuality = ItemQuality,
                RequestedAmount = amount
            };
        }

        public ZPackage Write()
        {
            ZPackage pkg = new();
            pkg.Write(TransactionId);
            pkg.Write(RequesterId);
            pkg.Write(TargetId);
            pkg.Write(ItemPrefabName);
            pkg.Write(ItemQuality);
            pkg.Write(RequestedAmount);
            return pkg;
        }

        public static bool TryRead(ZPackage pkg, out FeedRequestMessage? message)
        {
            message = null;
            try
            {
                message = new FeedRequestMessage
                {
                    TransactionId = pkg.ReadString(),
                    RequesterId = pkg.ReadZDOID(),
                    TargetId = pkg.ReadZDOID(),
                    ItemPrefabName = pkg.ReadString(),
                    ItemQuality = pkg.ReadInt(),
                    RequestedAmount = pkg.ReadInt()
                };
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }
    }

    private sealed class FeedPrepareMessage
    {
        public string TransactionId { get; init; } = string.Empty;
        public bool Success { get; set; }
        public bool RetryOwner { get; set; }
        public int AcceptedAmount { get; set; }
        public ZDOID TargetId { get; init; }
        public string ItemPrefabName { get; init; } = string.Empty;
        public string Message { get; set; } = string.Empty;

        public ZPackage Write()
        {
            ZPackage pkg = new();
            pkg.Write(TransactionId);
            pkg.Write(Success);
            pkg.Write(RetryOwner);
            pkg.Write(AcceptedAmount);
            pkg.Write(TargetId);
            pkg.Write(ItemPrefabName);
            pkg.Write(Message);
            return pkg;
        }

        public static bool TryRead(ZPackage pkg, out FeedPrepareMessage? message)
        {
            message = null;
            try
            {
                message = new FeedPrepareMessage
                {
                    TransactionId = pkg.ReadString(),
                    Success = pkg.ReadBool(),
                    RetryOwner = pkg.ReadBool(),
                    AcceptedAmount = pkg.ReadInt(),
                    TargetId = pkg.ReadZDOID(),
                    ItemPrefabName = pkg.ReadString(),
                    Message = pkg.ReadString()
                };
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }
    }

    private sealed class FeedResponseMessage
    {
        public string TransactionId { get; init; } = string.Empty;
        public bool Success { get; set; }
        public bool RetryOwner { get; set; }
        public int ConsumedAmount { get; set; }
        public string ItemPrefabName { get; init; } = string.Empty;
        public int TameDelta { get; set; }
        public int GrowthDelta { get; set; }
        public int StarDelta { get; set; }
        public float FedDelta { get; set; }
        public float HealPercent { get; set; }
        public ZDOID TargetId { get; set; }
        public Vector3 DisplayPosition { get; set; }
        public string Message { get; set; } = string.Empty;

        public string ToBase64()
        {
            return Convert.ToBase64String(Write().GetArray());
        }

        public ZPackage Write()
        {
            ZPackage pkg = new();
            pkg.Write(TransactionId);
            pkg.Write(Success);
            pkg.Write(RetryOwner);
            pkg.Write(ConsumedAmount);
            pkg.Write(ItemPrefabName);
            pkg.Write(TameDelta);
            pkg.Write(GrowthDelta);
            pkg.Write(StarDelta);
            pkg.Write(FedDelta);
            pkg.Write(HealPercent);
            pkg.Write(TargetId);
            pkg.Write(DisplayPosition);
            pkg.Write(Message);
            return pkg;
        }

        public static bool TryReadBase64(string text, out FeedResponseMessage? message)
        {
            message = null;
            if (string.IsNullOrWhiteSpace(text))
            {
                return false;
            }

            try
            {
                return TryRead(new ZPackage(Convert.FromBase64String(text)), out message);
            }
            catch (Exception)
            {
                return false;
            }
        }

        public static bool TryRead(ZPackage pkg, out FeedResponseMessage? message)
        {
            message = null;
            try
            {
                message = new FeedResponseMessage
                {
                    TransactionId = pkg.ReadString(),
                    Success = pkg.ReadBool(),
                    RetryOwner = pkg.ReadBool(),
                    ConsumedAmount = pkg.ReadInt(),
                    ItemPrefabName = pkg.ReadString(),
                    TameDelta = pkg.ReadInt(),
                    GrowthDelta = pkg.ReadInt(),
                    StarDelta = pkg.ReadInt(),
                    FedDelta = pkg.ReadSingle(),
                    HealPercent = pkg.ReadSingle(),
                    TargetId = pkg.ReadZDOID(),
                    DisplayPosition = pkg.ReadVector3(),
                    Message = pkg.ReadString()
                };
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }
    }
}
