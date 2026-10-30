#nullable disable
using BepInEx;
using BepInEx.Bootstrap;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using UnityEngine;

namespace FeedLikeGrandma.OrbDiagnostics
{
    // Owned by the main plugin. No second BepInEx entry point or network/config sync.
    internal sealed class OrbDiagnosticsController
    {
        internal const string LegacyPluginGuid = "sighsorry.FeedLikeGrandma.OrbDiagnostics";
        internal const string HarmonyId = "sighsorry.FeedLikeGrandma.OrbDiagnostics.Builtin";
        internal static OrbDiagnosticsController Instance;
        private PassiveTrace _observer;
        private DiagnosticReport _recording;
        private DiagnosticReport _unsaved;
        private float _deadline;
        private long _observerWorld;
        private int _sequence;
        private int _packetBudget;
        private string _reportDirectory;
        private bool _saving;
        private bool _enabled;
        private bool _initialized;
        private bool _shutdown;
        private bool _legacyWarning;
        private bool _transitioning;
        private bool _faulted;

        internal bool Recording => _recording != null && !_saving && !_transitioning;
        internal bool Enabled => _enabled;
        internal bool CanObserve => _enabled && !_shutdown && !_saving && !_transitioning &&
            !_faulted && _unsaved == null && _observer != null;
        internal PassiveTrace Observer => _observer;

        internal void Initialize()
        {
            if (_initialized || _shutdown) return;
            _reportDirectory = Path.Combine(Paths.ConfigPath, "FeedLikeGrandma", "diagnostics");
            Instance = this;
            _initialized = true;
        }

        internal void SetEnabled(bool enabled)
        {
            if (!_initialized || _shutdown) return;
            if (!enabled)
            {
                _enabled = false;
                StopAndDispose("OrbDiagnostics switched Off");
                return;
            }
            if (_enabled) return;
            if (Chainloader.PluginInfos.ContainsKey(LegacyPluginGuid))
            {
                if (!_legacyWarning)
                {
                    _legacyWarning = true;
                    FeedLikeGrandmaPlugin.Log.LogWarning("Built-in Orb client diagnostics disabled because the standalone OrbDiagnostics DLL is installed. Remove that DLL and restart to use [Debug] OrbDiagnostics. Its hooks are left unchanged.");
                }
                return;
            }
            _enabled = true;
            _faulted = false;
            // One explicit Off -> On transition retries a retained report. No
            // frame or Orb callback retries file writes or replaces that evidence.
            if (_unsaved != null) Save(_unsaved);
            // Discovery is deferred until a local player/world exists. Dedicated
            // servers use the server reports and never create this observer.
        }

        internal void Tick()
        {
            if (!_enabled || _shutdown) return;
            try
            {
                bool ready = Player.m_localPlayer != null && ZNet.instance != null && !ZNet.instance.IsDedicated();
                UpdateSession(ready, ready ? CurrentWorld() : 0L, Time.realtimeSinceStartup);
            }
            catch (Exception error)
            {
                FeedLikeGrandmaPlugin.Log.LogError("Orb diagnostics stopped (game unchanged): " + error);
                SetEnabled(false);
            }
        }

        // All hook installation/removal happens at lifecycle boundaries on the
        // game thread, never inside an observed game's prefix/postfix/finalizer.
        internal void UpdateSession(bool localPlayerReady, long world, float now)
        {
            if (!_enabled || _shutdown) return;
            if (_faulted)
            {
                _enabled = false;
                StopAndDispose("diagnostic observation failed; toggle Off then On to retry");
                return;
            }
            if (!localPlayerReady || world == 0L)
            {
                StopAndDispose("local player unavailable/disconnected");
                return;
            }
            if (_observer != null && _observerWorld != world)
                StopAndDispose("world changed", includeAfter: false);
            if (Recording && now >= _deadline) StopTrace("60-second limit");
            if (_unsaved != null)
            {
                // Saving failed or the report quota is full. Preserve the report
                // and stop observation until an explicit Off -> On retry.
                StopAndDispose("report awaits saving");
                return;
            }
            if (_observer != null) return;
            _observerWorld = world;
            _observer = new PassiveTrace();
            _observer.Initialize();
            _observer.StartObserving();
            Notice("Orb diagnostics On: use the affected Orb normally; collection starts automatically. Switch Off to save sooner. Reports: " + _reportDirectory);
        }

