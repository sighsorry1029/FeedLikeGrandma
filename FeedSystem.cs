using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using UnityEngine;

namespace FeedLikeGrandma;

internal sealed class FeedConsumeData
{
    public bool InheritedConsumeItems { get; init; }
    public List<ItemDrop> ConsumeItems { get; init; } = new();
}

internal static class FeedSystem
{
    private static readonly int StarProgressHash = StringExtensionMethods.GetStableHashCode("flg_star_progress");
    private static readonly int LevelScaledTameTotalHash = StringExtensionMethods.GetStableHashCode("flg_level_scaled_tame_total");
    private static readonly int LevelScaledTameBaseHash = StringExtensionMethods.GetStableHashCode("flg_level_scaled_tame_base");
    private static readonly int LevelScaledTameFactorHash = StringExtensionMethods.GetStableHashCode("flg_level_scaled_tame_factor");
    private static readonly int LevelScaledTameLevelHash = StringExtensionMethods.GetStableHashCode("flg_level_scaled_tame_level");
    private static readonly int GrowthElapsedHash = StringExtensionMethods.GetStableHashCode("flg_growth_elapsed");
    private static readonly int GrowthLastEvalHash = StringExtensionMethods.GetStableHashCode("flg_growth_last_eval");
    private static readonly int FeedCooldownHash = StringExtensionMethods.GetStableHashCode("flg_feed_cooldown");

    private const float MeadBoostRange = 60f;
    private const string FeedRequestRpc = "FLG_FeedRequest";
    private const string FeedResponseRpc = "FLG_FeedResponse";
    private const string TamedProgressionEffectPrefab = "fx_creature_tamed";
    private const long PendingRequestTimeoutTicks = TimeSpan.TicksPerSecond * 30;
    private const long CompletedRequestRetentionTicks = TimeSpan.TicksPerSecond * 60;
    private const int MaxCompletedRequestCacheEntries = 1024;
    private const float FeedRequestDistanceTolerance = 1f;

