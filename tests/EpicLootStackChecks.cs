using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Mono.Cecil;

// Actual mod methods and original game metadata. Only the FineDining plugin
// startup initializer is suppressed. No Unity/physical merge/expiry policy runs.
internal static class EpicLootStackChecks
{
    private const BindingFlags All = BindingFlags.Static | BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private const string DropId = "sighsorry.FeedLikeGrandma.FeedBarrelDrops", FlgId = "sighsorry.FeedLikeGrandma.EpicLootStacks";
    private const string EpicId = "randyknapp.mods.epicloot", FineId = "sighsorry.FineDining";
    private const string FineBridgeId = "sighsorry.FineDining.EpicLootStacks", FineEntryId = "sighsorry.FineDining.EpicLootStacks.Entry";
    private static Type _bridge, _protection, _fineBridge;
    private static MethodInfo _target, _filter, _epicPrefix, _fineTranspiler, _expiry, _entry, _clock;
    private static bool _withFlg, _withFine;
    private static int _passed, _detourCalls;

    internal static int Run(Assembly flg, string scenario, string directory, string selectedFineDll)
    {
        string[] dlls = Directory.GetFiles(directory, "*.dll", SearchOption.AllDirectories);
        AppDomain.CurrentDomain.AssemblyResolve += (_, request) =>
        {
            string name = new AssemblyName(request.Name).Name + ".dll";
            string path = dlls.FirstOrDefault(candidate => string.Equals(Path.GetFileName(candidate), name, StringComparison.OrdinalIgnoreCase));
            return path == null ? null : Assembly.LoadFrom(path);
        };
        _bridge = flg.GetType("FeedLikeGrandma.FeedEpicLootStackCompatibility", true);
        _protection = flg.GetType("FeedLikeGrandma.FeedBarrelDropProtection", true);
        _target = AccessTools.DeclaredMethod(typeof(ItemDrop), "AutoStackItems", Type.EmptyTypes);
        _filter = _protection.GetMethod("FilterStackCandidate", All);
        string epicPath = Path.Combine(directory, "RandyKnapp-EpicLoot", "EpicLoot.dll");
        Assembly epic = scenario == "identity" || scenario.StartsWith("changed-", StringComparison.Ordinal)
            ? EpicVariant(epicPath, scenario) : Assembly.LoadFrom(epicPath);
        Type magic = epic.GetType("EpicLoot.MagicItemData", true);
        _epicPrefix = epic.GetType("EpicLoot.MagicItemData+ItemDrop_AutoStackItems_Patch", true).GetMethod("Prefix", All);
        Require(_epicPrefix != null && _epicPrefix.ReturnType == typeof(bool), "actual EpicLoot replacement prefix found");
        Require(typeof(ItemDrop).GetField("m_nview", All).IsPrivate, "original game private access remains unchanged");
        _withFlg = scenario != "fine-alone";
        _withFine = !scenario.StartsWith("plain", StringComparison.Ordinal) && scenario != "detour";
        if (_withFine)
        {
            string finePath = File.Exists(selectedFineDll) ? selectedFineDll : Path.Combine(directory, "sighsorry-FineDining", "FineDining.dll");
            Assembly fine = FineFixture(finePath);
            _fineBridge = fine.GetType("FineDining.EpicLootStackCompatibility", true);
            Type patch = fine.GetType("FineDining.ItemDropAutoStackSpoilagePatch", true);
            RuntimeHelpers.RunClassConstructor(patch.TypeHandle);
            _fineTranspiler = patch.GetMethod("Transpiler", All);
            _expiry = fine.GetType("FineDining.DecayRuntime", true).GetMethod("ComposeGroundStackExpiry", All);
            _entry = _fineBridge.GetMethod("BeforeAutoStack", All);
            Require(_entry != null, "FineDining owns an operation-boundary guard");
            _clock = fine.GetType("FineDining.ItemDropClockReadinessPatch", true).GetMethod("Prefix", All);
        }

        var own = new Harmony(DropId);
        var external = new Harmony(EpicId);
        var fineOwner = new Harmony(FineId);
        var fineEntry = new Harmony(FineEntryId);
        var other = new Harmony("flg.epic.test.other");
        MethodInfo ownTranspiler = _protection.GetMethod("Transpiler", All);
        SetProtection("_target", _target); SetProtection("_transpiler", ownTranspiler);
        Action installFlg = () => own.Patch(_target, transpiler: new HarmonyMethod(ownTranspiler) { priority = Priority.Last });
        Action installFine = () =>
        {
            fineOwner.Patch(_target, transpiler: new HarmonyMethod(_fineTranspiler));
            fineOwner.CreateClassProcessor(_clock.DeclaringType).Patch();
            fineEntry.Patch(_target, prefix: new HarmonyMethod(_entry) { priority = Priority.First, before = new[] { EpicId } });
        };
        try
        {
            Require(!(Harmony.GetPatchInfo(_target)?.Owners.Any() ?? false), "isolated process starts without prior stack patches");
            if (scenario == "vanilla-both")
            {
                installFine(); installFlg();
                Require(!EnsureFine(), "absent EpicLoot needs no FineDining replacement bridge");
                EnsureFlg(); ValidateProtection(false);
                List<CodeInstruction> vanilla = PatchProcessor.GetCurrentInstructions(_target);
                Require(vanilla.Count(instruction => instruction.Calls(_filter)) == 1 && vanilla.Count(instruction => instruction.Calls(_expiry)) == 1,
                    "ordinary game route retains exactly one FLG filter and one FineDining expiry call");
                Require(!(Harmony.GetPatchInfo(_epicPrefix)?.Owners.Any() ?? false), "no unused EpicLoot callback detour installed");
                CheckClockGuard(other, false);
                return Finish(scenario);
            }
            if (scenario == "late-epic")
            {
                installFine(); installFlg();
                Require(!EnsureFine(), "initially absent EpicLoot does not create FineDining replacement hook");
                EnsureFlg(false); ValidateProtection(false);
            }
            else if (scenario == "plain-first") installFlg();
            external.Patch(_target, prefix: new HarmonyMethod(_epicPrefix) { wrapTryCatch = scenario == "wrapped" });

            if (scenario == "flg-first" || scenario == "late-fine")
            {
                installFlg(); EnsureFlg();
                Require(PatchProcessor.GetCurrentInstructions(_epicPrefix).All(instruction => !instruction.Calls(_expiry)),
                    "FLG alone does not insert or own expiry policy");
                installFine(); Require(EnsureFine(), "later FineDining installs its own expiry route");
                EnsureFlg();
            }
            else if (scenario == "pending-fine")
            {
                installFine(); installFlg();
                EnsureFlg(false);
                Reject(() => EnsureFlg(), "dispensing refuses a present FineDining whose expiry bridge is not ready");
                Require(PatchProcessor.GetCurrentInstructions(_epicPrefix).All(instruction => !instruction.Calls(_expiry)),
                    "FLG pending initialization never inserts a temporary duplicate expiry path");
                Require(EnsureFine(), "FineDining later Start completes its own expiry route"); EnsureFlg();
            }
            else
            {
                if (_withFine && scenario != "late-epic") installFine();
                if (_withFlg && scenario != "plain-first" && scenario != "late-epic" && scenario != "fine-first") installFlg();
                if (scenario.StartsWith("changed-", StringComparison.Ordinal) || scenario == "wrapped")
                {
                    Reject(() => EnsureFine(), "FineDining rejects changed replacement/exception contract: " + scenario);
                    Reject(() => EnsureFlg(), "FLG rejects changed replacement/exception contract: " + scenario);
                    Require(Harmony.GetPatchInfo(_target).Prefixes.Any(patch => patch.PatchMethod == _epicPrefix), "failure leaves EpicLoot's own patch intact");
                    return Finish(scenario);
                }
                if (_withFine) Require(EnsureFine(), "FineDining owns the active replacement expiry route");
                if (scenario == "fine-first") installFlg();
                if (_withFlg) EnsureFlg();
            }
            if (_withFlg) ValidateProtection(true);
            CheckComposition(_withFlg, _withFine);
            CheckMagicPolicy(magic);
            if (_withFine) Require(EnsureFine(), "repeated FineDining validation retains its route");
            if (_withFlg) EnsureFlg();
            CheckComposition(_withFlg, _withFine);
            if (scenario == "detour") CheckPrecompiledCallback(other);
            else if (scenario == "boundary") CheckBoundary(other);
            else if (scenario == "legacy-expiry") CheckLegacyExpiry(other);
            else if (scenario == "cleanup-flg") CheckCleanupFlg();
            else if (scenario == "cleanup-fine") CheckCleanupFine();
            else CheckRejections(other);
            return Finish(scenario);
        }
        finally
        {
            other.UnpatchSelf(); ShutdownFlg(); ShutdownFine();
            own.UnpatchSelf(); external.UnpatchSelf(); fineOwner.UnpatchSelf(); fineEntry.UnpatchSelf();
        }
    }

