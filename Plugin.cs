using System;
using System.Collections;
using System.IO;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using JetBrains.Annotations;
using ServerSync;
using UnityEngine;

namespace FeedLikeGrandma;

[Flags]
internal enum RuntimeRefreshFlags
{
    None = 0,
    TamingOrb = 1,
    FeedBarrel = 2,
    FeedRules = 4,
    All = TamingOrb | FeedBarrel | FeedRules
}

[BepInPlugin(ModGuid, ModName, ModVersion)]
[BepInDependency("Yggdrah.BetterRiding", BepInDependency.DependencyFlags.SoftDependency)]
[BepInDependency("Yggdrah.DragonRiders", BepInDependency.DependencyFlags.SoftDependency)]
[BepInDependency("expand_world_data", BepInDependency.DependencyFlags.SoftDependency)]
[BepInDependency("Therzie.Monstrum", BepInDependency.DependencyFlags.SoftDependency)]
[BepInDependency("Therzie.MonstrumDeepNorth", BepInDependency.DependencyFlags.SoftDependency)]
[BepInDependency("Raelaziel.OdinHorse", BepInDependency.DependencyFlags.SoftDependency)]
public sealed class FeedLikeGrandmaPlugin : BaseUnityPlugin
{
    internal const string ModName = "FeedLikeGrandma";
    internal const string ModVersion = "1.0.6";
    internal const string Author = "sighsorry";
    private const string ModGuid = $"{Author}.{ModName}";
    private static readonly string ConfigFileName = $"{ModGuid}.cfg";
    private static readonly string ConfigFileFullPath = Path.Combine(Paths.ConfigPath, ConfigFileName);
    internal static readonly string ConfigDirectoryPath = Path.Combine(Paths.ConfigPath, ModName);
    internal static readonly string YamlFileFullPath = Path.Combine(ConfigDirectoryPath, "feed.yml");
    internal static readonly string ReferenceFileFullPath = Path.Combine(ConfigDirectoryPath, "feed.reference.yml");
    private const float FileReloadDebounceSeconds = 0.25f;

    internal static readonly ManualLogSource Log = BepInEx.Logging.Logger.CreateLogSource(ModName);
    internal static readonly ConfigSync ConfigSync = new(ModGuid)
    {
        DisplayName = ModName,
        CurrentVersion = ModVersion,
        MinimumRequiredVersion = ModVersion
    };
    internal static FeedLikeGrandmaPlugin Instance = null!;
    internal static CustomSyncedValue<string> SyncedYaml = null!;

