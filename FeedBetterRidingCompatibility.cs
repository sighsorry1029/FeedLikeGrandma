using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace FeedLikeGrandma;

internal static class FeedBetterRidingCompatibility
{
    private const string BetterRidingPluginTypeName = "BetterRiding.BetterRidingPlugin";
    private const string MountStaminaSystemTypeName = "BetterRiding.Behaviour.MountStaminaSystem";
    private const string MountAttackControllerTypeName = "BetterRiding.Behaviour.MountAttackController";
    private const string BetterRidingBlockFlyingDismountPatchTypeName = "BetterRiding.Behaviour.BlockFlyingDismountPatch";
    private const string DragonRidersBlockFlyingDismountPatchTypeName = "DragonRiders.Patches.MountController.BlockFlyingDismountPatch";
    private const float AttackRecordCleanupInterval = 60f;
    private const float AttackRecordTtl = 1800f;

    private static readonly Dictionary<int, float> LastAttackTimes = new();
    private static readonly Dictionary<string, ConfigEntry<bool>?> BetterRidingBoolConfigEntries = new(StringComparer.Ordinal);
    private static readonly List<int> ExpiredAttackKeys = new();

    private static float _nextAttackRecordCleanupTime;
    private static bool _betterRidingPluginTypeSearched;
    private static Type? _betterRidingPluginType;
    private static bool _mountStaminaSystemTypeSearched;
    private static Type? _mountStaminaSystemType;
    private static bool _mountStaminaSaddleFieldSearched;
    private static FieldInfo? _mountStaminaSaddleField;
    private static int _mountAttackSafetySaddleId;
    private static int _mountAttackSafetyCharacterId;
    private static int _mountAttackSafetyFrame;
    private static bool _mountAttackSafetyAllowed = true;

    private static Type? BetterRidingPluginType
    {
        get
        {
            if (!_betterRidingPluginTypeSearched)
            {
                _betterRidingPluginType = FindType(BetterRidingPluginTypeName);
                _betterRidingPluginTypeSearched = true;
            }

            return _betterRidingPluginType;
        }
    }

    private static Type? MountStaminaSystemType
    {
        get
        {
            if (!_mountStaminaSystemTypeSearched)
            {
                _mountStaminaSystemType = FindType(MountStaminaSystemTypeName);
                _mountStaminaSystemTypeSearched = true;
            }

            return _mountStaminaSystemType;
        }
    }

    internal static bool HasBetterRiding => BetterRidingPluginType != null;

    internal static bool HasBetterRidingMountStaminaSystem => MountStaminaSystemType != null;

    internal static void Shutdown()
    {
        LastAttackTimes.Clear();
        BetterRidingBoolConfigEntries.Clear();
        ExpiredAttackKeys.Clear();
        _nextAttackRecordCleanupTime = 0f;
        _mountAttackSafetySaddleId = 0;
        _mountAttackSafetyCharacterId = 0;
        _mountAttackSafetyFrame = 0;
        _mountAttackSafetyAllowed = true;
        _betterRidingPluginTypeSearched = false;
        _betterRidingPluginType = null;
        _mountStaminaSystemTypeSearched = false;
        _mountStaminaSystemType = null;
        _mountStaminaSaddleFieldSearched = false;
        _mountStaminaSaddleField = null;
    }

    private static Type? FindType(string typeName)
    {
        foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            Type? type = assembly.GetType(typeName, throwOnError: false);
            if (type != null)
            {
                return type;
            }
        }

