using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using BepInEx.Bootstrap;
using HarmonyLib;
using UnityEngine;

namespace FeedLikeGrandma;

// The bridge owns only the ground-store entry prefix. It neither invokes Azu
// storage nor changes its inventory hotkey, YAML rules, ownership or retry queue.
internal static class FeedAzuAutoStoreCompatibility
{
    internal const string PluginGuid = "Azumatt.AzuAutoStore";
    private const string HarmonyId = "sighsorry.FeedLikeGrandma.AzuAutoStore";
    private enum State { Unknown, Absent, Active, Disabled }
    private static State _state;
    private static Harmony? _harmony;
    private static Assembly? _assembly;
    private static MethodInfo[] _methods = Array.Empty<MethodInfo>();
    private static string? _failure;
    private static readonly MethodInfo PrefixMethod = AccessTools.Method(typeof(FeedAzuAutoStoreCompatibility), nameof(SkipProtectedDrop));

    internal static void Initialize()
    {
        if (_state == State.Active || _state == State.Disabled) return;
        if (!Chainloader.PluginInfos.TryGetValue(PluginGuid, out var plugin))
        {
            _state = State.Absent;
            return;
        }
        try
        {
            if (plugin.Instance == null) throw new InvalidOperationException("AzuAutoStore plugin instance is unavailable");
            _assembly = plugin.Instance.GetType().Assembly;
            _methods = ValidateContract(_assembly);
            foreach (MethodInfo method in _methods) RequireUnpatched(method);
            _harmony = new Harmony(HarmonyId);
            _harmony.Patch(_methods[0], prefix: new HarmonyMethod(PrefixMethod) { priority = Priority.First });
            ValidatePatches();
            _state = State.Active;
            FeedLikeGrandmaPlugin.Log.LogInfo("AzuAutoStore Feed Barrel protection enabled: " +
                plugin.Metadata.Version + "; mvid=" + _assembly.ManifestModule.ModuleVersionId);
        }
        catch (Exception error) { Disable(error); }
    }

    // Called at dispensing boundaries, not for every world item or frame. An
    // initially absent soft dependency may register before the first use.
    internal static bool CanDispense(out string? reason)
    {
        if (_state == State.Unknown || (_state == State.Absent && Chainloader.PluginInfos.ContainsKey(PluginGuid))) Initialize();
        if (_state == State.Active)
        {
            try
            {
                if (!Chainloader.PluginInfos.TryGetValue(PluginGuid, out var plugin) || plugin.Instance == null ||
                    plugin.Instance.GetType().Assembly != _assembly)
                    throw new InvalidOperationException("Running AzuAutoStore instance changed; restart to rebind");
                ValidatePatches();
            }
            catch (Exception error) { Disable(error); }
        }
        reason = _failure;
        return _state == State.Absent || _state == State.Active;
    }

    internal static void Shutdown()
    {
        RemoveOwnPatch();
        _assembly = null;
        _methods = Array.Empty<MethodInfo>();
        _failure = null;
        _state = State.Unknown;
    }

    private static bool SkipProtectedDrop(ItemDrop __0)
    {
        try { return !FeedBarrelDropProtection.IsProtected(__0); }
        catch (Exception error)
        {
            // Reject only this observation on failure. Stop new local barrel
            // dispensing and remove our hook; normal game/Azu features remain.
            Disable(error);
            return false;
        }
    }

    private static void Disable(Exception error)
    {
        if (_state == State.Disabled) return;
        _state = State.Disabled;
        _failure = error.GetType().Name + ": " + error.Message;
        RemoveOwnPatch();
        try { FeedLikeGrandmaPlugin.Log.LogWarning("AzuAutoStore Feed Barrel protection unavailable; " +
            "local Feed Barrel dispensing is paused before stock removal. Other feeding/storage remains enabled. " + _failure); }
        catch (Exception) { }
    }

    private static void RemoveOwnPatch()
    {
        try
        {
            _harmony?.UnpatchSelf();
            _harmony = null;
        }
        catch (Exception error)
        {
            try { FeedLikeGrandmaPlugin.Log.LogWarning("AzuAutoStore bridge cleanup failed: " + error.GetType().Name); }
            catch (Exception) { }
        }
    }