        // Called only after an Orb-specific filter has matched. Time/world inputs
        // keep the lifecycle decision explicit; callbacks never change game state.
        internal bool PrepareObservation(bool mayStart, long world, float now)
        {
            if (!CanObserve || world == 0L || world != _observerWorld) return false;
            if (Recording) return true;
            if (!mayStart) return false;
            try
            {
                StartTrace(world, now);
                return Recording;
            }
            catch (Exception error)
            {
                _faulted = true;
                try { FeedLikeGrandmaPlugin.Log.LogError("Orb diagnostic start failed; collection stopped without changing the Orb. Toggle Off then On to retry: " + error); }
                catch { }
                return false;
            }
        }

        internal void Shutdown()
        {
            if (_shutdown) return;
            _enabled = false;
            StopAndDispose("FeedLikeGrandma shutting down");
            if (_unsaved != null)
                FeedLikeGrandmaPlugin.Log.LogWarning("An Orb diagnostic report could not be saved and will be lost when the game exits. See the earlier report write error.");
            _shutdown = true;
            if (ReferenceEquals(Instance, this)) Instance = null;
        }

        private void StopAndDispose(string reason, bool includeAfter = true)
        {
            try { if (Recording) StopTrace(reason, includeAfter); }
            catch (Exception error) { FeedLikeGrandmaPlugin.Log.LogError("Orb diagnostic stop failed: " + error); }
            try { _observer?.Dispose(); }
            catch (Exception error) { FeedLikeGrandmaPlugin.Log.LogError("Orb diagnostic cleanup failed: " + error); }
            _observer = null;
            _observerWorld = 0L;
        }

        private void StartTrace(long world, float now)
        {
            _transitioning = true;
            try
            {
                DiagnosticReport report = NewReport("trace");
                report.Environment["worldUid"] = world.ToString(CultureInfo.InvariantCulture);
                report.Snapshots.Add(Snapshot("before"));
                _sequence = 0;
                _packetBudget = 2 * 1024 * 1024;
                _deadline = now + 60f;
                _recording = report;
            }
            finally { _transitioning = false; }
            Notice("Orb trace started automatically (60 sec). Switch OrbDiagnostics Off to save sooner.");
        }

        private void StopTrace(string reason, bool includeAfter = true)
        {
            DiagnosticReport report = _recording;
            if (report == null) return;
            _recording = null;
            _transitioning = true;
            try
            {
                if (includeAfter)
                {
                    try { report.Snapshots.Add(Snapshot("after")); }
                    catch (Exception error) { report.Coverage.Add("after snapshot failed: " + error.GetType().Name); }
                }
                else report.Coverage.Add("after snapshot omitted: the current player belongs to a different world");
                report.FinishedUtc = DateTime.UtcNow.ToString("O");
                report.StopReason = reason;
                Save(report);
            }
            finally { _transitioning = false; }
        }

        private void Save(DiagnosticReport report)
        {
            _saving = true;
            _unsaved = report;
            try
            {
                string path = report.Save(_reportDirectory);
                _unsaved = null;
                Notice("Orb diagnostics saved (.txt + .json): " + path);
            }
            catch (Exception error)
            {
                FeedLikeGrandmaPlugin.Log.LogError("Report write failed; retained in memory and new collection paused. Check the path/space or move old reports out of the reports folder, then toggle OrbDiagnostics Off -> On once to retry: " + error);
                Notice("Orb report not saved; collection paused. See LogOutput.log. Fix the save problem or move old reports, then switch OrbDiagnostics Off and On to retry.");
            }
            finally { _saving = false; }
        }

