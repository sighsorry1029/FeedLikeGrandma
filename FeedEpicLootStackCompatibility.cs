using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Security.Cryptography;
using System.Text;
using HarmonyLib;
using UnityEngine;

namespace FeedLikeGrandma;

// FLG owns the world-drop marker boundary and delegates ItemDataManager policy
// to each active provider. FineDining independently owns expiry; never repeat it.
internal static class FeedEpicLootStackCompatibility
{
    private const string HarmonyId = "sighsorry.FeedLikeGrandma.EpicLootStacks";
    private const string EpicOwner = "randyknapp.mods.epicloot";
    private const string FineOwner = "sighsorry.FineDining";
    private const string FineBridgeOwner = "sighsorry.FineDining.EpicLootStacks";
    private const string FineEntryOwner = FineBridgeOwner + ".Entry";
    private const string DropOwner = "sighsorry.FeedLikeGrandma.FeedBarrelDrops";
    private const BindingFlags Declared = BindingFlags.Static | BindingFlags.Instance |
        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
    private static readonly MethodInfo TranspilerMethod = typeof(FeedEpicLootStackCompatibility).GetMethod(nameof(Transpiler), Declared)!;
    private static readonly MethodInfo DataEntryMethod = typeof(FeedEpicLootStackCompatibility).GetMethod(nameof(BeforeReplacement), Declared)!;
    private static readonly MethodInfo DataComposeMethod = typeof(FeedEpicLootStackCompatibility).GetMethod(nameof(ComposeItemData), Declared)!;
    private static readonly MethodInfo OriginalTarget = AccessTools.DeclaredMethod(typeof(ItemDrop), "AutoStackItems", Type.EmptyTypes);
    private static Harmony? _harmony;
    private static MethodInfo? _prefix, _filter, _fineOriginal, _fineTranspiler, _fineEntry, _fineClock, _compose, _fineDataCompose, _fineDataActive;
    private static MethodInfo[] _epicHelpers = Array.Empty<MethodInfo>();
    private static bool _composed, _validatedExpiry, _validatedFineData;
    private static Func<bool>? _fineDataIsActive;
    private static FeedItemDataStackCompatibility.Provider[] _providers = Array.Empty<FeedItemDataStackCompatibility.Provider>();
    private static Patch[] _dataPatches = Array.Empty<Patch>();
    private static bool _dataGuardInstalled;
    private static bool _delegateItemData;
    private static int _routeRevision;
    private static string? _dataFailure;
    private static bool OwnsItemData => _providers.Length != 0 && !_delegateItemData;
    internal static bool IsInstalled => _harmony != null;

    // Only relevant method bodies are fingerprinted. DLL versions/MVIDs/tokens,
    // nops and branch offsets/sizes are not activation gates.
    private const string PrefixContract = "09BDA7EC447CECFF365BBAAAEECEE508B9E698A57123D74D4D16302FEEA9E8ED";
    private const string StacksContract = "B6C5B4EADEB0350D1986322450BFC704A185049CDE27F5D9F5593A9F255CA982";
    private const string RawContract = "D1499ABD5712E108C56D0A6659725797894024B7ADFC3FDF1B9C46E11355979C";

