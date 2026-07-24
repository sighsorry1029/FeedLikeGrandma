using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using UnityEngine;

namespace FeedLikeGrandma;

internal static class TamingOrbSystem
{
    internal const string PrefabName = "FLG_TamingOrb";

    private const string RecipeName = "Recipe_FLG_TamingOrb";
    private const string PayloadKey = "flg.tame.payload";
    private const int PayloadVersion = 3;
    private const string DefaultSourcePrefabName = "DragonTear";
    private const string CllcEffectKey = "CL&LC effect";
    private const string OdinHorseHaveArmorKey = "HaveArmor";
    private const string ActionEffectPrefab = "fx_summon_start";
    private const float ActionDelay = 4.5f;
    private const float CaptureRange = 5f;
    private const float ReleaseAimRange = 5f;
    private const float ReleaseFallbackAimDistance = 1f;
    private const float ReleaseUpOffset = 1f;
    private const int IconRenderLayer = 31;
    private const int CreatureIconSize = 128;
    private const float CreatureIconOverlayScale = 0.78f;
    private const int CreatureIconBorderClearPixels = 2;
    private const string IconCacheRevision = "raw-v4-composite78-border2";
    private const string IconCacheMagic = "FLGICON";
    private const int IconCacheFormatVersion = 1;
    private static readonly Color TamingOrbGreen = new(0.18f, 0.72f, 0.28f, 1f);
    private static readonly Quaternion CreatureIconRotation = Quaternion.Euler(23f, 51f, 25.8f);
    private static readonly HashSet<ItemDrop.ItemData> PendingItems = new();
    private static readonly Dictionary<ItemDrop.ItemData.SharedData, Dictionary<string, int>> CreatureIconVariants = new(new SharedDataReferenceComparer());
    private static readonly HashSet<string> CreatureIconFailures = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<Type, List<Type>> IconComponentDependencies = new();
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

    private static readonly int StarProgressHash = StringExtensionMethods.GetStableHashCode("flg_star_progress");
    private static readonly int GrowthElapsedHash = StringExtensionMethods.GetStableHashCode("flg_growth_elapsed");
    private static readonly int GrowthLastEvalHash = StringExtensionMethods.GetStableHashCode("flg_growth_last_eval");
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
    private static Sprite? _icon;
    private static Camera? _iconCamera;
    private static Light? _iconLight;
    private static ItemDrop.ItemData.SharedData? _tamingOrbSharedData;

    internal static void Shutdown()
    {
        PendingItems.Clear();
        CreatureIconVariants.Clear();
        CreatureIconFailures.Clear();
        IconComponentDependencies.Clear();
        NonTamingOrbSharedData.Clear();
        if (_iconCamera != null)
        {
            UnityEngine.Object.Destroy(_iconCamera.gameObject);
            _iconCamera = null;
        }

        if (_iconLight != null)
        {
            UnityEngine.Object.Destroy(_iconLight.gameObject);
            _iconLight = null;
        }

        _tamingOrbSharedData = null;
    }

    internal static void OnGameDataChanged()
    {
        CreatureIconFailures.Clear();
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
        GameObject? prefab = EnsurePrefab(objectDb);
        if ((object?)prefab == null)
        {
            return;
        }

        RegisterWithObjectDb(objectDb, prefab);
        if ((object?)ZNetScene.instance != null)
        {
            RegisterWithZNetScene(ZNetScene.instance, prefab);
        }

        if (IsEnabled())
        {
            RegisterRecipe(objectDb, prefab);
        }
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

        if (!IsEnabled())
        {
            FeedLocalization.Message(player, "$flg_taming_orb_disabled");
            return true;
        }

        if (TryReadPayload(item, out CapturedTamePayload? _))
        {
            TryRelease(player, item);
            return true;
        }

        TryCapture(player, tameable, item);
        return true;
    }

    internal static bool TryHandlePlayerUseItem(Player player, ItemDrop.ItemData item)
    {
        if (player == null || !IsTamingOrb(item))
        {
            return false;
        }

        if (!IsEnabled())
        {
            FeedLocalization.Message(player, "$flg_taming_orb_disabled");
            return true;
        }

        if (TryReadPayload(item, out CapturedTamePayload? _))
        {
            TryRelease(player, item);
            return true;
        }

        GameObject? hoverObject = player.GetHoverObject();
        Tameable? tameable = hoverObject == null ? null : hoverObject.GetComponentInParent<Tameable>();
        if (tameable == null)
        {
            FeedLocalization.Message(player, "$flg_taming_orb_use_on_tamed");
            return true;
        }

        TryCapture(player, tameable, item);
        return true;
    }

