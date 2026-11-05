using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Runtime.Serialization;
using HarmonyLib;
using Mono.Cecil;

// Original game assemblies and the actual installed manager policy bodies.
// Fixtures retain ItemInfo's field initialization but omit its broad Harmony
// startup. FLG plugin startup and selected FineDining plugin startup are omitted.
// Uninitialized ItemDrop shells supply only m_itemData; no Unity native code,
// physics, count mutation, destruction, persistence or multiplayer is executed.
internal static class ItemDataStackChecks
{
    private const BindingFlags All = BindingFlags.Static | BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private const string DropId = "sighsorry.FeedLikeGrandma.FeedBarrelDrops";
    private const string EpicId = "randyknapp.mods.epicloot";
    private const string FineId = "sighsorry.FineDining";
    private const string EntryId = "sighsorry.FineDining.EpicLootStacks.Entry";
    private static Type _adapter, _providerType, _bridge, _protection;
    private static MethodInfo _target, _filter;
    private static int _passed;
    private static readonly Dictionary<string, string[]> Inputs = new Dictionary<string, string[]>
    {
        { "bows", new[] { "Azumatt-BowsBeforeHoes", "BowsBeforeHoes.dll", "ItemDataManager.ItemInfo", "Azumatt.BowsBeforeHoes" } },
        { "cuisine", new[] { "Xutz-ValheimCuisine", "ValheimCuisine.dll", "ItemDataManager.ItemInfo", "XutzBR.ValheimCuisine" } },
        { "crafty", new[] { "Azumatt-AzuCraftyBoxes", "AzuCraftyBoxes.dll", "ItemDataManager.ItemInfo", "Azumatt.AzuCraftyBoxes" } },
        { "sleep", new[] { "Vapok-BetterSleepBruh", "BetterSleepBruh.dll", "Vapok.Common.Managers.ItemInfo", "vapok.mods.BetterSleepBruh" } }
    };

    internal static int Run(Assembly flg, string scenario, string directory, string fineDll)
    {
        string[] dlls = Directory.GetFiles(directory, "*.dll", SearchOption.AllDirectories);
        AppDomain.CurrentDomain.AssemblyResolve += (_, request) =>
        {
            string name = new AssemblyName(request.Name).Name + ".dll";
            string path = dlls.FirstOrDefault(candidate => string.Equals(Path.GetFileName(candidate), name, StringComparison.OrdinalIgnoreCase));
            return path == null ? null : Assembly.LoadFrom(path);
        };
        _adapter = flg.GetType("FeedLikeGrandma.FeedItemDataStackCompatibility", true);
        _providerType = _adapter.GetNestedType("Provider", All);
        _bridge = flg.GetType("FeedLikeGrandma.FeedEpicLootStackCompatibility", true);
        _protection = flg.GetType("FeedLikeGrandma.FeedBarrelDropProtection", true);
        _target = AccessTools.DeclaredMethod(typeof(ItemDrop), "AutoStackItems", Type.EmptyTypes);
        _filter = _protection.GetMethod("FilterStackCandidate", All);
        if (scenario == "policies") { CheckSyntheticPolicies(); return Finish(scenario); }

        string[] names = Inputs.ContainsKey(scenario) ? new[] { scenario } : scenario == "changed" ? new[] { "bows" } : Inputs.Keys.ToArray();
        var methods = new List<MethodInfo>();
        var owners = new List<Harmony>();
        foreach (string name in names)
        {
            string[] input = Inputs[name];
            Assembly fixture = ManagerFixture(Path.Combine(directory, input[0], input[1]), input[2], scenario);
            Type info = fixture.GetType(input[2], true);
            RuntimeHelpers.RunClassConstructor(info.TypeHandle);
            info.GetField("_modGuid", All).SetValue(null, input[3]);
            methods.Add(info.GetMethod("HandleAutostackableItems", All));
            owners.Add(new Harmony("flg.idm.test." + name));
        }
        try
        {
            if (scenario.StartsWith("newfine-", StringComparison.Ordinal))
                CheckNewFineOwnership(directory, fineDll, methods, owners, scenario);
            else if (scenario.StartsWith("inventory", StringComparison.Ordinal))
                CheckInventoryDiscovery(methods, scenario);
            else if (new[] { "first", "last", "late", "removed-epic", "fine-first", "fine-last" }.Contains(scenario))
                CheckComposition(directory, fineDll, methods, owners, scenario);
            else
            {
                if (scenario == "changed")
                    Reject(() => Bind(new[] { new Patch(new HarmonyMethod(methods[0]), 0, owners[0].Id) }), "a changed actual manager transpiler is rejected before registration");
                else
                {
                    for (int i = 0; i < methods.Count; i++) owners[i].Patch(_target, transpiler: new HarmonyMethod(methods[i]));
                    Array providers = Bind();
                    Require(providers.Length == names.Length, "one provider per active installed manager copy");
                    _adapter.GetMethod("Validate", All).Invoke(null, new object[] { providers });
                    CheckRealPolicies(providers, methods);
                    CheckContractRejections(providers, methods, owners);
                }
            }
        }
        finally { _bridge.GetMethod("Shutdown", All).Invoke(null, null); foreach (Harmony owner in owners) owner.UnpatchSelf(); }
        return Finish(scenario);
    }

    internal static Assembly FlgFixture(string path)
    {
        using (AssemblyDefinition source = AssemblyDefinition.ReadAssembly(path))
        using (var stream = new MemoryStream())
        {
            MethodDefinition initializer = source.MainModule.GetType("FeedLikeGrandma.FeedLikeGrandmaPlugin").Methods.Single(method => method.Name == ".cctor");
            var methods = source.MainModule.Types.Where(type => type.FullName == "FeedLikeGrandma.FeedEpicLootStackCompatibility" ||
                type.FullName == "FeedLikeGrandma.FeedItemDataStackCompatibility" || type.FullName == "FeedLikeGrandma.FeedBarrelDropProtection")
                .SelectMany(type => type.Methods).Where(method => method.HasBody).ToArray();
            string before = string.Join("\n", methods.Select(CecilBody));
            initializer.Body.Instructions.Clear(); initializer.Body.Variables.Clear(); initializer.Body.ExceptionHandlers.Clear();
            initializer.Body.Instructions.Add(Mono.Cecil.Cil.Instruction.Create(Mono.Cecil.Cil.OpCodes.Ret));
            Require(before == string.Join("\n", methods.Select(CecilBody)), "FLG fixture retains actual bridge, policy-composition and protection methods");
            source.MainModule.Mvid = Guid.NewGuid(); source.Write(stream); return Assembly.Load(stream.ToArray());
        }
    }

