using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using UnityEngine;

namespace FeedLikeGrandma;

internal static partial class TamingOrbSystem
{
    private const string OrbRecoveryServerRpc = "FLG_OrbRecoveryServer_v3";
    private const string OrbRecoveryClientRpc = "FLG_OrbRecoveryClient_v3";
    private const string EmptyReleasedOrbRecovery = "EmptyReleased";
    private static TamingOrbJournal? _orbJournal;
    private static string? _orbJournalError;
    private static string? _orbJournalPath;
    private static string? _orbLegacyJournalPath;
    private static string _orbJournalStorageStatus = "not_opened";
    private static readonly Dictionary<string, float> OrbRecoveryRequests = new(StringComparer.Ordinal);
    private static float _lastReleaseRecoveryScan = -100f;
    private static readonly Dictionary<string, ZDOID> RecoveredReleaseIds = new(StringComparer.Ordinal);
    private static readonly HashSet<string> SessionFinalOrbTransactions = new(StringComparer.Ordinal);
    private static readonly Dictionary<ZDOID, string> DurableCaptureTargets = new();

    internal static void EndNetworkSession()
    {
        ShutdownOrbTransactions();
    }

    private static long OrbWorldUid() => ZNet.instance != null && ZNet.instance.GetWorld() != null
        ? ZNet.instance.GetWorldUID() : 0L;

    private static void CloseOrbJournal()
    {
        _orbJournal?.Dispose();
        _orbJournal = null;
        _orbJournalError = null;
        _orbJournalPath = null;
        _orbLegacyJournalPath = null;
        _orbJournalStorageStatus = "not_opened";
        RecoveredReleaseIds.Clear();
        SessionFinalOrbTransactions.Clear();
        DurableCaptureTargets.Clear();
        _lastReleaseRecoveryScan = -100f;
        ResetOrbDiagnosticSession();
    }

    private static bool EnsureOrbJournal()
    {
        if (!IsOrbServer() || OrbWorldUid() == 0) return false;
        if (_orbJournal != null) return _orbJournal.IsHealthy;
        if (_orbJournalError != null) return false;
        string error;
        try
        {
            long world = OrbWorldUid();
            string saveRoot = Utils.GetSaveDataPath(FileHelpers.FileSource.Local);
            if (string.IsNullOrWhiteSpace(saveRoot)) throw new InvalidOperationException("Valheim local save path is unavailable.");
            string directory = Path.GetFullPath(Path.Combine(saveRoot, "FeedLikeGrandma", "orb-transactions"));
            string legacyDirectory = Path.GetFullPath(Path.Combine(BepInEx.Paths.ConfigPath, "FeedLikeGrandma", "orb-transactions"));
            string fileName = world.ToString(CultureInfo.InvariantCulture) + ".journal";
            _orbJournalPath = Path.Combine(directory, fileName);
            _orbLegacyJournalPath = Path.Combine(legacyDirectory, fileName);
            if (TamingOrbJournal.TryOpenShared(directory, legacyDirectory, world, out _orbJournal, out _orbJournalStorageStatus, out error))
            {
                try { FeedLikeGrandmaPlugin.Log.LogInfo($"Orb journal {_orbJournalStorageStatus}: world={world} path=\"{_orbJournal!.FilePath}\" legacy=\"{_orbLegacyJournalPath}\"."); }
                catch (Exception) { /* A logging failure must not discard a valid open journal. */ }
                // ZDOIDs are reassigned on world load. Only current-session target IDs may be indexed;
                // older captures are identified by their custom transaction marker on the creature.
                return true;
            }
        }
        catch (Exception ex) { error = "Orb journal storage could not be initialized: " + ex.Message; }
        _orbJournalStorageStatus = "unavailable";
        _orbJournalError = error;
        FeedLikeGrandmaPlugin.Log.LogError($"Orb journal unavailable; this world's Orb transactions are blocked. path=\"{_orbJournalPath}\" legacy=\"{_orbLegacyJournalPath}\": {error}");
        return false;
    }

    private static bool SaveOrbRecord(TamingOrbJournal.Entry entry)
    {
        if (!EnsureOrbJournal()) return false;
        if (_orbJournal!.TrySave(entry, out string error))
        {
            UpdateDurableCaptureTarget(entry);
            if (entry.State == TamingOrbJournal.CompletedSuccess || entry.State == TamingOrbJournal.CompletedFailure)
                SessionFinalOrbTransactions.Add(entry.TransactionId);
            return true;
        }
        FeedLikeGrandmaPlugin.Log.LogError($"Orb journal write failed; transaction {entry.TransactionId} remains unresolved: {error}");
        return false;
    }

