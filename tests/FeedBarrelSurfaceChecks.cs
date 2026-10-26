using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

// The production source is linked unchanged. These fixtures implement downward
// ray/plane intersections and stock accounting only, not Unity physics, NavMesh,
// game ownership transfer, persistence, or the ItemDrop spawn implementation.
internal static class FeedBarrelSurfaceChecks
{
    private static readonly MethodInfo TryUseDropPoint = typeof(FeedLikeGrandma.FeedAutoFeedSystem)
        .GetMethod("TryUseDropPoint", BindingFlags.Static | BindingFlags.NonPublic)!;
    private static int _passed;

    private static int Main()
    {
        try
        {
            CheckSurfaces();
            CheckObstructions();
            CheckRangeAndPath();
            CheckStockAndTargets();
            CheckCompatibilityBoundary();
            Console.WriteLine("Passed " + _passed + " source-linked Feed Barrel surface checks.");
            Console.WriteLine("Scope: geometric plane fixtures and inventory counters; Unity collider behavior, NavMesh, ownership transport, and actual game feeding were not executed.");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            return 1;
        }
    }

    private static void CheckSurfaces()
    {
        Fixture fixture = NewFixture(0f);
        AddPlane(0f, "terrain");
        Require(TryCandidate(fixture, new Vector3(1.25f, 0f, 0f), 10f, out Vector3 drop), "flat terrain remains usable");
        Require(Near(drop, new Vector3(1.25f, 0.25f, 0f)), "terrain spawn retains quarter-meter clearance");

        fixture = NewFixture(12f);
        AddPlane(0f, "terrain");
        AddPlane(12f, "piece");
        AddPlane(16f, "piece");
        Require(TryCandidate(fixture, new Vector3(1.25f, 0f, 0f), 10f, out drop), "high building floor is usable over distant terrain");
        Require(Near(drop, new Vector3(1.25f, 12.25f, 0f)), "floor height follows animal storey rather than candidate height or roof");
        Require(Near(fixture.Animal.LastPathPoint, new Vector3(1.25f, 12f, 0f)), "path checks the actual floor before adding drop clearance");

        fixture = NewFixture(12f);
        AddPlane(8f, "piece");
        AddPlane(0f, "terrain");
        Require(!TryCandidate(fixture, new Vector3(1.25f, 12f, 0f), 20f, out drop), "floor gap does not fall back to lower storey or terrain");
        Require(Near(drop, Vector3.zero) && fixture.Animal.PathCalls == 0, "unsupported point yields no target and no path request");

        fixture = NewFixture(12f);
        AddPlane(12f, "piece", halfWidth: 0.5f);
        Require(!TryCandidate(fixture, new Vector3(1.25f, 12f, 0f), 10f, out _), "candidate outside finite floor bounds is a hole");

        fixture = NewFixture(12f);
        AddPlane(12f, "piece", slopeX: 0.5f);
        Require(TryCandidate(fixture, new Vector3(1.25f, 12f, 0f), 10f, out drop), "local sloping floor within one meter is accepted");
        Require(Near(drop, new Vector3(1.25f, 12.875f, 0f)), "slope ray intersection supplies surface height");

        foreach (float delta in new[] { -0.99f, 0.99f, -1.01f, 1.01f })
        {
            fixture = NewFixture(12f);
            AddPlane(12f + delta, "piece");
            bool found = TryCandidate(fixture, new Vector3(1.25f, 12f, 0f), 10f, out drop);
            Require(found == (Math.Abs(delta) < 1f), "surface tolerance boundary at animal height " + delta.ToString("+0.00;-0.00", System.Globalization.CultureInfo.InvariantCulture));
        }

        foreach (string layer in new[] { "Default", "static_solid", "Default_small", "piece", "terrain" })
        {
            fixture = NewFixture(12f);
            AddPlane(12f, layer);
            Require(TryCandidate(fixture, new Vector3(1.25f, 12f, 0f), 10f, out _), "support layer included: " + layer);
        }
    }

