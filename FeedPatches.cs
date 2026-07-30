using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace FeedLikeGrandma;

[HarmonyPatch(typeof(ObjectDB), nameof(ObjectDB.Awake))]
internal static class ObjectDbAwakePatch
{
    private static void Postfix(ObjectDB __instance)
    {
        TamingOrbSystem.OnGameDataChanged();
        FeedBarrelSystem.OnGameDataChanged();
        FeedLikeGrandmaPlugin.RegisterPersistentPrefabsNow(__instance);
        FeedLikeGrandmaPlugin.QueueRuntimeRefresh(RuntimeRefreshFlags.All);
    }
}

[HarmonyPatch(typeof(ObjectDB), nameof(ObjectDB.CopyOtherDB))]
internal static class ObjectDbCopyPatch
{
    private static void Postfix(ObjectDB __instance)
    {
        TamingOrbSystem.OnGameDataChanged();
        FeedBarrelSystem.OnGameDataChanged();
        FeedLikeGrandmaPlugin.RegisterConfiguredContentNow(__instance);
        FeedLikeGrandmaPlugin.QueueRuntimeRefresh(RuntimeRefreshFlags.All);
    }
}

[HarmonyPatch(typeof(ZNetScene), nameof(ZNetScene.Awake))]
internal static class ZNetSceneAwakePatch
{
    private static void Postfix()
    {
        TamingOrbSystem.OnGameDataChanged();
        FeedBarrelSystem.OnGameDataChanged();
        FeedLikeGrandmaPlugin.RegisterPersistentPrefabsNow(ObjectDB.instance);
        FeedLikeGrandmaPlugin.QueueRuntimeRefresh(RuntimeRefreshFlags.All);
    }
}

[HarmonyPatch(typeof(ZNetView), nameof(ZNetView.Awake))]
internal static class ZNetViewAwakePatch
{
    private static void Postfix(ZNetView __instance)
    {
        FeedProcreationSystem.RegisterSpawnedInstance(__instance);
    }
}

[HarmonyPatch(typeof(ZNetView), nameof(ZNetView.OnDestroy))]
internal static class ZNetViewOnDestroyPatch
{
    private static void Prefix(ZNetView __instance)
    {
        FeedProcreationSystem.DeregisterSpawnedInstance(__instance);
        FeedSystem.DeregisterView(__instance);
    }
}

[HarmonyPatch(typeof(Player), nameof(Player.Awake))]
internal static class PlayerAwakePatch
{
    private static void Postfix(Player __instance)
    {
        FeedLikeGrandmaPlugin.QueueRuntimeRefresh(RuntimeRefreshFlags.FeedBarrel);
        FeedSystem.RegisterPlayerRpc(__instance);
    }
}

[HarmonyPatch(typeof(Tameable), nameof(Tameable.Awake))]
internal static class TameableAwakePatch
{
    private static void Postfix(Tameable __instance)
    {
        FeedSystem.RegisterTargetRpc(__instance.m_nview);
    }
}

[HarmonyPatch(typeof(Growup), nameof(Growup.Start))]
internal static class GrowupStartPatch
{
    private static void Postfix(Growup __instance)
    {
        FeedSystem.RegisterTargetRpc(__instance.m_nview);
    }
}

[HarmonyPatch(typeof(Container), nameof(Container.Awake))]
internal static class ContainerAwakePatch
{
    private static void Postfix(Container __instance)
    {
        FeedAutoFeedSystem.RegisterContainer(__instance);
    }
}

[HarmonyPatch(typeof(Container), "OnDestroyed")]
internal static class ContainerOnDestroyedPatch
{
    private static void Prefix(Container __instance)
    {
        FeedAutoFeedSystem.DeregisterContainer(__instance);
    }
}

[HarmonyPatch(typeof(Tameable), nameof(Tameable.UseItem))]
internal static class TameableUseItemPatch
{
    private static bool Prefix(Tameable __instance, Humanoid user, ItemDrop.ItemData item, ref bool __result)
    {
        if (TamingOrbSystem.TryHandleTameableUseItem(__instance, user, item))
        {
            __result = true;
            return false;
        }

        if (!FeedSystem.TryHandleTameableUseItem(__instance, user, item))
        {
            return true;
        }

        __result = true;
        return false;
    }
}

