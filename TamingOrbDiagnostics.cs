using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using UnityEngine;

namespace FeedLikeGrandma;

internal static partial class TamingOrbSystem
{
    private static readonly Dictionary<string, float> OrbFailureLogTimes = new(StringComparer.Ordinal);

    internal static void SetOrbServerDiagnosticsEnabled(bool enabled) => OrbServerReport.SetEnabled(enabled);

    private static void ResetOrbDiagnosticSession()
    {
        OrbServerReport.Reset();
        OrbFailureLogTimes.Clear();
    }

    // These short failure reasons remain useful with detailed diagnostics disabled.
    // Only constant call-site reasons and validated identifiers are logged.
    private static void LogOrbFailure(string reason, string orbId = "", string transactionId = "")
    {
        try
        {
            float now = Time.realtimeSinceStartup;
            if (OrbFailureLogTimes.TryGetValue(reason, out float last) && now - last < 30f) return;
            OrbFailureLogTimes[reason] = now;
            FeedLikeGrandmaPlugin.Log.LogWarning($"[OrbFailure] reason={reason} world={OrbWorldUid()} " +
                $"orb={OrbServerReport.GuidText(orbId)} transaction={OrbServerReport.GuidText(transactionId)}; contents protected");
        }
        catch (Exception) { /* Logging must never change the operation's outcome. */ }
    }

    // Observation only: do not call FLG recovery, change a ZDO, load a zone, or send an RPC.
    private static class OrbServerReport
    {
        internal const float IntervalSeconds = 30f;
        internal const int DetailLimit = 8;
        private const int MaximumEncodedChars = 24 * 1024 * 1024;
        private static float _lastReport = float.NegativeInfinity;
        private static float _lastRecoveryReport = float.NegativeInfinity;
        private static bool _enabled;

        internal static void SetEnabled(bool enabled)
        {
            if (_enabled == enabled) return;
            _enabled = enabled;
            Reset();
        }

        internal static void Reset()
        {
            _lastReport = float.NegativeInfinity;
            _lastRecoveryReport = float.NegativeInfinity;
        }

        // Uses only the journal already opened by ordinary recovery. It never opens
        // files, retries migration/recovery, or scans world data to explain a result.
        internal static void ObserveUnknownRecovery(string transactionId, string orbId)
        {
            try
            {
                if (!_enabled || ZNet.instance == null || !ZNet.instance.IsServer()) return;
                float now = Time.realtimeSinceStartup;
                if (now - _lastRecoveryReport < IntervalSeconds) return;
                _lastRecoveryReport = now;
                StringBuilder report = new(512);
                report.AppendLine($"[OrbJournalDiagnostics v1] world={OrbWorldUid()} orb={GuidText(orbId)} request={GuidText(transactionId)} outcome=unknown");
                AppendJournalStatus(report, transactionId, orbId);
                Warn(report.ToString());
            }
            catch (Exception) { /* Diagnostic failure cannot alter a recovery response. */ }
        }

        private static void AppendJournalStatus(StringBuilder report, string transactionId, string orbId)
        {
            report.AppendLine($"journalPath={SafeLabel(_orbJournalPath ?? string.Empty)} legacyJournalPath={SafeLabel(_orbLegacyJournalPath ?? string.Empty)} storage={SafeLabel(_orbJournalStorageStatus)}");
            report.AppendLine($"journalHealthy={(_orbJournal?.IsHealthy)?.ToString() ?? "unavailable"}");
            if (_orbJournal == null)
            {
                report.AppendLine("requestedRecord=unavailable blockingRelease=unavailable; journal was not opened successfully");
                return;
            }
            bool found = _orbJournal.TryGet(transactionId, out TamingOrbJournal.Entry? entry);
            bool blocked = _orbJournal.TryGetBlockingRelease(orbId, out TamingOrbJournal.Entry? released);
            report.AppendLine($"requestedRecordFound={found} requestedRecordOrbMatches={found && entry!.OrbId == orbId} requestedRecordState={SafeLabel(entry?.State ?? string.Empty)} " +
                $"blockingReleaseFound={blocked} blockingReleaseState={SafeLabel(released?.State ?? string.Empty)}; missing_record_is_not_proof_that_no_creature_was_created");
        }

