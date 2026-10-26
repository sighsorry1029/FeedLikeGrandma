using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Mono.Cecil;

// Uses original game assemblies and installed optional-mod transpiler bodies.
// Fixtures suppress only ItemInfo's broad startup initializer (it hooks Player
// methods requiring native Unity during JIT), or FineDiningPlugin's ServerSync
// startup initializer (even a logging reference triggers it during Mono JIT).
// The transpiler and helper bodies remain intact. Never call the resulting Unity
// method or foreign item-data/expiry helpers; this is not full plugin startup.
internal static class AzuStackCompositionChecks
{
    private const BindingFlags All = BindingFlags.Static | BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private const string OwnId = "sighsorry.FeedLikeGrandma.FeedBarrelDrops";
    private static Type _protection;
    private static MethodInfo _target, _filter, _ownTranspiler;
    private static int _passed, _countingRuns;

    internal static int Run(Type protection, string scenario, string pluginDirectory)
    {
        _protection = protection;
        _target = AccessTools.DeclaredMethod(typeof(ItemDrop), "AutoStackItems", Type.EmptyTypes);
        _filter = protection.GetMethod("FilterStackCandidate", All);
        _ownTranspiler = protection.GetMethod("Transpiler", All);
        Set("_target", _target); Set("_transpiler", _ownTranspiler);
        if (scenario == "mutations") CheckMutations();
        else CheckForeignComposition(pluginDirectory, scenario);
        System.Console.WriteLine("PASS: " + _passed + " stack composition checks: " + scenario);
        System.Console.WriteLine("Scope: original game IL/Harmony detours and installed-mod transpiler bodies; explicit fixtures suppress only FineDiningPlugin or ItemInfo startup initializers. No Unity execution, item metadata callbacks, world/network simulation, or full plugin startup.");
        return 0;
    }

    private static void CheckForeignComposition(string directory, string scenario)
    {
        string[] names = scenario.StartsWith("combined", StringComparison.Ordinal)
            ? new[] { "fine", "crafty", "cuisine", "sleep" }
            : new[] { scenario.Split('-')[0] };
        var foreign = new List<MethodInfo>();
        foreach (string name in names)
        {
            string path, typeName, methodName;
            if (name == "fine")
            {
                path = Path.Combine(directory, "sighsorry-FineDining", "FineDining.dll");
                typeName = "FineDining.ItemDropAutoStackSpoilagePatch"; methodName = "Transpiler";
            }
            else if (name == "crafty")
            {
                path = Path.Combine(directory, "Azumatt-AzuCraftyBoxes", "AzuCraftyBoxes.dll");
                typeName = "ItemDataManager.ItemInfo"; methodName = "HandleAutostackableItems";
            }
            else if (name == "cuisine")
            {
                path = Path.Combine(directory, "Xutz-ValheimCuisine", "ValheimCuisine.dll");
                typeName = "ItemDataManager.ItemInfo"; methodName = "HandleAutostackableItems";
            }
            else if (name == "sleep")
            {
                path = Path.Combine(directory, "Vapok-BetterSleepBruh", "BetterSleepBruh.dll");
                typeName = "Vapok.Common.Managers.ItemInfo"; methodName = "HandleAutostackableItems";
            }
            else throw new ArgumentException(name);
            Assembly assembly = WithoutRuntimeStartup(path, typeName, methodName,
                name == "fine" ? "FineDining.FineDiningPlugin" : typeName);
            Type type = assembly.GetType(typeName, true);
            MethodInfo transpiler = type.GetMethod(methodName, All);
            Require(transpiler != null, "actual installed transpiler exists: " + name);
            // FineDining's patch-class initializer only binds fields/methods;
            // the plugin initializer was suppressed. ItemInfo's own startup has
            // explicitly been suppressed in each data-manager fixture.
            RuntimeHelpers.RunClassConstructor(type.TypeHandle);
            foreign.Add(transpiler);
        }
        Require(!(Harmony.GetPatchInfo(_target)?.Owners.Any() ?? false), "disposable composition process starts without preinstalled AutoStack hooks");

        var own = new Harmony(OwnId);
        var external = new Harmony("flg.test.unlisted.foreign.owner");
        try
        {
            bool ownFirst = scenario.EndsWith("first", StringComparison.Ordinal);
            if (ownFirst) own.Patch(_target, transpiler: new HarmonyMethod(_ownTranspiler));
            foreach (MethodInfo method in foreign)
                external.Patch(_target, transpiler: new HarmonyMethod(method));
            if (!ownFirst) own.Patch(_target, transpiler: new HarmonyMethod(_ownTranspiler));
            Validate();
            List<CodeInstruction> code = PatchProcessor.GetCurrentInstructions(_target);
            Require(code.Count(instruction => instruction.Calls(_filter)) == 1, "all modifiers retain exactly one FLG filter");
            Require(code.Count(instruction => IsCall(instruction, typeof(ZNetView), "Destroy")) == 1 &&
                code.Count(instruction => IsCall(instruction, typeof(ItemDrop), "Save")) == 1,
                "original destroy and save remain exactly once");
            if (names.Contains("fine"))
                Require(code.Count(instruction => instruction.operand is MethodInfo method &&
                    method.DeclaringType.FullName == "FineDining.DecayRuntime" && method.Name == "ComposeGroundStackExpiry") == 1,
                    "FineDining expiry composition remains present once");
            int managers = names.Count(name => name != "fine");
            Require(code.Count(instruction => instruction.operand is MethodInfo method && method.Name == "IsStackableItemDrop") == managers &&
                code.Count(instruction => instruction.operand is MethodInfo method && method.Name == "ApplyCustomItemDataStackableAutoStack") == managers,
                "all item-data acceptance and transfer callbacks are retained");
            own.UnpatchSelf();
            Require(Harmony.GetPatchInfo(_target).Transpilers.All(patch => patch.owner == external.Id) &&
                Harmony.GetPatchInfo(_target).Transpilers.Count == foreign.Count,
                "removing only the FLG owner retains every foreign transpiler");
            int filterIndex = code.FindIndex(instruction => instruction.Calls(_filter));
            code.RemoveRange(filterIndex - 1, 2);
            Require(InstructionSignature(code) == InstructionSignature(PatchProcessor.GetCurrentInstructions(_target)),
                "removing FLG's two instructions leaves the complete foreign pipeline unchanged, including helper order");
        }
        finally { own.UnpatchSelf(); external.UnpatchSelf(); }
    }