    internal static void AppendTooltip(ItemDrop.ItemData item, bool crafting, ref string tooltip)
    {
        if (crafting || !IsTamingOrb(item))
        {
            return;
        }

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

    private static bool TryCapture(Player player, Tameable tameable, ItemDrop.ItemData item)
    {
        if (tameable == null)
        {
            FeedLocalization.Message(player, "$flg_taming_orb_no_tameable");
            return false;
        }

        if (PendingItems.Contains(item))
        {
            FeedLocalization.Message(player, "$flg_taming_orb_pending");
            return true;
        }

        Character? character = ((Component)tameable).GetComponent<Character>();
        ZNetView? nview = ((Component)tameable).GetComponent<ZNetView>();
        if (character == null || nview == null || !nview.IsValid())
        {
            FeedLocalization.Message(player, "$flg_taming_orb_cannot_capture");
            return false;
        }

        if (character.IsDead())
        {
            FeedLocalization.Message(player, "$flg_taming_orb_dead");
            return false;
        }

        if (!character.IsTamed())
        {
            FeedLocalization.Message(player, "$flg_taming_orb_only_tamed");
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
        PendingItems.Add(item);
        FeedLocalization.Message(player, "$flg_taming_orb_capturing", character.GetHoverName());
        FeedLikeGrandmaPlugin.Instance.StartCoroutine(CompleteCaptureAfterDelay(player, tameable, character, nview, item, creaturePrefabName, ActionDelay));
        return true;
    }

    private static IEnumerator CompleteCaptureAfterDelay(Player player, Tameable tameable, Character character, ZNetView nview, ItemDrop.ItemData item, string creaturePrefabName, float delay)
    {
        yield return new WaitForSeconds(delay);
        try
        {
            CompleteCapture(player, tameable, character, nview, item, creaturePrefabName);
        }
        finally
        {
            PendingItems.Remove(item);
        }
    }

    private static bool CompleteCapture(Player player, Tameable tameable, Character character, ZNetView nview, ItemDrop.ItemData item, string creaturePrefabName)
    {
        if (player == null)
        {
            return false;
        }

        Inventory inventory = player.GetInventory();
        if (!inventory.ContainsItem(item))
        {
            return false;
        }

        if (character == null ||
            tameable == null ||
            nview == null ||
            !nview.IsValid() ||
            character.IsDead() ||
            !character.IsTamed() ||
            Vector3.Distance(player.transform.position, ((Component)character).transform.position) > CaptureRange)
        {
            FeedLocalization.Message(player, "$flg_taming_orb_no_longer_capturable");
            return false;
        }

        CapturedTamePayload payload;
        string serializedPayload;
        int iconVariant = 0;
        try
        {
            payload = CapturePayload(character, tameable, nview, creaturePrefabName);
            serializedPayload = payload.Serialize();
            try
            {
                iconVariant = GetIconVariantForPayload(item, payload);
            }
            catch (Exception ex)
            {
                FeedLikeGrandmaPlugin.Log.LogDebug(
                    $"Could not prepare {PrefabName} icon for {creaturePrefabName}: {ex.Message}");
            }
        }
        catch (Exception ex)
        {
            FeedLikeGrandmaPlugin.Log.LogWarning(
                $"{PrefabName} failed to prepare capture data for {creaturePrefabName}: {ex.Message}");
            FeedLocalization.Message(player, "$flg_taming_orb_cannot_capture");
            return false;
        }

        if (!inventory.ContainsItem(item) ||
            !nview.IsValid() ||
            character.IsDead() ||
            !character.IsTamed())
        {
            FeedLocalization.Message(player, "$flg_taming_orb_no_longer_capturable");
            return false;
        }

        nview.ClaimOwnership();
        if (!nview.IsOwner())
        {
            FeedLocalization.Message(player, "$flg_taming_orb_no_longer_capturable");
            return false;
        }

        int previousStack = item.m_stack;
        int previousVariant = item.m_variant;
        bool hadPreviousPayload = item.m_customData.TryGetValue(PayloadKey, out string? previousPayload);
        try
        {
            item.m_stack = 1;
            item.m_customData[PayloadKey] = serializedPayload;
            item.m_variant = iconVariant;
            inventory.Changed();
            nview.Destroy();
        }
        catch (Exception ex)
        {
            item.m_stack = previousStack;
            item.m_variant = previousVariant;
            if (hadPreviousPayload)
            {
                item.m_customData[PayloadKey] = previousPayload!;
            }
            else
            {
                item.m_customData.Remove(PayloadKey);
            }

            inventory.Changed();
            FeedLikeGrandmaPlugin.Log.LogWarning(
                $"{PrefabName} failed to commit capture for {creaturePrefabName}: {ex.Message}");
            FeedLocalization.Message(player, "$flg_taming_orb_cannot_capture");
            return false;
        }

        FeedLocalization.Message(player, "$flg_taming_orb_captured", GetPayloadDisplayName(payload));
        return true;
    }

    private static bool TryRelease(Player player, ItemDrop.ItemData item)
    {
        if (!TryReadPayload(item, out CapturedTamePayload? payload))
        {
            FeedLocalization.Message(player, "$flg_taming_orb_data_invalid");
            return false;
        }

        if (PendingItems.Contains(item))
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
        PlayActionEffect(spawnPosition, spawnRotation);
        PendingItems.Add(item);
        FeedLocalization.Message(player, "$flg_taming_orb_releasing", GetPayloadDisplayName(payload));
        FeedLikeGrandmaPlugin.Instance.StartCoroutine(CompleteReleaseAfterDelay(player, item, payload, creaturePrefab, spawnPosition, spawnRotation, ActionDelay));
        return true;
    }

    private static IEnumerator CompleteReleaseAfterDelay(Player player, ItemDrop.ItemData item, CapturedTamePayload payload, GameObject creaturePrefab, Vector3 spawnPosition, Quaternion spawnRotation, float delay)
    {
        yield return new WaitForSeconds(delay);
        try
        {
            CompleteRelease(player, item, payload, creaturePrefab, spawnPosition, spawnRotation);
        }
        finally
        {
            PendingItems.Remove(item);
        }
    }

    private static bool CompleteRelease(Player player, ItemDrop.ItemData item, CapturedTamePayload payload, GameObject creaturePrefab, Vector3 spawnPosition, Quaternion spawnRotation)
    {
        if (player == null || !player.GetInventory().ContainsItem(item))
        {
            return false;
        }

        if (!TryReadPayload(item, out CapturedTamePayload? currentPayload) || currentPayload!.Serialize() != payload.Serialize())
        {
            FeedLocalization.Message(player, "$flg_taming_orb_data_changed");
            return false;
        }

        GameObject? spawned = null;
        try
        {
            spawned = UnityEngine.Object.Instantiate(creaturePrefab, spawnPosition, spawnRotation);
            Character? character = spawned.GetComponent<Character>();
            Tameable? tameable = spawned.GetComponent<Tameable>();
            ZNetView? nview = spawned.GetComponent<ZNetView>();

            if (character == null || nview == null || !nview.IsValid())
            {
                DestroySpawnedRelease(spawned);
                FeedLocalization.Message(player, "$flg_taming_orb_invalid_creature");
                return false;
            }

            RestorePayload(payload, character, tameable, nview);
            item.m_customData.Remove(PayloadKey);
            item.m_variant = 0;
            player.GetInventory().Changed();

            FeedLocalization.Message(player, "$flg_taming_orb_released", GetPayloadDisplayName(payload));
            return true;
        }
        catch (Exception ex)
        {
            if ((object?)spawned != null)
            {
                DestroySpawnedRelease(spawned);
            }

            FeedLikeGrandmaPlugin.Log.LogWarning($"{PrefabName} failed to release {payload.Prefab}: {ex.Message}");
            FeedLocalization.Message(player, "$flg_taming_orb_release_failed");
            return false;
        }
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

    private static CapturedTamePayload CapturePayload(Character character, Tameable tameable, ZNetView nview, string creaturePrefabName)
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
            HasSaddle = tameable.HaveSaddle(),
            Inventory = zdo.GetString(ZDOVars.s_items, string.Empty),
            LastFedAgeTicks = lastFedTicks > 0L ? Math.Max(0L, nowTicks - lastFedTicks) : 0L,
            SpawnAgeTicks = spawnTicks > 0L ? Math.Max(0L, nowTicks - spawnTicks) : 0L,
            StarProgress = Mathf.Max(0f, zdo.GetFloat(StarProgressHash, 0f)),
            GrowthElapsed = zdo.GetFloat(GrowthElapsedHash, -1f),
            CllcEffect = zdo.GetInt(CllcEffectKey, 0),
            CllcInfusion = cllcInfusion,
            CllcExtraEffect = cllcExtraEffect,
            OdinHorseHasArmor = LooksLikeOdinHorse(tameable) && zdo.GetBool(OdinHorseHaveArmorHash, false),
            SaddleStamina = TryGetSaddle(character, tameable, out Sadle? saddle)
                ? Mathf.Clamp(saddle.GetStamina(), 0f, saddle.GetMaxStamina())
                : -1f
        };
    }

    private static void RestorePayload(CapturedTamePayload payload, Character character, Tameable? tameable, ZNetView nview)
    {
        ZDO zdo = nview.GetZDO();
        long nowTicks = ZNet.instance.GetTime().Ticks;

        if (payload.CllcEffect != 0)
        {
            zdo.Set(CllcEffectKey, payload.CllcEffect);
        }

        if (!string.IsNullOrEmpty(payload.Inventory))
        {
            zdo.Set(ZDOVars.s_items, payload.Inventory);
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
            zdo.Set(StarProgressHash, payload.StarProgress);
        }

        if (payload.GrowthElapsed >= 0f)
        {
            zdo.Set(GrowthElapsedHash, payload.GrowthElapsed);
            zdo.Set(GrowthLastEvalHash, nowTicks);
        }

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
        Camera? camera = (object?)GameCamera.instance != null ? GameCamera.instance.m_camera : null;
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
        if (nview.m_functions != null && nview.m_functions.ContainsKey(OdinHorseSetArmorRpcHash))
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

        CreatureIconVariants.Remove(shared);
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

    internal static void NormalizeVariant(ItemDrop.ItemData item)
    {
        if (!IsTamingOrb(item))
        {
            return;
        }

        if (!item.m_customData.ContainsKey(PayloadKey))
        {
            item.m_variant = 0;
            return;
        }

        if (!TryReadPayload(item, out CapturedTamePayload? payload, logWarnings: false))
        {
            item.m_variant = 0;
            return;
        }

        string payloadPrefabName = payload!.Prefab;
        if (TryGetCreatureIconVariant(item.m_shared, payloadPrefabName, out int expectedVariant) &&
            item.m_variant == expectedVariant &&
            IsIconVariantValid(item.m_shared, expectedVariant))
        {
            return;
        }

        item.m_variant = GetIconVariantForPayload(item, payload);
    }

    private static int GetIconVariantForPayload(ItemDrop.ItemData item, CapturedTamePayload payload)
    {
        if ((object?)item == null ||
            (object?)item.m_shared == null ||
            payload == null ||
            string.IsNullOrWhiteSpace(payload.Prefab))
        {
            return 0;
        }

        return EnsureCreatureIconVariant(item.m_shared, payload.Prefab);
    }

    private static int EnsureCreatureIconVariant(ItemDrop.ItemData.SharedData shared, string creaturePrefabName)
    {
        if (string.IsNullOrWhiteSpace(creaturePrefabName))
        {
            return 0;
        }

        Dictionary<string, int> variants = GetCreatureIconVariants(shared);
        if (variants.TryGetValue(creaturePrefabName, out int cachedVariant) &&
            IsIconVariantValid(shared, cachedVariant))
        {
            return cachedVariant;
        }

        variants.Remove(creaturePrefabName);
        if (CreatureIconFailures.Contains(creaturePrefabName))
        {
            return 0;
        }

        GameObject? creaturePrefab = ZNetScene.instance == null ? null : ZNetScene.instance.GetPrefab(creaturePrefabName);
        if ((object?)creaturePrefab == null)
        {
            CreatureIconFailures.Add(creaturePrefabName);
            return 0;
        }

        Sprite? creatureIcon = TryLoadCreatureIconFromCache(creaturePrefabName);
        if ((object?)creatureIcon == null)
        {
            Sprite? renderedCreatureIcon = CreateCreatureIcon(creaturePrefab);
            try
            {
                creatureIcon = CreateCompositeTamingOrbIcon(renderedCreatureIcon);
            }
            finally
            {
                if ((object?)renderedCreatureIcon != null)
                {
                    Texture2D renderedTexture = renderedCreatureIcon.texture;
                    UnityEngine.Object.Destroy(renderedCreatureIcon);
                    if ((object?)renderedTexture != null)
                    {
                        UnityEngine.Object.Destroy(renderedTexture);
                    }
                }
            }

            if ((object?)creatureIcon != null)
            {
                TrySaveCreatureIconToCache(creaturePrefabName, creatureIcon);
            }
        }

        if ((object?)creatureIcon == null)
        {
            CreatureIconFailures.Add(creaturePrefabName);
            return 0;
        }

        int variant = AppendInternalIconVariant(shared, creatureIcon);
        variants[creaturePrefabName] = variant;
        return variant;
    }

    private static Dictionary<string, int> GetCreatureIconVariants(ItemDrop.ItemData.SharedData shared)
    {
        if (!CreatureIconVariants.TryGetValue(shared, out Dictionary<string, int> variants))
        {
            variants = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            CreatureIconVariants[shared] = variants;
        }

        return variants;
    }

    private static bool TryGetCreatureIconVariant(ItemDrop.ItemData.SharedData shared, string creaturePrefabName, out int variant)
    {
        variant = 0;
        return !string.IsNullOrWhiteSpace(creaturePrefabName) &&
               CreatureIconVariants.TryGetValue(shared, out Dictionary<string, int> variants) &&
               variants.TryGetValue(creaturePrefabName, out variant) &&
               IsIconVariantValid(shared, variant);
    }

    private static bool IsIconVariantValid(ItemDrop.ItemData.SharedData shared, int variant)
    {
        return variant > 0 &&
               shared.m_icons != null &&
               variant < shared.m_icons.Length &&
               shared.m_icons[variant] != null;
    }

    private static int AppendInternalIconVariant(ItemDrop.ItemData.SharedData shared, Sprite icon)
    {
        Sprite[] icons = shared.m_icons ?? Array.Empty<Sprite>();
        if (icons.Length == 0 && (object?)_icon != null)
        {
            icons = new[] { _icon };
        }

        int variant = icons.Length;
        Array.Resize(ref icons, variant + 1);
        icons[variant] = icon;
        shared.m_icons = icons;
        shared.m_variants = 1;
        return variant;
    }

    private static Sprite? TryLoadCreatureIconFromCache(string creaturePrefabName)
    {
        string cachePath = GetCreatureIconCachePath(creaturePrefabName);
        if (!File.Exists(cachePath))
        {
            return null;
        }

        try
        {
            using FileStream stream = File.Open(cachePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            using BinaryReader reader = new(stream);

            string magic = reader.ReadString();
            int version = reader.ReadInt32();
            int width = reader.ReadInt32();
            int height = reader.ReadInt32();
            if (magic != IconCacheMagic ||
                version != IconCacheFormatVersion ||
                width <= 0 ||
                height <= 0 ||
                width > 1024 ||
                height > 1024)
            {
                TryDeleteIconCache(cachePath);
                return null;
            }

            int pixelCount = checked(width * height);
            Color32[] pixels = new Color32[pixelCount];
            for (int i = 0; i < pixelCount; i++)
            {
                pixels[i] = new Color32(
                    reader.ReadByte(),
                    reader.ReadByte(),
                    reader.ReadByte(),
                    reader.ReadByte());
            }

            Texture2D texture = new(width, height, TextureFormat.RGBA32, false)
            {
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp,
                name = $"FLG_{creaturePrefabName}_CachedIcon"
            };
            texture.SetPixels32(pixels);
            texture.Apply();
            return Sprite.Create(texture, new Rect(0f, 0f, texture.width, texture.height), new Vector2(0.5f, 0.5f));
        }
        catch (Exception ex)
        {
            FeedLikeGrandmaPlugin.Log.LogDebug($"Could not load cached {PrefabName} creature icon for {creaturePrefabName}: {ex.Message}");
            TryDeleteIconCache(cachePath);
            return null;
        }
    }

    private static void TrySaveCreatureIconToCache(string creaturePrefabName, Sprite icon)
    {
        if ((object?)icon == null || (object?)icon.texture == null)
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(GetCreatureIconCacheDirectory());
            Color32[] pixels = icon.texture.GetPixels32();
            if (pixels.Length == 0)
            {
                return;
            }

            using FileStream stream = File.Open(GetCreatureIconCachePath(creaturePrefabName), FileMode.Create, FileAccess.Write, FileShare.Read);
            using BinaryWriter writer = new(stream);
            writer.Write(IconCacheMagic);
            writer.Write(IconCacheFormatVersion);
            writer.Write(icon.texture.width);
            writer.Write(icon.texture.height);
            foreach (Color32 pixel in pixels)
            {
                writer.Write(pixel.r);
                writer.Write(pixel.g);
                writer.Write(pixel.b);
                writer.Write(pixel.a);
            }
        }
        catch (Exception ex)
        {
            FeedLikeGrandmaPlugin.Log.LogDebug($"Could not cache {PrefabName} creature icon for {creaturePrefabName}: {ex.Message}");
        }
    }

    private static void TryDeleteIconCache(string cachePath)
    {
        try
        {
            if (File.Exists(cachePath))
            {
                File.Delete(cachePath);
            }
        }
        catch
        {
            // Best effort only. A bad cache will be ignored next time if it cannot be deleted now.
        }
    }

    private static string GetCreatureIconCacheDirectory()
    {
        return Path.Combine(FeedLikeGrandmaPlugin.ConfigDirectoryPath, "cache", "icons");
    }

    private static string GetCreatureIconCachePath(string creaturePrefabName)
    {
        string safeName = SanitizeCacheFileName(creaturePrefabName);
        uint hash = unchecked((uint)creaturePrefabName.GetStableHashCode());
        return Path.Combine(GetCreatureIconCacheDirectory(), $"{safeName}-{hash:X8}-{IconCacheRevision}.flgicon");
    }

    private static string SanitizeCacheFileName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return "creature";
        }

        HashSet<char> invalid = new(Path.GetInvalidFileNameChars());
        char[] chars = name
            .Select(character => invalid.Contains(character) || char.IsWhiteSpace(character) ? '_' : character)
            .ToArray();
        string safe = new string(chars).Trim('_');
        if (string.IsNullOrWhiteSpace(safe))
        {
            safe = "creature";
        }

        return safe.Length <= 80 ? safe : safe.Substring(0, 80);
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

        if (!objectDb.m_itemByHash.ContainsKey(hash))
        {
            objectDb.m_itemByHash.Add(hash, prefab);
        }
        else if (objectDb.m_itemByHash[hash] == null || objectDb.m_itemByHash[hash].name == PrefabName)
        {
            objectDb.m_itemByHash[hash] = prefab;
        }

        ItemDrop? itemDrop = prefab.GetComponent<ItemDrop>();
        if ((object?)itemDrop != null)
        {
            objectDb.m_itemByData[itemDrop.m_itemData.m_shared] = prefab;
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

        if (!zNetScene.m_namedPrefabs.ContainsKey(hash))
        {
            zNetScene.m_namedPrefabs.Add(hash, prefab);
        }
        else if (zNetScene.m_namedPrefabs[hash] == null || zNetScene.m_namedPrefabs[hash].name == PrefabName)
        {
            zNetScene.m_namedPrefabs[hash] = prefab;
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
               ZNetScene.instance.m_namedPrefabs != null &&
               ZNetScene.instance.m_namedPrefabs.Count > 0;
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

    private sealed class SharedDataReferenceComparer : IEqualityComparer<ItemDrop.ItemData.SharedData>
    {
        public bool Equals(ItemDrop.ItemData.SharedData? x, ItemDrop.ItemData.SharedData? y)
        {
            return ReferenceEquals(x, y);
        }

        public int GetHashCode(ItemDrop.ItemData.SharedData obj)
        {
            return RuntimeHelpers.GetHashCode(obj);
        }
    }

    private static GameObject? FindSpawnableItemPrefab(ObjectDB objectDb, string prefabName)
    {
        if ((object?)ZNetScene.instance != null &&
            ZNetScene.instance.m_namedPrefabs.TryGetValue(prefabName.GetStableHashCode(), out GameObject scenePrefab) &&
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
            if (objectDb.m_itemByHash.TryGetValue(hash, out GameObject byHash) && (object?)byHash != null)
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
            ZNetScene.instance.m_namedPrefabs.TryGetValue(prefabName.GetStableHashCode(), out GameObject scenePrefab) &&
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

    private static Material? CreateSafeVisualMaterial(Material? preferred, Material? fallback)
    {
        try
        {
            if ((object?)preferred != null)
            {
                return new Material(preferred);
            }

            if ((object?)fallback != null)
            {
                return new Material(fallback);
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
            Material? material = CreateSafeVisualMaterial(sharedMaterials[i], null);
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

        prefabName = gameObject.name;
        int parenthesisIndex = prefabName.IndexOf('(');
        int spaceIndex = prefabName.IndexOf(' ');
        int index = parenthesisIndex < 0
            ? spaceIndex
            : spaceIndex < 0 ? parenthesisIndex : Mathf.Min(parenthesisIndex, spaceIndex);
        if (index >= 0)
        {
            prefabName = prefabName.Substring(0, index);
        }

        return !string.IsNullOrWhiteSpace(prefabName) && ZNetScene.instance.GetPrefab(prefabName) != null;
    }

    private static Sprite? CreateCreatureIcon(GameObject creaturePrefab)
    {
        GameObject? clone = null;
        try
        {
            clone = CreateCreatureIconClone(creaturePrefab);
            if ((object?)clone == null)
            {
                return null;
            }

            if (!TryGetRenderBounds(clone, out Bounds bounds))
            {
                return null;
            }

            clone.transform.position -= bounds.center;
            if (!TryGetRenderBounds(clone, out bounds))
            {
                return null;
            }

            EnsureIconRenderer();
            if (_iconCamera == null)
            {
                return null;
            }

            return RenderCreatureIcon(clone, bounds);
        }
        catch (Exception ex)
        {
            FeedLikeGrandmaPlugin.Log.LogDebug($"Could not render {PrefabName} creature icon for {creaturePrefab.name}: {ex.Message}");
            return null;
        }
        finally
        {
            if ((object?)clone != null)
            {
                RemoveIconCloneCharacters(clone);
                UnityEngine.Object.DestroyImmediate(clone);
            }
        }
    }

    private static GameObject? CreateCreatureIconClone(GameObject creaturePrefab)
    {
        GameObject root = new("FeedLikeGrandma Creature Icon Root");
        root.SetActive(false);
        try
        {
            GameObject clone = UnityEngine.Object.Instantiate(creaturePrefab, root.transform);
            clone.name = $"{creaturePrefab.name}_FLGIcon";
            if (!StripForIconRender(root.transform))
            {
                return null;
            }

            SetLayerRecursive(clone, IconRenderLayer);
            clone.transform.SetParent(null, false);
            clone.transform.position = Vector3.zero;
            clone.transform.rotation = CreatureIconRotation;
            clone.SetActive(true);
            return clone;
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(root);
        }
    }

    private static bool StripForIconRender(Transform parent)
    {
        for (int i = 0; i < parent.childCount; i++)
        {
            if (!StripForIconRender(parent.GetChild(i)))
            {
                return false;
            }
        }

        List<Type>? deletionOrder = GetIconComponentDeletionOrder(parent);
        if (deletionOrder == null)
        {
            return false;
        }

        GameObject gameObject = parent.gameObject;
        foreach (Type componentType in deletionOrder)
        {
            foreach (Component component in gameObject.GetComponents(componentType))
            {
                if ((object?)component == null || IsIconRenderComponent(component))
                {
                    continue;
                }

                try
                {
                    UnityEngine.Object.DestroyImmediate(component);
                }
                catch (Exception ex)
                {
                    FeedLikeGrandmaPlugin.Log.LogDebug($"Could not strip {component.GetType().Name} while rendering {PrefabName} creature icon: {ex.Message}");
                    return false;
                }
            }
        }

        return true;
    }

    private static bool IsIconRenderComponent(Component component)
    {
        return component is Transform ||
               component is MeshRenderer ||
               component is SkinnedMeshRenderer ||
               component is MeshFilter;
    }

    private static List<Type>? GetIconComponentDeletionOrder(Transform transform)
    {
        List<Type> result = new();
        HashSet<Type> visited = new();
        HashSet<Type> recursionStack = new();

        foreach (Component component in transform.gameObject.GetComponents<Component>())
        {
            if ((object?)component != null &&
                !AddIconComponentDeletionOrder(component.GetType(), visited, recursionStack, result))
            {
                return null;
            }
        }

        result.Reverse();
        return result;
    }

    private static bool AddIconComponentDeletionOrder(Type type, HashSet<Type> visited, HashSet<Type> recursionStack, List<Type> result)
    {
        if (visited.Contains(type))
        {
            return true;
        }

        List<Type>? subclasses = null;
        int subclassIndex = result.FindIndex(type.IsSubclassOf);
        if (subclassIndex >= 0)
        {
            subclasses = result.Skip(subclassIndex).ToList();
            result.RemoveRange(subclassIndex, result.Count - subclassIndex);
        }

        if (recursionStack.Contains(type))
        {
            return false;
        }

        recursionStack.Add(type);
        foreach (Type dependency in GetIconComponentDependencies(type))
        {
            if (!AddIconComponentDeletionOrder(dependency, visited, recursionStack, result))
            {
                return false;
            }
        }

        if (subclasses != null)
        {
            result.AddRange(subclasses);
        }
        else
        {
            result.Add(type);
        }

        recursionStack.Remove(type);
        visited.Add(type);
        return true;
    }

    private static List<Type> GetIconComponentDependencies(Type type)
    {
        if (IconComponentDependencies.TryGetValue(type, out List<Type>? dependencies))
        {
            return dependencies;
        }

        dependencies = new List<Type>();
        foreach (RequireComponent attribute in type.GetCustomAttributes<RequireComponent>(true))
        {
            if (attribute.m_Type0 != null)
            {
                dependencies.Add(attribute.m_Type0);
            }

            if (attribute.m_Type1 != null)
            {
                dependencies.Add(attribute.m_Type1);
            }

            if (attribute.m_Type2 != null)
            {
                dependencies.Add(attribute.m_Type2);
            }
        }

        dependencies = dependencies.Distinct().ToList();
        IconComponentDependencies[type] = dependencies;
        return dependencies;
    }

    private static void RemoveIconCloneCharacters(GameObject clone)
    {
        foreach (Character character in clone.GetComponentsInChildren<Character>(includeInactive: true))
        {
            if (character == null)
            {
                continue;
            }

            Character.GetAllCharacters().Remove(character);
            Character.Instances.Remove(character);
            EnemyHud.instance?.RemoveCharacterHud(character);
        }
    }

    private static void SetLayerRecursive(GameObject gameObject, int layer)
    {
        gameObject.layer = layer;
        foreach (Transform child in gameObject.transform)
        {
            SetLayerRecursive(child.gameObject, layer);
        }
    }

    private static bool TryGetRenderBounds(GameObject gameObject, out Bounds bounds)
    {
        bounds = default;
        bool found = false;
        foreach (Renderer renderer in gameObject.GetComponentsInChildren<Renderer>(includeInactive: false))
        {
            if ((object?)renderer == null ||
                renderer is not MeshRenderer and not SkinnedMeshRenderer)
            {
                continue;
            }

            if (!found)
            {
                bounds = renderer.bounds;
                found = true;
            }
            else
            {
                bounds.Encapsulate(renderer.bounds);
            }
        }

        return found;
    }

    private static void EnsureIconRenderer()
    {
        if (_iconCamera != null)
        {
            return;
        }

        int iconMask = 1 << IconRenderLayer;
        GameObject cameraObject = new("FeedLikeGrandma Creature Icon Camera", typeof(Camera));
        UnityEngine.Object.DontDestroyOnLoad(cameraObject);
        _iconCamera = cameraObject.GetComponent<Camera>();
        _iconCamera.enabled = false;
        _iconCamera.backgroundColor = Color.clear;
        _iconCamera.clearFlags = CameraClearFlags.Color;
        _iconCamera.cullingMask = iconMask;
        _iconCamera.orthographic = false;
        _iconCamera.fieldOfView = 0.5f;
        _iconCamera.nearClipPlane = 0.01f;
        _iconCamera.farClipPlane = 100000f;

        GameObject lightObject = new("FeedLikeGrandma Creature Icon Light", typeof(Light));
        UnityEngine.Object.DontDestroyOnLoad(lightObject);
        _iconLight = lightObject.GetComponent<Light>();
        _iconLight.type = LightType.Directional;
        _iconLight.intensity = 1.35f;
        _iconLight.cullingMask = iconMask;
        _iconLight.enabled = false;
    }

    private static Sprite? RenderCreatureIcon(GameObject clone, Bounds bounds)
    {
        if (_iconCamera == null)
        {
            return null;
        }

        float framedSize = Mathf.Max(bounds.size.x, bounds.size.y) + 0.25f;
        float distance = Mathf.Max(8f, framedSize / Mathf.Tan(_iconCamera.fieldOfView * Mathf.Deg2Rad) * 1.2f);

        _iconCamera.transform.position = new Vector3(0f, 0f, distance);
        _iconCamera.transform.rotation = Quaternion.Euler(0f, 180f, 0f);
        _iconCamera.farClipPlane = distance + Mathf.Max(1000f, bounds.size.magnitude * 8f);

        if (_iconLight != null)
        {
            _iconLight.transform.position = _iconCamera.transform.position;
            _iconLight.transform.rotation = Quaternion.Euler(5f, 180f, 5f);
        }

        RenderTexture? previousActive = RenderTexture.active;
        RenderTexture? previousTarget = _iconCamera.targetTexture;
        bool previousLightEnabled = _iconLight != null && _iconLight.enabled;
        RenderTexture temporary = RenderTexture.GetTemporary(CreatureIconSize, CreatureIconSize, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
        try
        {
            if (_iconLight != null)
            {
                _iconLight.enabled = true;
            }

            _iconCamera.targetTexture = temporary;
            RenderTexture.active = temporary;
            GL.Clear(true, true, Color.clear);
            _iconCamera.Render();

            Texture2D texture = new(CreatureIconSize, CreatureIconSize, TextureFormat.RGBA32, false)
            {
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp,
                name = $"FLG_{clone.name}_Icon"
            };
            texture.ReadPixels(new Rect(0f, 0f, CreatureIconSize, CreatureIconSize), 0, 0);
            texture.Apply();
            return Sprite.Create(texture, new Rect(0f, 0f, CreatureIconSize, CreatureIconSize), new Vector2(0.5f, 0.5f));
        }
        finally
        {
            if (_iconLight != null)
            {
                _iconLight.enabled = previousLightEnabled;
            }

            _iconCamera.targetTexture = previousTarget;
            RenderTexture.active = previousActive;
            RenderTexture.ReleaseTemporary(temporary);
        }
    }

    private static Sprite? CreateTintedIcon(Sprite? source, Color tint)
    {
        if ((object?)source == null || (object?)source.texture == null)
        {
            return source;
        }

        Texture2D? texture = DuplicateSpriteTexture(source);
        if ((object?)texture == null)
        {
            return source;
        }

        Color[] pixels = texture.GetPixels();
        for (int i = 0; i < pixels.Length; i++)
        {
            Color pixel = pixels[i];
            if (pixel.a > 0.01f)
            {
                pixels[i] = new Color(
                    Mathf.Lerp(pixel.r, tint.r, 0.45f),
                    Mathf.Lerp(pixel.g, tint.g, 0.45f),
                    Mathf.Lerp(pixel.b, tint.b, 0.45f),
                    pixel.a);
            }
        }

        texture.SetPixels(pixels);
        texture.Apply();
        texture.name = "FLG_TamingOrb_Icon";
        return Sprite.Create(texture, new Rect(0f, 0f, texture.width, texture.height), new Vector2(0.5f, 0.5f), source.pixelsPerUnit);
    }

    private static Sprite? CreateCompositeTamingOrbIcon(Sprite? creatureIcon)
    {
        if ((object?)creatureIcon == null || (object?)creatureIcon.texture == null)
        {
            return null;
        }

        Texture2D baseTexture = CreateReadableIconTexture(_icon, CreatureIconSize, CreatureIconSize);
        int overlaySize = Mathf.Clamp(Mathf.RoundToInt(CreatureIconSize * CreatureIconOverlayScale), 1, CreatureIconSize);
        Texture2D creatureTexture = CreateReadableIconTexture(creatureIcon, overlaySize, overlaySize);
        Color32[] basePixels = baseTexture.GetPixels32();
        Color32[] creaturePixels = creatureTexture.GetPixels32();
        ClearIconTextureBorder(creaturePixels, overlaySize, overlaySize, CreatureIconBorderClearPixels);
        int offset = (CreatureIconSize - overlaySize) / 2;
        for (int y = 0; y < overlaySize; y++)
        {
            int destinationY = y + offset;
            for (int x = 0; x < overlaySize; x++)
            {
                int destinationX = x + offset;
                int destinationIndex = destinationY * CreatureIconSize + destinationX;
                int sourceIndex = y * overlaySize + x;
                basePixels[destinationIndex] = AlphaBlend(basePixels[destinationIndex], creaturePixels[sourceIndex]);
            }
        }

        baseTexture.SetPixels32(basePixels);
        baseTexture.Apply();
        baseTexture.name = "FLG_TamingOrb_Creature_Icon";
        UnityEngine.Object.Destroy(creatureTexture);
        return Sprite.Create(baseTexture, new Rect(0f, 0f, baseTexture.width, baseTexture.height), new Vector2(0.5f, 0.5f));
    }

    private static void ClearIconTextureBorder(Color32[] pixels, int width, int height, int thickness)
    {
        if (pixels.Length != width * height || width <= 0 || height <= 0 || thickness <= 0)
        {
            return;
        }

        thickness = Mathf.Min(thickness, width / 2, height / 2);
        Color32 transparent = new(0, 0, 0, 0);
        for (int i = 0; i < thickness; i++)
        {
            int bottomRow = i * width;
            int topRow = (height - 1 - i) * width;
            for (int x = 0; x < width; x++)
            {
                pixels[bottomRow + x] = transparent;
                pixels[topRow + x] = transparent;
            }

            for (int y = 0; y < height; y++)
            {
                pixels[y * width + i] = transparent;
                pixels[y * width + (width - 1 - i)] = transparent;
            }
        }
    }

    private static Texture2D CreateReadableIconTexture(Sprite? source, int width, int height)
    {
        Texture2D texture = new(width, height, TextureFormat.RGBA32, false)
        {
            filterMode = FilterMode.Bilinear,
            wrapMode = TextureWrapMode.Clamp
        };

        if ((object?)source == null || (object?)source.texture == null)
        {
            return texture;
        }

        Texture2D? sourceTexture = DuplicateSpriteTexture(source);
        if ((object?)sourceTexture == null)
        {
            return texture;
        }

        RenderTexture? active = RenderTexture.active;
        RenderTexture temporary = RenderTexture.GetTemporary(width, height, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
        try
        {
            Graphics.Blit(sourceTexture, temporary);
            RenderTexture.active = temporary;
            texture.ReadPixels(new Rect(0f, 0f, width, height), 0, 0);
            texture.Apply();
        }
        finally
        {
            RenderTexture.active = active;
            RenderTexture.ReleaseTemporary(temporary);
            UnityEngine.Object.Destroy(sourceTexture);
        }

        return texture;
    }

    private static Color32 AlphaBlend(Color32 destination, Color32 source)
    {
        float sourceAlpha = source.a / 255f;
        if (sourceAlpha <= 0f)
        {
            return destination;
        }

        if (sourceAlpha >= 1f)
        {
            return source;
        }

        float destinationAlpha = destination.a / 255f;
        float outputAlpha = sourceAlpha + destinationAlpha * (1f - sourceAlpha);
        if (outputAlpha <= 0f)
        {
            return new Color32(0, 0, 0, 0);
        }

        byte r = (byte)Mathf.RoundToInt(((source.r / 255f * sourceAlpha) + (destination.r / 255f * destinationAlpha * (1f - sourceAlpha))) / outputAlpha * 255f);
        byte g = (byte)Mathf.RoundToInt(((source.g / 255f * sourceAlpha) + (destination.g / 255f * destinationAlpha * (1f - sourceAlpha))) / outputAlpha * 255f);
        byte b = (byte)Mathf.RoundToInt(((source.b / 255f * sourceAlpha) + (destination.b / 255f * destinationAlpha * (1f - sourceAlpha))) / outputAlpha * 255f);
        byte a = (byte)Mathf.RoundToInt(outputAlpha * 255f);
        return new Color32(r, g, b, a);
    }

    private static Texture2D? DuplicateSpriteTexture(Sprite source)
    {
        Texture sourceTexture = source.texture;
        Rect sourceRect = source.textureRect;
        int width = Mathf.Max(1, Mathf.RoundToInt(sourceRect.width));
        int height = Mathf.Max(1, Mathf.RoundToInt(sourceRect.height));
        RenderTexture? active = RenderTexture.active;
        RenderTexture temporary = RenderTexture.GetTemporary(sourceTexture.width, sourceTexture.height, 0, RenderTextureFormat.Default, RenderTextureReadWrite.sRGB);
        try
        {
            Graphics.Blit(sourceTexture, temporary);
            RenderTexture.active = temporary;
            Texture2D texture = new(width, height, TextureFormat.RGBA32, false)
            {
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp
            };
            texture.ReadPixels(sourceRect, 0, 0);
            texture.Apply();
            return texture;
        }
        catch (Exception ex)
        {
            FeedLikeGrandmaPlugin.Log.LogDebug($"Could not duplicate {PrefabName} source icon: {ex.Message}");
            return null;
        }
        finally
        {
            RenderTexture.active = active;
            RenderTexture.ReleaseTemporary(temporary);
        }
    }

    private sealed class CapturedTamePayload
    {
        public string Prefab { get; init; } = string.Empty;
        public string Name { get; init; } = string.Empty;
        public int Level { get; init; }
        public float Health { get; init; }
        public float MaxHealth { get; init; }
        public bool HasSaddle { get; init; }
        public string Inventory { get; init; } = string.Empty;
        public long LastFedAgeTicks { get; init; }
        public long SpawnAgeTicks { get; init; }
        public float StarProgress { get; init; }
        public float GrowthElapsed { get; init; }
        public int CllcEffect { get; init; }
        public string CllcInfusion { get; init; } = string.Empty;
        public string CllcExtraEffect { get; init; } = string.Empty;
        public bool OdinHorseHasArmor { get; init; }
        public float SaddleStamina { get; init; }

        public string Serialize()
        {
            ZPackage pkg = new();
            pkg.Write(PayloadVersion);
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
            return Convert.ToBase64String(pkg.GetArray());
        }

        public static bool TryDeserialize(string text, out CapturedTamePayload? payload, bool logWarnings = true)
        {
            payload = null;
            try
            {
                ZPackage pkg = new(Convert.FromBase64String(text));
                int version = pkg.ReadInt();
                if (version != PayloadVersion)
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

                payload = new CapturedTamePayload
                {
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

}