        internal static void Observe(OrbReleaseRequest request, TamingOrbJournal.Entry receipt, bool storedOutcomeUsable)
        {
            try
            {
                if (!_enabled || ZNet.instance == null || !ZNet.instance.IsServer() ||
                    receipt.Kind != TamingOrbJournal.Release || receipt.State != TamingOrbJournal.CompletedSuccess) return;
                float now = Time.realtimeSinceStartup;
                if (now - _lastReport < IntervalSeconds) return;
                _lastReport = now;

                long world = ZNet.instance.GetWorld() != null ? ZNet.instance.GetWorldUID() : 0L;
                StringBuilder report = new(2048);
                report.AppendLine($"[OrbServerDiagnostics v1] utc={DateTime.UtcNow:O} flg={FeedLikeGrandmaPlugin.ModVersion} " +
                    $"world={world} orb={GuidText(request.OrbId)} request={GuidText(request.TransactionId)}");
                report.AppendLine($"reason=observed_completed_release_rejection storedOutcomeUsable={storedOutcomeUsable} " +
                    $"receipt={GuidText(receipt.TransactionId)} sessionFinal={SessionFinalOrbTransactions.Contains(receipt.TransactionId)} " +
                    $"journalHealthy={(_orbJournal?.IsHealthy)?.ToString() ?? "unavailable"}");
                AppendJournalStatus(report, request.TransactionId, request.OrbId);
                report.AppendLine($"scope=current_server_resident_ZDOs_with_release_transaction_key; " +
                    $"no_zone_load_or_state_changes; globalIntervalSeconds={IntervalSeconds}; markerDetailLimit={DetailLimit}");

                bool requestReadable = TryReadRequest(receipt.RequestData, out OrbReleaseRequest? original);
                bool resultReadable = TryReadResult(receipt.ResultData, out OrbReleaseResult? result);
                report.AppendLine($"receiptRequestReadable={requestReadable} receiptResultReadable={resultReadable} " +
                    $"requestPayloadChars={request.Payload.Length} requestModifiersChars={request.CreatureManagerState.Length}");
                if (!requestReadable || original == null)
                {
                    report.AppendLine("scanComplete=False reason=unreadable_receipt_request; no inference about creature existence");
                    Warn(report.ToString());
                    return;
                }

                bool prefabReadable = TryReadPrefabHeader(original.Payload, out int expectedPrefab);
                report.AppendLine($"receiptOrbMatches={original.OrbId == request.OrbId} receiptTransactionMatches={original.TransactionId == receipt.TransactionId} " +
                    $"payloadMatches={original.Payload == request.Payload} modifiersMatch={original.CreatureManagerState == request.CreatureManagerState} " +
                    $"payloadHeaderReadable={prefabReadable} expectedPrefabHash={expectedPrefab} origin={VectorText(original.SpawnPosition)}");
                report.AppendLine("markerFieldsMatch_only_compares_marker_fields_and_prefab_header; full_payload_validation_not_repeated; " +
                    "storedOutcomeUsable_is_the_observed_FLG_result; matching_markers_do_not_establish_life_or_health");
                bool cacheHit = RecoveredReleaseIds.TryGetValue(receipt.TransactionId, out ZDOID cachedId);
                report.AppendLine($"recoveryCacheHit={cacheHit} recoveryCacheAgeSeconds={Number(now - _lastReleaseRecoveryScan)}; cache_not_refreshed_by_diagnostic");
                if (cacheHit)
                    AppendCandidate(report, "recovery_cache", cachedId, original, prefabReadable, expectedPrefab);
                if (resultReadable && result != null)
                {
                    report.AppendLine($"receiptResultSuccess={result.Success} receiptResultIdentityMatches=" +
                        $"{result.TransactionId == receipt.TransactionId && result.OrbId == request.OrbId}; oldSpawnedId_is_hint_only_after_restart");
                    AppendCandidate(report, "old_spawned_id_hint", result.SpawnedId, original, prefabReadable, expectedPrefab);
                }

                // The game materializes this list after scanning the resident string-data index.
                // Throttling limits frequency, not the world-dependent cost of one scan.
                List<ZDOID> ids = ZDOExtraData.GetAllZDOIDsWithHash(ZDOExtraData.Type.String, ReleaseTransactionHash);
                int matches = 0, fieldMatches = 0, missingZdos = 0;
                foreach (ZDOID id in ids)
                {
                    ZDO? zdo = ZDOMan.instance.GetZDO(id);
                    if (zdo == null) { missingZdos++; continue; }
                    if (zdo.GetString(ReleaseTransactionHash, string.Empty) != original.TransactionId &&
                        zdo.GetString(ReleaseOrbHash, string.Empty) != original.OrbId) continue;
                    matches++;
                    if (matches <= DetailLimit)
                    {
                        if (AppendCandidate(report, "fresh_marker", id, original, prefabReadable, expectedPrefab)) fieldMatches++;
                    }
                    else if (MarkerFieldsMatch(zdo, original, prefabReadable, expectedPrefab)) fieldMatches++;
                }
                report.AppendLine($"scanComplete=True indexedIds={ids.Count} missingZdos={missingZdos} matchingCandidates={matches} " +
                    $"markerFieldMatches={fieldMatches} detailsOmitted={Math.Max(0, matches - DetailLimit)}");
                report.AppendLine("interpretation=not_found_is_not_proof_of_death_or_capture; scene_absence_is_not_ZDO_absence; " +
                    "multiple_matching_candidates_require_review; no_raw_payloads_or_creature_names_logged");
                Warn(report.ToString());
            }
            catch (Exception error)
            {
                // Inspection and logging failures must never escape into FLG's handler.
                try { Warn($"[OrbServerDiagnostics v1] failed orb={GuidText(request.OrbId)} " +
                    $"exception={error.GetType().Name} scanComplete=False; ordinary_rejection_unchanged"); }
                catch (Exception) { }
            }
        }