    internal static ConfigEntry<Toggle> ServerConfigLocked = null!;
    internal static ConfigEntry<Toggle> ShowFeedingHelp = null!;
    internal static ConfigEntry<int> MaxItemsPerFeed = null!;
    internal static ConfigEntry<float> FeedCooldownSeconds = null!;
    internal static ConfigEntry<LevelUpLimit> LevelUpLimitMode = null!;
    internal static ConfigEntry<int> LevelUpMaxLevel = null!;
    internal static ConfigEntry<float> LevelTamingTimeMultiplier = null!;
    internal static ConfigEntry<float> LevelFedDurationMultiplier = null!;
    internal static ConfigEntry<Toggle> HorizontalProcreationLimit = null!;
    internal static ConfigEntry<string> FeedBarrelRecipe = null!;
    internal static ConfigEntry<FeedBarrelMaterial> FeedBarrelMaterialStyle = null!;
    internal static ConfigEntry<int> FeedBarrelContainerColumns = null!;
    internal static ConfigEntry<int> FeedBarrelContainerRows = null!;
    internal static ConfigEntry<Toggle> StableMountCamera = null!;
    internal static ConfigEntry<float> MountStaminaDrainMultiplier = null!;
    internal static ConfigEntry<float> MountStaminaRegenMultiplier = null!;
    internal static ConfigEntry<float> RidingSkillStaminaRegenMultiplierAt100 = null!;
    internal static ConfigEntry<Toggle> EncumberedMountSpeedReduction = null!;
    internal static ConfigEntry<Toggle> EncumberedWalkStaminaDrain = null!;
    internal static ConfigEntry<float> EncumberedMountStaminaDrainMultiplier = null!;
    internal static ConfigEntry<float> EncumberedMountStaminaRegenMultiplier = null!;
    internal static ConfigEntry<Toggle> BlockEncumberedDragonFlight = null!;
    internal static ConfigEntry<Toggle> BetterRidingFeedHealPriority = null!;
    internal static ConfigEntry<Toggle> MonstrumMountJumpCompatibility = null!;
    internal static ConfigEntry<string> DefaultDragonRestrictedBiomes = null!;
    internal static ConfigEntry<float> MeadTamerTameMultiplier = null!;
    internal static ConfigEntry<float> MeadTamerGrowthMultiplier = null!;
    internal static ConfigEntry<float> MeadTamerFeedMultiplier = null!;
    internal static ConfigEntry<bool> RequireCrouchForFoodIcons = null!;
    internal static ConfigEntry<float> FoodIconSize = null!;
    internal static ConfigEntry<Toggle> ShowFoodEvenIfAlerted = null!;
    internal static ConfigEntry<string> TamingOrbRecipe = null!;
    internal static ConfigEntry<string> TamingOrbCraftingStation = null!;

    private readonly Harmony _harmony = new(ModGuid);
    private readonly object _reloadLock = new();
    private FileSystemWatcher? _configWatcher;
    private FileSystemWatcher? _yamlWatcher;
    private Coroutine? _configReloadCoroutine;
    private Coroutine? _yamlReloadCoroutine;
    private int _configReloadGeneration;
    private int _yamlReloadGeneration;
    private RuntimeRefreshFlags _pendingRuntimeRefresh;
    private Coroutine? _runtimeRefreshCoroutine;
    private int _runtimeRefreshGeneration;
    private bool _isShuttingDown;
    public enum Toggle
    {
        On = 1,
        Off = 0
    }

    public enum LevelUpLimit
    {
        Vanilla = 0,
        Level = 1
    }

    public enum FeedBarrelMaterial
    {
        Ectoplasm = 0,
        Default = 1
    }

    public void Awake()
    {
        Instance = this;
        FeedLocalization.Load();

        bool originalSaveOnSet = Config.SaveOnConfigSet;
        Config.SaveOnConfigSet = false;

        BindConfigEntries();
        SubscribeRuntimeConfigChanges();
        EnsureYamlFileExists();

        SyncedYaml = new CustomSyncedValue<string>(ConfigSync, "YamlText", string.Empty);
        SyncedYaml.ValueChanged += OnSyncedYamlChanged;
        ConfigSync.SourceOfTruthChanged += OnSourceOfTruthChanged;

        FeedConsoleCommands.Register();
        LoadAndRefreshFeedRules(File.ReadAllText(YamlFileFullPath), "local fallback");

        _ = ConfigSync.AddLockingConfigEntry(ServerConfigLocked);

        _harmony.PatchAll(Assembly.GetExecutingAssembly());

        if (ConfigSync.IsSourceOfTruth)
        {
            PushLocalYamlToSync();
        }

        Config.Save();
        Config.SaveOnConfigSet = originalSaveOnSet;
        SetupWatchers();
    }