    private static void CheckRealPolicies(Array providers, List<MethodInfo> methods, Func<ItemDrop, ItemDrop, bool> selectedCompose = null)
    {
        Func<ItemDrop, ItemDrop, bool> merge = selectedCompose ?? ((receiver, source) => Compose(receiver, source, providers));
        ItemDrop a = Drop(), b = Drop();
        b.m_itemData.m_stack = 3;
        Require(merge(a, b), "actual empty-data manager policies accept ordinary drops");
        Require(a.m_itemData.m_customData.Count == 0 && b.m_itemData.m_customData.Count == 0, "empty data remains empty");
        a.m_itemData.m_customData["foreign.stable"] = "same";
        b.m_itemData.m_customData["foreign.stable"] = "same";
        a.m_itemData.m_customData["randyknapp.mods.epicloot#EpicLoot.MagicItemComponent"] = "{magic}";
        b.m_itemData.m_customData["randyknapp.mods.epicloot#EpicLoot.MagicItemComponent"] = "{magic}";
        Require(merge(a, b), "equal unrelated and EpicLoot metadata survive manager composition");
        Require(a.m_itemData.m_customData.Count == 2 && a.m_itemData.m_customData["foreign.stable"] == "same", "manager results do not replace the complete metadata dictionary");
        foreach (MethodInfo transpiler in methods)
        {
            Type info = transpiler.DeclaringType;
            object first = AddData(info, a.m_itemData, "stack", "same", true);
            object second = AddData(info, b.m_itemData, "stack", "same", true);
            Require(merge(a, b), "actual TryStack allows opted-in identical values: " + info.Assembly.GetName().Name);
            SetAllow(first, false);
            Dictionary<string, string> before = a.m_itemData.m_customData;
            Require(!merge(a, b), "actual default TryStack veto is preserved: " + info.Assembly.GetName().Name);
            Require(ReferenceEquals(before, a.m_itemData.m_customData), "a provider veto does not assign replacement metadata");
            SetAllow(first, true);
            second.GetType().GetProperty("Value", All).SetValue(second, "different", null);
            Require(!merge(a, b), "actual manager differing-value policy vetoes merging");
            second.GetType().GetProperty("Value", All).SetValue(second, "same", null);
        }
        int expected = 2 + methods.Count;
        Require(merge(a, b) && a.m_itemData.m_customData.Count == expected, "all live manager partitions and unrelated keys remain together");
        Require(b.m_itemData.m_customData.Count == expected && a.m_itemData.m_stack == 2 && b.m_itemData.m_stack == 3, "composition does not consume source data or change stack counts");
        a = Drop(); b = Drop();
        AddData(methods[0].DeclaringType, a.m_itemData, "one-sided", "same", true);
        Require(!merge(a, b), "actual TryStack(null) one-sided rejection is preserved");
        a = Drop(); b = Drop();
        string prefix = providers != null ? (string)Field(providers.GetValue(0), "Prefix")
            : (string)methods[0].DeclaringType.GetMethod("dataKey", All).Invoke(null, new object[] { "" });
        a.m_itemData.m_customData[prefix + "No.Such.Managed.Type"] = "retained";
        b.m_itemData.m_customData[prefix + "No.Such.Managed.Type"] = "retained";
        Require(!merge(a, b), "unknown stored manager type cannot silently disappear from its result");
    }

    private static void CheckContractRejections(Array providers, List<MethodInfo> methods, List<Harmony> owners)
    {
        Patch[] patches = Harmony.GetPatchInfo(_target).Transpilers.ToArray();
        Reject(() => Bind(patches.Concat(new[] { patches[0] }).ToArray()), "duplicated provider descriptor is rejected");
        string firstPrefix = (string)Field(providers.GetValue(0), "Prefix");
        Type firstInfo = methods[0].DeclaringType;
        string saved = (string)firstInfo.GetField("_modGuid", All).GetValue(null);
        if (methods.Count > 1)
        {
            Type otherInfo = methods[1].DeclaringType;
            string otherSaved = (string)otherInfo.GetField("_modGuid", All).GetValue(null);
            otherInfo.GetField("_modGuid", All).SetValue(null, saved);
            Reject(() => Bind(), "different providers claiming one data prefix are rejected");
            otherInfo.GetField("_modGuid", All).SetValue(null, otherSaved);
        }
        var foreign = new Harmony("flg.idm.test.foreign");
        try
        {
            MethodInfo evaluator = firstInfo.GetMethod("IsStackableItemDrop", All);
            foreign.Patch(evaluator, prefix: new HarmonyMethod(typeof(ItemDataStackChecks).GetMethod(nameof(Noop), All)));
            Reject(() => _adapter.GetMethod("Validate", All).Invoke(null, new object[] { providers }), "later external modification of a bound policy route is rejected");
        }
        finally { foreign.UnpatchSelf(); }
        _adapter.GetMethod("Validate", All).Invoke(null, new object[] { providers });
        Require(Harmony.GetPatchInfo(_target).Transpilers.Count == methods.Count, "validation preserves all original manager patches");
    }