        private static bool MarkerFieldsMatch(ZDO zdo, OrbReleaseRequest original, bool prefabReadable, int expectedPrefab) =>
            prefabReadable && zdo.GetString(ReleaseTransactionHash, string.Empty) == original.TransactionId &&
            zdo.GetString(ReleaseOrbHash, string.Empty) == original.OrbId && zdo.GetInt(ReleaseReadyHash, 0) == 1 &&
            zdo.GetPrefab() == expectedPrefab && zdo.GetVec3(ReleaseOriginHash, out Vector3 origin) &&
            (origin - original.SpawnPosition).sqrMagnitude < 0.01f;

        private static bool AppendCandidate(StringBuilder report, string source, ZDOID id,
            OrbReleaseRequest original, bool prefabReadable, int expectedPrefab)
        {
            ZDO? zdo = ZDOMan.instance.GetZDO(id);
            if (zdo == null)
            {
                report.AppendLine($"candidate source={source} id={id} residentZdo=False");
                return false;
            }
            bool hasTx = zdo.GetString(ReleaseTransactionHash, out string releaseTx);
            bool hasOrb = zdo.GetString(ReleaseOrbHash, out string orbId);
            bool hasReady = zdo.GetInt(ReleaseReadyHash, out int ready);
            bool hasOrigin = zdo.GetVec3(ReleaseOriginHash, out Vector3 origin);
            bool originMatches = hasOrigin && (origin - original.SpawnPosition).sqrMagnitude < 0.01f;
            GameObject? sceneObject = ZNetScene.instance != null ? ZNetScene.instance.FindInstance(id) : null;
            bool fieldsMatch = MarkerFieldsMatch(zdo, original, prefabReadable, expectedPrefab);
            string captureTx = zdo.GetString(CaptureTransactionHash, string.Empty);
            string captureReceipt = _orbJournal != null && !string.IsNullOrEmpty(captureTx) &&
                _orbJournal.TryGet(captureTx, out TamingOrbJournal.Entry? captured) && captured != null
                ? captured.Kind + "/" + captured.State : "unavailable";
            report.AppendLine($"candidate source={source} id={id} residentZdo=True sceneLookupAvailable={ZNetScene.instance != null} " +
                $"sceneInstance={sceneObject != null} characterComponent={sceneObject != null && sceneObject.GetComponent<Character>() != null} " +
                $"owner={zdo.GetOwner()} position={VectorText(zdo.GetPosition())} prefabHash={zdo.GetPrefab()} markerFieldsMatch={fieldsMatch}");
            report.AppendLine($"  releaseTx={GuidText(releaseTx)} txMatches={hasTx && releaseTx == original.TransactionId} " +
                $"releaseOrb={GuidText(orbId)} orbMatches={hasOrb && orbId == original.OrbId} " +
                $"ready={(hasReady ? ready.ToString(CultureInfo.InvariantCulture) : "unset")} " +
                $"prefabMatches={prefabReadable && zdo.GetPrefab() == expectedPrefab} " +
                $"origin={(hasOrigin ? VectorText(origin) : "unset")} originMatches={originMatches} " +
                $"captureTx={GuidText(captureTx)} captureReceipt={SafeLabel(captureReceipt)}");
            // Character.IsTamed refreshes local state. Read stored values without its fallbacks.
            report.AppendLine($"  zdoLevel={(zdo.GetInt(ZDOVars.s_level, out int level) ? level.ToString(CultureInfo.InvariantCulture) : "unset")} " +
                $"zdoTamed={(zdo.GetBool(ZDOVars.s_tamed, out bool tamed) ? tamed.ToString() : "unset")} " +
                $"zdoHealth={(zdo.GetFloat(ZDOVars.s_health, out float health) ? Number(health) : "unset")} " +
                $"zdoMaxHealth={(zdo.GetFloat(ZDOVars.s_maxHealth, out float maxHealth) ? Number(maxHealth) : "unset")}; replicated_values_may_lag_owner");
            return fieldsMatch;
        }

