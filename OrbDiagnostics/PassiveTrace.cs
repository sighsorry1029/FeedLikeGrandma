#nullable disable
using HarmonyLib;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using UnityEngine;

namespace FeedLikeGrandma.OrbDiagnostics
{
    // Observers never invoke the selected FLG methods, write arguments/results, or register/send RPCs.
    internal sealed class PassiveTrace : IDisposable
    {
        private readonly Harmony _harmony = new Harmony(OrbDiagnosticsController.HarmonyId);
        private readonly List<MethodInfo> _targets = new List<MethodInfo>();
        private readonly List<string> _coverage = new List<string>();
        private readonly List<FieldInfo> _stateFields = new List<FieldInfo>();
        private Func<ZRoutedRpc, long> _serverPeer;
        private Assembly _flgAssembly;
        private MethodInfo _outgoing;
        private bool _installHooks;

        internal void Initialize()
        {
            MethodInfo serverPeer = typeof(ZRoutedRpc).GetMethod("GetServerPeerID", BindingFlags.Instance | BindingFlags.NonPublic, null, Type.EmptyTypes, null);
            if (serverPeer != null && serverPeer.ReturnType == typeof(long))
            {
                try { _serverPeer = (Func<ZRoutedRpc, long>)Delegate.CreateDelegate(typeof(Func<ZRoutedRpc, long>), serverPeer); }
                catch { _serverPeer = null; }
            }
            Discover();
        }

        internal void StartObserving()
        {
            if (_installHooks) return;
            _installHooks = true;
            Discover();
        }

        private void Discover()
        {
            _coverage.Clear();
            if (_serverPeer == null) _coverage.Add("Server peer identity uninspected: getter unavailable; response source cannot be classified.");
            _targets.Clear();
            _stateFields.Clear();
            _flgAssembly = typeof(TamingOrbSystem).Assembly;
            Type orb = typeof(TamingOrbSystem);
            {
                Add(orb, "TryHandleTameableUseItem", typeof(bool), typeof(Tameable), typeof(Humanoid), typeof(ItemDrop.ItemData));
                foreach (string name in new[] { "TryHandlePlayerUseItem", "TryHandlePendingOrb", "TryRelease" })
                    Add(orb, name, typeof(bool), typeof(Player), typeof(ItemDrop.ItemData));
                Add(orb, "TryCapture", typeof(bool), typeof(Player), typeof(Character), typeof(ItemDrop.ItemData));
                Add(orb, "BeginNetworkCapture", typeof(bool), typeof(Player), typeof(ItemDrop.ItemData), typeof(ZDOID), typeof(string), typeof(float));
                Add(orb, "BeginNetworkRelease", typeof(bool), typeof(Player), typeof(ItemDrop.ItemData), Nested(orb, "CapturedTamePayload"), typeof(string), typeof(GameObject), typeof(Vector3), typeof(Quaternion), typeof(float));
                Add(orb, "ResolveOrbItem", typeof(bool), typeof(string), typeof(string), typeof(bool), typeof(string));
                foreach (string name in new[] { "ReceiveCapturePreparedOnClient", "ReceiveCaptureResultOnClient", "ReceiveReleaseApprovalOnClient", "ReceiveReleaseResultOnClient", "ReceiveOrbRecoveryOnClient", "ReceiveCapturePrepareOnOwner", "ReceiveCaptureCommitOnOwner" })
                    Add(orb, name, typeof(void), typeof(long), typeof(ZPackage));
                Add(orb, "FinishCaptureClientFailure", typeof(void), Nested(orb, "ClientCaptureTransaction"), typeof(string));
                Add(orb, "FinishReleaseClient", typeof(void), Nested(orb, "ClientReleaseTransaction"), typeof(bool), typeof(string));
                foreach (string name in new[] { "ClientCaptureTransactions", "ClientReleaseTransactions", "OrbRecoveryRequests" })
                {
                    FieldInfo field = orb.GetField(name, BindingFlags.Static | BindingFlags.NonPublic);
                    if (field != null && typeof(IDictionary).IsAssignableFrom(field.FieldType)) _stateFields.Add(field);
                    else _coverage.Add("Uninspected runtime dictionary: " + name);
                }
                Add(typeof(FeedLocalization), "Message", typeof(void), typeof(Player), typeof(string), typeof(object[]));
            }
            Add(typeof(Player), "UseHotbarItem", typeof(void), typeof(int));
            Add(typeof(Humanoid), "UseItem", typeof(void), typeof(Inventory), typeof(ItemDrop.ItemData), typeof(bool));
            Add(typeof(Tameable), "UseItem", typeof(bool), typeof(Humanoid), typeof(ItemDrop.ItemData));
            _outgoing = typeof(ZRoutedRpc).GetMethod("InvokeRoutedRPC", new[] { typeof(long), typeof(string), typeof(object[]) });
            if (_outgoing == null) _coverage.Add("Outgoing RPC observation unavailable: signature mismatch.");
            else
            {
                try
                {
                    if (_installHooks) _harmony.Patch(_outgoing, Hook(nameof(OutgoingPrefix), Priority.First), Hook(nameof(OutgoingPostfix), Priority.Last), finalizer: Hook(nameof(OutgoingFinalizer), Priority.Last));
                    _coverage.Add((_installHooks ? "Observed: " : "Available (trace off): ") + "ZRoutedRpc.InvokeRoutedRPC(long,string,object[]) filtered to FLG_Orb*");
                }
                catch (Exception error) { _coverage.Add("Outgoing RPC hook failed: " + error.GetType().Name); }
            }
        }