    private static Assembly WithoutRuntimeStartup(string path, string typeName, string methodName, string initializerType)
    {
        using (AssemblyDefinition source = AssemblyDefinition.ReadAssembly(path))
        using (var stream = new MemoryStream())
        {
            TypeDefinition type = source.MainModule.GetType(typeName);
            string body = BodySignature(type.Methods.Single(method => method.Name == methodName));
            MethodDefinition initializer = source.MainModule.GetType(initializerType).Methods.Single(method => method.Name == ".cctor");
            initializer.Body.Instructions.Clear(); initializer.Body.Variables.Clear(); initializer.Body.ExceptionHandlers.Clear();
            initializer.Body.Instructions.Add(Mono.Cecil.Cil.Instruction.Create(Mono.Cecil.Cil.OpCodes.Ret));
            source.MainModule.Mvid = Guid.NewGuid(); source.Write(stream);
            using (var verificationStream = new MemoryStream(stream.ToArray()))
            using (AssemblyDefinition fixture = AssemblyDefinition.ReadAssembly(verificationStream))
                Require(body == BodySignature(fixture.MainModule.GetType(typeName).Methods.Single(method => method.Name == methodName)),
                    "synthetic fixture retains exact transpiler instruction/operand signature: " + Path.GetFileName(path));
            return Assembly.Load(stream.ToArray());
        }
    }

    private static string BodySignature(MethodDefinition method) => string.Join("\n", method.Body.Instructions.Select(instruction =>
        instruction.OpCode.Code + " " + instruction.Operand));

    private static string InstructionSignature(List<CodeInstruction> code)
    {
        string Operand(object value)
        {
            if (value is Label label) return "target=" + code.FindIndex(instruction => instruction.labels.Contains(label));
            if (value is Label[] labels) return string.Join(",", labels.Select(label => Operand(label)));
            if (value is LocalVariableInfo local) return "local=" + local.LocalIndex + ":" + local.LocalType.FullName;
            if (value is Type type) return type.Assembly.GetName().Name + ":" + type.FullName;
            if (value is MemberInfo member) return member.DeclaringType?.Assembly.GetName().Name + ":" + member.DeclaringType?.FullName + ":" + member;
            return value?.ToString() ?? "";
        }
        return string.Join("\n", code.Select(instruction => instruction.opcode.Name + " " + Operand(instruction.operand)));
    }