    // Start may run before FineDining.Start; only that initialization call allows
    // its bridge to be pending. Every dispense requires the complete active route.
    internal static bool Ensure(Patches patches, MethodInfo filter, bool requireExpiry = true)
    {
        Patch? epic = patches.Prefixes.SingleOrDefault(patch => patch.owner == EpicOwner);
        Patch? entry = patches.Prefixes.SingleOrDefault(patch => patch.owner == FineEntryOwner);
        MethodInfo? fine = patches.Transpilers.SingleOrDefault(patch => patch.owner == FineOwner)?.PatchMethod;
        if (patches.Prefixes.Count == 0)
        {
            // Older FineDining has only a vanilla transpiler; it needs no EpicLoot
            // bridge. A newer build declaring the clock guard must install it.
            Require(fine?.DeclaringType?.Assembly.GetType("FineDining.ItemDropClockReadinessPatch") == null,
                "missing FineDining clock guard");
            if (_prefix != null) Shutdown();
            return false;
        }
        RefreshFineBinding(fine);
        Patch[] clocks = patches.Prefixes.Where(patch => _fineClock != null && patch.PatchMethod == _fineClock).ToArray();
        Require(clocks.Length == (_fineClock == null ? 0 : 1), "missing or duplicated FineDining clock guard");
        if (clocks.Length != 0) ValidateFineClock(clocks[0], patches);
        Require(patches.Prefixes.Count == (epic == null ? 0 : 1) + (entry == null ? 0 : 1) + clocks.Length,
            "unrecognized AutoStackItems prefix");
        if (entry != null) ValidateFineEntry(entry);
        if (epic == null)
        {
            if (_prefix != null) Shutdown();
            return entry != null || clocks.Length != 0;
        }
        Require(_dataFailure == null, _dataFailure ?? "ItemDataManager replacement guard failed");
        Require(!epic.wrapTryCatch, "EpicLoot prefix exception policy changed");
        Require(entry == null || !epic.before.Contains(FineEntryOwner), "EpicLoot conflicts with the FineDining entry order");
        MethodInfo prefix = epic.PatchMethod;
        if (_prefix == null)
        {
            Require(prefix.DeclaringType?.FullName == "EpicLoot.MagicItemData+ItemDrop_AutoStackItems_Patch" &&
                prefix.Name == "Prefix" && prefix.IsPrivate && HasSignature(prefix, typeof(bool), typeof(ItemDrop), typeof(bool)),
                "EpicLoot replacement signature/access");
            ParameterInfo[] parameters = prefix.GetParameters();
            Require(parameters[0].Name == "__instance" && parameters[1].Name == "__runOriginal", "EpicLoot Harmony parameter binding");
            Require(!prefix.GetCustomAttributes(typeof(HarmonyArgument), false).Any() &&
                !prefix.DeclaringType!.GetCustomAttributes(typeof(HarmonyArgument), false).Any() &&
                parameters.All(parameter => !parameter.GetCustomAttributes(typeof(HarmonyArgument), false).Any()), "EpicLoot argument remapping");
        }
        Require(patches.Postfixes.Count == 0 && patches.Finalizers.Count == 0 && patches.ILManipulators.Count == 0,
            "uninspected AutoStackItems wrapper");
        var dataPatches = new List<Patch>();
        foreach (Patch patch in patches.Transpilers)
        {
            if (patch.owner == DropOwner && patch.PatchMethod.DeclaringType == typeof(FeedBarrelDropProtection) &&
                patch.PatchMethod.Name == "Transpiler") continue;
            if (patch.owner == FineOwner && patch.PatchMethod == fine && !patch.wrapTryCatch) continue;
            dataPatches.Add(patch);
        }
        FeedItemDataStackCompatibility.AddInventoryProviders(dataPatches);
        // Old FineDining independently refuses this route. A cooperating build
        // declares and validates its own merge callback; FLG then only filters.
        Require(dataPatches.Count == 0 || fine == null || _fineDataCompose != null,
            "EpicLoot with ItemDataManager also needs a FineDining-side compatibility update; its safety checks are retained");
        bool dataChanged = dataPatches.Count != _dataPatches.Length ||
            dataPatches.Where((patch, index) => index >= _dataPatches.Length || !SamePatch(patch, _dataPatches[index])).Any();
        FeedItemDataStackCompatibility.Provider[] providers = dataChanged
            ? FeedItemDataStackCompatibility.Bind(dataPatches) : _providers;
        bool delegateItemData = _fineDataCompose != null;
        if (fine != null && requireExpiry) Require(entry != null, "FineDining expiry guard is not ready; update both mods and restart");
        if (_prefix != null)
        {
            Require(_prefix == prefix && _filter == filter, "active EpicLoot route changed; restart to rebind");
            if (dataChanged || _delegateItemData != delegateItemData)
            {
                _providers = providers; _dataPatches = dataPatches.ToArray(); _composed = false;
                _delegateItemData = delegateItemData;
                _routeRevision++;
                bool needsGuard = providers.Length != 0;
                if (needsGuard != _dataGuardInstalled)
                {
                    _dataGuardInstalled = needsGuard;
                    if (needsGuard) _harmony!.Patch(prefix, prefix: new HarmonyMethod(DataEntryMethod) { priority = Priority.First });
                    else _harmony!.Unpatch(prefix, DataEntryMethod);
                }
                else _harmony!.Patch(prefix);
            }
            ValidateInstalled(requireExpiry);
            return true;
        }
        MethodInfo[] helpers = ValidateEpicContract(prefix);
        foreach (MethodInfo method in helpers) RequireUnpatched(method);
        Require(filter == typeof(FeedBarrelDropProtection).GetMethod("FilterStackCandidate", Declared), "FLG candidate filter binding");
        ValidateCallbackPatches(Harmony.GetPatchInfo(prefix), requireOwn: false, requireExpiry: false);
        _prefix = prefix; _filter = filter; _epicHelpers = helpers;
        _providers = providers; _dataPatches = dataPatches.ToArray(); _delegateItemData = delegateItemData;
        _dataGuardInstalled = providers.Length != 0;
        try
        {
            _harmony = new Harmony(HarmonyId);
            _routeRevision++;
            _harmony.Patch(prefix,
                prefix: _dataGuardInstalled ? new HarmonyMethod(DataEntryMethod) { priority = Priority.First } : null,
                transpiler: new HarmonyMethod(TranspilerMethod)
                { priority = Priority.Last, before = new[] { FineBridgeOwner } });
            ValidateInstalled(requireExpiry);
        }
        catch { Shutdown(); throw; }
        return true;
    }

