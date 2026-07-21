using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using BepInEx.Bootstrap;
using UnityEngine;

namespace FeedLikeGrandma;

internal static class FeedReferenceSections
{
    internal const string VanillaOwnerName = "Valheim";
    internal const string UnknownOwnerName = "Unknown / Untracked";

    private sealed class GroupedPrefab
    {
        public GameObject Prefab { get; init; } = null!;
        public string PrefabName { get; init; } = "";
        public string OwnerName { get; init; } = UnknownOwnerName;
    }

    internal static void AppendPrefabSections(
        StringBuilder builder,
        IEnumerable<GameObject> prefabs,
        Action<StringBuilder, GameObject> appendEntry)
    {
        FeedAssetOwnerCatalog.RefreshMappings();
        List<IGrouping<string, GroupedPrefab>> sections = prefabs
            .Where(prefab => prefab != null)
            .Select(prefab =>
            {
                string prefabName = (prefab.name ?? "").Trim();
                string ownerName = FeedPrefabOwnerResolver.GetOwnerName(prefabName);
                return new GroupedPrefab
                {
                    Prefab = prefab,
                    PrefabName = prefabName,
                    OwnerName = string.IsNullOrWhiteSpace(ownerName) ? UnknownOwnerName : ownerName.Trim()
                };
            })
            .OrderBy(entry => GetOwnerSortBucket(entry.OwnerName))
            .ThenBy(entry => entry.OwnerName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(entry => entry.PrefabName, StringComparer.OrdinalIgnoreCase)
            .GroupBy(entry => entry.OwnerName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        bool wroteSection = false;
        foreach (IGrouping<string, GroupedPrefab> section in sections)
        {
            if (wroteSection)
            {
                builder.AppendLine();
            }

            AppendSectionHeaderComment(builder, section.Key);

            bool wroteEntry = false;
            foreach (GroupedPrefab entry in section)
            {
                if (wroteEntry)
                {
                    builder.AppendLine();
                }

                appendEntry(builder, entry.Prefab);
                wroteEntry = true;
            }

            wroteSection = true;
        }
    }

    private static void AppendSectionHeaderComment(StringBuilder builder, string ownerName)
    {
        builder.Append("# ===== ");
        builder.Append(string.IsNullOrWhiteSpace(ownerName) ? UnknownOwnerName : ownerName.Trim());
        builder.AppendLine(" =====");
    }

    private static int GetOwnerSortBucket(string ownerName)
    {
        if (string.Equals(ownerName, VanillaOwnerName, StringComparison.OrdinalIgnoreCase))
        {
            return 0;
        }

        return string.Equals(ownerName, UnknownOwnerName, StringComparison.OrdinalIgnoreCase) ? 2 : 1;
    }
}

internal static class FeedPrefabOwnerResolver
{
    internal static string GetOwnerName(string? prefabName)
    {
        string normalizedName = NormalizeName(prefabName);
        if (normalizedName.Length == 0)
        {
            return FeedReferenceSections.UnknownOwnerName;
        }

        foreach (string candidate in EnumerateLookupCandidates(normalizedName))
        {
            if (FeedVanillaPrefabCatalog.IsVanilla(candidate))
            {
                return FeedReferenceSections.VanillaOwnerName;
            }

            if (FeedExternalReferenceSupport.TryGetOwnerName(candidate, out string externalOwnerName))
            {
                return externalOwnerName;
            }
        }

        return FeedAssetOwnerCatalog.GetOwnerName(normalizedName);
    }

    private static IEnumerable<string> EnumerateLookupCandidates(string normalizedName)
    {
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
        AddIfNew(normalizedName);

        int aliasSeparatorIndex = normalizedName.IndexOf(':');
        if (aliasSeparatorIndex > 0)
        {
            AddIfNew(normalizedName.Substring(0, aliasSeparatorIndex));
        }

        foreach (string candidate in seen)
        {
            yield return candidate;
        }

        void AddIfNew(string candidate)
        {
            string normalizedCandidate = NormalizeName(candidate);
            if (normalizedCandidate.Length > 0)
            {
                seen.Add(normalizedCandidate);
            }
        }
    }

    private static string NormalizeName(string? name)
    {
        return (name ?? "").Replace("(Clone)", "").Trim();
    }

}

internal static class FeedVanillaPrefabCatalog
{
    private enum CatalogState
    {
        Uninitialized,
        Loaded,
        Unavailable
    }

    private static readonly object Sync = new();
    private static readonly HashSet<string> PrefabNames = new(StringComparer.OrdinalIgnoreCase);
    private static CatalogState State;

    internal static bool IsVanilla(string prefabName)
    {
        EnsureLoaded();
        return State == CatalogState.Loaded &&
               !string.IsNullOrWhiteSpace(prefabName) &&
               PrefabNames.Contains(prefabName);
    }

    private static void EnsureLoaded()
    {
        if (State != CatalogState.Uninitialized)
        {
            return;
        }

        lock (Sync)
        {
            if (State != CatalogState.Uninitialized)
            {
                return;
            }

            string manifestPath = Path.Combine(Application.dataPath, "StreamingAssets", "SoftRef", "manifest_extended");
            if (!File.Exists(manifestPath))
            {
                State = CatalogState.Unavailable;
                FeedLikeGrandmaPlugin.Log.LogWarning($"Vanilla prefab manifest was not found at '{manifestPath}'. Reference owner sections may place vanilla prefabs under '{FeedReferenceSections.UnknownOwnerName}'.");
                return;
            }

            const string marker = "path in bundle:";
            foreach (string rawLine in File.ReadLines(manifestPath))
            {
                int markerIndex = rawLine.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
                if (markerIndex < 0)
                {
                    continue;
                }

                string assetPath = rawLine.Substring(markerIndex + marker.Length).Trim();
                if (!assetPath.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                string prefabName = Path.GetFileNameWithoutExtension(assetPath);
                if (!string.IsNullOrWhiteSpace(prefabName))
                {
                    PrefabNames.Add(prefabName);
                }
            }

            State = CatalogState.Loaded;
            FeedLikeGrandmaPlugin.Log.LogDebug($"Loaded {PrefabNames.Count} vanilla prefab names for reference owner sections.");
        }
    }
}

internal static class FeedAssetOwnerCatalog
{
    private sealed class PluginResourceSnapshot
    {
        public string OwnerName { get; init; } = "";
        public string PluginName { get; init; } = "";
        public string PluginGuid { get; init; } = "";
        public string AssemblyName { get; init; } = "";
        public string[] ResourceNames { get; init; } = Array.Empty<string>();
    }

    private static readonly object Sync = new();
    private static readonly Dictionary<string, string> AssetOwners = new(StringComparer.OrdinalIgnoreCase);
    private static string LoadedSignature = "";

    internal static string GetOwnerName(string assetName)
    {
        if (LoadedSignature.Length == 0)
        {
            RefreshMappings();
        }

        foreach (string candidate in EnumerateLookupCandidates(assetName))
        {
            if (AssetOwners.TryGetValue(candidate, out string ownerName) &&
                !string.IsNullOrWhiteSpace(ownerName))
            {
                return ownerName;
            }
        }

        return FeedReferenceSections.UnknownOwnerName;
    }

    internal static void RefreshMappings()
    {
        string signature = BuildSignature();
        if (string.Equals(signature, LoadedSignature, StringComparison.Ordinal))
        {
            return;
        }

        lock (Sync)
        {
            if (string.Equals(signature, LoadedSignature, StringComparison.Ordinal))
            {
                return;
            }

            AssetOwners.Clear();
            List<PluginResourceSnapshot> plugins = GetPluginResources();
            foreach (AssetBundle assetBundle in AssetBundle.GetAllLoadedAssetBundles())
            {
                string bundleName = assetBundle.name ?? "";
                if (bundleName.Length == 0)
                {
                    continue;
                }

                string ownerName = ResolveOwnerName(bundleName, plugins);
                if (string.IsNullOrWhiteSpace(ownerName))
                {
                    continue;
                }

                foreach (string assetPath in assetBundle.GetAllAssetNames())
                {
                    if (!assetPath.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    string assetName = Path.GetFileNameWithoutExtension(assetPath);
                    if (!string.IsNullOrWhiteSpace(assetName))
                    {
                        AssetOwners[assetName] = ownerName;
                    }
                }
            }

            LoadedSignature = signature;
            FeedLikeGrandmaPlugin.Log.LogDebug($"Tracked {AssetOwners.Count} prefab owner mapping(s) for reference sections.");
        }
    }

    private static IEnumerable<string> EnumerateLookupCandidates(string assetName)
    {
        string normalizedName = (assetName ?? "").Replace("(Clone)", "").Trim();
        if (normalizedName.Length == 0)
        {
            yield break;
        }

        yield return normalizedName;
        int aliasSeparatorIndex = normalizedName.IndexOf(':');
        if (aliasSeparatorIndex > 0)
        {
            yield return normalizedName.Substring(0, aliasSeparatorIndex);
        }
    }

    private static List<PluginResourceSnapshot> GetPluginResources()
    {
        return Chainloader.PluginInfos.Values
            .Select(pluginInfo =>
            {
                string pluginName = (pluginInfo.Metadata.Name ?? "").Trim();
                string pluginGuid = (pluginInfo.Metadata.GUID ?? "").Trim();
                string assemblyName = "";
                string[] resourceNames = Array.Empty<string>();
                try
                {
                    assemblyName = pluginInfo.Instance?.GetType().Assembly.GetName().Name ?? "";
                    resourceNames = pluginInfo.Instance?.GetType().Assembly.GetManifestResourceNames() ?? Array.Empty<string>();
                }
                catch
                {
                    // Plugin assemblies can be partially initialized while ObjectDB is being copied.
                }

                return new PluginResourceSnapshot
                {
                    OwnerName = pluginName.Length > 0 ? pluginName : pluginGuid,
                    PluginName = pluginName,
                    PluginGuid = pluginGuid,
                    AssemblyName = assemblyName,
                    ResourceNames = resourceNames
                };
            })
            .Where(plugin => plugin.OwnerName.Length > 0)
            .ToList();
    }

    private static string ResolveOwnerName(string bundleName, List<PluginResourceSnapshot> plugins)
    {
        PluginResourceSnapshot? embeddedOwner = plugins.FirstOrDefault(plugin =>
            plugin.ResourceNames.Any(resourceName =>
                resourceName.EndsWith(bundleName, StringComparison.OrdinalIgnoreCase)));
        if (embeddedOwner != null)
        {
            return embeddedOwner.OwnerName;
        }

        string normalizedBundleName = NormalizeToken(Path.GetFileNameWithoutExtension(bundleName));
        if (normalizedBundleName.Length == 0)
        {
            return "";
        }

        PluginResourceSnapshot? tokenOwner = plugins.FirstOrDefault(plugin =>
        {
            string normalizedPluginName = NormalizeToken(plugin.PluginName);
            string normalizedPluginGuid = NormalizeToken(plugin.PluginGuid);
            string normalizedAssemblyName = NormalizeToken(plugin.AssemblyName);
            return IsTokenMatch(normalizedBundleName, normalizedPluginName) ||
                   IsTokenMatch(normalizedBundleName, normalizedPluginGuid) ||
                   IsTokenMatch(normalizedBundleName, normalizedAssemblyName);
        });

        return tokenOwner?.OwnerName ?? "";
    }

    private static bool IsTokenMatch(string bundleName, string pluginToken)
    {
        return pluginToken.Length > 0 &&
               (bundleName.IndexOf(pluginToken, StringComparison.OrdinalIgnoreCase) >= 0 ||
                pluginToken.IndexOf(bundleName, StringComparison.OrdinalIgnoreCase) >= 0);
    }

    private static string NormalizeToken(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "";
        }

        StringBuilder builder = new();
        foreach (char character in value)
        {
            if (char.IsLetterOrDigit(character))
            {
                builder.Append(char.ToLowerInvariant(character));
            }
        }

        return builder.ToString();
    }

    private static string BuildSignature()
    {
        IEnumerable<string> bundleTokens = AssetBundle.GetAllLoadedAssetBundles()
            .Select(bundle => bundle.name ?? "")
            .Where(name => name.Length > 0)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase);
        IEnumerable<string> pluginTokens = Chainloader.PluginInfos.Values
            .Select(pluginInfo =>
            {
                string pluginName = pluginInfo.Metadata.Name ?? "";
                string pluginGuid = pluginInfo.Metadata.GUID ?? "";
                string assemblyName = "";
                try
                {
                    assemblyName = pluginInfo.Instance?.GetType().Assembly.GetName().Name ?? "";
                }
                catch
                {
                    // The signature only needs stable ownership inputs.
                }

                return $"{pluginGuid}:{pluginName}:{assemblyName}";
            })
            .OrderBy(token => token, StringComparer.OrdinalIgnoreCase);

        return string.Join("|", bundleTokens) + "||" + string.Join("|", pluginTokens);
    }
}