    private static bool PersistCapture(ServerCaptureTransaction tx, string state, OrbCaptureResult? result = null)
    {
        return SaveOrbRecord(new TamingOrbJournal.Entry
        {
            TransactionId = tx.Request.TransactionId, OrbId = tx.Request.OrbId,
            Kind = TamingOrbJournal.Capture, State = state, RequestData = tx.Request.Write().GetBase64(),
            PreparedData = tx.Prepared?.Success == true ? tx.Prepared.Write().GetBase64() : string.Empty,
            ResultData = result?.Write().GetBase64() ?? string.Empty
        });
    }

    private static bool PersistReleaseResult(ServerReleaseTransaction tx, OrbReleaseResult? result)
    {
        return SaveOrbRecord(new TamingOrbJournal.Entry
        {
            TransactionId = tx.Request.TransactionId, OrbId = tx.Request.OrbId,
            Kind = TamingOrbJournal.Release,
            State = result == null ? TamingOrbJournal.Reserved : result.Success
                ? TamingOrbJournal.CompletedSuccess : TamingOrbJournal.CompletedFailure,
            RequestData = tx.Request.Write().GetBase64(), ResultData = result?.Write().GetBase64() ?? string.Empty
        });
    }

    private static bool BeginOrbItemTransaction(ItemDrop.ItemData item, string orbId, string txId,
        OrbPendingAction action, ZPackage request)
    {
        return TryBeginPendingOrbItem(item, new OrbPendingItemState
        {
            OrbId = orbId, TransactionId = txId, WorldUid = OrbWorldUid(), Action = action,
            Phase = OrbPendingPhase.Preparing, RequestData = request.GetBase64(), Previous = CaptureOrbItemSnapshot(item)
        });
    }

    private static ItemDrop.ItemData? FindTransactionItem(string txId, string orbId)
    {
        Player? player = Player.m_localPlayer;
        return player == null ? null : FindPendingOrbItem(player.GetInventory(), txId, orbId, OrbWorldUid());
    }

    // Never mutate the old ItemData reference: moving a slot clones it, and dropping transfers custody.
    // A moved-out orb keeps its durable marker and reconciles when its current holder next uses it.
    private static bool ResolveOrbItem(string txId, string orbId, bool success, string preparedData = "")
    {
        ItemDrop.ItemData? item = FindTransactionItem(txId, orbId);
        if (item == null || !TryReadPendingOrbItem(item, out OrbPendingItemState? pending) || pending == null) return false;
        bool capture = pending.Action == OrbPendingAction.Capture;
        if (!success && pending.Phase == OrbPendingPhase.Preparing && !CaptureOrbItemSnapshot(item).Matches(pending.Previous))
            return false;
        if (capture && success)
        {
            string preparedText = string.IsNullOrEmpty(preparedData) ? pending.PreparedData : preparedData;
            if (string.IsNullOrEmpty(preparedText) || !OrbCapturePrepared.TryRead(new ZPackage(preparedText), out OrbCapturePrepared? prepared) ||
                prepared == null || !prepared.Success || prepared.TransactionId != txId) return false;
            // Only the original empty contents or our own staged contents may be completed/rolled back.
            if (item.m_customData.TryGetValue(PayloadKey, out string current) && current != prepared.Payload) return false;
            item.m_customData[PayloadKey] = prepared.Payload;
            SetOrRemoveCustomData(item, CreatureManagerStateKey, prepared.CreatureManagerState);
            item.m_variant = 0;
        }
        if (!capture && success &&
            (!item.m_customData.TryGetValue(PayloadKey, out string contents) || contents != pending.Previous.Payload ||
             (item.m_customData.TryGetValue(CreatureManagerStateKey, out string currentModifiers) ? currentModifiers : string.Empty) != pending.Previous.CreatureManagerState)) return false;
        bool restore = capture && !success && pending.Phase == OrbPendingPhase.Committing;
        if (restore && !CaptureOrbItemSnapshot(item).Matches(pending.Previous))
        {
            if (!OrbCapturePrepared.TryRead(new ZPackage(pending.PreparedData), out OrbCapturePrepared? staged) || staged == null ||
                (item.m_customData.TryGetValue(PayloadKey, out string current) && current != staged.Payload) ||
                (item.m_customData.TryGetValue(CreatureManagerStateKey, out string captureModifiers) ? captureModifiers : string.Empty) != staged.CreatureManagerState) return false;
        }
        if (!TryFinishPendingOrbItem(item, pending, restore, clearContents: !capture && success)) return false;

        Player.m_localPlayer.GetInventory().Changed();
        return true;
    }