    private static void CheckSyntheticPolicies()
    {
        int callsA = 0, callsB = 0;
        object providerA = Provider("a#", (drop, data) => { callsA++; return new Dictionary<string, string> { { "a#data", "merged-a" } }; });
        object providerB = Provider("b#", (drop, data) => { callsB++; return new Dictionary<string, string> { { "b#data", "merged-b" } }; });
        Array providers = ProviderArray(providerA, providerB);
        ItemDrop a = Drop(), b = Drop();
        foreach (ItemDrop drop in new[] { a, b })
        {
            drop.m_itemData.m_customData["a#data"] = "a";
            drop.m_itemData.m_customData["b#data"] = "b";
            drop.m_itemData.m_customData["other"] = "keep";
        }
        Dictionary<string, string> originalA = a.m_itemData.m_customData, originalB = b.m_itemData.m_customData;
        Require(Compose(a, b, providers), "two synthetic provider outcomes compose successfully");
        Require(callsA == 1 && callsB == 1, "each provider callback is evaluated exactly once");
        Require(a.m_itemData.m_customData["a#data"] == "merged-a" && a.m_itemData.m_customData["b#data"] == "merged-b" && a.m_itemData.m_customData["other"] == "keep", "provider partitions merge without overwriting unrelated keys");
        Require(ReferenceEquals(originalB, b.m_itemData.m_customData) && originalB["a#data"] == "a" && originalA["a#data"] == "a", "original dictionaries and source values remain untouched");
        foreach (string failure in new[] { "null", "exception", "foreign-key", "missing-key", "null-value" })
        {
            a.m_itemData.m_customData = new Dictionary<string, string>(originalA);
            Dictionary<string, string> before = a.m_itemData.m_customData;
            Set(providerB, "Evaluate", new Func<ItemDrop, ItemDrop.ItemData, Dictionary<string, string>>((drop, data) =>
            {
                if (failure == "null") return null;
                if (failure == "exception") throw new InvalidOperationException("synthetic policy failure");
                if (failure == "foreign-key") return new Dictionary<string, string> { { "other", "clobber" } };
                if (failure == "missing-key") return new Dictionary<string, string>();
                return new Dictionary<string, string> { { "b#data", null } };
            }));
            Require(!Compose(a, b, providers), "unsafe policy result is refused: " + failure);
            Require(ReferenceEquals(before, a.m_itemData.m_customData) && before["a#data"] == "a" && before["other"] == "keep", "earlier provider result is not partially committed: " + failure);
        }
        providers = ProviderArray(Provider("a#", (_, __) => new Dictionary<string, string>()));
        a = Drop(); b = Drop(); a.m_itemData.m_customData["unmanaged"] = "a"; b.m_itemData.m_customData["unmanaged"] = "b";
        Require(!Compose(a, b, providers), "different unknown metadata is not discarded");
        b.m_itemData.m_customData.Clear();
        Require(!Compose(a, b, providers), "one-sided unknown metadata is not spread or lost");
        a.m_itemData.m_customData.Clear(); b.m_itemData.m_customData["unmanaged"] = "b";
        Require(!Compose(a, b, providers), "reverse one-sided unknown metadata is also refused");
        a.m_itemData.m_customData.Clear(); b.m_itemData.m_customData.Clear();
        a.m_itemData.m_customData["unregistered.manager#policy"] = "same";
        b.m_itemData.m_customData["unregistered.manager#policy"] = "same";
        Require(!Compose(a, b, providers), "equal values cannot bypass an unregistered manager's policy");
        foreach (string effect in new[] { "target-count", "source-count", "target-item", "source-item" })
        {
            a = Drop(); b = Drop();
            ItemDrop capturedSource = b;
            providers = ProviderArray(Provider("a#", (drop, data) =>
            {
                if (effect == "target-count") drop.m_itemData.m_stack++;
                if (effect == "source-count") data.m_stack++;
                if (effect == "target-item") drop.m_itemData = new ItemDrop.ItemData();
                if (effect == "source-item") capturedSource.m_itemData = new ItemDrop.ItemData();
                return new Dictionary<string, string>();
            }));
            Require(!Compose(a, b, providers), "foreign callback mutation cannot authorize stale merge: " + effect);
        }
    }

    private static void CheckInventoryDiscovery(List<MethodInfo> methods, string scenario)
    {
        MethodInfo inventory = AccessTools.DeclaredMethod(typeof(Inventory), "AddItem",
            new[] { typeof(ItemDrop.ItemData), typeof(int), typeof(int), typeof(int), typeof(bool) });
        Require(inventory != null, "original inventory stack target exists");
        var ground = new Harmony("flg.idm.test.removable-ground");
        Action restore = InstallInventoryDescriptors(methods, scenario == "inventory-partial", scenario == "inventory-owner");
        try
        {
            var dataPatches = new List<Patch>();
            Action discover = () => _adapter.GetMethod("AddInventoryProviders", All).Invoke(null, new object[] { dataPatches });
            if (scenario == "inventory-partial" || scenario == "inventory-owner")
            {
                Reject(discover, "incomplete or wrong-owner inventory provider is rejected: " + scenario);
                return;
            }
            if (scenario == "inventory-removed-ground")
            {
                foreach (MethodInfo method in methods) ground.Patch(_target, transpiler: new HarmonyMethod(method));
                dataPatches.AddRange(Harmony.GetPatchInfo(_target).Transpilers);
                discover();
                Require(dataPatches.Count == methods.Count, "ground and inventory routes identify one provider per copy");
                ground.UnpatchSelf(); dataPatches.Clear();
            }
            discover();
            Require(dataPatches.Count == methods.Count, "active complete inventory pairs recover every provider without ground patches");
            Array providers = Bind(dataPatches.ToArray());
            Require(providers.Length == methods.Count, "inventory descriptors bind the same live manager policies");
            CheckRealPolicies(providers, methods);
            discover();
            Require(dataPatches.Count == methods.Count, "repeated inventory discovery never duplicates providers");
            Require(!(Harmony.GetPatchInfo(_target)?.Transpilers.Any() ?? false), "discovery does not reinstall or remove foreign ground patches");
        }
        finally { restore(); ground.UnpatchSelf(); }
    }