    private void OnDestroy()
    {
        _isShuttingDown = true;
        UnsubscribeRuntimeConfigChanges();
        if (_runtimeRefreshCoroutine != null)
        {
            StopCoroutine(_runtimeRefreshCoroutine);
            _runtimeRefreshCoroutine = null;
        }

        if (_configReloadCoroutine != null)
        {
            StopCoroutine(_configReloadCoroutine);
            _configReloadCoroutine = null;
        }

        if (_yamlReloadCoroutine != null)
        {
            StopCoroutine(_yamlReloadCoroutine);
            _yamlReloadCoroutine = null;
        }

        _pendingRuntimeRefresh = RuntimeRefreshFlags.None;

        SyncedYaml.ValueChanged -= OnSyncedYamlChanged;
        ConfigSync.SourceOfTruthChanged -= OnSourceOfTruthChanged;
        TamingOrbSystem.Shutdown();
        FeedBetterRidingCompatibility.Shutdown();
        FeedDragonFlightRestrictionSystem.Shutdown();
        FeedRidingSystem.Shutdown();
        FeedAutoFeedSystem.Shutdown();
        FeedProcreationSystem.Shutdown();
        FeedSystem.Shutdown();
        FeedHoverHud.Shutdown();
        FeedCreatureModifierCompatibility.Shutdown();
        FeedReferenceWriter.Shutdown();
        FeedExternalReferenceSupport.Shutdown();
        _yamlWatcher?.Dispose();
        _configWatcher?.Dispose();
        _harmony.UnpatchSelf();
    }

