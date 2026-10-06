using System.Collections.Generic;
using UnityEngine;

namespace FeedLikeGrandma;

internal static class FeedAutoFeedSystem
{
    private const float ContainerCleanupInterval = 5f;
    private const float DropSurfaceHeightTolerance = 1f;
    private const float MinimumDropSurfaceNormalY = 0.5f;
    private static readonly float[] DropPointAngles = { 0f, 45f, -45f, 90f, -90f, 135f, -135f, 180f };

    private static readonly List<Container> RegisteredFeedContainers = new();
    private static float _nextContainerCleanupTime;
    private static int _dropSurfaceMask;

    internal static void Shutdown()
    {
        RegisteredFeedContainers.Clear();
        _nextContainerCleanupTime = 0f;
    }

    internal static void RegisterContainer(Container? container)
    {
        if (!IsContainerAlive(container) ||
            !FeedBarrelSystem.IsFeedBarrel(container))
        {
            return;
        }

        FeedBarrelSystem.ApplyConfiguredContainerSize(container);
        if (RegisteredFeedContainers.Contains(container!))
        {
            return;
        }

        RegisteredFeedContainers.Add(container!);
    }

    internal static void DeregisterContainer(Container? container)
    {
        if (container == null)
        {
            return;
        }

        RegisteredFeedContainers.Remove(container);
    }

    internal static void RefreshRegisteredContainerSizes()
    {
        CleanupContainers();
        foreach (Container container in RegisteredFeedContainers)
        {
            FeedBarrelSystem.ApplyConfiguredContainerSize(container);
        }
    }

    internal static bool TryFindClosestConsumableFromFeedBarrel(MonsterAI monsterAI, float maxRange, out ItemDrop? consumable)
    {
        consumable = null;
        if (!FeedBarrelSystem.IsEnabled)
        {
            return false;
        }

        if (monsterAI == null || maxRange <= 0f)
        {
            return false;
        }

        if (monsterAI.m_consumeItems == null || monsterAI.m_consumeItems.Count == 0)
        {
            return false;
        }

        CleanupContainersThrottled();
        if (RegisteredFeedContainers.Count == 0)
        {
            return false;
        }

        GameObject animalObject = ((Component)monsterAI).gameObject;
        if (!FeedSystem.HasRuleFor(animalObject))
        {
            return false;
        }

        ZNetView? animalView = animalObject.GetComponent<ZNetView>();
        Tameable? tameable = animalObject.GetComponent<Tameable>();
        if (animalView == null ||
            !animalView.IsValid() ||
            !animalView.IsOwner() ||
            tameable == null ||
            !tameable.IsHungry() ||
            monsterAI.IsAlerted())
        {
            return false;
        }

        Vector3 animalPosition = ((Component)monsterAI).transform.position;
        float sqrRange = maxRange * maxRange;
        Container? bestContainer = null;
        ItemDrop.ItemData? bestItem = null;
        Vector3 bestDropPosition = Vector3.zero;
        float bestDistanceSqr = float.PositiveInfinity;

        foreach (Container container in RegisteredFeedContainers)
        {
            if (!IsEligibleContainer(container, animalPosition, sqrRange))
            {
                continue;
            }

            float distanceSqr = (GetContainerCenter(container) - animalPosition).sqrMagnitude;
            if (distanceSqr >= bestDistanceSqr)
            {
                continue;
            }

            if (!TrySelectFood(monsterAI, container, out ItemDrop.ItemData? item) ||
                item == null)
            {
                continue;
            }

            if (!TryGetReachableDropPoint(monsterAI, container, animalPosition, maxRange, out Vector3 dropPosition))
            {
                continue;
            }

            bestContainer = container;
            bestItem = item;
            bestDropPosition = dropPosition;
            bestDistanceSqr = distanceSqr;
        }

        if (bestContainer != null &&
            bestItem != null &&
            TryRemoveAndDrop(bestContainer, bestItem, bestDropPosition, out consumable) &&
            consumable != null)
        {
            return true;
        }

        return false;
    }

    private static bool TrySelectFood(
        MonsterAI monsterAI,
        Container container,
        out ItemDrop.ItemData? item)
    {
        item = null;
        Inventory? inventory = container.GetInventory();
        if (inventory == null)
        {
            return false;
        }

        if (!FeedSystem.TrySelectAutoFeedItem(monsterAI, inventory.GetAllItems(), out item) || item == null)
        {
            return false;
        }

        return EnsureDropPrefab(item);
    }

    private static bool TryRemoveAndDrop(Container container, ItemDrop.ItemData item, Vector3 dropPosition, out ItemDrop? droppedItem)
    {
        droppedItem = null;
        Inventory? inventory = container.GetInventory();
        ZNetView? containerView = container.NetworkView();
        if (inventory == null || containerView == null || !containerView.IsValid())
        {
            return false;
        }

        if (!containerView.IsOwner())
        {
            containerView.ClaimOwnership();
        }

        if (!containerView.IsOwner())
        {
            return false;
        }

        ItemDrop.ItemData dropData = item.Clone();
        dropData.m_dropPrefab = item.m_dropPrefab;
        if (!inventory.RemoveOneItem(item))
        {
            return false;
        }

        container.Save();
        droppedItem = ItemDrop.DropItem(dropData, 1, dropPosition, ((Component)container).transform.rotation);
        return droppedItem != null;
    }

