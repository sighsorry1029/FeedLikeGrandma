using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using UnityEngine;
using UnityEngine.Rendering;

namespace FeedLikeGrandma;

internal static class FeedBarrelSystem
{
    internal const string PrefabName = "FLG_FeedBarrel";
    private const string SourcePrefabName = "piece_chest_barrel";
    private const string EctoplasmMaterialName = "ectoplasm_mat";
    private const int DefaultColumns = 6;
    private const int DefaultRows = 2;
    private const int MinColumns = 6;
    private const int MaxColumns = 8;
    private const int MinRows = 2;
    private const int MaxRows = 100;
    private const int IconSize = 128;
    private const int IconLayer = 30;

    private static GameObject? _prefab;
    private static GameObject? _prefabContainer;
    private static readonly Dictionary<string, Material> MaterialLookupCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> MaterialLookupMissCache = new(StringComparer.OrdinalIgnoreCase);
    private static List<RendererMaterialSnapshot>? _originalMaterials;
    private static Sprite? _originalIcon;
    private static Sprite? _ectoplasmIcon;
    private static bool _materialLookupCacheBuilt;

    internal static void OnGameDataChanged()
    {
        MaterialLookupCache.Clear();
        MaterialLookupMissCache.Clear();
        _materialLookupCacheBuilt = false;
    }

    internal static bool IsEnabled => !string.IsNullOrWhiteSpace(FeedLikeGrandmaPlugin.FeedBarrelRecipe.Value);

    internal static void RegisterPrefabsAndPieces(ObjectDB? objectDb = null)
    {
        GameObject? prefab = EnsurePrefab();
        if ((object?)prefab == null)
        {
            return;
        }

        ConfigurePrefab(prefab, objectDb ?? ObjectDB.instance);

        if ((object?)ZNetScene.instance != null)
        {
            RegisterWithZNetScene(ZNetScene.instance, prefab);
        }

        RegisterWithHammer(objectDb ?? ObjectDB.instance, prefab);
        FeedAutoFeedSystem.RefreshRegisteredContainerSizes();
    }

    internal static bool IsFeedBarrel(Container? container)
    {
        if (container == null)
        {
            return false;
        }

        return IsFeedBarrel(((Component)container).gameObject);
    }

    internal static bool IsFeedBarrel(GameObject? gameObject)
    {
        if (gameObject == null)
        {
            return false;
        }

        return string.Equals(Utils.GetPrefabName(gameObject), PrefabName, StringComparison.Ordinal);
    }

    private static GameObject? EnsurePrefab()
    {
        if ((object?)_prefab != null)
        {
            return _prefab;
        }

        GameObject? existing = FindScenePrefab(PrefabName);
        if ((object?)existing != null)
        {
            _prefab = existing;
            return _prefab;
        }

        GameObject? source = FindScenePrefab(SourcePrefabName);
        if ((object?)source == null)
        {
            return null;
        }

        GameObject prefab = UnityEngine.Object.Instantiate(source, EnsurePrefabContainer().transform);
        prefab.name = PrefabName;

        if ((object?)prefab.GetComponent<Piece>() == null ||
            (object?)prefab.GetComponent<Container>() == null ||
            (object?)prefab.GetComponent<ZNetView>() == null)
        {
            FeedLikeGrandmaPlugin.Log.LogWarning($"Could not create {PrefabName}: {SourcePrefabName} did not have the expected Piece, Container, and ZNetView components.");
            UnityEngine.Object.Destroy(prefab);
            return null;
        }

        _prefab = prefab;
        return _prefab;
    }

    private static void ConfigurePrefab(GameObject prefab, ObjectDB? objectDb)
    {
        Piece? piece = prefab.GetComponent<Piece>();
        if ((object?)piece != null)
        {
            bool enabled = IsEnabled;
            piece.m_name = FeedLocalization.FeedBarrelNameToken;
            piece.m_description = FeedLocalization.FeedBarrelDescriptionToken;
            piece.m_enabled = enabled;
            piece.m_category = Piece.PieceCategory.Misc;
            StoreOriginalIcon(piece);
            StoreOriginalMaterials(prefab);
            ApplyConfiguredMaterial(prefab, piece);

            if ((object?)objectDb != null)
            {
                piece.m_resources = enabled
                    ? BuildRequirements(objectDb, FeedLikeGrandmaPlugin.FeedBarrelRecipe.Value).ToArray()
                    : Array.Empty<Piece.Requirement>();
            }
        }

        Container? container = prefab.GetComponent<Container>();
        if ((object?)container != null)
        {
            container.m_name = FeedLocalization.FeedBarrelNameToken;
            ApplyConfiguredContainerSize(container);
        }
    }

