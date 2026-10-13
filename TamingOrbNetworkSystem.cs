using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace FeedLikeGrandma;

internal static partial class TamingOrbSystem
{
    // The server arbitrates locks, the creature owner captures live state, and the requesting peer owns release spawning.
    private const string CaptureRequestServerRpc = "FLG_OrbCaptureRequestServer_v3";
    private const string CapturePrepareOwnerRpc = "FLG_OrbCapturePrepareOwner_v3";
    private const string CapturePreparedServerRpc = "FLG_OrbCapturePreparedServer_v3";
    private const string CapturePreparedClientRpc = "FLG_OrbCapturePreparedClient_v3";
    private const string CaptureCommitServerRpc = "FLG_OrbCaptureCommitServer_v3";
    private const string CaptureCommitOwnerRpc = "FLG_OrbCaptureCommitOwner_v3";
    private const string CaptureResultServerRpc = "FLG_OrbCaptureResultServer_v3";
    private const string CaptureResultClientRpc = "FLG_OrbCaptureResultClient_v3";
    private const string ReleaseRequestServerRpc = "FLG_OrbReleaseRequestServer_v3";
    private const string ReleaseApprovedClientRpc = "FLG_OrbReleaseApprovedClient_v3";
    private const string ReleaseResultServerRpc = "FLG_OrbReleaseResultServer_v3";
    private const string ReleaseResultClientRpc = "FLG_OrbReleaseResultClient_v3";
    private const string OrbIdentityKey = "flg.tame.orbId";
    private const float OrbNetworkRetrySeconds = 1f;
    private const float OrbPrepareTimeoutSeconds = 30f;
    private const long OrbPendingRetentionTicks = TimeSpan.TicksPerMinute * 2;
    private const long OrbCompletedRetentionTicks = TimeSpan.TicksPerMinute * 5;
    private const int MaxOrbServerTransactions = 512;
    private static readonly int ReleaseTransactionHash = "flg_taming_orb_release_tx".GetStableHashCode();
    private static readonly int ReleaseOrbHash = "flg_taming_orb_release_orb".GetStableHashCode();
    private static readonly int ReleaseOriginHash = "flg_taming_orb_release_origin".GetStableHashCode();
    private static readonly int ReleaseReadyHash = "flg_taming_orb_release_ready".GetStableHashCode();
    private static readonly int CaptureTransactionHash = "flg_taming_orb_capture_tx".GetStableHashCode();

    private static readonly Dictionary<string, ClientCaptureTransaction> ClientCaptureTransactions = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, ServerCaptureTransaction> ServerCaptureTransactions = new(StringComparer.Ordinal);
    private static readonly Dictionary<ZDOID, string> ServerCaptureLocks = new();
    private static readonly Dictionary<string, OwnerCaptureTransaction> OwnerCaptureResults = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, ClientReleaseTransaction> ClientReleaseTransactions = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, ServerReleaseTransaction> ServerReleaseTransactions = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, string> ServerOrbActionLocks = new(StringComparer.Ordinal);
    private static ZRoutedRpc? _orbRpcInstance;

    private static void RegisterOrbNetworkRpcsCore()
    {
        ZRoutedRpc? routedRpc = ZRoutedRpc.instance;
        if (routedRpc == null || ReferenceEquals(_orbRpcInstance, routedRpc))
        {
            return;
        }

        ShutdownOrbTransactions();

        RegisterOrbRpc(routedRpc, CaptureRequestServerRpc, ReceiveCaptureRequestOnServer);
        RegisterOrbRpc(routedRpc, CapturePrepareOwnerRpc, ReceiveCapturePrepareOnOwner);
        RegisterOrbRpc(routedRpc, CapturePreparedServerRpc, ReceiveCapturePreparedOnServer);
        RegisterOrbRpc(routedRpc, CapturePreparedClientRpc, ReceiveCapturePreparedOnClient);
        RegisterOrbRpc(routedRpc, CaptureCommitServerRpc, ReceiveCaptureCommitOnServer);
        RegisterOrbRpc(routedRpc, CaptureCommitOwnerRpc, ReceiveCaptureCommitOnOwner);
        RegisterOrbRpc(routedRpc, CaptureResultServerRpc, ReceiveCaptureResultOnServer);
        RegisterOrbRpc(routedRpc, CaptureResultClientRpc, ReceiveCaptureResultOnClient);
        RegisterOrbRpc(routedRpc, ReleaseRequestServerRpc, ReceiveReleaseRequestOnServer);
        RegisterOrbRpc(routedRpc, ReleaseApprovedClientRpc, ReceiveReleaseApprovalOnClient);
        RegisterOrbRpc(routedRpc, ReleaseResultServerRpc, ReceiveReleaseResultOnServer);
        RegisterOrbRpc(routedRpc, ReleaseResultClientRpc, ReceiveReleaseResultOnClient);
        RegisterOrbRpc(routedRpc, OrbRecoveryServerRpc, ReceiveOrbRecoveryOnServer);
        RegisterOrbRpc(routedRpc, OrbRecoveryClientRpc, ReceiveOrbRecoveryOnClient);
        _orbRpcInstance = routedRpc;
    }

    private static void RegisterOrbRpc(
        ZRoutedRpc routedRpc,
        string name,
        Action<long, ZPackage> handler)
    {
        if (!routedRpc.RpcHandlers().Contains(name.GetStableHashCode()))
        {
            routedRpc.Register(name, handler);
        }
    }

    private static void ShutdownOrbTransactions()
    {
        CloseOrbJournal();
        OrbRecoveryRequests.Clear();
        ClientCaptureTransactions.Clear();
        ServerCaptureTransactions.Clear();
        ServerCaptureLocks.Clear();
        OwnerCaptureResults.Clear();
        ClientReleaseTransactions.Clear();
        ServerReleaseTransactions.Clear();
        ServerOrbActionLocks.Clear();
        _orbRpcInstance = null;
    }

    private static bool BeginNetworkCapture(
        Player player,
        ItemDrop.ItemData item,
        ZDOID targetId,
        string expectedPrefab,
        float delay)
    {
        ZRoutedRpc? routedRpc = ZRoutedRpc.instance;
        long serverPeerId = routedRpc?.GetServerPeerID() ?? 0L;
        if (routedRpc == null || serverPeerId == 0L)
        {
            FeedLocalization.Message(player, "$flg_taming_orb_network_unavailable");
            return false;
        }

        OrbCaptureRequest request = new()
        {
            TransactionId = Guid.NewGuid().ToString("N"),
            RequesterId = player.GetZDOID(),
            TargetId = targetId,
            OrbId = GetOrCreateOrbIdentity(item, player.GetInventory()),
            ExpectedPrefab = expectedPrefab
        };
        ClientCaptureTransaction transaction = new()
        {
            Request = request,
            Item = item,
            StartedRealtime = Time.realtimeSinceStartup,
            LastSentRealtime = Time.realtimeSinceStartup
        };
        ClientCaptureTransactions[request.TransactionId] = transaction;
        if (!BeginOrbItemTransaction(item, request.OrbId, request.TransactionId, OrbPendingAction.Capture, request.Write()))
        {
            ClientCaptureTransactions.Remove(request.TransactionId);
            return false;
        }
        player.GetInventory().Changed();
        routedRpc.InvokeRoutedRPC(serverPeerId, CaptureRequestServerRpc, request.Write());
        FeedLikeGrandmaPlugin.Instance.StartCoroutine(CompleteCaptureDelay(request.TransactionId, delay));
        FeedLikeGrandmaPlugin.Instance.StartCoroutine(RetryCaptureTransaction(request.TransactionId));
        return true;
    }

    private static IEnumerator CompleteCaptureDelay(string transactionId, float delay)
    {
        yield return new WaitForSeconds(delay);
        if (ClientCaptureTransactions.TryGetValue(transactionId, out ClientCaptureTransaction? transaction))
        {
            transaction.DelayElapsed = true;
            TryCommitCaptureOnClient(transaction);
        }
    }

    private static IEnumerator RetryCaptureTransaction(string transactionId)
    {
        WaitForSecondsRealtime wait = new(OrbNetworkRetrySeconds);
        while (ClientCaptureTransactions.TryGetValue(transactionId, out ClientCaptureTransaction? transaction))
        {
            yield return wait;
            if (!ClientCaptureTransactions.TryGetValue(transactionId, out transaction))
            {
                yield break;
            }

            if ((object?)Player.m_localPlayer == null ||
                !Player.m_localPlayer.GetZDOID().Equals(transaction.Request.RequesterId))
            {
                ClientCaptureTransactions.Remove(transactionId);
                yield break;
            }

            if (transaction.Phase == OrbCaptureClientPhase.Requesting &&
                Time.realtimeSinceStartup - transaction.StartedRealtime >= OrbPrepareTimeoutSeconds)
            {
                ClientCaptureTransactions.Remove(transactionId);
                FeedLocalization.Message(Player.m_localPlayer, "$flg_taming_orb_recovery_blocked");
                yield break;
            }

            if (Time.realtimeSinceStartup - transaction.LastSentRealtime < OrbNetworkRetrySeconds)
            {
                continue;
            }

            transaction.LastSentRealtime = Time.realtimeSinceStartup;
            string rpc = transaction.Phase == OrbCaptureClientPhase.Requesting
                ? CaptureRequestServerRpc
                : CaptureCommitServerRpc;
            ZRoutedRpc.instance.InvokeRoutedRPC(
                ZRoutedRpc.instance.GetServerPeerID(),
                rpc,
                transaction.Request.Write());
        }
    }