[HarmonyPatch(typeof(MonsterAI), nameof(MonsterAI.FindClosestConsumableItem))]
internal static class MonsterAIFindClosestConsumableItemPatch
{
    private static void Postfix(MonsterAI __instance, float maxRange, ref ItemDrop __result)
    {
        if ((object?)__result != null)
        {
            return;
        }

        if (FeedAutoFeedSystem.TryFindClosestConsumableFromFeedBarrel(__instance, maxRange, out ItemDrop? consumable) &&
            (object?)consumable != null)
        {
            __result = consumable;
        }
    }
}

[HarmonyPatch(typeof(Tameable), nameof(Tameable.OnConsumedItem))]
internal static class TameableOnConsumedItemPatch
{
    private struct AutomaticFeedState
    {
        public bool Handled;
        public float TargetHealth;
    }

    [HarmonyPriority(Priority.First)]
    private static bool Prefix(Tameable __instance, ItemDrop item, ref AutomaticFeedState __state)
    {
        if (!FeedSystem.TryHandleAutomaticConsumedItem(__instance, item, out float targetHealth))
        {
            return true;
        }

        __state = new AutomaticFeedState
        {
            Handled = true,
            TargetHealth = targetHealth
        };
        return false;
    }

    [HarmonyPriority(Priority.Last)]
    [HarmonyAfter("Yggdrah.BetterRiding")]
    private static void Postfix(Tameable __instance, AutomaticFeedState __state)
    {
        if (!__state.Handled ||
            FeedLikeGrandmaPlugin.BetterRidingFeedHealPriority.Value != FeedLikeGrandmaPlugin.Toggle.On ||
            (object?)__instance == null)
        {
            return;
        }

        Character? character = ((Component)__instance).GetComponent<Character>();
        if ((object?)character == null ||
            (object?)character.m_nview == null ||
            !character.m_nview.IsValid() ||
            !character.m_nview.IsOwner() ||
            character.IsDead())
        {
            return;
        }

        character.SetHealth(Mathf.Clamp(__state.TargetHealth, 0f, character.GetMaxHealth()));
    }
}

[HarmonyPatch(typeof(MonsterAI), nameof(MonsterAI.CanConsume))]
internal static class MonsterAICanConsumePatch
{
    private static bool Prefix(MonsterAI __instance, ItemDrop.ItemData item, ref bool __result)
    {
        if (!FeedSystem.TryCanAutoConsume(__instance, item, out bool canConsume))
        {
            return true;
        }

        __result = canConsume;
        return false;
    }
}

[HarmonyPatch(typeof(Humanoid), nameof(Humanoid.UseItem))]
internal static class HumanoidUseItemPatch
{
    private static bool Prefix(Humanoid __instance, Inventory inventory, ItemDrop.ItemData item, bool fromInventoryGui)
    {
        if (!fromInventoryGui && __instance is Player player && TamingOrbSystem.TryHandlePlayerUseItem(player, item))
        {
            return false;
        }

        if (fromInventoryGui)
        {
            return true;
        }

        return !FeedSystem.TryHandleGrowupUseItem(__instance, __instance.GetHoverObject(), item);
    }
}

[HarmonyPatch(typeof(Player), "UseHotbarItem")]
internal static class PlayerUseHotbarItemPatch
{
    private static bool Prefix(Player __instance, int index)
    {
        ItemDrop.ItemData item = __instance.GetInventory().GetItemAt(index - 1, 0);
        return !TamingOrbSystem.TryHandlePlayerUseItem(__instance, item);
    }
}

[HarmonyPatch(typeof(ItemDrop.ItemData), "GetTooltip", new[]
{
    typeof(ItemDrop.ItemData),
    typeof(int),
    typeof(bool),
    typeof(float),
    typeof(int)
})]
internal static class ItemDataGetTooltipPatch
{
    private static void Postfix(ItemDrop.ItemData item, bool crafting, ref string __result)
    {
        TamingOrbSystem.AppendTooltip(item, crafting, ref __result);
    }
}

[HarmonyPatch(typeof(ItemDrop.ItemData), nameof(ItemDrop.ItemData.GetStatusEffectTooltip))]
internal static class ItemDataGetStatusEffectTooltipPatch
{
    private static void Postfix(ItemDrop.ItemData __instance, ref string __result)
    {
        if (!MeadTamerTooltipSystem.TryBuildItemStatusEffectTooltip(__instance, out string tooltip))
        {
            return;
        }

        __result = tooltip;
    }
}

