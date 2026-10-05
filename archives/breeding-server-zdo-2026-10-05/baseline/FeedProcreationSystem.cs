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
            (object?)procreation.NetworkView() == null ||
            !procreation.NetworkView().IsValid() ||
            !procreation.NetworkView().IsOwner() ||
            (object?)procreation.TameableComponent() == null ||
            !procreation.TameableComponent().IsTamed() ||
            procreation.IsPregnant())
        {
            return true;
        }

        if (!TryEnsureProcreationPrefabs(procreation) ||
            (object?)procreation.OwnPrefab() == null ||
            (object?)procreation.OffspringPrefab() == null)
        {
            return true;
        }

        Vector3 center = ((Component)procreation).transform.position;
        int adultCount = CountPrefabInstancesXZ(procreation.OwnPrefab(), center, procreation.m_totalCheckRange);
        int offspringCount = CountPrefabInstancesXZ(procreation.OffspringPrefab(), center, procreation.m_totalCheckRange);
        return adultCount + offspringCount < procreation.m_maxCreatures;
    }

    private static bool TryEnsureProcreationPrefabs(Procreation procreation)
    {
        if ((object?)procreation.OwnPrefab() != null && (object?)procreation.OffspringPrefab() != null)
        {
            return true;
        }

        if ((object?)ZNetScene.instance == null ||
            (object?)procreation.NetworkView() == null ||
            !procreation.NetworkView().IsValid())
        {
            return false;
        }

        if ((object?)procreation.OffspringPrefab() == null)
        {
            if ((object?)procreation.m_offspring == null)
            {
                return false;
            }

            string prefabName = Utils.GetPrefabName(procreation.m_offspring);
            procreation.OffspringPrefab() = ZNetScene.instance.GetPrefab(prefabName);
        }

        if ((object?)procreation.OwnPrefab() == null)
        {
            int prefabHash = procreation.NetworkView().GetZDO().GetPrefab();
            procreation.OwnPrefab() = ZNetScene.instance.GetPrefab(prefabHash);
        }

        return (object?)procreation.OwnPrefab() != null && (object?)procreation.OffspringPrefab() != null;
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
        if ((object?)ZNetScene.instance != null && ZNetScene.instance.Instances() != null)
        {
            foreach (ZNetView instance in ZNetScene.instance.Instances().Values)
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

        string prefabName = Utils.GetPrefabName(prefab.name);
        return string.IsNullOrWhiteSpace(prefabName)
            ? 0
            : StringExtensionMethods.GetStableHashCode(prefabName);
    }
}