    private static bool TryHandlePendingOrb(Player player, ItemDrop.ItemData item)
    {
        if (!HasPendingOrbItem(item)) return false;
        if (!TryReadPendingOrbItem(item, out OrbPendingItemState? pending) || pending == null || pending.WorldUid != OrbWorldUid())
        {
            LogOrbFailure(pending == null ? "pending_unreadable" : "pending_world_mismatch",
                pending?.OrbId ?? string.Empty, pending?.TransactionId ?? string.Empty);
            FeedLocalization.Message(player, "$flg_taming_orb_recovery_blocked");
            return true;
        }
        if (ClientReleaseTransactions.TryGetValue(pending.TransactionId, out ClientReleaseTransaction? release) &&
            (!release.Request.RequesterId.Equals(player.GetZDOID()) ||
             (release.SpawnAttempted && Time.realtimeSinceStartup - release.StartedRealtime > OrbPrepareTimeoutSeconds)))
            ClientReleaseTransactions.Remove(pending.TransactionId);
        if (ClientCaptureTransactions.TryGetValue(pending.TransactionId, out ClientCaptureTransaction? capture) &&
            !capture.Request.RequesterId.Equals(player.GetZDOID())) ClientCaptureTransactions.Remove(pending.TransactionId);
        if (ClientCaptureTransactions.ContainsKey(pending.TransactionId) || ClientReleaseTransactions.ContainsKey(pending.TransactionId))
        {
            FeedLocalization.Message(player, "$flg_taming_orb_pending");
            return true;
        }
        if (ZRoutedRpc.instance == null || ZRoutedRpc.instance.GetServerPeerID() == 0L) return true;
        if (OrbRecoveryRequests.TryGetValue(pending.TransactionId, out float last) && Time.realtimeSinceStartup - last < 2f) return true;
        OrbRecoveryRequests[pending.TransactionId] = Time.realtimeSinceStartup;
        ZPackage pkg = new();
        pkg.Write(player.GetZDOID()); pkg.Write(pending.WorldUid); pkg.Write(pending.TransactionId); pkg.Write(pending.OrbId);
        pkg.Write(pending.RequestData); pkg.Write(pending.SpawnedId);
        ZRoutedRpc.instance.InvokeRoutedRPC(ZRoutedRpc.instance.GetServerPeerID(), OrbRecoveryServerRpc, pkg);
        return true;
    }

    private static bool TryRecoverEmptyOrbOnClient(string txId, string orbId, string requestData)
    {
        Player? player = Player.m_localPlayer;
        if (player == null) return false;
        foreach (ClientCaptureTransaction capture in ClientCaptureTransactions.Values)
            if (capture.Request.OrbId == orbId && (capture.Request.TransactionId != txId ||
                capture.Phase != OrbCaptureClientPhase.Requesting || capture.Prepared != null)) return false;
        foreach (ClientReleaseTransaction release in ClientReleaseTransactions.Values)
            if (release.Request.OrbId == orbId) return false;

        if (!TryRenewEmptyOrbIdentity(player.GetInventory(), txId, orbId, OrbWorldUid(), requestData)) return false;
        ClientCaptureTransactions.Remove(txId);
        OrbRecoveryRequests.Remove(txId);
        player.GetInventory().Changed();
        FeedLocalization.Message(player, "$flg_taming_orb_recovered");
        return true; // Retry explicitly; this response never captures or spawns a creature.
    }

    private static bool CanRecoverEmptyOrbIdentity(string orbId)
    {
        if (!EnsureOrbJournal() || !_orbJournal!.TryGetBlockingRelease(orbId, out TamingOrbJournal.Entry? released) ||
            released == null || !OrbReleaseResult.IsCompletedReleaseForEmptyOrb(released, orbId)) return false;

        // Successful releases retain their replay lock until cleanup. It is safe to leave
        // that completed lock intact, but a different or unfinished operation must block repair.
        if (ServerOrbActionLocks.TryGetValue(orbId, out string lockedBy) && lockedBy != released.TransactionId) return false;
        foreach (ServerCaptureTransaction capture in ServerCaptureTransactions.Values)
            if (capture.Request.OrbId == orbId && capture.Phase != OrbCaptureServerPhase.Completed) return false;
        foreach (ServerReleaseTransaction release in ServerReleaseTransactions.Values)
            if (release.Request.OrbId == orbId && release.Result == null) return false;
        return true;
    }

