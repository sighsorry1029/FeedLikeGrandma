using HarmonyLib;
using UnityEngine;
using Exception = System.Exception;

namespace FeedLikeGrandma;

internal static class FeedRidingSystem
{
    private static int _encumberedPlayerId;
    private static bool _encumberedCacheValid;
    private static bool _encumberedCached;

    internal static void Shutdown()
    {
        _encumberedPlayerId = 0;
        _encumberedCacheValid = false;
        _encumberedCached = false;
    }

    internal static bool ShouldSkipMountCameraTilt(Player player)
    {
        return FeedLikeGrandmaPlugin.StableMountCamera.Value.IsOn() &&
               (object?)player != null &&
               player.IsRiding();
    }

    internal static bool IsLocalRiderEncumbered()
    {
        Player? player = Player.m_localPlayer;
        return (object?)player != null && IsPlayerEncumberedCached(player);
    }

    internal static bool IsPlayerEncumberedCached(Player player)
    {
        if ((object?)player == null)
        {
            return false;
        }

        int playerId = ((Object)player).GetInstanceID();
        if (!_encumberedCacheValid || _encumberedPlayerId != playerId)
        {
            RefreshPlayerEncumbered(player);
        }

        return _encumberedPlayerId == playerId && _encumberedCached;
    }

    internal static void RefreshPlayerEncumbered(Player player)
    {
        if ((object?)player == null)
        {
            return;
        }

        _encumberedPlayerId = ((Object)player).GetInstanceID();
        _encumberedCached = player.m_inventory.GetTotalWeight() > player.GetMaxCarryWeight();
        _encumberedCacheValid = true;
    }

    internal static bool ShouldTreatMountAsEncumbered(Character character)
    {
        if (FeedLikeGrandmaPlugin.EncumberedMountSpeedReduction.Value != FeedLikeGrandmaPlugin.Toggle.On ||
            (object?)character == null)
        {
            return false;
        }

        Sadle? saddle = character.m_baseAI?.m_tamable?.m_saddle;
        return IsLocalRiderEncumbered(saddle);
    }

    internal static void ApplyStaminaCostMultiplier(Sadle saddle, ref float staminaCost)
    {
        if ((object?)saddle == null ||
            (object?)saddle.m_nview == null ||
            !saddle.m_nview.IsValid() ||
            !saddle.m_nview.IsOwner())
        {
            return;
        }

        float multiplier = IsLocalRiderEncumbered(saddle)
            ? FeedLikeGrandmaPlugin.EncumberedMountStaminaDrainMultiplier.Value
            : FeedLikeGrandmaPlugin.MountStaminaDrainMultiplier.Value;
        staminaCost *= Mathf.Max(0f, multiplier);
    }

    internal static void ApplyStaminaRegenMultiplier(Sadle saddle, ref StaminaRegenState state)
    {
        state = new StaminaRegenState
        {
            Applied = true,
            Regen = saddle.m_staminaRegen,
            RegenHungry = saddle.m_staminaRegenHungry
        };

        float multiplier = IsLocalRiderEncumbered(saddle)
            ? FeedLikeGrandmaPlugin.EncumberedMountStaminaRegenMultiplier.Value
            : FeedLikeGrandmaPlugin.MountStaminaRegenMultiplier.Value;
        float ridingSkill = Mathf.Clamp01(saddle.GetRiderSkill());
        float skillMultiplierAt100 = Mathf.Max(1f, FeedLikeGrandmaPlugin.RidingSkillStaminaRegenMultiplierAt100.Value);
        float skillMultiplier = Mathf.Lerp(1f, skillMultiplierAt100, ridingSkill);
        multiplier = Mathf.Max(0f, multiplier) * skillMultiplier;
        saddle.m_staminaRegen *= multiplier;
        saddle.m_staminaRegenHungry *= multiplier;
    }