    private static void CheckObstructions()
    {
        Fixture fixture = NewFixture(12f);
        AddPlane(12f, "piece");
        AddPlane(12.75f, "piece", trigger: true);
        AddPlane(12.5f, "character");
        Require(TryCandidate(fixture, new Vector3(1.25f, 12f, 0f), 10f, out Vector3 drop) && Near(drop.y, 12.25f),
            "triggers and excluded character layer do not replace the floor");

        fixture = NewFixture(12f);
        AddPlane(12.5f, "piece", trigger: true);
        Require(!TryCandidate(fixture, new Vector3(1.25f, 12f, 0f), 10f, out _), "trigger alone provides no support");

        fixture = NewFixture(12f);
        AddPlane(12f, "piece");
        AddPlane(12.5f, "piece").Collider.transform.parent = fixture.Barrel.transform;
        Require(!TryCandidate(fixture, new Vector3(1.25f, 12f, 0f), 10f, out _), "barrel child collider blocks candidate instead of dropping through barrel");
        Require(fixture.Animal.PathCalls == 0, "rejected first obstacle does not ask for a path to floor below it");

        fixture = NewFixture(12f);
        AddPlane(12f, "piece");
        AddPlane(12.5f, "piece").Collider.attachedRigidbody = new Rigidbody();
        Require(!TryCandidate(fixture, new Vector3(1.25f, 12f, 0f), 10f, out _), "dynamic first hit blocks candidate instead of falling through to floor");

        fixture = NewFixture(12f);
        AddPlane(12f, "piece");
        AddPlane(10f, "piece", slopeX: 2f);
        Require(!TryCandidate(fixture, new Vector3(1.25f, 12f, 0f), 10f, out _), "steep first hit blocks candidate instead of falling through to floor");
    }

    private static void CheckRangeAndPath()
    {
        Fixture fixture = NewFixture(12f);
        AddPlane(12f, "piece");
        fixture.Animal.PathAllowed = false;
        Require(!TryCandidate(fixture, new Vector3(1.25f, 12f, 0f), 10f, out Vector3 drop), "existing path failure still rejects supported point");
        Require(fixture.Animal.PathCalls == 1 && Near(drop, Vector3.zero), "path failure produces no drop position");

        fixture = NewFixture(12f);
        AddPlane(12f, "piece");
        Require(!TryCandidate(fixture, new Vector3(1.25f, 12f, 0f), 1f, out drop), "existing distance limit still rejects supported point");
        Require(fixture.Animal.PathCalls == 0 && Near(drop, Vector3.zero), "out-of-range point avoids path work and returns no target");
    }

    private static void CheckStockAndTargets()
    {
        Fixture fixture = NewFixture(12f);
        AddPlane(0f, "terrain");
        AddPlane(8f, "piece");
        Require(!FindFood(fixture, out ItemDrop? target), "all unsupported barrel candidates fail the public feeding lookup");
        RequireNoStockChange(fixture, target, "unsupported floor");

        fixture = NewFixture(12f);
        AddPlane(12f, "piece");
        fixture.Animal.PathAllowed = false;
        Require(!FindFood(fixture, out target), "all unreachable barrel candidates fail the public feeding lookup");
        RequireNoStockChange(fixture, target, "no path");

        fixture = NewFixture(12f);
        AddPlane(12f, "piece");
        fixture.Animal.gameObject.GetComponent<ZNetView>()!.Owner = false;
        Require(!FindFood(fixture, out target), "nonowner animal cannot start barrel feeding");
        RequireNoStockChange(fixture, target, "nonowner");
        Require(Physics.RaycastCalls == 0, "nonowner guard runs before surface search");

        fixture = NewFixture(12f);
        AddPlane(12f, "piece");
        AddPlane(0f, "terrain");
        AddPlane(16f, "piece");
        Require(FindFood(fixture, out target), "hungry owned animal receives food from a barrel on a high floor");
        Require(target != null && Near(target.transform.position.y, 12.25f), "public lookup returns food on the animal floor");
        Require(fixture.Food.m_stack == 2 && fixture.Barrel.Inventory.RemoveCalls == 1 && fixture.Barrel.SaveCalls == 1 && ItemDrop.DropCalls == 1,
            "successful public lookup removes, saves, and drops exactly one food");
        Require(fixture.Barrel.Inventory.GetAllItems()[0].m_dropPrefab == fixture.Food.m_dropPrefab && target!.m_itemData.m_stack == 1,
            "drop uses a single-item clone and retains remaining inventory item");
    }

    private static bool FindFood(Fixture fixture, out ItemDrop? target) =>
        FeedLikeGrandma.FeedAutoFeedSystem.TryFindClosestConsumableFromFeedBarrel(fixture.Animal, 10f, out target);