        private static Type Nested(Type owner, string name) => owner.GetNestedType(name, BindingFlags.Public | BindingFlags.NonPublic);

        private void Add(Type owner, string name, Type result, params Type[] parameters)
        {
            if (owner == null || parameters.Any(type => type == null)) { _coverage.Add("Unobserved: " + name + " (type unavailable)"); return; }
            MethodInfo method = owner.GetMethod(name, BindingFlags.Static | BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly, null, parameters, null);
            bool expectStatic = owner.Assembly == _flgAssembly;
            if (method == null || method.ReturnType != result || method.IsStatic != expectStatic || method.ContainsGenericParameters)
            {
                _coverage.Add("Unobserved: " + owner.FullName + "." + name + " (contract mismatch)");
                return;
            }
            try
            {
                if (_installHooks) _harmony.Patch(method, Hook(nameof(Prefix), Priority.First), Hook(result == typeof(bool) ? nameof(BoolPostfix) : nameof(VoidPostfix), Priority.Last), finalizer: Hook(nameof(Finalizer), Priority.Last));
                _targets.Add(method);
                _coverage.Add((_installHooks ? "Observed: " : "Available (trace off): ") + owner.FullName + "." + name);
            }
            catch (Exception error) { _coverage.Add("Unobserved: " + name + " (hook error " + error.GetType().Name + ")"); }
        }

        private static HarmonyMethod Hook(string name, int priority) => new HarmonyMethod(typeof(PassiveTrace).GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic)) { priority = priority };

        internal void StopObserving()
        {
            _installHooks = false;
            _harmony.UnpatchSelf();
        }
        public void Dispose() => StopObserving();

        internal void Describe(DiagnosticReport report)
        {
            report.Environment["diagnosticHooksActive"] = _installHooks.ToString();
            report.Coverage.AddRange(_coverage.Select(value => !_installHooks && value.StartsWith("Observed: ", StringComparison.Ordinal)
                ? "Previously observed (trace now off): " + value.Substring("Observed: ".Length) : value));
            report.Coverage.Add("Hooks remain armed while OrbDiagnostics is On in a local world. An Orb event starts a 60-second report; idle observation does not make snapshots or copy packets. Owner handling is visible only when this client owns the creature. Other clients/server internals are unobserved.");
            if (_flgAssembly != null)
            {
                report.Environment["flgAssembly"] = _flgAssembly.FullName;
                report.Environment["flgMvid.identificationOnly"] = _flgAssembly.ManifestModule.ModuleVersionId.ToString();
            }
            foreach (MethodInfo method in _targets)
            {
                Patches patches = Harmony.GetPatchInfo(method);
                if (patches == null) continue;
                foreach (Patch patch in patches.Prefixes) DescribePatch(report, method, "prefix", patch);
                foreach (Patch patch in patches.Postfixes) DescribePatch(report, method, "postfix", patch);
                foreach (Patch patch in patches.Transpilers) DescribePatch(report, method, "transpiler", patch);
                foreach (Patch patch in patches.Finalizers) DescribePatch(report, method, "finalizer", patch);
            }
        }

        private static void DescribePatch(DiagnosticReport report, MethodInfo method, string kind, Patch patch)
        {
            report.Coverage.Add("Patch metadata (not resolved execution order): " + method.DeclaringType?.FullName + "." + method.Name + " " + kind +
                " owner=" + patch.owner + " priority=" + patch.priority + " before=" + string.Join(",", patch.before) + " after=" + string.Join(",", patch.after) +
                " method=" + patch.PatchMethod.DeclaringType?.FullName + "." + patch.PatchMethod.Name);
        }