    // Once the provider route is active, check it at each actual stack operation,
    // including operations with no barrel nearby. Reflection/IL binding is cached.
    private static bool BeforeReplacement(bool __runOriginal, bool __1, ref bool __result)
    {
        // __1 is EpicLoot's original-game veto argument; __runOriginal belongs
        // to this nested Harmony wrapper. Neither veto may be overridden.
        if (!__runOriginal || !__1 || _dataFailure != null) { __result = false; return false; }
        try
        {
            int revision = _routeRevision;
            Patches patches = Harmony.GetPatchInfo(OriginalTarget);
            Ensure(patches, _filter!, requireExpiry: false);
            // Rebinding here cannot replace the remainder of this already-running
            // wrapper. Skip this call; the next one enters the new guarded body.
            if (revision != _routeRevision || (_fineOriginal != null &&
                (!patches.Prefixes.Any(patch => patch.owner == FineEntryOwner && patch.PatchMethod == _fineEntry) ||
                 !_validatedExpiry || (_delegateItemData && _providers.Length != 0 && !_validatedFineData))))
            { __result = false; return false; }
            return true;
        }
        catch (Exception error)
        {
            _dataFailure = error.GetBaseException().Message;
            try { FeedLikeGrandmaPlugin.Log.LogWarning("EpicLoot ItemDataManager ground stacking paused before item changes. " + _dataFailure); }
            catch (Exception) { }
            __result = false;
            return false;
        }
    }

    private static bool ComposeItemData(ItemDrop receiver, ItemDrop candidate)
        => _dataFailure == null && OwnsItemData && FeedItemDataStackCompatibility.TryCompose(receiver, candidate, _providers);

    private static bool SamePatch(Patch left, Patch right) => left.index == right.index && left.owner == right.owner &&
        left.PatchMethod == right.PatchMethod && left.priority == right.priority && left.wrapTryCatch == right.wrapTryCatch &&
        left.before.SequenceEqual(right.before) && left.after.SequenceEqual(right.after);

    // FineDining calls this after installing its operation-boundary guard. FLG
    // removes only its data call. Its entry guard stays while providers exist,
    // so a later FineDining teardown cannot leave ordinary stacks unguarded.
    internal static void YieldItemDataOwnership()
    {
        if (_prefix == null) return;
        Patches patches = Harmony.GetPatchInfo(OriginalTarget);
        MethodInfo? fine = patches.Transpilers.SingleOrDefault(patch => patch.owner == FineOwner)?.PatchMethod;
        RefreshFineBinding(fine);
        Require(_fineDataCompose != null && patches.Prefixes.Any(patch => patch.owner == FineEntryOwner && patch.PatchMethod == _fineEntry),
            "FineDining must install its entry guard before taking ItemDataManager ownership");
        // Also synchronize late-added/removed providers while already delegated.
        // This does not call FineDining.Ensure, so handoff cannot recurse.
        Ensure(patches, _filter!, requireExpiry: false);
    }