    internal static void ApplyConfiguredContainerSize(Container? container)
    {
        if ((object?)container == null || !IsFeedBarrel(container))
        {
            return;
        }

        GetConfiguredContainerSize(out int columns, out int rows);
        container!.m_width = columns;
        container.m_height = rows;

        Inventory? inventory = container.GetInventory();
        if (inventory == null)
        {
            return;
        }

        inventory.m_width = columns;
        inventory.m_height = rows;
    }

    private static void ApplyConfiguredMaterial(GameObject prefab, Piece piece)
    {
        RestoreOriginalMaterials();
        piece.m_icon = _originalIcon;

        if (FeedLikeGrandmaPlugin.FeedBarrelMaterialStyle.Value != FeedLikeGrandmaPlugin.FeedBarrelMaterial.Ectoplasm)
        {
            return;
        }

        Material? material = ResolveMaterial(EctoplasmMaterialName);
        if ((object?)material == null)
        {
            FeedLikeGrandmaPlugin.Log.LogWarning($"{PrefabName} material '{EctoplasmMaterialName}' was not found. Using default barrel material.");
            return;
        }

        ApplyMaterialOverride(prefab, material);
        _ectoplasmIcon ??= RenderPieceIcon(prefab);
        if ((object?)_ectoplasmIcon != null)
        {
            piece.m_icon = _ectoplasmIcon;
        }
    }