        private DiagnosticReport NewReport(string kind)
        {
            var report = new DiagnosticReport { Kind = kind };
            report.Environment["diagnosticVersion"] = "builtin/2-config-only";
            report.Environment["flgVersion"] = FeedLikeGrandmaPlugin.ModVersion;
            report.Environment["localTime"] = DateTimeOffset.Now.ToString("O");
            report.Environment["gameAssembly"] = typeof(Player).Assembly.FullName;
            report.Environment["gameMvid.identificationOnly"] = typeof(Player).Assembly.ManifestModule.ModuleVersionId.ToString();
            foreach (var plugin in Chainloader.PluginInfos.OrderBy(pair => pair.Key, StringComparer.Ordinal))
                report.Environment["plugin." + plugin.Key] = plugin.Value.Metadata.Name + " " + plugin.Value.Metadata.Version;
            _observer?.Describe(report);
            return report;
        }

        private InventorySnapshot Snapshot(string label)
        {
            var snapshot = new InventorySnapshot { Label = label };
            Player player = Player.m_localPlayer;
            snapshot.Facts["localPlayerPresent"] = (player != null).ToString();
            snapshot.Facts["worldUid"] = CurrentWorld().ToString(CultureInfo.InvariantCulture);
            snapshot.Facts["isServerOrHost"] = (ZNet.instance != null && ZNet.instance.IsServer()).ToString();
            _observer?.ReadRuntimeState(snapshot.Facts);
            if (player == null) return snapshot;
            List<ItemDrop.ItemData> inventory = player.GetInventory().GetAllItems();
            snapshot.Facts["inventoryItemCount"] = inventory.Count.ToString(CultureInfo.InvariantCulture);
            int rawBudget = 8 * 1024 * 1024;
            foreach (ItemDrop.ItemData item in inventory)
            {
                if (!IsCandidate(item)) continue;
                if (snapshot.Items.Count >= 64) { snapshot.Facts["itemLimit"] = "64 reached; remaining Orbs uninspected"; break; }
                var data = CopyFlgData(item);
                var itemReport = new ItemSnapshot { Label = "slot " + (item.m_gridPos.x + 1) + "," + (item.m_gridPos.y + 1) + " [" + ItemRef(item) + "]" };
                itemReport.Facts = OrbDataReader.Read(data);
                itemReport.Facts["runtimeItemRef"] = ItemRef(item);
                itemReport.Facts["slot"] = (item.m_gridPos.x + 1) + "," + (item.m_gridPos.y + 1);
                itemReport.Facts["prefab"] = item.m_dropPrefab != null ? item.m_dropPrefab.name : "<null>";
                itemReport.Facts["sharedName"] = item.m_shared?.m_name ?? "<null>";
                itemReport.Facts["stack"] = Convert.ToString(item.m_stack, CultureInfo.InvariantCulture);
                itemReport.Facts["variant"] = Convert.ToString(item.m_variant, CultureInfo.InvariantCulture);
                if (itemReport.Facts.TryGetValue("pending.worldUid", out string pendingWorld))
                    itemReport.Facts["pending.worldMatchesCurrentClientWorld"] = (pendingWorld == snapshot.Facts["worldUid"]).ToString();
                if (itemReport.Facts.TryGetValue("pending.previous.stack", out string previousStack))
                    itemReport.Facts["pending.currentStackMatchesPrevious"] = (previousStack == itemReport.Facts["stack"]).ToString();
                if (itemReport.Facts.TryGetValue("pending.previous.variant", out string previousVariant))
                    itemReport.Facts["pending.currentVariantMatchesPrevious"] = (previousVariant == itemReport.Facts["variant"]).ToString();
                foreach (var entry in data)
                {
                    int length = entry.Value?.Length ?? 0;
                    if (length <= 1024 * 1024 && length <= rawBudget)
                    {
                        itemReport.RawFlgData.Add(entry.Key, entry.Value);
                        rawBudget -= length;
                    }
                    else itemReport.Facts["rawExport." + entry.Key] = "omitted: per-value 1Mi chars / snapshot 8Mi chars cap";
                }
                snapshot.Items.Add(itemReport);
            }
            foreach (var group in snapshot.Items.Where(i => i.RawFlgData.ContainsKey("flg.tame.orbId"))
                .GroupBy(i => i.RawFlgData["flg.tame.orbId"], StringComparer.Ordinal))
            {
                if (string.IsNullOrEmpty(group.Key) || group.Count() < 2) continue;
                foreach (ItemSnapshot item in group) item.Facts["duplicateOrbIdInThisInventory"] = "YES: " + string.Join("; ", group.Select(i => i.Label));
            }
            return snapshot;
        }

