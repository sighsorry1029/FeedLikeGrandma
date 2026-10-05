using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace FeedLikeGrandma;

internal static class FeedProcreationSystem
{
    private const string CheckRpc = "FLG_BreedingCheck_v1";
    private const string ResultRpc = "FLG_BreedingResult_v1";
    private const float CheckLifetime = 5f;
    private const float MaximumPositionDrift = 1f;
    private const int MaxPendingChecks = 2048;
    private const int MaxServerScans = 64;
    private static readonly Dictionary<ZDOID, PendingCheck> Pending = new();
    private static readonly Dictionary<ZDOID, float> ServerRequests = new();
    private static readonly List<ZDOID> Expired = new();
    private static ZRoutedRpc? _rpc;
    private static PendingCheck? _approved;
    private static int _generation;
    private static int _serverScans;
    private static float _lastCleanup;

    private sealed class PendingCheck
    {
        internal Procreation Instance = null!;
        internal ZDOID Parent;
        internal string Token = string.Empty;
        internal Vector3 Center;
        internal float Range;
        internal int Limit;
        internal int Offspring;
        internal uint OwnerRevision;
        internal float Started;

        internal ZPackage Write()
        {
            ZPackage pkg = new();
            pkg.Write(Parent);
            pkg.Write(Token);
            pkg.Write(Center);
            pkg.Write(Range);
            pkg.Write(Limit);
            pkg.Write(Offspring);
            return pkg;
        }
    }

    internal static void ResetRuntimeCache()
    {
        // Also invalidates outstanding coroutine callbacks and approvals after YAML refresh.
        _generation++;
        Pending.Clear();
        ServerRequests.Clear();
        Expired.Clear();
        _approved = null;
        _serverScans = 0;
        _lastCleanup = 0f;
    }

    internal static void Shutdown()
    {
        ResetRuntimeCache();
        if (_rpc != null)
        {
            _rpc.RpcHandlers().Remove(CheckRpc.GetStableHashCode());
            _rpc.RpcHandlers().Remove(ResultRpc.GetStableHashCode());
        }
        _rpc = null;
    }

    internal static void RegisterNetworkRpcs()
    {
        ZRoutedRpc? rpc = ZRoutedRpc.instance;
        if (rpc == null || ReferenceEquals(_rpc, rpc)) return;
        Shutdown();
        rpc.Register<ZPackage>(CheckRpc, ReceiveCheck);
        rpc.Register<ZPackage>(ResultRpc, ReceiveResult);
        _rpc = rpc;
    }

    internal static bool ShouldRunProcreation(Procreation procreation)
    {
        if (FeedLikeGrandmaPlugin.HorizontalProcreationLimit.Value != FeedLikeGrandmaPlugin.Toggle.On)
            return true;
        if (procreation == null || procreation.NetworkView() == null ||
            !procreation.NetworkView().IsValid() || !procreation.NetworkView().IsOwner() ||
            procreation.TameableComponent() == null || !procreation.TameableComponent().IsTamed() ||
            procreation.IsPregnant()) return true;

        if (_approved != null && ReferenceEquals(_approved.Instance, procreation))
        {
            PendingCheck approval = _approved;
            _approved = null; // A nested call or duplicate reply cannot reuse this approval.
            return StillMatches(approval);
        }

        if (!TryEnsureProcreationPrefabs(procreation) || procreation.m_maxCreatures <= 0 ||
            !IsFinite(procreation.m_totalCheckRange)) return false;

        RegisterNetworkRpcs();
        if (_rpc == null || _rpc.GetServerPeerID() == 0L) return false;
        Cleanup();
        ZDOID parent = procreation.NetworkView().GetZDO().m_uid;
        if (Pending.ContainsKey(parent) || Pending.Count >= MaxPendingChecks) return false;
        PendingCheck check = new()
        {
            Instance = procreation,
            Parent = parent,
            Token = Guid.NewGuid().ToString("N"),
            Center = procreation.transform.position,
            Range = procreation.m_totalCheckRange,
            Limit = procreation.m_maxCreatures,
            Offspring = GetPrefabHash(procreation.OffspringPrefab()),
            OwnerRevision = procreation.NetworkView().GetZDO().OwnerRevision,
            Started = Time.realtimeSinceStartup
        };
        Pending.Add(parent, check);
        _rpc.InvokeRoutedRPC(_rpc.GetServerPeerID(), CheckRpc, check.Write());
        return false;
    }

