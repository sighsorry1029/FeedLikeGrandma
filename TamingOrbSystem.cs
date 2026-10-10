using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using UnityEngine;

namespace FeedLikeGrandma;

internal static partial class TamingOrbSystem
{
    internal const string PrefabName = "FLG_TamingOrb";

    private const string RecipeName = "Recipe_FLG_TamingOrb";
    private const string PayloadKey = "flg.tame.payload";
    private const string CreatureManagerStateKey = "flg.tame.creaturemanager";
    private const int PayloadVersion = 4;
    private const int LegacyPayloadVersion = 3;
    private const string DefaultSourcePrefabName = "DragonTear";
    private const string CllcEffectKey = "CL&LC effect";
    private const string OdinHorseHaveArmorKey = "HaveArmor";
    private const string ActionEffectPrefab = "fx_summon_start";
    private const float ActionDelay = 4.5f;
    private const float CaptureRange = 5f;
    private const float ReleaseAimRange = 5f;
    private const float ReleaseFallbackAimDistance = 1f;
    private const float ReleaseUpOffset = 1f;
    private static readonly Color TamingOrbGreen = new(0.18f, 0.72f, 0.28f, 1f);
    private static readonly HashSet<ItemDrop.ItemData.SharedData> NonTamingOrbSharedData = new();
    private static readonly int OdinHorseHaveArmorHash = OdinHorseHaveArmorKey.GetStableHashCode();
    private static readonly int OdinHorseSetArmorRpcHash = "SetArmor".GetStableHashCode();
    private static readonly int ColorProperty = Shader.PropertyToID("_Color");
    private static readonly int TintColorProperty = Shader.PropertyToID("_TintColor");
    private static readonly int EmissionColorProperty = Shader.PropertyToID("_EmissionColor");
    private static readonly int ModeProperty = Shader.PropertyToID("_Mode");
    private static readonly int SrcBlendProperty = Shader.PropertyToID("_SrcBlend");
    private static readonly int DstBlendProperty = Shader.PropertyToID("_DstBlend");
    private static readonly int ZWriteProperty = Shader.PropertyToID("_ZWrite");

    private static readonly int ReleaseRaycastMask = LayerMask.GetMask(
        "Default",
        "static_solid",
        "Default_small",
        "piece",
        "piece_nonsolid",
        "terrain",
        "character",
        "character_net",
        "character_ghost",
        "hitbox",
        "character_noenv",
        "vehicle");

    private static GameObject? _prefab;
    private static GameObject? _prefabContainer;
    private static ItemDrop.ItemData.SharedData? _tamingOrbSharedData;

    internal static void RegisterNetworkRpcs()
    {
        RegisterOrbNetworkRpcsCore();
    }

    internal static void Shutdown()
    {
        ShutdownOrbTransactions();
        ShutdownIconPipeline();
        NonTamingOrbSharedData.Clear();
        _tamingOrbSharedData = null;
    }

    internal static void OnGameDataChanged()
    {
        ResetIconFailures();
    }

    internal static void RegisterPrefabsAndRecipes(ObjectDB? objectDb = null)
    {
        objectDb ??= ObjectDB.instance;
        if ((object?)objectDb == null)
        {
            return;
        }

        objectDb.m_recipes.RemoveAll(recipe => recipe != null && recipe.name == RecipeName);
        // Existing orbs must remain loadable even when crafting is disabled.
        GameObject? prefab = RegisterPersistentPrefabCore(objectDb);
        if ((object?)prefab == null)
        {
            return;
        }

        if (IsEnabled())
        {
            RegisterRecipe(objectDb, prefab);
        }
    }

    internal static void RegisterPersistentPrefab(ObjectDB? objectDb = null)
    {
        objectDb ??= ObjectDB.instance;
        if ((object?)objectDb != null)
        {
            RegisterPersistentPrefabCore(objectDb);
        }
    }

    private static GameObject? RegisterPersistentPrefabCore(ObjectDB objectDb)
    {
        GameObject? prefab = EnsurePrefab(objectDb);
        if ((object?)prefab == null)
        {
            return null;
        }

        RegisterWithObjectDb(objectDb, prefab);
        if ((object?)ZNetScene.instance != null)
        {
            RegisterWithZNetScene(ZNetScene.instance, prefab);
        }

        return prefab;
    }

    internal static bool TryHandleTameableUseItem(Tameable tameable, Humanoid user, ItemDrop.ItemData item)
    {
        if (!IsTamingOrb(item))
        {
            return false;
        }

        if (user is not Player player)
        {
            return true;
        }

        if (TryHandlePendingOrb(player, item)) return true;

        if (!IsEnabled())
        {
            FeedLocalization.Message(player, "$flg_taming_orb_disabled");
            return true;
        }

        if (item.m_customData.ContainsKey(PayloadKey))
        {
            TryRelease(player, item);
            return true;
        }

        Character? character = ((Component)tameable).GetComponentInParent<Character>();
        if (character == null)
        {
            FeedLocalization.Message(player, "$flg_taming_orb_cannot_capture");
            return true;
        }

        TryCapture(player, character, item);
        return true;
    }

    internal static bool TryHandlePlayerUseItem(Player player, ItemDrop.ItemData item)
    {
        if (player == null || !IsTamingOrb(item))
        {
            return false;
        }

        if (TryHandlePendingOrb(player, item)) return true;

        if (!IsEnabled())
        {
            FeedLocalization.Message(player, "$flg_taming_orb_disabled");
            return true;
        }

        if (item.m_customData.ContainsKey(PayloadKey))
        {
            TryRelease(player, item);
            return true;
        }

        GameObject? hoverObject = player.GetHoverObject();
        Character? character = hoverObject == null ? null : hoverObject.GetComponentInParent<Character>();
        if (character == null)
        {
            FeedLocalization.Message(player, "$flg_taming_orb_use_on_tamed");
            return true;
        }

        TryCapture(player, character, item);
        return true;
    }

