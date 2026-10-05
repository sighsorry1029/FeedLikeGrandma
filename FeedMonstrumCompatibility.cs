using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace FeedLikeGrandma;

internal static class FeedMonstrumCompatibility
{
    internal enum Package
    {
        Monstrum,
        MonstrumDeepNorth
    }

    private const string MonstrumUpdateDoodadControlsPatchTypeName = "Monstrum.TwPlayerUpdateDoodadControlsPatch";
    private const string MonstrumDeepNorthUpdateDoodadControlsPatchTypeName = "MonstrumDeepNorth.TwPlayerUpdateDoodadControlsPatch";
    private const string MonstrumPlayerExtensionsTypeName = "Monstrum.PlayerExtensions";
    private const string MonstrumDeepNorthPlayerExtensionsTypeName = "MonstrumDeepNorth.PlayerExtensions";
    private const string MonstrumRidingPatchTypeName = "Monstrum.StartRidingMountPatch_Monstrum";
    private const string MonstrumDeepNorthRidingPatchTypeName = "MonstrumDeepNorth.StartRidingMountPatch_MonstrumDeepNorth";

    private static readonly HashSet<string> MonstrumMountPrefabs = new()
    {
        "Razorback_TW",
        "BlackBear_TW",
        "GrizzlyBear_TW",
        "Prowler_TW"
    };

    private static readonly HashSet<string> MonstrumDeepNorthMountPrefabs = new()
    {
        "ArcticMammoth_TW",
        "ArcticBear_TW"
    };
    private static readonly Dictionary<string, Type?> TypeCache = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, MethodInfo?> MethodCache = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, FieldInfo?> FieldCache = new(StringComparer.Ordinal);

    internal static MethodBase? GetUpdateDoodadControlsPostfix(Package package)
    {
        string typeName = package == Package.Monstrum
            ? MonstrumUpdateDoodadControlsPatchTypeName
            : MonstrumDeepNorthUpdateDoodadControlsPatchTypeName;
        return GetMethod(typeName, "Postfix");
    }

    internal static MethodBase? GetCustomAttachStop(Package package)
    {
        string typeName = package == Package.Monstrum
            ? MonstrumPlayerExtensionsTypeName
            : MonstrumDeepNorthPlayerExtensionsTypeName;
        return GetMethod(typeName, "CustomAttachStop", typeof(Player));
    }

    private static Type? FindType(string typeName)
    {
        if (TypeCache.TryGetValue(typeName, out Type? cachedType))
        {
            return cachedType;
        }

        foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            Type? type = assembly.GetType(typeName, throwOnError: false);
            if (type != null)
            {
                TypeCache[typeName] = type;
                return type;
            }
        }