        return null;
    }

    internal static MethodBase? GetMountStaminaSystemMethod(string methodName)
    {
        Type? type = MountStaminaSystemType;
        return type == null ? null : AccessTools.Method(type, methodName);
    }

    internal static MethodBase? GetMountAttackUpdate()
    {
        Type? type = FindType(MountAttackControllerTypeName);
        return type == null ? null : AccessTools.Method(type, "Update");
    }

    internal static MethodBase? GetBetterRidingBlockFlyingDismountPrefix()
    {
        Type? type = FindType(BetterRidingBlockFlyingDismountPatchTypeName);
        return type == null ? null : AccessTools.Method(type, "Prefix");
    }

    internal static MethodBase? GetDragonRidersBlockFlyingDismountPrefix()
    {
        Type? type = FindType(DragonRidersBlockFlyingDismountPatchTypeName);
        return type == null ? null : AccessTools.Method(type, "Prefix");
    }

    internal static bool HandleMountedAttack(Humanoid humanoid, ref bool result)
    {
        if (!TryFindAttackSaddle(humanoid, out Sadle? saddle) ||
            !TryGetMountStamina(saddle, out ResolvedMountStaminaValues? mountStamina) ||
            mountStamina == null)
        {
            return true;
        }

        float now = Time.time;
        CleanupAttackRecords(now);

        int key = GetMountKey(saddle);
        if (key != 0 &&
            LastAttackTimes.TryGetValue(key, out float lastAttackTime) &&
            now - lastAttackTime < mountStamina.AttackCooldown)
        {
            result = false;
            return false;
        }

        if (!CanUseAttackStamina(saddle, mountStamina))
        {
            result = false;
            return false;
        }

        UseAttackStamina(saddle, mountStamina);
        if (key != 0)
        {
            LastAttackTimes[key] = now;
        }

        result = true;
        return false;
    }

    private static void CleanupAttackRecords(float now)
    {
        if (LastAttackTimes.Count == 0 || now < _nextAttackRecordCleanupTime)
        {
            return;
        }

        _nextAttackRecordCleanupTime = now + AttackRecordCleanupInterval;
        ExpiredAttackKeys.Clear();
        foreach (KeyValuePair<int, float> entry in LastAttackTimes)
        {
            if (now - entry.Value > AttackRecordTtl)
            {
                ExpiredAttackKeys.Add(entry.Key);
            }
        }

        foreach (int key in ExpiredAttackKeys)
        {
            LastAttackTimes.Remove(key);
        }

        ExpiredAttackKeys.Clear();
    }

    internal static void SuppressPlayerAttackControlsWhileRiding(
        Player player,
        ref bool attack,
        ref bool attackHold,
        ref bool secondaryAttack,
        ref bool secondaryAttackHold)
    {
        if ((object?)player == null || !player.IsRiding())
        {
            return;
        }

        attack = false;
        attackHold = false;
        secondaryAttack = false;
        secondaryAttackHold = false;
        ResetPlayerDrawAttack(player);
    }

    internal static bool AllowPlayerAttackInput(Player player)
    {
        if ((object?)player == null || !player.IsRiding())
        {
            return true;
        }

        ResetPlayerDrawAttack(player);
        return false;
    }

    internal static bool CanAttack(object mountStaminaSystem, ref bool result)
    {
        if (!TryGetSystemSaddle(mountStaminaSystem, out Sadle? saddle) ||
            !TryGetMountStamina(saddle, out ResolvedMountStaminaValues? mountStamina) ||
            mountStamina == null)
        {
            return true;
        }

        result = CanUseAttackStamina(saddle, mountStamina);
        return false;
    }

    internal static bool CanJump(object mountStaminaSystem, ref bool result)
    {
        if (!TryGetSystemSaddle(mountStaminaSystem, out Sadle? saddle) ||
            !TryGetMountStamina(saddle, out ResolvedMountStaminaValues? mountStamina) ||
            mountStamina == null)
        {
            return true;
        }

        result = CanUseJumpStamina(saddle, mountStamina);
        return false;
    }

    internal static bool OnAttackStart(object mountStaminaSystem)
    {
        if (!TryGetSystemSaddle(mountStaminaSystem, out Sadle? saddle) ||
            !TryGetMountStamina(saddle, out ResolvedMountStaminaValues? mountStamina) ||
            mountStamina == null)
        {
            return true;
        }

        UseAttackStamina(saddle, mountStamina);
        return false;
    }

    internal static bool OnJumpStart(object mountStaminaSystem)
    {
        if (!TryGetSystemSaddle(mountStaminaSystem, out Sadle? saddle) ||
            !TryGetMountStamina(saddle, out ResolvedMountStaminaValues? mountStamina) ||
            mountStamina == null)
        {
            return true;
        }

        UseJumpStamina(saddle, mountStamina);
        return false;
    }

    internal static bool AllowMountAttackUpdate()
    {
        Player? localPlayer = Player.m_localPlayer;
        if ((object?)localPlayer == null)
        {
            return true;
        }

        Sadle? saddle = localPlayer.GetDoodadController() as Sadle;
        if ((object?)saddle == null)
        {
            return true;
        }

        int saddleId = saddle.GetInstanceID();
        Character? character = saddle.GetCharacter();
        if ((object?)character == null)
        {
            return true;
        }

        int characterId = character.GetInstanceID();
        if (saddleId == _mountAttackSafetySaddleId &&
            characterId == _mountAttackSafetyCharacterId &&
            Time.frameCount - _mountAttackSafetyFrame <= (_mountAttackSafetyAllowed ? 30 : 5))
        {
            return _mountAttackSafetyAllowed;
        }

        bool allowed = IsMountAttackStateSafe(character);
        _mountAttackSafetySaddleId = saddleId;
        _mountAttackSafetyCharacterId = characterId;
        _mountAttackSafetyFrame = Time.frameCount;
        _mountAttackSafetyAllowed = allowed;
        return allowed;
    }

    internal static bool AllowBlockFlyingDismount(Player player, ref bool result)
    {
        if (!ShouldAllowWaterDismount(player))
        {
            return true;
        }

        result = true;
        return false;
    }

    private static bool IsMountAttackStateSafe(Character character)
    {
        if (character is not Humanoid humanoid)
        {
            return true;
        }

        return (object?)character.m_baseAI != null &&
               humanoid.m_defaultItems != null &&
               humanoid.m_defaultItems.Length >= 2;
    }

    private static bool ShouldAllowWaterDismount(Player player)
    {
        if ((object?)player == null || !player.IsRiding())
        {
            return false;
        }

        Sadle? saddle = player.m_doodadController as Sadle;
        Character? mount = saddle?.GetCharacter();
        return (object?)saddle != null && (IsInWater(player) || IsInWater(mount));
    }

    private static bool IsInWater(Character? character)
    {
        if ((object?)character == null)
        {
            return false;
        }

        return character.InWater() ||
               character.m_waterLevel > character.m_tarLevel &&
               character.m_waterLevel - ((Component)character).transform.position.y > 0.05f;
    }

    private static void ResetPlayerDrawAttack(Player player)
    {
        if (player.m_attackDrawTime <= 0f)
        {
            return;
        }

        ItemDrop.ItemData? weapon = player.GetCurrentWeapon();
        string? drawAnimationState = weapon?.m_shared.m_attack.m_drawAnimationState;
        if (!string.IsNullOrEmpty(drawAnimationState))
        {
            player.m_zanim.SetBool(drawAnimationState, false);
            player.m_zanim.SetFloat("drawpercent", 0f);
        }

        player.m_attackDrawTime = 0f;
    }

    private static bool CanUseAttackStamina(Sadle? saddle, ResolvedMountStaminaValues mountStamina)
    {
        return !GetBetterRidingBoolConfig("EnableAttackStaminaDrain", fallback: true) ||
               CanUseStamina(saddle, mountStamina.AttackStaminaCost);
    }

    private static bool CanUseJumpStamina(Sadle? saddle, ResolvedMountStaminaValues mountStamina)
    {
        return !GetBetterRidingBoolConfig("EnableJumpStaminaDrain", fallback: true) ||
               CanUseStamina(saddle, mountStamina.JumpStaminaCost);
    }

    private static void UseAttackStamina(Sadle? saddle, ResolvedMountStaminaValues mountStamina)
    {
        if (GetBetterRidingBoolConfig("EnableAttackStaminaDrain", fallback: true))
        {
            UseStaminaDirect(saddle, mountStamina.AttackStaminaCost);
        }
    }

    private static void UseJumpStamina(Sadle? saddle, ResolvedMountStaminaValues mountStamina)
    {
        if (GetBetterRidingBoolConfig("EnableJumpStaminaDrain", fallback: true))
        {
            UseStaminaDirect(saddle, mountStamina.JumpStaminaCost);
        }
    }

    private static bool CanUseStamina(Sadle? saddle, float amount)
    {
        return (object?)saddle != null &&
               (object?)saddle.m_nview != null &&
               saddle.m_nview.IsValid() &&
               saddle.GetStamina() >= amount;
    }

    private static void UseStaminaDirect(Sadle? saddle, float amount)
    {
        if (amount <= 0f ||
            (object?)saddle == null ||
            (object?)saddle.m_nview == null ||
            !saddle.m_nview.IsValid() ||
            !saddle.m_nview.IsOwner())
        {
            return;
        }

        float stamina = saddle.GetStamina();
        if (stamina < amount)
        {
            return;
        }

        saddle.SetStamina(Mathf.Max(0f, stamina - amount));
    }

    private static bool TryGetSystemSaddle(object mountStaminaSystem, out Sadle? saddle)
    {
        saddle = null;
        if (mountStaminaSystem == null)
        {
            return false;
        }

        FieldInfo? field = GetMountStaminaSaddleField();
        if (field == null)
        {
            return false;
        }

        saddle = field.GetValue(mountStaminaSystem) as Sadle;
        return (object?)saddle != null;
    }

    private static bool TryFindAttackSaddle(Humanoid humanoid, out Sadle? saddle)
    {
        saddle = null;
        if ((object?)humanoid == null)
        {
            return false;
        }

        if (humanoid is Player rider && rider.IsRiding())
        {
            saddle = rider.m_doodadController as Sadle;
            return (object?)saddle != null;
        }

        Player? localPlayer = Player.m_localPlayer;
        if ((object?)localPlayer == null || !localPlayer.IsRiding())
        {
            return false;
        }

        saddle = localPlayer.m_doodadController as Sadle;
        Character? mount = saddle?.GetCharacter();
        if ((object?)mount == null || !ReferenceEquals(mount, humanoid))
        {
            saddle = null;
            return false;
        }

        return true;
    }

    private static bool TryGetMountStamina(Sadle? saddle, out ResolvedMountStaminaValues? mountStamina)
    {
        mountStamina = null;
        if ((object?)saddle == null)
        {
            return false;
        }

        Character? character = saddle.GetCharacter();
        return (object?)character != null && FeedSystem.TryGetMountStamina(character, out mountStamina);
    }

    private static int GetMountKey(Sadle? saddle)
    {
        if ((object?)saddle == null)
        {
            return 0;
        }

        Character? character = saddle.GetCharacter();
        return (object?)character == null ? 0 : ((Component)character).gameObject.GetInstanceID();
    }

    private static bool GetBetterRidingBoolConfig(string fieldName, bool fallback)
    {
        if (!BetterRidingBoolConfigEntries.TryGetValue(fieldName, out ConfigEntry<bool>? configEntry))
        {
            Type? type = BetterRidingPluginType;
            object? rawValue = type == null ? null : AccessTools.Field(type, fieldName)?.GetValue(null);
            configEntry = rawValue as ConfigEntry<bool>;
            BetterRidingBoolConfigEntries[fieldName] = configEntry;
        }

        return configEntry?.Value ?? fallback;
    }

    private static FieldInfo? GetMountStaminaSaddleField()
    {
        if (!_mountStaminaSaddleFieldSearched)
        {
            Type? type = MountStaminaSystemType;
            _mountStaminaSaddleField = type == null ? null : AccessTools.Field(type, "m_saddle");
            _mountStaminaSaddleFieldSearched = true;
        }

        return _mountStaminaSaddleField;
    }
}