    private static void CheckCompatibilityBoundary()
    {
        Fixture fixture = NewFixture(0f); AddPlane(0f, "terrain");
        FeedLikeGrandma.FeedBarrelDropProtection.Ready = false;
        Require(!FindFood(fixture, out ItemDrop? target), "unavailable merge protection prevents dispensing");
        RequireNoStockChange(fixture, target, "unavailable merge protection");

        fixture = NewFixture(0f); AddPlane(0f, "terrain");
        FeedLikeGrandma.FeedAzuAutoStoreCompatibility.Allowed = false;
        Require(!FindFood(fixture, out target), "incompatible Azu contract prevents dispensing");
        RequireNoStockChange(fixture, target, "incompatible Azu contract");

        fixture = NewFixture(0f); AddPlane(0f, "terrain");
        Require(FindFood(fixture, out target), "ready compatibility retains physical food target");
        Require(FeedLikeGrandma.FeedBarrelDropProtection.MarkCalls == 1 &&
            ReferenceEquals(FeedLikeGrandma.FeedBarrelDropProtection.LastMarked, target), "spawned target is marked exactly once before lookup returns");

        fixture = NewFixture(0f); AddPlane(0f, "terrain");
        FeedLikeGrandma.FeedBarrelDropProtection.MarkSucceeds = false;
        Require(FindFood(fixture, out target) && target != null && fixture.Food.m_stack == 2 &&
            ItemDrop.DropCalls == 1 && fixture.Barrel.Inventory.RemoveCalls == 1,
            "post-spawn marker failure retains one original food without refund or second spawn");
        Require(!FindFood(fixture, out _) && fixture.Food.m_stack == 2 && ItemDrop.DropCalls == 1,
            "latched marker failure prevents additional stock removal on the next attempt");
    }

    private static void RequireNoStockChange(Fixture fixture, ItemDrop? target, string label)
    {
        Require(target == null && fixture.Food.m_stack == 3 && fixture.Barrel.Inventory.RemoveCalls == 0 &&
            fixture.Barrel.SaveCalls == 0 && ItemDrop.DropCalls == 0, label + " leaves stock, save count, spawn count, and target unchanged");
    }

    private static bool TryCandidate(Fixture fixture, Vector3 point, float range, out Vector3 drop)
    {
        object[] args = { fixture.Animal, fixture.Barrel, fixture.Animal.transform.position, point, range, Vector3.zero };
        bool result = (bool)TryUseDropPoint.Invoke(null, args)!;
        drop = (Vector3)args[5];
        return result;
    }

    private static Fixture NewFixture(float height)
    {
        FeedLikeGrandma.FeedAutoFeedSystem.Shutdown();
        Physics.Planes.Clear();
        Physics.RaycastCalls = 0;
        ItemDrop.DropCalls = 0;
        FeedLikeGrandma.FeedBarrelDropProtection.Ready = true;
        FeedLikeGrandma.FeedBarrelDropProtection.MarkSucceeds = true;
        FeedLikeGrandma.FeedBarrelDropProtection.MarkCalls = 0;
        FeedLikeGrandma.FeedBarrelDropProtection.LastMarked = null;
        FeedLikeGrandma.FeedAzuAutoStoreCompatibility.Allowed = true;
        MonsterAI animal = new();
        animal.transform.position = new Vector3(3f, height, 0f);
        animal.gameObject.AddComponent(new ZNetView());
        animal.gameObject.AddComponent(new Tameable());
        animal.m_consumeItems.Add(new ItemDrop());
        Container barrel = new();
        barrel.transform.position = new Vector3(0f, height, 0f);
        ItemDrop.ItemData food = new() { m_stack = 3, m_dropPrefab = new GameObject() };
        barrel.Inventory.Items.Add(food);
        FeedLikeGrandma.FeedAutoFeedSystem.RegisterContainer(barrel);
        return new Fixture(animal, barrel, food);
    }

    private static Physics.Plane AddPlane(float height, string layer, float slopeX = 0f, float halfWidth = float.PositiveInfinity, bool trigger = false)
    {
        Physics.Plane plane = new(height, LayerMask.NameToLayer(layer), slopeX, halfWidth, trigger);
        Physics.Planes.Add(plane);
        return plane;
    }

    private static bool Near(float actual, float expected) => Math.Abs(actual - expected) < 0.0001f;
    private static bool Near(Vector3 actual, Vector3 expected) => (actual - expected).sqrMagnitude < 0.00001f;
    private static void Require(bool condition, string description)
    {
        if (!condition) throw new InvalidOperationException("FAIL: " + description);
        ++_passed;
    }

    private sealed class Fixture
    {
        internal readonly MonsterAI Animal;
        internal readonly Container Barrel;
        internal readonly ItemDrop.ItemData Food;
        internal Fixture(MonsterAI animal, Container barrel, ItemDrop.ItemData food) { Animal = animal; Barrel = barrel; Food = food; }
    }
}