        internal void ReadRuntimeState(Dictionary<string, string> facts)
        {
            facts["serverPeerAvailable"] = _serverPeer == null ? "uninspected: getter unavailable"
                : (ZRoutedRpc.instance != null && _serverPeer(ZRoutedRpc.instance) != 0).ToString();
            foreach (FieldInfo field in _stateFields)
            {
                if (!(field.GetValue(null) is IDictionary dictionary)) { facts[field.Name] = "unavailable"; continue; }
                facts[field.Name + ".count"] = dictionary.Count.ToString(CultureInfo.InvariantCulture);
                int count = 0;
                foreach (DictionaryEntry entry in dictionary)
                {
                    if (++count > 64) { facts[field.Name + ".truncated"] = "first 64 only"; break; }
                    facts[field.Name + ".tx." + entry.Key] = entry.Value is float time ? time.ToString("R", CultureInfo.InvariantCulture) : "present";
                }
            }
        }

        private static bool Relevant(MethodBase method, object instance, object[] args)
        {
            if (OrbDiagnosticsController.Instance == null || !OrbDiagnosticsController.Instance.CanObserve) return false;
            if (method.DeclaringType == typeof(Player))
            {
                if (!ReferenceEquals(instance, Player.m_localPlayer)) return false;
                int index = (int)args[0];
                return index > 0 && OrbDiagnosticsController.IsCandidate(Player.m_localPlayer.GetInventory().GetItemAt(index - 1, 0));
            }
            if (instance is Humanoid && !ReferenceEquals(instance, Player.m_localPlayer)) return false;
            // Check the user argument only: a capture target may itself be a
            // Humanoid, and must not be mistaken for a remote actor.
            if (method.DeclaringType == typeof(Tameable) && args.Length > 0 && !ReferenceEquals(args[0], Player.m_localPlayer)) return false;
            if (method.Name == "TryHandleTameableUseItem" && args.Length > 1 && !ReferenceEquals(args[1], Player.m_localPlayer)) return false;
            foreach (object arg in args)
            {
                if (arg is Player player && !ReferenceEquals(player, Player.m_localPlayer)) return false;
                if (arg is ItemDrop.ItemData item && !OrbDiagnosticsController.IsCandidate(item)) return false;
            }
            if (method.Name == "Message") return args.Length > 1 && args[1] is string token && token.StartsWith("$flg_taming_orb", StringComparison.Ordinal);
            return true;
        }

        private static bool Prepare(bool mayStart)
        {
            OrbDiagnosticsController controller = OrbDiagnosticsController.Instance;
            return controller != null && Player.m_localPlayer != null &&
                controller.PrepareObservation(mayStart, OrbDiagnosticsController.CurrentWorld(), Time.realtimeSinceStartup);
        }

        private static void Observe(MethodBase method, object instance, object[] args, string phase, bool? result, bool? ranOriginal, Exception error)
        {
            try
            {
                if (!Relevant(method, instance, args)) return;
                if (!Prepare(phase == "enter")) return;
                OrbDiagnosticsController plugin = OrbDiagnosticsController.Instance;
                plugin.Record(method.DeclaringType?.Name + "." + method.Name, phase, facts =>
                {
                    if (result.HasValue) facts["returnedBool"] = result.Value.ToString();
                    if (ranOriginal.HasValue) facts["ranOriginal"] = ranOriginal.Value.ToString();
                    if (error != null) { facts["exceptionType"] = error.GetType().FullName; facts["exceptionMessage"] = DiagnosticReport.Safe(error.Message); }
                    ParameterInfo[] parameters = method.GetParameters();
                    for (int i = 0; i < args.Length; ++i)
                    {
                        string name = i < parameters.Length ? parameters[i].Name : "arg" + i;
                        if (args[i] is ZPackage package)
                        {
                            // Only entry copies packets. Postfix position is observed without another allocation.
                            if (phase == "enter") plugin.ReadPackage(package, method.Name, facts, name + ".");
                            else facts[name + ".readPosition"] = package.GetPos().ToString(CultureInfo.InvariantCulture);
                        }
                        else Summarize(name, args[i], facts);
                    }
                    if (method.Name.StartsWith("Receive", StringComparison.Ordinal) && args.Length > 0 && args[0] is long sender)
                    {
                        var observer = plugin.Observer;
                        facts["senderMatchesServerPeer"] = observer._serverPeer != null && ZRoutedRpc.instance != null
                            ? (sender != 0 && sender == observer._serverPeer(ZRoutedRpc.instance)).ToString() : "uninspected";
                    }
                    if (method.DeclaringType == typeof(Player) && Player.m_localPlayer != null)
                        Summarize("hotbarItem", Player.m_localPlayer.GetInventory().GetItemAt((int)args[0] - 1, 0), facts);
                });
            }
            catch { /* A diagnostic failure must not escape into the original operation. */ }
        }