    internal static void AppendTooltip(ItemDrop.ItemData item, bool crafting, ref string tooltip)
    {
        if (crafting || !IsTamingOrb(item))
        {
            return;
        }

        if (HasPendingOrbItem(item))
            tooltip += "\n\n" + FeedLocalization.Text("$flg_taming_orb_recovery_hint");

        if (TryReadPayload(item, out CapturedTamePayload? payload))
        {
            string creatureName = GetPayloadDisplayName(payload!);
            tooltip +=
                "\n\n" + FeedLocalization.Format("$flg_taming_orb_tooltip_captured", creatureName) +
                "\n" + FeedLocalization.Format("$flg_taming_orb_tooltip_level", payload!.Level.ToString(CultureInfo.InvariantCulture)) +
                "\n" + FeedLocalization.Format("$flg_taming_orb_tooltip_hp", payload.Health.ToString("0.#", CultureInfo.InvariantCulture), payload.MaxHealth.ToString("0.#", CultureInfo.InvariantCulture)) +
                "\n" + FeedLocalization.Text("$flg_taming_orb_tooltip_release");
            return;
        }

        if (item.m_customData.ContainsKey(PayloadKey))
        {
            tooltip += "\n\n" + FeedLocalization.Text("$flg_taming_orb_tooltip_invalid");
            return;
        }

        tooltip += "\n\n" + FeedLocalization.Text("$flg_taming_orb_tooltip_empty") + "\n" + FeedLocalization.Text("$flg_taming_orb_tooltip_capture");
    }

    internal static string AugmentDroppedItemHoverText(ItemDrop itemDrop, string originalText)
    {
        if ((object?)itemDrop == null || !IsTamingOrb(itemDrop.m_itemData))
        {
            return originalText;
        }

        if (!TryReadPayload(itemDrop.m_itemData, out CapturedTamePayload? payload))
        {
            return originalText;
        }

        return originalText + $" ({GetPayloadDisplayName(payload!)})";
    }

    private static bool TryCapture(Player player, Character character, ItemDrop.ItemData item)
    {
        if (HasPendingOrbItem(item))
        {
            FeedLocalization.Message(player, "$flg_taming_orb_pending");
            return true;
        }

        ZNetView? nview = ((Component)character).GetComponent<ZNetView>();
        CaptureEligibility eligibility = GetCaptureEligibility(character, nview);
        if (eligibility != CaptureEligibility.Allowed)
        {
            string message = eligibility switch
            {
                CaptureEligibility.Dead => "$flg_taming_orb_dead",
                CaptureEligibility.PlayerSpawned => "$flg_taming_orb_player_spawned",
                CaptureEligibility.NotTamed => "$flg_taming_orb_only_tamed",
                _ => "$flg_taming_orb_cannot_capture"
            };
            FeedLocalization.Message(player, message);
            return false;
        }

        float distance = Vector3.Distance(player.transform.position, ((Component)character).transform.position);
        if (distance > CaptureRange)
        {
            FeedLocalization.Message(player, "$flg_taming_orb_too_far");
            return false;
        }

        if (!TryGetPrefabName(((Component)character).gameObject, out string creaturePrefabName))
        {
            FeedLocalization.Message(player, "$flg_taming_orb_prefab_missing");
            return false;
        }

        PlayActionEffect(character.GetCenterPoint(), ((Component)character).transform.rotation);
        FeedLocalization.Message(player, "$flg_taming_orb_capturing", character.GetHoverName());
        if (BeginNetworkCapture(player, item, nview.GetZDO().m_uid, creaturePrefabName, ActionDelay))
        {
            return true;
        }
        return false;
    }

    private static CaptureEligibility GetCaptureEligibility(Character? character, ZNetView? nview)
    {
        if ((object?)character == null || (object?)nview == null || !nview.IsValid())
        {
            return CaptureEligibility.Invalid;
        }

        if (character.IsDead())
        {
            return CaptureEligibility.Dead;
        }

        if (character.m_faction == Character.Faction.PlayerSpawned)
        {
            return CaptureEligibility.PlayerSpawned;
        }

        return character.IsTamed() ? CaptureEligibility.Allowed : CaptureEligibility.NotTamed;
    }

    private static bool TryRelease(Player player, ItemDrop.ItemData item)
    {
        if (!TryReadPayload(item, out CapturedTamePayload? payload))
        {
            FeedLocalization.Message(player, "$flg_taming_orb_data_invalid");
            return false;
        }

        if (HasPendingOrbItem(item))
        {
            FeedLocalization.Message(player, "$flg_taming_orb_pending");
            return true;
        }

        if ((object?)ZNetScene.instance == null)
        {
            FeedLocalization.Message(player, "$flg_taming_orb_prefab_database_not_ready");
            return false;
        }

        GameObject? creaturePrefab = ZNetScene.instance.GetPrefab(payload!.Prefab);
        if ((object?)creaturePrefab == null)
        {
            FeedLocalization.Message(player, "$flg_taming_orb_prefab_named_missing", payload.Prefab);
            return false;
        }

        GetReleaseTransform(player, out Vector3 spawnPosition, out Quaternion spawnRotation);
        string creatureManagerState = item.m_customData.TryGetValue(
            CreatureManagerStateKey,
            out string? storedCreatureManagerState)
            ? storedCreatureManagerState
            : string.Empty;
        PlayActionEffect(spawnPosition, spawnRotation);
        FeedLocalization.Message(player, "$flg_taming_orb_releasing", GetPayloadDisplayName(payload));
        if (BeginNetworkRelease(
            player,
            item,
            payload,
            creatureManagerState,
            creaturePrefab,
            spawnPosition,
            spawnRotation,
            ActionDelay))
        {
            return true;
        }
        return false;
    }