    private static void RefreshFineBinding(MethodInfo? fine)
    {
        if (_fineOriginal == fine) return;
        _composed = false;
        _fineOriginal = null; _fineTranspiler = null; _fineEntry = null; _fineClock = null; _compose = null;
        _fineDataCompose = null; _fineDataActive = null; _fineDataIsActive = null;
        if (fine == null) return;
        Require(fine.DeclaringType?.FullName == "FineDining.ItemDropAutoStackSpoilagePatch" && fine.Name == "Transpiler" &&
            HasSignature(fine, typeof(IEnumerable<CodeInstruction>), typeof(IEnumerable<CodeInstruction>)), "FineDining original stack patch");
        Type? bridge = fine.DeclaringType!.Assembly.GetType("FineDining.EpicLootStackCompatibility");
        Require(bridge != null, "FineDining must provide its own EpicLoot expiry bridge; update both mods");
        _fineTranspiler = Method(bridge!, "Transpiler", typeof(IEnumerable<CodeInstruction>), typeof(IEnumerable<CodeInstruction>));
        _fineEntry = Method(bridge!, "BeforeAutoStack", typeof(bool), typeof(bool));
        Require(_fineEntry.GetParameters()[0].Name == "__runOriginal", "FineDining entry binding");
        if (bridge!.GetMethod("ComposeItemData", Declared) != null)
        {
            _fineDataCompose = Method(bridge, "ComposeItemData", typeof(bool), typeof(ItemDrop), typeof(ItemDrop));
            _fineDataActive = Method(bridge, "get_ItemDataMergeActive", typeof(bool));
            Require(_fineDataCompose.IsPrivate && _fineDataActive.IsAssembly, "FineDining data bridge access");
            _fineDataIsActive = (Func<bool>)Delegate.CreateDelegate(typeof(Func<bool>), _fineDataActive);
        }
        Type? clock = fine.DeclaringType.Assembly.GetType("FineDining.ItemDropClockReadinessPatch");
        if (clock != null)
        {
            _fineClock = Method(clock, "Prefix", typeof(bool), typeof(ItemDrop));
            Require(_fineClock.IsAssembly && _fineClock.GetParameters()[0].Name == "__instance" &&
                !_fineClock.GetCustomAttributes(typeof(HarmonyArgument), false).Any() &&
                !clock.GetCustomAttributes(typeof(HarmonyArgument), false).Any() &&
                !_fineClock.GetParameters()[0].GetCustomAttributes(typeof(HarmonyArgument), false).Any(),
                "FineDining clock guard binding");
        }
        Type runtime = fine.DeclaringType.Assembly.GetType("FineDining.DecayRuntime", true)!;
        _compose = runtime.GetMethod("ComposeGroundStackExpiry", Declared, null, new[] { typeof(ItemDrop), typeof(ItemDrop) }, null);
        Require(_compose != null && _compose.IsStatic && _compose.IsAssembly && _compose.ReturnType == typeof(void) &&
            !_compose.ContainsGenericParameters, "FineDining expiry callback");
        _fineOriginal = fine;
    }

    private static void ValidateFineEntry(Patch entry)
    {
        Require(_fineEntry != null && entry.PatchMethod == _fineEntry && !entry.wrapTryCatch &&
            entry.before.SequenceEqual(new[] { EpicOwner }) && entry.after.Length == 0 &&
            entry.priority == Priority.First, "FineDining entry guard/order changed");
        RequireUnpatched(_fineEntry!);
    }

