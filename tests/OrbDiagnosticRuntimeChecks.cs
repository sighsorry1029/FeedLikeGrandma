using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Serialization;
using HarmonyLib;
using Mono.Cecil;

// Isolated original-Mono check. Harmony patches target only synthetic managed methods.
// Original ZPackage and the helper's pure managed packet reader are exercised separately.
// No world, native Unity component, plugin lifecycle, or real transaction is initialized.
internal static class OrbDiagnosticRuntimeChecks
{
    private const string OwnId = "flg.diagnostics.isolated.observer";
    private const string OtherId = "flg.diagnostics.isolated.other";
    private static Type _hooks;
    private static int _checks;
    private static int _originalCalls;
    private static int _otherCalls;
    private static readonly InvalidOperationException OriginalError = new InvalidOperationException("original sentinel");
    private const BindingFlags All = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

    private static int Main(string[] args)
    {
        if (args.Length != 3 && args.Length != 4) throw new ArgumentException("Expected merged FLG DLL, game Managed directory, BepInEx core directory, optional lifecycle mode.");
        string[] search = { Path.GetDirectoryName(Path.GetFullPath(args[0])), Path.GetFullPath(args[1]), Path.GetFullPath(args[2]) };
        AppDomain.CurrentDomain.AssemblyResolve += (_, request) =>
        {
            foreach (string directory in search)
            {
                string candidate = Path.Combine(directory, new AssemblyName(request.Name).Name + ".dll");
                if (File.Exists(candidate)) return Assembly.LoadFrom(candidate);
            }
            return null;
        };
        if (args.Length == 3) return Run(args);
        if (args[3] != "lifecycle") throw new ArgumentException("Only the explicit lifecycle fixture mode is supported.");
        string fixtureDir = Path.Combine(Path.GetTempPath(), "flg-diagnostic-startup-fixture-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(fixtureDir);
        string fixturePath = Path.Combine(fixtureDir, "FeedLikeGrandma.dll");
        try
        {
            Type paths = Assembly.Load("BepInEx").GetType("BepInEx.Paths", true);
            foreach (string name in new[] { "BepInExRootPath", "PluginPath", "GameRootPath" })
                paths.GetProperty(name, All).SetValue(null, fixtureDir, null);
            paths.GetProperty("ConfigPath", All).SetValue(null, Path.Combine(fixtureDir, "config"), null);
            paths.GetProperty("BepInExConfigPath", All).SetValue(null, Path.Combine(fixtureDir, "BepInEx.cfg"), null);
            // The fixture bypasses plugin/game-dependent startup, not any method
            // under test. Original game assemblies are never altered.
            using (AssemblyDefinition assembly = AssemblyDefinition.ReadAssembly(args[0]))
            {
                foreach (string name in new[] { "FeedLikeGrandma.FeedLikeGrandmaPlugin", "FeedLikeGrandma.TamingOrbSystem" })
                {
                    TypeDefinition type = assembly.MainModule.Types.Single(t => t.FullName == name);
                    MethodDefinition initializer = type.Methods.Single(m => m.IsConstructor && m.IsStatic);
                    initializer.Body.Instructions.Clear(); initializer.Body.ExceptionHandlers.Clear(); initializer.Body.Variables.Clear();
                    initializer.Body.GetILProcessor().Append(Mono.Cecil.Cil.Instruction.Create(Mono.Cecil.Cil.OpCodes.Ret));
                }
                TypeDefinition controller = assembly.MainModule.Types.Single(t => t.FullName == "FeedLikeGrandma.OrbDiagnostics.OrbDiagnosticsController");
                TypeDefinition snapshot = assembly.MainModule.Types.Single(t => t.FullName == "FeedLikeGrandma.OrbDiagnostics.InventorySnapshot");
                foreach (string name in new[] { "Snapshot", "Notice" })
                {
                    MethodDefinition method = controller.Methods.Single(m => m.Name == name);
                    method.Body.Instructions.Clear(); method.Body.ExceptionHandlers.Clear(); method.Body.Variables.Clear();
                    if (name == "Snapshot") method.Body.GetILProcessor().Append(Mono.Cecil.Cil.Instruction.Create(Mono.Cecil.Cil.OpCodes.Newobj, snapshot.Methods.Single(m => m.IsConstructor && !m.IsStatic)));
                    method.Body.GetILProcessor().Append(Mono.Cecil.Cil.Instruction.Create(Mono.Cecil.Cil.OpCodes.Ret));
                }
                // Fixture items have no native prefab. Replace only Unity's null
                // comparison at that boundary, preserving all candidate rules.
                var nullComparison = new MethodDefinition("__FixtureReferenceInequality", Mono.Cecil.MethodAttributes.Private | Mono.Cecil.MethodAttributes.Static,
                    assembly.MainModule.TypeSystem.Boolean);
                nullComparison.Parameters.Add(new ParameterDefinition(assembly.MainModule.TypeSystem.Object));
                nullComparison.Parameters.Add(new ParameterDefinition(assembly.MainModule.TypeSystem.Object));
                controller.Methods.Add(nullComparison);
                var nullIl = nullComparison.Body.GetILProcessor();
                foreach (var opcode in new[] { Mono.Cecil.Cil.OpCodes.Ldarg_0, Mono.Cecil.Cil.OpCodes.Ldarg_1, Mono.Cecil.Cil.OpCodes.Ceq,
                    Mono.Cecil.Cil.OpCodes.Ldc_I4_0, Mono.Cecil.Cil.OpCodes.Ceq, Mono.Cecil.Cil.OpCodes.Ret })
                    nullIl.Append(Mono.Cecil.Cil.Instruction.Create(opcode));
                foreach (var instruction in controller.Methods.Single(m => m.Name == "IsCandidate").Body.Instructions)
                    if (instruction.Operand is MethodReference call && call.DeclaringType.FullName == "UnityEngine.Object" && call.Name == "op_Inequality") instruction.Operand = nullComparison;
                // Supply a ready synthetic local-world/clock context at the exact
                // native-facing boundary. Trigger filtering, automatic-start
                // decisions and event observation remain the production methods.
                TypeDefinition trace = assembly.MainModule.Types.Single(t => t.FullName == "FeedLikeGrandma.OrbDiagnostics.PassiveTrace");
                FieldReference localPlayer = trace.Methods.Where(m => m.HasBody).SelectMany(m => m.Body.Instructions)
                    .Select(i => i.Operand as FieldReference).First(f => f != null && f.DeclaringType.FullName == "Player" && f.Name == "m_localPlayer");
                var suppliedPlayer = new FieldDefinition("__FixtureLocalPlayer", Mono.Cecil.FieldAttributes.Public | Mono.Cecil.FieldAttributes.Static, localPlayer.FieldType);
                trace.Fields.Add(suppliedPlayer);
                foreach (MethodDefinition method in CecilDescendants(trace).SelectMany(t => t.Methods).Where(m => m.HasBody))
                    foreach (var instruction in method.Body.Instructions)
                        if (instruction.Operand is FieldReference field && field.DeclaringType.FullName == "Player" && field.Name == "m_localPlayer") instruction.Operand = suppliedPlayer;
                MethodDefinition prepare = trace.Methods.Single(m => m.Name == "Prepare");
                prepare.Body.Instructions.Clear(); prepare.Body.ExceptionHandlers.Clear(); prepare.Body.Variables.Clear();
                var prepareIl = prepare.Body.GetILProcessor();
                prepareIl.Append(Mono.Cecil.Cil.Instruction.Create(Mono.Cecil.Cil.OpCodes.Ldsfld, controller.Fields.Single(f => f.Name == "Instance")));
                prepareIl.Append(Mono.Cecil.Cil.Instruction.Create(Mono.Cecil.Cil.OpCodes.Ldarg_0));
                prepareIl.Append(Mono.Cecil.Cil.Instruction.Create(Mono.Cecil.Cil.OpCodes.Ldc_I8, 123L));
                prepareIl.Append(Mono.Cecil.Cil.Instruction.Create(Mono.Cecil.Cil.OpCodes.Ldc_R4, 1000f));
                prepareIl.Append(Mono.Cecil.Cil.Instruction.Create(Mono.Cecil.Cil.OpCodes.Callvirt, controller.Methods.Single(m => m.Name == "PrepareObservation")));
                prepareIl.Append(Mono.Cecil.Cil.Instruction.Create(Mono.Cecil.Cil.OpCodes.Ret));
                assembly.Write(fixturePath);
            }
            return Run(new[] { fixturePath, args[1], args[2], "lifecycle" });
        }
        finally { foreach (string path in Directory.GetFiles(fixtureDir)) File.Delete(path); Directory.Delete(fixtureDir, false); }
    }

    private static IEnumerable<TypeDefinition> CecilDescendants(TypeDefinition type)
    {
        yield return type;
        foreach (TypeDefinition child in type.NestedTypes)
            foreach (TypeDefinition nested in CecilDescendants(child)) yield return nested;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int Run(string[] args)
    {
        Assembly diagnostic = Assembly.LoadFrom(args[0]);
        _hooks = diagnostic.GetType("FeedLikeGrandma.OrbDiagnostics.PassiveTrace", true);
        Type plugin = diagnostic.GetType("FeedLikeGrandma.OrbDiagnostics.OrbDiagnosticsController", true);
        Assert(plugin.GetField("Instance", BindingFlags.Static | BindingFlags.NonPublic).GetValue(null) == null, "no Unity/plugin instance initialized");
        Assembly game = Assembly.LoadFrom(Path.Combine(args[1], "assembly_valheim.dll"));
        VerifyContracts(diagnostic, game);
        VerifyPacketIsolation(plugin, game.GetType("ZPackage", true));
        if (args.Length == 4) { VerifyControllerLifecycle(plugin, diagnostic); VerifyAutomaticCollection(plugin, diagnostic); VerifyServerReport(diagnostic); }
        var own = new Harmony(OwnId);
        var other = new Harmony(OtherId);
        try
        {
            MethodInfo staticBool = Method("StaticBool");
            Attach(own, staticBool, true);
            var untouched = new object();
            object[] payload = { untouched, "unchanged" };
            Assert(StaticBool(true, payload), "true result preserved");
            Assert(!StaticBool(false, payload), "false result preserved");
            Assert(payload.Length == 2 && ReferenceEquals(payload[0], untouched) && (string)payload[1] == "unchanged", "arguments untouched");

            MethodInfo instanceBool = typeof(Synthetic).GetMethod("InstanceBool");
            Attach(own, instanceBool, true);
            var instance = new Synthetic();
            Assert(instance.InstanceBool(17), "instance result true preserved");
            Assert(!instance.InstanceBool(19), "instance result false preserved");
            Assert(instance.Last == 19, "instance original executed");

            Attach(own, Method("StaticVoid"), false);
            int before = _originalCalls;
            StaticVoid();
            Assert(_originalCalls == before + 1, "void original executed exactly once");
            Attach(own, Method("StaticThrows"), false);
            ExpectOriginalError(StaticThrows, "void finalizer preserves original exception");

            MethodInfo vetoed = Method("VetoedBool");
            other.Patch(vetoed, new HarmonyMethod(Method("OtherVeto")) { priority = Priority.First + 100 });
            Attach(own, vetoed, true);
            before = _originalCalls;
            Assert(!VetoedBool(), "earlier veto result preserved");
            Assert(_originalCalls == before && _otherCalls == 1, "earlier veto keeps original unexecuted");

            MethodInfo outgoing = Method("SyntheticOutgoing");
            own.Patch(outgoing, Hook("OutgoingPrefix", Priority.First), Hook("OutgoingPostfix", Priority.Last), finalizer: Hook("OutgoingFinalizer", Priority.Last));
            before = _originalCalls;
            SyntheticOutgoing(123, "FLG_OrbSynthetic_v3", payload);
            Assert(_originalCalls == before + 1 && ReferenceEquals(payload[0], untouched), "typed outgoing callbacks preserve arguments and execution");
            ExpectOriginalError(() => SyntheticOutgoing(123, "FLG_OrbThrow_v3", payload), "typed outgoing finalizer preserves exception");

            own.UnpatchSelf();
            Assert(Harmony.GetPatchInfo(vetoed).Prefixes.Any(p => p.owner == OtherId), "unrelated owner patch preserved after own cleanup");
            Assert(!Harmony.GetPatchInfo(vetoed).Owners.Contains(OwnId), "own observer removed");
            before = _originalCalls;
            Assert(!VetoedBool() && _originalCalls == before && _otherCalls == 2, "unrelated veto still works after own cleanup");
            other.UnpatchSelf();
            Assert(VetoedBool(), "original resumes after synthetic veto cleanup");
            Console.WriteLine("PASS: " + _checks + (args.Length == 4
                ? " integrated diagnostic fixture checks: passive callbacks, API/packet isolation, controller lifecycle and server parser/Off checks."
                : " original-DLL diagnostic checks: passive callbacks, original API contracts and packet isolation."));
            Console.WriteLine(args.Length == 4
                ? "Lifecycle fixture: main-plugin/TamingOrbSystem static startup suppressed in a temporary mod copy; controller/observer fields supplied; Snapshot/Notice, local-player/world/clock reads and null-prefab Unity comparison substituted. Production automatic-start, candidate/actor filtering, lifecycle and file paths run against synthetic managed Harmony targets. No original game changes, actual game-method hook installation, real inventory/native prefab identity, BepInEx startup, Unity/world or multiplayer execution."
                : "Original unmodified final DLL: synthetic passive callbacks and original managed packet reads only; no Unity lifecycle, inventory or multiplayer execution.");
            return 0;
        }
        finally { own.UnpatchSelf(); other.UnpatchSelf(); }
    }

    private static void VerifyControllerLifecycle(Type controller, Assembly flg)
    {
        Assert(controller.BaseType == typeof(object), "integrated controller has no second Unity/plugin lifecycle");
        Type logType = Assembly.Load("BepInEx").GetType("BepInEx.Logging.ManualLogSource", true);
        flg.GetType("FeedLikeGrandma.FeedLikeGrandmaPlugin", true).GetField("Log", All).SetValue(null, Activator.CreateInstance(logType, new object[] { "isolated diagnostic fixture" }));
        object instance = Activator.CreateInstance(controller, true);
        MethodInfo setEnabled = controller.GetMethod("SetEnabled", All), shutdown = controller.GetMethod("Shutdown", All);
        FieldInfo enabled = controller.GetField("_enabled", All), initialized = controller.GetField("_initialized", All), singleton = controller.GetField("Instance", All);
        FieldInfo observerField = controller.GetField("_observer", All), recording = controller.GetField("_recording", All);
        Assert(!(bool)enabled.GetValue(instance), "client diagnostics default to Off");
        setEnabled.Invoke(instance, new object[] { true });
        Assert(!(bool)enabled.GetValue(instance) && observerField.GetValue(instance) == null, "uninitialized controller never starts observation");

        string output = Path.Combine(Path.GetTempPath(), "flg-integrated-diagnostic-check-" + Guid.NewGuid().ToString("N"));
        var external = new Harmony("flg.diagnostics.lifecycle.external");
        var builtIn = new Harmony((string)controller.GetField("HarmonyId", All).GetRawConstantValue());
        try
        {
            // The actual disable/save/dispose code runs. Only native-facing snapshot
            // and logging/HUD access are substituted; no game API or DLL is modified.
            controller.GetMethod("Initialize", All).Invoke(instance, null);
            string configPath = (string)Assembly.Load("BepInEx").GetType("BepInEx.Paths", true).GetProperty("ConfigPath", All).GetValue(null, null);
            string initialReportPath = (string)controller.GetField("_reportDirectory", All).GetValue(instance);
            Assert(initialReportPath == Path.Combine(configPath, "FeedLikeGrandma", "diagnostics"), "Initialize uses the current profile ConfigPath for reports");
            Assert((bool)initialized.GetValue(instance) && ReferenceEquals(singleton.GetValue(null), instance) && !Directory.Exists(initialReportPath), "Initialize registers the controller without creating a report directory");
            controller.GetField("_reportDirectory", All).SetValue(instance, output);
            singleton.SetValue(null, instance);
            setEnabled.Invoke(instance, new object[] { true });
            Assert((bool)enabled.GetValue(instance), "On enables client diagnostics");
            Assert(observerField.GetValue(instance) == null && recording.GetValue(instance) == null, "On alone does not start a trace or install observer");
            setEnabled.Invoke(instance, new object[] { true });
            Assert(observerField.GetValue(instance) == null, "repeated On does not allocate observers");

            object observer = Activator.CreateInstance(_hooks, true);
            observerField.SetValue(instance, observer);
            MethodInfo target = Method("LifecycleTarget");
            Attach(builtIn, target, true);
            external.Patch(target, prefix: new HarmonyMethod(Method("OtherVeto")));
            Type reportType = flg.GetType("FeedLikeGrandma.OrbDiagnostics.DiagnosticReport", true);
            object report = Activator.CreateInstance(reportType, true);
            reportType.GetField("Kind").SetValue(report, "trace");
            recording.SetValue(instance, report);
            setEnabled.Invoke(instance, new object[] { false });
            Assert(!(bool)enabled.GetValue(instance) && recording.GetValue(instance) == null, "Off stops active trace before returning");
            Assert(observerField.GetValue(instance) == null, "Off disposes and releases observer");
            Assert(controller.GetField("_unsaved", All).GetValue(instance) == null, "successful Off save clears pending report");
            Assert(Directory.GetFiles(output, "*.json").Length == 1 && Directory.GetFiles(output, "*.txt").Length == 1, "Off saves both report formats");
            string rendered = File.ReadAllText(Directory.GetFiles(output, "*.txt")[0]);
            Assert(rendered.Contains("OrbDiagnostics switched Off"), "saved report records disable reason");
            Patches info = Harmony.GetPatchInfo(target);
            Assert(info != null && info.Owners.Contains(external.Id) && !info.Owners.Contains(builtIn.Id), "disable removes only diagnostic-owned hooks");
            setEnabled.Invoke(instance, new object[] { false });
            Assert(ReportFiles(output).Length == 2, "repeated Off neither records nor writes another report");
            setEnabled.Invoke(instance, new object[] { true });
            Assert((bool)enabled.GetValue(instance) && observerField.GetValue(instance) == null, "On after Off starts clean without old observer");

            // Real file failure (an existing file used as a directory) must keep the
            // report retriable while still stopping and disposing observation.
            object retry = Activator.CreateInstance(reportType, true);
            reportType.GetField("Kind").SetValue(retry, "trace");
            recording.SetValue(instance, retry);
            controller.GetField("_reportDirectory", All).SetValue(instance, Directory.GetFiles(output, "*.json")[0]);
            setEnabled.Invoke(instance, new object[] { false });
            Assert(ReferenceEquals(controller.GetField("_unsaved", All).GetValue(instance), retry), "failed Off save retains exact report in memory");
            Assert(recording.GetValue(instance) == null && observerField.GetValue(instance) == null && !(bool)enabled.GetValue(instance), "write failure still stops recording and releases observer");
            controller.GetField("_reportDirectory", All).SetValue(instance, output);
            setEnabled.Invoke(instance, new object[] { true });
            Assert(controller.GetField("_unsaved", All).GetValue(instance) == null && ReportFiles(output).Length == 4, "Off then On retries retained report once without starting another trace");
            shutdown.Invoke(instance, null);
            shutdown.Invoke(instance, null);
            Assert(!(bool)enabled.GetValue(instance) && singleton.GetValue(null) == null, "shutdown is idempotent and clears controller singleton");
            setEnabled.Invoke(instance, new object[] { true });
            Assert(!(bool)enabled.GetValue(instance), "shutdown controller cannot be re-enabled");

            // Only the isolated BepInEx registry is changed; standalone hooks are
            // represented by a pre-existing external synthetic patch above.
            var registry = (System.Collections.IDictionary)Assembly.Load("BepInEx").GetType("BepInEx.Bootstrap.Chainloader", true).GetProperty("PluginInfos", All).GetValue(null, null);
            string legacy = (string)controller.GetField("LegacyPluginGuid", All).GetRawConstantValue();
            object legacyController = Activator.CreateInstance(controller, true);
            initialized.SetValue(legacyController, true);
            registry.Add(legacy, null);
            try
            {
                setEnabled.Invoke(legacyController, new object[] { true });
                Assert(!(bool)enabled.GetValue(legacyController) && observerField.GetValue(legacyController) == null, "installed standalone GUID prevents built-in client observation");
                Assert(registry.Contains(legacy) && Harmony.GetPatchInfo(target).Owners.Contains(external.Id), "legacy detection preserves other registry entries and patches");
                setEnabled.Invoke(legacyController, new object[] { true });
                Assert((bool)controller.GetField("_legacyWarning", All).GetValue(legacyController), "legacy conflict notice is latched");
            }
            finally { registry.Remove(legacy); shutdown.Invoke(legacyController, null); }
        }
        finally
        {
            singleton.SetValue(null, null);
            builtIn.UnpatchSelf(); external.UnpatchSelf();
            if (Directory.Exists(output))
            {
                foreach (string path in Directory.GetFiles(output)) File.Delete(path);
                Directory.Delete(output, false);
            }
        }
    }

    private static string[] ReportFiles(string path) => Directory.Exists(path)
        ? Directory.GetFiles(path).Where(file => file.EndsWith(".json") || file.EndsWith(".txt")).ToArray() : new string[0];

    private static void VerifyAutomaticCollection(Type controller, Assembly flg)
    {
        object instance = Activator.CreateInstance(controller, true);
        MethodInfo set = controller.GetMethod("SetEnabled", All), update = controller.GetMethod("UpdateSession", All);
        MethodInfo prepare = controller.GetMethod("PrepareObservation", All);
        FieldInfo observer = controller.GetField("_observer", All), world = controller.GetField("_observerWorld", All);
        FieldInfo recording = controller.GetField("_recording", All), unsaved = controller.GetField("_unsaved", All);
        FieldInfo singleton = controller.GetField("Instance", All), outputField = controller.GetField("_reportDirectory", All);
        FieldInfo transitioning = controller.GetField("_transitioning", All);
        Type reportType = flg.GetType("FeedLikeGrandma.OrbDiagnostics.DiagnosticReport", true);
        Type itemType = Assembly.Load("assembly_valheim").GetType("ItemDrop+ItemData", true);
        object orb = Activator.CreateInstance(itemType, true), ordinary = Activator.CreateInstance(itemType, true);
        ((System.Collections.IDictionary)itemType.GetField("m_customData", All).GetValue(orb))["flg.tame.orbId"] = new string('b', 32);
        Func<object, System.Collections.IList> events = report => (System.Collections.IList)reportType.GetField("Events").GetValue(report);
        Func<object, System.Collections.IList> snapshots = report => (System.Collections.IList)reportType.GetField("Snapshots").GetValue(report);
        string output = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "flg-auto-diagnostic-check-" + Guid.NewGuid().ToString("N")));
        Assert(output.StartsWith(Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase),
            "automatic fixture output remains inside its owned temporary directory");
        var own = new Harmony((string)controller.GetField("HarmonyId", All).GetRawConstantValue());
        MethodInfo target = Method("LifecycleOrbUse");
        Action arm = () => {
            object source = Activator.CreateInstance(_hooks, true);
            observer.SetValue(instance, source); world.SetValue(instance, 123L);
            _hooks.GetField("_installHooks", All).SetValue(source, true);
            Attach(own, target, true);
            own.Patch(Method("SyntheticOutgoing"), Hook("OutgoingPrefix", Priority.First), Hook("OutgoingPostfix", Priority.Last), finalizer: Hook("OutgoingFinalizer", Priority.Last));
        };
        try
        {
            controller.GetField("_initialized", All).SetValue(instance, true); outputField.SetValue(instance, output); singleton.SetValue(null, instance);
            set.Invoke(instance, new object[] { true });
            update.Invoke(instance, new object[] { false, 0L, 10000f });
            Assert(observer.GetValue(instance) == null && recording.GetValue(instance) == null && !Directory.Exists(output),
                "On in menu/disconnected waits beyond 60 seconds without snapshots, observer or files");
            arm();
            object armed = observer.GetValue(instance);
            object humanoidTarget = FormatterServices.GetUninitializedObject(Assembly.Load("assembly_valheim").GetType("Humanoid", true));
            MethodInfo relevant = _hooks.GetMethod("Relevant", All);
            Type orbSystem = flg.GetType("FeedLikeGrandma.TamingOrbSystem", true);
            Assert((bool)relevant.Invoke(null, new object[] { orbSystem.GetMethod("TryCapture", All), null, new[] { null, humanoidTarget, orb } }),
                "a Humanoid capture target is not mistaken for the user actor");
            Assert(!(bool)relevant.Invoke(null, new object[] { orbSystem.GetMethod("TryHandleTameableUseItem", All), null, new[] { null, humanoidTarget, orb } }),
                "tameable handler rejects a nonlocal Humanoid user without rejecting Humanoid targets elsewhere");
            MethodInfo tameableUse = Assembly.Load("assembly_valheim").GetType("Tameable", true).GetMethod("UseItem", All);
            Assert(!(bool)relevant.Invoke(null, new object[] { tameableUse, null, new[] { humanoidTarget, orb } }),
                "Tameable.UseItem uses its actor argument for local-user filtering");
            update.Invoke(instance, new object[] { true, 123L, 10000f });
            Assert(recording.GetValue(instance) == null && ReferenceEquals(observer.GetValue(instance), armed) && !Directory.Exists(output),
                "long armed idle does not expire, write or recreate hooks");
            Assert(LifecycleOrbUse(ordinary) && recording.GetValue(instance) == null, "unrelated item is ignored without changing original result");
            SyntheticOutgoing(9, "Unrelated_RPC", new object[0]);
            Assert(recording.GetValue(instance) == null, "unrelated RPC does not start a report");
            Assert(!(bool)prepare.Invoke(instance, new object[] { false, 123L, 1000f }), "return-only event does not invent a new trace");
            Assert(!(bool)prepare.Invoke(instance, new object[] { true, 0L, 1000f }) && !(bool)prepare.Invoke(instance, new object[] { true, 999L, 1000f }),
                "zero or different-world context cannot start a trace");
            transitioning.SetValue(instance, true);
            Assert(!(bool)prepare.Invoke(instance, new object[] { true, 123L, 1000f }) && recording.GetValue(instance) == null,
                "nested start during snapshot/save transition is rejected");
            transitioning.SetValue(instance, false);
            Assert(LifecycleOrbUse(orb), "first automatic Orb event preserves original result");
            object first = recording.GetValue(instance);
            Assert(first != null && events(first).Count == 2, "first automatic Orb prefix and return both appear in trace");
            Assert((string)events(first)[0].GetType().GetField("Phase").GetValue(events(first)[0]) == "enter", "trigger is the first collected event");
            var firstFacts = (Dictionary<string, string>)events(first)[0].GetType().GetField("Facts").GetValue(events(first)[0]);
            Assert(!firstFacts.ContainsKey("diagnosticReadError") && firstFacts["item.flg.tame.orbId.present"] == "True", "first automatic event contains parsed item evidence without a hidden fixture/native read error");
            Assert(((System.Collections.IDictionary)itemType.GetField("m_customData", All).GetValue(orb)).Count == 1 &&
                (string)((System.Collections.IDictionary)itemType.GetField("m_customData", All).GetValue(orb))["flg.tame.orbId"] == new string('b', 32),
                "automatic collection preserves original item custom data");
            Assert(snapshots(first).Count == 1 && ReportFiles(output).Length == 0, "automatic start snapshots before but does not write prematurely");
            Assert((float)controller.GetField("_deadline", All).GetValue(instance) == 1060f, "60-second trace deadline starts at trigger, not enable time");
            Assert(ReferenceEquals(observer.GetValue(instance), armed) && Harmony.GetPatchInfo(target).Owners.Contains(own.Id), "trigger keeps existing hooks rather than repatching");
            transitioning.SetValue(instance, true);
            LifecycleOrbUse(orb);
            Assert(events(first).Count == 2 && ReferenceEquals(first, recording.GetValue(instance)), "transition guard suppresses recursive collection");
            transitioning.SetValue(instance, false);
            update.Invoke(instance, new object[] { true, 123L, 1059.99f });
            Assert(ReferenceEquals(recording.GetValue(instance), first), "trace continues before deadline");
            update.Invoke(instance, new object[] { true, 123L, 1060f });
            Assert(recording.GetValue(instance) == null && unsaved.GetValue(instance) == null && ReportFiles(output).Length == 2, "exactly 60 seconds saves complete report pair");
            Assert((bool)controller.GetField("_enabled", All).GetValue(instance) && ReferenceEquals(observer.GetValue(instance), armed) && Harmony.GetPatchInfo(target).Owners.Contains(own.Id),
                "expiry leaves On and existing hooks armed");
            Assert(snapshots(first).Count == 2 && (string)reportType.GetField("StopReason").GetValue(first) == "60-second limit", "expiry records after snapshot and stop reason");
            SyntheticOutgoing(9, "FLG_OrbSynthetic_v3", new object[0]);
            object second = recording.GetValue(instance);
            Assert(second != null && !ReferenceEquals(first, second) && events(second).Count == 2, "next Orb RPC starts a new trace containing its invocation and return");
            update.Invoke(instance, new object[] { false, 0L, 1100f });
            Assert(recording.GetValue(instance) == null && observer.GetValue(instance) == null && ReportFiles(output).Length == 4, "disconnect saves and removes observer");
            Assert((bool)controller.GetField("_enabled", All).GetValue(instance) && !Harmony.GetPatchInfo(target).Owners.Contains(own.Id), "disconnect retains On for next session but removes its hooks");

            // Force world-change save failure to return before real game-method
            // hook installation for the next world. This tests old-world evidence
            // preservation without claiming native player/startup coverage.
            arm(); prepare.Invoke(instance, new object[] { true, 123L, 2000f });
            object changed = recording.GetValue(instance);
            outputField.SetValue(instance, Directory.GetFiles(output, "*.json")[0]);
            Assert(!(bool)prepare.Invoke(instance, new object[] { true, 456L, 2001f }), "new-world callback cannot enter prior report before update");
            update.Invoke(instance, new object[] { true, 456L, 2001f });
            Assert(ReferenceEquals(unsaved.GetValue(instance), changed) && observer.GetValue(instance) == null && recording.GetValue(instance) == null,
                "world-change write failure retains exact report and releases observer");
            Assert(snapshots(changed).Count == 1 && ((System.Collections.IList)reportType.GetField("Coverage").GetValue(changed)).Cast<string>().Any(value => value.Contains("different world")),
                "world switch never labels new-world inventory as previous world's after snapshot");
            outputField.SetValue(instance, output);
            update.Invoke(instance, new object[] { true, 456L, 9999f }); set.Invoke(instance, new object[] { true });
            Assert(ReferenceEquals(unsaved.GetValue(instance), changed) && ReportFiles(output).Length == 4, "frames/repeated On never retry or replace failed evidence");
            Assert(!(bool)prepare.Invoke(instance, new object[] { true, 123L, 9999f }), "pending report blocks new collection");
            set.Invoke(instance, new object[] { false });
            Assert(ReferenceEquals(unsaved.GetValue(instance), changed) && ReportFiles(output).Length == 4, "Off alone does not silently retry pending evidence");
            set.Invoke(instance, new object[] { true });
            Assert(unsaved.GetValue(instance) == null && recording.GetValue(instance) == null && ReportFiles(output).Length == 6, "Off then On retries once without another trace");
            set.Invoke(instance, new object[] { true });
            Assert(ReportFiles(output).Length == 6, "repeated On after successful retry cannot duplicate saved report");
            controller.GetMethod("Shutdown", All).Invoke(instance, null);
        }
        finally
        {
            singleton.SetValue(null, null); own.UnpatchSelf();
            if (Directory.Exists(output)) { foreach (string path in Directory.GetFiles(output)) File.Delete(path); Directory.Delete(output, false); }
        }
    }