    private static int Finish(string scenario)
    {
        System.Console.WriteLine("PASS: " + _passed + " EpicLoot stack checks: " + scenario);
        System.Console.WriteLine("Scope: actual mod methods/original game metadata and Harmony composition; only FineDiningPlugin startup suppressed. Detour test uses temporary early-stop callback and null managed call. No native Unity, physical merge, expiry-policy execution, ownership transfer or multiplayer run.");
        return 0;
    }

    private static void CheckComposition(bool filterExpected, bool expiryExpected)
    {
        List<CodeInstruction> original = PatchProcessor.GetOriginalInstructions(_epicPrefix);
        List<CodeInstruction> code = PatchProcessor.GetCurrentInstructions(_epicPrefix);
        int filter = code.FindIndex(instruction => instruction.Calls(_filter));
        Require(code.Count(instruction => instruction.Calls(_filter)) == (filterExpected ? 1 : 0), "exact expected FLG filter count");
        Require(code.Count(instruction => IsCall(instruction, typeof(UnityEngine.Physics), "OverlapSphere")) == 1 &&
            code.Count(instruction => IsCall(instruction, typeof(ZNetView), "Destroy")) == 1 &&
            code.Count(instruction => IsCall(instruction, typeof(ItemDrop), "Save")) == 1, "existing single search/destruction/save routes are preserved");
        Require(code.Count(instruction => instruction.operand is MethodInfo method && method.Name == "StacksWith") == 1,
            "EpicLoot owns the original magic eligibility policy");
        var removed = new List<CodeInstruction>(code);
        if (filterExpected)
        {
            Require(filter > 1 && code[filter - 1].opcode == OpCodes.Ldarg_0 && code[filter - 2].operand is MethodInfo lookup &&
                lookup.Name == "GetComponent" && lookup.GetGenericArguments().Single() == typeof(ItemDrop), "FLG filters the candidate before any merge predicates");
            removed.RemoveRange(filter - 1, 2);
        }
        int expiryCalls = code.Count(instruction => instruction.operand is MethodInfo method && method.Name == "ComposeGroundStackExpiry");
        Require(expiryCalls == (expiryExpected ? 1 : 0), "expiry is composed exactly once only when FineDining owns it");
        if (expiryExpected)
        {
            int compose = removed.FindIndex(instruction => instruction.Calls(_expiry));
            int add = removed.FindIndex(instruction => instruction.opcode == OpCodes.Stfld && instruction.operand is FieldInfo field && field.Name == "m_stack");
            int policy = removed.FindIndex(instruction => instruction.operand is MethodInfo method && method.Name == "StacksWith");
            Require(compose >= 2 && compose == add - 8 && policy < compose && removed[compose - 2].opcode == OpCodes.Ldarg_0 && removed[compose - 1].IsLdloc(),
                "FineDining receives receiver/candidate after acceptance and immediately before count mutation");
            removed[compose + 1].labels.AddRange(removed[compose - 2].labels);
            removed.RemoveRange(compose - 2, 3);
            Require(Harmony.GetPatchInfo(_epicPrefix).Transpilers.Any(patch => patch.owner == FineBridgeId), "FineDining owns its replacement transpiler");
        }
        Require(Signature(removed) == Signature(original), "removing owned insertions restores complete EpicLoot body and branches");
        if (filterExpected && expiryExpected)
        {
            Patches patches = Harmony.GetPatchInfo(_epicPrefix);
            Require(patches.Transpilers.Count == 2 && patches.Transpilers.Count(patch => patch.owner == FlgId) == 1 &&
                patches.Transpilers.Count(patch => patch.owner == FineBridgeId) == 1, "independent owners each install one replacement transpiler");
        }
    }

