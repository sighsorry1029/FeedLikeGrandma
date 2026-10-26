using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Mono.Cecil;

// Loads unchanged original game/Azu assemblies and the final merged FLG DLL.
// Altered Azu inputs below are explicitly synthetic copies used only to exercise
// contract rejection. No Unity components, world, or storage routines execute.
internal static class AzuCompatibilityChecks
{
    private const BindingFlags All = BindingFlags.Static | BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private const string OwnId = "sighsorry.FeedLikeGrandma.AzuAutoStore", OtherId = "flg.azu.test.other";
    private static Type _bridge, _protection;
    private static int _passed;

    private static int Main(string[] args)
    {
        if (args.Length != 5) throw new ArgumentException("Expected FLG DLL, Azu DLL, original Managed, BepInEx core, phase.");
        string[] search = { Path.GetDirectoryName(args[0]), Path.GetDirectoryName(args[1]), args[2], args[3], AppDomain.CurrentDomain.BaseDirectory };
        AppDomain.CurrentDomain.AssemblyResolve += (_, request) =>
        {
            foreach (string directory in search)
            {
                string path = Path.Combine(directory, new AssemblyName(request.Name).Name + ".dll");
                if (File.Exists(path)) return Assembly.LoadFrom(path);
            }
            return null;
        };
        try { return Run(args); }
        catch (Exception error) { System.Console.Error.WriteLine(error); return 1; }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int Run(string[] args)
    {
        Assembly flg = Assembly.LoadFrom(args[0]);
        _bridge = flg.GetType("FeedLikeGrandma.FeedAzuAutoStoreCompatibility", true);
        _protection = flg.GetType("FeedLikeGrandma.FeedBarrelDropProtection", true);
        if (args[4].StartsWith("compose:", StringComparison.Ordinal))
        {
            string[] parts = args[4].Split(new[] { ':' }, 3);
            return AzuStackCompositionChecks.Run(_protection, parts[1], parts[2]);
        }
        if (args[4] != "original") return CheckVariant(args[1], args[4]);
        Assembly azu = Assembly.LoadFrom(args[1]);
        MethodInfo[] methods = Bind(azu);
        Require(methods.Length == 8 && methods[0].Name == "CheckItemDropInstanceAndStore", "original Azu ground-store routes bind");
        Require(methods[0].IsStatic && methods[0].IsAssembly && methods[0].ReturnType == typeof(void) &&
            methods[0].GetParameters().Single().ParameterType == typeof(ItemDrop), "actual internal target signature/access is preserved");
        Require(typeof(ItemDrop).GetField("m_nview", All).IsPrivate, "original game m_nview remains private");
        Require(AccessTools.FieldRefAccess<ItemDrop, ZNetView>(typeof(ItemDrop).GetField("m_nview", All)) != null,
            "cached private accessor can be generated against original Mono metadata");

        Reject(() => Bind(typeof(object).Assembly), "missing external contract rejected");
        CheckOriginalStackIL();
        CheckPatchOwnership();
        System.Console.WriteLine("PASS: " + _passed + " actual-assembly contract/IL and isolated Harmony lifecycle checks in original Mono.");
        System.Console.WriteLine("Scope: unchanged original game/Azu metadata, final FLG IL, explicit synthetic variants/managed Harmony targets. No Unity lifecycle, world persistence, owner transfer, physical feeding, or multiplayer execution.");
        return 0;
    }

    private static int CheckVariant(string path, string phase)
    {
        Assembly variant = Variant(path, phase, module =>
        {
            if (phase == "identity")
            {
                foreach (var method in module.GetType("AzuAutoStore.Util.Functions").Methods.Where(method => method.HasBody))
                    method.Body.Instructions.Insert(0, Mono.Cecil.Cil.Instruction.Create(Mono.Cecil.Cil.OpCodes.Nop));
            }
            else if (phase == "missing-route")
            {
                var route = module.GetType("AzuAutoStore.Managers.AzuAutoStoreItemDropManager").Methods.Single(method => method.Name == "ProcessAskedFor");
                var call = route.Body.Instructions.Single(instruction => instruction.Operand is MethodReference method && method.Name == "CheckItemDropInstanceAndStore");
                call.OpCode = Mono.Cecil.Cil.OpCodes.Pop; call.Operand = null;
            }
            else if (phase == "ownership-rpc")
            {
                var target = module.GetType("AzuAutoStore.Util.Functions").Methods.Single(method => method.Name == "CheckItemDropInstanceAndStore");
                var rpc = target.Body.Instructions.Single(instruction => instruction.OpCode == Mono.Cecil.Cil.OpCodes.Ldstr && (string)instruction.Operand == "RPC_RequestOwn");
                rpc.Operand = "DifferentOwnershipProtocol";
            }
            else throw new ArgumentException("Unknown phase " + phase);
        });
        if (phase == "identity")
        {
            using (var original = AssemblyDefinition.ReadAssembly(path))
                Require(variant.ManifestModule.ModuleVersionId != original.MainModule.Mvid && variant.GetName().Version != original.Name.Version,
                    "test variant actually changes version and MVID");
            Require(Bind(variant).Length == 8, "version/MVID and harmless instruction-offset changes do not gate compatibility");
        }
        else Reject(() => Bind(variant), "relevant contract alteration rejected: " + phase);
        System.Console.WriteLine("PASS: " + _passed + " isolated Azu variant checks: " + phase);
        return 0;
    }

    private static void CheckOriginalStackIL()
    {
        MethodInfo target = AccessTools.DeclaredMethod(typeof(ItemDrop), "AutoStackItems", Type.EmptyTypes);
        List<CodeInstruction> original = PatchProcessor.GetOriginalInstructions(target);
        MethodInfo filter = _protection.GetMethod("FilterStackCandidate", All);
        ValidateUnfiltered(original);
        List<CodeInstruction> patched = Transpile(original);
        Require(patched.Count == original.Count + 2 && patched.Count(instruction => instruction.Calls(filter)) == 1,
            "actual game stack IL receives exactly one two-instruction filter");
        int injected = patched.FindIndex(instruction => instruction.Calls(filter));
        Require(patched[injected - 1].opcode == OpCodes.Ldarg_0 && patched[injected - 2].operand is MethodInfo lookup &&
            lookup.Name == "GetComponent" && lookup.GetGenericArguments().Single() == typeof(ItemDrop), "filter follows existing candidate lookup");
        var withoutInjection = patched.Where((instruction, index) => index != injected && index != injected - 1).ToArray();
        Require(withoutInjection.SequenceEqual(original), "all original instruction objects/labels, ownership, quantity, save and destroy flow are retained");
        Require(patched.Count(instruction => instruction.operand is MethodInfo method && method.DeclaringType == typeof(UnityEngine.Physics) &&
            method.Name == "OverlapSphere") == 1, "filter adds no physics scan");
        var missingLookup = original.Where(instruction => !ReferenceEquals(instruction, patched[injected - 2])).ToList();
        Reject(() => ValidateUnfiltered(missingLookup), "missing lookup rejected by structural validator");
        Reject(() => ValidateUnfiltered(original.Concat(new[] { patched[injected - 2] })), "ambiguous lookup rejected by structural validator");
        Reject(() => ValidateUnfiltered(patched), "already filtered input is not a second insertion site");
        Require(Transpile(missingLookup).SequenceEqual(missingLookup), "unrecognized input is returned intact instead of breaking another owner's patch registration");
        ValidateComposed(patched); Require(true, "valid composed filter and vanilla path are accepted");
        Reject(() => ValidateComposed(original), "composed path without FLG filter is rejected");
        var duplicateFilter = patched.Select(instruction => new CodeInstruction(instruction)).ToList();
        duplicateFilter.InsertRange(injected + 1, new[] { new CodeInstruction(OpCodes.Ldarg_0), new CodeInstruction(OpCodes.Call, filter) });
        Reject(() => ValidateComposed(duplicateFilter), "duplicate FLG candidate filters are rejected");
        var reversedGuard = original.Select(instruction => new CodeInstruction(instruction)).ToList();
        reversedGuard[injected - 2 + 5].opcode = OpCodes.Brfalse;
        Reject(() => ValidateUnfiltered(reversedGuard), "reversed candidate null branch rejected");
        var reversedOwner = original.Select(instruction => new CodeInstruction(instruction)).ToList();
        int ownerCall = reversedOwner.FindIndex(instruction => instruction.operand is MethodInfo method &&
            method.DeclaringType == typeof(ZNetView) && method.Name == "IsOwner");
        reversedOwner[ownerCall + 1].opcode = OpCodes.Brtrue;
        Reject(() => ValidateUnfiltered(reversedOwner), "reversed ownership branch rejected");
        var bypassOwner = original.Select(instruction => new CodeInstruction(instruction)).ToList();
        var generator = new DynamicMethod("stack_guard_labels", typeof(void), Type.EmptyTypes).GetILGenerator();
        var existingLabels = new HashSet<Label>(original.SelectMany(instruction => instruction.labels));
        Label bypass;
        do { bypass = generator.DefineLabel(); } while (existingLabels.Contains(bypass));
        bypassOwner[ownerCall + 2].labels.Add(bypass);
        bypassOwner.Insert(injected - 2 + 6, new CodeInstruction(OpCodes.Br, bypass));
        Reject(() => ValidateUnfiltered(bypassOwner), "branch into post-ownership pre-increment region is rejected");
        const string protectionId = "sighsorry.FeedLikeGrandma.FeedBarrelDrops";
        var harmony = new Harmony(protectionId);
        try
        {
            MethodInfo transpiler = _protection.GetMethod("Transpiler", All);
            harmony.Patch(target, transpiler: new HarmonyMethod(transpiler));
            _protection.GetField("_target", All).SetValue(null, target);
            _protection.GetField("_transpiler", All).SetValue(null, transpiler);
            _protection.GetMethod("ValidateInstalledPatch", All).Invoke(null, null);
            Require(PatchProcessor.GetCurrentInstructions(target).Count(instruction => instruction.Calls(filter)) == 1,
                "actual AutoStack transpiler detour installs, validates and reads back in original Mono");
        }
        finally { harmony.UnpatchSelf(); }
        Require(!Harmony.GetPatchInfo(target).Owners.Contains(protectionId), "actual AutoStack detour can be removed by its owner");
    }

    private static void CheckPatchOwnership()
    {
        // The real prefix and patch-set validator are applied to a managed fixture
        // with the same argument signature. Never execute Azu storage outside Unity.
        MethodInfo target = typeof(AzuCompatibilityChecks).GetMethod(nameof(GroundStore), All);
        MethodInfo route = typeof(AzuCompatibilityChecks).GetMethod(nameof(ScheduledRoute), All);
        MethodInfo prefix = _bridge.GetMethod("SkipProtectedDrop", All);
        var own = new Harmony(OwnId); var other = new Harmony(OtherId);
        try
        {
            Set("_methods", new[] { target, route }); Set("_harmony", own);
            own.Patch(target, prefix: new HarmonyMethod(prefix) { priority = Priority.First });
            Invoke("ValidatePatches"); Require(true, "complete owned prefix set accepted");
            other.Patch(route, prefix: new HarmonyMethod(typeof(AzuCompatibilityChecks).GetMethod(nameof(ForeignPrefix), All)));
            Reject(() => Invoke("ValidatePatches"), "later foreign patch on scheduled route rejected at boundary");
            CheckCleanupBody();
            own.UnpatchSelf();
            Require(!Harmony.GetPatchInfo(target).Owners.Contains(OwnId) && Harmony.GetPatchInfo(route).Owners.Contains(OtherId),
                "isolated owner unpatch preserves the unrelated owner's hook");
            other.UnpatchSelf();
            Set("_methods", new[] { target, route }); Set("_harmony", own);
            Reject(() => Invoke("ValidatePatches"), "missing/partial bridge hook set rejected");
            own.Patch(target, prefix: new HarmonyMethod(prefix) { priority = Priority.First });
            Invoke("ValidatePatches"); own.UnpatchSelf();
            Require(!Harmony.GetPatchInfo(target).Owners.Contains(OwnId), "Harmony owned removal clears bridge");
        }
        finally { own.UnpatchSelf(); other.UnpatchSelf(); Set("_methods", new MethodInfo[0]); Set("_harmony", null); }
    }

    private static void CheckCleanupBody()
    {
        foreach (Type type in new[] { _bridge, _protection })
        {
            var calls = PatchProcessor.GetOriginalInstructions(type.GetMethod("RemoveOwnPatch", All))
                .Select(instruction => instruction.operand).OfType<MethodInfo>().Where(method => method.DeclaringType == typeof(Harmony)).ToArray();
            Require(calls.Length == 1 && calls[0].Name == "UnpatchSelf", type.Name + " cleanup only invokes own Harmony removal (static IL check)");
        }
    }

    private static Assembly Variant(string path, string suffix, Action<ModuleDefinition> mutate)
    {
        using (var assembly = AssemblyDefinition.ReadAssembly(path))
        using (var stream = new MemoryStream())
        {
            string originalName = assembly.Name.Name;
            assembly.Name.Name += ".FLGTest." + suffix;
            assembly.Name.Version = new System.Version(98, 76, 54, 32);
            // ILRepack may leave self-referencing AssemblyRef rows. Keep these
            // attached to the test copy rather than to the unchanged original.
            foreach (var reference in assembly.MainModule.AssemblyReferences.Where(reference => reference.Name == originalName))
            {
                reference.Name = assembly.Name.Name; reference.Version = assembly.Name.Version;
            }
            assembly.MainModule.Mvid = Guid.NewGuid(); mutate(assembly.MainModule);
            assembly.Write(stream); return Assembly.Load(stream.ToArray());
        }
    }
    private static MethodInfo[] Bind(Assembly assembly) => (MethodInfo[])Invoke("ValidateContract", assembly);
    private static object Invoke(string name, params object[] args) => _bridge.GetMethod(name, All).Invoke(null, args);
    private static void Set(string name, object value) => _bridge.GetField(name, All).SetValue(null, value);
    private static List<CodeInstruction> Transpile(IEnumerable<CodeInstruction> code) =>
        ((IEnumerable<CodeInstruction>)_protection.GetMethod("Transpiler", All).Invoke(null, new object[] { code })).ToList();
    private static void ValidateUnfiltered(IEnumerable<CodeInstruction> code) =>
        _protection.GetMethod("ValidateStackBody", All).Invoke(null, new object[] { code.ToList() });
    private static void ValidateComposed(IEnumerable<CodeInstruction> code) =>
        _protection.GetMethod("ValidateComposedBody", All).Invoke(null, new object[] { code.ToList() });
    private static void Reject(Action action, string message)
    {
        try { action(); } catch (TargetInvocationException error) when (error.InnerException is InvalidOperationException || error.InnerException is MissingMemberException) { _passed++; return; }
        throw new Exception("Expected rejection: " + message);
    }
    private static void Require(bool condition, string message) { if (!condition) throw new Exception(message); _passed++; }
    [MethodImpl(MethodImplOptions.NoInlining)] private static void GroundStore(ItemDrop item) { }
    [MethodImpl(MethodImplOptions.NoInlining)] private static void ScheduledRoute() { }
    private static void ForeignPrefix() { }
}