    private static void VerifyServerReport(Assembly flg)
    {
        Type report = flg.GetType("FeedLikeGrandma.TamingOrbSystem+OrbServerReport", true);
        FieldInfo enabled = report.GetField("_enabled", All), last = report.GetField("_lastReport", All);
        MethodInfo set = report.GetMethod("SetEnabled", All), observe = report.GetMethod("Observe", All);
        Assert(!(bool)enabled.GetValue(null), "server diagnostics default to Off");
        observe.Invoke(null, new object[] { null, null, false });
        Assert(float.IsNegativeInfinity((float)last.GetValue(null)), "Off server report returns before request/world access or throttle mutation");
        set.Invoke(null, new object[] { true });
        Assert((bool)enabled.GetValue(null), "server detail can be enabled independently");
        last.SetValue(null, 42f);
        set.Invoke(null, new object[] { true });
        Assert((float)last.GetValue(null) == 42f, "repeated On preserves global scan throttle");
        set.Invoke(null, new object[] { false });
        Assert(!(bool)enabled.GetValue(null) && float.IsNegativeInfinity((float)last.GetValue(null)), "Off clears detail throttle");
        observe.Invoke(null, new object[] { null, null, true });
        Assert(float.IsNegativeInfinity((float)last.GetValue(null)), "Off does not inspect even a purported successful outcome");

        string tx = new string('a', 32), orb = new string('b', 32);
        string request = Encode(writer => {
            writer.Write(tx); writer.Write(55L); writer.Write((uint)44); writer.Write(orb);
            writer.Write("payload unchanged"); writer.Write("modifiers unchanged");
            writer.Write(1f); writer.Write(2f); writer.Write(3f);
            writer.Write(0f); writer.Write(0f); writer.Write(0f); writer.Write(1f);
        });
        object parsed;
        Assert(Parse(report, "TryReadRequest", request, out parsed), "server report parses copied original request bytes");
        Assert((string)parsed.GetType().GetProperty("TransactionId").GetValue(parsed) == tx, "request parser preserves transaction identity");
        foreach (string bad in new[] { "!bad!", Convert.ToBase64String(new byte[] { 1 }), Convert.ToBase64String(Convert.FromBase64String(request).Concat(new byte[] { 0 }).ToArray()) })
            Assert(!Parse(report, "TryReadRequest", bad, out parsed) && parsed == null, "malformed/trailing request remains uninspected");
        string result = Encode(writer => { writer.Write(tx); writer.Write(orb); writer.Write(true); writer.Write(77L); writer.Write((uint)88); writer.Write(""); });
        Assert(Parse(report, "TryReadResult", result, out parsed), "server report parses copied original result bytes");
        Assert(!Parse(report, "TryReadResult", result.Substring(0, result.Length - 4), out parsed) && parsed == null, "truncated result is rejected without exposing partial object");
        foreach (int version in new[] { 3, 4 })
            Assert(Parse(report, "TryReadPrefabHeader", Encode(w => { w.Write(version); w.Write("Wolf"); }), out parsed) && (int)parsed == "Wolf".GetStableHashCode(), "supported header inspected independently: " + version);
        Assert(!Parse(report, "TryReadPrefabHeader", Encode(w => { w.Write(5); w.Write("Wolf"); }), out parsed), "unknown payload version is not misclassified");
        Assert((string)report.GetMethod("GuidText", All).Invoke(null, new object[] { "bad\nidentifier" }) == "invalid", "identifier cannot inject report lines");
        Assert((string)report.GetMethod("SafeLabel", All).Invoke(null, new object[] { "bad\nlabel" }) == "invalid", "state label cannot inject report lines");
    }

