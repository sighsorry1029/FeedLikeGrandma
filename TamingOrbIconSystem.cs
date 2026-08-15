using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using UnityEngine;

namespace FeedLikeGrandma;

internal static partial class TamingOrbSystem
{
    private const int IconRenderLayer = 31;
    private const int CreatureIconSize = 128;
    private const float CreatureIconOverlayScale = 0.78f;
    private const int CreatureIconBorderClearPixels = 2;
    private const string IconCacheRevision = "raw-v4-composite78-border2";
    private const string IconCacheMagic = "FLGICON";
    private const int IconCacheFormatVersion = 1;

    private static readonly Quaternion CreatureIconRotation = Quaternion.Euler(23f, 51f, 25.8f);
    private static readonly Dictionary<ItemDrop.ItemData.SharedData, Dictionary<string, int>> CreatureIconVariants = new(new SharedDataReferenceComparer());
    private static readonly HashSet<string> CreatureIconFailures = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<Type, List<Type>> IconComponentDependencies = new();

    private static Camera? _iconCamera;
    private static Light? _iconLight;

    private static void ShutdownIconPipeline()
    {
        CreatureIconVariants.Clear();
        CreatureIconFailures.Clear();
        IconComponentDependencies.Clear();
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
    }

    private static void ResetIconFailures()
    {
        CreatureIconFailures.Clear();
    }

    private static void ResetIconVariants(ItemDrop.ItemData.SharedData shared)
    {
        CreatureIconVariants.Remove(shared);
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
}