    private static bool IsEnabled()
    {
        return !string.IsNullOrWhiteSpace(FeedLikeGrandmaPlugin.TamingOrbRecipe.Value);
    }

    private static void PlayActionEffect(Vector3 position, Quaternion rotation)
    {
        GameObject? effectPrefab = ZNetScene.instance == null ? null : ZNetScene.instance.GetPrefab(ActionEffectPrefab);
        if ((object?)effectPrefab == null)
        {
            FeedLikeGrandmaPlugin.Log.LogWarning($"{PrefabName} effect prefab '{ActionEffectPrefab}' was not found.");
            return;
        }

        UnityEngine.Object.Instantiate(effectPrefab, position, rotation);
    }

    private static CapturedTamePayload CapturePayload(Character character, Tameable? tameable, ZNetView nview, string creaturePrefabName)
    {
        ZDO zdo = nview.GetZDO();
        long nowTicks = ZNet.instance.GetTime().Ticks;
        long lastFedTicks = zdo.GetLong(ZDOVars.s_tameLastFeeding, 0L);
        long spawnTicks = zdo.GetLong(ZDOVars.s_spawnTime, 0L);
        FeedCreatureModifierCompatibility.CaptureCllcModifiers(character, out string cllcInfusion, out string cllcExtraEffect);

        return new CapturedTamePayload
        {
            Prefab = creaturePrefabName,
            Name = zdo.GetString(ZDOVars.s_tamedName, string.Empty),
            Level = Mathf.Max(1, character.GetLevel()),
            Health = Mathf.Max(1f, character.GetHealth()),
            MaxHealth = Mathf.Max(1f, character.GetMaxHealth()),
            HasSaddle = tameable != null && tameable.HaveSaddle(),
            Inventory = CaptureInventoryData(zdo),
            LastFedAgeTicks = lastFedTicks > 0L ? Math.Max(0L, nowTicks - lastFedTicks) : 0L,
            SpawnAgeTicks = spawnTicks > 0L ? Math.Max(0L, nowTicks - spawnTicks) : 0L,
            StarProgress = Mathf.Max(0f, zdo.GetFloat(FeedSystem.StarProgressHash, 0f)),
            GrowthElapsed = zdo.GetFloat(FeedSystem.GrowthElapsedHash, -1f),
            CllcEffect = zdo.GetInt(CllcEffectKey, 0),
            CllcInfusion = cllcInfusion,
            CllcExtraEffect = cllcExtraEffect,
            CuisineState = FeedCuisineCompatibility.CaptureState(character, nview),
            OdinHorseHasArmor = LooksLikeOdinHorse(tameable) && zdo.GetBool(OdinHorseHaveArmorHash, false),
            SaddleStamina = TryGetSaddle(character, tameable, out Sadle? saddle)
                ? Mathf.Clamp(saddle.GetStamina(), 0f, saddle.GetMaxStamina())
                : -1f
        };
    }

    private static string CaptureInventoryData(ZDO zdo)
    {
        byte[]? inventory = zdo.GetByteArray(ZDOVars.s_items);
        if (inventory == null && !string.IsNullOrEmpty(zdo.GetString(ZDOVars.s_items, string.Empty)))
        {
            // Do not discard inventory written by an unported mod or migrate its format here.
            throw new InvalidOperationException("Creature inventory is not stored in the Valheim 1.0 byte-array format.");
        }

        return inventory == null ? string.Empty : Convert.ToBase64String(inventory);
    }

    private static void RestorePayload(CapturedTamePayload payload, byte[] inventory, Character character, Tameable? tameable, ZNetView nview)
    {
        ZDO zdo = nview.GetZDO();
        long nowTicks = ZNet.instance.GetTime().Ticks;

        if (payload.CllcEffect != 0)
        {
            zdo.Set(CllcEffectKey, payload.CllcEffect);
        }

        if (inventory.Length > 0)
        {
            zdo.Set(ZDOVars.s_items, inventory);
        }

        if (payload.LastFedAgeTicks > 0L)
        {
            zdo.Set(ZDOVars.s_tameLastFeeding, nowTicks - payload.LastFedAgeTicks);
        }

        if (payload.SpawnAgeTicks > 0L)
        {
            zdo.Set(ZDOVars.s_spawnTime, nowTicks - payload.SpawnAgeTicks);
        }

        if (payload.StarProgress > 0f)
        {
            zdo.Set(FeedSystem.StarProgressHash, payload.StarProgress);
        }

        if (payload.GrowthElapsed >= 0f)
        {
            zdo.Set(FeedSystem.GrowthElapsedHash, payload.GrowthElapsed);
            zdo.Set(FeedSystem.GrowthLastEvalHash, nowTicks);
        }

        // Restore before publishing the creature as tamed/release-ready so its first
        // interaction cannot observe a freshly reset milking cooldown.
        FeedCuisineCompatibility.RestoreState(payload.CuisineState, character, nview);
        character.SetTamed(true);
        character.SetLevel(Mathf.Max(1, payload.Level));
        character.SetMaxHealth(Mathf.Max(1f, payload.MaxHealth));
        character.SetHealth(Mathf.Clamp(payload.Health, 1f, Mathf.Max(1f, payload.MaxHealth)));
        FeedCreatureModifierCompatibility.RestoreCllcModifiers(character, payload.CllcInfusion, payload.CllcExtraEffect);

        if (tameable != null)
        {
            if (!string.IsNullOrWhiteSpace(payload.Name))
            {
                tameable.RPC_SetName(0L, payload.Name, string.Empty);
            }

            if (payload.HasSaddle)
            {
                zdo.Set(ZDOVars.s_haveSaddleHash, true);
                tameable.SetSaddle(true);
            }

            RestoreOdinHorseArmor(payload, tameable, nview, zdo);
        }

        if (payload.SaddleStamina >= 0f && TryGetSaddle(character, tameable, out Sadle? saddle))
        {
            zdo.Set(ZDOVars.s_stamina, Mathf.Clamp(payload.SaddleStamina, 0f, saddle.GetMaxStamina()));
        }
    }