    private static void GetConfiguredContainerSize(out int columns, out int rows)
    {
        columns = FeedLikeGrandmaPlugin.FeedBarrelContainerColumns != null
            ? FeedLikeGrandmaPlugin.FeedBarrelContainerColumns.Value
            : DefaultColumns;
        rows = FeedLikeGrandmaPlugin.FeedBarrelContainerRows != null
            ? FeedLikeGrandmaPlugin.FeedBarrelContainerRows.Value
            : DefaultRows;
        columns = Mathf.Clamp(columns, MinColumns, MaxColumns);
        rows = Mathf.Clamp(rows, MinRows, MaxRows);
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

    private static void RegisterWithHammer(ObjectDB? objectDb, GameObject prefab)
    {
        if ((object?)objectDb == null)
        {
            return;
        }

        GameObject? hammerPrefab = objectDb.GetItemPrefab("Hammer");
        ItemDrop? hammer = hammerPrefab == null ? null : hammerPrefab.GetComponent<ItemDrop>();
        PieceTable? pieceTable = hammer?.m_itemData.m_shared.m_buildPieces;
        if ((object?)pieceTable == null)
        {
            return;
        }

        pieceTable.m_pieces.RemoveAll(existing => existing == null || existing.name == PrefabName);
        if (!IsEnabled)
        {
            RefreshLocalBuildTable(pieceTable);
            return;
        }

        pieceTable.m_pieces.Add(prefab);
        RefreshLocalBuildTable(pieceTable);
    }

    private static void RefreshLocalBuildTable(PieceTable pieceTable)
    {
        if ((object?)Player.m_localPlayer == null || (object?)Player.m_localPlayer.m_buildPieces == null)
        {
            return;
        }

        if (Player.m_localPlayer.m_buildPieces == pieceTable)
        {
            ((Humanoid)Player.m_localPlayer).SetPlaceMode(pieceTable);
        }
    }

    private static IEnumerable<Piece.Requirement> BuildRequirements(ObjectDB objectDb, string recipeText)
    {
        if (string.IsNullOrWhiteSpace(recipeText))
        {
            yield break;
        }

        foreach (string rawPart in recipeText.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries))
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
                FeedLikeGrandmaPlugin.Log.LogWarning($"{PrefabName} build ingredient '{itemName}' was not found.");
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

    private static GameObject? FindItemPrefab(ObjectDB objectDb, string itemName)
    {
        GameObject? prefab = objectDb.GetItemPrefab(itemName);
        if ((object?)prefab != null)
        {
            return prefab;
        }

        int hash = itemName.GetStableHashCode();
        return objectDb.m_itemByHash.TryGetValue(hash, out GameObject hashedPrefab) ? hashedPrefab : null;
    }

    private static GameObject? FindScenePrefab(string prefabName)
    {
        if ((object?)ZNetScene.instance == null)
        {
            return null;
        }

        GameObject? prefab = ZNetScene.instance.GetPrefab(prefabName);
        if ((object?)prefab != null)
        {
            return prefab;
        }

        int hash = prefabName.GetStableHashCode();
        return ZNetScene.instance.m_namedPrefabs.TryGetValue(hash, out GameObject hashedPrefab) ? hashedPrefab : null;
    }

    private static void StoreOriginalIcon(Piece piece)
    {
        _originalIcon ??= piece.m_icon;
    }

    private static void StoreOriginalMaterials(GameObject prefab)
    {
        if (_originalMaterials != null)
        {
            return;
        }

        _originalMaterials = GetVisualRenderers(prefab)
            .Select(renderer => new RendererMaterialSnapshot(renderer, renderer.sharedMaterials.ToArray()))
            .ToList();
    }

    private static void RestoreOriginalMaterials()
    {
        if (_originalMaterials == null)
        {
            return;
        }

        foreach (RendererMaterialSnapshot snapshot in _originalMaterials)
        {
            if ((object?)snapshot.Renderer != null)
            {
                snapshot.Renderer.sharedMaterials = snapshot.Materials.ToArray();
            }
        }
    }

    private static void ApplyMaterialOverride(GameObject prefab, Material material)
    {
        foreach (Renderer renderer in GetVisualRenderers(prefab))
        {
            Material[] materials = renderer.sharedMaterials;
            if (materials.Length == 0)
            {
                continue;
            }

            Material[] updatedMaterials = new Material[materials.Length];
            for (int index = 0; index < updatedMaterials.Length; index++)
            {
                updatedMaterials[index] = material;
            }

            renderer.sharedMaterials = updatedMaterials;
        }
    }

    private static Material? ResolveMaterial(string materialName)
    {
        string normalizedName = NormalizeMaterialName(materialName);
        if (normalizedName.Length == 0 || MaterialLookupMissCache.Contains(normalizedName))
        {
            return null;
        }

        EnsureMaterialLookupCache(force: false);
        if (TryResolveCachedMaterial(materialName, normalizedName, out Material? material))
        {
            return material;
        }

        EnsureMaterialLookupCache(force: true);
        if (TryResolveCachedMaterial(materialName, normalizedName, out material))
        {
            return material;
        }

        MaterialLookupMissCache.Add(normalizedName);
        return null;
    }

    private static bool TryResolveCachedMaterial(string materialName, string normalizedName, out Material? material)
    {
        if (MaterialLookupCache.TryGetValue(materialName, out Material exactMaterial) && (object?)exactMaterial != null)
        {
            material = exactMaterial;
            return true;
        }

        if (MaterialLookupCache.TryGetValue(normalizedName, out Material normalizedMaterial) && (object?)normalizedMaterial != null)
        {
            material = normalizedMaterial;
            return true;
        }

        material = null;
        return false;
    }

    private static void EnsureMaterialLookupCache(bool force)
    {
        if (_materialLookupCacheBuilt && !force)
        {
            return;
        }

        MaterialLookupCache.Clear();
        MaterialLookupMissCache.Clear();
        foreach (Material material in Resources.FindObjectsOfTypeAll<Material>())
        {
            if ((object?)material == null || string.IsNullOrWhiteSpace(material.name))
            {
                continue;
            }

            if (!MaterialLookupCache.ContainsKey(material.name))
            {
                MaterialLookupCache[material.name] = material;
            }

            string normalizedName = NormalizeMaterialName(material.name);
            if (normalizedName.Length > 0 && !MaterialLookupCache.ContainsKey(normalizedName))
            {
                MaterialLookupCache[normalizedName] = material;
            }
        }

        _materialLookupCacheBuilt = true;
    }

    private static string NormalizeMaterialName(string name)
    {
        return (name ?? string.Empty)
            .Replace(" (Instance)", string.Empty)
            .Trim();
    }

    private static Sprite? RenderPieceIcon(GameObject sourcePrefab)
    {
        if (SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null)
        {
            return null;
        }

        GameObject? renderObject = null;
        Camera? camera = null;
        Light? light = null;
        RenderTexture? renderTexture = null;
        RenderTexture? previousActive = RenderTexture.active;

        try
        {
            renderObject = SpawnIconRenderClone(sourcePrefab, out List<Renderer> renderers);
            if ((object?)renderObject == null || renderers.Count == 0)
            {
                return null;
            }

            Bounds bounds = renderers[0].bounds;
            foreach (Renderer renderer in renderers.Skip(1))
            {
                bounds.Encapsulate(renderer.bounds);
            }

            Vector3 renderSize = bounds.size;
            renderObject.transform.position -= bounds.center;

            camera = new GameObject("FeedLikeGrandma Feed Barrel Icon Camera", typeof(Camera)).GetComponent<Camera>();
            camera.backgroundColor = Color.clear;
            camera.clearFlags = CameraClearFlags.Color;
            camera.fieldOfView = 0.5f;
            camera.farClipPlane = 10000000f;
            camera.cullingMask = 1 << IconLayer;
            camera.transform.rotation = Quaternion.Euler(0f, 180f, 0f);

            light = new GameObject("FeedLikeGrandma Feed Barrel Icon Light", typeof(Light)).GetComponent<Light>();
            light.transform.position = Vector3.zero;
            light.transform.rotation = Quaternion.Euler(5f, 180f, 5f);
            light.type = LightType.Directional;
            light.cullingMask = 1 << IconLayer;
            light.intensity = 1.3f;

            float framedSize = Mathf.Max(renderSize.x, renderSize.y) + 0.1f;
            float distance = framedSize / Mathf.Tan(camera.fieldOfView * ((float)Math.PI / 180f));
            if (float.IsNaN(distance) || float.IsInfinity(distance) || distance <= 0f)
            {
                distance = Mathf.Max(renderSize.x, Mathf.Max(renderSize.y, renderSize.z)) + 1f;
            }

            camera.transform.position = new Vector3(0f, 0f, distance);

            renderTexture = RenderTexture.GetTemporary(IconSize, IconSize, 24, RenderTextureFormat.ARGB32);
            camera.targetTexture = renderTexture;
            RenderTexture.active = renderTexture;
            GL.Clear(clearDepth: true, clearColor: true, Color.clear);
            camera.Render();

            Texture2D texture = new(IconSize, IconSize, TextureFormat.RGBA32, mipChain: false)
            {
                name = $"{PrefabName}_Icon"
            };
            Rect rect = new(0f, 0f, IconSize, IconSize);
            texture.ReadPixels(rect, 0, 0);
            texture.Apply();

            Sprite sprite = Sprite.Create(texture, rect, new Vector2(0.5f, 0.5f), 100f);
            sprite.name = texture.name;
            return sprite;
        }
        catch (Exception ex)
        {
            FeedLikeGrandmaPlugin.Log.LogWarning($"{PrefabName} icon render failed: {ex.Message}");
            return null;
        }
        finally
        {
            RenderTexture.active = previousActive;
            if ((object?)camera != null)
            {
                camera.targetTexture = null;
                DestroyTemporaryObject(camera.gameObject);
            }

            if ((object?)light != null)
            {
                DestroyTemporaryObject(light.gameObject);
            }

            if (renderTexture != null)
            {
                RenderTexture.ReleaseTemporary(renderTexture);
            }

            if ((object?)renderObject != null)
            {
                DestroyTemporaryObject(renderObject);
            }
        }
    }

    private static GameObject? SpawnIconRenderClone(GameObject sourcePrefab, out List<Renderer> renderers)
    {
        renderers = new List<Renderer>();
        GameObject? inactiveRoot = null;
        try
        {
            inactiveRoot = new GameObject($"{PrefabName}_IconRoot");
            inactiveRoot.SetActive(false);

            GameObject renderObject = UnityEngine.Object.Instantiate(sourcePrefab, inactiveRoot.transform, worldPositionStays: false);
            renderObject.name = $"{PrefabName}_IconRender";
            StripIconRenderClone(renderObject);
            renderObject.transform.SetParent(null, worldPositionStays: false);
            UnityEngine.Object.DestroyImmediate(inactiveRoot);
            inactiveRoot = null;

            renderObject.transform.position = Vector3.zero;
            renderObject.transform.rotation = Quaternion.Euler(23f, 51f, 25.8f);
            SetLayerRecursive(renderObject, IconLayer);
            renderObject.SetActive(true);

            renderers = GetVisualRenderers(renderObject)
                .Where(renderer => renderer.gameObject.activeInHierarchy)
                .ToList();
            if (renderers.Count == 0)
            {
                DestroyTemporaryObject(renderObject);
                return null;
            }

            HashSet<Renderer> selected = new(renderers);
            foreach (Renderer renderer in renderObject.GetComponentsInChildren<Renderer>(includeInactive: true))
            {
                if ((object?)renderer != null && !selected.Contains(renderer))
                {
                    renderer.enabled = false;
                }
            }

            return renderObject;
        }
        catch
        {
            if ((object?)inactiveRoot != null)
            {
                UnityEngine.Object.DestroyImmediate(inactiveRoot);
            }

            throw;
        }
    }

    private static void StripIconRenderClone(GameObject renderObject)
    {
        foreach (Transform transform in renderObject.GetComponentsInChildren<Transform>(includeInactive: true))
        {
            Component[] components = transform.GetComponents<Component>();
            for (int index = components.Length - 1; index >= 0; index--)
            {
                Component component = components[index];
                if (component == null ||
                    component is Transform ||
                    component is MeshFilter ||
                    component is MeshRenderer ||
                    component is SkinnedMeshRenderer)
                {
                    continue;
                }

                try
                {
                    UnityEngine.Object.DestroyImmediate(component);
                }
                catch (Exception ex)
                {
                    FeedLikeGrandmaPlugin.Log.LogDebug($"Could not strip icon render component '{component.GetType().Name}' from '{renderObject.name}': {ex.Message}");
                }
            }
        }
    }

    private static List<Renderer> GetVisualRenderers(GameObject prefab)
    {
        return prefab
            .GetComponentsInChildren<Renderer>(includeInactive: true)
            .Where(renderer => renderer != null && renderer.enabled && !renderer.GetType().Name.Equals("ParticleSystemRenderer", StringComparison.Ordinal))
            .ToList();
    }

    private static void SetLayerRecursive(GameObject gameObject, int layer)
    {
        gameObject.layer = layer;
        foreach (Transform child in gameObject.transform)
        {
            SetLayerRecursive(child.gameObject, layer);
        }
    }

    private static void DestroyTemporaryObject(GameObject? gameObject)
    {
        if ((object?)gameObject == null)
        {
            return;
        }

        try
        {
            gameObject.SetActive(false);
            UnityEngine.Object.DestroyImmediate(gameObject);
        }
        catch (Exception ex)
        {
            FeedLikeGrandmaPlugin.Log.LogDebug($"Could not immediately destroy icon render object '{gameObject.name}': {ex.Message}");
            UnityEngine.Object.Destroy(gameObject);
        }
    }

    private static GameObject EnsurePrefabContainer()
    {
        if ((object?)_prefabContainer != null)
        {
            return _prefabContainer;
        }

        _prefabContainer = new GameObject("FeedLikeGrandma Feed Barrel Prefabs");
        _prefabContainer.SetActive(false);
        UnityEngine.Object.DontDestroyOnLoad(_prefabContainer);
        return _prefabContainer;
    }

    private sealed class RendererMaterialSnapshot
    {
        public RendererMaterialSnapshot(Renderer renderer, Material[] materials)
        {
            Renderer = renderer;
            Materials = materials;
        }

        public Renderer Renderer { get; }
        public Material[] Materials { get; }
    }
}