    private static Action InstallInventoryDescriptors(List<MethodInfo> methods, bool partial = false, bool wrongOwner = false)
    {
        MethodInfo inventory = AccessTools.DeclaredMethod(typeof(Inventory), "AddItem",
            new[] { typeof(ItemDrop.ItemData), typeof(int), typeof(int), typeof(int), typeof(bool) });
        // Inventory.AddItem detour generation JITs Player/ZSyncAnimation native
        // initialization in this runtime. These four discovery cases therefore
        // use explicit Harmony descriptors, not an executable inventory wrapper.
        Type patchManager = typeof(Harmony).Assembly.GetType("HarmonyLib.Public.Patching.PatchManager", true);
        IDictionary patchInfos = (IDictionary)patchManager.GetField("PatchInfos", All).GetValue(null);
        object previous = patchInfos[inventory];
        var prefixPatches = new List<Patch>(); var postfixPatches = new List<Patch>();
            foreach (MethodInfo method in methods)
            {
                string ownerName = method.DeclaringType.Namespace.StartsWith("Vapok", StringComparison.Ordinal)
                    ? "vapok.common.ItemDataManager" : "org.bepinex.helpers.ItemDataManager";
                if (wrongOwner) ownerName = "flg.idm.test.wrong-owner";
                MethodInfo prefix = method.DeclaringType.GetMethod("CheckItemDataStackableAddItem", All);
                MethodInfo postfix = method.DeclaringType.GetMethod("ApplyCustomItemDataStackableAddItem", All);
                prefixPatches.Add(new Patch(new HarmonyMethod(prefix), prefixPatches.Count, ownerName));
                if (!partial) postfixPatches.Add(new Patch(new HarmonyMethod(postfix), postfixPatches.Count, ownerName));
            }
            Type patchInfo = typeof(Harmony).Assembly.GetType("HarmonyLib.PatchInfo", true);
            object fixture = Activator.CreateInstance(patchInfo);
            patchInfo.GetField("prefixes", All).SetValue(fixture, prefixPatches.ToArray());
            patchInfo.GetField("postfixes", All).SetValue(fixture, postfixPatches.ToArray());
            patchInfos[inventory] = fixture;
        return () =>
        {
            if (previous == null) patchInfos.Remove(inventory); else patchInfos[inventory] = previous;
        };
    }