    private static void CheckPrecompiledCallback(Harmony other)
    {
        other.Patch(_epicPrefix, prefix: new HarmonyMethod(Method(nameof(StopEpic))) { priority = Priority.First });
        var invoke = new DynamicMethod("flg_epic_invoke_wrapper", typeof(void), Type.EmptyTypes, _bridge.Module, true);
        ILGenerator il = invoke.GetILGenerator(); il.Emit(OpCodes.Ldnull); il.Emit(OpCodes.Call, _target); il.Emit(OpCodes.Ret);
        ((Action)invoke.CreateDelegate(typeof(Action)))();
        Require(_detourCalls == 1, "already-emitted game wrapper follows later callback detour");
        other.UnpatchSelf(); EnsureFlg();
    }
    private static void CheckBoundary(Harmony other)
    {
        Require((bool)_entry.Invoke(null, new object[] { true }), "FineDining runtime boundary admits the validated combined route");
        Require(!(bool)_entry.Invoke(null, new object[] { false }), "FineDining preserves an earlier prefix veto without executing replacement logic");
        other.Patch(_target, prefix: new HarmonyMethod(Method(nameof(ForeignPrefix))));
        Require(!(bool)_entry.Invoke(null, new object[] { true }), "unknown original wrapper is refused at the actual operation boundary");
        var invoke = new DynamicMethod("fine_epic_blocked_wrapper", typeof(void), Type.EmptyTypes, _bridge.Module, true);
        ILGenerator il = invoke.GetILGenerator(); il.Emit(OpCodes.Ldnull); il.Emit(OpCodes.Call, _target); il.Emit(OpCodes.Ret);
        ((Action)invoke.CreateDelegate(typeof(Action)))();
        Require(true, "actual original-target wrapper exits before any receiver/game access when boundary is blocked");
        other.UnpatchSelf();
        Require(!(bool)_entry.Invoke(null, new object[] { true }), "runtime failure remains latched after external patch disappears");
        ShutdownFine();
        Require(EnsureFine() && (bool)_entry.Invoke(null, new object[] { true }), "explicit lifecycle reset restores the verified boundary");
        EnsureFlg(); CheckComposition(true, true);
    }
    private static void CheckLegacyExpiry(Harmony other)
    {
        // Explicit synthetic older-FLG behavior: a test postfix adds expiry to
        // the real FLG transpiler's result before FineDining sees that result.
        // The original game and all on-disk mod DLLs remain untouched.
        other.Patch(_bridge.GetMethod("Transpiler", All), postfix: new HarmonyMethod(Method(nameof(AddLegacyExpiry))));
        new Harmony(FlgId).Patch(_epicPrefix);
        List<CodeInstruction> code = PatchProcessor.GetCurrentInstructions(_epicPrefix);
        Require(code.Count(instruction => instruction.Calls(_expiry)) == 1,
            "FineDining never adds a second expiry call to a legacy-FLG expiry path");
        Require(!(bool)_fineBridge.GetField("_inserted", All).GetValue(null),
            "FineDining records that the preexisting expiry call is not its own insertion");
        Reject(() => EnsureFine(), "FineDining refuses to claim an inherited expiry callback as a complete own patch");
        Require(!(bool)_entry.Invoke(null, new object[] { true }),
            "legacy expiry replacement is blocked at operation boundary before item mutation");
        other.UnpatchSelf();
        new Harmony(FlgId).Patch(_epicPrefix);
        CheckComposition(true, true);
    }