[HarmonyPatch(typeof(Player), nameof(Player.SetControls))]
internal static class BetterRidingSuppressPlayerAttackHoldPatch
{
    private static bool Prepare()
    {
        return FeedBetterRidingCompatibility.HasBetterRiding;
    }

    [HarmonyPriority(Priority.Last)]
    [HarmonyAfter("Yggdrah.BetterRiding")]
    private static void Prefix(
        Player __instance,
        ref bool attack,
        ref bool attackHold,
        ref bool secondaryAttack,
        ref bool secondaryAttackHold)
    {
        FeedBetterRidingCompatibility.SuppressPlayerAttackControlsWhileRiding(
            __instance,
            ref attack,
            ref attackHold,
            ref secondaryAttack,
            ref secondaryAttackHold);
    }
}

[HarmonyPatch(typeof(Player), nameof(Player.PlayerAttackInput))]
internal static class BetterRidingSuppressPlayerAttackInputPatch
{
    private static bool Prepare()
    {
        return FeedBetterRidingCompatibility.HasBetterRiding;
    }

    private static bool Prefix(Player __instance)
    {
        return FeedBetterRidingCompatibility.AllowPlayerAttackInput(__instance);
    }
}

[HarmonyPatch]
internal static class BetterRidingHandleMountedAttackPatch
{
    private static bool Prepare()
    {
        return FeedBetterRidingCompatibility.GetMountStaminaSystemMethod("HandleMountedAttack") != null;
    }