    private static void CheckComposition(string directory, string fineDll, List<MethodInfo> methods, List<Harmony> managers, string scenario)
    {
        Assembly epic = Assembly.LoadFrom(Path.Combine(directory, "RandyKnapp-EpicLoot", "EpicLoot.dll"));
        MethodInfo epicPrefix = epic.GetType("EpicLoot.MagicItemData+ItemDrop_AutoStackItems_Patch", true).GetMethod("Prefix", All);
        MethodInfo ownTranspiler = _protection.GetMethod("Transpiler", All);
        _protection.GetField("_target", All).SetValue(null, _target);
        _protection.GetField("_transpiler", All).SetValue(null, ownTranspiler);
        var own = new Harmony(DropId); var external = new Harmony(EpicId);
        var fine = new Harmony(FineId); var entry = new Harmony(EntryId);
        Type fineBridge = null;
        Action installManagers = () => { for (int i = 0; i < methods.Count; i++) managers[i].Patch(_target, transpiler: new HarmonyMethod(methods[i])); };
        try
        {
            external.Patch(_target, prefix: new HarmonyMethod(epicPrefix));
            if (scenario == "first") installManagers();
            own.Patch(_target, transpiler: new HarmonyMethod(ownTranspiler) { priority = Priority.Last });
            if (scenario == "late") { EnsureFlg(); Require(true, "FLG initially binds an EpicLoot route without managers"); }
            if (scenario.StartsWith("fine-", StringComparison.Ordinal))
            {
                Assembly fixture = FineFixture(fineDll);
                fineBridge = fixture.GetType("FineDining.EpicLootStackCompatibility", true);
                MethodInfo transpiler = fixture.GetType("FineDining.ItemDropAutoStackSpoilagePatch", true).GetMethod("Transpiler", All);
                MethodInfo clock = fixture.GetType("FineDining.ItemDropClockReadinessPatch", true).GetMethod("Prefix", All);
                MethodInfo guard = fineBridge.GetMethod("BeforeAutoStack", All);
                if (scenario == "fine-first")
                {
                    fine.Patch(_target, transpiler: new HarmonyMethod(transpiler)); fine.CreateClassProcessor(clock.DeclaringType).Patch();
                    entry.Patch(_target, prefix: new HarmonyMethod(guard) { priority = Priority.First, before = new[] { EpicId } });
                    fineBridge.GetMethod("Ensure", All).Invoke(null, new object[] { Harmony.GetPatchInfo(_target) });
                    EnsureFlg(); installManagers();
                }
                else
                {
                    installManagers(); EnsureFlg();
                    fine.Patch(_target, transpiler: new HarmonyMethod(transpiler)); fine.CreateClassProcessor(clock.DeclaringType).Patch();
                    entry.Patch(_target, prefix: new HarmonyMethod(guard) { priority = Priority.First, before = new[] { EpicId } });
                }
                Reject(() => EnsureFlg(), "FLG refuses the unsupported unchanged FineDining plus manager route");
                Reject(() => fineBridge.GetMethod("Ensure", All).Invoke(null, new object[] { Harmony.GetPatchInfo(_target) }), "unchanged FineDining retains its independent compatibility refusal");
                Require(!(bool)guard.Invoke(null, new object[] { true }), "actual FineDining boundary blocks the unsupported route before item access");
                Require(methods.All(method => Harmony.GetPatchInfo(_target).Transpilers.Any(patch => patch.PatchMethod == method)), "unsupported combination leaves all foreign manager patches installed");
                return;
            }
            if (scenario != "first") installManagers();
            EnsureFlg();
            _protection.GetMethod("ValidateInstalledPatchCore", All).Invoke(null, new object[] { true });
            List<CodeInstruction> code = PatchProcessor.GetCurrentInstructions(epicPrefix);
            Require(code.Count(instruction => instruction.Calls(_filter)) == 1, "Epic replacement retains one FLG candidate filter");
            int callbacks = code.Count(instruction => instruction.operand is MethodInfo method && method.DeclaringType == _bridge && method.Name == "ComposeItemData");
            Require(callbacks == 1, "Epic replacement receives one independent ItemData composition call");
            Require(code.All(instruction => !(instruction.operand is MethodInfo method && method.Name == "ComposeGroundStackExpiry")), "FLG-only path does not add or own FineDining expiry");
            Require(methods.All(method => Harmony.GetPatchInfo(_target).Transpilers.Any(patch => patch.PatchMethod == method)), "all original manager transpilers remain registered");
            int queries = code.Count(instruction => instruction.operand is MethodInfo method && method.Name == "OverlapSphere");
            Require(queries == 1, "composition adds no physics lookup");
            MethodInfo guardMethod = _bridge.GetMethod("BeforeReplacement", All);
            Require((bool)guardMethod.Invoke(null, new object[] { true, true, false }), "actual replacement boundary admits a valid provider route");
            object[] nestedVeto = { false, true, true };
            Require(!(bool)guardMethod.Invoke(null, nestedVeto) && !(bool)nestedVeto[2], "nested Harmony veto is retained");
            object[] gameVeto = { true, false, true };
            Require(!(bool)guardMethod.Invoke(null, gameVeto) && !(bool)gameVeto[2], "original-game Harmony veto is retained");
            EnsureFlg();
            Require(PatchProcessor.GetCurrentInstructions(epicPrefix).Count(instruction => instruction.operand is MethodInfo method && method.DeclaringType == _bridge && method.Name == "ComposeItemData") == 1, "revalidation does not duplicate metadata merging");
            if (scenario == "removed-epic")
            {
                external.UnpatchSelf();
                object[] removedVeto = { true, true, true };
                Require(!(bool)guardMethod.Invoke(null, removedVeto) && !(bool)removedVeto[2], "removing EpicLoot during the active operation cancels its stale replacement body");
                Require(!(bool)_bridge.GetProperty("IsInstalled", All).GetValue(null, null), "operation boundary removes only the obsolete FLG replacement bridge");
                Require(_bridge.GetField("_dataFailure", All).GetValue(null) == null, "expected EpicLoot removal does not latch a policy failure");
                Require(methods.All(method => Harmony.GetPatchInfo(_target).Transpilers.Any(patch => patch.PatchMethod == method)), "runtime EpicLoot removal preserves every original manager patch");
                external.Patch(_target, prefix: new HarmonyMethod(epicPrefix)); EnsureFlg();
                Require((bool)guardMethod.Invoke(null, new object[] { true, true, false }), "a later valid EpicLoot installation can bind and accept operations again");
            }
            if (scenario == "last")
            {
                var changed = new Harmony("flg.idm.test.boundary-change");
                try
                {
                    changed.Patch(methods[0].DeclaringType.GetMethod("IsStackableItemDrop", All),
                        prefix: new HarmonyMethod(typeof(ItemDataStackChecks).GetMethod(nameof(Noop), All)));
                    var invoke = new DynamicMethod("flg_idm_blocked_epic_wrapper", typeof(bool), Type.EmptyTypes, _bridge.Module, true);
                    ILGenerator il = invoke.GetILGenerator(); il.Emit(OpCodes.Ldnull); il.Emit(OpCodes.Ldc_I4_1); il.Emit(OpCodes.Call, epicPrefix); il.Emit(OpCodes.Ret);
                    Require(!((Func<bool>)invoke.CreateDelegate(typeof(Func<bool>)))(), "actual detoured Epic callback refuses changed policy before any receiver/Unity access");
                    changed.UnpatchSelf();
                    Require(!(bool)guardMethod.Invoke(null, new object[] { true, true, false }), "operation-boundary failure stays latched until lifecycle reset");
                }
                finally { changed.UnpatchSelf(); }
            }
            _bridge.GetMethod("Shutdown", All).Invoke(null, null);
            Require(methods.All(method => Harmony.GetPatchInfo(_target).Transpilers.Any(patch => patch.PatchMethod == method)) && Harmony.GetPatchInfo(_target).Prefixes.Any(patch => patch.PatchMethod == epicPrefix), "FLG cleanup preserves EpicLoot and every manager owner");
        }
        finally
        {
            if (fineBridge != null) fineBridge.GetMethod("Shutdown", All).Invoke(null, null);
            _bridge.GetMethod("Shutdown", All).Invoke(null, null);
            own.UnpatchSelf(); external.UnpatchSelf(); fine.UnpatchSelf(); entry.UnpatchSelf();
        }
    }

