using System;
using System.Collections.Generic;
using UnityEngine;

namespace FeedLikeGrandma;

internal static class FeedProcreationSystem
{
    private static readonly HashSet<int> InterestedPrefabHashes = new();
    private static readonly Dictionary<int, HashSet<ZNetView>> InstancesByPrefabHash = new();

    internal static void ResetRuntimeCache()
    {
        InterestedPrefabHashes.Clear();
        InstancesByPrefabHash.Clear();
    }

    internal static void Shutdown()
    {
        ResetRuntimeCache();
    }

    internal static void RegisterSpawnedInstance(ZNetView? nview)
    {
        if (InterestedPrefabHashes.Count == 0 ||
            !TryGetSpawnedInstancePrefabHash(nview, out int prefabHash) ||
            !InterestedPrefabHashes.Contains(prefabHash))
        {
            return;
        }

        if (!InstancesByPrefabHash.TryGetValue(prefabHash, out HashSet<ZNetView>? instances))
        {
            instances = new HashSet<ZNetView>();
            InstancesByPrefabHash[prefabHash] = instances;
        }

        instances.Add(nview!);
    }

    internal static void DeregisterSpawnedInstance(ZNetView? nview)
    {
        if ((object?)nview == null || InstancesByPrefabHash.Count == 0)
        {
            return;
        }

        if (TryGetSpawnedInstancePrefabHash(nview, out int prefabHash) &&
            InstancesByPrefabHash.TryGetValue(prefabHash, out HashSet<ZNetView>? directInstances))
        {
            directInstances.Remove(nview);
            return;
        }

        foreach (HashSet<ZNetView> instances in InstancesByPrefabHash.Values)
        {
            instances.Remove(nview);
        }
    }

    internal static bool ShouldRunProcreation(Procreation procreation)
    {
        if (FeedLikeGrandmaPlugin.HorizontalProcreationLimit.Value != FeedLikeGrandmaPlugin.Toggle.On)
        {
            return true;
        }

        if ((object?)procreation == null ||
            (object?)procreation.m_nview == null ||
            !procreation.m_nview.IsValid() ||
            !procreation.m_nview.IsOwner() ||
            (object?)procreation.m_tameable == null ||
            !procreation.m_tameable.IsTamed() ||
            procreation.IsPregnant())
        {
            return true;
        }

        if (!TryEnsureProcreationPrefabs(procreation) ||
            (object?)procreation.m_myPrefab == null ||
            (object?)procreation.m_offspringPrefab == null)
        {
            return true;
        }

        Vector3 center = ((Component)procreation).transform.position;
        int adultCount = CountPrefabInstancesXZ(procreation.m_myPrefab, center, procreation.m_totalCheckRange);
        int offspringCount = CountPrefabInstancesXZ(procreation.m_offspringPrefab, center, procreation.m_totalCheckRange);
        return adultCount + offspringCount < procreation.m_maxCreatures;
    }

    private static bool TryEnsureProcreationPrefabs(Procreation procreation)
    {
        if ((object?)procreation.m_myPrefab != null && (object?)procreation.m_offspringPrefab != null)
        {
            return true;
        }

        if ((object?)ZNetScene.instance == null ||
            (object?)procreation.m_nview == null ||
            !procreation.m_nview.IsValid())
        {
            return false;
        }

        if ((object?)procreation.m_offspringPrefab == null)
        {
            if ((object?)procreation.m_offspring == null)
            {
                return false;
            }

            string prefabName = Utils.GetPrefabName(procreation.m_offspring);
            procreation.m_offspringPrefab = ZNetScene.instance.GetPrefab(prefabName);
        }

        if ((object?)procreation.m_myPrefab == null)
        {
            int prefabHash = procreation.m_nview.GetZDO().GetPrefab();
            procreation.m_myPrefab = ZNetScene.instance.GetPrefab(prefabHash);
        }

        return (object?)procreation.m_myPrefab != null && (object?)procreation.m_offspringPrefab != null;
    }

    private static int CountPrefabInstancesXZ(GameObject prefab, Vector3 center, float maxRange)
    {
        int prefabHash = GetPrefabHash(prefab);
        if (prefabHash == 0)
        {
            return 0;
        }

        EnsureInstanceInterest(prefabHash);
        if (!InstancesByPrefabHash.TryGetValue(prefabHash, out HashSet<ZNetView>? instances) ||
            instances.Count == 0)
        {
            return 0;
        }

        instances.RemoveWhere(view => !IsCachedInstanceValid(view, prefabHash));

        int count = 0;
        foreach (ZNetView instance in instances)
        {
            if (maxRange <= 0f || Utils.DistanceXZ(instance.transform.position, center) <= maxRange)
            {
                count++;
            }
        }

        return count;
    }

    private static void EnsureInstanceInterest(int prefabHash)
    {
        if (prefabHash == 0 || !InterestedPrefabHashes.Add(prefabHash))
        {
            return;
        }

        HashSet<ZNetView> instances = new();
        if ((object?)ZNetScene.instance != null && ZNetScene.instance.m_instances != null)
        {
            foreach (ZNetView instance in ZNetScene.instance.m_instances.Values)
            {
                if (IsCachedInstanceValid(instance, prefabHash))
                {
                    instances.Add(instance);
                }
            }
        }

        InstancesByPrefabHash[prefabHash] = instances;
    }

    private static bool IsCachedInstanceValid(ZNetView? nview, int prefabHash)
    {
        return TryGetSpawnedInstancePrefabHash(nview, out int instancePrefabHash) &&
               instancePrefabHash == prefabHash;
    }

    private static bool TryGetSpawnedInstancePrefabHash(ZNetView? nview, out int prefabHash)
    {
        prefabHash = 0;
        if (nview == null || !nview.IsValid())
        {
            return false;
        }

        ZDO zdo = nview.GetZDO();
        if (zdo == null)
        {
            return false;
        }

        prefabHash = zdo.GetPrefab();
        return prefabHash != 0;
    }

    private static int GetPrefabHash(GameObject prefab)
    {
        if ((object?)prefab == null)
        {
            return 0;
        }

        string prefabName = NormalizePrefabName(prefab.name);
        return string.IsNullOrWhiteSpace(prefabName)
            ? 0
            : StringExtensionMethods.GetStableHashCode(prefabName);
    }

    private static string NormalizePrefabName(string name)
    {
        int parenthesisIndex = name.IndexOf('(');
        int spaceIndex = name.IndexOf(' ');
        int index = parenthesisIndex < 0
            ? spaceIndex
            : spaceIndex < 0 ? parenthesisIndex : Math.Min(parenthesisIndex, spaceIndex);
        return index >= 0 ? name.Substring(0, index) : name;
    }
}
