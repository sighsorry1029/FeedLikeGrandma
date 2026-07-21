using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using BepInEx;
using BepInEx.Bootstrap;
using BepInEx.Configuration;
using UnityEngine;

namespace FeedLikeGrandma;

internal static class FeedExternalReferenceSupport
{
    private enum ProviderKind
    {
        CreatureManagerTemplate,
        JotunnCreatureManager
    }

    internal sealed class ExternalFeedProjection
    {
        public GameObject Prefab { get; init; } = null!;
        public string PrefabName { get; init; } = "";
        public string OwnerName { get; init; } = "";
        public List<string> ConsumeItemNames { get; init; } = new();
    }

    private sealed class ProviderHandle
    {
        public ProviderKind Kind { get; init; }
        public string AssemblyName { get; init; } = "";
        public string OwnerName { get; init; } = "";
        public PropertyInfo? ManagerInstanceProperty { get; init; }
        public FieldInfo? ManagerCreaturesField { get; init; }
        public PropertyInfo? ManagerCreaturesProperty { get; init; }
        public FieldInfo? RegisteredCreaturesField { get; init; }
        public FieldInfo? CreatureConfigsField { get; init; }
    }

    private sealed class ProjectionSnapshot
    {
        public List<ExternalFeedProjection> Projections { get; init; } = new();
        public Dictionary<string, ExternalFeedProjection> ByPrefabName { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private static readonly object Sync = new();
    private static string _providerAssemblySignature = "";
    private static List<ProviderHandle> _providerCache = new();
    private static string _snapshotAssemblySignature = "";
    private static string _snapshotDataSignature = "";
    private static ProjectionSnapshot? _snapshotCache;

    internal static void RefreshSnapshot()
    {
        Assembly[] assemblies = AppDomain.CurrentDomain.GetAssemblies()
            .Where(assembly => !assembly.IsDynamic)
            .ToArray();
        string assemblySignature = BuildAssemblySignature(assemblies);

        lock (Sync)
        {
            List<ProviderHandle> providers = GetProviders(assemblies, assemblySignature);
            List<ExternalFeedProjection> collectedProjections = providers
                .SelectMany(CollectProjections)
                .Where(projection => (object?)projection.Prefab != null && !string.IsNullOrWhiteSpace(projection.PrefabName))
                .ToList();
            string dataSignature = BuildProjectionSignature(collectedProjections);
            if (_snapshotCache != null &&
                string.Equals(_snapshotAssemblySignature, assemblySignature, StringComparison.Ordinal) &&
                string.Equals(_snapshotDataSignature, dataSignature, StringComparison.Ordinal))
            {
                return;
            }

            Dictionary<string, ExternalFeedProjection> merged = new(StringComparer.OrdinalIgnoreCase);
            foreach (ExternalFeedProjection projection in collectedProjections)
            {
                if (!merged.TryGetValue(projection.PrefabName, out ExternalFeedProjection? existing))
                {
                    merged[projection.PrefabName] = projection;
                    continue;
                }

                List<string> consumeItemNames = existing.ConsumeItemNames
                    .Concat(projection.ConsumeItemNames)
                    .Where(name => !string.IsNullOrWhiteSpace(name))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                merged[projection.PrefabName] = new ExternalFeedProjection
                {
                    Prefab = existing.Prefab,
                    PrefabName = existing.PrefabName,
                    OwnerName = string.IsNullOrWhiteSpace(existing.OwnerName) ? projection.OwnerName : existing.OwnerName,
                    ConsumeItemNames = consumeItemNames
                };
            }

            _snapshotAssemblySignature = assemblySignature;
            _snapshotDataSignature = dataSignature;
            _snapshotCache = new ProjectionSnapshot
            {
                Projections = merged.Values
                    .OrderBy(projection => projection.PrefabName, StringComparer.OrdinalIgnoreCase)
                    .ToList(),
                ByPrefabName = merged
            };
        }
    }

    internal static void Shutdown()
    {
        lock (Sync)
        {
            _providerAssemblySignature = "";
            _providerCache.Clear();
            _snapshotAssemblySignature = "";
            _snapshotDataSignature = "";
            _snapshotCache = null;
        }
    }

    internal static IEnumerable<GameObject> GetExternalPrefabs()
    {
        return GetSnapshot().Projections
            .Select(projection => projection.Prefab)
            .Where(prefab => prefab != null);
    }

    internal static List<string> GetConfiguredConsumeItemNames(GameObject prefab)
    {
        if ((object?)prefab == null || string.IsNullOrWhiteSpace(prefab.name))
        {
            return new List<string>();
        }

        return GetSnapshot().ByPrefabName.TryGetValue(prefab.name, out ExternalFeedProjection? projection)
            ? projection.ConsumeItemNames.ToList()
            : new List<string>();
    }

    internal static List<ItemDrop> ResolveConfiguredConsumeItems(GameObject prefab)
    {
        List<ItemDrop> itemDrops = new();
        if ((object?)ObjectDB.instance == null)
        {
            return itemDrops;
        }

        foreach (string itemName in GetConfiguredConsumeItemNames(prefab))
        {
            GameObject? itemPrefab = ObjectDB.instance.GetItemPrefab(itemName);
            ItemDrop? itemDrop = itemPrefab == null ? null : itemPrefab.GetComponent<ItemDrop>();
            if ((object?)itemDrop != null)
            {
                itemDrops.Add(itemDrop);
            }
        }

        return itemDrops;
    }

    internal static bool TryGetOwnerName(string prefabName, out string ownerName)
    {
        ownerName = "";
        if (string.IsNullOrWhiteSpace(prefabName))
        {
            return false;
        }

        if (!GetSnapshot().ByPrefabName.TryGetValue(prefabName, out ExternalFeedProjection? projection) ||
            string.IsNullOrWhiteSpace(projection.OwnerName))
        {
            return false;
        }

        ownerName = projection.OwnerName;
        return true;
    }

    internal static string ExtractItemDropPrefabName(ItemDrop? itemDrop)
    {
        if ((object?)itemDrop == null)
        {
            return "";
        }

        GameObject? dropPrefab = itemDrop.m_itemData?.m_dropPrefab;
        if ((object?)dropPrefab != null && !string.IsNullOrWhiteSpace(dropPrefab.name))
        {
            return NormalizePrefabName(dropPrefab.name);
        }

        return NormalizePrefabName(((Component)itemDrop).gameObject.name);
    }

    private static ProjectionSnapshot GetSnapshot()
    {
        if (_snapshotCache == null)
        {
            RefreshSnapshot();
        }

        return _snapshotCache ?? new ProjectionSnapshot();
    }

    private static List<ProviderHandle> GetProviders(Assembly[] assemblies, string assemblySignature)
    {
        if (string.Equals(_providerAssemblySignature, assemblySignature, StringComparison.Ordinal))
        {
            return _providerCache;
        }

        List<ProviderHandle> providers = new();
        foreach (Assembly assembly in assemblies)
        {
            Type? creatureType = SafeGetType(assembly, "CreatureManager.Creature");
            if (creatureType == null)
            {
                continue;
            }

            FieldInfo? registeredCreaturesField = TryGetTypeField(creatureType, "registeredCreatures");
            if (registeredCreaturesField == null)
            {
                continue;
            }

            providers.Add(new ProviderHandle
            {
                Kind = ProviderKind.CreatureManagerTemplate,
                AssemblyName = assembly.GetName().Name ?? assembly.FullName ?? FeedReferenceSections.UnknownOwnerName,
                OwnerName = ResolveAssemblyOwnerName(assembly),
                RegisteredCreaturesField = registeredCreaturesField,
                CreatureConfigsField = TryGetTypeField(creatureType, "creatureConfigs")
            });
        }

        ProviderHandle? jotunnProvider = TryCreateJotunnCreatureProvider(assemblies);
        if (jotunnProvider != null)
        {
            providers.Add(jotunnProvider);
        }

        _providerAssemblySignature = assemblySignature;
        _providerCache = providers;
        return _providerCache;
    }

    private static ProviderHandle? TryCreateJotunnCreatureProvider(IEnumerable<Assembly> assemblies)
    {
        foreach (Assembly assembly in assemblies)
        {
            Type? managerType = SafeGetType(assembly, "Jotunn.Managers.CreatureManager");
            Type? creatureType = SafeGetType(assembly, "Jotunn.Entities.CustomCreature");
            if (managerType == null || creatureType == null)
            {
                continue;
            }

            PropertyInfo? instanceProperty = TryGetTypeProperty(managerType, "Instance");
            FieldInfo? creaturesField = TryGetTypeField(managerType, "Creatures");
            PropertyInfo? creaturesProperty = TryGetTypeProperty(managerType, "Creatures");
            if (instanceProperty == null || (creaturesField == null && creaturesProperty == null))
            {
                continue;
            }

            return new ProviderHandle
            {
                Kind = ProviderKind.JotunnCreatureManager,
                AssemblyName = assembly.GetName().Name ?? assembly.FullName ?? "Jotunn",
                OwnerName = ResolveAssemblyOwnerName(assembly),
                ManagerInstanceProperty = instanceProperty,
                ManagerCreaturesField = creaturesField,
                ManagerCreaturesProperty = creaturesProperty
            };
        }

        return null;
    }

    private static IEnumerable<ExternalFeedProjection> CollectProjections(ProviderHandle provider)
    {
        return provider.Kind == ProviderKind.JotunnCreatureManager
            ? CollectJotunnCreatureProjections(provider)
            : CollectCreatureManagerTemplateProjections(provider);
    }

    private static IEnumerable<ExternalFeedProjection> CollectJotunnCreatureProjections(ProviderHandle provider)
    {
        object? managerInstance = provider.ManagerInstanceProperty?.GetValue(null, null);
        if (managerInstance == null)
        {
            yield break;
        }

        object? creaturesValue = provider.ManagerCreaturesField?.GetValue(managerInstance) ??
                                 provider.ManagerCreaturesProperty?.GetValue(managerInstance, null);
        foreach (object? creature in GetEnumerable(creaturesValue))
        {
            if (creature == null ||
                !TryGetRawMemberValue(creature, "Prefab", out object? prefabValue) ||
                prefabValue is not GameObject prefab ||
                string.IsNullOrWhiteSpace(prefab.name))
            {
                continue;
            }

            yield return new ExternalFeedProjection
            {
                Prefab = prefab,
                PrefabName = NormalizePrefabName(prefab.name),
                OwnerName = ResolveJotunnOwnerName(creature, provider.OwnerName),
                ConsumeItemNames = ExtractConsumeItemNames(prefab)
            };
        }
    }

    private static IEnumerable<ExternalFeedProjection> CollectCreatureManagerTemplateProjections(ProviderHandle provider)
    {
        IEnumerable creatures = GetEnumerable(provider.RegisteredCreaturesField?.GetValue(null));
        IDictionary? creatureConfigs = provider.CreatureConfigsField?.GetValue(null) as IDictionary;

        foreach (object? creature in creatures)
        {
            if (creature == null ||
                !TryGetRawMemberValue(creature, "Prefab", out object? prefabValue) ||
                prefabValue is not GameObject prefab ||
                string.IsNullOrWhiteSpace(prefab.name))
            {
                continue;
            }

            object? creatureConfig = null;
            if (creatureConfigs != null && creatureConfigs.Contains(creature))
            {
                creatureConfig = creatureConfigs[creature];
            }

            List<string> consumeItemNames = ReadCreatureManagerConsumeNames(creature, creatureConfig);
            if (consumeItemNames.Count == 0)
            {
                consumeItemNames = ExtractConsumeItemNames(prefab);
            }

            yield return new ExternalFeedProjection
            {
                Prefab = prefab,
                PrefabName = NormalizePrefabName(prefab.name),
                OwnerName = string.IsNullOrWhiteSpace(provider.OwnerName) ? provider.AssemblyName : provider.OwnerName,
                ConsumeItemNames = consumeItemNames
            };
        }
    }

    private static List<string> ReadCreatureManagerConsumeNames(object creature, object? creatureConfig)
    {
        if (TryGetWrappedMemberValue(creatureConfig, "ConsumesItemName", out object? configuredValue) &&
            configuredValue is string configuredText)
        {
            List<string> configuredNames = SplitConsumeItemNames(configuredText);
            if (configuredNames.Count > 0)
            {
                return configuredNames;
            }
        }

        if (TryGetWrappedMemberValue(creature, "ConsumesItemName", out object? directConfigValue) &&
            directConfigValue is string directConfigText)
        {
            List<string> directConfigNames = SplitConsumeItemNames(directConfigText);
            if (directConfigNames.Count > 0)
            {
                return directConfigNames;
            }
        }

        if (TryGetRawMemberValue(creature, "FoodItems", out object? foodItemsValue))
        {
            return SplitConsumeItemNames(foodItemsValue?.ToString());
        }

        return new List<string>();
    }

    private static List<string> ExtractConsumeItemNames(GameObject prefab)
    {
        MonsterAI? monsterAI = prefab.GetComponent<MonsterAI>();
        if ((object?)monsterAI == null || monsterAI.m_consumeItems == null)
        {
            return new List<string>();
        }

        return monsterAI.m_consumeItems
            .Where(item => (object?)item != null)
            .Select(ExtractItemDropPrefabName)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static List<string> SplitConsumeItemNames(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return new List<string>();
        }

        string text = value!;
        return text.Split(',')
            .Select(NormalizePrefabName)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string ResolveJotunnOwnerName(object creature, string fallback)
    {
        if (TryGetRawMemberValue(creature, "SourceMod", out object? sourceMod) && sourceMod != null)
        {
            if (TryGetRawMemberValue(sourceMod, "GUID", out object? guidValue))
            {
                string guid = NormalizePrefabName(guidValue?.ToString());
                if (guid.Length > 0 && Chainloader.PluginInfos.TryGetValue(guid, out PluginInfo pluginInfo))
                {
                    string pluginName = NormalizePrefabName(pluginInfo.Metadata.Name);
                    return pluginName.Length > 0 ? pluginName : guid;
                }

                if (guid.Length > 0)
                {
                    return guid;
                }
            }

            if (TryGetRawMemberValue(sourceMod, "Name", out object? nameValue))
            {
                string sourceName = NormalizePrefabName(nameValue?.ToString());
                if (sourceName.Length > 0)
                {
                    return sourceName;
                }
            }
        }

        return fallback;
    }

    private static string ResolveAssemblyOwnerName(Assembly assembly)
    {
        PluginInfo? pluginInfo = Chainloader.PluginInfos.Values.FirstOrDefault(info =>
            info.Instance != null && info.Instance.GetType().Assembly == assembly);
        if (pluginInfo != null)
        {
            string pluginName = NormalizePrefabName(pluginInfo.Metadata.Name);
            if (pluginName.Length > 0)
            {
                return pluginName;
            }

            string pluginGuid = NormalizePrefabName(pluginInfo.Metadata.GUID);
            if (pluginGuid.Length > 0)
            {
                return pluginGuid;
            }
        }

        return assembly.GetName().Name ?? FeedReferenceSections.UnknownOwnerName;
    }

    private static bool TryGetWrappedMemberValue(object? instance, string memberName, out object? value)
    {
        value = null;
        if (!TryGetRawMemberValue(instance, memberName, out object? rawValue))
        {
            return false;
        }

        value = UnwrapConfigValue(rawValue);
        return true;
    }

    private static bool TryGetRawMemberValue(object? instance, string memberName, out object? value)
    {
        value = null;
        if (instance == null || string.IsNullOrWhiteSpace(memberName))
        {
            return false;
        }

        const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
        Type type = instance.GetType();

        FieldInfo? field = type.GetField(memberName, flags) ??
                           type.GetFields(flags).FirstOrDefault(candidate =>
                               string.Equals(candidate.Name, memberName, StringComparison.OrdinalIgnoreCase));
        if (field != null)
        {
            value = field.GetValue(instance);
            return true;
        }

        PropertyInfo? property = type.GetProperty(memberName, flags) ??
                                 type.GetProperties(flags).FirstOrDefault(candidate =>
                                     string.Equals(candidate.Name, memberName, StringComparison.OrdinalIgnoreCase));
        if (property == null || !property.CanRead)
        {
            return false;
        }

        value = property.GetValue(instance, null);
        return true;
    }

    private static object? UnwrapConfigValue(object? value)
    {
        object? current = value;
        for (int depth = 0; depth < 5 && current != null; depth++)
        {
            if (current is string || current.GetType().IsPrimitive || current.GetType().IsEnum)
            {
                return current;
            }

            if (current is ConfigEntryBase configEntry)
            {
                return configEntry.BoxedValue;
            }

            if (current is Delegate directDelegate)
            {
                try
                {
                    current = directDelegate.DynamicInvoke();
                    continue;
                }
                catch
                {
                    break;
                }
            }

            if (TryGetRawMemberValue(current, "config", out object? configValue) && configValue != null)
            {
                current = configValue;
                continue;
            }

            if (TryGetRawMemberValue(current, "get", out object? getterValue) && getterValue is Delegate getter)
            {
                try
                {
                    current = getter.DynamicInvoke();
                    continue;
                }
                catch
                {
                    break;
                }
            }

            MethodInfo? getMethod = current.GetType().GetMethod("get", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance, null, Type.EmptyTypes, null);
            if (getMethod != null && getMethod.ReturnType != typeof(void))
            {
                current = getMethod.Invoke(current, null);
                continue;
            }

            PropertyInfo? boxedValueProperty = current.GetType().GetProperty("BoxedValue", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (boxedValueProperty != null && boxedValueProperty.CanRead)
            {
                current = boxedValueProperty.GetValue(current, null);
                continue;
            }

            PropertyInfo? valueProperty = current.GetType().GetProperty("Value", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (valueProperty != null && valueProperty.CanRead)
            {
                current = valueProperty.GetValue(current, null);
                continue;
            }

            break;
        }

        return current;
    }

    private static IEnumerable GetEnumerable(object? value)
    {
        if (value is string || value is not IEnumerable enumerable)
        {
            return Array.Empty<object>();
        }

        return enumerable;
    }

    private static FieldInfo? TryGetTypeField(Type type, string fieldName)
    {
        const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
        return type.GetField(fieldName, flags) ??
               type.GetFields(flags).FirstOrDefault(field =>
                   string.Equals(field.Name, fieldName, StringComparison.OrdinalIgnoreCase));
    }

    private static PropertyInfo? TryGetTypeProperty(Type type, string propertyName)
    {
        const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
        return type.GetProperty(propertyName, flags) ??
               type.GetProperties(flags).FirstOrDefault(property =>
                   string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase));
    }

    private static Type? SafeGetType(Assembly assembly, string typeName)
    {
        try
        {
            return assembly.GetType(typeName, throwOnError: false);
        }
        catch
        {
            return null;
        }
    }

    private static string BuildAssemblySignature(IEnumerable<Assembly> assemblies)
    {
        return BuildStableHash(assemblies
            .Select(assembly => assembly.FullName ?? assembly.GetName().Name ?? "")
            .Where(token => token.Length > 0));
    }

    private static string BuildProjectionSignature(IEnumerable<ExternalFeedProjection> projections)
    {
        return BuildStableHash(projections.Select(projection =>
        {
            int prefabInstanceId = projection.Prefab == null ? 0 : projection.Prefab.GetInstanceID();
            return $"{projection.PrefabName}|{projection.OwnerName}|{prefabInstanceId}|{string.Join(",", projection.ConsumeItemNames)}";
        }));
    }

    private static string BuildStableHash(IEnumerable<string> tokens)
    {
        unchecked
        {
            int hash = 17;
            foreach (string token in tokens.OrderBy(token => token, StringComparer.Ordinal))
            {
                hash = (hash * 31) + StringComparer.Ordinal.GetHashCode(token);
            }

            return hash.ToString("x8");
        }
    }

    private static string NormalizePrefabName(string? name)
    {
        string normalized = (name ?? "").Replace("(Clone)", "").Trim();
        bool wasMockName = false;
        if (normalized.StartsWith("JVLmock_", StringComparison.Ordinal))
        {
            normalized = normalized.Substring("JVLmock_".Length);
            wasMockName = true;
        }
        else if (normalized.StartsWith("VLmock_", StringComparison.Ordinal))
        {
            normalized = normalized.Substring("VLmock_".Length);
            wasMockName = true;
        }

        int separatorIndex = normalized.IndexOf("__", StringComparison.Ordinal);
        if (wasMockName && separatorIndex > 0)
        {
            normalized = normalized.Substring(0, separatorIndex);
        }

        return normalized.Trim();
    }
}