    private static void AddLegacyExpiry(ref IEnumerable<CodeInstruction> __result)
    {
        var code = __result.ToList();
        int lookup = code.FindIndex(instruction => instruction.operand is MethodInfo method && method.Name == "GetComponent" &&
            method.IsGenericMethod && method.GetGenericArguments().Single() == typeof(ItemDrop));
        int candidateLoad = lookup + 4; // after FLG's ldarg/filter, candidate store/load
        int store = code.FindIndex(instruction => instruction.opcode == OpCodes.Stfld && instruction.operand is FieldInfo field && field.Name == "m_stack");
        int start = store - 7;
        var receiver = new CodeInstruction(OpCodes.Ldarg_0);
        receiver.labels.AddRange(code[start].labels); code[start].labels.Clear();
        code.InsertRange(start, new[] { receiver, new CodeInstruction(code[candidateLoad].opcode, code[candidateLoad].operand), new CodeInstruction(OpCodes.Call, _expiry) });
        __result = code;
    }
    private static void CheckRejections(Harmony other)
    {
        if (_withFine && _withFlg) CheckClockGuard(other, true);
        other.Patch(_target, prefix: new HarmonyMethod(Method(nameof(ForeignPrefix))));
        RejectActive("unrecognized original wrapper"); other.UnpatchSelf(); EnsureActive();
        other.Patch(_target, transpiler: new HarmonyMethod(Method(nameof(KeepBody))));
        RejectActive("otherwise bypassed original transpiler"); other.UnpatchSelf(); EnsureActive();
        other.Patch(_epicPrefix, postfix: new HarmonyMethod(Method(nameof(ForeignPostfix))));
        RejectActive("unrecognized replacement wrapper"); other.UnpatchSelf(); EnsureActive();
        if (_withFine)
        {
            new Harmony(FineBridgeId).UnpatchSelf();
            Reject(() => EnsureFine(), "missing FineDining hook cannot masquerade as active expiry support");
            if (_withFlg) Reject(() => EnsureFlg(), "FLG refuses dispensing with missing FineDining expiry route");
            ShutdownFine();
        }
        else
        {
            new Harmony(FlgId).UnpatchSelf();
            Reject(() => EnsureFlg(), "missing FLG filter cannot masquerade as protected dispensing");
        }
    }