[HarmonyPatch]
internal static class StatusEffectGetTooltipStringPatch
{
    private static IEnumerable<MethodBase> TargetMethods()
    {
        MethodInfo? baseMethod = AccessTools.Method(typeof(StatusEffect), nameof(StatusEffect.GetTooltipString));
        if (baseMethod != null)
        {
            yield return baseMethod;
        }

        MethodInfo? statsMethod = AccessTools.Method(typeof(SE_Stats), nameof(SE_Stats.GetTooltipString));
        if (statsMethod != null)
        {
            yield return statsMethod;
        }
    }

    private static void Postfix(StatusEffect __instance, ref string __result)
    {
        if (!MeadTamerTooltipSystem.IsTamerPotion(__instance))
        {
            return;
        }

        __result = MeadTamerTooltipSystem.BuildTamerTooltip();
    }
}

internal static class MeadTamerTooltipSystem
{
    private const string TamerPotionStatusEffectName = "Potion_tamer";

    internal static bool TryBuildItemStatusEffectTooltip(ItemDrop.ItemData item, out string tooltip)
    {
        tooltip = string.Empty;
        StatusEffect? effect = item?.m_shared?.m_consumeStatusEffect;
        if (!IsTamerPotion(effect))
        {
            return false;
        }

        string effectName = Localization.instance != null ? Localization.instance.Localize(effect!.m_name) : effect!.m_name;
        tooltip = $"<color=orange>{effectName}</color>\n{BuildTamerTooltip()}";
        return true;
    }

    internal static bool IsTamerPotion(StatusEffect? statusEffect)
    {
        if ((object?)statusEffect == null)
        {
            return false;
        }

        string objectName = NormalizeUnityObjectName(statusEffect.name);
        return objectName.Equals(TamerPotionStatusEffectName, StringComparison.OrdinalIgnoreCase);
    }

    internal static string BuildTamerTooltip()
    {
        return
            FeedLocalization.Text("$flg_mead_tamer_tooltip_header") + "\n" +
            FeedLocalization.Format("$flg_mead_tamer_tooltip_passive_tame", FormatMultiplier(FeedLikeGrandmaPlugin.GetTamingBoostMultiplier())) + "\n" +
            FeedLocalization.Format("$flg_mead_tamer_tooltip_passive_growth", FormatMultiplier(FeedLikeGrandmaPlugin.GetGrowthBoostMultiplier())) + "\n" +
            FeedLocalization.Format("$flg_mead_tamer_tooltip_feed", FormatMultiplier(FeedLikeGrandmaPlugin.GetFeedBoostMultiplier())) + "\n" +
            FeedLocalization.Text("$flg_mead_tamer_tooltip_feed_note");
    }

    private static string FormatMultiplier(float value)
    {
        return "x" + Math.Max(1f, value).ToString("0.##", CultureInfo.InvariantCulture);
    }

    private static string NormalizeUnityObjectName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return string.Empty;
        }

        const string cloneSuffix = "(Clone)";
        return name.EndsWith(cloneSuffix, StringComparison.Ordinal)
            ? name.Substring(0, name.Length - cloneSuffix.Length).Trim()
            : name.Trim();
    }
}

[HarmonyPatch(typeof(ItemDrop.ItemData), nameof(ItemDrop.ItemData.GetIcon))]
internal static class ItemDataGetIconPatch
{
    private static bool Prefix(ItemDrop.ItemData __instance, ref Sprite __result)
    {
        if (!TamingOrbSystem.TryGetIcon(__instance, out Sprite? icon))
        {
            return true;
        }

        __result = icon!;
        return false;
    }
}

[HarmonyPatch(typeof(HotkeyBar), nameof(HotkeyBar.UpdateIcons))]
internal static class HotkeyBarUpdateIconsPatch
{
    [HarmonyPriority(Priority.Last)]
    private static void Postfix(HotkeyBar __instance)
    {
        if (__instance.m_items == null || __instance.m_elements == null)
        {
            return;
        }

        foreach (ItemDrop.ItemData item in __instance.m_items)
        {
            if ((object?)item == null)
            {
                continue;
            }

            int elementIndex = item.m_gridPos.x;
            if (elementIndex < 0 ||
                elementIndex >= __instance.m_elements.Count ||
                !TamingOrbSystem.TryGetIcon(item, out Sprite? icon) ||
                (object?)icon == null)
            {
                continue;
            }

            HotkeyBar.ElementData element = __instance.m_elements[elementIndex];
            if (element?.m_icon != null && element.m_used)
            {
                element.m_icon.sprite = icon;
            }
        }
    }
}