    private static void ReceiveCaptureRequestOnServer(long sender, ZPackage pkg)
    {
        if (!IsOrbServer() ||
            !OrbCaptureRequest.TryRead(pkg, out OrbCaptureRequest? request) ||
            request == null ||
            !IsValidCaptureRequest(request) ||
            !AuthenticateOrbPlayer(sender, request.RequesterId))
        {
            return;
        }

        CleanupOrbServerTransactions();
        if (ServerCaptureTransactions.TryGetValue(request.TransactionId, out ServerCaptureTransaction? existing))
        {
            if (!existing.Request.Matches(request))
            {
                return;
            }

            existing.RequesterPeerId = sender;
            if (existing.Phase == OrbCaptureServerPhase.Completed && existing.Result != null)
            {
                SendCaptureResultToClient(existing);
            }
            else if (existing.Prepared != null)
            {
                SendCapturePreparedToClient(existing);
            }
            else
            {
                ForwardCapturePrepare(existing);
            }

            return;
        }

        if (!IsEnabled())
        {
            SendCapturePreparedToPeer(sender, OrbCapturePrepared.Failure(request, FeedLocalization.Text("$flg_taming_orb_disabled")));
            return;
        }
        if (ServerCaptureTransactions.Count >= MaxOrbServerTransactions)
        {
            SendCapturePreparedToPeer(sender, OrbCapturePrepared.Failure(request, FeedLocalization.Text("$flg_taming_orb_target_busy")));
            return;
        }

        if (HandleRecordedCaptureRequest(sender, request)) return;
        ZDO? requesterZdo = ZDOMan.instance.GetZDO(request.RequesterId);
        ZDO? targetZdo = ZDOMan.instance.GetZDO(request.TargetId);
        if (requesterZdo == null || targetZdo == null ||
            Vector3.Distance(requesterZdo.GetPosition(), targetZdo.GetPosition()) > CaptureRange + 1f)
        {
            SendCapturePreparedToPeer(sender, OrbCapturePrepared.Failure(request, FeedLocalization.Text("$flg_taming_orb_too_far")));
            return;
        }

        if (!FeedWardCompatibility.IsAllowed(requesterZdo, targetZdo.GetPosition()))
        {
            SendCapturePreparedToPeer(sender, OrbCapturePrepared.Failure(request, FeedLocalization.Text(FeedWardCompatibility.DeniedToken)));
            return;
        }

        if (!CanCaptureReleasedCreature(targetZdo) || HasUnresolvedCaptureForTarget(request.TargetId) ||
            FeedCuisineCompatibility.IsMilkBusyOnServer(request.TargetId))
        {
            SendCapturePreparedToPeer(sender, OrbCapturePrepared.Failure(request, FeedLocalization.Text("$flg_taming_orb_target_busy")));
            return;
        }
        if (ServerCaptureLocks.TryGetValue(request.TargetId, out string lockedBy) &&
            !lockedBy.Equals(request.TransactionId, StringComparison.Ordinal))
        {
            SendCapturePreparedToPeer(sender, OrbCapturePrepared.Failure(request, FeedLocalization.Text("$flg_taming_orb_target_busy")));
            return;
        }

        if (ServerOrbActionLocks.TryGetValue(request.OrbId, out string orbLockedBy) &&
            !orbLockedBy.Equals(request.TransactionId, StringComparison.Ordinal))
        {
            SendCapturePreparedToPeer(sender, OrbCapturePrepared.Failure(request, FeedLocalization.Text("$flg_taming_orb_pending")));
            return;
        }

        ServerCaptureTransaction transaction = new()
        {
            Request = request,
            RequesterPeerId = sender,
            Phase = OrbCaptureServerPhase.Preparing,
            UpdatedAtTicks = OrbNetworkTimeTicks()
        };
        if (!PersistCapture(transaction, TamingOrbJournal.Reserved))
        {
            SendCapturePreparedToPeer(sender, OrbCapturePrepared.Failure(request, FeedLocalization.Text("$flg_taming_orb_recovery_blocked")));
            return;
        }
        ServerCaptureTransactions[request.TransactionId] = transaction;
        ServerCaptureLocks[request.TargetId] = request.TransactionId;
        ServerOrbActionLocks[request.OrbId] = request.TransactionId;
        ForwardCapturePrepare(transaction);
    }

    internal static bool IsCuisineCaptureBlocked(ZDOID target)
    {
        if (!IsOrbServer()) return true;
        ZDO? zdo = ZDOMan.instance?.GetZDO(target);
        return zdo == null || ServerCaptureLocks.ContainsKey(target) ||
            !CanCaptureReleasedCreature(zdo) || HasUnresolvedCaptureForTarget(target);
    }

    private static void ForwardCapturePrepare(ServerCaptureTransaction transaction)
    {
        if (!TryResolveOrbTargetOwner(transaction.Request.TargetId, out long ownerPeerId))
        {
            FailCaptureBeforeCommit(transaction, FeedLocalization.Text("$flg_taming_orb_cannot_capture"));
            return;
        }

        transaction.OwnerPeerId = ownerPeerId;
        transaction.PrepareOwnerPeerIds.Add(ownerPeerId);
        transaction.UpdatedAtTicks = OrbNetworkTimeTicks();
        ZRoutedRpc.instance.InvokeRoutedRPC(ownerPeerId, CapturePrepareOwnerRpc, transaction.Request.Write());
    }

    private static void ReceiveCapturePrepareOnOwner(long sender, ZPackage pkg)
    {
        if (!IsOrbMessageFromServer(sender) ||
            !OrbCaptureRequest.TryRead(pkg, out OrbCaptureRequest? request) ||
            request == null)
        {
            return;
        }

        OrbCapturePrepared prepared = new()
        {
            TransactionId = request.TransactionId,
            TargetId = request.TargetId,
            ExpectedPrefab = request.ExpectedPrefab
        };
        if (!TryFindOwnedOrbTarget(request.TargetId, out Character? character, out Tameable? tameable, out ZNetView? nview))
        {
            prepared.RetryOwner = true;
        }
        else
        {
            CaptureEligibility eligibility = GetCaptureEligibility(character, nview);
            if (eligibility != CaptureEligibility.Allowed ||
                !TryGetPrefabName(((Component)character!).gameObject, out string prefabName) ||
                !prefabName.Equals(request.ExpectedPrefab, StringComparison.OrdinalIgnoreCase))
            {
                prepared.Message = FeedLocalization.Text("$flg_taming_orb_no_longer_capturable");
            }
            else
            {
                try
                {
                    nview!.GetZDO().Set(CaptureTransactionHash, request.TransactionId);
                    ZDOMan.instance.ForceSendZDO(nview.GetZDO().m_uid);
                    CapturedTamePayload payload = CapturePayload(character!, tameable, nview!, prefabName);
                    prepared.Payload = payload.Serialize();
                    prepared.CreatureManagerState =
                        FeedCreatureModifierCompatibility.CaptureCreatureManagerModifierState(character!);
                    prepared.Success = true;
                }
                catch (Exception ex)
                {
                    FeedLikeGrandmaPlugin.Log.LogWarning(
                        $"{PrefabName} owner failed to prepare capture for {prefabName}: {ex.Message}");
                    prepared.Message = FeedLocalization.Text("$flg_taming_orb_cannot_capture");
                }
            }
        }

        ZRoutedRpc.instance.InvokeRoutedRPC(
            ZRoutedRpc.instance.GetServerPeerID(),
            CapturePreparedServerRpc,
            prepared.Write());
    }

