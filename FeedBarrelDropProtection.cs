using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using UnityEngine;

namespace FeedLikeGrandma;

// A world-drop attribute, not item customData: picking food up ends its protection.
// Keep the merge boundary on every FLG peer, since Azu may run on another owner.
internal static class FeedBarrelDropProtection
{
    private const string HarmonyId = "sighsorry.FeedLikeGrandma.FeedBarrelDrops";
    private static readonly int MarkerHash = "flg.feedbarrel.food.v1".GetStableHashCode();
    private static AccessTools.FieldRef<ItemDrop, ZNetView>? _networkView;
    private static Harmony? _harmony;
    private static MethodInfo? _target;
    private static MethodInfo? _transpiler;
    private static Patches? _validatedPatches;
    private static bool _installed;
    private static string? _failure;

    internal static void Initialize()
    {
        Shutdown();
        try
        {
            // ItemDrop has no public ZNetView getter. Cache its original private
            // field once instead of repeated component searches in pickup loops.
            FieldInfo? field = typeof(ItemDrop).GetField("m_nview",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
            if (field == null || field.IsStatic || field.FieldType != typeof(ZNetView))
                throw new MissingFieldException("ItemDrop.m_nview contract changed.");
            _networkView = AccessTools.FieldRefAccess<ItemDrop, ZNetView>(field);
            _target = AccessTools.DeclaredMethod(typeof(ItemDrop), "AutoStackItems", Type.EmptyTypes);
            if (_target == null || _target.IsStatic || _target.ReturnType != typeof(void) ||
                _target.ContainsGenericParameters || _target.GetMethodBody() == null ||
                _target.GetMethodBody()!.ExceptionHandlingClauses.Count != 0)
                throw new MissingMethodException("ItemDrop.AutoStackItems contract changed.");

            ValidateStackBody(PatchProcessor.GetOriginalInstructions(_target));
            _transpiler = AccessTools.DeclaredMethod(typeof(FeedBarrelDropProtection), nameof(Transpiler));
            _harmony = new Harmony(HarmonyId);
            _harmony.Patch(_target, transpiler: new HarmonyMethod(_transpiler) { priority = Priority.Last });
            ValidateInstalledPatch();
            _installed = true;
            _failure = null;
        }
        catch (Exception error)
        {
            RemoveOwnPatch();
            BlockDispensing(error.Message);
        }
    }

    internal static bool IsReady(out string? reason)
    {
        if (_installed && _failure == null)
        {
            try { ValidateInstalledPatch(); }
            catch (Exception error) { BlockDispensing(error.Message); }
        }
        reason = _failure ?? (_installed ? null : "Feed Barrel drop protection has not initialized.");
        return _installed && reason == null;
    }

    internal static bool Mark(ItemDrop? item)
    {
        try
        {
            if (!_installed || _failure != null || item == null || _networkView == null)
                throw new InvalidOperationException("Feed Barrel drop protection is unavailable after spawning.");
            ZNetView view = _networkView(item);
            if (view == null || !view.IsValid() || !view.IsOwner())
                throw new InvalidOperationException("Feed Barrel cannot mark an unowned or invalid food drop.");
            ZDO zdo = view.GetZDO();
            zdo.Set(MarkerHash, true);
            if (!zdo.GetBool(MarkerHash, false))
                throw new InvalidOperationException("Feed Barrel food marker did not persist.");
            return true;
        }
        catch (Exception error)
        {
            // The item has already left the container. Retain it and the valid
            // protection hooks; block future dispenses rather than duplicate it.
            BlockDispensing(error.Message);
            return false;
        }
    }

    internal static bool IsProtected(ItemDrop? item)
    {
        if (item == null || _networkView == null) return false;
        ZNetView view = _networkView(item);
        return view != null && view.IsValid() && view.GetZDO().GetBool(MarkerHash, false);
    }

    private static ItemDrop? FilterStackCandidate(ItemDrop? candidate, ItemDrop receiver)
    {
        if (candidate == null || candidate == receiver) return candidate;
        try
        {
            // Both marked drops retain a marked survivor; two ordinary drops use
            // vanilla rules. Mixed pairs must not lose or spread the marker.
            return IsProtected(candidate) == IsProtected(receiver) ? candidate : null;
        }
        catch (Exception error)
        {
            BlockDispensing(error.Message);
            return null;
        }
    }

    private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        // A rebuild can change emitted IL even with identical callback metadata
        // (for example a foreign transpiler reacting to its own configuration).
        _validatedPatches = null;
        List<CodeInstruction> code = instructions.ToList();
        try { ValidateStackBody(code); }
        catch (Exception)
        {
            // Harmony can rerun us while another mod registers a later patch.
            // Leave its input intact instead of failing that mod's startup.
            // Final composed-code validation rejects a missing guard before
            // FLG takes any new barrel stock.
            return code;
        }
        int lookup = FindCandidateLookup(code);
        MethodInfo filter = AccessTools.DeclaredMethod(typeof(FeedBarrelDropProtection), nameof(FilterStackCandidate));
        // The existing null check consumes the filtered result. All ownership,
        // amount, quality, destruction and saving logic stays in the original.
        code.InsertRange(lookup + 1, new[]
        {
            new CodeInstruction(OpCodes.Ldarg_0), new CodeInstruction(OpCodes.Call, filter)
        });
        return code;
    }