    private static void ReceiveOrbRecoveryOnServer(long sender, ZPackage pkg)
    {
        if (!IsOrbServer()) return;
        try
        {
            ZDOID requester = pkg.ReadZDOID();
            long world = pkg.ReadLong(); string txId = pkg.ReadString(); string orbId = pkg.ReadString();
            string requestData = pkg.ReadString();
            string spawnedId = pkg.ReadString();
            if (!AuthenticateOrbPlayer(sender, requester) || world != OrbWorldUid() ||
                !Guid.TryParseExact(txId, "N", out _) || !Guid.TryParseExact(orbId, "N", out _) || requestData.Length > 8 * 1024 * 1024 || spawnedId.Length > 128) return;
            string state = "unknown", prepared = string.Empty;
            if (EnsureOrbJournal() && _orbJournal!.TryGet(txId, out TamingOrbJournal.Entry? entry) && entry != null && entry.OrbId == orbId)
            {
                entry = RecoverOrbRecord(entry, spawnedId);
                state = CanUseStoredOutcome(entry) ? entry.State : "unknown";
                prepared = entry.PreparedData;
            }
            else if (EnsureOrbJournal() && !_orbJournal!.TryGet(txId, out _) &&
                     OrbCaptureRequest.TryRead(new ZPackage(requestData), out OrbCaptureRequest? capture) && capture != null &&
                     IsValidCaptureRequest(capture) && capture.TransactionId == txId && capture.OrbId == orbId &&
                     CanRecoverEmptyOrbIdentity(orbId))
            {
                state = EmptyReleasedOrbRecovery;
                prepared = requestData; // Bind this permission to the exact rejected capture request.
            }
            else if (EnsureOrbJournal() && _orbJournal!.TryGetBlockingRelease(orbId, out TamingOrbJournal.Entry? consumed) &&
                     consumed != null && consumed.State == TamingOrbJournal.CompletedSuccess && CanUseStoredOutcome(consumed) &&
                     OrbReleaseRequest.TryRead(new ZPackage(requestData), out OrbReleaseRequest? requested) && requested != null &&
                     requested.TransactionId == txId && requested.OrbId == orbId &&
                     OrbReleaseRequest.TryRead(new ZPackage(consumed.RequestData), out OrbReleaseRequest? original) && original != null &&
                     requested.Payload == original.Payload && requested.CreatureManagerState == original.CreatureManagerState)
                state = TamingOrbJournal.CompletedSuccess;
            // A missing record alone is not proof of failure after a backup restore. Only
            // the confirmed empty-identity case above is recoverable without a world mutation.
            ZPackage reply = new();
            reply.Write(world); reply.Write(txId); reply.Write(orbId); reply.Write(state); reply.Write(prepared);
            ZRoutedRpc.instance.InvokeRoutedRPC(sender, OrbRecoveryClientRpc, reply);
            if (state == "unknown") OrbServerReport.ObserveUnknownRecovery(txId, orbId);
        }
        catch (Exception ex) { FeedLikeGrandmaPlugin.Log.LogWarning($"Orb recovery request rejected: {ex.Message}"); }
    }

    private static void ReceiveOrbRecoveryOnClient(long sender, ZPackage pkg)
    {
        if (!IsOrbMessageFromServer(sender)) return;
        try
        {
            long world = pkg.ReadLong(); string txId = pkg.ReadString(); string orbId = pkg.ReadString();
            string state = pkg.ReadString(); string prepared = pkg.ReadString();
            if (world != OrbWorldUid() || !OrbRecoveryRequests.ContainsKey(txId)) return;
            if (state == EmptyReleasedOrbRecovery)
            {
                if (TryRecoverEmptyOrbOnClient(txId, orbId, prepared)) return;
                LogOrbFailure("empty_identity_recovery_unresolved", orbId, txId);
                if (Player.m_localPlayer != null)
                    FeedLocalization.Message(Player.m_localPlayer, "$flg_taming_orb_recovery_blocked");
                return;
            }
            bool success = state == TamingOrbJournal.CompletedSuccess;
            bool final = success || state == TamingOrbJournal.CompletedFailure;
            bool resolved = final && ResolveOrbItem(txId, orbId, success, prepared);
            if (resolved)
            {
                ClientCaptureTransactions.Remove(txId);
                ClientReleaseTransactions.Remove(txId);
                OrbRecoveryRequests.Remove(txId);
            }
            else LogOrbFailure(final ? "recovery_item_unresolved" : "recovery_outcome_unconfirmed", orbId, txId);
            if (Player.m_localPlayer != null)
                FeedLocalization.Message(Player.m_localPlayer, resolved ? "$flg_taming_orb_recovered" : "$flg_taming_orb_recovery_blocked");
        }
        catch (Exception ex) { FeedLikeGrandmaPlugin.Log.LogWarning($"Orb recovery reply rejected: {ex.Message}"); }
    }