    private static void ReceiveCapturePreparedOnServer(long sender, ZPackage pkg)
    {
        if (!IsOrbServer() ||
            !OrbCapturePrepared.TryRead(pkg, out OrbCapturePrepared? prepared) ||
            prepared == null ||
            !ServerCaptureTransactions.TryGetValue(prepared.TransactionId, out ServerCaptureTransaction? transaction) ||
            transaction.Phase != OrbCaptureServerPhase.Preparing ||
            !transaction.PrepareOwnerPeerIds.Contains(sender) ||
            !prepared.TargetId.Equals(transaction.Request.TargetId) ||
            !prepared.ExpectedPrefab.Equals(transaction.Request.ExpectedPrefab, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (prepared.RetryOwner)
        {
            if (transaction.OwnerPeerId == sender)
            {
                transaction.OwnerPeerId = 0L;
            }

            transaction.UpdatedAtTicks = OrbNetworkTimeTicks();
            return;
        }

        if (!prepared.Success ||
            prepared.Payload.Length > 1024 * 1024 ||
            prepared.CreatureManagerState.Length > 1024 * 1024 ||
            !CapturedTamePayload.TryDeserialize(prepared.Payload, out CapturedTamePayload? payload, logWarnings: false) ||
            payload == null ||
            !payload.Prefab.Equals(transaction.Request.ExpectedPrefab, StringComparison.OrdinalIgnoreCase))
        {
            FailCaptureBeforeCommit(transaction, prepared.Message);
            return;
        }

        transaction.Prepared = prepared;
        if (!PersistCapture(transaction, TamingOrbJournal.Reserved)) return;
        transaction.Phase = OrbCaptureServerPhase.Prepared;
        transaction.UpdatedAtTicks = OrbNetworkTimeTicks();
        SendCapturePreparedToClient(transaction);
    }

    private static void ReceiveCapturePreparedOnClient(long sender, ZPackage pkg)
    {
        if (!IsOrbMessageFromServer(sender) ||
            !OrbCapturePrepared.TryRead(pkg, out OrbCapturePrepared? prepared) ||
            prepared == null ||
            !ClientCaptureTransactions.TryGetValue(prepared.TransactionId, out ClientCaptureTransaction? transaction) ||
            !prepared.TargetId.Equals(transaction.Request.TargetId) ||
            !prepared.ExpectedPrefab.Equals(transaction.Request.ExpectedPrefab, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (transaction.Phase == OrbCaptureClientPhase.Committing) return;
        if (!prepared.Success)
        {
            FinishCaptureClientFailure(transaction, prepared.Message);
            return;
        }

        transaction.Prepared = prepared;
        TryCommitCaptureOnClient(transaction);
    }

    private static void TryCommitCaptureOnClient(ClientCaptureTransaction transaction)
    {
        if (!transaction.DelayElapsed || transaction.Prepared == null ||
            transaction.Phase != OrbCaptureClientPhase.Requesting)
        {
            return;
        }

        Player? player = Player.m_localPlayer;
        ItemDrop.ItemData? currentItem = FindTransactionItem(transaction.Request.TransactionId, transaction.Request.OrbId);
        if (currentItem != null) transaction.Item = currentItem;
        if ((object?)player == null || currentItem == null ||
            transaction.Item.m_customData.ContainsKey(PayloadKey))
        {
            FinishCaptureClientFailure(transaction, FeedLocalization.Text("$flg_taming_orb_data_changed"));
            return;
        }

        if (!TryReadPendingOrbItem(transaction.Item, out OrbPendingItemState? pending) || pending == null)
            return;

        if (!CapturedTamePayload.TryDeserialize(
                transaction.Prepared.Payload,
                out CapturedTamePayload? payload,
                logWarnings: false) ||
            payload == null)
        {
            FinishCaptureClientFailure(transaction, FeedLocalization.Text("$flg_taming_orb_data_invalid"));
            return;
        }

        try
        {
            _ = GetIconVariantForPayload(transaction.Item, payload);
            pending.Phase = OrbPendingPhase.Committing;
            pending.PreparedData = transaction.Prepared.Write().GetBase64();
            if (!TryUpdatePendingOrbItem(transaction.Item, pending))
                throw new InvalidOperationException("Orb transaction identity changed during capture.");
            transaction.Item.m_stack = 1;
            transaction.Item.m_customData[PayloadKey] = transaction.Prepared.Payload;
            SetOrRemoveCustomData(
                transaction.Item,
                CreatureManagerStateKey,
                transaction.Prepared.CreatureManagerState);
            transaction.Item.m_variant = 0;
            player.GetInventory().Changed();
        }
        catch (Exception ex)
        {
            FeedLikeGrandmaPlugin.Log.LogWarning($"{PrefabName} failed to stage capture: {ex.Message}");
            FinishCaptureClientFailure(transaction, FeedLocalization.Text("$flg_taming_orb_cannot_capture"));
            return;
        }

        transaction.Phase = OrbCaptureClientPhase.Committing;
        transaction.LastSentRealtime = Time.realtimeSinceStartup;
        ZRoutedRpc.instance.InvokeRoutedRPC(
            ZRoutedRpc.instance.GetServerPeerID(),
            CaptureCommitServerRpc,
            transaction.Request.Write());
    }

    private static void ReceiveCaptureCommitOnServer(long sender, ZPackage pkg)
    {
        if (!IsOrbServer() ||
            !OrbCaptureRequest.TryRead(pkg, out OrbCaptureRequest? request) ||
            request == null ||
            !AuthenticateOrbPlayer(sender, request.RequesterId) ||
            !ServerCaptureTransactions.TryGetValue(request.TransactionId, out ServerCaptureTransaction? transaction) ||
            !transaction.Request.Matches(request))
        {
            return;
        }

        transaction.RequesterPeerId = sender;
        if (transaction.Phase == OrbCaptureServerPhase.Completed && transaction.Result != null)
        {
            SendCaptureResultToClient(transaction);
            return;
        }

        if (transaction.Prepared == null ||
            transaction.Phase is not (OrbCaptureServerPhase.Prepared or OrbCaptureServerPhase.Committing))
        {
            FailCaptureBeforeCommit(transaction, FeedLocalization.Text("$flg_taming_orb_data_changed"));
            return;
        }

        ZDO? captureTarget = ZDOMan.instance.GetZDO(request.TargetId);
        if (captureTarget != null && !CanCaptureReleasedCreature(captureTarget)) return;
        if (!PersistCapture(transaction, TamingOrbJournal.Committing)) return;
        transaction.Phase = OrbCaptureServerPhase.Committing;
        ForwardCaptureCommit(transaction);
    }

    private static void ForwardCaptureCommit(ServerCaptureTransaction transaction)
    {
        if (!EnsureOrbJournal()) return;
        if (transaction.PendingResult != null && ZDOMan.instance.GetZDO(transaction.Request.TargetId) == null)
        {
            CompleteCaptureResult(transaction, transaction.PendingResult);
            return;
        }
        if (!TryResolveOrbTargetOwner(transaction.Request.TargetId, out long ownerPeerId))
        {
            if (!transaction.CommitForwarded && ZDOMan.instance?.GetZDO(transaction.Request.TargetId) == null)
            {
                CompleteUndispatchedCaptureFailure(
                    transaction,
                    FeedLocalization.Text("$flg_taming_orb_no_longer_capturable"));
            }

            // Once dispatched, a missing target may mean the owner already destroyed it; retain the lock and retry.
            return;
        }

        // Recheck just before the first destructive dispatch, after the capture delay.
        // Later retries may refer to an already deleted animal and must still resolve.
        if (!transaction.CommitForwarded)
        {
            ZDO? requester = ZDOMan.instance.GetZDO(transaction.Request.RequesterId);
            ZDO? target = ZDOMan.instance.GetZDO(transaction.Request.TargetId);
            if (target == null || !FeedWardCompatibility.IsAllowed(requester, target.GetPosition()))
            {
                CompleteUndispatchedCaptureFailure(transaction, FeedLocalization.Text(FeedWardCompatibility.DeniedToken));
                return;
            }
        }

        transaction.OwnerPeerId = ownerPeerId;
        transaction.CommitOwnerPeerIds.Add(ownerPeerId);
        transaction.CommitForwarded = true;
        transaction.UpdatedAtTicks = OrbNetworkTimeTicks();
        ZRoutedRpc.instance.InvokeRoutedRPC(ownerPeerId, CaptureCommitOwnerRpc, transaction.Request.Write());
    }

    private static void ReceiveCaptureCommitOnOwner(long sender, ZPackage pkg)
    {
        if (!IsOrbMessageFromServer(sender) ||
            !OrbCaptureRequest.TryRead(pkg, out OrbCaptureRequest? request) ||
            request == null)
        {
            return;
        }

        CleanupOwnerCaptureResults();
        if (OwnerCaptureResults.TryGetValue(request.TransactionId, out OwnerCaptureTransaction? completed))
        {
            SendCaptureResultToServer(completed.Result);
            return;
        }

        OrbCaptureResult result = new()
        {
            TransactionId = request.TransactionId,
            TargetId = request.TargetId,
            ExpectedPrefab = request.ExpectedPrefab
        };
        if (!TryFindOwnedOrbTarget(request.TargetId, out Character? character, out _, out ZNetView? nview))
        {
            result.RetryOwner = true;
        }
        else if (GetCaptureEligibility(character, nview) != CaptureEligibility.Allowed ||
                 !TryGetPrefabName(((Component)character!).gameObject, out string prefabName) ||
                 !prefabName.Equals(request.ExpectedPrefab, StringComparison.OrdinalIgnoreCase))
        {
            result.Message = FeedLocalization.Text("$flg_taming_orb_no_longer_capturable");
        }
        else
        {
            try
            {
                result.Success = true;
                OwnerCaptureResults[request.TransactionId] = new OwnerCaptureTransaction
                {
                    Result = result,
                    CompletedAtTicks = OrbNetworkTimeTicks()
                };
                nview!.Destroy();
            }
            catch (Exception ex)
            {
                // Destroy may have queued the deletion before throwing. Keep the success candidate;
                // the server still requires actual disappearance before committing it.
                FeedLikeGrandmaPlugin.Log.LogWarning(
                    $"{PrefabName} owner failed to commit capture for {request.ExpectedPrefab}: {ex.Message}");
            }
        }

        SendCaptureResultToServer(result);
    }

    private static void ReceiveCaptureResultOnServer(long sender, ZPackage pkg)
    {
        if (!IsOrbServer() ||
            !OrbCaptureResult.TryRead(pkg, out OrbCaptureResult? result) ||
            result == null ||
            !ServerCaptureTransactions.TryGetValue(result.TransactionId, out ServerCaptureTransaction? transaction) ||
            transaction.Phase != OrbCaptureServerPhase.Committing ||
            !transaction.CommitOwnerPeerIds.Contains(sender) ||
            !result.TargetId.Equals(transaction.Request.TargetId) ||
            !result.ExpectedPrefab.Equals(transaction.Request.ExpectedPrefab, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (result.RetryOwner)
        {
            if (transaction.OwnerPeerId == sender)
            {
                transaction.OwnerPeerId = 0L;
                if (transaction.CommitOwnerPeerIds.Count == 1)
                {
                    transaction.CommitForwarded = false;
                }
            }

            transaction.UpdatedAtTicks = OrbNetworkTimeTicks();
            return;
        }

        if (transaction.PendingResult != null && !result.Success) return;
        if (result.Success && ZDOMan.instance.GetZDO(transaction.Request.TargetId) != null)
        {
            // Owner Destroy queues a network deletion; success is final only after the server observes it.
            transaction.PendingResult = result;
            return;
        }
        CompleteCaptureResult(transaction, result);
    }

    private static void CompleteCaptureResult(ServerCaptureTransaction transaction, OrbCaptureResult result)
    {
        if (!PersistCapture(transaction, result.Success ? TamingOrbJournal.CompletedSuccess : TamingOrbJournal.CompletedFailure, result)) return;
        transaction.Result = result;
        transaction.Phase = OrbCaptureServerPhase.Completed;
        transaction.UpdatedAtTicks = OrbNetworkTimeTicks();
        ReleaseCaptureLock(transaction);
        SendCaptureResultToClient(transaction);
    }

    private static void ReceiveCaptureResultOnClient(long sender, ZPackage pkg)
    {
        if (!IsOrbMessageFromServer(sender) ||
            !OrbCaptureResult.TryRead(pkg, out OrbCaptureResult? result) ||
            result == null ||
            !ClientCaptureTransactions.TryGetValue(result.TransactionId, out ClientCaptureTransaction? transaction) ||
            !result.TargetId.Equals(transaction.Request.TargetId))
        {
            return;
        }

        Player? player = Player.m_localPlayer;
        if (!result.Success)
        {
            FinishCaptureClientFailure(transaction, result.Message);
            return;
        }

        ClientCaptureTransactions.Remove(result.TransactionId);
        ResolveOrbItem(transaction.Request.TransactionId, transaction.Request.OrbId, true, transaction.Prepared?.Write().GetBase64() ?? string.Empty);
        if ((object?)player != null &&
            CapturedTamePayload.TryDeserialize(transaction.Prepared!.Payload, out CapturedTamePayload? payload, logWarnings: false) &&
            payload != null)
        {
            FeedLocalization.Message(player, "$flg_taming_orb_captured", GetPayloadDisplayName(payload));
        }
    }

    private static bool BeginNetworkRelease(
        Player player,
        ItemDrop.ItemData item,
        CapturedTamePayload payload,
        string creatureManagerState,
        GameObject creaturePrefab,
        Vector3 spawnPosition,
        Quaternion spawnRotation,
        float delay)
    {
        ZRoutedRpc? routedRpc = ZRoutedRpc.instance;
        long serverPeerId = routedRpc?.GetServerPeerID() ?? 0L;
        if (routedRpc == null || serverPeerId == 0L)
        {
            FeedLocalization.Message(player, "$flg_taming_orb_network_unavailable");
            return false;
        }

        string orbId = GetOrCreateOrbIdentity(item, player.GetInventory());
        OrbReleaseRequest request = new()
        {
            TransactionId = Guid.NewGuid().ToString("N"),
            RequesterId = player.GetZDOID(),
            OrbId = orbId,
            Payload = payload.Serialize(),
            CreatureManagerState = creatureManagerState,
            SpawnPosition = spawnPosition,
            SpawnRotation = spawnRotation
        };
        ClientReleaseTransaction transaction = new()
        {
            Request = request,
            Item = item,
            Payload = payload,
            CreaturePrefab = creaturePrefab,
            StartedRealtime = Time.realtimeSinceStartup,
            LastSentRealtime = Time.realtimeSinceStartup
        };
        ClientReleaseTransactions[request.TransactionId] = transaction;
        if (!BeginOrbItemTransaction(item, request.OrbId, request.TransactionId, OrbPendingAction.Release, request.Write()))
        {
            ClientReleaseTransactions.Remove(request.TransactionId);
            return false;
        }
        player.GetInventory().Changed();
        routedRpc.InvokeRoutedRPC(serverPeerId, ReleaseRequestServerRpc, request.Write());
        FeedLikeGrandmaPlugin.Instance.StartCoroutine(CompleteReleaseDelay(request.TransactionId, delay));
        FeedLikeGrandmaPlugin.Instance.StartCoroutine(RetryReleaseTransaction(request.TransactionId));
        return true;
    }

    private static IEnumerator CompleteReleaseDelay(string transactionId, float delay)
    {
        yield return new WaitForSeconds(delay);
        if (ClientReleaseTransactions.TryGetValue(transactionId, out ClientReleaseTransaction? transaction))
        {
            transaction.DelayElapsed = true;
            TryExecuteReleaseOnClient(transaction);
        }
    }

    private static IEnumerator RetryReleaseTransaction(string transactionId)
    {
        WaitForSecondsRealtime wait = new(OrbNetworkRetrySeconds);
        while (ClientReleaseTransactions.TryGetValue(transactionId, out ClientReleaseTransaction? transaction))
        {
            yield return wait;
            if (!ClientReleaseTransactions.TryGetValue(transactionId, out transaction))
            {
                yield break;
            }

            if ((object?)Player.m_localPlayer == null ||
                !Player.m_localPlayer.GetZDOID().Equals(transaction.Request.RequesterId))
            {
                ClientReleaseTransactions.Remove(transactionId);
                yield break;
            }

            if (!transaction.SpawnAttempted &&
                Time.realtimeSinceStartup - transaction.StartedRealtime >= OrbPrepareTimeoutSeconds)
            {
                ClientReleaseTransactions.Remove(transactionId);
                FeedLocalization.Message(Player.m_localPlayer, "$flg_taming_orb_recovery_blocked");
                yield break;
            }

            if (Time.realtimeSinceStartup - transaction.LastSentRealtime < OrbNetworkRetrySeconds)
            {
                continue;
            }

            transaction.LastSentRealtime = Time.realtimeSinceStartup;
            bool resendResult = transaction.SpawnAttempted;
            ZRoutedRpc.instance.InvokeRoutedRPC(
                ZRoutedRpc.instance.GetServerPeerID(),
                ReleaseRequestServerRpc,
                transaction.Request.Write());
            if (resendResult && ClientReleaseTransactions.ContainsKey(transactionId) && transaction.Result != null)
            {
                SendReleaseResultToServer(transaction.Result);
            }
        }
    }

    private static void ReceiveReleaseRequestOnServer(long sender, ZPackage pkg)
    {
        if (!IsOrbServer() ||
            !OrbReleaseRequest.TryRead(pkg, out OrbReleaseRequest? request) ||
            request == null ||
            !IsValidReleaseRequest(request) ||
            !AuthenticateOrbPlayer(sender, request.RequesterId))
        {
            return;
        }

        CleanupOrbServerTransactions();
        if (ServerReleaseTransactions.TryGetValue(request.TransactionId, out ServerReleaseTransaction? existing))
        {
            if (!existing.Request.Matches(request))
            {
                return;
            }

            existing.RequesterPeerId = sender;
            if (existing.Result != null)
            {
                SendReleaseResultToClient(existing);
            }
            else
            {
                SendReleaseApprovalToClient(existing);
            }

            return;
        }

        if (!IsEnabled())
        {
            SendReleaseApprovalToPeer(sender, OrbReleaseApproval.Failure(request, FeedLocalization.Text("$flg_taming_orb_disabled")));
            return;
        }

        if (ServerReleaseTransactions.Count >= MaxOrbServerTransactions)
        {
            SendReleaseApprovalToPeer(sender, OrbReleaseApproval.Failure(request, FeedLocalization.Text("$flg_taming_orb_pending")));
            return;
        }

        if (HandleRecordedReleaseRequest(sender, request)) return;
        if (ServerOrbActionLocks.TryGetValue(request.OrbId, out string lockedBy) &&
            !lockedBy.Equals(request.TransactionId, StringComparison.Ordinal))
        {
            SendReleaseApprovalToPeer(sender, OrbReleaseApproval.Failure(request, FeedLocalization.Text("$flg_taming_orb_pending")));
            return;
        }

        ZDO? requesterZdo = ZDOMan.instance.GetZDO(request.RequesterId);
        if (requesterZdo == null ||
            Vector3.Distance(requesterZdo.GetPosition(), request.SpawnPosition) > ReleaseAimRange + ReleaseUpOffset + 2f)
        {
            SendReleaseApprovalToPeer(sender, OrbReleaseApproval.Failure(request, FeedLocalization.Text("$flg_taming_orb_release_failed")));
            return;
        }

        if (!FeedWardCompatibility.IsAllowed(requesterZdo, request.SpawnPosition))
        {
            SendReleaseApprovalToPeer(sender, OrbReleaseApproval.Failure(request, FeedLocalization.Text(FeedWardCompatibility.DeniedToken)));
            return;
        }

        ServerReleaseTransaction transaction = new()
        {
            Request = request,
            RequesterPeerId = sender,
            UpdatedAtTicks = OrbNetworkTimeTicks()
        };
        if (!PersistReleaseResult(transaction, null))
        {
            SendReleaseApprovalToPeer(sender, OrbReleaseApproval.Failure(request, FeedLocalization.Text("$flg_taming_orb_recovery_blocked")));
            return;
        }
        ServerReleaseTransactions[request.TransactionId] = transaction;
        ServerOrbActionLocks[request.OrbId] = request.TransactionId;
        SendReleaseApprovalToClient(transaction);
    }

    private static void ReceiveReleaseApprovalOnClient(long sender, ZPackage pkg)
    {
        if (!IsOrbMessageFromServer(sender) ||
            !OrbReleaseApproval.TryRead(pkg, out OrbReleaseApproval? approval) ||
            approval == null ||
            !ClientReleaseTransactions.TryGetValue(approval.TransactionId, out ClientReleaseTransaction? transaction) ||
            !approval.OrbId.Equals(transaction.Request.OrbId, StringComparison.Ordinal))
        {
            return;
        }

        if (!approval.Success)
        {
            if (transaction.SpawnAttempted)
            {
                // Once spawning has been attempted, only a final result can resolve the transaction.
                return;
            }

            FinishReleaseClient(transaction, success: false, approval.Message);
            return;
        }

        transaction.Approved = true;
        TryExecuteReleaseOnClient(transaction);
    }

    private static void TryExecuteReleaseOnClient(ClientReleaseTransaction transaction)
    {
        if (!transaction.DelayElapsed || !transaction.Approved || transaction.SpawnAttempted)
        {
            return;
        }

        Player? player = Player.m_localPlayer;
        ItemDrop.ItemData? currentItem = FindTransactionItem(transaction.Request.TransactionId, transaction.Request.OrbId);
        if (currentItem != null) transaction.Item = currentItem;
        if ((object?)player == null || currentItem == null ||
            !TryReadPayload(transaction.Item, out CapturedTamePayload? currentPayload, logWarnings: false) ||
            currentPayload == null ||
            !currentPayload.Serialize().Equals(transaction.Request.Payload, StringComparison.Ordinal) ||
            !transaction.Item.m_customData.TryGetValue(OrbIdentityKey, out string orbId) ||
            !orbId.Equals(transaction.Request.OrbId, StringComparison.Ordinal))
        {
            transaction.SpawnAttempted = true;
            transaction.Result = OrbReleaseResult.Failure(transaction.Request, FeedLocalization.Text("$flg_taming_orb_data_changed"));
            SendReleaseResultToServer(transaction.Result);
            return;
        }

        if (!FeedWardCompatibility.IsAllowed(player, transaction.Request.SpawnPosition))
        {
            transaction.SpawnAttempted = true;
            transaction.Result = OrbReleaseResult.Failure(transaction.Request, FeedLocalization.Text(FeedWardCompatibility.DeniedToken));
            SendReleaseResultToServer(transaction.Result);
            return;
        }

        if (!TryReadPendingOrbItem(transaction.Item, out OrbPendingItemState? pending) || pending == null)
            return;
        pending.Phase = OrbPendingPhase.Spawning;
        if (!TryUpdatePendingOrbItem(transaction.Item, pending)) return;
        player.GetInventory().Changed();
        transaction.SpawnAttempted = true;
        GameObject? spawned = null;
        try
        {
            byte[] inventory = transaction.Payload.DecodeInventory();
            spawned = UnityEngine.Object.Instantiate(
                transaction.CreaturePrefab,
                transaction.Request.SpawnPosition,
                transaction.Request.SpawnRotation);
            Character? character = spawned.GetComponent<Character>();
            Tameable? tameable = spawned.GetComponent<Tameable>();
            ZNetView? nview = spawned.GetComponent<ZNetView>();
            if ((object?)character == null || (object?)nview == null || !nview.IsValid())
            {
                throw new InvalidOperationException("Released prefab is not a valid networked creature.");
            }

            nview.GetZDO().Set(ReleaseTransactionHash, transaction.Request.TransactionId);
            nview.GetZDO().Set(ReleaseOrbHash, transaction.Request.OrbId);
            nview.GetZDO().Set(ReleaseOriginHash, transaction.Request.SpawnPosition);
            RestorePayload(transaction.Payload, inventory, character, tameable, nview);
            if (!string.IsNullOrWhiteSpace(transaction.Request.CreatureManagerState) &&
                !FeedCreatureModifierCompatibility.RestoreCreatureManagerModifierState(
                    character,
                    transaction.Request.CreatureManagerState))
            {
                throw new InvalidOperationException("CreatureManager state could not be restored.");
            }

            transaction.Spawned = spawned;
            transaction.Result = OrbReleaseResult.SuccessResult(
                transaction.Request,
                nview.GetZDO().m_uid);
            nview.GetZDO().Set(ReleaseReadyHash, 1);
            ZDOMan.instance.ForceSendZDO(nview.GetZDO().m_uid);
            ItemDrop.ItemData? pendingItem = FindTransactionItem(transaction.Request.TransactionId, transaction.Request.OrbId);
            if (pendingItem != null && TryReadPendingOrbItem(pendingItem, out OrbPendingItemState? savedPending) && savedPending != null)
            {
                ZDOID spawnedId = nview.GetZDO().m_uid;
                savedPending.SpawnedId = spawnedId.UserID.ToString(CultureInfo.InvariantCulture) + ":" + spawnedId.ID.ToString(CultureInfo.InvariantCulture);
                if (TryUpdatePendingOrbItem(pendingItem, savedPending)) player.GetInventory().Changed();
            }
        }
        catch (Exception ex)
        {
            if (transaction.Result?.Success == true)
            {
                // Restoration is complete. UI/item notification failures cannot roll the creature back.
                FeedLikeGrandmaPlugin.Log.LogWarning($"Orb release {transaction.Request.TransactionId} completion will be retried: {ex.Message}");
            }
            else if ((object?)spawned != null)
            {
                // Destruction is asynchronous. Never return a reusable filled orb on unconfirmed cleanup.
                try { DestroySpawnedRelease(spawned); }
                catch (Exception cleanupError) { FeedLikeGrandmaPlugin.Log.LogWarning($"Orb release cleanup is unresolved: {cleanupError.Message}"); }
                FeedLikeGrandmaPlugin.Log.LogWarning($"Orb release {transaction.Request.TransactionId} requires recovery after spawn failure: {ex.Message}");
                return;
            }
            else
            {
                FeedLikeGrandmaPlugin.Log.LogWarning(
                    $"{PrefabName} failed to release {transaction.Payload.Prefab}: {ex.Message}");
                transaction.Result = OrbReleaseResult.Failure(
                    transaction.Request,
                    FeedLocalization.Text("$flg_taming_orb_release_failed"));
            }
        }

        SendReleaseResultToServer(transaction.Result);
    }

    private static void ReceiveReleaseResultOnServer(long sender, ZPackage pkg)
    {
        if (!IsOrbServer() ||
            !OrbReleaseResult.TryRead(pkg, out OrbReleaseResult? result) ||
            result == null ||
            !ServerReleaseTransactions.TryGetValue(result.TransactionId, out ServerReleaseTransaction? transaction) ||
            transaction.RequesterPeerId != sender ||
            !result.OrbId.Equals(transaction.Request.OrbId, StringComparison.Ordinal) ||
            !AuthenticateOrbPlayer(sender, transaction.Request.RequesterId))
        {
            return;
        }

        if (transaction.Result != null)
        {
            SendReleaseResultToClient(transaction);
            return;
        }

        if (result.Success)
        {
            ZDO? spawnedZdo = ZDOMan.instance.GetZDO(result.SpawnedId);
            if (!IsVerifiedReleasedCreature(spawnedZdo, transaction.Request))
            {
                return;
            }
        }

        if (!PersistReleaseResult(transaction, result)) return;
        transaction.Result = result;
        transaction.UpdatedAtTicks = OrbNetworkTimeTicks();
        if (!result.Success) ReleaseReleaseLock(transaction);
        SendReleaseResultToClient(transaction);
    }

    private static void ReceiveReleaseResultOnClient(long sender, ZPackage pkg)
    {
        if (!IsOrbMessageFromServer(sender) ||
            !OrbReleaseResult.TryRead(pkg, out OrbReleaseResult? result) ||
            result == null ||
            !ClientReleaseTransactions.TryGetValue(result.TransactionId, out ClientReleaseTransaction? transaction) ||
            !result.OrbId.Equals(transaction.Request.OrbId, StringComparison.Ordinal))
        {
            return;
        }

        if (!result.Success && (object?)transaction.Spawned != null)
        {
            DestroySpawnedRelease(transaction.Spawned!);
            transaction.Spawned = null;
        }

        FinishReleaseClient(transaction, result.Success, result.Message);
    }

    private static void FinishReleaseClient(ClientReleaseTransaction transaction, bool success, string message)
    {
        ClientReleaseTransactions.Remove(transaction.Request.TransactionId);
        Player? player = Player.m_localPlayer;
        if ((object?)player == null)
        {
            return;
        }

        if (success)
        {
            if (ResolveOrbItem(transaction.Request.TransactionId, transaction.Request.OrbId, true))
                FeedLocalization.Message(player, "$flg_taming_orb_released", GetPayloadDisplayName(transaction.Payload));
        }
        else
        {
            ResolveOrbItem(transaction.Request.TransactionId, transaction.Request.OrbId, false);
            player.Message(
                MessageHud.MessageType.Center,
                string.IsNullOrWhiteSpace(message)
                    ? FeedLocalization.Text("$flg_taming_orb_release_failed")
                    : message);
        }
    }

    private static void FinishCaptureClientFailure(ClientCaptureTransaction transaction, string message)
    {
        ClientCaptureTransactions.Remove(transaction.Request.TransactionId);
        Player? player = Player.m_localPlayer;
        if ((object?)player != null)
        {
            ResolveOrbItem(transaction.Request.TransactionId, transaction.Request.OrbId, false);

            if (!string.IsNullOrWhiteSpace(message))
            {
                player.Message(MessageHud.MessageType.Center, message);
            }
        }
    }

    private static void RestoreCustomData(
        ItemDrop.ItemData item,
        string key,
        bool hadValue,
        string? previousValue)
    {
        if (hadValue)
        {
            item.m_customData[key] = previousValue ?? string.Empty;
        }
        else
        {
            item.m_customData.Remove(key);
        }
    }

    private static void SetOrRemoveCustomData(ItemDrop.ItemData item, string key, string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            item.m_customData.Remove(key);
        }
        else
        {
            item.m_customData[key] = value;
        }
    }

    private static string GetOrCreateOrbIdentity(ItemDrop.ItemData item, Inventory inventory)
    {
        if (item.m_customData.TryGetValue(OrbIdentityKey, out string orbId) &&
            Guid.TryParseExact(orbId, "N", out _))
        {
            return orbId;
        }

        orbId = Guid.NewGuid().ToString("N");
        item.m_customData[OrbIdentityKey] = orbId;
        inventory.Changed();
        return orbId;
    }

    private static bool AuthenticateOrbPlayer(long sender, ZDOID playerId)
    {
        if (!IsOrbServer() || sender == 0L || playerId.IsNone())
        {
            return false;
        }

        if (sender == ZRoutedRpc.instance.PeerId())
        {
            Player? localPlayer = Player.m_localPlayer;
            return (object?)localPlayer != null && localPlayer.GetZDOID().Equals(playerId);
        }

        ZNetPeer? peer = ZNet.instance.GetPeer(sender);
        return peer != null && peer.m_characterID.Equals(playerId);
    }

    private static bool IsOrbServer()
    {
        return ZNet.instance != null && ZNet.instance.IsServer() && ZRoutedRpc.instance != null;
    }

    private static bool IsOrbMessageFromServer(long sender)
    {
        return ZRoutedRpc.instance != null &&
               sender != 0L &&
               sender == ZRoutedRpc.instance.GetServerPeerID();
    }

    private static bool TryResolveOrbTargetOwner(ZDOID targetId, out long ownerPeerId)
    {
        ownerPeerId = 0L;
        ZDO? zdo = ZDOMan.instance?.GetZDO(targetId);
        if (zdo == null)
        {
            return false;
        }

        ownerPeerId = zdo.GetOwner();
        if (ownerPeerId != 0L)
        {
            return true;
        }

        GameObject? target = ZNetScene.instance?.FindInstance(targetId);
        ZNetView? nview = target?.GetComponent<ZNetView>();
        if ((object?)nview != null && nview.IsValid() && nview.IsOwner())
        {
            ownerPeerId = ZRoutedRpc.instance.PeerId();
        }

        return ownerPeerId != 0L;
    }

    private static bool TryFindOwnedOrbTarget(
        ZDOID targetId,
        out Character? character,
        out Tameable? tameable,
        out ZNetView? nview)
    {
        GameObject? target = ZNetScene.instance?.FindInstance(targetId);
        character = target?.GetComponent<Character>();
        tameable = target?.GetComponent<Tameable>();
        nview = target?.GetComponent<ZNetView>();
        return (object?)character != null &&
               (object?)nview != null &&
               nview.IsValid() &&
               nview.IsOwner();
    }

    private static bool IsValidCaptureRequest(OrbCaptureRequest request)
    {
        return Guid.TryParseExact(request.TransactionId, "N", out _) &&
               Guid.TryParseExact(request.OrbId, "N", out _) &&
               !request.RequesterId.IsNone() &&
               !request.TargetId.IsNone() &&
               !string.IsNullOrWhiteSpace(request.ExpectedPrefab) &&
               request.ExpectedPrefab.Length <= 256;
    }

    private static bool IsValidReleaseRequest(OrbReleaseRequest request)
    {
        return Guid.TryParseExact(request.TransactionId, "N", out _) &&
               Guid.TryParseExact(request.OrbId, "N", out _) &&
               !request.RequesterId.IsNone() &&
               request.Payload.Length <= 1024 * 1024 &&
               request.CreatureManagerState.Length <= 1024 * 1024 &&
               CapturedTamePayload.TryDeserialize(request.Payload, out _, logWarnings: false);
    }

    private static long OrbNetworkTimeTicks()
    {
        return ZNet.instance != null ? ZNet.instance.GetTime().Ticks : DateTime.UtcNow.Ticks;
    }

    private static void FailCaptureBeforeCommit(ServerCaptureTransaction transaction, string message)
    {
        OrbCaptureResult failed = new()
        {
            TransactionId = transaction.Request.TransactionId, TargetId = transaction.Request.TargetId,
            ExpectedPrefab = transaction.Request.ExpectedPrefab, Message = message
        };
        if (!PersistCapture(transaction, TamingOrbJournal.CompletedFailure, failed)) return;
        transaction.Prepared = OrbCapturePrepared.Failure(transaction.Request, message);
        SendCapturePreparedToClient(transaction);
        ReleaseCaptureLock(transaction);
        ServerCaptureTransactions.Remove(transaction.Request.TransactionId);
    }

    private static void SendCapturePreparedToClient(ServerCaptureTransaction transaction)
    {
        if (transaction.Prepared != null)
        {
            SendCapturePreparedToPeer(transaction.RequesterPeerId, transaction.Prepared);
        }
    }

    private static void SendCapturePreparedToPeer(long peerId, OrbCapturePrepared prepared)
    {
        ZRoutedRpc.instance.InvokeRoutedRPC(peerId, CapturePreparedClientRpc, prepared.Write());
    }

    private static void SendCaptureResultToServer(OrbCaptureResult result)
    {
        ZRoutedRpc.instance.InvokeRoutedRPC(
            ZRoutedRpc.instance.GetServerPeerID(),
            CaptureResultServerRpc,
            result.Write());
    }

    private static void SendCaptureResultToClient(ServerCaptureTransaction transaction)
    {
        if (transaction.Result != null)
        {
            ZRoutedRpc.instance.InvokeRoutedRPC(
                transaction.RequesterPeerId,
                CaptureResultClientRpc,
                transaction.Result.Write());
        }
    }

    private static void CompleteUndispatchedCaptureFailure(
        ServerCaptureTransaction transaction,
        string message)
    {
        OrbCaptureResult result = new OrbCaptureResult
        {
            TransactionId = transaction.Request.TransactionId,
            TargetId = transaction.Request.TargetId,
            ExpectedPrefab = transaction.Request.ExpectedPrefab,
            Message = message
        };
        if (!PersistCapture(transaction, TamingOrbJournal.CompletedFailure, result)) return;
        transaction.Result = result;
        transaction.Phase = OrbCaptureServerPhase.Completed;
        transaction.UpdatedAtTicks = OrbNetworkTimeTicks();
        ReleaseCaptureLock(transaction);
        SendCaptureResultToClient(transaction);
    }

    private static void ReleaseCaptureLock(ServerCaptureTransaction transaction)
    {
        if (ServerCaptureLocks.TryGetValue(transaction.Request.TargetId, out string transactionId) &&
            transactionId.Equals(transaction.Request.TransactionId, StringComparison.Ordinal))
        {
            ServerCaptureLocks.Remove(transaction.Request.TargetId);
        }

        if (ServerOrbActionLocks.TryGetValue(transaction.Request.OrbId, out string orbTransactionId) &&
            orbTransactionId.Equals(transaction.Request.TransactionId, StringComparison.Ordinal))
        {
            ServerOrbActionLocks.Remove(transaction.Request.OrbId);
        }
    }

    private static void SendReleaseApprovalToClient(ServerReleaseTransaction transaction)
    {
        if (!EnsureOrbJournal()) return;
        SendReleaseApprovalToPeer(
            transaction.RequesterPeerId,
            OrbReleaseApproval.SuccessResult(transaction.Request));
    }

    private static void SendReleaseApprovalToPeer(long peerId, OrbReleaseApproval approval)
    {
        ZRoutedRpc.instance.InvokeRoutedRPC(peerId, ReleaseApprovedClientRpc, approval.Write());
    }

    private static void SendReleaseResultToServer(OrbReleaseResult result)
    {
        ZRoutedRpc.instance.InvokeRoutedRPC(
            ZRoutedRpc.instance.GetServerPeerID(),
            ReleaseResultServerRpc,
            result.Write());
    }

    private static void SendReleaseResultToClient(ServerReleaseTransaction transaction)
    {
        if (transaction.Result != null)
        {
            ZRoutedRpc.instance.InvokeRoutedRPC(
                transaction.RequesterPeerId,
                ReleaseResultClientRpc,
                transaction.Result.Write());
        }
    }

    private static void ReleaseReleaseLock(ServerReleaseTransaction transaction)
    {
        if (ServerOrbActionLocks.TryGetValue(transaction.Request.OrbId, out string transactionId) &&
            transactionId.Equals(transaction.Request.TransactionId, StringComparison.Ordinal))
        {
            ServerOrbActionLocks.Remove(transaction.Request.OrbId);
        }
    }

    private static void CleanupOrbServerTransactions()
    {
        long nowTicks = OrbNetworkTimeTicks();
        List<string>? expiredCapture = null;
        foreach (KeyValuePair<string, ServerCaptureTransaction> entry in ServerCaptureTransactions)
        {
            long retention = entry.Value.Phase == OrbCaptureServerPhase.Completed
                ? OrbCompletedRetentionTicks
                : OrbPendingRetentionTicks;
            if (entry.Value.Phase == OrbCaptureServerPhase.Committing ||
                nowTicks - entry.Value.UpdatedAtTicks <= retention)
            {
                continue;
            }

            expiredCapture ??= new List<string>();
            expiredCapture.Add(entry.Key);
        }

        if (expiredCapture != null)
        {
            foreach (string transactionId in expiredCapture)
            {
                ServerCaptureTransaction transaction = ServerCaptureTransactions[transactionId];
                if (transaction.Phase != OrbCaptureServerPhase.Completed)
                {
                    OrbCaptureResult cancelled = new()
                    {
                        TransactionId = transactionId, TargetId = transaction.Request.TargetId,
                        ExpectedPrefab = transaction.Request.ExpectedPrefab
                    };
                    if (!PersistCapture(transaction, TamingOrbJournal.CompletedFailure, cancelled)) continue;
                }
                ReleaseCaptureLock(transaction);
                ServerCaptureTransactions.Remove(transactionId);
            }
        }

        List<string>? expiredRelease = null;
        foreach (KeyValuePair<string, ServerReleaseTransaction> entry in ServerReleaseTransactions)
        {
            if (entry.Value.Result == null) continue;
            long retention = entry.Value.Result != null
                ? OrbCompletedRetentionTicks
                : OrbPendingRetentionTicks;
            if (nowTicks - entry.Value.UpdatedAtTicks <= retention)
            {
                continue;
            }

            expiredRelease ??= new List<string>();
            expiredRelease.Add(entry.Key);
        }

        if (expiredRelease != null)
        {
            foreach (string transactionId in expiredRelease)
            {
                ServerReleaseTransaction transaction = ServerReleaseTransactions[transactionId];
                ReleaseReleaseLock(transaction);
                ServerReleaseTransactions.Remove(transactionId);
            }
        }

        TrimCompletedCaptureTransactions();
        TrimCompletedReleaseTransactions();
    }

    private static void CleanupOwnerCaptureResults()
    {
        long minimumTicks = OrbNetworkTimeTicks() - OrbCompletedRetentionTicks;
        List<string>? expired = null;
        foreach (KeyValuePair<string, OwnerCaptureTransaction> entry in OwnerCaptureResults)
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
                OwnerCaptureResults.Remove(transactionId);
            }
        }
    }

    private static void TrimCompletedCaptureTransactions()
    {
        while (ServerCaptureTransactions.Count >= MaxOrbServerTransactions)
        {
            string? oldestId = null;
            long oldestTicks = long.MaxValue;
            foreach (KeyValuePair<string, ServerCaptureTransaction> entry in ServerCaptureTransactions)
            {
                if (entry.Value.Phase == OrbCaptureServerPhase.Completed && entry.Value.UpdatedAtTicks < oldestTicks)
                {
                    oldestId = entry.Key;
                    oldestTicks = entry.Value.UpdatedAtTicks;
                }
            }

            if (oldestId == null)
            {
                return;
            }

            ServerCaptureTransactions.Remove(oldestId);
        }
    }

    private static void TrimCompletedReleaseTransactions()
    {
        while (ServerReleaseTransactions.Count >= MaxOrbServerTransactions)
        {
            string? oldestId = null;
            long oldestTicks = long.MaxValue;
            foreach (KeyValuePair<string, ServerReleaseTransaction> entry in ServerReleaseTransactions)
            {
                if (entry.Value.Result != null && entry.Value.UpdatedAtTicks < oldestTicks)
                {
                    oldestId = entry.Key;
                    oldestTicks = entry.Value.UpdatedAtTicks;
                }
            }

            if (oldestId == null)
            {
                return;
            }

            ServerReleaseTransaction transaction = ServerReleaseTransactions[oldestId];
            ReleaseReleaseLock(transaction);
            ServerReleaseTransactions.Remove(oldestId);
        }
    }

    private enum OrbCaptureClientPhase
    {
        Requesting,
        Committing
    }

    private enum OrbCaptureServerPhase
    {
        Preparing,
        Prepared,
        Committing,
        Completed
    }

    private sealed class ClientCaptureTransaction
    {
        public OrbCaptureRequest Request { get; init; } = null!;
        public ItemDrop.ItemData Item { get; set; } = null!;
        public OrbCapturePrepared? Prepared { get; set; }
        public OrbCaptureClientPhase Phase { get; set; }
        public bool DelayElapsed { get; set; }
        public float StartedRealtime { get; init; }
        public float LastSentRealtime { get; set; }
    }

    private sealed class ServerCaptureTransaction
    {
        public OrbCaptureRequest Request { get; init; } = null!;
        public long RequesterPeerId { get; set; }
        public long OwnerPeerId { get; set; }
        public HashSet<long> PrepareOwnerPeerIds { get; } = new();
        public HashSet<long> CommitOwnerPeerIds { get; } = new();
        public bool CommitForwarded { get; set; }
        public OrbCaptureServerPhase Phase { get; set; }
        public OrbCapturePrepared? Prepared { get; set; }
        public OrbCaptureResult? Result { get; set; }
        public OrbCaptureResult? PendingResult { get; set; }
        public long UpdatedAtTicks { get; set; }
    }

    private sealed class OwnerCaptureTransaction
    {
        public OrbCaptureResult Result { get; init; } = null!;
        public long CompletedAtTicks { get; init; }
    }

    private sealed class ClientReleaseTransaction
    {
        public OrbReleaseRequest Request { get; init; } = null!;
        public ItemDrop.ItemData Item { get; set; } = null!;
        public CapturedTamePayload Payload { get; init; } = null!;
        public GameObject CreaturePrefab { get; init; } = null!;
        public bool Approved { get; set; }
        public bool DelayElapsed { get; set; }
        public bool SpawnAttempted { get; set; }
        public GameObject? Spawned { get; set; }
        public OrbReleaseResult? Result { get; set; }
        public float StartedRealtime { get; init; }
        public float LastSentRealtime { get; set; }
    }

    private sealed class ServerReleaseTransaction
    {
        public OrbReleaseRequest Request { get; init; } = null!;
        public long RequesterPeerId { get; set; }
        public OrbReleaseResult? Result { get; set; }
        public long UpdatedAtTicks { get; set; }
    }

    private sealed class OrbCaptureRequest
    {
        public string TransactionId { get; init; } = string.Empty;
        public ZDOID RequesterId { get; init; }
        public ZDOID TargetId { get; init; }
        public string OrbId { get; init; } = string.Empty;
        public string ExpectedPrefab { get; init; } = string.Empty;

        public bool Matches(OrbCaptureRequest other)
        {
            return RequesterId.Equals(other.RequesterId) &&
                   TargetId.Equals(other.TargetId) &&
                   OrbId.Equals(other.OrbId, StringComparison.Ordinal) &&
                   ExpectedPrefab.Equals(other.ExpectedPrefab, StringComparison.OrdinalIgnoreCase);
        }

        public ZPackage Write()
        {
            ZPackage pkg = new();
            pkg.Write(TransactionId);
            pkg.Write(RequesterId);
            pkg.Write(TargetId);
            pkg.Write(OrbId);
            pkg.Write(ExpectedPrefab);
            return pkg;
        }

        public static bool TryRead(ZPackage pkg, out OrbCaptureRequest? message)
        {
            message = null;
            try
            {
                message = new OrbCaptureRequest
                {
                    TransactionId = pkg.ReadString(),
                    RequesterId = pkg.ReadZDOID(),
                    TargetId = pkg.ReadZDOID(),
                    OrbId = pkg.ReadString(),
                    ExpectedPrefab = pkg.ReadString()
                };
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }
    }

    private sealed class OrbCapturePrepared
    {
        public string TransactionId { get; init; } = string.Empty;
        public bool Success { get; set; }
        public bool RetryOwner { get; set; }
        public ZDOID TargetId { get; init; }
        public string ExpectedPrefab { get; init; } = string.Empty;
        public string Payload { get; set; } = string.Empty;
        public string CreatureManagerState { get; set; } = string.Empty;
        public string Message { get; set; } = string.Empty;

        public static OrbCapturePrepared Failure(OrbCaptureRequest request, string message)
        {
            return new OrbCapturePrepared
            {
                TransactionId = request.TransactionId,
                TargetId = request.TargetId,
                ExpectedPrefab = request.ExpectedPrefab,
                Message = message
            };
        }

        public ZPackage Write()
        {
            ZPackage pkg = new();
            pkg.Write(TransactionId);
            pkg.Write(Success);
            pkg.Write(RetryOwner);
            pkg.Write(TargetId);
            pkg.Write(ExpectedPrefab);
            pkg.Write(Payload);
            pkg.Write(CreatureManagerState);
            pkg.Write(Message);
            return pkg;
        }

        public static bool TryRead(ZPackage pkg, out OrbCapturePrepared? message)
        {
            message = null;
            try
            {
                message = new OrbCapturePrepared
                {
                    TransactionId = pkg.ReadString(),
                    Success = pkg.ReadBool(),
                    RetryOwner = pkg.ReadBool(),
                    TargetId = pkg.ReadZDOID(),
                    ExpectedPrefab = pkg.ReadString(),
                    Payload = pkg.ReadString(),
                    CreatureManagerState = pkg.ReadString(),
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

    private sealed class OrbCaptureResult
    {
        public string TransactionId { get; init; } = string.Empty;
        public bool Success { get; set; }
        public bool RetryOwner { get; set; }
        public ZDOID TargetId { get; init; }
        public string ExpectedPrefab { get; init; } = string.Empty;
        public string Message { get; set; } = string.Empty;

        public ZPackage Write()
        {
            ZPackage pkg = new();
            pkg.Write(TransactionId);
            pkg.Write(Success);
            pkg.Write(RetryOwner);
            pkg.Write(TargetId);
            pkg.Write(ExpectedPrefab);
            pkg.Write(Message);
            return pkg;
        }

        public static bool TryRead(ZPackage pkg, out OrbCaptureResult? message)
        {
            message = null;
            try
            {
                message = new OrbCaptureResult
                {
                    TransactionId = pkg.ReadString(),
                    Success = pkg.ReadBool(),
                    RetryOwner = pkg.ReadBool(),
                    TargetId = pkg.ReadZDOID(),
                    ExpectedPrefab = pkg.ReadString(),
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

    private sealed class OrbReleaseRequest
    {
        public string TransactionId { get; init; } = string.Empty;
        public ZDOID RequesterId { get; init; }
        public string OrbId { get; init; } = string.Empty;
        public string Payload { get; init; } = string.Empty;
        public string CreatureManagerState { get; init; } = string.Empty;
        public Vector3 SpawnPosition { get; init; }
        public Quaternion SpawnRotation { get; init; }

        public bool Matches(OrbReleaseRequest other)
        {
            return RequesterId.Equals(other.RequesterId) &&
                   OrbId.Equals(other.OrbId, StringComparison.Ordinal) &&
                   Payload.Equals(other.Payload, StringComparison.Ordinal) &&
                   CreatureManagerState.Equals(other.CreatureManagerState, StringComparison.Ordinal) &&
                   SpawnPosition == other.SpawnPosition &&
                   SpawnRotation == other.SpawnRotation;
        }

        public ZPackage Write()
        {
            ZPackage pkg = new();
            pkg.Write(TransactionId);
            pkg.Write(RequesterId);
            pkg.Write(OrbId);
            pkg.Write(Payload);
            pkg.Write(CreatureManagerState);
            pkg.Write(SpawnPosition);
            pkg.Write(SpawnRotation);
            return pkg;
        }

        public static bool TryRead(ZPackage pkg, out OrbReleaseRequest? message)
        {
            message = null;
            try
            {
                message = new OrbReleaseRequest
                {
                    TransactionId = pkg.ReadString(),
                    RequesterId = pkg.ReadZDOID(),
                    OrbId = pkg.ReadString(),
                    Payload = pkg.ReadString(),
                    CreatureManagerState = pkg.ReadString(),
                    SpawnPosition = pkg.ReadVector3(),
                    SpawnRotation = pkg.ReadQuaternion()
                };
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }
    }

    private sealed class OrbReleaseApproval
    {
        public string TransactionId { get; init; } = string.Empty;
        public string OrbId { get; init; } = string.Empty;
        public bool Success { get; init; }
        public string Message { get; init; } = string.Empty;

        public static OrbReleaseApproval SuccessResult(OrbReleaseRequest request)
        {
            return new OrbReleaseApproval
            {
                TransactionId = request.TransactionId,
                OrbId = request.OrbId,
                Success = true
            };
        }

        public static OrbReleaseApproval Failure(OrbReleaseRequest request, string message)
        {
            return new OrbReleaseApproval
            {
                TransactionId = request.TransactionId,
                OrbId = request.OrbId,
                Message = message
            };
        }

        public ZPackage Write()
        {
            ZPackage pkg = new();
            pkg.Write(TransactionId);
            pkg.Write(OrbId);
            pkg.Write(Success);
            pkg.Write(Message);
            return pkg;
        }

        public static bool TryRead(ZPackage pkg, out OrbReleaseApproval? message)
        {
            message = null;
            try
            {
                message = new OrbReleaseApproval
                {
                    TransactionId = pkg.ReadString(),
                    OrbId = pkg.ReadString(),
                    Success = pkg.ReadBool(),
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

    private sealed class OrbReleaseResult
    {
        public string TransactionId { get; init; } = string.Empty;
        public string OrbId { get; init; } = string.Empty;
        public bool Success { get; init; }
        public ZDOID SpawnedId { get; init; }
        public string Message { get; init; } = string.Empty;

        public static OrbReleaseResult SuccessResult(OrbReleaseRequest request, ZDOID spawnedId)
        {
            return new OrbReleaseResult
            {
                TransactionId = request.TransactionId,
                OrbId = request.OrbId,
                Success = true,
                SpawnedId = spawnedId
            };
        }

        public static OrbReleaseResult Failure(OrbReleaseRequest request, string message)
        {
            return new OrbReleaseResult
            {
                TransactionId = request.TransactionId,
                OrbId = request.OrbId,
                Message = message
            };
        }

        public ZPackage Write()
        {
            ZPackage pkg = new();
            pkg.Write(TransactionId);
            pkg.Write(OrbId);
            pkg.Write(Success);
            pkg.Write(SpawnedId);
            pkg.Write(Message);
            return pkg;
        }

        public static bool TryRead(ZPackage pkg, out OrbReleaseResult? message)
        {
            message = null;
            try
            {
                message = new OrbReleaseResult
                {
                    TransactionId = pkg.ReadString(),
                    OrbId = pkg.ReadString(),
                    Success = pkg.ReadBool(),
                    SpawnedId = pkg.ReadZDOID(),
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