        // Decode copies of saved strings. Existing packet readers only consume their
        // package; require complete consumption here to distinguish malformed records.
        private static ZPackage ReadPackage(string text)
        {
            if (string.IsNullOrEmpty(text) || text.Length > MaximumEncodedChars) throw new FormatException();
            return new ZPackage(text);
        }

        private static bool TryReadRequest(string text, out OrbReleaseRequest? request)
        {
            request = null;
            try
            {
                ZPackage pkg = ReadPackage(text);
                if (!OrbReleaseRequest.TryRead(pkg, out OrbReleaseRequest? parsed) || pkg.GetPos() != pkg.Size()) return false;
                request = parsed;
                return true;
            }
            catch (Exception) { return false; }
        }

        private static bool TryReadResult(string text, out OrbReleaseResult? result)
        {
            result = null;
            try
            {
                ZPackage pkg = ReadPackage(text);
                if (!OrbReleaseResult.TryRead(pkg, out OrbReleaseResult? parsed) || pkg.GetPos() != pkg.Size()) return false;
                result = parsed;
                return true;
            }
            catch (Exception) { return false; }
        }

        private static bool TryReadPrefabHeader(string text, out int hash)
        {
            hash = 0;
            try
            {
                ZPackage pkg = ReadPackage(text);
                int version = pkg.ReadInt();
                if (version != LegacyPayloadVersion && version != PayloadVersion) return false;
                string prefab = pkg.ReadString();
                if (string.IsNullOrWhiteSpace(prefab)) return false;
                hash = prefab.GetStableHashCode();
                return true;
            }
            catch (Exception) { return false; }
        }

        private static void Warn(string text)
        {
            try { FeedLikeGrandmaPlugin.Log.LogWarning(text); }
            catch (Exception) { }
        }
        internal static string GuidText(string text) => string.IsNullOrEmpty(text) ? "absent" :
            Guid.TryParseExact(text, "N", out _) ? text : "invalid";
        private static string Number(float value) => value.ToString("R", CultureInfo.InvariantCulture);
        private static string VectorText(Vector3 value) => $"({Number(value.x)},{Number(value.y)},{Number(value.z)})";
        private static string SafeLabel(string text)
        {
            if (string.IsNullOrEmpty(text)) return "unavailable";
            if (text.Length > 256) return "invalid";
            foreach (char character in text) if (char.IsControl(character)) return "invalid";
            return text;
        }
    }
}
