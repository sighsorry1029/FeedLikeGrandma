using System;
using System.Collections.Generic;
using System.Reflection;
using FeedLikeGrandma;
using HarmonyLib;

// Production marker/filter source with explicitly synthetic world and inventory
// objects. This does not emulate Unity, networking, or vanilla stack execution.
internal static class AzuDropProtectionChecks
{
    private const BindingFlags All = BindingFlags.Static | BindingFlags.NonPublic;
    private static readonly Type Protection = typeof(FeedBarrelDropProtection);
    private static int _passed;

    private static int Main()
    {
        try
        {
            Reset();
            ItemDrop owned = new();
            object shared = owned.m_itemData.m_shared;
            owned.m_itemData.m_customData.Add("other-mod", "unchanged");
            Require(FeedBarrelDropProtection.Mark(owned), "owner can mark spawned food");
            Require(FeedBarrelDropProtection.IsProtected(owned) && owned.View.Data.SetCalls == 1, "marker persists only in world ZDO");
            Require(owned.m_itemData.m_stack == 3 && owned.m_itemData.m_customData.Count == 1 &&
                owned.m_itemData.m_customData["other-mod"] == "unchanged" && ReferenceEquals(shared, owned.m_itemData.m_shared),
                "marking preserves quantity, item custom data and shared prefab data");
            ItemDrop reloaded = new(owned.View.Data); reloaded.View.Owner = false;
            Require(FeedBarrelDropProtection.IsProtected(reloaded), "replicated/reloaded world marker is readable by nonowner");
            ItemDrop redropped = new() { m_itemData = owned.m_itemData };
            Require(!FeedBarrelDropProtection.IsProtected(redropped), "picked-up item data does not transfer protection to a fresh world drop");
            Require(!FeedBarrelDropProtection.IsProtected(null), "null drop is not protected");

            ItemDrop ordinary = new(); ItemDrop protectedOther = new();
            FeedBarrelDropProtection.Mark(protectedOther);
            int total = owned.m_itemData.m_stack + ordinary.m_itemData.m_stack + protectedOther.m_itemData.m_stack;
            Require(Filter(ordinary, owned) == null && Filter(owned, ordinary) == null, "mixed pairs rejected in both receiver directions");
            Require(ReferenceEquals(Filter(protectedOther, owned), protectedOther) && ReferenceEquals(Filter(owned, protectedOther), owned),
                "protected pairs preserve vanilla merge candidates in both directions");
            Require(ReferenceEquals(Filter(redropped, ordinary), redropped) && ReferenceEquals(Filter(ordinary, redropped), ordinary),
                "ordinary pairs preserve vanilla merge candidates in both directions");
            Require(Filter(null, owned) == null && ReferenceEquals(Filter(owned, owned), owned), "null and self candidates retain original behavior");
            Require(total == owned.m_itemData.m_stack + ordinary.m_itemData.m_stack + protectedOther.m_itemData.m_stack &&
                owned.View.Data.SetCalls == 1 && ordinary.View.Data.SetCalls == 0 && protectedOther.View.Data.SetCalls == 1,
                "filter only chooses candidates: no item counts or marker writes change");

            foreach (string problem in new[] { "unowned", "invalid", "null", "write rejected" })
            {
                Reset(); ItemDrop rejected = new();
                if (problem == "unowned") rejected.View.Owner = false;
                if (problem == "invalid") rejected.View.Valid = false;
                if (problem == "write rejected") rejected.View.Data.RejectWrites = true;
                Require(!FeedBarrelDropProtection.Mark(problem == "null" ? null : rejected), problem + " marking fails");
                Require(!FeedBarrelDropProtection.IsReady(out string? reason) && !string.IsNullOrEmpty(reason), problem + " blocks subsequent dispensing");
                Require(rejected.m_itemData.m_stack == 3 && !rejected.Destroyed &&
                    (problem == "write rejected" || rejected.View.Data.SetCalls == 0), problem + " does not destroy, refund, or alter unowned world data");
            }
            Reset(); FeedBarrelDropProtection.Mark(owned); FeedBarrelDropProtection.Shutdown();
            Require(owned.View.Data.SetCalls == 2 && owned.View.Data.Values.Count == 1, "shutdown does not erase existing world markers");
            Console.WriteLine("PASS: " + _passed + " source-linked marker/merge policy checks (synthetic world objects only).");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }

    private static void Reset()
    {
        Protection.GetField("_networkView", All)!.SetValue(null, AccessTools.FieldRefAccess<ItemDrop, ZNetView>(
            typeof(ItemDrop).GetField("m_nview", BindingFlags.Instance | BindingFlags.NonPublic)!));
        Protection.GetField("_installed", All)!.SetValue(null, true);
        Protection.GetField("_failure", All)!.SetValue(null, null);
    }
    private static ItemDrop? Filter(ItemDrop? candidate, ItemDrop receiver) =>
        (ItemDrop?)Protection.GetMethod("FilterStackCandidate", All)!.Invoke(null, new object?[] { candidate, receiver });
    private static void Require(bool condition, string message) { if (!condition) throw new Exception(message); _passed++; }
}

internal sealed class ItemDrop : UnityEngine.Component
{
    private ZNetView m_nview;
    internal ItemDrop(ZDO? data = null) { m_nview = new ZNetView(data ?? new ZDO()); }
    internal ZNetView View => m_nview;
    internal bool Destroyed = false;
    internal ItemData m_itemData = new();
    internal void Save() { }
    internal sealed class ItemData
    {
        internal int m_stack = 3;
        internal object m_shared = new();
        internal readonly Dictionary<string, string> m_customData = new();
    }
}
internal sealed class ZNetView
{
    internal ZNetView(ZDO data) { Data = data; }
    internal readonly ZDO Data;
    internal bool Owner = true, Valid = true;
    public bool IsOwner() => Owner;
    public bool IsValid() => Valid;
    public ZDO GetZDO() => Data;
    public void Destroy() { }
}
internal sealed class ZDO
{
    internal readonly Dictionary<int, bool> Values = new();
    internal int SetCalls;
    internal bool RejectWrites;
    public void Set(int key, bool value) { SetCalls++; if (!RejectWrites) Values[key] = value; }
    public bool GetBool(int key, bool fallback) => Values.TryGetValue(key, out bool value) ? value : fallback;
}
internal static class HashFixture
{
    // Exact hash value is irrelevant to the policy fixture; real game hash/API is checked separately.
    internal static int GetStableHashCode(this string value) => value.GetHashCode();
}
namespace UnityEngine
{
    internal class Object
    {
        public static bool operator ==(Object? left, Object? right) => ReferenceEquals(left, right);
        public static bool operator !=(Object? left, Object? right) => !ReferenceEquals(left, right);
        public override bool Equals(object? value) => ReferenceEquals(this, value);
        public override int GetHashCode() => base.GetHashCode();
    }
    internal class Component : Object { public T? GetComponent<T>() where T : class => null; }
    internal static class Physics { public static object[] OverlapSphere() => Array.Empty<object>(); }
}
namespace FeedLikeGrandma
{
    // This runner exercises marker policy only; actual optional-mod binding and
    // Harmony composition are exercised by EpicLootStackChecks in original Mono.
    internal static class FeedEpicLootStackCompatibility
    {
        internal static bool IsInstalled => false;
        internal static bool Ensure(Patches patches, MethodInfo filter, bool requireExpiry) => false;
        internal static void Shutdown() { }
    }
    internal static class FeedLikeGrandmaPlugin { internal static readonly TestLog Log = new(); }
    internal sealed class TestLog { internal void LogError(string message) { } }
}