    private static void GetReleaseTransform(Player player, out Vector3 position, out Quaternion rotation)
    {
        Camera? camera = (object?)GameCamera.instance != null ? GameCamera.instance.GameCameraComponent() : null;
        Transform aimTransform = (object?)camera != null ? camera.transform : player.transform;
        Vector3 aimForward = aimTransform.forward;
        Vector3 rayOrigin = player.GetEyePoint();
        if (Physics.Raycast(rayOrigin, aimForward, out RaycastHit hitInfo, ReleaseAimRange, ReleaseRaycastMask))
        {
            position = hitInfo.point + Vector3.up * ReleaseUpOffset;
        }
        else
        {
            position = player.transform.position + aimForward * ReleaseFallbackAimDistance + Vector3.up * ReleaseUpOffset;
        }

        rotation = Quaternion.identity;
    }

    private static void DestroySpawnedRelease(GameObject spawned)
    {
        ZNetView? nview = spawned.GetComponent<ZNetView>();
        if (nview != null && nview.IsValid())
        {
            nview.ClaimOwnership();
            nview.Destroy();
            return;
        }

        UnityEngine.Object.Destroy(spawned);
    }

    private static bool TryGetSaddle(Character character, Tameable? tameable, out Sadle saddle)
    {
        saddle = null!;
        if (tameable != null && tameable.m_saddle != null)
        {
            saddle = tameable.m_saddle;
            return true;
        }

        Sadle? componentSaddle = ((Component)character).GetComponentInChildren<Sadle>();
        if ((object?)componentSaddle == null)
        {
            return false;
        }

        saddle = componentSaddle;
        return true;
    }

    private static void RestoreOdinHorseArmor(CapturedTamePayload payload, Tameable tameable, ZNetView nview, ZDO zdo)
    {
        if (!payload.OdinHorseHasArmor ||
            tameable == null ||
            nview == null ||
            !nview.IsValid() ||
            !LooksLikeOdinHorse(tameable))
        {
            return;
        }

        zdo.Set(OdinHorseHaveArmorHash, true);
        if (nview.RpcHandlers() != null && nview.RpcHandlers().Contains(OdinHorseSetArmorRpcHash))
        {
            try
            {
                nview.InvokeRPC(ZNetView.Everybody, "SetArmor", true);
            }
            catch (Exception ex)
            {
                FeedLikeGrandmaPlugin.Log.LogDebug($"Could not invoke OdinHorse SetArmor RPC for {tameable.name}: {ex.Message}");
            }
        }

        TrySetOdinHorseArmorObjects(tameable, true);
    }