    private static void ValidateFineClock(Patch clock, Patches patches)
    {
        Require(clock.owner == FineOwner && !clock.wrapTryCatch && clock.priority == Priority.First &&
            clock.before.SequenceEqual(new[] { FineEntryOwner, EpicOwner }) && clock.after.Length == 0,
            "FineDining clock guard/order changed");
        RequireUnpatched(_fineClock!);
        Require(PatchProcessor.GetSortedPatchMethods(AccessTools.DeclaredMethod(typeof(ItemDrop), "AutoStackItems"),
            patches.Prefixes.ToArray()).FirstOrDefault() == _fineClock, "FineDining clock guard must precede replacement work");
    }

    internal static MethodInfo[] ValidateEpicContract(MethodInfo prefix)
    {
        Require(HasSignature(prefix, typeof(bool), typeof(ItemDrop), typeof(bool)), "EpicLoot prefix signature");
        Type owner = prefix.DeclaringType?.DeclaringType ?? throw new InvalidOperationException("EpicLoot prefix owner missing.");
        Require(owner.FullName == "EpicLoot.MagicItemData", "EpicLoot data owner");
        MethodInfo stacks = Method(owner, "StacksWith", typeof(bool), typeof(ItemDrop.ItemData), typeof(ItemDrop.ItemData));
        MethodInfo raw = Method(owner, "RawOf", typeof(string), typeof(ItemDrop.ItemData));
        Require(stacks.IsAssembly && raw.IsPublic, "EpicLoot helper access");
        Match(prefix, PrefixContract); Match(stacks, StacksContract); Match(raw, RawContract);
        return new[] { stacks, raw };
    }