    private static void CheckClockGuard(Harmony other, bool epic)
    {
        var owner = new Harmony(FineId);
        owner.Unpatch(_target, _clock);
        Reject(() => EnsureFlg(), "missing known winter clock guard refuses dispensing");
        if (epic) Reject(() => EnsureFine(), "FineDining rejects missing winter clock guard");
        owner.Patch(_target,prefix:new HarmonyMethod(_clock) { priority=Priority.First, before=new[]{EpicId} });
        Reject(() => EnsureFlg(), "winter guard with incomplete ordering is refused");
        if (epic) Reject(() => EnsureFine(), "FineDining rejects incomplete winter guard ordering");
        owner.Unpatch(_target, _clock);
        other.Patch(_target,prefix:new HarmonyMethod(_clock));
        Reject(() => EnsureFlg(), "winter guard under a foreign owner is refused");
        other.UnpatchSelf();
        owner.CreateClassProcessor(_clock.DeclaringType).Patch();
        EnsureFlg();
        if (epic) Require(EnsureFine(), "complete winter guard restores the shared route");
    }
    private static void CheckCleanupFlg()
    {
        ShutdownFlg();
        Require(!Harmony.GetPatchInfo(_epicPrefix).Owners.Contains(FlgId), "FLG shutdown removes only its callback owner");
        Require(Harmony.GetPatchInfo(_epicPrefix).Owners.Contains(FineBridgeId), "FineDining expiry survives FLG shutdown");
        CheckComposition(false, true);
    }
    private static void CheckCleanupFine()
    {
        ShutdownFine();
        Require(!Harmony.GetPatchInfo(_epicPrefix).Owners.Contains(FineBridgeId), "FineDining shutdown removes only its callback owner");
        Require(Harmony.GetPatchInfo(_epicPrefix).Owners.Contains(FlgId), "FLG marker filter survives FineDining shutdown");
        CheckComposition(true, false);
        Reject(() => EnsureFlg(), "remaining FineDining original policy without replacement expiry blocks new dispensing");
    }
    private static void RejectActive(string message)
    {
        if (_withFine) Reject(() => EnsureFine(), "FineDining rejects " + message);
        if (_withFlg) Reject(() => EnsureFlg(), "FLG rejects " + message);
    }
    private static void EnsureActive()
    {
        if (_withFine) Require(EnsureFine(), "FineDining valid route restored");
        if (_withFlg) EnsureFlg();
    }
    private static void CheckMagicPolicy(Type magic)
    {
        MethodInfo stacks = magic.GetMethod("StacksWith", All);
        var a = new ItemDrop.ItemData { m_customData = new Dictionary<string, string>() };
        var b = new ItemDrop.ItemData { m_customData = new Dictionary<string, string>() };
        bool Match() => (bool)stacks.Invoke(null, new object[] { a, b });
        Require(Match(), "ordinary items retain EpicLoot stack acceptance");
        const string key = "randyknapp.mods.epicloot#EpicLoot.MagicItemComponent";
        a.m_customData[key] = "{one}"; Require(!Match(), "magic/ordinary remain incompatible");
        b.m_customData[key] = "{two}"; Require(!Match(), "different magic data stays incompatible");
        b.m_customData[key] = "{one}"; Require(Match(), "identical magic data stays compatible");
    }