    private static void CheckMutations()
    {
        var own = new Harmony(OwnId);
        var other = new Harmony("flg.test.arbitrary-owner");
        try
        {
            own.Patch(_target, transpiler: new HarmonyMethod(_ownTranspiler));
            other.Patch(_target, transpiler: new HarmonyMethod(Method(nameof(CountAndKeep))));
            Validate(); Require(true, "unlisted harmless transpiler is accepted");
            int afterFirstValidation = _countingRuns;
            Validate(); Validate();
            Require(_countingRuns == afterFirstValidation, "unchanged patch set does not repeat IL composition");
            own.Patch(_target);
            Require(_countingRuns > afterFirstValidation, "an unchanged-callback Harmony wrapper rebuild runs the transpilers");
            int afterRebuild = _countingRuns;
            Validate();
            Require(_countingRuns > afterRebuild, "wrapper rebuild invalidates previously cached body validation");
            other.Patch(_target, transpiler: new HarmonyMethod(Method(nameof(AddNop))));
            int afterLatePatch = _countingRuns;
            Validate();
            Require(_countingRuns > afterLatePatch, "later transpiler registration triggers composed-IL revalidation");

            other.Patch(_target, transpiler: new HarmonyMethod(Method(nameof(RemoveFilter))));
            Reject(Validate, "later removal of the protected candidate filter is rejected");
            other.Unpatch(_target, Method(nameof(RemoveFilter)));
            Validate(); Require(true, "restored valid composed path can be validated");
            other.Patch(_target, prefix: new HarmonyMethod(Method(nameof(ForeignPrefix))));
            Reject(Validate, "outside-body prefix cannot silently bypass the composed-IL contract");
            other.Unpatch(_target, Method(nameof(ForeignPrefix)));
            other.Patch(_target, postfix: new HarmonyMethod(Method(nameof(ForeignPostfix))));
            Reject(Validate, "outside-body postfix requires an explicit contract");
            other.Unpatch(_target, Method(nameof(ForeignPostfix)));
            own.UnpatchSelf();
            Reject(Validate, "missing own hook is rejected");
            Require(Harmony.GetPatchInfo(_target).Owners.Contains(other.Id), "failed own-hook validation leaves external hooks intact");
        }
        finally { own.UnpatchSelf(); other.UnpatchSelf(); }
    }

    private static IEnumerable<CodeInstruction> CountAndKeep(IEnumerable<CodeInstruction> code) { _countingRuns++; return code; }
    private static IEnumerable<CodeInstruction> AddNop(IEnumerable<CodeInstruction> code)
    {
        var result = code.ToList(); result.Insert(0, new CodeInstruction(OpCodes.Nop)); return result;
    }
    private static IEnumerable<CodeInstruction> RemoveFilter(IEnumerable<CodeInstruction> code)
    {
        var result = code.ToList();
        int filter = result.FindIndex(instruction => instruction.Calls(_filter));
        if (filter > 0) result.RemoveRange(filter - 1, 2);
        return result;
    }
    private static void ForeignPrefix() { }
    private static void ForeignPostfix() { }
    private static bool IsCall(CodeInstruction instruction, Type owner, string name) =>
        (instruction.opcode == OpCodes.Call || instruction.opcode == OpCodes.Callvirt) &&
        instruction.operand is MethodInfo method && method.DeclaringType == owner && method.Name == name;
    private static MethodInfo Method(string name) => typeof(AzuStackCompositionChecks).GetMethod(name, All);
    private static void Set(string field, object value) => _protection.GetField(field, All).SetValue(null, value);
    private static void Validate() => _protection.GetMethod("ValidateInstalledPatch", All).Invoke(null, null);
    private static void Reject(Action action, string message)
    {
        try { action(); }
        catch (TargetInvocationException error) when (error.InnerException is InvalidOperationException) { _passed++; return; }
        throw new Exception("Expected rejection: " + message);
    }
    private static void Require(bool condition, string message) { if (!condition) throw new Exception(message); _passed++; }
}