    private void BindConfigEntries()
    {
        const string general = "01 - General";
        const string feeding = "02 - Feeding";
        const string leveling = "03 - Leveling";
        const string meadTamer = "04 - MeadTamer";
        const string riding = "05 - Riding";
        const string compatibility = "06 - Compatibility";

        ServerConfigLocked = config(general, "Lock Configuration", Toggle.On,
            "If on, the synced config and YAML are controlled by the server.", order: 1000);

        ShowFeedingHelp = config(general, "Show Feeding Help", Toggle.On,
            "If on, shows a brief hotbar feeding instruction below creature progress while food icons are visible.", synchronizedSetting: false, order: 995);

        HorizontalProcreationLimit = config(general, "Horizontal Procreation Limit", Toggle.On,
            "If on, breeding total limits count adults and offspring by horizontal XZ distance, preventing vertical breeder limit bypasses.", order: 990);

        TamingOrbRecipe = config(general, "Taming Orb Recipe", "AncientSeed:3, Ectoplasm:5, SurtlingCore:7, Chitin:9",
            "Crafting recipe for FLG_TamingOrb. Format: ItemPrefab:Amount,OtherPrefab:Amount. Leave empty to disable Taming Orb.", order: 980);

        TamingOrbCraftingStation = config(general, "Taming Orb Crafting Station", "piece_workbench:1",
            "Crafting station for FLG_TamingOrb. Format: StationPrefab:Level. Use None for no station.", order: 970);

        FeedBarrelRecipe = config(general, "Feed Barrel Recipe", "BarrelRings:1, Ectoplasm:3, RoundLog:6, ElderBark:9",
            "Build cost for Feed Barrel. Format: ItemPrefab:Amount,OtherPrefab:Amount. Leave empty to disable the Feed Barrel and container auto-feed.", order: 960);

        FeedBarrelMaterialStyle = config(general, "Feed Barrel Material", FeedBarrelMaterial.Ectoplasm,
            "Visual material for Feed Barrel. Ectoplasm applies Valheim's ectoplasm_mat and regenerates the hammer piece icon; Default keeps the source barrel material.", order: 950);

        FeedBarrelContainerColumns = config(general, "Feed Barrel Container Columns", 6,
            new ConfigDescription(
                "Inventory width for Feed Barrel. Clamped to 6-8 columns.",
                new AcceptableValueRange<int>(6, 8)),
            order: 940);

        FeedBarrelContainerRows = config(general, "Feed Barrel Container Rows", 2,
            new ConfigDescription(
                "Inventory height for Feed Barrel. Clamped to 2-100 rows.",
                new AcceptableValueRange<int>(2, 100)),
            order: 939);

        RequireCrouchForFoodIcons = config(feeding, "Require Crouch For Food Icons", true,
            "If on, food icons are only shown while the local player is crouching.", synchronizedSetting: false, order: 1000);

        FoodIconSize = config(feeding, "Food Icon Size", 36f,
            new ConfigDescription("Size of each hover food icon in pixels.", new AcceptableValueRange<float>(16f, 96f)), synchronizedSetting: false, order: 990);

        ShowFoodEvenIfAlerted = config(feeding, "Show Food Even If Alerted", Toggle.On,
            "If on, food icons remain visible while a creature is alerted. Feeding is still blocked while alerted.", order: 985);

        MaxItemsPerFeed = config(feeding, "Max Items Per Feed", 10,
            new ConfigDescription("Maximum number of items consumed by one manual feed action.", new AcceptableValueRange<int>(1, 100)), order: 980);

        FeedCooldownSeconds = config(feeding, "Feed Cooldown Seconds", 3f,
            new ConfigDescription("Cooldown between manual feed actions on the same creature.", new AcceptableValueRange<float>(0f, 300f)), order: 970);

        LevelUpLimitMode = config(leveling, "Level Up Limit Mode", LevelUpLimit.Vanilla,
            "Vanilla keeps Lox and Hen at Character level 1 and lets other creatures reach Character level 3. Level uses the Level Up Max Level setting.", order: 1000);

        LevelUpMaxLevel = config(leveling, "Level Up Max Level", 3,
            new ConfigDescription("Maximum Character level FLG can raise creatures to when Level Up Limit Mode is Level.", new AcceptableValueRange<int>(1, 100)), order: 990);

        LevelTamingTimeMultiplier = config(leveling, "Level Taming Time Multiplier", 1.5f,
            new ConfigDescription("Scales tame.tamingTime by target creature level. Level 1 uses tamingTime; each higher target level adds tamingTime * (value - 1).", new AcceptableValueRange<float>(1f, 10f)), order: 980);

        LevelFedDurationMultiplier = config(leveling, "Level Fed Duration Multiplier", 1.5f,
            new ConfigDescription("Scales tame.fedDuration by creature level for FLG's stacked fed gauge. Level 1 uses fedDuration; each higher level adds fedDuration * (value - 1).", new AcceptableValueRange<float>(1f, 10f)), order: 970);

        MeadTamerTameMultiplier = config(meadTamer, "Passive Tame Multiplier", 2f,
            new ConfigDescription("Multiplier applied to passive taming speed while a nearby player has the TamingBoost status attribute. x2.0 means double speed.", new AcceptableValueRange<float>(1f, 6f)), order: 1000);

        MeadTamerGrowthMultiplier = config(meadTamer, "Passive Growth Multiplier", 1.5f,
            new ConfigDescription("Multiplier applied to passive growth speed while a nearby player has the TamingBoost status attribute. x1.5 means 50% faster growth.", new AcceptableValueRange<float>(1f, 6f)), order: 990);

        MeadTamerFeedMultiplier = config(meadTamer, "Feed Multiplier", 1.5f,
            new ConfigDescription("Multiplier applied to FLG manual and automatic feeding while the feeding player, or any nearby player for automatic feeding, has the TamingBoost status attribute. Affects taming, growth, level-up, healing, and fed time.", new AcceptableValueRange<float>(1f, 6f)), order: 980);

        StableMountCamera = config(riding, "Stable Mount Camera", Toggle.On,
            "If on, skips Valheim's camera tilt while riding a saddle.", synchronizedSetting: false, order: 1000);

        MountStaminaDrainMultiplier = config(riding, "Mount Stamina Drain Multiplier", 1f,
            new ConfigDescription("Multiplier applied to saddle stamina costs.", new AcceptableValueRange<float>(0f, 10f)), order: 990);

        MountStaminaRegenMultiplier = config(riding, "Mount Stamina Regen Multiplier", 3f,
            new ConfigDescription("Base multiplier applied to saddle stamina regeneration while the local rider is not encumbered.", new AcceptableValueRange<float>(0f, 10f)), order: 980);

        RidingSkillStaminaRegenMultiplierAt100 = config(riding, "Riding Skill Stamina Regen Multiplier At 100", 5f,
            new ConfigDescription("Riding-skill multiplier applied after the selected normal or encumbered regen multiplier. Scales linearly from x1 at Riding 0 to this value at Riding 100.", new AcceptableValueRange<float>(1f, 10f)), order: 970);

        EncumberedMountSpeedReduction = config(riding, "Encumbered Mount Speed Reduction", Toggle.On,
            "If on, a mounted creature is treated as encumbered while its local rider is encumbered. Valheim then blocks run/jump and uses the creature's m_crouchSpeed, which is 2f by vanilla default.", order: 960);

        EncumberedWalkStaminaDrain = config(riding, "Encumbered Walk Stamina Drain", Toggle.On,
            "If on, walking on a mount while encumbered drains saddle stamina. Uses vanilla saddle run stamina drain as the base, scaled by riding skill and Encumbered Mount Stamina Drain Multiplier.", order: 950);

        EncumberedMountStaminaDrainMultiplier = config(riding, "Encumbered Mount Stamina Drain Multiplier", 2f,
            new ConfigDescription("Multiplier applied to saddle stamina costs while the local rider is encumbered.", new AcceptableValueRange<float>(0f, 10f)), order: 940);

        EncumberedMountStaminaRegenMultiplier = config(riding, "Encumbered Mount Stamina Regen Multiplier", 0f,
            new ConfigDescription("Base multiplier applied to saddle stamina regeneration while the local rider is encumbered. At x0, Riding skill cannot restore regeneration.", new AcceptableValueRange<float>(0f, 10f)), order: 930);

        BlockEncumberedDragonFlight = config(compatibility, "Block Encumbered Dragon Flight", Toggle.On,
            "If on, BetterRiding dragons cannot start or continue flying while the local rider is encumbered.", order: 1000);

        BetterRidingFeedHealPriority = config(compatibility, "FLG Feed Heal Priority", Toggle.On,
            "If on, FLG reapplies its automatic feed/heal result after other OnConsumedItem postfixes such as BetterRiding's full heal.", order: 990);

        MonstrumMountJumpCompatibility = config(compatibility, "Monstrum Mount Jump Compatibility", Toggle.On,
            "If on, jump stays on the saddle for Monstrum and MonstrumDeepNorth rideable creatures instead of triggering their forced dismount path.", synchronizedSetting: false, order: 980);

        DefaultDragonRestrictedBiomes = config(compatibility, "Default Dragon Restricted Biomes", "Ashlands",
            "Comma-separated biome names used for dragons without a prefab restrictedBiome override. Leave empty for no global default; prefab restrictedBiome entries still apply. Expand World Data custom biome display names are supported when available.", order: 960);
    }