    private static int FindCandidateLookup(List<CodeInstruction> code)
    {
        int found = -1;
        for (int i = 0; i < code.Count; i++)
        {
            if ((code[i].opcode != OpCodes.Call && code[i].opcode != OpCodes.Callvirt) ||
                code[i].operand is not MethodInfo method || method.Name != "GetComponent" ||
                !method.IsGenericMethod || method.GetGenericArguments().Length != 1 ||
                method.GetGenericArguments()[0] != typeof(ItemDrop) || method.GetParameters().Length != 0 ||
                method.DeclaringType != typeof(Component)) continue;
            if (found != -1) throw new InvalidOperationException("Ambiguous ItemDrop stack candidate lookup.");
            found = i;
        }
        if (found < 0) throw new InvalidOperationException("ItemDrop stack candidate lookup is unavailable.");
        return found;
    }

    private static void ValidateStackBody(List<CodeInstruction> code)
    {
        int lookup = FindCandidateLookup(code);
        if (lookup + 5 >= code.Count || !code[lookup + 1].IsStloc() || code.Any(instruction => instruction.blocks.Count != 0))
            throw new InvalidOperationException("ItemDrop stack candidate flow changed.");
        RequireNoEntry(code, lookup + 1, lookup + 5);
        int candidate = LocalIndex(code[lookup + 1]);
        if (code.Count(instruction => instruction.IsStloc() && LocalIndex(instruction) == candidate) != 1 ||
            code.Any(instruction => (instruction.opcode == OpCodes.Ldloca || instruction.opcode == OpCodes.Ldloca_S) &&
                LocalIndex(instruction) == candidate))
            throw new InvalidOperationException("ItemDrop stack candidate can be replaced after filtering.");
        MethodInfo equality = AccessTools.DeclaredMethod(typeof(UnityEngine.Object), "op_Equality",
            new[] { typeof(UnityEngine.Object), typeof(UnityEngine.Object) });
        CodeInstruction branch = code[lookup + 5];
        int destroy = code.FindIndex(instruction => instruction.operand is MethodInfo method &&
            method.DeclaringType == typeof(ZNetView) && method.Name == nameof(ZNetView.Destroy));
        // A null candidate must jump over the entire merge/destruction block.
        // Match locals and branch destinations, not compiler-specific indexes.
        if (!code[lookup + 2].IsLdloc() || LocalIndex(code[lookup + 1]) != LocalIndex(code[lookup + 2]) ||
            code[lookup + 3].opcode != OpCodes.Ldnull || !code[lookup + 4].Calls(equality) ||
            (branch.opcode != OpCodes.Brtrue && branch.opcode != OpCodes.Brtrue_S) ||
            branch.operand is not Label skip || destroy <= lookup + 5 || destroy + 1 >= code.Count ||
            !code[destroy + 1].labels.Contains(skip))
            throw new InvalidOperationException("ItemDrop stack null-candidate guard changed.");
        bool CallsOnce(Type type, string name) => code.Count(instruction =>
            instruction.operand is MethodInfo method && method.DeclaringType == type && method.Name == name &&
            (instruction.opcode == OpCodes.Call || instruction.opcode == OpCodes.Callvirt)) == 1;
        if (!CallsOnce(typeof(Physics), nameof(Physics.OverlapSphere)) ||
            !CallsOnce(typeof(ZNetView), nameof(ZNetView.IsOwner)) ||
            !CallsOnce(typeof(ZNetView), nameof(ZNetView.Destroy)) || !CallsOnce(typeof(ItemDrop), "Save"))
            throw new InvalidOperationException("ItemDrop stack query/ownership/save flow changed.");

        int owner = code.FindIndex(instruction => instruction.operand is MethodInfo method &&
            method.DeclaringType == typeof(ZNetView) && method.Name == nameof(ZNetView.IsOwner));
        if (owner < lookup + 8 || owner + 1 >= destroy ||
            !LoadsCandidate(code[owner - 2], candidate) || !IsField(code[owner - 1], OpCodes.Ldfld, typeof(ItemDrop), "m_nview") ||
            (code[owner + 1].opcode != OpCodes.Brfalse && code[owner + 1].opcode != OpCodes.Brfalse_S) ||
            code[owner + 1].operand is not Label ownerSkip || !code[destroy + 1].labels.Contains(ownerSkip) ||
            !LoadsCandidate(code[destroy - 2], candidate) || !IsField(code[destroy - 1], OpCodes.Ldfld, typeof(ItemDrop), "m_nview"))
            throw new InvalidOperationException("ItemDrop stack ownership/destruction no longer uses the filtered candidate.");
        RequireNoEntry(code, owner - 2, owner + 1);

        var stores = code.Select((instruction, index) => (instruction, index))
            .Where(pair => IsField(pair.instruction, OpCodes.Stfld, typeof(ItemDrop.ItemData), nameof(ItemDrop.ItemData.m_stack)))
            .Select(pair => pair.index).ToArray();
        if (stores.Length != 1 || stores[0] - 8 <= owner + 1 || stores[0] >= destroy)
            throw new InvalidOperationException("ItemDrop stack quantity update changed.");
        int add = stores[0] - 8;
        if (code[add].opcode != OpCodes.Ldarg_0 ||
            !IsField(code[add + 1], OpCodes.Ldfld, typeof(ItemDrop), nameof(ItemDrop.m_itemData)) ||
            code[add + 2].opcode != OpCodes.Dup ||
            !IsField(code[add + 3], OpCodes.Ldfld, typeof(ItemDrop.ItemData), nameof(ItemDrop.ItemData.m_stack)) ||
            !LoadsCandidate(code[add + 4], candidate) ||
            !IsField(code[add + 5], OpCodes.Ldfld, typeof(ItemDrop), nameof(ItemDrop.m_itemData)) ||
            !IsField(code[add + 6], OpCodes.Ldfld, typeof(ItemDrop.ItemData), nameof(ItemDrop.ItemData.m_stack)) ||
            code[add + 7].opcode != OpCodes.Add)
            throw new InvalidOperationException("ItemDrop stack quantity update no longer uses the filtered candidate.");

        RequireGuardedBranches(code, lookup + 5, lookup + 6, destroy);
        RequireGuardedBranches(code, owner + 1, owner + 2, destroy);
    }