    private static bool StillMatches(PendingCheck check)
    {
        Procreation instance = check.Instance;
        return FeedLikeGrandmaPlugin.HorizontalProcreationLimit.Value == FeedLikeGrandmaPlugin.Toggle.On &&
               Time.realtimeSinceStartup - check.Started <= CheckLifetime && instance != null &&
               instance.NetworkView() != null && instance.NetworkView().IsValid() &&
               instance.NetworkView().IsOwner() && instance.NetworkView().GetZDO().m_uid.Equals(check.Parent) &&
               instance.NetworkView().GetZDO().OwnerRevision == check.OwnerRevision &&
               instance.TameableComponent() != null && instance.TameableComponent().IsTamed() &&
               !instance.IsPregnant() && instance.m_totalCheckRange == check.Range &&
               instance.m_maxCreatures == check.Limit &&
               GetPrefabHash(instance.OffspringPrefab()) == check.Offspring &&
               Utils.DistanceXZ(instance.transform.position, check.Center) <= MaximumPositionDrift;
    }

    private static void ReceiveResult(long sender, ZPackage pkg)
    {
        if (_rpc == null || !ReferenceEquals(_rpc, ZRoutedRpc.instance) ||
            sender != _rpc.GetServerPeerID() || pkg == null || pkg.Size() > 128) return;
        try
        {
            ZDOID parent = pkg.ReadZDOID();
            string token = pkg.ReadString();
            bool allowed = pkg.ReadBool();
            if (!Pending.TryGetValue(parent, out PendingCheck? check) || check.Token != token) return;
            Pending.Remove(parent);
            if (!allowed || !StillMatches(check)) return;
            _approved = check;
            try
            {
                // Use the patched game method so food, partner, chance and optional-mod policies stay intact.
                check.Instance.RunProcreation();
            }
            finally { _approved = null; }
        }
        catch (Exception error)
        {
            FeedLikeGrandmaPlugin.Log.LogWarning($"Breeding reply failed: {error.Message}");
        }
    }

    private static void ReceiveCheck(long sender, ZPackage pkg)
    {
        if (ZNet.instance == null || !ZNet.instance.IsServer() || _rpc == null ||
            !ReferenceEquals(_rpc, ZRoutedRpc.instance) || pkg == null || pkg.Size() > 160) return;
        try
        {
            PendingCheck check = new()
            {
                Parent = pkg.ReadZDOID(), Token = pkg.ReadString(), Center = pkg.ReadVector3(),
                Range = pkg.ReadSingle(), Limit = pkg.ReadInt(), Offspring = pkg.ReadInt(),
                Started = Time.realtimeSinceStartup
            };
            if (check.Token.Length != 32 || !Guid.TryParseExact(check.Token, "N", out _) ||
                !IsFinite(check.Range) || !IsFinite(check.Center.x) || !IsFinite(check.Center.y) ||
                !IsFinite(check.Center.z) || check.Limit <= 0 ||
                !TryGetServerPrefabs(sender, check, out GameObject adult, out GameObject offspring)) return;
            Cleanup();
            if (_serverScans >= MaxServerScans || ServerRequests.Count >= MaxPendingChecks ||
                (ServerRequests.TryGetValue(check.Parent, out float last) &&
                 Time.realtimeSinceStartup - last < 1f)) return;
            ServerRequests[check.Parent] = Time.realtimeSinceStartup;
            check.OwnerRevision = ZDOMan.instance.GetZDO(check.Parent).OwnerRevision;
            _serverScans++;
            FeedLikeGrandmaPlugin.Instance.StartCoroutine(CountOnServer(sender, check, adult, offspring, _generation));
        }
        catch (Exception error)
        {
            FeedLikeGrandmaPlugin.Log.LogWarning($"Breeding request failed: {error.Message}");
        }
    }

    private static bool TryGetServerPrefabs(long sender, PendingCheck check, out GameObject adult, out GameObject offspring)
    {
        adult = null!;
        offspring = null!;
        if (ZNet.instance == null || !ZNet.instance.IsServer() || ZDOMan.instance == null ||
            ZNetScene.instance == null ||
            FeedLikeGrandmaPlugin.HorizontalProcreationLimit.Value != FeedLikeGrandmaPlugin.Toggle.On) return false;
        if (sender != ZDOMan.GetSessionID() && ZNet.instance.GetPeer(sender) == null) return false;
        ZDO parent = ZDOMan.instance.GetZDO(check.Parent);
        if (parent == null || !parent.IsValid() || parent.GetOwner() != sender ||
            Utils.DistanceXZ(parent.GetPosition(), check.Center) > 2f) return false;
        adult = ZNetScene.instance.GetPrefab(parent.GetPrefab());
        if (adult == null) return false;
        Procreation? rule = adult.GetComponent<Procreation>();
        Tameable? tameable = adult.GetComponent<Tameable>();
        if (rule == null || tameable == null ||
            (!parent.GetBool(ZDOVars.s_tamed) && !tameable.m_startsTamed) || rule.m_offspring == null) return false;
        offspring = ZNetScene.instance.GetPrefab(Utils.GetPrefabName(rule.m_offspring));
        // The owner already simulates breeding. Preserve its live instance radius/limit (including
        // pre-reload instances); the server supplies counts, not a new anti-cheat breeding policy.
        return offspring != null && GetPrefabHash(offspring) == check.Offspring;
    }