    private static void CheckNewFineOwnership(string directory, string fineDll, List<MethodInfo> methods, List<Harmony> managers, string scenario)
    {
        Assembly epic = Assembly.LoadFrom(Path.Combine(directory, "RandyKnapp-EpicLoot", "EpicLoot.dll"));
        MethodInfo epicPrefix = epic.GetType("EpicLoot.MagicItemData+ItemDrop_AutoStackItems_Patch", true).GetMethod("Prefix", All);
        Assembly fineAssembly = FineFixture(fineDll);
        Type fineBridge = fineAssembly.GetType("FineDining.EpicLootStackCompatibility", true);
        MethodInfo fineData = fineBridge.GetMethod("ComposeItemData", All);
        Require(fineData != null && fineBridge.GetProperty("ItemDataMergeActive", All) != null, "selected FineDining exposes explicit metadata-ownership handshake");
        MethodInfo fineExpiry = fineAssembly.GetType("FineDining.DecayRuntime", true).GetMethod("ComposeGroundStackExpiry", All);
        MethodInfo originalFine = fineAssembly.GetType("FineDining.ItemDropAutoStackSpoilagePatch", true).GetMethod("Transpiler", All);
        MethodInfo fineClock = fineAssembly.GetType("FineDining.ItemDropClockReadinessPatch", true).GetMethod("Prefix", All);
        MethodInfo fineEntry = fineBridge.GetMethod("BeforeAutoStack", All);
        MethodInfo ownTranspiler = _protection.GetMethod("Transpiler", All);
        _protection.GetField("_target", All).SetValue(null, _target);
        _protection.GetField("_transpiler", All).SetValue(null, ownTranspiler);
        var own = new Harmony(DropId); var external = new Harmony(EpicId);
        var fine = new Harmony(FineId); var entry = new Harmony(EntryId);
        bool inventoryOnly = scenario == "newfine-inventory";
        Action restoreInventory = inventoryOnly ? InstallInventoryDescriptors(methods) : (Action)(() => { });
        Action installFine = () =>
        {
            fine.Patch(_target, transpiler: new HarmonyMethod(originalFine));
            fine.CreateClassProcessor(fineClock.DeclaringType).Patch();
            entry.Patch(_target, prefix: new HarmonyMethod(fineEntry) { priority = Priority.First, before = new[] { EpicId } });
        };
        Action ensureFine = () => fineBridge.GetMethod("Ensure", All).Invoke(null, new object[] { Harmony.GetPatchInfo(_target) });
        Action check = () =>
        {
            List<CodeInstruction> code = PatchProcessor.GetCurrentInstructions(epicPrefix);
            Require(code.Count(instruction => instruction.Calls(_filter)) == 1, "coordinated replacement retains FLG filter exactly once");
            Require(code.Count(instruction => instruction.Calls(fineData)) == 1 && code.Count(instruction => instruction.Calls(fineExpiry)) == 1,
                "FineDining owns one metadata merge and one expiry merge");
            Require(code.All(instruction => !(instruction.operand is MethodInfo method && method.DeclaringType == _bridge && method.Name == "ComposeItemData")),
                "FLG does not duplicate FineDining metadata ownership");
            int data = code.FindIndex(instruction => instruction.Calls(fineData));
            int expiry = code.FindIndex(instruction => instruction.Calls(fineExpiry));
            int amount = code.FindIndex(instruction => instruction.opcode == OpCodes.Stfld && instruction.operand is FieldInfo field && field.Name == "m_stack");
            Require(data < expiry && expiry < amount && code[data + 1].opcode == OpCodes.Brfalse,
                "metadata acceptance precedes expiry and quantity changes with a rejection branch");
            Require(Harmony.GetPatchInfo(epicPrefix).Prefixes.Count(patch => patch.PatchMethod.DeclaringType == _bridge && patch.PatchMethod.Name == "BeforeReplacement") == 1,
                "FLG retains exactly one transition guard while FineDining owns metadata merging");
            Require((bool)fineEntry.Invoke(null, new object[] { true }), "actual FineDining entry accepts composed ownership");
            Require(methods.All(method => Harmony.GetPatchInfo(_target).Transpilers.Any(patch => patch.PatchMethod == method) == !inventoryOnly),
                "joint operation preserves the original manager ground-patch presence or absence");
        };
        try
        {
            external.Patch(_target, prefix: new HarmonyMethod(epicPrefix));
            if (scenario == "newfine-late")
            {
                own.Patch(_target, transpiler: new HarmonyMethod(ownTranspiler) { priority = Priority.Last });
                installFine(); ensureFine(); EnsureFlg();
                Require(PatchProcessor.GetCurrentInstructions(epicPrefix).All(instruction => !instruction.Calls(fineData)),
                    "joint route initially needs no manager composition");
                foreach (var pair in methods.Select((method, index) => new { method, index }))
                    managers[pair.index].Patch(_target, transpiler: new HarmonyMethod(pair.method));
                Require((bool)fineEntry.Invoke(null, new object[] { true }),
                    "FineDining operation boundary discovers late managers without an external FLG Ensure call");
                check();
                foreach (Harmony manager in managers) manager.UnpatchSelf();
                Require((bool)fineEntry.Invoke(null, new object[] { true }),
                    "FineDining operation boundary observes removed providers without an external FLG Ensure call");
                List<CodeInstruction> noManagers = PatchProcessor.GetCurrentInstructions(epicPrefix);
                Require(noManagers.All(instruction => !instruction.Calls(fineData)) && noManagers.Count(instruction => instruction.Calls(fineExpiry)) == 1,
                    "provider removal removes metadata composition while retaining one expiry callback");
                Require(!(Harmony.GetPatchInfo(epicPrefix)?.Prefixes.Any(patch => patch.PatchMethod.DeclaringType == _bridge) ?? false),
                    "provider removal also removes the no-longer-needed FLG transition guard");
                return;
            }
            if (!inventoryOnly)
                foreach (var pair in methods.Select((method, index) => new { method, index })) managers[pair.index].Patch(_target, transpiler: new HarmonyMethod(pair.method));
            own.Patch(_target, transpiler: new HarmonyMethod(ownTranspiler) { priority = Priority.Last });
            if (scenario != "newfine-first")
            {
                EnsureFlg();
                Require(PatchProcessor.GetCurrentInstructions(epicPrefix).Count(instruction => instruction.operand is MethodInfo method && method.DeclaringType == _bridge && method.Name == "ComposeItemData") == 1,
                    "FLG initially owns its standalone provider merge");
            }
            if (scenario == "newfine-pending")
            {
                fine.Patch(_target, transpiler: new HarmonyMethod(originalFine));
                fine.CreateClassProcessor(fineClock.DeclaringType).Patch();
                _bridge.GetMethod("Ensure", All).Invoke(null, new object[] { Harmony.GetPatchInfo(_target), _filter, false });
                Require(true, "initial FLG binding tolerates FineDining Start still pending");
                Reject(() => EnsureFlg(), "actual dispensing requires the pending FineDining owner to be ready");
                object[] waiting = { true, true, true };
                Require(!(bool)_bridge.GetMethod("BeforeReplacement", All).Invoke(null, waiting) && !(bool)waiting[2],
                    "actual replacement guard pauses pending FineDining startup");
                entry.Patch(_target, prefix: new HarmonyMethod(fineEntry) { priority = Priority.First, before = new[] { EpicId } });
            }
            else installFine();
            ensureFine(); EnsureFlg(); check();
            Require((bool)_bridge.GetMethod("BeforeReplacement", All).Invoke(null, new object[] { true, true, false }),
                "ready FineDining cooperation resumes the transition guard without a permanent failure latch");
            ensureFine(); EnsureFlg(); check();
            ItemDrop receiver = Drop(), candidate = Drop();
            Require((bool)fineData.Invoke(null, new object[] { receiver, candidate }), "actual FineDining metadata wrapper admits ordinary data without executing expiry");
            if (scenario == "newfine-policies" || inventoryOnly)
            {
                Func<ItemDrop, ItemDrop, bool> merge = (a, b) => (bool)fineData.Invoke(null, new object[] { a, b });
                CheckRealPolicies(null, methods, merge);
                foreach (string[] keySpec in new[]
                {
                    new[] { "FineDining.SpoilageClock", "ExpiryDataKey" },
                    new[] { "FineDining.FreshnessRuntime", "AssignedLifetimeDataKey" },
                    new[] { "FineDining.SpoilageClock", "SpoiledDataKey" }
                })
                {
                    string key = (string)fineAssembly.GetType(keySpec[0], true).GetField(keySpec[1], All).GetRawConstantValue();
                    receiver = Drop(); candidate = Drop();
                    receiver.m_itemData.m_customData[key] = "receiver-value"; candidate.m_itemData.m_customData[key] = "source-value";
                    Require(merge(receiver, candidate), "FineDining delegates differing expiry-owned metadata: " + keySpec[1]);
                    Require(receiver.m_itemData.m_customData[key] == "receiver-value" && candidate.m_itemData.m_customData[key] == "source-value",
                        "metadata phase preserves both inputs for the later actual expiry phase: " + keySpec[1]);
                }
                receiver = Drop(); candidate = Drop();
                receiver.m_itemData.m_customData["foreign"] = "a"; candidate.m_itemData.m_customData["foreign"] = "b";
                Require(!merge(receiver, candidate), "FineDining rejects differing unowned metadata");
                receiver.m_itemData.m_customData.Clear(); candidate.m_itemData.m_customData.Clear();
                receiver.m_itemData.m_customData["missing.manager#policy"] = "same"; candidate.m_itemData.m_customData["missing.manager#policy"] = "same";
                Require(!merge(receiver, candidate), "FineDining refuses equal serialized values from an unknown manager policy");
            }
            if (scenario == "newfine-cleanup-fine")
            {
                fineBridge.GetMethod("Shutdown", All).Invoke(null, null); fine.UnpatchSelf(); entry.UnpatchSelf();
                Require(!(bool)_bridge.GetMethod("BeforeReplacement", All).Invoke(null, new object[] { true, true, false }),
                    "first operation after FineDining teardown rebinds but refuses to execute its stale wrapper");
                List<CodeInstruction> code = PatchProcessor.GetCurrentInstructions(epicPrefix);
                Require(code.Count(instruction => instruction.operand is MethodInfo method && method.DeclaringType == _bridge && method.Name == "ComposeItemData") == 1,
                    "FLG reacquires provider ownership after FineDining hooks are independently removed");
                Require(code.All(instruction => !instruction.Calls(fineData) && !instruction.Calls(fineExpiry)), "FineDining cleanup leaves no stale merge callbacks");
                Require((bool)_bridge.GetMethod("BeforeReplacement", All).Invoke(null, new object[] { true, true, false }),
                    "the next operation uses FLG's rebound metadata route");
            }
            if (scenario == "newfine-cleanup-flg")
            {
                _bridge.GetMethod("Shutdown", All).Invoke(null, null); own.UnpatchSelf(); ensureFine();
                List<CodeInstruction> code = PatchProcessor.GetCurrentInstructions(epicPrefix);
                Require(code.Count(instruction => instruction.Calls(fineData)) == 1 && code.Count(instruction => instruction.Calls(fineExpiry)) == 1 && code.All(instruction => !instruction.Calls(_filter)),
                    "FLG cleanup leaves FineDining metadata and expiry ownership intact");
            }
        }
        finally
        {
            restoreInventory();
            fineBridge.GetMethod("Shutdown", All).Invoke(null, null);
            _bridge.GetMethod("Shutdown", All).Invoke(null, null);
            own.UnpatchSelf(); external.UnpatchSelf(); fine.UnpatchSelf(); entry.UnpatchSelf();
        }
    }