    private static TamingOrbJournal.Entry RecoverOrbRecord(TamingOrbJournal.Entry entry, string savedSpawnedId)
    {
        if (entry.State == TamingOrbJournal.CompletedSuccess || entry.State == TamingOrbJournal.CompletedFailure) return entry;
        if (entry.Kind == TamingOrbJournal.Release && OrbReleaseRequest.TryRead(new ZPackage(entry.RequestData), out OrbReleaseRequest? request) && request != null)
        {
            if (!string.IsNullOrEmpty(savedSpawnedId))
            {
                string[] parts = savedSpawnedId.Split(':');
                if (parts.Length != 2 || !long.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out long user) ||
                    !uint.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out uint id)) return entry;
                ZDOID savedId = new(user, id);
                if (IsVerifiedReleasedCreature(ZDOMan.instance.GetZDO(savedId), request))
                    RecoveredReleaseIds[entry.TransactionId] = savedId;
            }
            RefreshReleaseRecoveryIndex(entry.TransactionId);
            if (RecoveredReleaseIds.TryGetValue(entry.TransactionId, out ZDOID spawnedId) &&
                IsVerifiedReleasedCreature(ZDOMan.instance.GetZDO(spawnedId), request))
            {
                ServerReleaseTransaction transaction = new() { Request = request };
                OrbReleaseResult result = OrbReleaseResult.SuccessResult(request, spawnedId);
                if (PersistReleaseResult(transaction, result))
                {
                    if (ServerReleaseTransactions.TryGetValue(entry.TransactionId, out ServerReleaseTransaction? live)) live.Result = result;
                    if (_orbJournal!.TryGet(entry.TransactionId, out TamingOrbJournal.Entry? saved) && saved != null) entry = saved;
                }
            }
        }
        else if (entry.Kind == TamingOrbJournal.Capture)
        {
            if (entry.State == TamingOrbJournal.Reserved && OrbCaptureRequest.TryRead(new ZPackage(entry.RequestData), out OrbCaptureRequest? captureRequest) && captureRequest != null)
            {
                // No destruction was authorized: persist cancellation before telling the holder to roll back.
                ServerCaptureTransaction tx = new() { Request = captureRequest };
                if (!string.IsNullOrEmpty(entry.PreparedData) &&
                    OrbCapturePrepared.TryRead(new ZPackage(entry.PreparedData), out OrbCapturePrepared? prepared)) tx.Prepared = prepared;
                OrbCaptureResult failed = new() { TransactionId = captureRequest.TransactionId, TargetId = captureRequest.TargetId, ExpectedPrefab = captureRequest.ExpectedPrefab };
                if (PersistCapture(tx, TamingOrbJournal.CompletedFailure, failed))
                {
                    if (ServerCaptureTransactions.TryGetValue(entry.TransactionId, out ServerCaptureTransaction? live))
                    {
                        live.Result = failed; live.Phase = OrbCaptureServerPhase.Completed; ReleaseCaptureLock(live);
                    }
                    if (_orbJournal!.TryGet(entry.TransactionId, out TamingOrbJournal.Entry? saved) && saved != null) entry = saved;
                }
            }
            else if (ServerCaptureTransactions.TryGetValue(entry.TransactionId, out ServerCaptureTransaction? live) && live.Phase == OrbCaptureServerPhase.Committing)
                ForwardCaptureCommit(live);
        }
        return entry!;
    }

    private static bool IsVerifiedReleasedCreature(ZDO? zdo, OrbReleaseRequest request)
    {
        return zdo != null && zdo.GetInt(ReleaseReadyHash, 0) == 1 &&
               zdo.GetString(ReleaseTransactionHash, string.Empty) == request.TransactionId &&
               zdo.GetString(ReleaseOrbHash, string.Empty) == request.OrbId &&
               zdo.GetVec3(ReleaseOriginHash, out Vector3 origin) && (origin - request.SpawnPosition).sqrMagnitude < 0.01f &&
               CapturedTamePayload.TryDeserialize(request.Payload, out CapturedTamePayload? payload, logWarnings: false) &&
               payload != null && zdo.GetPrefab() == payload.Prefab.GetStableHashCode();
    }

    private static bool CanCaptureReleasedCreature(ZDO zdo)
    {
        string txId = zdo.GetString(ReleaseTransactionHash, string.Empty);
        // Legacy release marks had no orb id/ready state and cannot be mistaken for new pending releases.
        if (string.IsNullOrEmpty(txId) || string.IsNullOrEmpty(zdo.GetString(ReleaseOrbHash, string.Empty))) return true;
        return EnsureOrbJournal() && _orbJournal!.TryGet(txId, out TamingOrbJournal.Entry? entry) && entry != null &&
               entry.Kind == TamingOrbJournal.Release && entry.State == TamingOrbJournal.CompletedSuccess;
    }

    private static bool HasUnresolvedCaptureForTarget(ZDOID target)
    {
        if (!EnsureOrbJournal() || DurableCaptureTargets.ContainsKey(target)) return true;
        string previous = ZDOMan.instance.GetZDO(target)?.GetString(CaptureTransactionHash, string.Empty) ?? string.Empty;
        return previous.Length > 0 && _orbJournal!.TryGet(previous, out TamingOrbJournal.Entry? entry) && entry != null &&
               entry.Kind == TamingOrbJournal.Capture && entry.State != TamingOrbJournal.CompletedFailure;
    }

    private static void RefreshReleaseRecoveryIndex(string txId)
    {
        // Recovery only; never scan the world's extra data on a frame or normal retry path.
        if (RecoveredReleaseIds.TryGetValue(txId, out ZDOID cached) && ZDOMan.instance.GetZDO(cached) != null) return;
        if (Time.realtimeSinceStartup - _lastReleaseRecoveryScan < 10f) return;
        _lastReleaseRecoveryScan = Time.realtimeSinceStartup;
        RecoveredReleaseIds.Clear();
        foreach (ZDOID id in ZDOExtraData.GetAllZDOIDsWithHash(ZDOExtraData.Type.String, ReleaseTransactionHash))
        {
            ZDO? zdo = ZDOMan.instance.GetZDO(id);
            if (zdo != null) RecoveredReleaseIds[zdo.GetString(ReleaseTransactionHash, string.Empty)] = id;
        }
    }

    private static void UpdateDurableCaptureTarget(TamingOrbJournal.Entry entry)
    {
        if (entry.Kind != TamingOrbJournal.Capture ||
            !OrbCaptureRequest.TryRead(new ZPackage(entry.RequestData), out OrbCaptureRequest? request) || request == null) return;
        if (entry.State == TamingOrbJournal.Reserved || entry.State == TamingOrbJournal.Committing)
            DurableCaptureTargets[request.TargetId] = entry.TransactionId;
        else if (DurableCaptureTargets.TryGetValue(request.TargetId, out string txId) && txId == entry.TransactionId)
            DurableCaptureTargets.Remove(request.TargetId);
    }

    private static bool CanUseStoredOutcome(TamingOrbJournal.Entry entry)
    {
        if (entry.State != TamingOrbJournal.CompletedSuccess || SessionFinalOrbTransactions.Contains(entry.TransactionId)) return true;
        // Journal flush precedes world saves. A receipt from an earlier server session alone cannot
        // establish that the corresponding world mutation survived a crash or a world rollback.
        if (entry.Kind == TamingOrbJournal.Release)
        {
            if (!OrbReleaseRequest.TryRead(new ZPackage(entry.RequestData), out OrbReleaseRequest? request) || request == null) return false;
            RefreshReleaseRecoveryIndex(entry.TransactionId);
            return RecoveredReleaseIds.TryGetValue(entry.TransactionId, out ZDOID id) &&
                   IsVerifiedReleasedCreature(ZDOMan.instance.GetZDO(id), request);
        }
        // Absence of an OLD ZDOID is not evidence of a durable deletion after world load.
        // No tombstone is stored in the vanilla world for captures; leave that ambiguous receipt quarantined.
        return false;
    }

    private static bool HandleRecordedCaptureRequest(long peer, OrbCaptureRequest request)
    {
        if (!EnsureOrbJournal())
        {
            SendCapturePreparedToPeer(peer, OrbCapturePrepared.Failure(request, FeedLocalization.Text("$flg_taming_orb_recovery_blocked")));
            LogOrbFailure("capture_journal_unavailable", request.OrbId, request.TransactionId);
            return true;
        }
        if (_orbJournal!.TryGet(request.TransactionId, out TamingOrbJournal.Entry? same) && same != null)
        {
            if (same.RequestData == request.Write().GetBase64() && !string.IsNullOrEmpty(same.ResultData) && CanUseStoredOutcome(same))
                ZRoutedRpc.instance.InvokeRoutedRPC(peer, CaptureResultClientRpc, new ZPackage(same.ResultData));
            else LogOrbFailure("recorded_capture_unresolved", request.OrbId, request.TransactionId);
            return true; // A persisted reservation must be reconciled, never silently re-executed.
        }
        if (!_orbJournal.TryGetBlockingAction(request.OrbId, TamingOrbJournal.Capture, out _)) return false;
        OrbCapturePrepared rejected = OrbCapturePrepared.Failure(request, FeedLocalization.Text("$flg_taming_orb_recovery_blocked"));
        rejected.RecoverEmptyOrb = CanRecoverEmptyOrbIdentity(request.OrbId);
        SendCapturePreparedToPeer(peer, rejected);
        LogOrbFailure("capture_blocked_by_record", request.OrbId, request.TransactionId);
        return true;
    }

    private static bool HandleRecordedReleaseRequest(long peer, OrbReleaseRequest request)
    {
        if (!EnsureOrbJournal())
        {
            SendReleaseApprovalToPeer(peer, OrbReleaseApproval.Failure(request, FeedLocalization.Text("$flg_taming_orb_recovery_blocked")));
            LogOrbFailure("release_journal_unavailable", request.OrbId, request.TransactionId);
            return true;
        }
        if (_orbJournal!.TryGet(request.TransactionId, out TamingOrbJournal.Entry? same) && same != null)
        {
            if (same.RequestData == request.Write().GetBase64() && !string.IsNullOrEmpty(same.ResultData) && CanUseStoredOutcome(same))
                ZRoutedRpc.instance.InvokeRoutedRPC(peer, ReleaseResultClientRpc, new ZPackage(same.ResultData));
            else LogOrbFailure("recorded_release_unresolved", request.OrbId, request.TransactionId);
            return true;
        }
        if (!_orbJournal.TryGetBlockingAction(request.OrbId, TamingOrbJournal.Release, out TamingOrbJournal.Entry? blocking)) return false;
        bool completedRelease = blocking != null && blocking.Kind == TamingOrbJournal.Release && blocking.State == TamingOrbJournal.CompletedSuccess;
        // This is the original decision, evaluated once. Diagnostics must not refresh
        // the recovery cache or repeat the decision merely to explain a rejection.
        bool storedOutcomeUsable = completedRelease && CanUseStoredOutcome(blocking!);
        if (storedOutcomeUsable &&
            OrbReleaseRequest.TryRead(new ZPackage(blocking!.RequestData), out OrbReleaseRequest? old) && old != null &&
            old.Payload == request.Payload && old.CreatureManagerState == request.CreatureManagerState &&
            OrbReleaseResult.TryRead(new ZPackage(blocking.ResultData), out OrbReleaseResult? result) && result != null)
        {
            // A stale saved copy of an already-consumed orb may only become empty, never spawn again.
            ZRoutedRpc.instance.InvokeRoutedRPC(peer, ReleaseResultClientRpc, OrbReleaseResult.SuccessResult(request, result.SpawnedId).Write());
        }
        else
        {
            SendReleaseApprovalToPeer(peer, OrbReleaseApproval.Failure(request, FeedLocalization.Text("$flg_taming_orb_recovery_blocked")));
            LogOrbFailure(!completedRelease ? "release_blocked_by_record" : !storedOutcomeUsable
                ? "release_outcome_unconfirmed" : "release_record_mismatch", request.OrbId, request.TransactionId);
            if (completedRelease) OrbServerReport.Observe(request, blocking!, storedOutcomeUsable);
        }
        return true;
    }
}