        internal static long CurrentWorld()
        {
            return ZNet.instance != null && ZNet.instance.GetWorld() != null ? ZNet.instance.GetWorldUID() : 0;
        }

        internal static string ItemRef(ItemDrop.ItemData item) => item == null ? "null" : RuntimeHelpers.GetHashCode(item).ToString("x8", CultureInfo.InvariantCulture);

        internal static bool IsCandidate(ItemDrop.ItemData item)
        {
            return item != null && ((item.m_dropPrefab != null && item.m_dropPrefab.name == "FLG_TamingOrb") ||
                item.m_shared?.m_name == "$flg_taming_orb" ||
                (item.m_customData != null && (item.m_customData.ContainsKey("flg.tame.orbId") ||
                    item.m_customData.ContainsKey("flg.tame.pending") || item.m_customData.ContainsKey("flg.tame.payload"))));
        }

        private static Dictionary<string, string> CopyFlgData(ItemDrop.ItemData item)
        {
            var copy = new Dictionary<string, string>(StringComparer.Ordinal);
            if (item.m_customData == null) return copy;
            foreach (string key in new[] { "flg.tame.orbId", "flg.tame.payload", "flg.tame.pending", "flg.tame.creaturemanager" })
                if (item.m_customData.TryGetValue(key, out string value)) copy.Add(key, value);
            return copy;
        }

        internal void Record(string name, string phase, Action<Dictionary<string, string>> populate)
        {
            if (!Recording) return;
            if (_recording.Events.Count >= 600) { ++_recording.DroppedEvents; return; }
            var entry = new TraceEntry { Name = name, Phase = phase, Call = ++_sequence };
            try { populate(entry.Facts); }
            catch (Exception error) { entry.Facts["diagnosticReadError"] = error.GetType().Name; }
            _recording.Events.Add(entry);
        }

        internal void ReadPackage(ZPackage package, string method, Dictionary<string, string> facts, string prefix)
        {
            if (package == null) return;
            int length = package.Size();
            facts[prefix + "bytes"] = length.ToString(CultureInfo.InvariantCulture);
            int position = package.GetPos();
            facts[prefix + "readPosition"] = position.ToString(CultureInfo.InvariantCulture);
            if (length > 256 * 1024 || length > _packetBudget)
            {
                facts[prefix + "capture"] = "uninspected: packet cap 256KiB / trace budget 2MiB";
                return;
            }
            byte[] copy = package.GetArray(); // Original cursor unchanged; parsing only the copy.
            _packetBudget -= copy.Length;
            facts[prefix + "rawBase64"] = Convert.ToBase64String(copy);
            if (method == "ReceiveOrbRecoveryOnClient")
            {
                if (position == 0)
                    foreach (var fact in OrbDataReader.ReadRecoveryReply(copy)) facts[prefix + fact.Key] = fact.Value;
                else facts[prefix + "recovery.status"] = "uninspected: incoming read position is not zero";
            }
        }

        private void Notice(string message)
        {
            try { FeedLikeGrandmaPlugin.Log.LogInfo(message); }
            catch { }
            // A HUD that is being destroyed must not turn a successful save into
            // a reported failure or interrupt an observed Orb operation.
            try { if (MessageHud.instance != null) MessageHud.instance.ShowMessage(MessageHud.MessageType.Center, message); }
            catch { }
        }
    }
}