    private static bool TryGetReachableDropPoint(
        MonsterAI monsterAI,
        Container container,
        Vector3 animalPosition,
        float maxRange,
        out Vector3 dropPosition)
    {
        Vector3 center = GetContainerCenter(container);
        Vector3 closestPoint = container.ContainerPiece() != null
            ? container.ContainerPiece().FindClosestPoint(animalPosition)
            : center;

        Vector3 preferredDirection = HorizontalNormalized(animalPosition - closestPoint);
        if (preferredDirection.sqrMagnitude <= 0f)
        {
            preferredDirection = -((Component)container).transform.forward;
        }

        float radius = Mathf.Max(1.25f, monsterAI.m_consumeRange * 0.75f);
        if (TryUseDropPoint(monsterAI, container, animalPosition, closestPoint + preferredDirection * radius, maxRange, out dropPosition))
        {
            return true;
        }

        float sampleRadius = Mathf.Max(1.5f, monsterAI.m_consumeRange + 0.5f);
        foreach (float angle in DropPointAngles)
        {
            Vector3 direction = Quaternion.Euler(0f, angle, 0f) * preferredDirection;
            if (TryUseDropPoint(monsterAI, container, animalPosition, center + direction * sampleRadius, maxRange, out dropPosition))
            {
                return true;
            }
        }

        dropPosition = Vector3.zero;
        return false;
    }

    private static bool TryUseDropPoint(MonsterAI monsterAI, Container container, Vector3 animalPosition, Vector3 point, float maxRange, out Vector3 dropPosition)
    {
        dropPosition = Vector3.zero;
        if (!TryGetDropSurface(container, animalPosition, point, out point))
        {
            return false;
        }

        Vector3 offset = point - animalPosition;
        if (offset.sqrMagnitude > maxRange * maxRange || !monsterAI.HavePath(point))
        {
            return false;
        }

        dropPosition = point + Vector3.up * 0.25f;
        return true;
    }

    private static Vector3 GetContainerCenter(Container container)
    {
        if (container.ContainerPiece() != null)
        {
            return container.ContainerPiece().GetCenter();
        }

        return ((Component)container).transform.position;
    }

    private static Vector3 HorizontalNormalized(Vector3 vector)
    {
        vector.y = 0f;
        return vector.sqrMagnitude > 0.0001f ? vector.normalized : Vector3.zero;
    }

    private static bool TryGetDropSurface(Container container, Vector3 animalPosition, Vector3 point, out Vector3 surfacePoint)
    {
        surfacePoint = Vector3.zero;
        if (_dropSurfaceMask == 0)
        {
            _dropSurfaceMask = LayerMask.GetMask("Default", "static_solid", "Default_small", "piece", "terrain");
        }

        // Keep the probe near the animal's feet: terrain-only height queries put food
        // below raised floors, while a ray from high above can select an upper roof.
        point.y = animalPosition.y + DropSurfaceHeightTolerance;
        if (!Physics.Raycast(point, Vector3.down, out RaycastHit hit, DropSurfaceHeightTolerance * 2f,
                _dropSurfaceMask, QueryTriggerInteraction.Ignore) ||
            hit.collider == null ||
            hit.collider.attachedRigidbody != null ||
            hit.collider.transform.IsChildOf(((Component)container).transform) ||
            hit.normal.y < MinimumDropSurfaceNormalY)
        {
            return false;
        }

        // Do not skip an obstructing first hit and select a floor underneath it.
        surfacePoint = hit.point;
        return true;
    }

    private static bool IsEligibleContainer(Container container, Vector3 animalPosition, float sqrRange)
    {
        if (!IsContainerObjectValid(container))
        {
            return false;
        }

        Inventory? inventory = container.GetInventory();
        if (inventory == null || inventory.NrOfItems() <= 0 || container.IsInUse())
        {
            return false;
        }

        if (!FeedBarrelSystem.IsEnabled || !FeedBarrelSystem.IsFeedBarrel(container))
        {
            return false;
        }

        Vector3 offset = GetContainerCenter(container) - animalPosition;
        return offset.sqrMagnitude <= sqrRange;
    }

    private static bool IsContainerAlive(Container? container)
    {
        return container != null;
    }

    private static bool IsContainerObjectValid(Container? container)
    {
        if (!IsContainerAlive(container))
        {
            return false;
        }

        ZNetView? nview = container!.NetworkView();
        if (nview == null ||
            !nview.IsValid() ||
            container!.GetInventory() == null)
        {
            return false;
        }

        long creator = nview.GetZDO().GetLong(ZDOVars.s_creator, 0L);
        return creator != 0L;
    }

    private static bool EnsureDropPrefab(ItemDrop.ItemData item)
    {
        if (item.m_dropPrefab != null)
        {
            return true;
        }

        if ((object)ObjectDB.instance == null || !ObjectDB.instance.TryGetItemPrefab(item.m_shared, out GameObject prefab))
        {
            return false;
        }

        item.m_dropPrefab = prefab;
        return true;
    }

    private static void CleanupContainersThrottled()
    {
        if (RegisteredFeedContainers.Count == 0 || Time.time < _nextContainerCleanupTime)
        {
            return;
        }

        _nextContainerCleanupTime = Time.time + ContainerCleanupInterval;
        CleanupContainers();
    }

    private static void CleanupContainers()
    {
        RegisteredFeedContainers.RemoveAll(container => !IsContainerAlive(container) || !FeedBarrelSystem.IsFeedBarrel(container));
    }
}