    private static MethodBase? TargetMethod()
    {
        return FeedBetterRidingCompatibility.GetMountStaminaSystemMethod("HandleMountedAttack");
    }

    private static bool Prefix(Humanoid instance, ref bool __result)
    {
        return FeedBetterRidingCompatibility.HandleMountedAttack(instance, ref __result);
    }
}

[HarmonyPatch]
internal static class BetterRidingCanAttackPatch
{
    private static bool Prepare()
    {
        return FeedBetterRidingCompatibility.GetMountStaminaSystemMethod("CanAttack") != null;
    }

    private static MethodBase? TargetMethod()
    {
        return FeedBetterRidingCompatibility.GetMountStaminaSystemMethod("CanAttack");
    }

    private static bool Prefix(object __instance, ref bool __result)
    {
        return FeedBetterRidingCompatibility.CanAttack(__instance, ref __result);
    }
}

[HarmonyPatch]
internal static class BetterRidingCanJumpPatch
{
    private static bool Prepare()
    {
        return FeedBetterRidingCompatibility.GetMountStaminaSystemMethod("CanJump") != null;
    }

    private static MethodBase? TargetMethod()
    {
        return FeedBetterRidingCompatibility.GetMountStaminaSystemMethod("CanJump");
    }

    private static bool Prefix(object __instance, ref bool __result)
    {
        return FeedBetterRidingCompatibility.CanJump(__instance, ref __result);
    }
}