    private static IEnumerator CountOnServer(long sender, PendingCheck check, GameObject adult, GameObject offspring, int generation)
    {
        try
        {
            // Avoid a synchronous host RPC recursively entering the same Harmony invocation.
            yield return null;
            if (!IsCurrent(generation, check)) yield break;
            int adultHash = GetPrefabHash(adult);
            int offspringHash = GetPrefabHash(offspring);
            int count = 0;
            List<ZDO> candidates = new();
            HashSet<ZDOID> seen = new();
            if (check.Range > 0f && check.Range <= 256f)
            {
                int near = Mathf.CeilToInt(check.Range / 64f);
                ZDOMan.instance.FindSectorObjects(ZoneSystem.GetZone(check.Center), new SimulationDistance(near, 0, true), candidates);
                AddCandidates(candidates, seen, check, adultHash, offspringHash, ref count);
            }
            else
            {
                // Unlimited/large radii need a world query. Yield between the game's bounded batches.
                // The iterator repeats sector boundaries, so deduplicate ZDOIDs, not prefab policies.
                string[] names = adultHash == offspringHash
                    ? new[] { Utils.GetPrefabName(adult) }
                    : new[] { Utils.GetPrefabName(adult), Utils.GetPrefabName(offspring) };
                foreach (string name in names)
                {
                    int index = 0;
                    bool done;
                    do
                    {
                        if (!IsCurrent(generation, check)) yield break;
                        candidates.Clear();
                        done = ZDOMan.instance.GetAllZDOsWithPrefabIterative(name, candidates, ref index);
                        AddCandidates(candidates, seen, check, adultHash, offspringHash, ref count);
                        if (count >= check.Limit) break;
                        if (!done) yield return null;
                    } while (!done);
                    if (count >= check.Limit) break;
                }
            }
            if (!IsCurrent(generation, check) || !TryGetServerPrefabs(sender, check, out GameObject currentAdult, out GameObject currentOffspring) ||
                currentAdult != adult || currentOffspring != offspring ||
                ZDOMan.instance.GetZDO(check.Parent).OwnerRevision != check.OwnerRevision) yield break;
            ZPackage result = new();
            result.Write(check.Parent);
            result.Write(check.Token);
            result.Write(count < check.Limit);
            _rpc!.InvokeRoutedRPC(sender, ResultRpc, result);
        }
        finally
        {
            if (generation == _generation) _serverScans--;
        }
    }

    private static void AddCandidates(List<ZDO> candidates, HashSet<ZDOID> seen, PendingCheck check, int adultHash, int offspringHash, ref int count)
    {
        foreach (ZDO candidate in candidates)
        {
            if (candidate == null || !candidate.IsValid()) continue;
            int hash = candidate.GetPrefab();
            int weight = (hash == adultHash ? 1 : 0) + (hash == offspringHash ? 1 : 0);
            if (weight == 0 || !seen.Add(candidate.m_uid) ||
                (check.Range > 0f && Utils.DistanceXZ(candidate.GetPosition(), check.Center) > check.Range)) continue;
            count = (int)Math.Min((long)check.Limit, (long)count + weight);
            if (count >= check.Limit) return;
        }
    }

    private static bool IsCurrent(int generation, PendingCheck check) =>
        generation == _generation && ZDOMan.instance != null && _rpc != null &&
        ReferenceEquals(_rpc, ZRoutedRpc.instance) && Time.realtimeSinceStartup - check.Started <= CheckLifetime;

    private static void Cleanup()
    {
        float now = Time.realtimeSinceStartup;
        if (now - _lastCleanup < 1f) return;
        _lastCleanup = now;
        Expired.Clear();
        foreach (KeyValuePair<ZDOID, PendingCheck> item in Pending)
            if (now - item.Value.Started > CheckLifetime || item.Value.Instance == null) Expired.Add(item.Key);
        foreach (ZDOID id in Expired) Pending.Remove(id);
        Expired.Clear();
        foreach (KeyValuePair<ZDOID, float> item in ServerRequests)
            if (now - item.Value > CheckLifetime) Expired.Add(item.Key);
        foreach (ZDOID id in Expired) ServerRequests.Remove(id);
        Expired.Clear();
    }

    private static bool TryEnsureProcreationPrefabs(Procreation procreation)
    {
        if (ZNetScene.instance == null) return false;
        if (procreation.OffspringPrefab() == null && procreation.m_offspring != null)
            procreation.OffspringPrefab() = ZNetScene.instance.GetPrefab(Utils.GetPrefabName(procreation.m_offspring));
        if (procreation.OwnPrefab() == null)
            procreation.OwnPrefab() = ZNetScene.instance.GetPrefab(procreation.NetworkView().GetZDO().GetPrefab());
        return procreation.OwnPrefab() != null && procreation.OffspringPrefab() != null;
    }

    private static int GetPrefabHash(GameObject prefab) => prefab == null ? 0 : Utils.GetPrefabName(prefab).GetStableHashCode();
    private static bool IsFinite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
}