        private static void Summarize(string name, object value, Dictionary<string, string> facts)
        {
            if (value == null) { facts[name] = "<null>"; return; }
            if (value is ItemDrop.ItemData item)
            {
                facts[name + ".runtimeItemRef"] = OrbDiagnosticsController.ItemRef(item);
                facts[name + ".slot"] = (item.m_gridPos.x + 1) + "," + (item.m_gridPos.y + 1);
                facts[name + ".stack"] = Convert.ToString(item.m_stack, CultureInfo.InvariantCulture);
                facts[name + ".variant"] = Convert.ToString(item.m_variant, CultureInfo.InvariantCulture);
                foreach (string key in new[] { "flg.tame.orbId", "flg.tame.pending", "flg.tame.payload" })
                {
                    bool found = item.m_customData != null && item.m_customData.TryGetValue(key, out _);
                    facts[name + "." + key + ".present"] = found.ToString();
                    if (found)
                    {
                        string text = item.m_customData[key];
                        facts[name + "." + key] = key.EndsWith("orbId", StringComparison.Ordinal) && (text?.Length ?? 0) <= 128
                            ? text : "chars=" + (text?.Length ?? 0);
                    }
                }
                return;
            }
            if (value is string str) { facts[name] = str.Length <= 512 ? str : "<string chars=" + str.Length + "; body omitted>"; return; }
            if (value is Character character)
            {
                facts[name + ".localPlayer"] = ReferenceEquals(character, Player.m_localPlayer).ToString();
                facts[name + ".zdoId"] = character.GetZDOID().ToString();
                return;
            }
            if (value is GameObject gameObject) { facts[name + ".prefab"] = gameObject.name; return; }
            if (value is bool || value is int || value is long || value is float || value is ZDOID || value is Vector3 || value is Quaternion)
                facts[name] = Convert.ToString(value, CultureInfo.InvariantCulture);
            else facts[name] = "<" + value.GetType().FullName + ">";
        }

        private static void Prefix(MethodBase __originalMethod, object __instance, object[] __args) => Observe(__originalMethod, __instance, __args, "enter", null, null, null);
        private static void BoolPostfix(MethodBase __originalMethod, object __instance, object[] __args, bool __result, bool __runOriginal) => Observe(__originalMethod, __instance, __args, "return", __result, __runOriginal, null);
        private static void VoidPostfix(MethodBase __originalMethod, object __instance, object[] __args, bool __runOriginal) => Observe(__originalMethod, __instance, __args, "return", null, __runOriginal, null);
        private static void Finalizer(MethodBase __originalMethod, object __instance, object[] __args, Exception __exception)
        {
            if (__exception != null) Observe(__originalMethod, __instance, __args, "exception (unchanged)", null, null, __exception);
        }

        private static void OutgoingPrefix(long __0, string __1, object[] __2)
        {
            try
            {
                if (__1 == null || !__1.StartsWith("FLG_Orb", StringComparison.Ordinal) || !Prepare(true)) return;
                OrbDiagnosticsController.Instance.Record(__1, "outgoing invocation", facts =>
                {
                    facts["targetPeer"] = __0.ToString(CultureInfo.InvariantCulture);
                    for (int i = 0; i < __2.Length; ++i)
                        if (__2[i] is ZPackage package) OrbDiagnosticsController.Instance.ReadPackage(package, __1, facts, "arg" + i + ".");
                });
            }
            catch { }
        }

        private static void OutgoingPostfix(string __1, bool __runOriginal)
        {
            try
            {
                if (__1 != null && __1.StartsWith("FLG_Orb", StringComparison.Ordinal) && Prepare(false))
                    OrbDiagnosticsController.Instance?.Record(__1, "outgoing invocation returned (delivery unknown)", facts => facts["ranOriginal"] = __runOriginal.ToString());
            }
            catch { }
        }

        private static void OutgoingFinalizer(string __1, Exception __exception)
        {
            try
            {
                if (__exception != null && __1 != null && __1.StartsWith("FLG_Orb", StringComparison.Ordinal) && Prepare(false))
                    OrbDiagnosticsController.Instance?.Record(__1, "outgoing exception (unchanged)", facts => facts["exceptionType"] = __exception.GetType().FullName);
            }
            catch { }
        }
    }
}