    private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        _composed = false;
        List<CodeInstruction> code = instructions.ToList();
        // FLG runs first. FineDining then adds its own expiry callback to this
        // guarded body; neither owner duplicates or takes over the other's work.
        try { if (Fingerprint(code) != PrefixContract || _filter == null) return code; }
        catch (Exception) { return code; }
        int lookup = code.FindIndex(IsCandidateLookup);
        if (OwnsItemData)
        {
            // The inspected Epic body checks capacity and magic compatibility
            // before this increment. A refusal takes its existing candidate skip.
            int store = code.FindIndex(instruction => instruction.opcode == OpCodes.Stfld && instruction.operand is FieldInfo field &&
                field.DeclaringType == typeof(ItemDrop.ItemData) && field.Name == "m_stack");
            int start = store - 7;
            Label? skip = null;
            Require(start > lookup && code[start - 1].Branches(out skip) && skip.HasValue,
                "ItemDataManager merge insertion point");
            var receiver = new CodeInstruction(OpCodes.Ldarg_0);
            receiver.labels.AddRange(code[start].labels); code[start].labels.Clear();
            code.InsertRange(start, new[] { receiver, new CodeInstruction(OpCodes.Ldloc, LocalIndex(code[lookup + 1])),
                new CodeInstruction(OpCodes.Call, DataComposeMethod), new CodeInstruction(OpCodes.Brfalse, skip!.Value) });
        }
        code.InsertRange(lookup + 1, new[] { new CodeInstruction(OpCodes.Ldarg_0), new CodeInstruction(OpCodes.Call, _filter) });
        return code;
    }

    private static bool ValidateCallbackPatches(Patches? info, bool requireOwn, bool requireExpiry)
    {
        if (info == null)
        {
            Require(!requireOwn && (!requireExpiry || _fineOriginal == null), "missing EpicLoot replacement hooks");
            return false;
        }
        int expectedGuard = requireOwn && _dataGuardInstalled ? 1 : 0;
        Require(info.Prefixes.Count == expectedGuard && info.Prefixes.All(patch =>
                patch.owner == HarmonyId && patch.PatchMethod == DataEntryMethod && !patch.wrapTryCatch &&
                patch.priority == Priority.First && patch.before.Length == 0 && patch.after.Length == 0) &&
            info.Postfixes.Count == 0 && info.Finalizers.Count == 0 && info.ILManipulators.Count == 0,
            "uninspected EpicLoot replacement wrapper");
        int own = 0, expiry = 0;
        foreach (Patch patch in info.Transpilers)
        {
            Require(!patch.wrapTryCatch, "replacement transpiler exception policy changed");
            if (patch.owner == HarmonyId && patch.PatchMethod == TranspilerMethod && patch.before.Contains(FineBridgeOwner)) own++;
            else if (_fineTranspiler != null && patch.owner == FineBridgeOwner && patch.PatchMethod == _fineTranspiler &&
                patch.after.Contains(HarmonyId)) expiry++;
            else throw new InvalidOperationException("Unsupported EpicLoot Feed Barrel contract: uninspected replacement transpiler");
        }
        Require(own == (requireOwn ? 1 : 0) && expiry <= 1, "missing or duplicated replacement guard");
        if (requireExpiry && _fineOriginal != null) Require(expiry == 1, "FineDining expiry bridge is not ready");
        if (_fineOriginal != null)
        {
            RequireUnpatched(_fineTranspiler!); RequireUnpatched(_compose!);
            if (_fineDataCompose != null) { RequireUnpatched(_fineDataCompose); RequireUnpatched(_fineDataActive!); }
        }
        return expiry == 1;
    }

    private static void ValidateInstalled(bool requireExpiry)
    {
        bool expiry = ValidateCallbackPatches(Harmony.GetPatchInfo(_prefix!), requireOwn: true, requireExpiry: requireExpiry);
        foreach (MethodInfo method in _epicHelpers) RequireUnpatched(method);
        FeedItemDataStackCompatibility.Validate(_providers);
        if (_dataGuardInstalled) RequireUnpatched(DataEntryMethod);
        if (OwnsItemData) RequireUnpatched(DataComposeMethod);
        bool fineData = _fineDataIsActive?.Invoke() ?? false;
        Require(!fineData || _delegateItemData, "duplicate ItemDataManager merge owners");
        Require(_providers.Length == 0 || !_delegateItemData || !requireExpiry || (fineData && expiry),
            "FineDining ItemDataManager bridge is not ready");
        // Its transpiler can be rebuilt without changing the descriptor list.
        // Query the cheap ownership state as well before reusing composed IL.
        if (_composed && _validatedExpiry == expiry && _validatedFineData == fineData) return;
        List<CodeInstruction> code = PatchProcessor.GetCurrentInstructions(_prefix!);
        int lookup = code.FindIndex(IsCandidateLookup);
        Require(lookup >= 0 && code.Count(instruction => instruction.Calls(_filter!)) == 1 &&
            code[lookup + 1].opcode == OpCodes.Ldarg_0 && code[lookup + 2].Calls(_filter!), "EpicLoot candidate guard absent or moved");
        RequireNoEntry(code[lookup + 1]); RequireNoEntry(code[lookup + 2]);
        code.RemoveRange(lookup + 1, 2);
        if (OwnsItemData)
        {
            int call = code.FindIndex(instruction => instruction.Calls(DataComposeMethod));
            int store = code.FindIndex(instruction => instruction.opcode == OpCodes.Stfld && instruction.operand is FieldInfo field &&
                field.DeclaringType == typeof(ItemDrop.ItemData) && field.Name == "m_stack");
            Require(!expiry && call >= 3 && call == store - 9 &&
                code.Count(instruction => instruction.Calls(DataComposeMethod)) == 1 &&
                code[call - 2].opcode == OpCodes.Ldarg_0 && code[call - 1].IsLdloc() &&
                LocalIndex(code[call - 1]) == LocalIndex(code[lookup + 1]) &&
                code[call + 1].opcode == OpCodes.Brfalse && code[call - 3].Branches(out Label? skip) &&
                skip.HasValue && Equals(code[call + 1].operand, skip.Value), "ItemDataManager guard missing, duplicated or moved");
            RequireNoEntry(code[call - 1]); RequireNoEntry(code[call]); RequireNoEntry(code[call + 1]);
            Require(code[call - 2].blocks.Count == 0, "ItemDataManager guard exception entry");
            code[call + 2].labels.AddRange(code[call - 2].labels);
            code.RemoveRange(call - 2, 4);
        }
        if (expiry)
        {
            int call = code.FindIndex(instruction => instruction.Calls(_compose!));
            int store = code.FindIndex(instruction => instruction.opcode == OpCodes.Stfld && instruction.operand is FieldInfo field &&
                field.DeclaringType == typeof(ItemDrop.ItemData) && field.Name == "m_stack");
            Require(call >= 2 && code.Count(instruction => instruction.Calls(_compose!)) == 1 && call == store - 8 &&
                code[call - 2].opcode == OpCodes.Ldarg_0 && code[call - 1].IsLdloc() &&
                LocalIndex(code[call - 1]) == LocalIndex(code[lookup + 1]), "FineDining expiry missing, duplicated or misplaced");
            RequireNoEntry(code[call - 1]); RequireNoEntry(code[call]);
            Require(code[call - 2].blocks.Count == 0, "FineDining expiry exception entry");
            code[call + 1].labels.AddRange(code[call - 2].labels);
            code.RemoveRange(call - 2, 3);
        }
        if (fineData)
        {
            int call = code.FindIndex(instruction => instruction.Calls(_fineDataCompose!));
            int store = code.FindIndex(instruction => instruction.opcode == OpCodes.Stfld && instruction.operand is FieldInfo field &&
                field.DeclaringType == typeof(ItemDrop.ItemData) && field.Name == "m_stack");
            Require(call >= 3 && call == store - 9 && code.Count(instruction => instruction.Calls(_fineDataCompose!)) == 1 &&
                code[call - 2].opcode == OpCodes.Ldarg_0 && code[call - 1].IsLdloc() &&
                LocalIndex(code[call - 1]) == LocalIndex(code[lookup + 1]) &&
                code[call + 1].opcode == OpCodes.Brfalse && code[call - 3].Branches(out Label? skip) &&
                skip.HasValue && Equals(code[call + 1].operand, skip.Value), "FineDining ItemDataManager guard missing, duplicated or moved");
            RequireNoEntry(code[call - 1]); RequireNoEntry(code[call]); RequireNoEntry(code[call + 1]);
            Require(code[call - 2].blocks.Count == 0, "FineDining ItemDataManager exception entry");
            code[call + 2].labels.AddRange(code[call - 2].labels);
            code.RemoveRange(call - 2, 4);
        }
        Require(Fingerprint(code) == PrefixContract, "EpicLoot composed replacement flow changed");
        _composed = true; _validatedExpiry = expiry; _validatedFineData = fineData;
    }

    internal static void Shutdown()
    {
        if (_harmony != null) { _routeRevision++; _harmony.UnpatchSelf(); }
        _harmony = null; _prefix = null; _filter = null; _fineOriginal = null;
        _fineTranspiler = null; _fineEntry = null; _fineClock = null; _compose = null; _fineDataCompose = null; _fineDataActive = null; _fineDataIsActive = null;
        _epicHelpers = Array.Empty<MethodInfo>(); _composed = false;
        _providers = Array.Empty<FeedItemDataStackCompatibility.Provider>(); _dataPatches = Array.Empty<Patch>();
        _dataGuardInstalled = false; _delegateItemData = false; _dataFailure = null; _validatedFineData = false;
    }

    private static bool IsCandidateLookup(CodeInstruction instruction) => instruction.operand is MethodInfo method &&
        (instruction.opcode == OpCodes.Call || instruction.opcode == OpCodes.Callvirt) && method.DeclaringType == typeof(Component) &&
        method.Name == "GetComponent" && method.IsGenericMethod && method.GetGenericArguments().SequenceEqual(new[] { typeof(ItemDrop) });
    private static int LocalIndex(CodeInstruction instruction)
    {
        if (instruction.opcode == OpCodes.Stloc_0 || instruction.opcode == OpCodes.Ldloc_0) return 0;
        if (instruction.opcode == OpCodes.Stloc_1 || instruction.opcode == OpCodes.Ldloc_1) return 1;
        if (instruction.opcode == OpCodes.Stloc_2 || instruction.opcode == OpCodes.Ldloc_2) return 2;
        if (instruction.opcode == OpCodes.Stloc_3 || instruction.opcode == OpCodes.Ldloc_3) return 3;
        return instruction.operand is LocalVariableInfo local ? local.LocalIndex : Convert.ToInt32(instruction.operand, CultureInfo.InvariantCulture);
    }
    private static bool HasSignature(MethodInfo method, Type result, params Type[] args) => method.IsStatic &&
        method.ReturnType == result && !method.ContainsGenericParameters && method.GetMethodBody() != null &&
        method.GetParameters().Select(parameter => parameter.ParameterType).SequenceEqual(args);
    private static MethodInfo Method(Type type, string name, Type result, params Type[] args)
    {
        MethodInfo? method = type.GetMethod(name, Declared, null, args, null);
        Require(method != null && HasSignature(method, result, args), type.FullName + "." + name + " signature");
        return method!;
    }
    private static void RequireUnpatched(MethodInfo method)
    {
        Patches? info = Harmony.GetPatchInfo(method);
        Require(info == null || !info.Owners.Any(), "uninspected patch on " + method.DeclaringType?.FullName + "." + method.Name);
    }
    private static void RequireNoEntry(CodeInstruction instruction) => Require(instruction.labels.Count == 0 && instruction.blocks.Count == 0,
        "branch/exception entry inside replacement guard");
    private static void Match(MethodInfo method, string expected) => Require(Fingerprint(PatchProcessor.GetOriginalInstructions(method)) == expected,
        "unrecognized relevant method body " + method.DeclaringType?.FullName + "." + method.Name);
    private static void Require(bool condition, string reason)
    {
        if (!condition) throw new InvalidOperationException("Unsupported EpicLoot Feed Barrel contract: " + reason);
    }

    internal static string Fingerprint(List<CodeInstruction> code)
    {
        Require(code.All(instruction => instruction.blocks.Count == 0), "exception flow in relevant method");
        var ordinal = new int[code.Count + 1];
        int count = 0;
        for (int i = 0; i < code.Count; i++) { ordinal[i] = count; if (code[i].opcode != OpCodes.Nop) count++; }
        ordinal[code.Count] = count;
        int Target(Label label)
        {
            int index = code.FindIndex(instruction => instruction.labels.Contains(label));
            Require(index >= 0, "unresolved branch target");
            return ordinal[index];
        }
        string TypeName(Type type)
        {
            if (type.IsGenericParameter) return "!" + type.GenericParameterPosition;
            if (type.IsGenericType) return type.GetGenericTypeDefinition().FullName + "<" + string.Join(",", type.GetGenericArguments().Select(TypeName)) + ">";
            return type.FullName ?? type.Name;
        }
        string Operand(object? value)
        {
            if (value is Label label) return "@" + Target(label);
            if (value is Label[] labels) return string.Join(",", labels.Select(branchLabel => "@" + Target(branchLabel)));
            if (value is LocalVariableInfo local) return local.LocalIndex.ToString(CultureInfo.InvariantCulture);
            if (value is MethodBase method) return TypeName(method.DeclaringType!) + "::" + method.Name +
                (method is MethodInfo generic && generic.IsGenericMethod ? "<" + string.Join(",", generic.GetGenericArguments().Select(TypeName)) + ">" : "") +
                "(" + string.Join(",", method.GetParameters().Select(parameter => TypeName(parameter.ParameterType))) + ")" +
                (method is MethodInfo returns ? ":" + TypeName(returns.ReturnType) : "");
            if (value is FieldInfo field) return TypeName(field.DeclaringType!) + "::" + field.Name + ":" + TypeName(field.FieldType);
            if (value is Type type) return TypeName(type);
            return Convert.ToString(value, CultureInfo.InvariantCulture) ?? "";
        }
        var text = new StringBuilder();
        foreach (CodeInstruction instruction in code)
        {
            if (instruction.opcode == OpCodes.Nop) continue;
            string name = instruction.opcode.Name!;
            string operand = Operand(instruction.operand);
            if (instruction.IsLdloc() || instruction.IsStloc())
            { name = instruction.IsLdloc() ? "ldloc" : "stloc"; operand = LocalIndex(instruction).ToString(CultureInfo.InvariantCulture); }
            else if (name.EndsWith(".s", StringComparison.Ordinal)) name = name.Substring(0, name.Length - 2);
            text.Append(name).Append(' ').Append(operand).Append('\n');
        }
        using SHA256 sha = SHA256.Create();
        return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(text.ToString()))).Replace("-", "");
    }
}