    private static readonly Dictionary<string, RawFeedingRule> RawRules = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, float> FoodNumbers = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, ResolvedFeedingRule> ResolvedRules = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<int, PendingFeedRequest> PendingRequests = new();
    private static readonly Dictionary<(long Sender, int RequestId), CompletedFeedRequest> CompletedFeedRequests = new();
    private static readonly HashSet<int> RegisteredTargetViews = new();
    private static readonly HashSet<int> RegisteredPlayerViews = new();
    private static readonly List<Player> NearbyTamingBoostPlayers = new();
    private static readonly HashSet<string> ExcludedPrefabNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Placeable_HardRock",
        "Player"
    };

    private static RawFeedValues FeedDefaults = new();
    private static int _nextRequestId = 1;
    private static int _hoverTextTargetId;
    private static int _hoverTextTimeBucket = -1;
    private static int _hoverTextUiState = -1;
    private static string _hoverTextOriginal = string.Empty;
    private static string _hoverTextLanguage = string.Empty;
    private static string _hoverTextResult = string.Empty;
    private static int _eggCoverTargetId;
    private static int _eggCoverTimeBucket = -1;
    private static bool _eggNeedsMoreCover;

    internal static void Shutdown()
    {
        RawRules.Clear();
        FoodNumbers.Clear();
        ResolvedRules.Clear();
        PendingRequests.Clear();
        CompletedFeedRequests.Clear();
        RegisteredTargetViews.Clear();
        RegisteredPlayerViews.Clear();
        NearbyTamingBoostPlayers.Clear();
        FeedDefaults = new RawFeedValues();
        _nextRequestId = 1;
        InvalidateHoverTextCache();
    }

    internal static bool LoadYamlText(string yamlText, string source)
    {
        List<string> warnings = new();
        RawFeedConfig parsedConfig;

        try
        {
            parsedConfig = FeedingYamlParser.Parse(yamlText, warnings);
        }
        catch (Exception ex)
        {
            FeedLikeGrandmaPlugin.Log.LogError($"Failed to parse YAML from {source}: {ex}");
            return false;
        }

        RawRules.Clear();
        FoodNumbers.Clear();
        FeedDefaults = parsedConfig.Defaults;
        foreach (KeyValuePair<string, float> entry in parsedConfig.FoodNumbers)
        {
            FoodNumbers[entry.Key] = entry.Value;
        }

        foreach (KeyValuePair<string, RawFeedingRule> entry in parsedConfig.Rules)
        {
            RawRules[entry.Key] = entry.Value;
        }

        FeedLikeGrandmaPlugin.Log.LogInfo($"Loaded {RawRules.Count} feed rule(s) and {FoodNumbers.Count} global food number(s) from {source}.");
        foreach (string warning in warnings)
        {
            FeedLikeGrandmaPlugin.Log.LogWarning(warning);
        }

        return true;
    }

    internal static void RebuildRuntimeData()
    {
        InvalidateHoverTextCache();
        ResolvedRules.Clear();
        if ((object)ObjectDB.instance == null || (object)ZNetScene.instance == null)
        {
            return;
        }

        FeedExternalReferenceSupport.RefreshSnapshot();
        List<GameObject> loadedPrefabs = EnumerateLoadedPrefabs().ToList();
        FeedReferenceWriter.CaptureMissingReferenceSnapshots(loadedPrefabs);
        FeedReferenceWriter.RestoreReferenceSnapshots(loadedPrefabs);
        ApplyGlobalEggHatchTimeMultiplier(loadedPrefabs);

        foreach (KeyValuePair<string, RawFeedingRule> entry in RawRules)
        {
            GameObject? prefab = FindLoadedPrefab(entry.Key, loadedPrefabs);
            if ((object?)prefab != null && !ShouldIgnorePrefab(prefab))
            {
                ApplyConfiguredPrefabData(prefab, entry.Value);
            }
        }

        ApplyInheritedConsumeItemsForLoadedPrefabs();

        foreach (KeyValuePair<string, RawFeedingRule> entry in RawRules)
        {
            string prefabName = entry.Key;
            RawFeedingRule rawRule = entry.Value;
            GameObject? prefab = FindLoadedPrefab(prefabName, loadedPrefabs);
            if ((object?)prefab == null)
            {
                FeedLikeGrandmaPlugin.Log.LogWarning($"Configured prefab '{prefabName}' was not found in loaded prefabs.");
                continue;
            }

            if (ShouldIgnorePrefab(prefab))
            {
                continue;
            }

            if (TryBuildResolvedRule(prefabName, prefab, rawRule, explicitRule: true, out ResolvedFeedingRule? resolvedRule) && resolvedRule != null)
            {
                ResolvedRules[prefabName] = resolvedRule;
            }
        }

        foreach (GameObject prefab in loadedPrefabs)
        {
            string prefabName = NormalizePrefabName(prefab.name);
            if (ResolvedRules.ContainsKey(prefabName) || !HasImplicitFeedRule(prefab))
            {
                continue;
            }

            RawFeedingRule rawRule = new() { PrefabName = prefabName };
            if (TryBuildResolvedRule(prefabName, prefab, rawRule, explicitRule: false, out ResolvedFeedingRule? resolvedRule) && resolvedRule != null)
            {
                ResolvedRules[prefabName] = resolvedRule;
            }
        }

    }

    internal static void DeregisterView(ZNetView? nview)
    {
        if ((object?)nview == null)
        {
            return;
        }

        int instanceId = nview.GetInstanceID();
        RegisteredTargetViews.Remove(instanceId);
        RegisteredPlayerViews.Remove(instanceId);
    }

    private static GameObject? FindLoadedPrefab(string prefabName, IEnumerable<GameObject> loadedPrefabs)
    {
        string normalizedName = NormalizePrefabName(prefabName);
        GameObject? prefab = ZNetScene.instance?.GetPrefab(normalizedName);
        if ((object?)prefab != null)
        {
            return prefab;
        }

        return loadedPrefabs.FirstOrDefault(candidate =>
            (object?)candidate != null &&
            NormalizePrefabName(candidate.name).Equals(normalizedName, StringComparison.OrdinalIgnoreCase));
    }

    private static bool TryBuildResolvedRule(string prefabName, GameObject prefab, RawFeedingRule rawRule, bool explicitRule, out ResolvedFeedingRule? resolvedRule)
    {
        resolvedRule = null;
        Tameable? tameable = prefab.GetComponent<Tameable>();
        Growup? growup = prefab.GetComponent<Growup>();
        EggGrow? eggGrow = prefab.GetComponent<EggGrow>();
        if ((object?)tameable == null && (object?)growup == null)
        {
            if (explicitRule)
            {
                if (IsEggGrowOnlyConfigRule(rawRule, eggGrow))
                {
                    return false;
                }

                FeedLikeGrandmaPlugin.Log.LogWarning($"Configured prefab '{prefabName}' is neither Tameable nor Growup. Skipping.");
            }

            return false;
        }

        List<RawFoodEntry> effectiveFoods = GetEffectiveFoodEntries(prefab, rawRule);
        HashSet<string> validFoodNames = CollectValidFoodNames(prefab, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        validFoodNames.UnionWith(effectiveFoods.Select(food => food.PrefabName));
        RawFeedValues feedValues = rawRule.Feed ?? FeedDefaults;
        List<ResolvedFoodEntry> resolvedFoods = ResolveFoodEntries(
            prefabName,
            effectiveFoods,
            validFoodNames,
            explicitRule,
            validateNativeFood: true);
        Dictionary<int, List<ResolvedFoodEntry>> levelUpItems = ResolveLevelUpItems(prefabName, rawRule, explicitRule);
        ResolvedMountStaminaValues? mountStamina = ResolveMountStamina(rawRule.MountStamina);
        List<string> restrictedBiomes = ResolveRestrictedBiomes(rawRule);

        if (!explicitRule && resolvedFoods.Count == 0 && levelUpItems.Count == 0 && mountStamina == null && !rawRule.HasRestrictedBiomeOverride)
        {
            return false;
        }

        resolvedRule = new ResolvedFeedingRule
        {
            Prefab = prefab,
            LevelUpConstant = Math.Max(0f, feedValues.LevelUpConstant),
            TamingConstant = Math.Max(0f, feedValues.TamingConstant),
            GrowthConstant = Math.Max(0f, feedValues.GrowthConstant),
            HealConstant = Math.Max(0f, feedValues.HealConstant),
            FedConstant = Math.Max(0f, feedValues.FedConstant),
            Foods = resolvedFoods,
            LevelUpItems = levelUpItems,
            MountStamina = mountStamina,
            HasRestrictedBiomeOverride = rawRule.HasRestrictedBiomeOverride,
            RestrictedBiomes = restrictedBiomes
        };
        return true;
    }

    private static List<string> ResolveRestrictedBiomes(RawFeedingRule rawRule)
    {
        if (!rawRule.HasRestrictedBiomeOverride || rawRule.RestrictedBiomes.Count == 0)
        {
            return new List<string>();
        }

        return rawRule.RestrictedBiomes
            .Select(value => value.Trim())
            .Where(value => value.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static ResolvedMountStaminaValues? ResolveMountStamina(RawMountStaminaValues? rawMountStamina)
    {
        if (rawMountStamina == null)
        {
            return null;
        }

        return new ResolvedMountStaminaValues
        {
            AttackStaminaCost = Mathf.Max(0f, rawMountStamina.AttackStaminaCost),
            JumpStaminaCost = Mathf.Max(0f, rawMountStamina.JumpStaminaCost),
            AttackCooldown = Mathf.Max(0f, rawMountStamina.AttackCooldown)
        };
    }

    private static Dictionary<int, List<ResolvedFoodEntry>> ResolveLevelUpItems(string prefabName, RawFeedingRule rawRule, bool explicitRule)
    {
        Dictionary<int, List<ResolvedFoodEntry>> resolved = new();
        foreach (KeyValuePair<int, List<RawFoodEntry>> entry in rawRule.LevelUpItems)
        {
            List<ResolvedFoodEntry> foods = ResolveFoodEntries(
                prefabName,
                entry.Value,
                validFoodNames: null,
                explicitRule,
                validateNativeFood: false);
            resolved[Math.Max(1, entry.Key)] = foods;
        }

        return resolved;
    }

    private static List<ResolvedFoodEntry> ResolveFoodEntries(
        string prefabName,
        IEnumerable<RawFoodEntry> rawFoods,
        HashSet<string>? validFoodNames,
        bool explicitRule,
        bool validateNativeFood)
    {
        List<ResolvedFoodEntry> resolvedFoods = new();
        foreach (RawFoodEntry rawFood in rawFoods)
        {
            string foodKey = FeedingYamlParser.FormatFoodKey(rawFood.PrefabName, rawFood.RequiredQuality);
            float foodValue = ResolveFoodValue(rawFood);

            if (validateNativeFood && validFoodNames != null && !validFoodNames.Contains(rawFood.PrefabName))
            {
                if (explicitRule)
                {
                    FeedLikeGrandmaPlugin.Log.LogWarning(
                        $"Ignoring food '{foodKey}' for prefab '{prefabName}' because it is not valid native food for that creature chain.");
                }

                continue;
            }

            GameObject? itemPrefab = ObjectDB.instance.GetItemPrefab(rawFood.PrefabName);
            if ((object?)itemPrefab == null)
            {
                if (explicitRule)
                {
                    FeedLikeGrandmaPlugin.Log.LogWarning($"Configured food '{foodKey}' for prefab '{prefabName}' was not found in ObjectDB.");
                }

                continue;
            }

            ItemDrop? itemDrop = itemPrefab.GetComponent<ItemDrop>();
            if ((object?)itemDrop == null)
            {
                if (explicitRule)
                {
                    FeedLikeGrandmaPlugin.Log.LogWarning($"Configured food '{foodKey}' for prefab '{prefabName}' is not an item prefab.");
                }

                continue;
            }

            resolvedFoods.Add(new ResolvedFoodEntry
            {
                PrefabName = itemPrefab.name,
                RequiredQuality = rawFood.RequiredQuality,
                Value = foodValue,
                Icon = itemDrop.m_itemData.GetIcon()
            });
        }

        return resolvedFoods;
    }

    private static bool HasImplicitFeedRule(GameObject prefab)
    {
        if ((object?)prefab.GetComponent<Tameable>() == null && (object?)prefab.GetComponent<Growup>() == null)
        {
            return false;
        }

        return ResolveConsumeData(prefab).ConsumeItems.Count > 0;
    }

    private static int GetMaxAllowedLevel(FeedTargetContext context)
    {
        if (FeedLikeGrandmaPlugin.LevelUpLimitMode.Value == FeedLikeGrandmaPlugin.LevelUpLimit.Level)
        {
            return Mathf.Max(1, FeedLikeGrandmaPlugin.LevelUpMaxLevel.Value);
        }

        string prefabName = NormalizePrefabName(((Component)context.Character).gameObject.name);
        return IsVanillaLevelOneLockedPrefab(prefabName) ? 1 : 3;
    }

    private static bool CanGainLevel(FeedTargetContext context)
    {
        return Mathf.Max(1, context.Character.GetLevel()) < GetMaxAllowedLevel(context);
    }

    private static bool IsVanillaLevelOneLockedPrefab(string prefabName)
    {
        return prefabName.Equals("Lox", StringComparison.OrdinalIgnoreCase) ||
               prefabName.Equals("Hen", StringComparison.OrdinalIgnoreCase);
    }

    private static IEnumerable<GameObject> EnumerateLoadedPrefabs()
    {
        if ((object?)ZNetScene.instance == null || ZNetScene.instance.m_prefabs == null)
        {
            yield break;
        }

        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
        IEnumerable<GameObject> prefabs = ZNetScene.instance.m_prefabs.Concat(FeedExternalReferenceSupport.GetExternalPrefabs());
        foreach (GameObject prefab in prefabs)
        {
            if ((object?)prefab == null || string.IsNullOrWhiteSpace(prefab.name))
            {
                continue;
            }

            string prefabName = NormalizePrefabName(prefab.name);
            if (!seen.Add(prefabName) || ShouldIgnorePrefab(prefab))
            {
                continue;
            }

            yield return prefab;
        }
    }

    internal static bool ShouldIgnorePrefab(GameObject? prefab)
    {
        if ((object?)prefab == null)
        {
            return true;
        }

        string prefabName = NormalizePrefabName(prefab.name);
        if (ExcludedPrefabNames.Contains(prefabName))
        {
            return true;
        }

        Character? character = prefab.GetComponent<Character>();
        return (object?)character != null &&
               (character.m_faction == Character.Faction.PlayerSpawned ||
                character.m_faction == Character.Faction.Players);
    }

    internal static FeedConsumeData ResolveConsumeData(GameObject prefab)
    {
        List<ItemDrop> ownConsumeItems = ResolveOwnConsumeItems(prefab);
        bool ownHasConsumeItems = ownConsumeItems.Count > 0;
        FeedConsumeData ownData = new()
        {
            ConsumeItems = ownConsumeItems
        };

        if (!TryFindGrowupConsumeSource(prefab, new HashSet<string>(StringComparer.OrdinalIgnoreCase), out GameObject? sourcePrefab))
        {
            return ownData;
        }

        List<ItemDrop> sourceConsumeItems = sourcePrefab == null ? new List<ItemDrop>() : ResolveOwnConsumeItems(sourcePrefab);
        bool sourceHasConsumeItems = sourceConsumeItems.Count > 0;
        bool inheritItems = !ownHasConsumeItems && sourceHasConsumeItems;
        if (!inheritItems)
        {
            return ownData;
        }

        return new FeedConsumeData
        {
            InheritedConsumeItems = inheritItems,
            ConsumeItems = inheritItems ? sourceConsumeItems : ownData.ConsumeItems
        };
    }

    private static List<ItemDrop> ResolveOwnConsumeItems(GameObject prefab)
    {
        MonsterAI? monsterAI = prefab.GetComponent<MonsterAI>();
        List<ItemDrop> consumeItems = GetConsumeItems(monsterAI);
        return consumeItems.Count > 0
            ? consumeItems
            : FeedExternalReferenceSupport.ResolveConfiguredConsumeItems(prefab);
    }

    private static bool HasConsumeItems(MonsterAI? monsterAI)
    {
        return (object?)monsterAI != null && monsterAI.m_consumeItems != null && monsterAI.m_consumeItems.Any(item => (object?)item != null);
    }

    private static List<ItemDrop> GetConsumeItems(MonsterAI? monsterAI)
    {
        if ((object?)monsterAI == null || monsterAI.m_consumeItems == null)
        {
            return new List<ItemDrop>();
        }

        return monsterAI.m_consumeItems.Where(item => (object?)item != null).ToList();
    }

    private static bool TryFindGrowupConsumeSource(GameObject prefab, HashSet<string> visited, out GameObject? sourcePrefab)
    {
        sourcePrefab = null;
        if ((object?)prefab == null || !visited.Add(prefab.name))
        {
            return false;
        }

        Growup? growup = prefab.GetComponent<Growup>();
        foreach (GameObject target in EnumerateGrowthTargets(growup))
        {
            if ((object?)target == null || visited.Contains(target.name))
            {
                continue;
            }

            List<ItemDrop> targetConsumeItems = ResolveOwnConsumeItems(target);
            if (targetConsumeItems.Count > 0)
            {
                sourcePrefab = target;
                return true;
            }

            if (TryFindGrowupConsumeSource(target, visited, out sourcePrefab))
            {
                return true;
            }
        }

        return false;
    }

    private static void ApplyInheritedConsumeItemsForLoadedPrefabs()
    {
        foreach (GameObject prefab in EnumerateLoadedPrefabs())
        {
            MonsterAI? monsterAI = prefab.GetComponent<MonsterAI>();
            if ((object?)monsterAI == null || HasConsumeItems(monsterAI))
            {
                continue;
            }

            FeedConsumeData consumeData = ResolveConsumeData(prefab);
            if (consumeData.InheritedConsumeItems)
            {
                monsterAI.m_consumeItems = consumeData.ConsumeItems.ToList();
            }
        }
    }

    private static List<RawFoodEntry> GetEffectiveFoodEntries(GameObject prefab, RawFeedingRule rawRule)
    {
        List<RawFoodEntry> foods = rawRule.Foods.ToList();
        if (rawRule.HasConsumeItemsOverride)
        {
            return foods;
        }

        FeedConsumeData consumeData = ResolveConsumeData(prefab);
        if (consumeData.ConsumeItems.Count == 0)
        {
            return foods;
        }

        foreach (ItemDrop consumeItem in consumeData.ConsumeItems)
        {
            string itemName = FeedExternalReferenceSupport.ExtractItemDropPrefabName(consumeItem);
            if (foods.Any(food => food.PrefabName.Equals(itemName, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            foods.Add(new RawFoodEntry
            {
                PrefabName = itemName
            });
        }

        return foods;
    }

    private static void ApplyConfiguredPrefabData(GameObject prefab, RawFeedingRule rawRule)
    {
        ApplyConfiguredConsumeData(prefab, rawRule);
        ApplyConfiguredTameableData(prefab, rawRule);
        ApplyConfiguredProcreationData(prefab, rawRule);
        ApplyConfiguredGrowupData(prefab, rawRule);
        ApplyConfiguredEggGrowData(prefab, rawRule);
    }

    private static void ApplyGlobalEggHatchTimeMultiplier(IEnumerable<GameObject> prefabs)
    {
        float multiplier = Mathf.Clamp01(FeedLikeGrandmaPlugin.EggHatchTimeMultiplier.Value);
        foreach (GameObject prefab in prefabs)
        {
            if ((object?)prefab == null)
            {
                continue;
            }

            EggGrow? eggGrow = prefab.GetComponent<EggGrow>();
            if ((object?)eggGrow != null)
            {
                eggGrow.m_growTime = Mathf.Max(0f, eggGrow.m_growTime) * multiplier;
            }
        }
    }

    private static bool IsEggGrowOnlyConfigRule(RawFeedingRule rawRule, EggGrow? eggGrow)
    {
        return (object?)eggGrow != null &&
               rawRule.EggGrow != null &&
               rawRule.Feed == null &&
               rawRule.Tame == null &&
               rawRule.Procreation == null &&
               rawRule.Grow == null &&
               !rawRule.HasConsumeItemsOverride &&
               rawRule.LevelUpItems.Count == 0;
    }

    private static void ApplyConfiguredConsumeData(GameObject prefab, RawFeedingRule rawRule)
    {
        if (!rawRule.HasConsumeItemsOverride)
        {
            return;
        }

        List<string> consumeItemNames = rawRule.Foods
            .Select(food => food.PrefabName)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        MonsterAI? monsterAI = prefab.GetComponent<MonsterAI>();
        if ((object?)monsterAI == null)
        {
            return;
        }

        List<ItemDrop> consumeItems = new();
        foreach (string itemName in consumeItemNames)
        {
            if (TryResolveItemDrop(prefab.name, itemName, out ItemDrop? itemDrop))
            {
                consumeItems.Add(itemDrop!);
            }
        }

        monsterAI.m_consumeItems = consumeItems;
    }

    private static void ApplyConfiguredTameableData(GameObject prefab, RawFeedingRule rawRule)
    {
        if (rawRule.Tame == null)
        {
            return;
        }

        Tameable? tameable = prefab.GetComponent<Tameable>();
        if ((object?)tameable == null)
        {
            FeedLikeGrandmaPlugin.Log.LogWarning($"Configured prefab '{prefab.name}' has tameable settings but no Tameable component.");
            return;
        }

        tameable.m_fedDuration = rawRule.Tame.FedDuration;
        tameable.m_tamingTime = rawRule.Tame.TamingTime;
        tameable.m_startsTamed = rawRule.Tame.StartsTamed;
        tameable.m_commandable = rawRule.Tame.Commandable;
    }

    private static void ApplyConfiguredProcreationData(GameObject prefab, RawFeedingRule rawRule)
    {
        if (rawRule.Procreation == null)
        {
            return;
        }

        Procreation? procreation = prefab.GetComponent<Procreation>();
        if ((object?)procreation == null)
        {
            FeedLikeGrandmaPlugin.Log.LogWarning($"Configured prefab '{prefab.name}' has procreation settings but no Procreation component.");
            return;
        }

        procreation.m_maxCreatures = rawRule.Procreation.MaxCreatures;
        procreation.m_totalCheckRange = rawRule.Procreation.TotalCheckRange;
    }

    private static void ApplyConfiguredGrowupData(GameObject prefab, RawFeedingRule rawRule)
    {
        if (rawRule.Grow == null)
        {
            return;
        }

        Growup? growup = prefab.GetComponent<Growup>();
        if ((object?)growup == null)
        {
            FeedLikeGrandmaPlugin.Log.LogWarning($"Configured prefab '{prefab.name}' has grow settings but no Growup component.");
            return;
        }

        growup.m_growTime = rawRule.Grow.GrowTime;
    }

    private static void ApplyConfiguredEggGrowData(GameObject prefab, RawFeedingRule rawRule)
    {
        if (rawRule.EggGrow == null)
        {
            return;
        }

        EggGrow? eggGrow = prefab.GetComponent<EggGrow>();
        if ((object?)eggGrow == null)
        {
            FeedLikeGrandmaPlugin.Log.LogWarning($"Configured prefab '{prefab.name}' has eggGrow settings but no EggGrow component.");
            return;
        }

        eggGrow.m_growTime = rawRule.EggGrow.GrowTime;
    }

    private static bool TryResolveItemDrop(string creaturePrefabName, string itemPrefabName, out ItemDrop? itemDrop)
    {
        itemDrop = null;
        GameObject? itemPrefab = ObjectDB.instance.GetItemPrefab(itemPrefabName);
        if ((object?)itemPrefab == null)
        {
            FeedLikeGrandmaPlugin.Log.LogWarning($"Configured consume item '{itemPrefabName}' for prefab '{creaturePrefabName}' was not found in ObjectDB.");
            return false;
        }

        itemDrop = itemPrefab.GetComponent<ItemDrop>();
        if ((object?)itemDrop == null)
        {
            FeedLikeGrandmaPlugin.Log.LogWarning($"Configured consume item '{itemPrefabName}' for prefab '{creaturePrefabName}' is not an item prefab.");
            return false;
        }

        return true;
    }

    private static float ResolveFoodValue(RawFoodEntry food)
    {
        if (food.Value.HasValue)
        {
            return food.Value.Value;
        }

        if (food.RequiredQuality.HasValue &&
            FoodNumbers.TryGetValue(
                FeedingYamlParser.FormatFoodKey(food.PrefabName, food.RequiredQuality),
                out float qualityValue))
        {
            return qualityValue;
        }

        if (FoodNumbers.TryGetValue(food.PrefabName, out float value))
        {
            return value;
        }

        return 1f;
    }

    internal static void RegisterTargetRpc(ZNetView? nview)
    {
        if ((object?)nview == null || !nview.IsValid())
        {
            return;
        }

        int instanceId = nview.GetInstanceID();
        if (!RegisteredTargetViews.Add(instanceId))
        {
            return;
        }

        nview.Register<ZPackage>(FeedRequestRpc, (sender, pkg) => ReceiveFeedRequest(nview, sender, pkg));
    }

    internal static void RegisterPlayerRpc(Player? player)
    {
        if ((object?)player == null || (object?)player.m_nview == null || !player.m_nview.IsValid())
        {
            return;
        }

        int instanceId = player.m_nview.GetInstanceID();
        if (!RegisteredPlayerViews.Add(instanceId))
        {
            return;
        }

        player.m_nview.Register<ZPackage>(FeedResponseRpc, (sender, pkg) => ReceiveFeedResponse(player, sender, pkg));
    }

    internal static bool TryHandleTameableUseItem(Tameable tameable, Humanoid user, ItemDrop.ItemData item)
    {
        if ((object?)tameable == null || user is not Player player || (object?)item == null)
        {
            return false;
        }

        if ((object?)tameable.m_saddleItem != null &&
            item.m_dropPrefab != null &&
            item.m_dropPrefab.name == tameable.m_saddleItem.name)
        {
            return false;
        }

        return TryStartFeedRequest(player, ((Component)tameable).gameObject, item);
    }

    internal static bool TryHandleGrowupUseItem(Humanoid user, GameObject? hoverObject, ItemDrop.ItemData item)
    {
        if (user is not Player player || (object?)hoverObject == null || (object?)item == null)
        {
            return false;
        }

        Character? character = hoverObject.GetComponentInParent<Character>();
        if ((object?)character == null || (object?)character.GetComponent<Tameable>() != null)
        {
            return false;
        }

        return TryStartFeedRequest(player, ((Component)character).gameObject, item);
    }

    internal static string AugmentHoverText(Character character, string originalText)
    {
        string sourceText = originalText ?? string.Empty;
        int targetId = character == null ? 0 : character.GetInstanceID();
        int timeBucket = Mathf.FloorToInt(Time.unscaledTime);
        int uiState = GetHoverTextUiState();
        string language = Localization.instance == null ? string.Empty : Localization.instance.GetSelectedLanguage();
        if (_hoverTextTargetId == targetId &&
            _hoverTextTimeBucket == timeBucket &&
            _hoverTextUiState == uiState &&
            string.Equals(_hoverTextOriginal, sourceText, StringComparison.Ordinal) &&
            string.Equals(_hoverTextLanguage, language, StringComparison.Ordinal))
        {
            return _hoverTextResult;
        }

        if (!TryBuildContext(character, out FeedTargetContext context))
        {
            return RememberHoverText(targetId, timeBucket, uiState, sourceText, language, sourceText);
        }

        List<string> lines = new();

        if ((object?)context.Tameable != null && !context.Character.IsTamed())
        {
            GetTameProgress(context, out float elapsedTame, out float totalTame);
            lines.Add("<color=orange>" + FeedLocalization.Format(
                "$flg_hover_tame_progress",
                elapsedTame.ToString("0", CultureInfo.InvariantCulture),
                totalTame.ToString("0", CultureInfo.InvariantCulture)) + "</color>");
        }

        if ((object?)context.Growup != null)
        {
            if (context.Character.IsTamed())
            {
                float growthElapsed = GetGrowthElapsedSeconds(context.Growup, context.Character, initializeIfMissing: false);
                float cappedElapsed = Mathf.Min(growthElapsed, context.Growup.m_growTime);
                lines.Add("<color=orange>" + FeedLocalization.Format(
                    "$flg_hover_growth_progress",
                    cappedElapsed.ToString("0", CultureInfo.InvariantCulture),
                    context.Growup.m_growTime.ToString("0", CultureInfo.InvariantCulture)) + "</color>");
            }
            else
            {
                lines.Add("<color=orange>" + FeedLocalization.Text("$flg_hover_growth_locked_tamed") + "</color>");
            }
        }

        if ((object?)context.Tameable != null && context.Character.IsTamed())
        {
            float fedRemaining = GetFedRemainingSeconds(context, out float maxFedSeconds);
            if (maxFedSeconds > 0f)
            {
                lines.Add("<color=orange>" + FeedLocalization.Format("$flg_hover_fed_progress", FormatDurationClock(fedRemaining), FormatDurationClock(maxFedSeconds)) + "</color>");
            }
        }

        if (CanUseTamingTimeRequirements(context))
        {
            if (!context.Character.IsTamed())
            {
                lines.Add("<color=orange>" + FeedLocalization.Text("$flg_hover_stars_locked_tamed") + "</color>");
            }
            else if ((object?)context.Growup != null)
            {
                lines.Add("<color=orange>" + FeedLocalization.Text("$flg_hover_stars_locked_growth") + "</color>");
            }
            else
            {
                int currentLevel = Mathf.Max(1, context.Character.GetLevel());
                int maxLevel = GetMaxAllowedLevel(context);
                if (currentLevel >= maxLevel)
                {
                    lines.Add("<color=orange>" + FeedLocalization.Text("$flg_hover_level_max") + "</color>");
                }
                else
                {
                    int targetLevel = currentLevel + 1;
                    float progress = GetStarProgress(context.NView);
                    float required = GetRequiredStarProgress(context, targetLevel);
                    lines.Add("<color=orange>" + FeedLocalization.Format(
                        "$flg_hover_level_progress",
                        currentLevel.ToString(CultureInfo.InvariantCulture),
                        targetLevel.ToString(CultureInfo.InvariantCulture),
                        Mathf.Min(progress, required).ToString("0", CultureInfo.InvariantCulture),
                        required.ToString("0", CultureInfo.InvariantCulture)) + "</color>");
                }
            }
        }

        if (FeedLikeGrandmaPlugin.ShowFeedingHelp.Value.IsOn() &&
            TryBuildHoverFoodRows(Player.m_localPlayer, context, out FeedHoverFoodRows foodRows) &&
            foodRows.TotalCount > 0)
        {
            lines.Add(FeedLocalization.Text("$flg_hover_feed_instruction"));
        }

        if (lines.Count == 0)
        {
            return RememberHoverText(targetId, timeBucket, uiState, sourceText, language, sourceText);
        }

        string baseText = sourceText.Length == 0 ? context.Character.GetHoverName() : sourceText;
        string result = baseText + "\n" + string.Join("\n", lines);
        return RememberHoverText(targetId, timeBucket, uiState, sourceText, language, result);
    }

    internal static string AugmentEggGrowHoverText(EggGrow eggGrow, string originalText)
    {
        string sourceText = originalText ?? string.Empty;
        if ((object?)eggGrow == null ||
            (object?)eggGrow.m_nview == null ||
            !eggGrow.m_nview.IsValid() ||
            (object?)eggGrow.m_item == null ||
            eggGrow.m_item.m_itemData.m_stack > 1)
        {
            return sourceText;
        }

        ZDO? zdo = eggGrow.m_nview.GetZDO();
        float growStart = zdo?.GetFloat(ZDOVars.s_growStart, 0f) ?? 0f;
        string line;
        if (growStart > 0f)
        {
            if ((object?)ZNet.instance == null)
            {
                return sourceText;
            }

            double hatchAt = growStart + Math.Max(0f, eggGrow.m_growTime);
            float remaining = (float)Math.Max(0d, hatchAt - ZNet.instance.GetTimeSeconds());
            line = FeedLocalization.Format("$flg_hover_hatching", FormatDurationClock(remaining));
        }
        else if (eggGrow.m_requireUnderRoof && EggNeedsMoreCover(eggGrow))
        {
            line = FeedLocalization.Text("$flg_hover_needs_more_cover");
        }
        else
        {
            return sourceText;
        }

        string coloredLine = "<color=orange>" + line + "</color>";
        return sourceText.Length == 0 ? coloredLine : sourceText + "\n" + coloredLine;
    }

    private static bool EggNeedsMoreCover(EggGrow eggGrow)
    {
        int targetId = eggGrow.GetInstanceID();
        int timeBucket = Mathf.FloorToInt(Time.unscaledTime);
        if (_eggCoverTargetId == targetId && _eggCoverTimeBucket == timeBucket)
        {
            return _eggNeedsMoreCover;
        }

        Cover.GetCoverForPoint(
            eggGrow.transform.position,
            out float coverPercentage,
            out bool underRoof,
            0.1f);
        _eggCoverTargetId = targetId;
        _eggCoverTimeBucket = timeBucket;
        _eggNeedsMoreCover = !underRoof || coverPercentage < eggGrow.m_requireCoverPercentige;
        return _eggNeedsMoreCover;
    }

    private static int GetHoverTextUiState()
    {
        int state = FeedLikeGrandmaPlugin.ShowFeedingHelp.Value.IsOn() ? 1 : 0;
        if (FeedLikeGrandmaPlugin.ShowFoodEvenIfAlerted.Value.IsOn())
        {
            state |= 2;
        }

        Player? player = Player.m_localPlayer;
        bool foodIconInputActive = player != null &&
                                   (!FeedLikeGrandmaPlugin.RequireCrouchForFoodIcons.Value || player.IsCrouching());
        if (foodIconInputActive)
        {
            state |= 4;
        }

        return state;
    }

    private static string RememberHoverText(
        int targetId,
        int timeBucket,
        int uiState,
        string originalText,
        string language,
        string result)
    {
        _hoverTextTargetId = targetId;
        _hoverTextTimeBucket = timeBucket;
        _hoverTextUiState = uiState;
        _hoverTextOriginal = originalText;
        _hoverTextLanguage = language;
        _hoverTextResult = result;
        return result;
    }

    private static void InvalidateHoverTextCache()
    {
        _hoverTextTargetId = 0;
        _hoverTextTimeBucket = -1;
        _hoverTextUiState = -1;
        _hoverTextOriginal = string.Empty;
        _hoverTextLanguage = string.Empty;
        _hoverTextResult = string.Empty;
        _eggCoverTargetId = 0;
        _eggCoverTimeBucket = -1;
        _eggNeedsMoreCover = false;
    }

    internal static void UpdateHoverIcons(Player player)
    {
        Hud? hud = Hud.instance;
        if (!TryGetHoverFoods(player, out FeedHoverFoodRows rows))
        {
            FeedHoverHud.Update(hud, rows: null);
            return;
        }

        FeedHoverHud.Update(hud, rows);
    }

    internal static bool HasRuleFor(GameObject gameObject)
    {
        return !ShouldIgnorePrefab(gameObject) && ResolvedRules.ContainsKey(NormalizePrefabName(gameObject.name));
    }

    internal static bool TryGetMountStamina(Character character, out ResolvedMountStaminaValues? mountStamina)
    {
        mountStamina = null;
        if ((object?)character == null)
        {
            return false;
        }

        GameObject characterObject = ((Component)character).gameObject;
        if (ShouldIgnorePrefab(characterObject))
        {
            return false;
        }

        string prefabName = NormalizePrefabName(characterObject.name);
        if (!ResolvedRules.TryGetValue(prefabName, out ResolvedFeedingRule? rule) || rule.MountStamina == null)
        {
            return false;
        }

        mountStamina = rule.MountStamina;
        return true;
    }

    internal static bool TryGetRestrictedBiomes(Character character, out IReadOnlyList<string> restrictedBiomes)
    {
        restrictedBiomes = Array.Empty<string>();
        if ((object?)character == null)
        {
            return false;
        }

        GameObject characterObject = ((Component)character).gameObject;
        if (ShouldIgnorePrefab(characterObject))
        {
            return false;
        }

        string prefabName = NormalizePrefabName(characterObject.name);
        if (!ResolvedRules.TryGetValue(prefabName, out ResolvedFeedingRule? rule) || !rule.HasRestrictedBiomeOverride)
        {
            return false;
        }

        restrictedBiomes = rule.RestrictedBiomes;
        return true;
    }

    internal static void ApplyConfiguredTamingMultiplier(Tameable tameable)
    {
        Character? character = ((Component)tameable).GetComponentInParent<Character>();
        if (!TryBuildContext(character, out FeedTargetContext context))
        {
            return;
        }

        if ((object?)context.Tameable != null)
        {
            context.Tameable.m_tamingBoostMultiplier = FeedLikeGrandmaPlugin.GetTamingBoostMultiplier();
            EnsureLevelScaledTameTime(context);
            ApplyEffectiveFedDuration(context);
        }
    }

    internal static bool TryGetHungryState(Tameable tameable, out bool isHungry)
    {
        isHungry = false;
        Character? character = (object?)tameable == null ? null : ((Component)tameable).GetComponentInParent<Character>();
        if ((object?)tameable == null ||
            !TryBuildContext(character, out FeedTargetContext context) ||
            (object?)context.Tameable == null)
        {
            return false;
        }

        ApplyEffectiveFedDuration(context);
        float remaining = GetFedRemainingSeconds(context, out float maxFedSeconds);
        if (maxFedSeconds <= 0f)
        {
            return false;
        }

        isHungry = remaining <= 0f;
        return true;
    }

    internal static bool TryCanAutoConsume(MonsterAI monsterAI, ItemDrop.ItemData item, out bool canConsume)
    {
        return TryGetAutoConsumeMatch(monsterAI, item, out canConsume, out _);
    }

    internal static bool TryGetAutoConsumeMatch(
        MonsterAI monsterAI,
        ItemDrop.ItemData item,
        out bool canConsume,
        out bool matchesLevelUpFood)
    {
        canConsume = false;
        matchesLevelUpFood = false;
        Character? character = (object?)monsterAI == null ? null : ((Component)monsterAI).GetComponentInParent<Character>();
        if ((object?)monsterAI == null ||
            (object?)item == null ||
            !TryBuildContext(character, out FeedTargetContext context))
        {
            return false;
        }

        return TryGetAutoConsumeMatch(context, item, out canConsume, out matchesLevelUpFood);
    }

    internal static bool TrySelectAutoFeedItem(
        MonsterAI monsterAI,
        IEnumerable<ItemDrop.ItemData> candidates,
        out ItemDrop.ItemData? selectedItem)
    {
        selectedItem = null;
        Character? character = (object?)monsterAI == null ? null : ((Component)monsterAI).GetComponentInParent<Character>();
        if ((object?)monsterAI == null ||
            candidates == null ||
            !TryBuildContext(character, out FeedTargetContext context))
        {
            return false;
        }

        ItemDrop.ItemData? dailyFallback = null;
        foreach (ItemDrop.ItemData candidate in candidates)
        {
            if ((object?)candidate == null || candidate.m_stack <= 0 ||
                !TryGetAutoConsumeMatch(context, candidate, out bool canConsume, out bool matchesLevelUpFood) ||
                !canConsume)
            {
                continue;
            }

            if (matchesLevelUpFood)
            {
                selectedItem = candidate;
                return true;
            }

            dailyFallback ??= candidate;
        }

        selectedItem = dailyFallback;
        return selectedItem != null;
    }

    internal static bool TryHandleAutomaticConsumedItem(Tameable tameable, ItemDrop item, out float targetHealth)
    {
        targetHealth = 0f;
        Character? character = (object?)tameable == null ? null : ((Component)tameable).GetComponentInParent<Character>();
        if ((object?)tameable == null ||
            (object?)item == null ||
            !TryBuildContext(character, out FeedTargetContext context) ||
            (object?)context.Tameable == null ||
            !context.NView.IsValid() ||
            !context.NView.IsOwner() ||
            !TryGetItemPrefabName(item.m_itemData, out string itemPrefabName))
        {
            return false;
        }

        int itemQuality = Mathf.Max(1, item.m_itemData.m_quality);
        if (!TryGetFeedEntriesForItem(context, itemPrefabName, itemQuality, out ResolvedFoodEntry? dailyFood, out ResolvedFoodEntry? levelUpFood, out bool hasLevelUpItems))
        {
            return false;
        }

        ApplyAutomaticFeedEffects(context, dailyFood, levelUpFood, hasLevelUpItems);
        targetHealth = context.Character.GetHealth();
        return true;
    }

    internal static bool HandleGrowUpdate(Growup growup)
    {
        Character? character = (object?)growup == null ? null : ((Component)growup).GetComponentInParent<Character>();
        if ((object?)growup == null || !TryBuildContext(character, out FeedTargetContext context))
        {
            return false;
        }

        if (!context.NView.IsValid() || !context.NView.IsOwner())
        {
            return false;
        }

        float elapsed = AdvanceGrowth(context.Growup!, context.Character);
        if (elapsed < context.Growup!.m_growTime)
        {
            return false;
        }

        GrowNow(context.Growup, context.Character);
        return false;
    }

    private static bool TryStartFeedRequest(Player player, GameObject targetObject, ItemDrop.ItemData item)
    {
        if (!TryBuildContext(targetObject, out FeedTargetContext context))
        {
            return false;
        }

        if (!TryGetItemPrefabName(item, out string itemPrefabName))
        {
            return false;
        }

        int itemQuality = Mathf.Max(1, item.m_quality);
        bool matchesDailyFood = FindMatchingFood(context.Rule.Foods, itemPrefabName, itemQuality) != null;
        bool matchesLevelUpFood = TryGetLevelUpItems(context, out IReadOnlyList<ResolvedFoodEntry>? levelUpFoods) &&
                                  levelUpFoods != null &&
                                  FindMatchingFood(levelUpFoods, itemPrefabName, itemQuality) != null;
        if (!matchesDailyFood && !matchesLevelUpFood)
        {
            return false;
        }

        if ((object?)context.BaseAI != null && context.BaseAI.IsAlerted())
        {
            FeedLocalization.Message(player, "$flg_feed_target_alerted", context.Character.GetHoverName());
            return true;
        }

        long cooldownTicks = context.NView.GetZDO().GetLong(FeedCooldownHash, 0L);
        long nowTicks = ZNet.instance.GetTime().Ticks;
        if (cooldownTicks > nowTicks)
        {
            TimeSpan remaining = new(cooldownTicks - nowTicks);
            FeedLocalization.Message(player, "$flg_feed_cooldown", remaining.TotalSeconds.ToString("0.#", CultureInfo.InvariantCulture));
            return true;
        }

        CleanupPendingRequests(nowTicks);
        int available = CountItemsByPrefab(player.GetInventory(), itemPrefabName, itemQuality);
        int reserved = CountPendingItems(itemPrefabName, itemQuality);
        int requestedAmount = Mathf.Min(
            Mathf.Max(0, available - reserved),
            FeedLikeGrandmaPlugin.MaxItemsPerFeed.Value);
        if (requestedAmount <= 0)
        {
            FeedLocalization.Message(player, "$flg_feed_no_matching_item");
            return true;
        }

        int requestId = _nextRequestId++;
        PendingRequests[requestId] = new PendingFeedRequest
        {
            ItemPrefabName = itemPrefabName,
            ItemQuality = itemQuality,
            TargetId = context.NView.GetZDO().m_uid,
            ExpectedResponderId = context.NView.GetZDO().GetOwner(),
            RequestedAmount = requestedAmount,
            CreatedAtTicks = nowTicks
        };

        FeedRequestMessage request = new()
        {
            RequestId = requestId,
            RequesterId = player.GetZDOID(),
            ItemPrefabName = itemPrefabName,
            ItemQuality = itemQuality,
            RequestedAmount = requestedAmount
        };

        context.NView.InvokeRPC(FeedRequestRpc, request.Write());
        return true;
    }

    private static void CleanupPendingRequests(long nowTicks)
    {
        if (PendingRequests.Count == 0)
        {
            return;
        }

        List<int>? expiredRequestIds = null;
        foreach (KeyValuePair<int, PendingFeedRequest> entry in PendingRequests)
        {
            if (entry.Value.CreatedAtTicks <= 0L ||
                nowTicks - entry.Value.CreatedAtTicks > PendingRequestTimeoutTicks)
            {
                expiredRequestIds ??= new List<int>();
                expiredRequestIds.Add(entry.Key);
            }
        }

        if (expiredRequestIds == null)
        {
            return;
        }

        foreach (int requestId in expiredRequestIds)
        {
            PendingRequests.Remove(requestId);
        }
    }

    private static void ReceiveFeedRequest(ZNetView nview, long sender, ZPackage pkg)
    {
        if (!nview.IsOwner())
        {
            return;
        }

        if (!FeedRequestMessage.TryRead(pkg, out FeedRequestMessage? request) || request == null)
        {
            FeedLikeGrandmaPlugin.Log.LogDebug($"Ignored malformed feed request from peer {sender}.");
            return;
        }

        Player? requester = FindPlayer(request.RequesterId);
        if ((object?)requester?.m_nview == null || !requester.m_nview.IsValid())
        {
            return;
        }

        ZDO requesterZdo = requester.m_nview.GetZDO();
        if (sender == 0L || (object?)requesterZdo == null || requesterZdo.GetOwner() != sender)
        {
            FeedLikeGrandmaPlugin.Log.LogWarning(
                $"Ignored feed request {request.RequestId} whose sender does not own the requesting player.");
            return;
        }

        if (request.RequestId <= 0 || string.IsNullOrWhiteSpace(request.ItemPrefabName) ||
            request.ItemPrefabName.Length > 256 || request.ItemQuality <= 0 || request.RequestedAmount <= 0)
        {
            FeedLikeGrandmaPlugin.Log.LogDebug($"Ignored invalid feed request {request.RequestId} from peer {sender}.");
            return;
        }

        ZDO targetZdo = nview.GetZDO();
        ZDOID targetId = targetZdo.m_uid;
        long nowTicks = ZNet.instance.GetTime().Ticks;
        CleanupCompletedFeedRequests(nowTicks);
        (long Sender, int RequestId) completedKey = (sender, request.RequestId);
        if (CompletedFeedRequests.TryGetValue(completedKey, out CompletedFeedRequest? completed))
        {
            if (completed.Matches(request, targetId))
            {
                requester.m_nview.InvokeRPC(FeedResponseRpc, completed.Response.Write());
            }
            else
            {
                FeedLikeGrandmaPlugin.Log.LogWarning(
                    $"Ignored reused feed request id {request.RequestId} from peer {sender} with different request data.");
            }

            return;
        }

        GameObject targetObject = ((Component)nview).gameObject;
        FeedResponseMessage response;
        if (!IsRequesterInFeedRange(requester, targetObject))
        {
            response = new FeedResponseMessage
            {
                RequestId = request.RequestId,
                ItemPrefabName = request.ItemPrefabName,
                TargetId = targetId,
                Message = FeedLocalization.Text("$flg_feed_too_far")
            };
        }
        else
        {
            response = ProcessFeedRequest(targetObject, request, requester);
        }

        StoreCompletedFeedRequest(completedKey, new CompletedFeedRequest
        {
            RequesterId = request.RequesterId,
            TargetId = targetId,
            ItemPrefabName = request.ItemPrefabName,
            ItemQuality = request.ItemQuality,
            RequestedAmount = request.RequestedAmount,
            CompletedAtTicks = nowTicks,
            Response = response
        });
        requester.m_nview.InvokeRPC(FeedResponseRpc, response.Write());
    }

    private static FeedResponseMessage ProcessFeedRequest(
        GameObject targetObject,
        FeedRequestMessage request,
        Player requester)
    {
        FeedResponseMessage response = new()
        {
            RequestId = request.RequestId,
            ItemPrefabName = request.ItemPrefabName,
            TargetId = ZDOID.None,
            Message = string.Empty
        };

        if (!TryBuildContext(targetObject, out FeedTargetContext context))
        {
            response.Message = FeedLocalization.Text("$flg_feed_target_not_configured");
            return response;
        }

        response.TargetId = context.NView.GetZDO().m_uid;
        response.DisplayPosition = context.Character.GetTopPoint();

        if ((object?)context.BaseAI != null && context.BaseAI.IsAlerted())
        {
            response.Message = FeedLocalization.Format("$flg_feed_target_alerted", context.Character.GetHoverName());
            return response;
        }

        int requestQuality = Mathf.Max(1, request.ItemQuality);
        if (!TryGetFeedEntriesForItem(context, request.ItemPrefabName, requestQuality, out ResolvedFoodEntry? dailyFood, out ResolvedFoodEntry? levelUpFood, out bool hasLevelUpItems))
        {
            response.Message = FeedLocalization.Text("$flg_feed_invalid_item");
            return response;
        }

        int amount = Mathf.Min(request.RequestedAmount, FeedLikeGrandmaPlugin.MaxItemsPerFeed.Value);
        if (amount <= 0)
        {
            response.Message = FeedLocalization.Text("$flg_feed_no_items_consumed");
            return response;
        }

        long nowTicks = ZNet.instance.GetTime().Ticks;
        long cooldownUntilTicks = context.NView.GetZDO().GetLong(FeedCooldownHash, 0L);
        if (cooldownUntilTicks > nowTicks)
        {
            TimeSpan remaining = new(cooldownUntilTicks - nowTicks);
            response.Message = FeedLocalization.Format("$flg_feed_cooldown", remaining.TotalSeconds.ToString("0.#", CultureInfo.InvariantCulture));
            return response;
        }

        bool requesterHasTamingBoost = requester.GetSEMan()
            .HaveStatusAttribute(StatusEffect.StatusAttribute.TamingBoost);
        float feedMultiplier = requesterHasTamingBoost
            ? FeedLikeGrandmaPlugin.GetFeedBoostMultiplier()
            : 1f;
        FeedEffectPlan plan = BuildFeedEffectPlan(
            context,
            dailyFood,
            levelUpFood,
            hasLevelUpItems,
            amount,
            feedMultiplier);

        if (!plan.HasAnyEffect)
        {
            response.Message = "That feed would not change anything right now.";
            return response;
        }

        response.Success = true;
        response.ConsumedAmount = plan.Amount;
        TriggerConsumeAnimation(context);

        if ((object?)context.Tameable != null && plan.FedDeltaRequested > 0f)
        {
            response.FedDelta = AddFedTime(context, plan.FedDeltaRequested);
        }

        response.TameDelta = ApplyTameDelta(context, plan.TameDeltaRequested);

        response.GrowthDelta = ApplyGrowthDelta(context, plan.GrowthDeltaRequested, out bool grewThisFeed);

        if (plan.StarDeltaRequested > 0)
        {
            response.StarDelta = ApplyStarProgress(context, plan.StarDeltaRequested);
        }

        if (!grewThisFeed)
        {
            response.HealPercent = ApplyHealPercent(context, plan.HealPercentRequested);
        }

        if (!grewThisFeed)
        {
            long cooldownTicksToSet = ZNet.instance.GetTime().AddSeconds(FeedLikeGrandmaPlugin.FeedCooldownSeconds.Value).Ticks;
            context.NView.GetZDO().Set(FeedCooldownHash, cooldownTicksToSet);
            response.DisplayPosition = context.Character.GetTopPoint();
        }

        return response;
    }

    private static void TriggerConsumeAnimation(FeedTargetContext context)
    {
        ZSyncAnimation? animator = context.BaseAI?.m_animator;
        if ((object?)animator == null || (object?)animator.m_animator == null)
        {
            return;
        }

        bool hasConsumeTrigger = animator.m_animator.parameters.Any(parameter =>
            parameter.type == AnimatorControllerParameterType.Trigger &&
            parameter.name.Equals("consume", StringComparison.Ordinal));
        if (!hasConsumeTrigger)
        {
            return;
        }

        animator.SetTrigger("consume");
    }

    private static FeedEffectPlan BuildFeedEffectPlan(
        FeedTargetContext context,
        ResolvedFoodEntry? dailyFood,
        ResolvedFoodEntry? levelUpFood,
        bool hasLevelUpItems,
        int amount,
        float feedMultiplier)
    {
        amount = Mathf.Max(0, amount);
        bool isTamed = context.Character.IsTamed();
        ResolvedFoodEntry? starFood = hasLevelUpItems ? levelUpFood : dailyFood;
        ResolvedFoodEntry? sustainFood = dailyFood ?? levelUpFood;

        float starDeltaPerItem = starFood != null &&
                                 isTamed &&
                                 CanUseTamingTimeRequirements(context) &&
                                 (object?)context.Growup == null &&
                                 CanGainLevel(context)
            ? starFood.Value * context.Rule.LevelUpConstant * feedMultiplier
            : 0f;

        if (starDeltaPerItem > 0f)
        {
            amount = CapAmountForSingleLevelUp(context, amount, starDeltaPerItem);
        }

        int tameDeltaRequested = dailyFood != null && !isTamed && (object?)context.Tameable != null
            ? Mathf.FloorToInt(dailyFood.Value * context.Rule.TamingConstant * amount * feedMultiplier)
            : 0;

        int growthDeltaRequested = dailyFood != null && isTamed && (object?)context.Growup != null
            ? Mathf.FloorToInt(dailyFood.Value * context.Rule.GrowthConstant * amount * feedMultiplier)
            : 0;

        int starDeltaRequested = starDeltaPerItem > 0f
            ? Mathf.FloorToInt(starDeltaPerItem * amount)
            : 0;

        float healPercentRequested = sustainFood != null
            ? sustainFood.Value * context.Rule.HealConstant * amount * feedMultiplier
            : 0f;

        float fedDeltaRequested = sustainFood != null && (object?)context.Tameable != null
            ? sustainFood.Value * context.Rule.FedConstant * amount * feedMultiplier
            : 0f;

        return new FeedEffectPlan
        {
            Amount = amount,
            TameDeltaRequested = tameDeltaRequested,
            GrowthDeltaRequested = growthDeltaRequested,
            StarDeltaRequested = starDeltaRequested,
            HealPercentRequested = healPercentRequested,
            FedDeltaRequested = fedDeltaRequested,
            FedDeltaPossible = GetPotentialFedDelta(context, fedDeltaRequested)
        };
    }

    private static int ApplyTameDelta(FeedTargetContext context, int requestedDelta)
    {
        if (requestedDelta <= 0 || (object?)context.Tameable == null)
        {
            return 0;
        }

        EnsureLevelScaledTameTime(context);
        float remainingBefore = context.Tameable.GetRemainingTime();
        int applied = Mathf.Min(requestedDelta, Mathf.CeilToInt(Mathf.Max(0f, remainingBefore)));
        if (applied <= 0)
        {
            return 0;
        }

        float remainingAfter = Mathf.Max(0f, remainingBefore - applied);
        context.NView.GetZDO().Set(ZDOVars.s_tameTimeLeft, remainingAfter);
        if (remainingAfter <= 0f && !context.Character.IsTamed())
        {
            context.Tameable.Tame();
        }

        return applied;
    }

    private static int ApplyGrowthDelta(FeedTargetContext context, int requestedDelta, out bool grewThisFeed)
    {
        grewThisFeed = false;
        if (requestedDelta <= 0 || (object?)context.Growup == null)
        {
            return 0;
        }

        float growthBefore = GetGrowthElapsedSeconds(context.Growup, context.Character, initializeIfMissing: true);
        float remainingGrowth = Mathf.Max(0f, context.Growup.m_growTime - growthBefore);
        int applied = Mathf.Min(requestedDelta, Mathf.CeilToInt(remainingGrowth));
        if (applied <= 0)
        {
            return 0;
        }

        SetGrowthElapsedSeconds(context.Growup, growthBefore + applied);
        if (growthBefore + applied >= context.Growup.m_growTime)
        {
            grewThisFeed = true;
            GrowNow(context.Growup, context.Character);
        }

        return applied;
    }

    private static float ApplyHealPercent(FeedTargetContext context, float requestedPercent)
    {
        if (requestedPercent <= 0f)
        {
            return 0f;
        }

        float maxHealth = context.Character.GetMaxHealth();
        if (maxHealth <= 0f)
        {
            return 0f;
        }

        float currentHealth = context.Character.GetHealth();
        float healAmount = Mathf.Min(maxHealth - currentHealth, maxHealth * requestedPercent / 100f);
        if (healAmount <= 0f)
        {
            return 0f;
        }

        context.Character.Heal(healAmount, showText: false);
        return healAmount / maxHealth * 100f;
    }

    private static void ReceiveFeedResponse(Player player, long sender, ZPackage pkg)
    {
        if (!FeedResponseMessage.TryRead(pkg, out FeedResponseMessage? response) || response == null)
        {
            FeedLikeGrandmaPlugin.Log.LogDebug($"Ignored malformed feed response from peer {sender}.");
            return;
        }

        if (!PendingRequests.TryGetValue(response.RequestId, out PendingFeedRequest? pending))
        {
            return;
        }

        if (pending.ExpectedResponderId != 0L && pending.ExpectedResponderId != sender)
        {
            FeedLikeGrandmaPlugin.Log.LogWarning(
                $"Ignored feed response {response.RequestId} from unexpected peer {sender}.");
            return;
        }

        PendingRequests.Remove(response.RequestId);

        if (!response.Success)
        {
            if (!string.IsNullOrWhiteSpace(response.Message))
            {
                player.Message(MessageHud.MessageType.Center, response.Message);
            }

            return;
        }

        if (!response.TargetId.Equals(pending.TargetId) ||
            !response.ItemPrefabName.Equals(pending.ItemPrefabName, StringComparison.OrdinalIgnoreCase) ||
            response.ConsumedAmount <= 0 ||
            response.ConsumedAmount > pending.RequestedAmount)
        {
            FeedLikeGrandmaPlugin.Log.LogWarning(
                $"Rejected mismatched feed response {response.RequestId}: expected {pending.ItemPrefabName} for {pending.TargetId}, received {response.ItemPrefabName} for {response.TargetId} with amount {response.ConsumedAmount}.");
            return;
        }

        int removedAmount = RemoveItemsByPrefab(
            player.GetInventory(),
            pending.ItemPrefabName,
            pending.ItemQuality,
            response.ConsumedAmount);
        if (removedAmount != response.ConsumedAmount)
        {
            FeedLikeGrandmaPlugin.Log.LogWarning(
                $"Feed response {response.RequestId} consumed {response.ConsumedAmount} item(s), but only {removedAmount} matching item(s) remained in the local inventory.");
        }

        Vector3 basePosition = response.DisplayPosition;
        int line = 0;
        if (response.TameDelta > 0)
        {
            ShowFloatingText(basePosition + Vector3.up * (0.35f * line++), $"+{response.TameDelta}s", DamageText.TextType.Bonus);
        }

        if (response.GrowthDelta > 0)
        {
            ShowFloatingText(basePosition + Vector3.up * (0.35f * line++), $"+{response.GrowthDelta}s", DamageText.TextType.Bonus);
        }

        if (response.FedDelta > 0f)
        {
            ShowFloatingText(basePosition + Vector3.up * (0.35f * line++), FeedLocalization.Format("$flg_floating_fed", FormatDurationClock(response.FedDelta)), DamageText.TextType.Bonus);
        }

        if (response.StarDelta > 0)
        {
            ShowFloatingText(basePosition + Vector3.up * (0.35f * line++), $"+{response.StarDelta}", DamageText.TextType.Bonus);
        }

        if (response.HealPercent > 0f)
        {
            ShowFloatingText(basePosition + Vector3.up * (0.35f * line++), FeedLocalization.Format("$flg_floating_hp", response.HealPercent.ToString("0.#", CultureInfo.InvariantCulture)), DamageText.TextType.Heal);
        }

        if (!string.IsNullOrWhiteSpace(response.Message))
        {
            player.Message(MessageHud.MessageType.Center, response.Message);
        }
    }

    private static void ApplyAutomaticFeedEffects(
        FeedTargetContext context,
        ResolvedFoodEntry? dailyFood,
        ResolvedFoodEntry? levelUpFood,
        bool hasLevelUpItems)
    {
        float feedMultiplier = AnyNearbyPlayerHasTamingBoost(((Component)context.Character).transform.position)
            ? FeedLikeGrandmaPlugin.GetFeedBoostMultiplier()
            : 1f;

        FeedEffectPlan plan = BuildFeedEffectPlan(
            context,
            dailyFood,
            levelUpFood,
            hasLevelUpItems,
            amount: 1,
            feedMultiplier);

        if ((object?)context.Tameable != null && plan.FedDeltaRequested > 0f)
        {
            AddFedTime(context, plan.FedDeltaRequested);
        }

        ApplyTameDelta(context, plan.TameDeltaRequested);
        ApplyGrowthDelta(context, plan.GrowthDeltaRequested, out bool grewThisFeed);

        if (plan.StarDeltaRequested > 0)
        {
            ApplyStarProgress(context, plan.StarDeltaRequested);
        }

        if (!grewThisFeed)
        {
            ApplyHealPercent(context, plan.HealPercentRequested);
        }
    }

    private static bool TryGetFeedEntriesForItem(
        FeedTargetContext context,
        string itemPrefabName,
        int itemQuality,
        out ResolvedFoodEntry? dailyFood,
        out ResolvedFoodEntry? levelUpFood,
        out bool hasLevelUpItems)
    {
        int normalizedQuality = Mathf.Max(1, itemQuality);
        dailyFood = FindMatchingFood(context.Rule.Foods, itemPrefabName, normalizedQuality);
        hasLevelUpItems = TryGetLevelUpItems(context, out IReadOnlyList<ResolvedFoodEntry>? levelUpFoods);
        levelUpFood = hasLevelUpItems && levelUpFoods != null
            ? FindMatchingFood(levelUpFoods, itemPrefabName, normalizedQuality)
            : null;
        return dailyFood != null || levelUpFood != null;
    }

    private static bool TryGetAutoConsumeMatch(
        FeedTargetContext context,
        ItemDrop.ItemData item,
        out bool canConsume,
        out bool matchesLevelUpFood)
    {
        canConsume = false;
        matchesLevelUpFood = false;
        if (!TryGetItemPrefabName(item, out string itemPrefabName))
        {
            return false;
        }

        int itemQuality = Mathf.Max(1, item.m_quality);
        if (!TryGetFeedEntriesForItem(context, itemPrefabName, itemQuality, out ResolvedFoodEntry? dailyFood, out ResolvedFoodEntry? levelUpFood, out _))
        {
            return true;
        }

        canConsume = dailyFood != null || levelUpFood != null;
        matchesLevelUpFood = levelUpFood != null;
        return true;
    }

    private static void ShowFloatingText(Vector3 position, string text, DamageText.TextType type)
    {
        if ((object)DamageText.instance != null)
        {
            DamageText.instance.ShowText(type, position, text, player: false);
        }
    }

    private static bool TryGetHoverFoods(Player player, out FeedHoverFoodRows rows)
    {
        rows = default;
        if (player == null)
        {
            return false;
        }

        GameObject? hoverObject = player.GetHoverObject();
        if (hoverObject == null || !TryBuildContext(hoverObject, out FeedTargetContext context))
        {
            return false;
        }

        return TryBuildHoverFoodRows(player, context, out rows);
    }

    private static bool TryBuildHoverFoodRows(Player? player, FeedTargetContext context, out FeedHoverFoodRows rows)
    {
        rows = default;
        if (player == null ||
            (FeedLikeGrandmaPlugin.RequireCrouchForFoodIcons.Value && !player.IsCrouching()))
        {
            return false;
        }

        if (context.BaseAI != null &&
            context.BaseAI.IsAlerted() &&
            !FeedLikeGrandmaPlugin.ShowFoodEvenIfAlerted.Value.IsOn())
        {
            return false;
        }

        IReadOnlyList<ResolvedFoodEntry> levelUpFoods = TryGetLevelUpItems(context, out IReadOnlyList<ResolvedFoodEntry>? currentLevelUpFoods) && currentLevelUpFoods != null
            ? currentLevelUpFoods
            : Array.Empty<ResolvedFoodEntry>();
        IReadOnlyList<ResolvedFoodEntry> dailyFoods = context.Rule.Foods;
        if (levelUpFoods.Count == 0 && dailyFoods.Count == 0)
        {
            return false;
        }

        int currentLevel = levelUpFoods.Count > 0 ? Mathf.Max(1, context.Character.GetLevel()) : 0;
        rows = new FeedHoverFoodRows(levelUpFoods, dailyFoods, currentLevel);
        return true;
    }

    private static bool TryGetLevelUpItems(FeedTargetContext context, out IReadOnlyList<ResolvedFoodEntry>? foods)
    {
        foods = null;
        if (!ShouldUseLevelUpItems(context))
        {
            return false;
        }

        int currentLevel = Mathf.Max(1, context.Character.GetLevel());
        if (!context.Rule.LevelUpItems.TryGetValue(currentLevel, out List<ResolvedFoodEntry>? levelFoods))
        {
            return false;
        }

        foods = levelFoods;
        return true;
    }

    private static bool ShouldUseLevelUpItems(FeedTargetContext context)
    {
        return context.Character.IsTamed() &&
               (object?)context.Tameable != null &&
               (object?)context.Growup == null &&
               CanUseTamingTimeRequirements(context) &&
               CanGainLevel(context);
    }

    private static ResolvedFoodEntry? FindMatchingFood(IEnumerable<ResolvedFoodEntry> foods, string itemPrefabName, int itemQuality)
    {
        ResolvedFoodEntry? wildcard = null;
        foreach (ResolvedFoodEntry food in foods)
        {
            if (!food.PrefabName.Equals(itemPrefabName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (food.RequiredQuality.HasValue)
            {
                if (food.RequiredQuality.Value == itemQuality)
                {
                    return food;
                }

                continue;
            }

            wildcard ??= food;
        }

        return wildcard;
    }

    private static bool TryBuildContext(GameObject hoverObject, out FeedTargetContext context)
    {
        Character? character = hoverObject.GetComponentInParent<Character>();
        return TryBuildContext(character, out context);
    }

    private static bool TryBuildContext(Character? character, out FeedTargetContext context)
    {
        context = default;
        if ((object?)character == null)
        {
            return false;
        }

        GameObject characterObject = ((Component)character).gameObject;
        string prefabName = NormalizePrefabName(characterObject.name);
        if (ExcludedPrefabNames.Contains(prefabName) ||
            character.m_faction == Character.Faction.PlayerSpawned ||
            character.m_faction == Character.Faction.Players)
        {
            return false;
        }

        if (!ResolvedRules.TryGetValue(prefabName, out ResolvedFeedingRule? rule))
        {
            return false;
        }

        ZNetView? nview = characterObject.GetComponent<ZNetView>();
        if ((object?)nview == null || !nview.IsValid())
        {
            return false;
        }

        context = new FeedTargetContext(
            character,
            characterObject.GetComponent<Tameable>(),
            characterObject.GetComponent<Growup>(),
            character.GetBaseAI(),
            nview,
            rule);

        return true;
    }

    private static string NormalizePrefabName(string name)
    {
        int parenthesisIndex = name.IndexOf('(');
        int spaceIndex = name.IndexOf(' ');
        int index = parenthesisIndex < 0
            ? spaceIndex
            : spaceIndex < 0 ? parenthesisIndex : Mathf.Min(parenthesisIndex, spaceIndex);
        return index >= 0 ? name.Substring(0, index) : name;
    }

    private static bool TryGetItemPrefabName(ItemDrop.ItemData item, out string prefabName)
    {
        prefabName = string.Empty;
        if ((object?)item.m_dropPrefab != null)
        {
            prefabName = item.m_dropPrefab.name;
            return true;
        }

        if ((object)ObjectDB.instance != null && ObjectDB.instance.TryGetItemPrefab(item.m_shared, out GameObject prefab))
        {
            prefabName = prefab.name;
            return true;
        }

        return false;
    }

    private static HashSet<string> CollectValidFoodNames(GameObject prefab, HashSet<string> visited)
    {
        HashSet<string> foods = new(StringComparer.OrdinalIgnoreCase);
        if ((object?)prefab == null || !visited.Add(prefab.name))
        {
            return foods;
        }

        MonsterAI? monsterAI = prefab.GetComponent<MonsterAI>();
        Tameable? tameable = prefab.GetComponent<Tameable>();
        if ((object?)monsterAI != null && (object?)tameable != null && monsterAI.m_consumeItems != null)
        {
            foreach (ItemDrop consumeItem in monsterAI.m_consumeItems)
            {
                if ((object?)consumeItem != null)
                {
                    foods.Add(((Component)consumeItem).gameObject.name);
                }
            }

        }

        Growup? growup = prefab.GetComponent<Growup>();
        foreach (GameObject target in EnumerateGrowthTargets(growup))
        {
            foods.UnionWith(CollectValidFoodNames(target, visited));
        }

        return foods;
    }

    private static IEnumerable<GameObject> EnumerateGrowthTargets(Growup? growup)
    {
        if ((object?)growup == null)
        {
            yield break;
        }

        if ((object?)growup.m_grownPrefab != null)
        {
            yield return growup.m_grownPrefab;
        }

        if (growup.m_altGrownPrefabs == null)
        {
            yield break;
        }

        foreach (Growup.GrownEntry entry in growup.m_altGrownPrefabs)
        {
            if ((object?)entry?.m_prefab != null)
            {
                yield return entry.m_prefab;
            }
        }
    }

    private static float AddFedTime(FeedTargetContext context, float requestedSeconds)
    {
        if ((object?)context.Tameable == null || requestedSeconds <= 0f)
        {
            return 0f;
        }

        ApplyEffectiveFedDuration(context);
        bool wasHungry = context.Tameable.IsHungry();
        float before = GetFedRemainingSeconds(context, out float maxFedSeconds);
        if (maxFedSeconds <= 0f)
        {
            return 0f;
        }

        float after = Mathf.Clamp(before + requestedSeconds, 0f, maxFedSeconds);
        SetFedRemainingSeconds(context, after, maxFedSeconds);
        float delta = Mathf.Max(0f, after - before);
        if (delta > 0f && wasHungry)
        {
            context.Tameable.m_sootheEffect.Create(context.Character.GetCenterPoint(), Quaternion.identity);
        }

        return delta;
    }

    private static float GetPotentialFedDelta(FeedTargetContext context, float requestedSeconds)
    {
        if ((object?)context.Tameable == null || requestedSeconds <= 0f)
        {
            return 0f;
        }

        float before = GetFedRemainingSeconds(context, out float maxFedSeconds);
        return maxFedSeconds <= 0f ? 0f : Mathf.Max(0f, Mathf.Clamp(before + requestedSeconds, 0f, maxFedSeconds) - before);
    }

    private static void ApplyEffectiveFedDuration(FeedTargetContext context)
    {
        if ((object?)context.Tameable == null)
        {
            return;
        }

        float maxFedSeconds = GetMaxFedSeconds(context);
        if (maxFedSeconds > 0f)
        {
            context.Tameable.m_fedDuration = maxFedSeconds;
        }
    }

    private static float GetFedRemainingSeconds(FeedTargetContext context, out float maxFedSeconds)
    {
        maxFedSeconds = GetMaxFedSeconds(context);
        if (maxFedSeconds <= 0f || (object?)context.Tameable == null || !context.NView.IsValid())
        {
            return 0f;
        }

        long lastFeedingTicks = context.NView.GetZDO().GetLong(ZDOVars.s_tameLastFeeding, 0L);
        if (lastFeedingTicks <= 0L)
        {
            return 0f;
        }

        double elapsedSeconds = (ZNet.instance.GetTime() - new DateTime(lastFeedingTicks)).TotalSeconds;
        return Mathf.Clamp(maxFedSeconds - (float)elapsedSeconds, 0f, maxFedSeconds);
    }

    private static void SetFedRemainingSeconds(FeedTargetContext context, float remainingSeconds, float maxFedSeconds)
    {
        if (!context.NView.IsValid())
        {
            return;
        }

        if (remainingSeconds <= 0f || maxFedSeconds <= 0f)
        {
            context.NView.GetZDO().Set(ZDOVars.s_tameLastFeeding, 0L);
            return;
        }

        DateTime lastFeedingTime = ZNet.instance.GetTime().AddSeconds(-(maxFedSeconds - remainingSeconds));
        context.NView.GetZDO().Set(ZDOVars.s_tameLastFeeding, lastFeedingTime.Ticks);
    }

    private static float GetMaxFedSeconds(FeedTargetContext context)
    {
        float baseFedSeconds = GetBaseFedSeconds(context);
        if (baseFedSeconds <= 0f)
        {
            return 0f;
        }

        int level = Mathf.Max(1, context.Character.GetLevel());
        float levelMultiplier = Mathf.Max(1f, FeedLikeGrandmaPlugin.LevelFedDurationMultiplier.Value);
        float scale = 1f + (level - 1) * (levelMultiplier - 1f);
        return baseFedSeconds * scale;
    }

    private static float GetBaseFedSeconds(FeedTargetContext context)
    {
        Tameable? prefabTameable = context.Rule.Prefab.GetComponent<Tameable>();
        if ((object?)prefabTameable != null && prefabTameable.m_fedDuration > 0f)
        {
            return prefabTameable.m_fedDuration;
        }

        return context.Tameable?.m_fedDuration ?? 0f;
    }

    private static string FormatDurationClock(float seconds)
    {
        int totalSeconds = Mathf.Max(0, Mathf.CeilToInt(seconds));
        int hours = totalSeconds / 3600;
        int minutes = totalSeconds % 3600 / 60;
        int secs = totalSeconds % 60;
        return hours > 0
            ? $"{hours.ToString(CultureInfo.InvariantCulture)}:{minutes.ToString("00", CultureInfo.InvariantCulture)}:{secs.ToString("00", CultureInfo.InvariantCulture)}"
            : $"{minutes.ToString(CultureInfo.InvariantCulture)}:{secs.ToString("00", CultureInfo.InvariantCulture)}";
    }

    private static int CapAmountForSingleLevelUp(FeedTargetContext context, int amount, float starDeltaPerItem)
    {
        if (amount <= 1 || starDeltaPerItem <= 0f)
        {
            return amount;
        }

        int currentLevel = Mathf.Max(1, context.Character.GetLevel());
        if (currentLevel >= GetMaxAllowedLevel(context))
        {
            return amount;
        }

        float required = GetRequiredStarProgress(context, currentLevel + 1);
        float progress = Mathf.Clamp(GetStarProgress(context.NView), 0f, required);
        float remaining = Mathf.Max(0f, required - progress);
        if (remaining <= 0.0001f)
        {
            return 1;
        }

        int usefulAmount = Mathf.CeilToInt(remaining / starDeltaPerItem);
        return Mathf.Clamp(usefulAmount, 1, amount);
    }

    private static int ApplyStarProgress(FeedTargetContext context, int requestedDelta)
    {
        if (requestedDelta <= 0)
        {
            return 0;
        }

        int maxLevel = GetMaxAllowedLevel(context);
        int currentLevel = Mathf.Max(1, context.Character.GetLevel());
        if (currentLevel >= maxLevel)
        {
            SetStarProgress(context.NView, 0f);
            return 0;
        }

        float progress = GetStarProgress(context.NView);
        int applied = 0;
        int targetLevel = currentLevel + 1;
        float required = GetRequiredStarProgress(context, targetLevel);
        if (progress + 0.0001f < required)
        {
            int toApply = Mathf.Min(requestedDelta, Mathf.CeilToInt(required - progress));
            progress += toApply;
            applied = toApply;
        }

        if (progress + 0.0001f >= required)
        {
            int levelBefore = context.Character.GetLevel();
            FeedCreatureModifierCompatibility.SetLevelPreservingCllcModifiers(context.Character, targetLevel);
            if (context.Character.IsTamed() && context.Character.GetLevel() > levelBefore)
            {
                SpawnTamedProgressionEffect(((Component)context.Character).transform);
            }

            ApplyEffectiveFedDuration(context);
            progress = 0f;
        }

        SetStarProgress(context.NView, progress);
        return applied;
    }

    private static float GetRequiredStarProgress(FeedTargetContext context, int targetLevel)
    {
        return GetLevelScaledTamingTime(context.Tameable!.m_tamingTime, targetLevel);
    }

    private static bool CanUseTamingTimeRequirements(FeedTargetContext context)
    {
        return (object?)context.Tameable != null && context.Tameable.m_tamingTime > 0f;
    }

    private static void EnsureLevelScaledTameTime(FeedTargetContext context)
    {
        if (!CanUseTamingTimeRequirements(context) || context.Character.IsTamed() || !context.NView.IsValid() || !context.NView.IsOwner())
        {
            return;
        }

        ZDO zdo = context.NView.GetZDO();
        int level = Mathf.Max(1, context.Character.GetLevel());
        float baseTamingTime = context.Tameable!.m_tamingTime;
        float factor = GetLevelTamingTimeMultiplier();
        float desiredTotal = GetLevelScaledTamingTime(baseTamingTime, level);
        float appliedTotal = zdo.GetFloat(LevelScaledTameTotalHash, 0f);
        float appliedBase = zdo.GetFloat(LevelScaledTameBaseHash, 0f);
        float appliedFactor = zdo.GetFloat(LevelScaledTameFactorHash, 0f);
        int appliedLevel = zdo.GetInt(LevelScaledTameLevelHash, 0);

        if (appliedLevel == level &&
            Approximately(appliedBase, baseTamingTime) &&
            Approximately(appliedFactor, factor) &&
            Approximately(appliedTotal, desiredTotal))
        {
            return;
        }

        float remaining = context.Tameable.GetRemainingTime();
        float previousTotal = ResolvePreviousTameTotal(baseTamingTime, remaining, appliedTotal);
        float progress = Mathf.Clamp01(1f - remaining / Mathf.Max(1f, previousTotal));
        float scaledRemaining = desiredTotal * (1f - progress);

        zdo.Set(ZDOVars.s_tameTimeLeft, Mathf.Max(0f, scaledRemaining));
        zdo.Set(LevelScaledTameTotalHash, desiredTotal);
        zdo.Set(LevelScaledTameBaseHash, baseTamingTime);
        zdo.Set(LevelScaledTameFactorHash, factor);
        zdo.Set(LevelScaledTameLevelHash, level);
    }

    private static void GetTameProgress(FeedTargetContext context, out float elapsed, out float total)
    {
        elapsed = 0f;
        total = 0f;
        if (!CanUseTamingTimeRequirements(context))
        {
            return;
        }

        int level = Mathf.Max(1, context.Character.GetLevel());
        float baseTamingTime = context.Tameable!.m_tamingTime;
        float remaining = context.Tameable.GetRemainingTime();
        float appliedTotal = context.NView.GetZDO().GetFloat(LevelScaledTameTotalHash, 0f);
        float previousTotal = ResolvePreviousTameTotal(baseTamingTime, remaining, appliedTotal);
        float progress = Mathf.Clamp01(1f - remaining / Mathf.Max(1f, previousTotal));
        total = GetLevelScaledTamingTime(baseTamingTime, level);
        elapsed = total * progress;
    }

    private static float GetLevelScaledTamingTime(float baseTamingTime, int targetLevel)
    {
        if (baseTamingTime <= 0f)
        {
            return 0f;
        }

        float factor = GetLevelTamingTimeMultiplier();
        int levelSteps = Mathf.Max(0, targetLevel - 1);
        return baseTamingTime * (1f + (factor - 1f) * levelSteps);
    }

    private static float GetLevelTamingTimeMultiplier()
    {
        return Mathf.Max(1f, FeedLikeGrandmaPlugin.LevelTamingTimeMultiplier.Value);
    }

    private static float ResolvePreviousTameTotal(float baseTamingTime, float remaining, float appliedTotal)
    {
        if (appliedTotal > 0f)
        {
            return appliedTotal;
        }

        return remaining > baseTamingTime ? remaining : baseTamingTime;
    }

    private static bool Approximately(float left, float right)
    {
        return Mathf.Abs(left - right) < 0.001f;
    }

    private static float GetStarProgress(ZNetView nview)
    {
        return nview.GetZDO().GetFloat(StarProgressHash, 0f);
    }

    private static void SetStarProgress(ZNetView nview, float value)
    {
        nview.GetZDO().Set(StarProgressHash, Mathf.Max(0f, value));
    }

    private static float AdvanceGrowth(Growup growup, Character character)
    {
        float elapsed = GetGrowthElapsedSeconds(growup, character, initializeIfMissing: true);
        long nowTicks = ZNet.instance.GetTime().Ticks;
        long lastTicks = growup.m_nview.GetZDO().GetLong(GrowthLastEvalHash, nowTicks);
        if (lastTicks == 0L)
        {
            growup.m_nview.GetZDO().Set(GrowthLastEvalHash, nowTicks);
            return elapsed;
        }

        float deltaSeconds = (float)new TimeSpan(nowTicks - lastTicks).TotalSeconds;
        if (deltaSeconds <= 0f)
        {
            return elapsed;
        }

        growup.m_nview.GetZDO().Set(GrowthLastEvalHash, nowTicks);
        if (!character.IsTamed())
        {
            return elapsed;
        }

        float multiplier = AnyNearbyPlayerHasTamingBoost(((Component)growup).transform.position)
            ? FeedLikeGrandmaPlugin.GetGrowthBoostMultiplier()
            : 1f;

        elapsed += deltaSeconds * multiplier;
        SetGrowthElapsedSeconds(growup, elapsed);
        return elapsed;
    }

    private static float GetGrowthElapsedSeconds(Growup growup, Character character, bool initializeIfMissing)
    {
        ZDO zdo = growup.m_nview.GetZDO();
        if (zdo == null)
        {
            return 0f;
        }

        float elapsed = zdo.GetFloat(GrowthElapsedHash, float.MinValue);
        if (elapsed != float.MinValue)
        {
            return elapsed;
        }

        if (!initializeIfMissing)
        {
            BaseAI? baseAI = character.GetBaseAI();
            if ((object?)baseAI != null && character.IsTamed())
            {
                return Mathf.Min((float)baseAI.GetTimeSinceSpawned().TotalSeconds, growup.m_growTime);
            }

            return 0f;
        }

        float initialElapsed = 0f;
        BaseAI? ownerBaseAI = character.GetBaseAI();
        if ((object?)ownerBaseAI != null && character.IsTamed())
        {
            initialElapsed = Mathf.Min((float)ownerBaseAI.GetTimeSinceSpawned().TotalSeconds, growup.m_growTime);
        }

        zdo.Set(GrowthElapsedHash, initialElapsed);
        zdo.Set(GrowthLastEvalHash, ZNet.instance.GetTime().Ticks);
        return initialElapsed;
    }

    private static void SetGrowthElapsedSeconds(Growup growup, float value)
    {
        growup.m_nview.GetZDO().Set(GrowthElapsedHash, Mathf.Max(0f, value));
        growup.m_nview.GetZDO().Set(GrowthLastEvalHash, ZNet.instance.GetTime().Ticks);
    }

    private static void GrowNow(Growup growup, Character currentCharacter)
    {
        FeedCreatureModifierCompatibility.CaptureCllcModifiers(currentCharacter, out string cllcInfusion, out string cllcExtraEffect);
        Character? grownCharacter = UnityEngine.Object.Instantiate(growup.GetPrefab(), ((Component)growup).transform.position, ((Component)growup).transform.rotation).GetComponent<Character>();
        if ((object?)grownCharacter != null)
        {
            if (growup.m_inheritTame)
            {
                grownCharacter.SetTamed(currentCharacter.IsTamed());
            }

            grownCharacter.SetLevel(currentCharacter.GetLevel());
            FeedCreatureModifierCompatibility.InheritCreatureManagerModifiers(currentCharacter, grownCharacter);
            FeedCreatureModifierCompatibility.RestoreCllcModifiers(grownCharacter, cllcInfusion, cllcExtraEffect);
            if (currentCharacter.IsTamed())
            {
                SpawnTamedProgressionEffect(((Component)grownCharacter).transform);
            }
        }

        growup.m_nview.Destroy();
    }

    private static void SpawnTamedProgressionEffect(Transform target)
    {
        ZNetScene? scene = ZNetScene.instance;
        if ((object?)scene == null)
        {
            return;
        }

        GameObject? effectPrefab = scene.GetPrefab(TamedProgressionEffectPrefab);
        if ((object?)effectPrefab != null)
        {
            UnityEngine.Object.Instantiate(effectPrefab, target.position, target.rotation);
        }
    }

    private static bool AnyNearbyPlayerHasTamingBoost(Vector3 position)
    {
        NearbyTamingBoostPlayers.Clear();
        try
        {
            Player.GetPlayersInRange(position, MeadBoostRange, NearbyTamingBoostPlayers);
            foreach (Player player in NearbyTamingBoostPlayers)
            {
                if ((object?)player != null &&
                    player.GetSEMan().HaveStatusAttribute(StatusEffect.StatusAttribute.TamingBoost))
                {
                    return true;
                }
            }

            return false;
        }
        finally
        {
            NearbyTamingBoostPlayers.Clear();
        }
    }

    private static Player? FindPlayer(ZDOID playerId)
    {
        GameObject? playerObject = ZNetScene.instance.FindInstance(playerId);
        return (object?)playerObject == null ? null : playerObject.GetComponent<Player>();
    }

    private static bool IsRequesterInFeedRange(Player requester, GameObject targetObject)
    {
        Character? target = targetObject.GetComponent<Character>();
        if ((object?)target == null)
        {
            return true;
        }

        Vector3 requesterEye = requester.GetEyePoint();
        Vector3 targetPoint = (object?)target.m_collider != null && target.m_collider.enabled
            ? target.m_collider.ClosestPoint(requesterEye)
            : target.GetCenterPoint();
        float maxDistance = Mathf.Max(0f, requester.m_maxInteractDistance) + FeedRequestDistanceTolerance;
        return (targetPoint - requesterEye).sqrMagnitude <= maxDistance * maxDistance;
    }

    private static void CleanupCompletedFeedRequests(long nowTicks)
    {
        if (CompletedFeedRequests.Count == 0)
        {
            return;
        }

        List<(long Sender, int RequestId)>? expiredKeys = null;
        foreach (KeyValuePair<(long Sender, int RequestId), CompletedFeedRequest> entry in CompletedFeedRequests)
        {
            if (nowTicks - entry.Value.CompletedAtTicks <= CompletedRequestRetentionTicks)
            {
                continue;
            }

            expiredKeys ??= new List<(long Sender, int RequestId)>();
            expiredKeys.Add(entry.Key);
        }

        if (expiredKeys == null)
        {
            return;
        }

        foreach ((long Sender, int RequestId) key in expiredKeys)
        {
            CompletedFeedRequests.Remove(key);
        }
    }

    private static void StoreCompletedFeedRequest(
        (long Sender, int RequestId) key,
        CompletedFeedRequest completed)
    {
        if (CompletedFeedRequests.Count >= MaxCompletedRequestCacheEntries)
        {
            (long Sender, int RequestId) oldestKey = default;
            long oldestTicks = long.MaxValue;
            foreach (KeyValuePair<(long Sender, int RequestId), CompletedFeedRequest> entry in CompletedFeedRequests)
            {
                if (entry.Value.CompletedAtTicks >= oldestTicks)
                {
                    continue;
                }

                oldestKey = entry.Key;
                oldestTicks = entry.Value.CompletedAtTicks;
            }

            CompletedFeedRequests.Remove(oldestKey);
        }

        CompletedFeedRequests[key] = completed;
    }

    private static int CountItemsByPrefab(Inventory inventory, string prefabName, int itemQuality)
    {
        int count = 0;
        foreach (ItemDrop.ItemData item in inventory.GetAllItems())
        {
            if (ItemMatches(item, prefabName, itemQuality))
            {
                count += item.m_stack;
            }
        }

        return count;
    }

    private static int CountPendingItems(string prefabName, int itemQuality)
    {
        int count = 0;
        foreach (PendingFeedRequest pending in PendingRequests.Values)
        {
            if (pending.ItemQuality == itemQuality &&
                pending.ItemPrefabName.Equals(prefabName, StringComparison.OrdinalIgnoreCase))
            {
                count += pending.RequestedAmount;
            }
        }

        return count;
    }

    private static int RemoveItemsByPrefab(Inventory inventory, string prefabName, int itemQuality, int amount)
    {
        if (amount <= 0)
        {
            return 0;
        }

        int removedAmount = 0;
        foreach (ItemDrop.ItemData item in inventory.GetAllItems().ToList())
        {
            if (!ItemMatches(item, prefabName, itemQuality))
            {
                continue;
            }

            int remove = Mathf.Min(amount, item.m_stack);
            inventory.RemoveItem(item, remove);
            amount -= remove;
            removedAmount += remove;
            if (amount <= 0)
            {
                break;
            }
        }

        return removedAmount;
    }

    private static bool ItemMatches(ItemDrop.ItemData item, string prefabName, int itemQuality)
    {
        return (object?)item != null &&
               Mathf.Max(1, item.m_quality) == Mathf.Max(1, itemQuality) &&
               TryGetItemPrefabName(item, out string itemPrefabName) &&
               itemPrefabName.Equals(prefabName, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class PendingFeedRequest
    {
        public string ItemPrefabName { get; init; } = string.Empty;
        public int ItemQuality { get; init; } = 1;
        public ZDOID TargetId { get; init; }
        public long ExpectedResponderId { get; init; }
        public int RequestedAmount { get; init; }
        public long CreatedAtTicks { get; init; }
    }

    private sealed class CompletedFeedRequest
    {
        public ZDOID RequesterId { get; init; }
        public ZDOID TargetId { get; init; }
        public string ItemPrefabName { get; init; } = string.Empty;
        public int ItemQuality { get; init; }
        public int RequestedAmount { get; init; }
        public long CompletedAtTicks { get; init; }
        public FeedResponseMessage Response { get; init; } = null!;

        public bool Matches(FeedRequestMessage request, ZDOID targetId)
        {
            return RequesterId.Equals(request.RequesterId) &&
                   TargetId.Equals(targetId) &&
                   ItemQuality == request.ItemQuality &&
                   RequestedAmount == request.RequestedAmount &&
                   ItemPrefabName.Equals(request.ItemPrefabName, StringComparison.OrdinalIgnoreCase);
        }
    }

    private sealed class FeedEffectPlan
    {
        public int Amount { get; init; }
        public int TameDeltaRequested { get; init; }
        public int GrowthDeltaRequested { get; init; }
        public int StarDeltaRequested { get; init; }
        public float HealPercentRequested { get; init; }
        public float FedDeltaRequested { get; init; }
        public float FedDeltaPossible { get; init; }

        public bool HasAnyEffect =>
            TameDeltaRequested > 0 ||
            GrowthDeltaRequested > 0 ||
            StarDeltaRequested > 0 ||
            HealPercentRequested > 0f ||
            FedDeltaPossible > 0f;
    }

    private readonly struct FeedTargetContext
    {
        public FeedTargetContext(
            Character character,
            Tameable? tameable,
            Growup? growup,
            BaseAI? baseAI,
            ZNetView nview,
            ResolvedFeedingRule rule)
        {
            Character = character;
            Tameable = tameable;
            Growup = growup;
            BaseAI = baseAI;
            NView = nview;
            Rule = rule;
        }

        public Character Character { get; }
        public Tameable? Tameable { get; }
        public Growup? Growup { get; }
        public BaseAI? BaseAI { get; }
        public ZNetView NView { get; }
        public ResolvedFeedingRule Rule { get; }
    }

    private sealed class FeedRequestMessage
    {
        public int RequestId { get; init; }
        public ZDOID RequesterId { get; init; }
        public string ItemPrefabName { get; init; } = string.Empty;
        public int ItemQuality { get; init; } = 1;
        public int RequestedAmount { get; init; }

        public ZPackage Write()
        {
            ZPackage pkg = new();
            pkg.Write(RequestId);
            pkg.Write(RequesterId);
            pkg.Write(ItemPrefabName);
            pkg.Write(ItemQuality);
            pkg.Write(RequestedAmount);
            return pkg;
        }

        public static bool TryRead(ZPackage pkg, out FeedRequestMessage? message)
        {
            message = null;
            try
            {
                message = new FeedRequestMessage
                {
                    RequestId = pkg.ReadInt(),
                    RequesterId = pkg.ReadZDOID(),
                    ItemPrefabName = pkg.ReadString(),
                    ItemQuality = pkg.ReadInt(),
                    RequestedAmount = pkg.ReadInt()
                };
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }
    }

    private sealed class FeedResponseMessage
    {
        public int RequestId { get; init; }
        public bool Success { get; set; }
        public int ConsumedAmount { get; set; }
        public string ItemPrefabName { get; init; } = string.Empty;
        public int TameDelta { get; set; }
        public int GrowthDelta { get; set; }
        public int StarDelta { get; set; }
        public float FedDelta { get; set; }
        public float HealPercent { get; set; }
        public ZDOID TargetId { get; set; }
        public Vector3 DisplayPosition { get; set; }
        public string Message { get; set; } = string.Empty;

        public ZPackage Write()
        {
            ZPackage pkg = new();
            pkg.Write(RequestId);
            pkg.Write(Success);
            pkg.Write(ConsumedAmount);
            pkg.Write(ItemPrefabName);
            pkg.Write(TameDelta);
            pkg.Write(GrowthDelta);
            pkg.Write(StarDelta);
            pkg.Write(FedDelta);
            pkg.Write(HealPercent);
            pkg.Write(TargetId);
            pkg.Write(DisplayPosition);
            pkg.Write(Message);
            return pkg;
        }

        public static bool TryRead(ZPackage pkg, out FeedResponseMessage? message)
        {
            message = null;
            try
            {
                message = new FeedResponseMessage
                {
                    RequestId = pkg.ReadInt(),
                    Success = pkg.ReadBool(),
                    ConsumedAmount = pkg.ReadInt(),
                    ItemPrefabName = pkg.ReadString(),
                    TameDelta = pkg.ReadInt(),
                    GrowthDelta = pkg.ReadInt(),
                    StarDelta = pkg.ReadInt(),
                    FedDelta = pkg.ReadSingle(),
                    HealPercent = pkg.ReadSingle(),
                    TargetId = pkg.ReadZDOID(),
                    DisplayPosition = pkg.ReadVector3(),
                    Message = pkg.ReadString()
                };
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }
    }
}