    private static Assembly FineFixture(string path)
    {
        using (AssemblyDefinition source = AssemblyDefinition.ReadAssembly(path))
        using (var stream = new MemoryStream())
        {
            var methods = source.MainModule.Types.Where(type => type.FullName == "FineDining.EpicLootStackCompatibility" ||
                type.FullName == "FineDining.ItemDropAutoStackSpoilagePatch" || type.FullName == "FineDining.DecayRuntime")
                .SelectMany(type => type.Methods).Where(method => method.HasBody).ToArray();
            string before = string.Join("\n", methods.Select(CecilBody));
            MethodDefinition initializer = source.MainModule.GetType("FineDining.FineDiningPlugin").Methods.Single(method => method.Name == ".cctor");
            initializer.Body.Instructions.Clear(); initializer.Body.Variables.Clear(); initializer.Body.ExceptionHandlers.Clear();
            initializer.Body.Instructions.Add(Mono.Cecil.Cil.Instruction.Create(Mono.Cecil.Cil.OpCodes.Ret));
            Require(before == string.Join("\n", methods.Select(CecilBody)), "FineDining fixture preserves all integration/transpiler/expiry method bodies");
            source.MainModule.Mvid = Guid.NewGuid(); source.Write(stream); return Assembly.Load(stream.ToArray());
        }
    }
    private static Assembly EpicVariant(string path, string scenario)
    {
        using (AssemblyDefinition source = AssemblyDefinition.ReadAssembly(path))
        using (var stream = new MemoryStream())
        {
            Guid originalMvid = source.MainModule.Mvid;
            source.MainModule.Mvid = Guid.NewGuid(); source.Name.Version = new System.Version(98, 76, 54, 32);
            TypeDefinition magic = source.MainModule.GetType("EpicLoot.MagicItemData");
            MethodDefinition prefix = magic.NestedTypes.Single(type => type.Name == "ItemDrop_AutoStackItems_Patch").Methods.Single(method => method.Name == "Prefix");
            if (scenario == "identity")
            {
                foreach (MethodDefinition method in new[] { prefix, magic.Methods.Single(method => method.Name == "StacksWith"), magic.Methods.Single(method => method.Name == "RawOf"), magic.Methods.Single(method => method.Name == "HasMagicData") })
                    method.Body.Instructions.Insert(0, Mono.Cecil.Cil.Instruction.Create(Mono.Cecil.Cil.OpCodes.Nop));
            }
            else if (scenario == "changed-prefix") prefix.Body.Instructions.Single(instruction => instruction.OpCode == Mono.Cecil.Cil.OpCodes.Ldc_R4 && (float)instruction.Operand == 4f).Operand = 8f;
            else if (scenario == "changed-stacks") magic.Methods.Single(method => method.Name == "StacksWith").Body.Instructions.First(instruction => instruction.OpCode == Mono.Cecil.Cil.OpCodes.Ldc_I4_1).OpCode = Mono.Cecil.Cil.OpCodes.Ldc_I4_0;
            else if (scenario == "changed-raw") magic.Methods.Single(method => method.Name == "RawOf").Body.Instructions.First(instruction => instruction.OpCode == Mono.Cecil.Cil.OpCodes.Ldstr).Operand = "changed.magic.key";
            else throw new ArgumentException(scenario);
            source.Write(stream); Assembly result = Assembly.Load(stream.ToArray());
            Require(result.ManifestModule.ModuleVersionId != originalMvid && result.GetName().Version == new System.Version(98, 76, 54, 32), "synthetic Epic variant changes version and MVID");
            return result;
        }
    }
    private static string CecilBody(MethodDefinition method) => string.Join("\n", method.Body.Instructions.Select(instruction => instruction.OpCode.Code + " " + instruction.Operand));
    private static string Signature(List<CodeInstruction> code)
    {
        string Operand(object operand)
        {
            if (operand is Label label) return "target=" + code.FindIndex(instruction => instruction.labels.Contains(label));
            if (operand is Label[] labels) return string.Join(",", labels.Select(value => Operand(value)));
            if (operand is LocalVariableInfo local) return "local=" + local.LocalIndex + ":" + local.LocalType.FullName;
            if (operand is MemberInfo member) return member.DeclaringType?.FullName + ":" + member;
            return operand?.ToString() ?? "";
        }
        return string.Join("\n", code.Select(instruction => instruction.opcode.Name + " " + Operand(instruction.operand)));
    }
    private static bool IsCall(CodeInstruction instruction, Type owner, string name) => instruction.operand is MethodInfo method && method.DeclaringType == owner && method.Name == name && (instruction.opcode == OpCodes.Call || instruction.opcode == OpCodes.Callvirt);
    private static void EnsureFlg(bool requireExpiry = true)
    {
        bool active = (bool)_bridge.GetMethod("Ensure", All).Invoke(null, new object[] { Harmony.GetPatchInfo(_target), _filter, requireExpiry });
        Require(active, "FLG recognizes the supported active wrapper route");
    }
    private static bool EnsureFine() => (bool)_fineBridge.GetMethod("Ensure", All).Invoke(null, new object[] { Harmony.GetPatchInfo(_target) });
    private static void ShutdownFlg() => _bridge.GetMethod("Shutdown", All).Invoke(null, null);
    private static void ShutdownFine() { if (_fineBridge != null) _fineBridge.GetMethod("Shutdown", All).Invoke(null, null); }
    private static void ValidateProtection(bool requireExpiry) => _protection.GetMethod("ValidateInstalledPatchCore", All).Invoke(null, new object[] { requireExpiry });
    private static void SetProtection(string name, object value) => _protection.GetField(name, All).SetValue(null, value);
    private static MethodInfo Method(string name) => typeof(EpicLootStackChecks).GetMethod(name, All);
    private static void Reject(Action action, string message)
    {
        try { action(); }
        catch (TargetInvocationException error) when (error.InnerException is InvalidOperationException || error.InnerException is MissingMemberException) { _passed++; return; }
        throw new Exception("Expected rejection: " + message);
    }
    private static void Require(bool value, string message) { if (!value) throw new Exception(message); _passed++; }
    private static IEnumerable<CodeInstruction> KeepBody(IEnumerable<CodeInstruction> code) => code;
    private static void ForeignPrefix() { }
    private static void ForeignPostfix() { }
    private static bool StopEpic(ref bool __result) { _detourCalls++; __result = false; return false; }
}