    private static Assembly ManagerFixture(string path, string typeName, string scenario)
    {
        using (AssemblyDefinition source = AssemblyDefinition.ReadAssembly(path))
        using (var stream = new MemoryStream())
        {
            TypeDefinition type = source.MainModule.GetType(typeName);
            string policyBefore = string.Join("\n", type.Methods.Where(method => method.Name != ".cctor" && method.HasBody).Select(CecilBody));
            MethodDefinition initializer = type.Methods.Single(method => method.Name == ".cctor");
            int harmony = initializer.Body.Instructions.ToList().FindIndex(instruction => instruction.OpCode.Code == Mono.Cecil.Cil.Code.Newobj && instruction.Operand is MethodReference method && method.DeclaringType.FullName == "HarmonyLib.Harmony");
            Require(harmony > 0, "fixture found broad manager startup after initialized fields");
            int lastField = initializer.Body.Instructions.Take(harmony).ToList().FindLastIndex(instruction => instruction.OpCode.Code == Mono.Cecil.Cil.Code.Stsfld);
            Require(lastField >= 0, "fixture retains the original static field initialization");
            while (initializer.Body.Instructions.Count > lastField + 1) initializer.Body.Instructions.RemoveAt(lastField + 1);
            initializer.Body.ExceptionHandlers.Clear();
            initializer.Body.Instructions.Add(Mono.Cecil.Cil.Instruction.Create(Mono.Cecil.Cil.OpCodes.Ret));
            Require(policyBefore == string.Join("\n", type.Methods.Where(method => method.Name != ".cctor" && method.HasBody).Select(CecilBody)), "fixture retains all non-startup manager method bodies");
            if (scenario == "identity")
            {
                source.Name.Version = new System.Version(98, 76, 54, 32);
                foreach (MethodDefinition method in type.Methods.Where(method => method.Name != ".cctor" && method.HasBody))
                    method.Body.Instructions.Insert(0, Mono.Cecil.Cil.Instruction.Create(Mono.Cecil.Cil.OpCodes.Nop));
            }
            else if (scenario == "changed")
            {
                MethodDefinition transpiler = type.Methods.Single(method => method.Name == "HandleAutostackableItems");
                transpiler.Body.Instructions.First(instruction => instruction.OpCode.Code == Mono.Cecil.Cil.Code.Ldstr && (string)instruction.Operand == "IsStackableItemDrop").Operand = "UnrecognizedStackPolicy";
            }
            source.MainModule.Mvid = Guid.NewGuid(); source.Write(stream); return Assembly.Load(stream.ToArray());
        }
    }