[HarmonyPatch]
internal static class BetterRidingOnAttackStartPatch
{
    private static bool Prepare()
    {
        return FeedBetterRidingCompatibility.GetMountStaminaSystemMethod("OnAttackStart") != null;
    }

    private static MethodBase? TargetMethod()
    {
        return FeedBetterRidingCompatibility.GetMountStaminaSystemMethod("OnAttackStart");
    }

    private static bool Prefix(object __instance)
    {
        return FeedBetterRidingCompatibility.OnAttackStart(__instance);
    }
}

[HarmonyPatch]
internal static class BetterRidingOnJumpStartPatch
{
    private static bool Prepare()
    {
        return FeedBetterRidingCompatibility.GetMountStaminaSystemMethod("OnJumpStart") != null;
    }

    private static MethodBase? TargetMethod()
    {
        return FeedBetterRidingCompatibility.GetMountStaminaSystemMethod("OnJumpStart");
    }

    private static bool Prefix(object __instance)
    {
        return FeedBetterRidingCompatibility.OnJumpStart(__instance);
    }
}

[HarmonyPatch]
internal static class BetterRidingMountAttackSafetyPatch
{
    private static bool Prepare()
    {
        return FeedBetterRidingCompatibility.GetMountAttackUpdate() != null;
    }

    private static MethodBase? TargetMethod()
    {
        return FeedBetterRidingCompatibility.GetMountAttackUpdate();
    }

    private static bool Prefix()
    {
        return FeedBetterRidingCompatibility.AllowMountAttackUpdate();
    }
}

[HarmonyPatch]
internal static class BetterRidingBlockFlyingDismountWaterPatch
{
    private static bool Prepare()
    {
        return FeedBetterRidingCompatibility.GetBetterRidingBlockFlyingDismountPrefix() != null;
    }

    private static MethodBase? TargetMethod()
    {
        return FeedBetterRidingCompatibility.GetBetterRidingBlockFlyingDismountPrefix();
    }

    [HarmonyPriority(Priority.First)]
    private static bool Prefix(Player __0, ref bool __result)
    {
        return FeedBetterRidingCompatibility.AllowBlockFlyingDismount(__0, ref __result);
    }
}

[HarmonyPatch]
internal static class DragonRidersBlockFlyingDismountWaterPatch
{
    private static bool Prepare()
    {
        return FeedBetterRidingCompatibility.GetDragonRidersBlockFlyingDismountPrefix() != null;
    }

    private static MethodBase? TargetMethod()
    {
        return FeedBetterRidingCompatibility.GetDragonRidersBlockFlyingDismountPrefix();
    }

    [HarmonyPriority(Priority.First)]
    private static bool Prefix(Player __0, ref bool __result)
    {
        return FeedBetterRidingCompatibility.AllowBlockFlyingDismount(__0, ref __result);
    }
}