    private static bool LoadsCandidate(CodeInstruction instruction, int candidate) =>
        instruction.IsLdloc() && LocalIndex(instruction) == candidate;

    private static bool IsField(CodeInstruction instruction, OpCode opcode, Type owner, string name) =>
        instruction.opcode == opcode && instruction.operand is FieldInfo field && field.DeclaringType == owner && field.Name == name;

    private static void RequireNoEntry(List<CodeInstruction> code, int first, int last)
    {
        for (int i = first; i <= last; i++)
            if (code[i].labels.Count != 0)
                throw new InvalidOperationException("A branch can enter inside an ItemDrop stack guard.");
    }

    private static void RequireGuardedBranches(List<CodeInstruction> code, int guard, int first, int last)
    {
        var protectedLabels = new HashSet<Label>();
        for (int i = first; i <= last; i++) protectedLabels.UnionWith(code[i].labels);
        for (int i = 0; i < code.Count; i++)
        {
            if (i > guard && i <= last) continue;
            if ((code[i].operand is Label label && protectedLabels.Contains(label)) ||
                (code[i].operand is Label[] labels && labels.Any(protectedLabels.Contains)))
                throw new InvalidOperationException("A branch bypasses the ItemDrop stack candidate/ownership guard.");
        }
    }

    private static void ValidateComposedBody(List<CodeInstruction> code)
    {
        int lookup = FindCandidateLookup(code);
        MethodInfo filter = AccessTools.DeclaredMethod(typeof(FeedBarrelDropProtection), nameof(FilterStackCandidate));
        if (code.Count(instruction => instruction.Calls(filter)) != 1 || lookup + 2 >= code.Count ||
            code[lookup + 1].opcode != OpCodes.Ldarg_0 || !code[lookup + 2].Calls(filter))
            throw new InvalidOperationException("Feed Barrel stack filter is missing, duplicated or moved.");
        RequireNoEntry(code, lookup + 1, lookup + 2);
        if (code[lookup + 1].blocks.Count != 0 || code[lookup + 2].blocks.Count != 0)
            throw new InvalidOperationException("Feed Barrel stack filter exception flow changed.");
        // Inspect the combined body, not just vanilla IL or the other mods' names.
        var withoutFilter = new List<CodeInstruction>(code);
        withoutFilter.RemoveRange(lookup + 1, 2);
        ValidateStackBody(withoutFilter);
    }