    private static Assembly FineFixture(string path)
    {
        using (AssemblyDefinition source = AssemblyDefinition.ReadAssembly(path))
        using (var stream = new MemoryStream())
        {
            MethodDefinition initializer = source.MainModule.GetType("FineDining.FineDiningPlugin").Methods.Single(method => method.Name == ".cctor");
            initializer.Body.Instructions.Clear(); initializer.Body.Variables.Clear(); initializer.Body.ExceptionHandlers.Clear();
            initializer.Body.Instructions.Add(Mono.Cecil.Cil.Instruction.Create(Mono.Cecil.Cil.OpCodes.Ret));
            source.MainModule.Mvid = Guid.NewGuid(); source.Write(stream); return Assembly.Load(stream.ToArray());
        }
    }
    private static string CecilBody(MethodDefinition method) => string.Join("\n", method.Body.Instructions.Select(instruction => instruction.OpCode.Code + " " + instruction.Operand));
    private static object AddData(Type infoType, ItemDrop.ItemData item, string key, string value, bool allow)
    {
        string prefix = infoType.Namespace;
        Type extensions = infoType.Assembly.GetType(prefix + ".ItemExtensions", true);
        object info = extensions.GetMethod("Data", All, null, new[] { typeof(ItemDrop.ItemData) }, null).Invoke(null, new object[] { item });
        Type dataType = infoType.Assembly.GetType(prefix + ".StringItemData", true);
        object data = infoType.GetMethods(All).Single(method => method.Name == "Add" && method.IsGenericMethodDefinition).MakeGenericMethod(dataType).Invoke(info, new object[] { key });
        dataType.GetProperty("Value", All).SetValue(data, value, null); SetAllow(data, allow); return data;
    }
    private static void SetAllow(object data, bool allow) => data.GetType().BaseType.GetProperty("AllowStackingIdenticalValues", All).SetValue(data, allow, null);
    private static ItemDrop Drop()
    {
        var drop = (ItemDrop)FormatterServices.GetUninitializedObject(typeof(ItemDrop));
        drop.m_itemData = new ItemDrop.ItemData { m_customData = new Dictionary<string, string>(), m_stack = 2 };
        // A different source count is set explicitly by callers where required.
        return drop;
    }
    private static Array Bind() => Bind(Harmony.GetPatchInfo(_target).Transpilers.ToArray());
    private static Array Bind(Patch[] patches) => (Array)_adapter.GetMethod("Bind", All).Invoke(null, new object[] { patches });
    private static bool Compose(ItemDrop receiver, ItemDrop candidate, Array providers) => (bool)_adapter.GetMethod("TryCompose", All).Invoke(null, new object[] { receiver, candidate, providers });
    private static object Provider(string prefix, Func<ItemDrop, ItemDrop.ItemData, Dictionary<string, string>> evaluate)
    {
        object provider = FormatterServices.GetUninitializedObject(_providerType);
        Set(provider, "Prefix", prefix); Set(provider, "Evaluate", evaluate); Set(provider, "Methods", Array.Empty<MethodInfo>()); return provider;
    }
    private static Array ProviderArray(params object[] providers)
    {
        Array array = Array.CreateInstance(_providerType, providers.Length);
        for (int i = 0; i < providers.Length; i++) array.SetValue(providers[i], i);
        return array;
    }
    private static object Field(object target, string name) => _providerType.GetField(name, All).GetValue(target);
    private static void Set(object target, string name, object value) => _providerType.GetField(name, All).SetValue(target, value);
    private static void EnsureFlg() => _bridge.GetMethod("Ensure", All).Invoke(null, new object[] { Harmony.GetPatchInfo(_target), _filter, true });
    private static void Noop() { }
    private static void Reject(Action action, string message)
    {
        try { action(); }
        catch (TargetInvocationException error) when (error.GetBaseException() is InvalidOperationException || error.GetBaseException() is MissingMemberException) { _passed++; return; }
        throw new Exception("Expected rejection: " + message);
    }
    private static void Require(bool condition, string message) { if (!condition) throw new Exception(message); _passed++; }
    private static int Finish(string scenario)
    {
        System.Console.WriteLine("PASS: " + _passed + " ItemData manager composition checks: " + scenario);
        System.Console.WriteLine("Scope: actual manager/FLG methods with startup-only fixtures, managed ItemData and uninitialized ItemDrop shells; actual Harmony composition. No Unity startup/native merge, ownership, persistence, expiry execution or multiplayer.");
        if (scenario.StartsWith("inventory", StringComparison.Ordinal) || scenario == "newfine-inventory")
            System.Console.WriteLine("Inventory discovery uses explicit Harmony PatchInfo descriptors; Inventory.AddItem is not detoured or executed because its JIT requires native Unity initialization.");
        return 0;
    }
}