    private void EnsureYamlFileExists()
    {
        Directory.CreateDirectory(ConfigDirectoryPath);
        if (File.Exists(YamlFileFullPath))
        {
            return;
        }

        File.WriteAllText(YamlFileFullPath,
            FeedReferenceWriter.BuildDefaultOverrideYaml());
    }

    internal static void RefreshGeneratedFilesIfNeeded()
    {
        if (!IsGameDataReady() || ConfigSync.IsSourceOfTruth == false)
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(ConfigDirectoryPath);
            bool wroteReference = WriteFileIfChanged(
                ReferenceFileFullPath,
                FeedReferenceWriter.BuildReferenceYaml(refreshExternalSnapshot: false));
            if (wroteReference)
            {
                Log.LogInfo($"Updated generated FeedLikeGrandma reference YAML at {ReferenceFileFullPath}.");
            }
        }
        catch (Exception ex)
        {
            Log.LogWarning($"Failed to refresh generated FeedLikeGrandma YAML files: {ex}");
        }
    }

    internal static bool TryWriteReferenceConfigurationFile(out string path, out string error)
    {
        path = ReferenceFileFullPath;
        error = string.Empty;

        if (!IsGameDataReady())
        {
            error = "FeedLikeGrandma game data is not ready yet.";
            return false;
        }

        Directory.CreateDirectory(ConfigDirectoryPath);
        File.WriteAllText(path, FeedReferenceWriter.BuildReferenceYaml());
        Log.LogInfo($"Wrote FeedLikeGrandma reference YAML to {path}.");
        return true;
    }

    private static bool WriteFileIfChanged(string path, string content)
    {
        if (File.Exists(path) && string.Equals(File.ReadAllText(path), content, StringComparison.Ordinal))
        {
            return false;
        }

        File.WriteAllText(path, content);
        return true;
    }

    private static bool IsGameDataReady()
    {
        return ZNetScene.instance != null && ObjectDB.instance != null;
    }

    private void SetupWatchers()
    {
        _configWatcher = new FileSystemWatcher(Paths.ConfigPath, ConfigFileName);
        _configWatcher.Changed += OnConfigFileChanged;
        _configWatcher.Created += OnConfigFileChanged;
        _configWatcher.Renamed += OnConfigFileChanged;
        _configWatcher.IncludeSubdirectories = false;
        _configWatcher.SynchronizingObject = ThreadingHelper.SynchronizingObject;
        _configWatcher.EnableRaisingEvents = true;

        Directory.CreateDirectory(ConfigDirectoryPath);
        _yamlWatcher = new FileSystemWatcher(ConfigDirectoryPath, Path.GetFileName(YamlFileFullPath));
        _yamlWatcher.Changed += OnYamlFileChanged;
        _yamlWatcher.Created += OnYamlFileChanged;
        _yamlWatcher.Renamed += OnYamlFileChanged;
        _yamlWatcher.IncludeSubdirectories = false;
        _yamlWatcher.SynchronizingObject = ThreadingHelper.SynchronizingObject;
        _yamlWatcher.EnableRaisingEvents = true;
    }

    private void OnConfigFileChanged(object sender, FileSystemEventArgs e)
    {
        if (_isShuttingDown)
        {
            return;
        }

        _configReloadGeneration++;
        _configReloadCoroutine ??= StartCoroutine(ReloadConfigAfterQuietPeriod());
    }

    private IEnumerator ReloadConfigAfterQuietPeriod()
    {
        while (!_isShuttingDown)
        {
            int generation = _configReloadGeneration;
            yield return new WaitForSecondsRealtime(FileReloadDebounceSeconds);
            if (generation != _configReloadGeneration)
            {
                continue;
            }

            ReloadConfigFromDisk();
            break;
        }

        _configReloadCoroutine = null;
    }

    private void ReloadConfigFromDisk()
    {
        lock (_reloadLock)
        {
            if (!File.Exists(ConfigFileFullPath))
            {
                return;
            }

            try
            {
                Config.Reload();
                QueueRuntimeRefresh(RuntimeRefreshFlags.All);
            }
            catch (Exception ex)
            {
                Log.LogError($"Failed to reload config: {ex}");
            }
        }
    }

    private void OnYamlFileChanged(object sender, FileSystemEventArgs e)
    {
        if (_isShuttingDown)
        {
            return;
        }

        _yamlReloadGeneration++;
        _yamlReloadCoroutine ??= StartCoroutine(ReloadYamlAfterQuietPeriod());
    }

    private IEnumerator ReloadYamlAfterQuietPeriod()
    {
        while (!_isShuttingDown)
        {
            int generation = _yamlReloadGeneration;
            yield return new WaitForSecondsRealtime(FileReloadDebounceSeconds);
            if (generation != _yamlReloadGeneration)
            {
                continue;
            }

            ReloadYamlFromDisk();
            break;
        }

        _yamlReloadCoroutine = null;
    }

    private void ReloadYamlFromDisk()
    {
        lock (_reloadLock)
        {
            if (!File.Exists(YamlFileFullPath))
            {
                return;
            }

            if (!ConfigSync.IsSourceOfTruth)
            {
                Log.LogDebug("Ignoring local YAML change while server data is authoritative.");
                return;
            }

            try
            {
                PushLocalYamlToSync();
            }
            catch (Exception ex)
            {
                Log.LogError($"Failed to reload YAML: {ex}");
            }
        }
    }

    private void PushLocalYamlToSync()
    {
        string yamlText = File.ReadAllText(YamlFileFullPath);
        SyncedYaml.Value = yamlText;
    }

    private void OnSyncedYamlChanged()
    {
        LoadAndRefreshFeedRules(
            SyncedYaml.Value ?? string.Empty,
            ConfigSync.IsSourceOfTruth ? "local sync" : "server sync");
    }

    private void OnSourceOfTruthChanged(bool isSourceOfTruth)
    {
        if (isSourceOfTruth)
        {
            PushLocalYamlToSync();
        }
        else if (!string.IsNullOrWhiteSpace(SyncedYaml.Value))
        {
            LoadAndRefreshFeedRules(SyncedYaml.Value, "server sync");
        }
    }

    private static void LoadAndRefreshFeedRules(string yamlText, string source)
    {
        if (FeedSystem.LoadYamlText(yamlText, source))
        {
            RefreshFeedRuleRuntime();
        }
    }

    private static void RefreshFeedRuleRuntime()
    {
        FeedProcreationSystem.ResetRuntimeCache();
        FeedSystem.RebuildRuntimeData();
        FeedDragonFlightRestrictionSystem.OnRulesChanged();
        RefreshGeneratedFilesIfNeeded();
    }

    internal static float GetTamingBoostMultiplier()
    {
        return Math.Max(1f, MeadTamerTameMultiplier.Value);
    }

    internal static float GetGrowthBoostMultiplier()
    {
        return Math.Max(1f, MeadTamerGrowthMultiplier.Value);
    }

    internal static float GetFeedBoostMultiplier()
    {
        return Math.Max(1f, MeadTamerFeedMultiplier.Value);
    }

    internal static void QueueRuntimeRefresh(RuntimeRefreshFlags flags)
    {
        if (flags == RuntimeRefreshFlags.None || (object?)Instance == null || Instance._isShuttingDown)
        {
            return;
        }

        Instance._pendingRuntimeRefresh |= flags;
        Instance._runtimeRefreshGeneration++;
        Instance._runtimeRefreshCoroutine ??= Instance.StartCoroutine(Instance.FlushRuntimeRefresh());
    }

    private IEnumerator FlushRuntimeRefresh()
    {
        while (!_isShuttingDown)
        {
            int generation = _runtimeRefreshGeneration;
            yield return null;
            if (generation != _runtimeRefreshGeneration)
            {
                continue;
            }

            RuntimeRefreshFlags refresh = _pendingRuntimeRefresh;
            _pendingRuntimeRefresh = RuntimeRefreshFlags.None;
            if ((refresh & RuntimeRefreshFlags.TamingOrb) != 0)
            {
                TryRefreshRuntime("Taming Orb registration", () => TamingOrbSystem.RegisterPrefabsAndRecipes());
            }

            if ((refresh & RuntimeRefreshFlags.FeedBarrel) != 0)
            {
                TryRefreshRuntime("Feed Barrel registration", () => FeedBarrelSystem.RegisterPrefabsAndPieces());
            }

            if ((refresh & RuntimeRefreshFlags.FeedRules) != 0)
            {
                TryRefreshRuntime("feed rule rebuild", RefreshFeedRuleRuntime);
            }

            if (_pendingRuntimeRefresh == RuntimeRefreshFlags.None)
            {
                break;
            }
        }

        _runtimeRefreshCoroutine = null;
    }

    private static void TryRefreshRuntime(string operation, Action refresh)
    {
        try
        {
            refresh();
        }
        catch (Exception ex)
        {
            Log.LogError($"Failed to refresh {operation}: {ex}");
        }
    }

    private static void OnTamingOrbConfigChanged(object sender, EventArgs e)
    {
        QueueRuntimeRefresh(RuntimeRefreshFlags.TamingOrb);
    }

    private static void OnFeedBarrelConfigChanged(object sender, EventArgs e)
    {
        QueueRuntimeRefresh(RuntimeRefreshFlags.FeedBarrel);
    }

    private static void OnDragonFlightConfigChanged(object sender, EventArgs e)
    {
        FeedDragonFlightRestrictionSystem.OnRulesChanged();
    }

    private static void SubscribeRuntimeConfigChanges()
    {
        TamingOrbRecipe.SettingChanged += OnTamingOrbConfigChanged;
        TamingOrbCraftingStation.SettingChanged += OnTamingOrbConfigChanged;
        FeedBarrelRecipe.SettingChanged += OnFeedBarrelConfigChanged;
        FeedBarrelMaterialStyle.SettingChanged += OnFeedBarrelConfigChanged;
        FeedBarrelContainerColumns.SettingChanged += OnFeedBarrelConfigChanged;
        FeedBarrelContainerRows.SettingChanged += OnFeedBarrelConfigChanged;
        DefaultDragonRestrictedBiomes.SettingChanged += OnDragonFlightConfigChanged;
    }

    private static void UnsubscribeRuntimeConfigChanges()
    {
        TamingOrbRecipe.SettingChanged -= OnTamingOrbConfigChanged;
        TamingOrbCraftingStation.SettingChanged -= OnTamingOrbConfigChanged;
        FeedBarrelRecipe.SettingChanged -= OnFeedBarrelConfigChanged;
        FeedBarrelMaterialStyle.SettingChanged -= OnFeedBarrelConfigChanged;
        FeedBarrelContainerColumns.SettingChanged -= OnFeedBarrelConfigChanged;
        FeedBarrelContainerRows.SettingChanged -= OnFeedBarrelConfigChanged;
        DefaultDragonRestrictedBiomes.SettingChanged -= OnDragonFlightConfigChanged;
    }

    private ConfigEntry<T> config<T>(string group, string name, T value, string description, bool synchronizedSetting = true, int? order = null)
    {
        return config(group, name, value, new ConfigDescription(description), synchronizedSetting, order);
    }

    private ConfigEntry<T> config<T>(string group, string name, T value, ConfigDescription description, bool synchronizedSetting = true, int? order = null)
    {
        object[] tags = description.Tags ?? Array.Empty<object>();
        if (order.HasValue)
        {
            object[] orderedTags = new object[tags.Length + 1];
            Array.Copy(tags, orderedTags, tags.Length);
            orderedTags[orderedTags.Length - 1] = new ConfigurationManagerAttributes { Order = order.Value };
            tags = orderedTags;
        }

        ConfigDescription syncedDescription = new(
            description.Description + (synchronizedSetting ? " [Synced with Server]" : " [Not Synced with Server]"),
            description.AcceptableValues,
            tags);

        ConfigEntry<T> entry = Config.Bind(group, name, value, syncedDescription);
        SyncedConfigEntry<T> syncedEntry = ConfigSync.AddConfigEntry(entry);
        syncedEntry.SynchronizedConfig = synchronizedSetting;
        return entry;
    }

    private sealed class ConfigurationManagerAttributes
    {
        [UsedImplicitly] public int? Order = null;
        [UsedImplicitly] public bool? Browsable = null;
        [UsedImplicitly] public string? Category = null;
        [UsedImplicitly] public Action<ConfigEntryBase>? CustomDrawer = null;
    }
}

public static class ToggleExtensions
{
    public static bool IsOn(this FeedLikeGrandmaPlugin.Toggle value)
    {
        return value == FeedLikeGrandmaPlugin.Toggle.On;
    }
}