namespace UnityEngine
{
    internal struct Vector3
    {
        public float x, y, z;
        public Vector3(float x, float y, float z) { this.x = x; this.y = y; this.z = z; }
        public static Vector3 zero => new(0f, 0f, 0f);
        public static Vector3 up => new(0f, 1f, 0f);
        public static Vector3 down => new(0f, -1f, 0f);
        public float sqrMagnitude => x * x + y * y + z * z;
        public Vector3 normalized => this * (1f / (float)Math.Sqrt(sqrMagnitude));
        public static Vector3 operator +(Vector3 a, Vector3 b) => new(a.x + b.x, a.y + b.y, a.z + b.z);
        public static Vector3 operator -(Vector3 a, Vector3 b) => a + -b;
        public static Vector3 operator -(Vector3 a) => a * -1f;
        public static Vector3 operator *(Vector3 a, float scale) => new(a.x * scale, a.y * scale, a.z * scale);
    }
    internal struct Quaternion
    {
        private float _yaw;
        public static Quaternion Euler(float x, float y, float z) => new() { _yaw = y * (float)Math.PI / 180f };
        public static Vector3 operator *(Quaternion q, Vector3 v) => new(
            v.x * (float)Math.Cos(q._yaw) + v.z * (float)Math.Sin(q._yaw), v.y,
            -v.x * (float)Math.Sin(q._yaw) + v.z * (float)Math.Cos(q._yaw));
    }
    internal sealed class Transform
    {
        public Vector3 position;
        public Quaternion rotation => default;
        public Vector3 forward => new(0f, 0f, 1f);
        public Transform? parent;
        public bool IsChildOf(Transform other) => ReferenceEquals(this, other) || (parent != null && parent.IsChildOf(other));
    }
    internal sealed class GameObject
    {
        public readonly Transform transform = new();
        private readonly Dictionary<Type, Component> _components = new();
        public T? GetComponent<T>() where T : Component => _components.TryGetValue(typeof(T), out Component component) ? (T)component : null;
        public void AddComponent(Component component) { component.gameObject = this; _components[component.GetType()] = component; }
    }
    internal class Component
    {
        public GameObject gameObject = new();
        public Transform transform => gameObject.transform;
    }
    internal sealed class Rigidbody { }
    internal sealed class Collider : Component { public Rigidbody? attachedRigidbody; }
    internal struct RaycastHit { public Collider? collider; public Vector3 point, normal; public Rigidbody? rigidbody => collider?.attachedRigidbody; public Transform? transform => collider?.transform; }
    internal enum QueryTriggerInteraction { UseGlobal, Ignore, Collide }
    internal static class LayerMask
    {
        private static readonly string[] Names = { "Default", "static_solid", "Default_small", "piece", "terrain", "character" };
        public static int NameToLayer(string name) { int index = Array.IndexOf(Names, name); if (index < 0) throw new ArgumentException(name); return index; }
        public static int GetMask(params string[] names) { int mask = 0; foreach (string name in names) mask |= 1 << NameToLayer(name); return mask; }
    }
    internal static class Physics
    {
        // Axis-aligned patches may have an X slope. The query picks the closest
        // ray intersection, honoring requested layers, distance, and triggers.
        internal sealed class Plane
        {
            internal readonly float Height, SlopeX, HalfWidth;
            internal readonly int Layer;
            internal readonly bool Trigger;
            internal readonly Collider Collider = new();
            internal Plane(float height, int layer, float slopeX, float halfWidth, bool trigger)
            { Height = height; Layer = layer; SlopeX = slopeX; HalfWidth = halfWidth; Trigger = trigger; }
        }
        internal static readonly List<Plane> Planes = new();
        internal static int RaycastCalls;
        public static bool Raycast(Vector3 origin, Vector3 direction, out RaycastHit hit, float maxDistance, int layerMask, QueryTriggerInteraction queryTriggerInteraction)
        {
            ++RaycastCalls;
            if (direction.x != 0f || direction.y != -1f || direction.z != 0f) throw new NotSupportedException("Fixture only supports downward rays.");
            hit = default;
            float nearest = float.PositiveInfinity;
            foreach (Plane plane in Planes)
            {
                if ((layerMask & (1 << plane.Layer)) == 0 || (plane.Trigger && queryTriggerInteraction == QueryTriggerInteraction.Ignore) ||
                    Math.Abs(origin.x) > plane.HalfWidth || Math.Abs(origin.z) > plane.HalfWidth) continue;
                float y = plane.Height + plane.SlopeX * origin.x;
                float distance = origin.y - y;
                if (distance < 0f || distance > maxDistance || distance >= nearest) continue;
                nearest = distance;
                hit = new RaycastHit { collider = plane.Collider, point = new Vector3(origin.x, y, origin.z), normal = new Vector3(-plane.SlopeX, 1f, 0f).normalized };
            }
            return hit.collider != null;
        }
    }
    internal static class Mathf { public static float Max(float a, float b) => Math.Max(a, b); }
    internal static class Time { public static float time => 0f; }
}