    private static bool Parse(Type report, string name, string input, out object result)
    {
        object[] args = { input, null };
        bool success = (bool)report.GetMethod(name, All).Invoke(null, args);
        result = args[1]; return success;
    }
    private static string Encode(Action<BinaryWriter> write)
    {
        using (var bytes = new MemoryStream())
        {
            using (var writer = new BinaryWriter(bytes, System.Text.Encoding.UTF8, true)) write(writer);
            return Convert.ToBase64String(bytes.ToArray());
        }
    }

    private static void VerifyPacketIsolation(Type plugin, Type packageType)
    {
        // Constructor-free managed shell solely for ReadPackage's private byte budget.
        // It is never assigned to Instance and no BepInEx/Unity lifecycle is called.
        object shell;
        try { shell = FormatterServices.GetUninitializedObject(plugin); }
        catch (Exception error)
        {
            Console.WriteLine("SKIP packet isolation: original runtime cannot allocate a constructor-free diagnostic shell: " + error.GetType().Name);
            return;
        }
        FieldInfo budget = plugin.GetField("_packetBudget", BindingFlags.Instance | BindingFlags.NonPublic);
        budget.SetValue(shell, 2 * 1024 * 1024);
        MethodInfo read = plugin.GetMethod("ReadPackage", BindingFlags.Instance | BindingFlags.NonPublic);
        MethodInfo getBytes = packageType.GetMethod("GetArray"), getPos = packageType.GetMethod("GetPos"), setPos = packageType.GetMethod("SetPos");
        byte[] good;
        using (var stream = new MemoryStream())
        {
            using (var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, true))
            {
                writer.Write(1234L);
                writer.Write(new string('a', 32));
                writer.Write(new string('b', 32));
                writer.Write("unknown");
                writer.Write("");
            }
            good = stream.ToArray();
        }
        byte[][] packets = { good, new byte[] { 0x01, 0x02 }, new byte[256 * 1024 + 1], good };
        string[] labels = { "valid recovery", "malformed recovery", "oversize packet", "nonzero incoming cursor" };
        for (int i = 0; i < packets.Length; ++i)
        {
            byte[] bytes = packets[i];
            object package = Activator.CreateInstance(packageType, new object[] { bytes });
            int position = i >= 2 ? 3 : 0;
            setPos.Invoke(package, new object[] { position });
            var facts = new Dictionary<string, string>();
            int priorBudget = (int)budget.GetValue(shell);
            read.Invoke(shell, new object[] { package, "ReceiveOrbRecoveryOnClient", facts, "pkg." });
            Assert((int)getPos.Invoke(package, null) == position, labels[i] + " preserves original read cursor");
            Assert(((byte[])getBytes.Invoke(package, null)).SequenceEqual(bytes), labels[i] + " preserves original bytes");
            if (i == 0)
            {
                Assert(facts["pkg.recovery.status"] == "parsed" && facts["pkg.recovery.state"] == "unknown", "valid recovery is decoded from its independent copy");
                Assert(Convert.FromBase64String(facts["pkg.rawBase64"]).SequenceEqual(bytes), "exported packet equals source bytes");
            }
            else if (i == 1) Assert(facts["pkg.recovery.status"] == "malformed", "malformed recovery is reported without consuming source packet");
            else if (i == 2)
            {
                Assert(facts.ContainsKey("pkg.capture") && !facts.ContainsKey("pkg.rawBase64"), "oversize packet is marked uninspected without copying payload");
                Assert((int)budget.GetValue(shell) == priorBudget, "oversize packet does not consume copy budget");
            }
            else
            {
                Assert(facts["pkg.recovery.status"] == "uninspected: incoming read position is not zero" && !facts.ContainsKey("pkg.recovery.state"), "nonzero incoming cursor avoids falsely parsing from byte zero");
                Assert(Convert.FromBase64String(facts["pkg.rawBase64"]).SequenceEqual(bytes), "nonzero cursor still preserves raw packet evidence");
            }
        }
        Assert(plugin.GetField("Instance", BindingFlags.Static | BindingFlags.NonPublic).GetValue(null) == null, "packet checks never activate a diagnostic plugin instance");
    }

    private static void VerifyContracts(Assembly flg, Assembly game)
    {
        Type orb = flg.GetType("FeedLikeGrandma.TamingOrbSystem", true);
        Type player = game.GetType("Player", true), humanoid = game.GetType("Humanoid", true), tameable = game.GetType("Tameable", true);
        Type item = game.GetType("ItemDrop+ItemData", true), zdo = game.GetType("ZDOID", true), package = game.GetType("ZPackage", true);
        Type unity = Assembly.Load("UnityEngine.CoreModule").GetType("UnityEngine.GameObject", true);
        Assembly engine = unity.Assembly;
        Contract(orb, "TryHandleTameableUseItem", true, typeof(bool), tameable, humanoid, item);
        foreach (string name in new[] { "TryHandlePlayerUseItem", "TryHandlePendingOrb", "TryRelease" }) Contract(orb, name, true, typeof(bool), player, item);
        Contract(orb, "TryCapture", true, typeof(bool), player, game.GetType("Character", true), item);
        Contract(orb, "BeginNetworkCapture", true, typeof(bool), player, item, zdo, typeof(string), typeof(float));
        Contract(orb, "BeginNetworkRelease", true, typeof(bool), player, item, Nested(orb, "CapturedTamePayload"), typeof(string), unity, engine.GetType("UnityEngine.Vector3", true), engine.GetType("UnityEngine.Quaternion", true), typeof(float));
        Contract(orb, "ResolveOrbItem", true, typeof(bool), typeof(string), typeof(string), typeof(bool), typeof(string));
        foreach (string name in new[] { "ReceiveCapturePreparedOnClient", "ReceiveCaptureResultOnClient", "ReceiveReleaseApprovalOnClient", "ReceiveReleaseResultOnClient", "ReceiveOrbRecoveryOnClient", "ReceiveCapturePrepareOnOwner", "ReceiveCaptureCommitOnOwner" })
            Contract(orb, name, true, typeof(void), typeof(long), package);
        Contract(orb, "FinishCaptureClientFailure", true, typeof(void), Nested(orb, "ClientCaptureTransaction"), typeof(string));
        Contract(orb, "FinishReleaseClient", true, typeof(void), Nested(orb, "ClientReleaseTransaction"), typeof(bool), typeof(string));
        Contract(flg.GetType("FeedLikeGrandma.FeedLocalization", true), "Message", true, typeof(void), player, typeof(string), typeof(object[]));
        Contract(player, "UseHotbarItem", false, typeof(void), typeof(int));
        Contract(humanoid, "UseItem", false, typeof(void), game.GetType("Inventory", true), item, typeof(bool));
        Contract(tameable, "UseItem", false, typeof(bool), humanoid, item);
        Type routed = game.GetType("ZRoutedRpc", true);
        Contract(routed, "InvokeRoutedRPC", false, typeof(void), typeof(long), typeof(string), typeof(object[]));
        MethodInfo serverPeer = Contract(routed, "GetServerPeerID", false, typeof(long));
        Assert(serverPeer.IsPrivate, "server peer getter requires explicit nonpublic access");
        // Creating the cached open delegate does not call the real method.
        Delegate.CreateDelegate(typeof(Func<,>).MakeGenericType(routed, typeof(long)), serverPeer);
        Assert(true, "private pure getter delegate creation supported by original Mono");
        foreach (string name in new[] { "Size", "GetPos" }) Assert(Contract(package, name, false, typeof(int)).IsPublic, name + " is public");
        Assert(Contract(package, "GetArray", false, typeof(byte[])).IsPublic, "GetArray is public");
        foreach (string name in new[] { "ClientCaptureTransactions", "ClientReleaseTransactions", "OrbRecoveryRequests" })
        {
            FieldInfo field = orb.GetField(name, BindingFlags.Static | BindingFlags.NonPublic);
            Assert(field != null && typeof(System.Collections.IDictionary).IsAssignableFrom(field.FieldType), "read-only runtime dictionary contract " + name);
        }
    }

    private static MethodInfo Contract(Type type, string name, bool isStatic, Type result, params Type[] args)
    {
        MethodInfo method = type.GetMethod(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly, null, args, null);
        Assert(method != null && method.IsStatic == isStatic && method.ReturnType == result && !method.ContainsGenericParameters, "contract " + type.Name + "." + name);
        return method;
    }
    private static Type Nested(Type type, string name) => type.GetNestedType(name, BindingFlags.Public | BindingFlags.NonPublic);
    private static MethodInfo Method(string name) => typeof(OrbDiagnosticRuntimeChecks).GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic);
    private static HarmonyMethod Hook(string name, int priority) => new HarmonyMethod(_hooks.GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic)) { priority = priority };
    private static void Attach(Harmony harmony, MethodInfo target, bool returnsBool) => harmony.Patch(target, Hook("Prefix", Priority.First), Hook(returnsBool ? "BoolPostfix" : "VoidPostfix", Priority.Last), finalizer: Hook("Finalizer", Priority.Last));
    private static void Assert(bool value, string name) { if (!value) throw new Exception("FAIL: " + name); ++_checks; }
    private static void ExpectOriginalError(Action action, string name)
    {
        try { action(); }
        catch (InvalidOperationException error) { Assert(ReferenceEquals(error, OriginalError), name); return; }
        throw new Exception("FAIL: suppressed original exception: " + name);
    }

    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.NoOptimization)]
    private static bool StaticBool(bool value, object[] payload) { ++_originalCalls; return value; }
    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.NoOptimization)]
    private static void StaticVoid() { ++_originalCalls; }
    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.NoOptimization)]
    private static void StaticThrows() { throw OriginalError; }
    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.NoOptimization)]
    private static bool VetoedBool() { ++_originalCalls; return true; }
    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.NoOptimization)]
    private static bool LifecycleTarget() { return true; }
    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.NoOptimization)]
    private static bool LifecycleOrbUse(object item) { return true; }
    private static bool OtherVeto(ref bool __result) { ++_otherCalls; __result = false; return false; }
    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.NoOptimization)]
    private static void SyntheticOutgoing(long peer, string name, object[] values) { if (name == "FLG_OrbThrow_v3") throw OriginalError; ++_originalCalls; }
    private sealed class Synthetic
    {
        internal int Last;
        [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.NoOptimization)]
        public bool InstanceBool(int value) { Last = value; return value == 17; }
    }
}