    private static void ValidatePatches()
    {
        Require(_methods.Length > 0, "unbound bridge");
        Patches? info = Harmony.GetPatchInfo(_methods[0]);
        Require(info != null && info.Prefixes.Count == 1 && info.Prefixes[0].owner == HarmonyId &&
            info.Prefixes[0].PatchMethod == PrefixMethod && info.Postfixes.Count == 0 &&
            info.Transpilers.Count == 0 && info.Finalizers.Count == 0, "ground-store patch set changed");
        for (int i = 1; i < _methods.Length; i++) RequireUnpatched(_methods[i]);
    }

    private static void RequireUnpatched(MethodInfo method)
    {
        Patches? info = Harmony.GetPatchInfo(method);
        Require(info == null || !info.Owners.Any(), "uninspected patch on " + method.Name);
    }

    // Relevant shape/call contracts, independent of tokens, branch offsets,
    // assembly version and MVID. This recognizes the inspected route, not the
    // semantic equivalence of every future implementation with matching names.
    internal static MethodInfo[] ValidateContract(Assembly assembly)
    {
        Type functions = RequiredType(assembly, "AzuAutoStore.Util.Functions");
        Type manager = RequiredType(assembly, "AzuAutoStore.Managers.AzuAutoStoreItemDropManager");
        Type startPatch = RequiredType(assembly, "AzuAutoStore.Managers.GameStartPatch");
        Type boxes = RequiredType(assembly, "AzuAutoStore.Util.Boxes");
        Require(typeof(MonoBehaviour).IsAssignableFrom(manager), "item-drop manager lifecycle");
        MethodInfo target = Method(functions, "CheckItemDropInstanceAndStore", true, typeof(void), typeof(ItemDrop));
        MethodInfo process = Method(manager, "ProcessItemDrops", false, typeof(void));
        MethodInfo asked = Method(manager, "ProcessAskedFor", false, typeof(void));
        MethodInfo awake = Method(manager, "Awake", false, typeof(void));
        MethodInfo start = Method(startPatch, "Postfix", true, typeof(void));
        MethodInfo pause = Method(manager, "ShouldPause", true, typeof(bool));
        MethodInfo nearby = Method(functions, "BuildNearbyContainers", true, typeof(void));
        MethodInfo clear = Method(functions, "ClearNearbyContainers", true, typeof(void));
        MethodInfo store = Method(functions, "TryStore", true, typeof(bool), typeof(Container),
            typeof(ItemDrop.ItemData).MakeByRefType(), typeof(bool), typeof(bool));
        MethodInfo age = Method(functions, "SatLongEnoughToAskFor", true, typeof(bool), typeof(ItemDrop));
        MethodInfo wants = Method(functions, "AnyContainerWants", true, typeof(bool), typeof(ItemDrop));
        FieldInfo retryItems = Field(functions, "AskedFor", typeof(List<ItemDrop>));
        FieldInfo dropItems = Field(manager, "DropBuffer", typeof(List<ItemDrop>));

        List<CodeInstruction> targetCode = Code(target);
        Require(Calls(targetCode, store) == 1 && Calls(targetCode, age) == 1 && Calls(targetCode, wants) == 1 &&
            HasCall(targetCode, typeof(ZNetView), "IsOwner") && HasCall(targetCode, typeof(ZNetView), "InvokeRPC") &&
            HasString(targetCode, "RPC_RequestOwn") && HasCall(targetCode, typeof(ItemDrop), "Save") &&
            HasCall(targetCode, typeof(ZNetScene), "Destroy") && Reads(targetCode, retryItems), "ground-store ownership/storage route");
        Require(!targetCode.Any(instruction => instruction.opcode == OpCodes.Ldftn || instruction.opcode == OpCodes.Ldvirtftn),
            "deferred ground-store work");
        foreach (var route in new[] { (process, dropItems), (asked, retryItems) })
        {
            List<CodeInstruction> code = Code(route.Item1);
            Require(Calls(code, target) == 1 && Calls(code, pause) == 1 && Calls(code, nearby) == 1 &&
                Calls(code, clear) == 1 && Reads(code, route.Item2), "scheduled collection route " + route.Item1.Name);
            // The manager may collect references and call the guarded entry;
            // direct stores/RPCs or unknown Azu helpers would bypass protection.
            foreach (MethodBase call in CalledMethods(code))
            {
                if (call.DeclaringType?.Assembly == assembly)
                    Require(call == target || call == pause || call == nearby || call == clear,
                        "new collection helper " + call.Name);
                Require(call.DeclaringType != typeof(ZNetView) || call.Name == "IsValid" || call.Name == "IsOwner",
                    "collection ownership side effect " + call.Name);
                Require(call.DeclaringType != typeof(Inventory) && call.DeclaringType != typeof(ZNetScene),
                    "collection bypass storage " + call.Name);
            }
        }
        List<CodeInstruction> awakeCode = Code(awake);
        Require(HasString(awakeCode, process.Name) && HasString(awakeCode, asked.Name) &&
            CalledMethods(awakeCode).Count(call => call.Name == "InvokeRepeating" && call.DeclaringType == typeof(MonoBehaviour)) == 2,
            "scheduled collection callbacks");
        Require(CalledMethods(Code(start)).Any(call => call is MethodInfo method && method.IsGenericMethod &&
            method.Name == "AddComponent" && method.GetGenericArguments().SequenceEqual(new[] { manager })), "manager registration");
        Require(HasCall(Code(pause), typeof(Character), "IsDead") &&
            Code(pause).Any(instruction => instruction.operand is FieldInfo field &&
                field.DeclaringType == typeof(Player) && field.Name == "m_localPlayer"), "client collection guard");
        // The discovery helper must build the container index, not store items.
        foreach (MethodBase call in CalledMethods(Code(nearby)))
        {
            if (call.DeclaringType?.Assembly == assembly)
                Require((call.DeclaringType == boxes && call.Name == "PruneDestroyed") ||
                    (call.DeclaringType == functions && call.Name == "GetContainerRange"), "container discovery helper " + call.Name);
            Require(call.DeclaringType != typeof(Inventory) || call.Name == "NrOfItems", "container discovery write");
        }
        return new[] { target, process, asked, awake, start, pause, nearby, clear };
    }