internal sealed class MonsterAI : Component
{
    public readonly List<ItemDrop> m_consumeItems = new();
    public float m_consumeRange => 1f;
    internal bool PathAllowed = true;
    internal int PathCalls;
    internal Vector3 LastPathPoint;
    public bool IsAlerted() => false;
    public bool HavePath(Vector3 point) { ++PathCalls; LastPathPoint = point; return PathAllowed; }
}
internal sealed class Tameable : Component { public bool IsHungry() => true; }
internal sealed class ZNetView : Component
{
    public bool Owner = true;
    public bool IsOwner() => Owner;
    public bool IsValid() => true;
    public void ClaimOwnership() => Owner = true;
    public ZDO GetZDO() => new();
}
internal sealed class ZDO { public long GetLong(int key, long fallback) => 1L; }
internal static class ZDOVars { public static int s_creator => 1; }
internal sealed class Container : Component
{
    internal readonly Inventory Inventory = new();
    private readonly ZNetView _view = new();
    private readonly Piece _piece = new();
    internal int SaveCalls;
    public Inventory GetInventory() => Inventory;
    public ZNetView NetworkView() => _view;
    public Piece ContainerPiece() { _piece.gameObject = gameObject; return _piece; }
    public bool IsInUse() => false;
    public void Save() => ++SaveCalls;
}
internal sealed class Piece : Component
{
    public Vector3 FindClosestPoint(Vector3 point) => transform.position;
    public Vector3 GetCenter() => transform.position;
}
internal sealed class Inventory
{
    internal readonly List<ItemDrop.ItemData> Items = new();
    internal int RemoveCalls;
    public List<ItemDrop.ItemData> GetAllItems() => Items;
    public int NrOfItems() => Items.Count;
    public bool RemoveOneItem(ItemDrop.ItemData item)
    {
        ++RemoveCalls;
        if (!Items.Contains(item) || item.m_stack <= 0) return false;
        if (--item.m_stack == 0) Items.Remove(item);
        return true;
    }
}
internal sealed class ItemDrop : Component
{
    internal static int DropCalls;
    public ItemData m_itemData = new();
    internal sealed class ItemData
    {
        public GameObject? m_dropPrefab;
        public object m_shared = new();
        public int m_stack;
        public ItemData Clone() => (ItemData)MemberwiseClone();
    }
    public static ItemDrop DropItem(ItemData item, int amount, Vector3 position, Quaternion rotation)
    {
        ++DropCalls;
        ItemDrop result = new() { m_itemData = item };
        result.m_itemData.m_stack = amount;
        result.transform.position = position;
        return result;
    }
}
internal sealed class ObjectDB
{
    public static ObjectDB instance = new();
    public bool TryGetItemPrefab(object shared, out GameObject prefab) { prefab = new GameObject(); return false; }
}
namespace FeedLikeGrandma
{
    internal static class FeedBarrelDropProtection
    {
        internal static bool Ready = true, MarkSucceeds = true;
        internal static int MarkCalls;
        internal static ItemDrop? LastMarked;
        internal static bool IsReady(out string? reason) { reason = Ready ? null : "fixture merge protection unavailable"; return Ready; }
        internal static bool Mark(ItemDrop? item)
        { MarkCalls++; LastMarked = item; if (!MarkSucceeds) Ready = false; return MarkSucceeds; }
    }
    internal static class FeedAzuAutoStoreCompatibility
    {
        internal static bool Allowed = true;
        internal static bool CanDispense(out string? reason) { reason = Allowed ? null : "fixture Azu contract unavailable"; return Allowed; }
    }
    internal static class FeedBarrelSystem
    {
        public static bool IsEnabled => true;
        public static bool IsFeedBarrel(Container? container) => container != null;
        public static void ApplyConfiguredContainerSize(Container? container) { }
    }
    internal static class FeedSystem
    {
        public static bool HasRuleFor(GameObject animal) => true;
        public static bool TrySelectAutoFeedItem(MonsterAI animal, List<ItemDrop.ItemData> items, out ItemDrop.ItemData? item)
        { item = items.Count > 0 ? items[0] : null; return item != null; }
    }
}