[HarmonyPatch(typeof(ItemDrop), nameof(ItemDrop.Load))]
internal static class ItemDropLoadPatch
{
    private static void Postfix(ItemDrop __instance)
    {
        TamingOrbSystem.NormalizeWorldItemVariant(__instance);
    }
}

[HarmonyPatch(typeof(ItemDrop), nameof(ItemDrop.GetHoverText))]
internal static class ItemDropHoverTextPatch
{
    private static void Postfix(ItemDrop __instance, ref string __result)
    {
        __result = TamingOrbSystem.AugmentDroppedItemHoverText(__instance, __result);
    }
}

[HarmonyPatch(typeof(Character), nameof(Character.GetHoverText))]
internal static class CharacterHoverTextPatch
{
    private static void Postfix(Character __instance, ref string __result)
    {
        __result = FeedSystem.AugmentHoverText(__instance, __result);
    }
}

[HarmonyPatch(typeof(EggGrow), nameof(EggGrow.GetHoverText))]
internal static class EggGrowHoverTextPatch
{
    private static void Postfix(EggGrow __instance, ref string __result)
    {
        __result = FeedSystem.AugmentEggGrowHoverText(__instance, __result);
    }
}

[HarmonyPatch(typeof(EnemyHud), nameof(EnemyHud.TestShow))]
internal static class EnemyHudTestShowPatch
{
    private static bool Prefix(Character c, ref bool __result)
    {
        if ((object?)c != null && c != null)
        {
            return true;
        }

        __result = false;
        return false;
    }
}

[HarmonyPatch(typeof(ZSyncTransform), nameof(ZSyncTransform.Awake))]
internal static class ZSyncTransformAwakePatch
{
    private static bool Prefix(ZSyncTransform __instance)
    {
        if (!TamingOrbSystem.ShouldSkipInventoryCloneWorldAwake(__instance))
        {
            return true;
        }

        __instance.enabled = false;
        return false;
    }
}

[HarmonyPatch(typeof(Floating), nameof(Floating.Awake))]
internal static class FloatingAwakePatch
{
    private static bool Prefix(Floating __instance)
    {
        if (!TamingOrbSystem.ShouldSkipInventoryCloneWorldAwake(__instance))
        {
            return true;
        }

        __instance.enabled = false;
        return false;
    }
}

[HarmonyPatch(typeof(Tameable), nameof(Tameable.DecreaseRemainingTime))]
internal static class TameableDecreaseRemainingTimePatch
{
    private static void Prefix(Tameable __instance)
    {
        FeedSystem.ApplyConfiguredTamingMultiplier(__instance);
    }
}

[HarmonyPatch(typeof(Tameable), nameof(Tameable.IsHungry))]
internal static class TameableIsHungryPatch
{
    private static bool Prefix(Tameable __instance, ref bool __result)
    {
        if (!FeedSystem.TryGetHungryState(__instance, out bool isHungry))
        {
            return true;
        }

        __result = isHungry;
        return false;
    }
}

[HarmonyPatch(typeof(Growup), nameof(Growup.GrowUpdate))]
internal static class GrowupGrowUpdatePatch
{
    private static bool Prefix(Growup __instance)
    {
        if (!FeedSystem.HasRuleFor(((UnityEngine.Component)__instance).gameObject))
        {
            return true;
        }

        return FeedSystem.HandleGrowUpdate(__instance);
    }
}

[HarmonyPatch(typeof(Procreation), nameof(Procreation.Procreate))]
internal static class ProcreationProcreatePatch
{
    private static bool Prefix(Procreation __instance)
    {
        return FeedProcreationSystem.ShouldRunProcreation(__instance);
    }
}

[HarmonyPatch(typeof(Hud), nameof(Hud.UpdateCrosshair))]
internal static class HudUpdateCrosshairPatch
{
    private static void Postfix(Player player)
    {
        if ((object)player != null)
        {
            FeedSystem.UpdateHoverIcons(player);
        }
    }
}