        TypeCache[typeName] = null;
        return null;
    }

    private static MethodInfo? GetMethod(string typeName, string methodName, params Type[] parameterTypes)
    {
        string key = parameterTypes.Length == 0
            ? $"{typeName}.{methodName}"
            : $"{typeName}.{methodName}({string.Join(",", Array.ConvertAll(parameterTypes, type => type.FullName))})";
        if (MethodCache.TryGetValue(key, out MethodInfo? cachedMethod))
        {
            return cachedMethod;
        }

        Type? type = FindType(typeName);
        MethodInfo? method = type == null
            ? null
            : parameterTypes.Length == 0
                ? AccessTools.Method(type, methodName)
                : AccessTools.Method(type, methodName, parameterTypes);
        MethodCache[key] = method;
        return method;
    }

    private static FieldInfo? GetField(string typeName, string fieldName)
    {
        string key = $"{typeName}.{fieldName}";
        if (FieldCache.TryGetValue(key, out FieldInfo? cachedField))
        {
            return cachedField;
        }

        Type? type = FindType(typeName);
        FieldInfo? field = type == null ? null : AccessTools.Field(type, fieldName);
        FieldCache[key] = field;
        return field;
    }

    internal static bool ShouldSkipJumpDismountPostfix(Player player, Package package)
    {
        if (!IsEnabled() ||
            !HasJumpInput() ||
            !TryGetRidingMonstrumMount(player, package, out Character? mount))
        {
            return false;
        }

        if ((object?)mount == null)
        {
            return false;
        }

        return mount.GetHealth() > 0f;
    }

    internal static void ConvertJumpToMountJump(Player player, ref bool jump)
    {
        if (!jump ||
            !IsEnabled() ||
            !TryGetRidingMonstrumMount(player, out Character? mount))
        {
            return;
        }

        if ((object?)mount == null)
        {
            return;
        }

        if (mount.GetHealth() <= 0f)
        {
            return;
        }

        mount.Jump(false);
        jump = false;
    }

    internal static bool ShouldAllowCustomAttachStop(Player player, Package package)
    {
        if (!IsEnabled() ||
            !HasJumpInput() ||
            !TryGetRidingMonstrumMount(player, package, out Character? mount))
        {
            return true;
        }

        return (object?)mount == null || mount.GetHealth() <= 0f;
    }

    private static bool IsEnabled()
    {
        return FeedLikeGrandmaPlugin.MonstrumMountJumpCompatibility.Value.IsOn();
    }

    private static bool HasJumpInput()
    {
        return ZInput.GetButton("Jump") || ZInput.GetButtonDown("JoyJump");
    }

    private static bool TryGetRidingMonstrumMount(Player player, out Character? mount)
    {
        mount = null;
        if (TryGetPackageRidingMount(Package.Monstrum, out mount) ||
            TryGetPackageRidingMount(Package.MonstrumDeepNorth, out mount))
        {
            return true;
        }

        if (!TryGetRidingSaddle(player, out Sadle? saddle, out string prefabName))
        {
            return false;
        }

        if (!IsMonstrumMountPrefab(prefabName))
        {
            return false;
        }

        mount = saddle!.GetCharacter();
        return (object?)mount != null;
    }

    private static bool TryGetRidingMonstrumMount(Player player, Package package, out Character? mount)
    {
        mount = null;
        if (TryGetPackageRidingMount(package, out mount))
        {
            return true;
        }

        if (!TryGetRidingSaddle(player, out Sadle? saddle, out string prefabName))
        {
            return false;
        }

        if (!IsMonstrumMountPrefab(prefabName, package))
        {
            return false;
        }

        mount = saddle!.GetCharacter();
        return (object?)mount != null;
    }

    private static bool TryGetRidingSaddle(Player player, out Sadle? saddle, out string prefabName)
    {
        saddle = null;
        prefabName = string.Empty;
        if ((object?)player == null || !player.IsRiding())
        {
            return false;
        }

        saddle = player.GetDoodadController() as Sadle;
        Component? controlled = saddle?.GetControlledComponent();
        if ((object?)controlled == null)
        {
            return false;
        }

        prefabName = Utils.GetPrefabName(((UnityEngine.Object)controlled.gameObject).name);
        return !string.IsNullOrWhiteSpace(prefabName);
    }

    private static bool TryGetPackageRidingMount(Package package, out Character? mount)
    {
        mount = null;
        string typeName = package == Package.Monstrum ? MonstrumRidingPatchTypeName : MonstrumDeepNorthRidingPatchTypeName;

        string ridingFlagName = package == Package.Monstrum ? "RidingMountMonstrum" : "RidingMountMonstrumDeepNorth";
        FieldInfo? ridingFlag = GetField(typeName, ridingFlagName);
        FieldInfo? ridingHumanoid = GetField(typeName, "RidingHumanoid");
        if (ridingFlag?.GetValue(null) is not true || ridingHumanoid?.GetValue(null) is not Humanoid humanoid)
        {
            return false;
        }

        mount = humanoid as Character;
        return (object?)mount != null;
    }

    private static bool IsMonstrumMountPrefab(string prefabName)
    {
        return MonstrumMountPrefabs.Contains(prefabName) ||
               MonstrumDeepNorthMountPrefabs.Contains(prefabName);
    }

    private static bool IsMonstrumMountPrefab(string prefabName, Package package)
    {
        return package == Package.Monstrum
            ? MonstrumMountPrefabs.Contains(prefabName)
            : MonstrumDeepNorthMountPrefabs.Contains(prefabName);
    }
}

