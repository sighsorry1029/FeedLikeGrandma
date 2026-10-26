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

            RequireNoPatches(_target);
            ValidateStackBody(PatchProcessor.GetOriginalInstructions(_target));
            _transpiler = AccessTools.DeclaredMethod(typeof(FeedBarrelDropProtection), nameof(Transpiler));
            _harmony = new Harmony(HarmonyId);
            _harmony.Patch(_target, transpiler: new HarmonyMethod(_transpiler));
            ValidateInstalledPatch();
            List<CodeInstruction> patched = PatchProcessor.GetCurrentInstructions(_target);
            int lookup = FindCandidateLookup(patched);
            MethodInfo filter = AccessTools.DeclaredMethod(typeof(FeedBarrelDropProtection), nameof(FilterStackCandidate));
            if (patched.Count(instruction => instruction.Calls(filter)) != 1 ||
                lookup + 2 >= patched.Count || patched[lookup + 1].opcode != OpCodes.Ldarg_0 ||
                !patched[lookup + 2].Calls(filter))
                throw new InvalidOperationException("Feed Barrel stack filter was not installed completely.");
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
        List<CodeInstruction> code = instructions.ToList();
        ValidateStackBody(code);
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
        if (lookup + 5 >= code.Count || !code[lookup + 1].IsStloc() ||
            code[lookup + 1].labels.Count != 0 || code[lookup + 1].blocks.Count != 0)
            throw new InvalidOperationException("ItemDrop stack candidate flow changed.");
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
    }

    private static int LocalIndex(CodeInstruction instruction)
    {
        if (instruction.opcode == OpCodes.Stloc_0 || instruction.opcode == OpCodes.Ldloc_0) return 0;
        if (instruction.opcode == OpCodes.Stloc_1 || instruction.opcode == OpCodes.Ldloc_1) return 1;
        if (instruction.opcode == OpCodes.Stloc_2 || instruction.opcode == OpCodes.Ldloc_2) return 2;
        if (instruction.opcode == OpCodes.Stloc_3 || instruction.opcode == OpCodes.Ldloc_3) return 3;
        return instruction.operand is LocalVariableInfo local ? local.LocalIndex : Convert.ToInt32(instruction.operand);
    }

    private static void RequireNoPatches(MethodInfo target)
    {
        Patches? patches = Harmony.GetPatchInfo(target);
        if (patches != null && patches.Owners.Any())
            throw new InvalidOperationException("Uninspected patch on ItemDrop.AutoStackItems.");
    }

    private static void ValidateInstalledPatch()
    {
        Patches? patches = _target == null ? null : Harmony.GetPatchInfo(_target);
        if (patches == null || patches.Prefixes.Count != 0 || patches.Postfixes.Count != 0 ||
            patches.Finalizers.Count != 0 || patches.Transpilers.Count != 1 ||
            patches.Transpilers[0].owner != HarmonyId || patches.Transpilers[0].PatchMethod != _transpiler)
            throw new InvalidOperationException("Feed Barrel stack guard changed or is incomplete.");
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