    internal static void RestoreStaminaRegen(Sadle saddle, StaminaRegenState state)
    {
        if (!state.Applied || (object?)saddle == null)
        {
            return;
        }

        saddle.m_staminaRegen = state.Regen;
        saddle.m_staminaRegenHungry = state.RegenHungry;
    }

    internal static void DrainEncumberedWalkStamina(Sadle saddle, float dt, bool updatedRiding)
    {
        if (!updatedRiding ||
            FeedLikeGrandmaPlugin.EncumberedWalkStaminaDrain.Value != FeedLikeGrandmaPlugin.Toggle.On ||
            (object?)saddle == null ||
            saddle.m_speed != Sadle.Speed.Walk ||
            !IsLocalRiderEncumbered(saddle))
        {
            return;
        }

        float riderSkillRatio = Mathf.Lerp(1f, 0.5f, saddle.GetRiderSkill());
        saddle.UseStamina(saddle.m_runStaminaDrain * riderSkillRatio * dt);
    }

    private static bool IsLocalRiderEncumbered(Sadle? saddle)
    {
        return (object?)saddle != null && saddle.IsLocalUser() && IsLocalRiderEncumbered();
    }

    internal readonly struct StaminaRegenState
    {
        internal bool Applied { get; init; }
        internal float Regen { get; init; }
        internal float RegenHungry { get; init; }
    }
}

[HarmonyPatch(typeof(GameCamera), nameof(GameCamera.ApplyCameraTilt))]
internal static class GameCameraApplyCameraTiltPatch
{
    private static bool Prefix(Player player)
    {
        return !FeedRidingSystem.ShouldSkipMountCameraTilt(player);
    }
}

[HarmonyPatch(typeof(Sadle), nameof(Sadle.UseStamina))]
internal static class SaddleUseStaminaPatch
{
    private static void Prefix(Sadle __instance, ref float v)
    {
        FeedRidingSystem.ApplyStaminaCostMultiplier(__instance, ref v);
    }
}

[HarmonyPatch(typeof(Sadle), nameof(Sadle.UpdateStamina))]
internal static class SaddleUpdateStaminaPatch
{
    private static void Prefix(Sadle __instance, ref FeedRidingSystem.StaminaRegenState __state)
    {
        FeedRidingSystem.ApplyStaminaRegenMultiplier(__instance, ref __state);
    }

    private static Exception? Finalizer(
        Sadle __instance,
        FeedRidingSystem.StaminaRegenState __state,
        Exception? __exception)
    {
        FeedRidingSystem.RestoreStaminaRegen(__instance, __state);
        return __exception;
    }
}

[HarmonyPatch(typeof(Sadle), nameof(Sadle.UpdateRiding))]
internal static class SaddleUpdateRidingPatch
{
    private static void Postfix(Sadle __instance, float dt, bool __result)
    {
        FeedRidingSystem.DrainEncumberedWalkStamina(__instance, dt, __result);
    }
}

[HarmonyPatch(typeof(Character), nameof(Character.IsEncumbered))]
internal static class CharacterIsEncumberedForMountPatch
{
    private static void Postfix(Character __instance, ref bool __result)
    {
        if (FeedRidingSystem.ShouldTreatMountAsEncumbered(__instance))
        {
            __result = true;
        }
    }
}

[HarmonyPatch(typeof(Player), nameof(Player.OnInventoryChanged))]
internal static class PlayerOnInventoryChangedEncumberedCachePatch
{
    private static void Postfix(Player __instance)
    {
        if (__instance.IsRiding())
        {
            FeedRidingSystem.RefreshPlayerEncumbered(__instance);
        }
    }
}

[HarmonyPatch(typeof(Player), nameof(Player.StartDoodadControl))]
internal static class PlayerStartDoodadControlEncumberedCachePatch
{
    private static void Postfix(Player __instance)
    {
        FeedRidingSystem.RefreshPlayerEncumbered(__instance);
    }
}