[HarmonyPatch(typeof(Player), nameof(Player.SetControls))]
internal static class PlayerSetControlsMonstrumMountJumpPatch
{
    [HarmonyPriority(Priority.First)]
    [HarmonyBefore("Yggdrah.BetterRiding")]
    private static void Prefix(Player __instance, ref bool jump)
    {
        FeedMonstrumCompatibility.ConvertJumpToMountJump(__instance, ref jump);
    }
}

[HarmonyPatch]
internal static class MonstrumUpdateDoodadControlsCompatibilityPatch
{
    private static bool Prepare()
    {
        return FeedMonstrumCompatibility.GetUpdateDoodadControlsPostfix(FeedMonstrumCompatibility.Package.Monstrum) != null;
    }

    private static MethodBase? TargetMethod()
    {
        return FeedMonstrumCompatibility.GetUpdateDoodadControlsPostfix(FeedMonstrumCompatibility.Package.Monstrum);
    }

    private static bool Prefix(Player __0)
    {
        return !FeedMonstrumCompatibility.ShouldSkipJumpDismountPostfix(__0, FeedMonstrumCompatibility.Package.Monstrum);
    }
}

[HarmonyPatch]
internal static class MonstrumCustomAttachStopCompatibilityPatch
{
    private static bool Prepare()
    {
        return FeedMonstrumCompatibility.GetCustomAttachStop(FeedMonstrumCompatibility.Package.Monstrum) != null;
    }

    private static MethodBase? TargetMethod()
    {
        return FeedMonstrumCompatibility.GetCustomAttachStop(FeedMonstrumCompatibility.Package.Monstrum);
    }

    private static bool Prefix(Player __0)
    {
        return FeedMonstrumCompatibility.ShouldAllowCustomAttachStop(__0, FeedMonstrumCompatibility.Package.Monstrum);
    }
}

[HarmonyPatch]
internal static class MonstrumDeepNorthUpdateDoodadControlsCompatibilityPatch
{
    private static bool Prepare()
    {
        return FeedMonstrumCompatibility.GetUpdateDoodadControlsPostfix(FeedMonstrumCompatibility.Package.MonstrumDeepNorth) != null;
    }

    private static MethodBase? TargetMethod()
    {
        return FeedMonstrumCompatibility.GetUpdateDoodadControlsPostfix(FeedMonstrumCompatibility.Package.MonstrumDeepNorth);
    }

    private static bool Prefix(Player __0)
    {
        return !FeedMonstrumCompatibility.ShouldSkipJumpDismountPostfix(__0, FeedMonstrumCompatibility.Package.MonstrumDeepNorth);
    }
}

[HarmonyPatch]
internal static class MonstrumDeepNorthCustomAttachStopCompatibilityPatch
{
    private static bool Prepare()
    {
        return FeedMonstrumCompatibility.GetCustomAttachStop(FeedMonstrumCompatibility.Package.MonstrumDeepNorth) != null;
    }

    private static MethodBase? TargetMethod()
    {
        return FeedMonstrumCompatibility.GetCustomAttachStop(FeedMonstrumCompatibility.Package.MonstrumDeepNorth);
    }

    private static bool Prefix(Player __0)
    {
        return FeedMonstrumCompatibility.ShouldAllowCustomAttachStop(__0, FeedMonstrumCompatibility.Package.MonstrumDeepNorth);
    }
}