    private static List<CodeInstruction> Code(MethodInfo method) => PatchProcessor.GetOriginalInstructions(method).ToList();
    private static IEnumerable<MethodBase> CalledMethods(IEnumerable<CodeInstruction> code) => code.Where(instruction =>
        instruction.opcode == OpCodes.Call || instruction.opcode == OpCodes.Callvirt).Select(instruction => instruction.operand).OfType<MethodBase>();
    private static int Calls(IEnumerable<CodeInstruction> code, MethodInfo method) => CalledMethods(code).Count(call => call == method);
    private static bool HasCall(IEnumerable<CodeInstruction> code, Type owner, string name) => CalledMethods(code).Any(call => call.DeclaringType == owner && call.Name == name);
    private static bool HasString(IEnumerable<CodeInstruction> code, string value) => code.Any(instruction => instruction.opcode == OpCodes.Ldstr && Equals(instruction.operand, value));
    private static bool Reads(IEnumerable<CodeInstruction> code, FieldInfo field) => code.Any(instruction => instruction.opcode == OpCodes.Ldsfld && Equals(instruction.operand, field));
    private static Type RequiredType(Assembly assembly, string name) => assembly.GetType(name, false) ?? throw new MissingMemberException(name);
    private static MethodInfo Method(Type type, string name, bool isStatic, Type result, params Type[] parameters)
    {
        MethodInfo? method = type.GetMethod(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance |
            BindingFlags.DeclaredOnly, null, parameters, null);
        Require(method != null && method.IsStatic == isStatic && method.ReturnType == result &&
            !method.ContainsGenericParameters && method.GetMethodBody() != null, "method " + type.Name + "." + name);
        return method!;
    }
    private static FieldInfo Field(Type type, string name, Type expected)
    {
        FieldInfo? field = type.GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.DeclaredOnly);
        Require(field != null && field.FieldType == expected, "field " + type.Name + "." + name);
        return field!;
    }
    private static void Require(bool value, string contract)
    {
        if (!value) throw new InvalidOperationException("Unsupported AzuAutoStore contract: " + contract);
    }
}