    private static bool LooksLikeOdinHorse(Tameable? tameable)
    {
        if (tameable == null)
        {
            return false;
        }

        string rootName = ((Component)tameable).transform.root.gameObject.name;
        return rootName.IndexOf("rae_OdinHorse", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static bool TrySetOdinHorseArmorObjects(Tameable tameable, bool enabled)
    {
        try
        {
            bool maskSet = TrySetChildActive(((Component)tameable).transform, "Horse_mask", enabled);
            bool armorSet = TrySetChildActive(((Component)tameable).transform, "horse_armor", enabled);
            return maskSet || armorSet;
        }
        catch (Exception ex)
        {
            FeedLikeGrandmaPlugin.Log.LogDebug($"Could not set OdinHorse armor visuals for {tameable.name}: {ex.Message}");
            return false;
        }
    }

    private static bool TrySetChildActive(Transform root, string childName, bool enabled)
    {
        Transform? child = FindChildRecursive(root, childName);
        if ((object?)child == null)
        {
            return false;
        }

        child.gameObject.SetActive(enabled);
        return true;
    }

    private static Transform? FindChildRecursive(Transform root, string childName)
    {
        foreach (Transform child in root)
        {
            if (child.name == childName)
            {
                return child;
            }

            Transform? nested = FindChildRecursive(child, childName);
            if ((object?)nested != null)
            {
                return nested;
            }
        }

        return null;
    }

    private static bool TryReadPayload(ItemDrop.ItemData item, out CapturedTamePayload? payload, bool logWarnings = true)
    {
        payload = null;
        if ((object?)item == null || !item.m_customData.TryGetValue(PayloadKey, out string payloadText) || string.IsNullOrWhiteSpace(payloadText))
        {
            return false;
        }

        return CapturedTamePayload.TryDeserialize(payloadText, out payload, logWarnings);
    }

    private static string GetPayloadDisplayName(CapturedTamePayload payload)
    {
        if (!string.IsNullOrWhiteSpace(payload.Name))
        {
            return payload.Name;
        }

        GameObject? prefab = ZNetScene.instance == null ? null : ZNetScene.instance.GetPrefab(payload.Prefab);
        Character? character = prefab == null ? null : prefab.GetComponent<Character>();
        if (character != null && Localization.instance != null)
        {
            return Localization.instance.Localize(character.m_name);
        }

        return payload.Prefab;
    }

    private static GameObject? EnsurePrefab(ObjectDB objectDb)
    {
        if ((object?)_prefab != null)
        {
            return _prefab;
        }

        GameObject? source = FindSpawnableItemPrefab(objectDb, DefaultSourcePrefabName);
        if ((object?)source == null)
        {
            FeedLikeGrandmaPlugin.Log.LogDebug($"Could not create {PrefabName} yet: source item '{DefaultSourcePrefabName}' was not found.");
            return null;
        }

        if (!HasSpawnableItemShape(source))
        {
            FeedLikeGrandmaPlugin.Log.LogDebug($"Could not create {PrefabName} yet: source item '{DefaultSourcePrefabName}' is not a complete spawnable item prefab.");
            return null;
        }

        GameObject prefab = UnityEngine.Object.Instantiate(source, EnsurePrefabContainer().transform);
        prefab.name = PrefabName;

        ItemDrop? itemDrop = prefab.GetComponent<ItemDrop>();
        if ((object?)itemDrop == null)
        {
            FeedLikeGrandmaPlugin.Log.LogWarning($"Could not create {PrefabName}: source item has no ItemDrop component.");
            UnityEngine.Object.Destroy(prefab);
            return null;
        }

        ConfigureItemData(itemDrop);
        ConfigureVisual(prefab, itemDrop);
        _prefab = prefab;
        return _prefab;
    }

    private static GameObject EnsurePrefabContainer()
    {
        if ((object?)_prefabContainer != null)
        {
            return _prefabContainer;
        }

        _prefabContainer = new GameObject("FeedLikeGrandma Prefabs");
        _prefabContainer.SetActive(false);
        UnityEngine.Object.DontDestroyOnLoad(_prefabContainer);
        return _prefabContainer;
    }

    private static void ConfigureItemData(ItemDrop itemDrop)
    {
        ItemDrop.ItemData.SharedData shared = itemDrop.m_itemData.m_shared;
        shared.m_name = FeedLocalization.TamingOrbNameToken;
        shared.m_description = FeedLocalization.TamingOrbDescriptionToken;
        shared.m_maxStackSize = 1;
        shared.m_weight = 1f;
        shared.m_itemType = ItemDrop.ItemData.ItemType.Misc;
        shared.m_attachOverride = ItemDrop.ItemData.ItemType.None;
        shared.m_useDurability = false;
        shared.m_maxDurability = 0f;
        shared.m_canBeReparied = false;
        shared.m_destroyBroken = false;

        Sprite? sourceIcon = shared.m_icons is { Length: > 0 } ? shared.m_icons[0] : null;
        _icon ??= CreateTintedIcon(sourceIcon, TamingOrbGreen);
        if ((object?)_icon != null)
        {
            shared.m_icons = new[] { _icon };
        }

        ResetIconVariants(shared);
        shared.m_variants = 1;
        itemDrop.m_itemData.m_variant = 0;
        _tamingOrbSharedData = shared;
        NonTamingOrbSharedData.Remove(shared);
    }

    private static void ConfigureVisual(GameObject prefab, ItemDrop itemDrop)
    {
        EnsureRootZNetView(prefab);
        TintDragonTearCoreMeshes(prefab, TamingOrbGreen);
        itemDrop.m_itemData.m_dropPrefab = prefab;
        itemDrop.m_itemData.m_variant = 0;
    }

    internal static void NormalizeWorldItemVariant(ItemDrop itemDrop)
    {
        if ((object?)itemDrop == null || !IsTamingOrb(itemDrop.m_itemData))
        {
            return;
        }

        // Creature icon indexes are client-local; persisted indexes can make vanilla reject pickup.
        itemDrop.m_itemData.m_variant = 0;
    }

    internal static bool ShouldSkipInventoryCloneWorldAwake(Component component)
    {
        if (!ZNetView.m_forceDisableInit || (object?)component == null)
        {
            return false;
        }

        GameObject gameObject = component.gameObject;
        if (gameObject.name.StartsWith(PrefabName, StringComparison.Ordinal))
        {
            return true;
        }

        ItemDrop? itemDrop = gameObject.GetComponent<ItemDrop>();
        return (object?)itemDrop != null && IsTamingOrb(itemDrop.m_itemData);
    }

    private static void EnsureRootZNetView(GameObject prefab)
    {
        if ((object?)prefab.GetComponent<ZNetView>() != null)
        {
            return;
        }

        ZNetView zNetView = prefab.AddComponent<ZNetView>();
        zNetView.m_persistent = true;
        FeedLikeGrandmaPlugin.Log.LogDebug($"{PrefabName}: added missing root ZNetView to cloned item prefab.");
    }

    private static void RegisterWithObjectDb(ObjectDB objectDb, GameObject prefab)
    {
        int hash = PrefabName.GetStableHashCode();
        if (objectDb.m_items.All(item => item == null || item.name != PrefabName))
        {
            objectDb.m_items.Add(prefab);
        }

        if (!objectDb.ItemHashes().ContainsKey(hash))
        {
            objectDb.ItemHashes().Add(hash, prefab);
        }
        else if (objectDb.ItemHashes()[hash] == null || objectDb.ItemHashes()[hash].name == PrefabName)
        {
            objectDb.ItemHashes()[hash] = prefab;
        }

        ItemDrop? itemDrop = prefab.GetComponent<ItemDrop>();
        if ((object?)itemDrop != null)
        {
            objectDb.ItemDataPrefabs()[itemDrop.m_itemData.m_shared] = prefab;
        }
    }

    private static void RegisterWithZNetScene(ZNetScene zNetScene, GameObject prefab)
    {
        int hash = PrefabName.GetStableHashCode();
        if (prefab.GetComponent<ZNetView>() != null)
        {
            if (zNetScene.m_prefabs.All(existing => existing == null || existing.name != PrefabName))
            {
                zNetScene.m_prefabs.Add(prefab);
            }
        }
        else if (zNetScene.m_nonNetViewPrefabs.All(existing => existing == null || existing.name != PrefabName))
        {
            zNetScene.m_nonNetViewPrefabs.Add(prefab);
        }

        if (!zNetScene.NamedPrefabs().ContainsKey(hash))
        {
            zNetScene.NamedPrefabs().Add(hash, prefab);
        }
        else if (zNetScene.NamedPrefabs()[hash] == null || zNetScene.NamedPrefabs()[hash].name == PrefabName)
        {
            zNetScene.NamedPrefabs()[hash] = prefab;
        }
    }

    private static void RegisterRecipe(ObjectDB objectDb, GameObject prefab)
    {
        ItemDrop? itemDrop = prefab.GetComponent<ItemDrop>();
        if ((object?)itemDrop == null)
        {
            return;
        }

        objectDb.m_recipes.RemoveAll(recipe => recipe != null && recipe.name == RecipeName);

        Recipe recipe = ScriptableObject.CreateInstance<Recipe>();
        recipe.name = RecipeName;
        recipe.m_enabled = true;
        recipe.m_amount = 1;
        recipe.m_item = itemDrop;
        recipe.m_resources = BuildRequirements(objectDb, FeedLikeGrandmaPlugin.TamingOrbRecipe.Value).ToArray();

        if (TryResolveCraftingStation(FeedLikeGrandmaPlugin.TamingOrbCraftingStation.Value, out CraftingStation? station, out int stationLevel))
        {
            recipe.m_craftingStation = station;
            recipe.m_minStationLevel = stationLevel;
        }

        objectDb.m_recipes.Add(recipe);
    }

    private static IEnumerable<Piece.Requirement> BuildRequirements(ObjectDB objectDb, string recipeText)
    {
        if (string.IsNullOrWhiteSpace(recipeText))
        {
            yield break;
        }

        string[] parts = recipeText.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
        foreach (string rawPart in parts)
        {
            string[] itemAndAmount = rawPart.Trim().Split(new[] { ':' }, StringSplitOptions.RemoveEmptyEntries);
            if (itemAndAmount.Length == 0)
            {
                continue;
            }

            string itemName = itemAndAmount[0].Trim();
            int amount = 1;
            if (itemAndAmount.Length > 1)
            {
                int.TryParse(itemAndAmount[1].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out amount);
                amount = Mathf.Max(1, amount);
            }

            GameObject? itemPrefab = FindItemPrefab(objectDb, itemName);
            ItemDrop? itemDrop = itemPrefab == null ? null : itemPrefab.GetComponent<ItemDrop>();
            if ((object?)itemDrop == null)
            {
                FeedLikeGrandmaPlugin.Log.LogWarning($"{PrefabName} recipe ingredient '{itemName}' was not found.");
                continue;
            }

            yield return new Piece.Requirement
            {
                m_resItem = itemDrop,
                m_amount = amount,
                m_amountPerLevel = 0,
                m_recover = true
            };
        }
    }

    private static bool TryResolveCraftingStation(string stationText, out CraftingStation? station, out int stationLevel)
    {
        station = null;
        stationLevel = 1;

        if (string.IsNullOrWhiteSpace(stationText) || stationText.Equals("None", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        string[] parts = stationText.Split(new[] { ':' }, StringSplitOptions.RemoveEmptyEntries);
        string stationPrefabName = parts[0].Trim();
        if (parts.Length > 1)
        {
            int.TryParse(parts[1].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out stationLevel);
            stationLevel = Mathf.Max(1, stationLevel);
        }

        GameObject? stationPrefab = ZNetScene.instance == null ? null : ZNetScene.instance.GetPrefab(stationPrefabName);
        station = stationPrefab == null ? null : stationPrefab.GetComponent<CraftingStation>();
        if ((object?)station == null)
        {
            if (IsZNetScenePrefabRegistryReady())
            {
                FeedLikeGrandmaPlugin.Log.LogWarning($"{PrefabName} crafting station '{stationPrefabName}' was not found. Recipe will not require a station.");
            }

            return false;
        }

        return true;
    }

    private static bool IsZNetScenePrefabRegistryReady()
    {
        return ZNetScene.instance != null &&
               ZNetScene.instance.m_prefabs != null &&
               ZNetScene.instance.m_prefabs.Count > 0 &&
               ZNetScene.instance.NamedPrefabs() != null &&
               ZNetScene.instance.NamedPrefabs().Count > 0;
    }

    private static bool IsTamingOrb(ItemDrop.ItemData item)
    {
        if ((object?)item == null || (object?)item.m_shared == null)
        {
            return false;
        }

        if ((object?)item.m_dropPrefab != null && item.m_dropPrefab.name == PrefabName)
        {
            _tamingOrbSharedData ??= item.m_shared;
            NonTamingOrbSharedData.Remove(item.m_shared);
            return true;
        }

        if (ReferenceEquals(item.m_shared, _tamingOrbSharedData) ||
            item.m_shared.m_name == FeedLocalization.TamingOrbNameToken)
        {
            _tamingOrbSharedData ??= item.m_shared;
            NonTamingOrbSharedData.Remove(item.m_shared);
            return true;
        }

        if (NonTamingOrbSharedData.Contains(item.m_shared))
        {
            return false;
        }

        if ((object?)ObjectDB.instance != null && ObjectDB.instance.TryGetItemPrefab(item.m_shared, out GameObject prefab))
        {
            if ((object?)prefab != null && prefab.name == PrefabName)
            {
                _tamingOrbSharedData = item.m_shared;
                NonTamingOrbSharedData.Remove(item.m_shared);
                return true;
            }

            NonTamingOrbSharedData.Add(item.m_shared);
            return false;
        }

        return false;
    }

    private static GameObject? FindSpawnableItemPrefab(ObjectDB objectDb, string prefabName)
    {
        if ((object?)ZNetScene.instance != null &&
            ZNetScene.instance.NamedPrefabs().TryGetValue(prefabName.GetStableHashCode(), out GameObject scenePrefab) &&
            (object?)scenePrefab != null &&
            HasSpawnableItemShape(scenePrefab))
        {
            return scenePrefab;
        }

        GameObject? itemPrefab = FindItemPrefab(objectDb, prefabName);
        if ((object?)itemPrefab != null && HasSpawnableItemShape(itemPrefab))
        {
            return itemPrefab;
        }

        return null;
    }

    private static GameObject? FindItemPrefab(ObjectDB? objectDb, string prefabName)
    {
        if ((object?)objectDb != null)
        {
            int hash = prefabName.GetStableHashCode();
            if (objectDb.ItemHashes().TryGetValue(hash, out GameObject byHash) && (object?)byHash != null)
            {
                return byHash;
            }

            GameObject? byList = objectDb.m_items.FirstOrDefault(item => (object?)item != null && item.name == prefabName);
            if ((object?)byList != null)
            {
                return byList;
            }

            GameObject? byApi = objectDb.GetItemPrefab(prefabName);
            if ((object?)byApi != null)
            {
                return byApi;
            }
        }

        if ((object?)ZNetScene.instance != null &&
            ZNetScene.instance.NamedPrefabs().TryGetValue(prefabName.GetStableHashCode(), out GameObject scenePrefab) &&
            (object?)scenePrefab != null)
        {
            return scenePrefab;
        }

        return null;
    }

    private static bool HasSpawnableItemShape(GameObject prefab)
    {
        if ((object?)prefab == null)
        {
            return false;
        }

        return (object?)prefab.GetComponent<ItemDrop>() != null &&
               (object?)prefab.GetComponent<ZNetView>() != null &&
               (object?)prefab.GetComponent<ZSyncTransform>() != null &&
               (object?)prefab.GetComponent<Rigidbody>() != null &&
               (object?)prefab.GetComponent<Floating>() != null &&
               (object?)prefab.GetComponentInChildren<Collider>(true) != null;
    }

    private static Material? CreateSafeVisualMaterial(Material? preferred)
    {
        try
        {
            if ((object?)preferred != null)
            {
                return new Material(preferred);
            }
        }
        catch (Exception ex)
        {
            FeedLikeGrandmaPlugin.Log.LogDebug($"Could not clone {PrefabName} material: {ex.Message}");
        }

        return null;
    }

    private static void TintDragonTearCoreMeshes(GameObject prefab, Color color)
    {
        bool tinted = false;
        tinted |= TintRendererAtPath(prefab, "attach/model/inner", color, forceOpaque: true);
        tinted |= TintRendererAtPath(prefab, "attach/model/hull", color, forceOpaque: false);

        if (!tinted)
        {
            FeedLikeGrandmaPlugin.Log.LogDebug($"{PrefabName}: DragonTear core mesh renderers were not found.");
        }
    }

    private static bool TintRendererAtPath(GameObject prefab, string path, Color color, bool forceOpaque)
    {
        Transform? target = prefab.transform.Find(path);
        Renderer? renderer = target == null ? null : target.GetComponent<Renderer>();
        if ((object?)renderer == null)
        {
            return false;
        }

        Material[] sharedMaterials = renderer.sharedMaterials;
        for (int i = 0; i < sharedMaterials.Length; i++)
        {
            Material? material = CreateSafeVisualMaterial(sharedMaterials[i]);
            if ((object?)material == null)
            {
                continue;
            }

            TintMaterial(material, color, forceOpaque);
            sharedMaterials[i] = material;
        }

        renderer.sharedMaterials = sharedMaterials;
        renderer.enabled = true;
        return true;
    }

    private static void TintMaterial(Material material, Color color, bool forceOpaque)
    {
        Color baseColor = material.HasProperty(ColorProperty) ? material.GetColor(ColorProperty) : Color.white;
        Color tinted = new(color.r, color.g, color.b, forceOpaque ? 1f : baseColor.a);

        if (material.HasProperty(ColorProperty))
        {
            material.SetColor(ColorProperty, tinted);
        }

        if (material.HasProperty(TintColorProperty))
        {
            material.SetColor(TintColorProperty, tinted);
        }

        if (material.HasProperty(EmissionColorProperty))
        {
            material.EnableKeyword("_EMISSION");
            material.SetColor(EmissionColorProperty, new Color(color.r, color.g, color.b, 1f) * 0.75f);
        }

        if (!forceOpaque)
        {
            return;
        }

        if (material.HasProperty(ModeProperty))
        {
            material.SetFloat(ModeProperty, 0f);
        }

        if (material.HasProperty(SrcBlendProperty))
        {
            material.SetFloat(SrcBlendProperty, (float)UnityEngine.Rendering.BlendMode.One);
        }

        if (material.HasProperty(DstBlendProperty))
        {
            material.SetFloat(DstBlendProperty, (float)UnityEngine.Rendering.BlendMode.Zero);
        }

        if (material.HasProperty(ZWriteProperty))
        {
            material.SetFloat(ZWriteProperty, 1f);
        }

        material.DisableKeyword("_ALPHABLEND_ON");
        material.DisableKeyword("_ALPHAPREMULTIPLY_ON");
        material.renderQueue = -1;
    }

    private static bool TryGetPrefabName(GameObject gameObject, out string prefabName)
    {
        prefabName = string.Empty;
        if ((object?)gameObject == null)
        {
            return false;
        }

        prefabName = Utils.GetPrefabName(gameObject);

        return !string.IsNullOrWhiteSpace(prefabName) && ZNetScene.instance.GetPrefab(prefabName) != null;
    }

    private sealed class CapturedTamePayload
    {
        // Keep a loaded v3 payload's version: pending transactions compare its exact
        // serialized value, and reading an old Orb must not silently rewrite it.
        public int SerializationVersion { get; init; } = PayloadVersion;
        public string Prefab { get; init; } = string.Empty;
        public string Name { get; init; } = string.Empty;
        public int Level { get; init; }
        public float Health { get; init; }
        public float MaxHealth { get; init; }
        public bool HasSaddle { get; init; }
        public string Inventory { get; init; } = string.Empty;

        // Both envelopes store base64; only the game's ZDO storage type changed.
        public byte[] DecodeInventory() => string.IsNullOrEmpty(Inventory)
            ? Array.Empty<byte>()
            : Convert.FromBase64String(Inventory);
        public long LastFedAgeTicks { get; init; }
        public long SpawnAgeTicks { get; init; }
        public float StarProgress { get; init; }
        public float GrowthElapsed { get; init; }
        public int CllcEffect { get; init; }
        public string CllcInfusion { get; init; } = string.Empty;
        public string CllcExtraEffect { get; init; } = string.Empty;
        public bool OdinHorseHasArmor { get; init; }
        public float SaddleStamina { get; init; }
        public string CuisineState { get; init; } = string.Empty;

        public string Serialize()
        {
            if (SerializationVersion != LegacyPayloadVersion && SerializationVersion != PayloadVersion)
            {
                throw new InvalidOperationException("Unsupported Taming Orb payload version.");
            }

            if (SerializationVersion == PayloadVersion && !FeedCuisineCompatibility.IsValidState(CuisineState))
            {
                throw new InvalidOperationException("Invalid ValheimCuisine state in Taming Orb payload.");
            }

            ZPackage pkg = new();
            pkg.Write(SerializationVersion);
            pkg.Write(Prefab ?? string.Empty);
            pkg.Write(Name ?? string.Empty);
            pkg.Write(Level);
            pkg.Write(Health);
            pkg.Write(MaxHealth);
            pkg.Write(HasSaddle);
            pkg.Write(Inventory ?? string.Empty);
            pkg.Write(LastFedAgeTicks);
            pkg.Write(SpawnAgeTicks);
            pkg.Write(StarProgress);
            pkg.Write(GrowthElapsed);
            pkg.Write(CllcEffect);
            pkg.Write(SaddleStamina);
            pkg.Write(CllcInfusion ?? string.Empty);
            pkg.Write(CllcExtraEffect ?? string.Empty);
            pkg.Write(OdinHorseHasArmor);
            if (SerializationVersion == PayloadVersion)
            {
                pkg.Write(CuisineState);
            }
            return Convert.ToBase64String(pkg.GetArray());
        }

        public static bool TryDeserialize(string text, out CapturedTamePayload? payload, bool logWarnings = true)
        {
            payload = null;
            try
            {
                ZPackage pkg = new(Convert.FromBase64String(text));
                int version = pkg.ReadInt();
                if (version != LegacyPayloadVersion && version != PayloadVersion)
                {
                    return false;
                }

                string prefab = pkg.ReadString();
                string name = pkg.ReadString();
                int level = pkg.ReadInt();
                float health = pkg.ReadSingle();
                float maxHealth = pkg.ReadSingle();
                bool hasSaddle = pkg.ReadBool();
                string inventory = pkg.ReadString();
                long lastFedAgeTicks = pkg.ReadLong();
                long spawnAgeTicks = pkg.ReadLong();
                float starProgress = pkg.ReadSingle();
                float growthElapsed = pkg.ReadSingle();
                int cllcEffect = pkg.ReadInt();
                float saddleStamina = pkg.ReadSingle();
                string cllcInfusion = pkg.ReadString();
                string cllcExtraEffect = pkg.ReadString();
                bool odinHorseHasArmor = pkg.ReadBool();
                string cuisineState = version == PayloadVersion ? pkg.ReadString() : string.Empty;
                if (version == PayloadVersion &&
                    (pkg.GetPos() != pkg.Size() || !FeedCuisineCompatibility.IsValidState(cuisineState)))
                {
                    return false;
                }

                payload = new CapturedTamePayload
                {
                    SerializationVersion = version,
                    Prefab = prefab,
                    Name = name,
                    Level = level,
                    Health = health,
                    MaxHealth = maxHealth,
                    HasSaddle = hasSaddle,
                    Inventory = inventory,
                    LastFedAgeTicks = lastFedAgeTicks,
                    SpawnAgeTicks = spawnAgeTicks,
                    StarProgress = starProgress,
                    GrowthElapsed = growthElapsed,
                    CllcEffect = cllcEffect,
                    CllcInfusion = cllcInfusion,
                    CllcExtraEffect = cllcExtraEffect,
                    CuisineState = cuisineState,
                    OdinHorseHasArmor = odinHorseHasArmor,
                    SaddleStamina = saddleStamina
                };

                return !string.IsNullOrWhiteSpace(payload.Prefab);
            }
            catch (Exception ex)
            {
                if (logWarnings)
                {
                    FeedLikeGrandmaPlugin.Log.LogWarning($"Failed to read {PrefabName} payload: {ex.Message}");
                }

                return false;
            }
        }
    }

    private enum CaptureEligibility
    {
        Allowed,
        Invalid,
        Dead,
        PlayerSpawned,
        NotTamed
    }

}