    private static int LocalIndex(CodeInstruction instruction)
    {
        if (instruction.opcode == OpCodes.Stloc_0 || instruction.opcode == OpCodes.Ldloc_0) return 0;
        if (instruction.opcode == OpCodes.Stloc_1 || instruction.opcode == OpCodes.Ldloc_1) return 1;
        if (instruction.opcode == OpCodes.Stloc_2 || instruction.opcode == OpCodes.Ldloc_2) return 2;
        if (instruction.opcode == OpCodes.Stloc_3 || instruction.opcode == OpCodes.Ldloc_3) return 3;
        return instruction.operand is LocalVariableInfo local ? local.LocalIndex : Convert.ToInt32(instruction.operand);
    }

    private static void ValidateInstalledPatch()
    {
        Patches? patches = _target == null ? null : Harmony.GetPatchInfo(_target);
        try
        {
            // GetCurrentInstructions covers transpilers, not wrapper callbacks or
            // HarmonyX IL manipulators. Do not silently approve an unseen route.
            if (patches == null || patches.Prefixes.Count != 0 || patches.Postfixes.Count != 0 ||
                patches.Finalizers.Count != 0 || patches.ILManipulators.Count != 0 ||
                patches.Transpilers.Count(patch => patch.owner == HarmonyId) != 1 ||
                !patches.Transpilers.Any(patch => patch.owner == HarmonyId && patch.PatchMethod == _transpiler))
                throw new InvalidOperationException("Unsupported wrapper or incomplete Feed Barrel stack guard.");
            if (_validatedPatches != null && SameTranspilers(_validatedPatches, patches)) return;
            ValidateComposedBody(PatchProcessor.GetCurrentInstructions(_target!));
            _validatedPatches = patches;
        }
        catch (Exception error)
        {
            _validatedPatches = null;
            throw new InvalidOperationException(error.Message + " Patches: " + DescribePatches(patches), error);
        }
    }

    private static bool SameTranspilers(Patches previous, Patches current)
    {
        if (previous.Transpilers.Count != current.Transpilers.Count) return false;
        for (int i = 0; i < previous.Transpilers.Count; i++)
        {
            Patch left = previous.Transpilers[i], right = current.Transpilers[i];
            if (left.index != right.index || left.owner != right.owner || left.PatchMethod != right.PatchMethod ||
                left.priority != right.priority || left.wrapTryCatch != right.wrapTryCatch ||
                !left.before.SequenceEqual(right.before) || !left.after.SequenceEqual(right.after)) return false;
        }
        return true;
    }

    private static string DescribePatches(Patches? patches)
    {
        if (patches == null) return "none";
        var entries = new List<string>();
        void Add(string kind, IEnumerable<Patch> list)
        {
            foreach (Patch patch in list)
                entries.Add(kind + " " + patch.owner + ":" + patch.PatchMethod.DeclaringType?.FullName + "." + patch.PatchMethod.Name);
        }
        Add("prefix", patches.Prefixes);
        Add("transpiler", patches.Transpilers);
        Add("postfix", patches.Postfixes);
        Add("finalizer", patches.Finalizers);
        Add("IL manipulator", patches.ILManipulators);
        return string.Join("; ", entries);
    }

    private static void BlockDispensing(string reason)
    {
        if (_failure != null) return;
        _failure = reason;
        try { FeedLikeGrandmaPlugin.Log.LogError("Feed Barrel dispensing stopped; existing items are retained. " + reason); }
        catch (Exception) { }
    }

    private static void RemoveOwnPatch()
    {
        _installed = false;
        _validatedPatches = null;
        try
        {
            _harmony?.UnpatchSelf();
            _harmony = null;
        }
        catch (Exception error)
        {
            try { FeedLikeGrandmaPlugin.Log.LogError("Feed Barrel stack guard cleanup failed: " + error.Message); }
            catch (Exception) { }
        }
    }

    internal static void Shutdown()
    {
        RemoveOwnPatch();
        if (_harmony != null) return; // Retain the accessor while our filter could still be installed.
        _networkView = null;
        _target = null;
        _transpiler = null;
        _failure = null;
    }
}
