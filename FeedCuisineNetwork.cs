using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace FeedLikeGrandma;

internal static partial class FeedCuisineCompatibility
{
    // The server sequences a Lox, its owner persists the cooldown, then the requester receives milk.
    // Inventory/world saves are independent: uncertain delivery is never retried as a new grant.
    private const string MilkHelloServerRpc = "FLG_CuisineHelloServer_v1";
    private const string MilkHelloClientRpc = "FLG_CuisineHelloClient_v1";
    private const string MilkRequestServerRpc = "FLG_CuisineMilkServer_v1";
    private const string MilkApplyOwnerRpc = "FLG_CuisineMilkOwner_v1";
    private const string MilkResultServerRpc = "FLG_CuisineResultServer_v1";
    private const string MilkResultClientRpc = "FLG_CuisineResultClient_v1";
    private const float MilkRequestTimeout = 30f;
    private const int MaxMilkLocks = 512;
    private static readonly int MilkBucketHash = "VC_MilkinBucket".GetStableHashCode();
    private static readonly Dictionary<long, MilkPeerSession> MilkSessions = new();
    private static readonly Dictionary<ZDOID, MilkServerRequest> MilkLocks = new();
    private static ZRoutedRpc? _milkRpcInstance;
    private static string _milkClientNonce = string.Empty;
    private static string _milkCookie = string.Empty;
    private static long _milkSequence;
    private static MilkClientRequest? _pendingMilk;

    internal static void RegisterNetworkRpcs()
    {
        ZRoutedRpc? rpc = ZRoutedRpc.instance;
        if (rpc == null || ReferenceEquals(_milkRpcInstance, rpc)) return;
        EndNetworkSession();
        RegisterMilkRpc(rpc, MilkHelloServerRpc, ReceiveMilkHelloOnServer);
        RegisterMilkRpc(rpc, MilkHelloClientRpc, ReceiveMilkHelloOnClient);
        RegisterMilkRpc(rpc, MilkRequestServerRpc, ReceiveMilkRequestOnServer);
        RegisterMilkRpc(rpc, MilkApplyOwnerRpc, ReceiveMilkApplyOnOwner);
        RegisterMilkRpc(rpc, MilkResultServerRpc, ReceiveMilkResultOnServer);
        RegisterMilkRpc(rpc, MilkResultClientRpc, ReceiveMilkResultOnClient);
        _milkRpcInstance = rpc;
        _milkClientNonce = Guid.NewGuid().ToString("N");
    }

    private static void RegisterMilkRpc(ZRoutedRpc rpc, string name, Action<long, ZPackage> handler)
    {
        if (!rpc.RpcHandlers().Contains(name.GetStableHashCode())) rpc.Register(name, handler);
    }

    internal static void EndNetworkSession()
    {
        _pendingMilk = null;
        _milkCookie = string.Empty;
        _milkClientNonce = string.Empty;
        _milkSequence = 0;
        MilkSessions.Clear();
        MilkLocks.Clear();
        _milkRpcInstance = null;
    }

    internal static void RequestMilk(Player player, Character lox)
    {
        if ((object?)player == null || player != Player.m_localPlayer) return;
        RegisterNetworkRpcs();
        if (!Ready || _milkRpcInstance == null || _milkRpcInstance.GetServerPeerID() == 0L)
        {
            ShowMessage(player, "$flg_cuisine_milk_unavailable");
            return;
        }
        if (_pendingMilk != null)
        {
            ShowMessage(player, "$flg_cuisine_milk_busy");
            return;
        }
        ItemDrop.ItemData? bucket = player.RightItem;
        if ((object?)lox == null || !IsLox(lox) || !lox.IsTamed() || lox.IsDead() ||
            player.IsDead() || bucket?.m_dropPrefab == null || bucket.m_dropPrefab.name != "VC_MilkinBucket") return;
        if (Vector3.Distance(player.transform.position, lox.transform.position) > 3f)
        {
            ShowMessage(player, "$flg_cuisine_milk_too_far");
            return;
        }
        MilkClientRequest request = new()
        {
            Id = Guid.NewGuid().ToString("N"),
            PlayerId = player.GetZDOID(),
            TargetId = lox.GetZDOID(),
            Sequence = ++_milkSequence,
            Started = Time.realtimeSinceStartup,
            Rpc = _milkRpcInstance
        };
        _pendingMilk = request;
        SendMilkClientRequest(request);
        FeedLikeGrandmaPlugin.Instance.StartCoroutine(RetryMilkRequest(request));
    }

    private static IEnumerator RetryMilkRequest(MilkClientRequest request)
    {
        WaitForSecondsRealtime wait = new(1f);
        while (ReferenceEquals(_pendingMilk, request) && ReferenceEquals(_milkRpcInstance, request.Rpc))
        {
            yield return wait;
            if (!ReferenceEquals(_pendingMilk, request)) yield break;
            Player? player = Player.m_localPlayer;
            if ((object?)player == null || !player.GetZDOID().Equals(request.PlayerId))
            {
                _pendingMilk = null;
                yield break;
            }
            if (Time.realtimeSinceStartup - request.Started >= MilkRequestTimeout)
            {
                _pendingMilk = null;
                ShowMessage(player, "$flg_cuisine_milk_unavailable");
                yield break;
            }
            SendMilkClientRequest(request);
        }
    }

    private static void SendMilkClientRequest(MilkClientRequest request)
    {
        if (!ReferenceEquals(request.Rpc, _milkRpcInstance)) return;
        ZPackage pkg = new();
        pkg.Write(request.PlayerId);
        pkg.Write(_milkClientNonce);
        if (_milkCookie.Length == 0)
        {
            request.Rpc.InvokeRoutedRPC(request.Rpc.GetServerPeerID(), MilkHelloServerRpc, pkg);
            return;
        }
        pkg.Write(_milkCookie);
        pkg.Write(request.Sequence);
        pkg.Write(request.Id);
        pkg.Write(request.TargetId);
        request.Rpc.InvokeRoutedRPC(request.Rpc.GetServerPeerID(), MilkRequestServerRpc, pkg);
    }

    private static void ReceiveMilkHelloOnServer(long sender, ZPackage pkg)
    {
        if (!IsMilkServer() || !Ready || pkg.Size() > 256) return;
        try
        {
            ZDOID playerId = pkg.ReadZDOID();
            string nonce = pkg.ReadString();
            if (!IsMilkGuid(nonce) || !AuthenticateMilkPlayer(sender, playerId)) return;
            if (!MilkSessions.TryGetValue(sender, out MilkPeerSession? session) ||
                !session.PlayerId.Equals(playerId) || session.Nonce != nonce)
            {
                // A fresh cookie invalidates packets from the previous connection, even if its peer ID is reused.
                if (MilkSessions.Count >= 256)
                {
                    List<long> disconnected = new();
                    foreach (KeyValuePair<long, MilkPeerSession> pair in MilkSessions)
                        if (!AuthenticateMilkPlayer(pair.Key, pair.Value.PlayerId)) disconnected.Add(pair.Key);
                    foreach (long peer in disconnected) MilkSessions.Remove(peer);
                    if (MilkSessions.Count >= 256 && !MilkSessions.ContainsKey(sender)) return;
                }
                session = new MilkPeerSession { PlayerId = playerId, Nonce = nonce, Cookie = Guid.NewGuid().ToString("N") };
                MilkSessions[sender] = session;
            }
            ZPackage response = new();
            response.Write(nonce);
            response.Write(session.Cookie);
            ZRoutedRpc.instance.InvokeRoutedRPC(sender, MilkHelloClientRpc, response);
        }
        catch (Exception) { /* Malformed network input cannot invoke the original milking path. */ }
    }

    private static void ReceiveMilkHelloOnClient(long sender, ZPackage pkg)
    {
        if (!IsMilkServerMessage(sender) || pkg.Size() > 256 || _pendingMilk == null) return;
        try
        {
            string nonce = pkg.ReadString();
            string cookie = pkg.ReadString();
            if (nonce != _milkClientNonce || !IsMilkGuid(cookie)) return;
            // Once established, a late handshake cannot replace this session's replay boundary.
            if (_milkCookie.Length != 0 && _milkCookie != cookie) return;
            _milkCookie = cookie;
            SendMilkClientRequest(_pendingMilk);
        }
        catch (Exception) { }
    }

    private static void ReceiveMilkRequestOnServer(long sender, ZPackage pkg)
    {
        if (!IsMilkServer() || !Ready || pkg.Size() > 512) return;
        try
        {
            ZDOID playerId = pkg.ReadZDOID();
            string nonce = pkg.ReadString();
            string cookie = pkg.ReadString();
            long sequence = pkg.ReadLong();
            string id = pkg.ReadString();
            ZDOID targetId = pkg.ReadZDOID();
            if (!AuthenticateMilkPlayer(sender, playerId) || !MilkSessions.TryGetValue(sender, out MilkPeerSession? session) ||
                session.Nonce != nonce || session.Cookie != cookie || !session.PlayerId.Equals(playerId) ||
                sequence <= 0 || !IsMilkGuid(id) || targetId.IsNone()) return;
            if (sequence <= session.LastSequence)
            {
                MilkServerRequest? previous = session.LastRequest;
                if (previous == null || previous.Sequence != sequence || previous.Id != id || !previous.TargetId.Equals(targetId)) return;
                TryObserveMilkWrite(previous);
                if (previous.Complete) SendMilkResult(previous);
                return;
            }
            // Only the newest sequence remains replayable; older requests can never become new milkings.
            MilkServerRequest request = new()
            {
                Id = id, PlayerId = playerId, TargetId = targetId, Sequence = sequence,
                Sender = sender, Cookie = cookie, Rpc = ZRoutedRpc.instance
            };
            session.LastSequence = sequence;
            session.LastRequest = request;
            if (IsMilkBusyOnServer(targetId) || TamingOrbSystem.IsCuisineCaptureBlocked(targetId) || MilkLocks.Count >= MaxMilkLocks)
            {
                CompleteMilkRequest(request, false, "$flg_cuisine_milk_busy");
                return;
            }
            if (!TryValidateMilkRequestOnServer(playerId, targetId, out ZDO? targetZdo, out string reason))
            {
                CompleteMilkRequest(request, false, reason);
                return;
            }
            ZDO zdo = targetZdo!;
            long currentUntil = ReadCooldownUntil(zdo);
            if (currentUntil > WorldNowTicks())
            {
                CompleteMilkRequest(request, false, currentUntil == long.MaxValue
                    ? "$flg_cuisine_milk_unavailable" : "$flg_cuisine_milk_cooldown");
                return;
            }
            if (RequireBreeding && !HasBreedingEligibility(zdo))
            {
                CompleteMilkRequest(request, false, "$flg_cuisine_milk_breeding");
                return;
            }
            request.Owner = zdo.GetOwner();
            if (request.Owner == 0L)
            {
                CompleteMilkRequest(request, false, "$flg_cuisine_milk_unavailable");
                return;
            }
            request.Until = checked(WorldNowTicks() + (long)(Math.Max(60f, Math.Min(7200f, CooldownSeconds)) * TimeSpan.TicksPerSecond));
            request.Amount = Math.Max(1, Math.Min(20, MilkAmount));
            request.Position = zdo.GetPosition();
            request.ExpectedPrevious = ReadLastMilkRequest(zdo);
            request.ExpectedUntil = ReadCooldownUntil(zdo);
            MilkLocks[targetId] = request;
            ZPackage owner = new();
            owner.Write(id);
            owner.Write(playerId);
            owner.Write(targetId);
            owner.Write(request.Until);
            owner.Write(request.ExpectedPrevious);
            owner.Write(request.ExpectedUntil);
            owner.Write(RequireBreeding);
            // Exactly one apply dispatch. Retries observe the persisted marker, never reissue an ambiguous write.
            ZRoutedRpc.instance.InvokeRoutedRPC(request.Owner, MilkApplyOwnerRpc, owner);
            FeedLikeGrandmaPlugin.Instance.StartCoroutine(ObserveMilkWrite(request));
        }
        catch (Exception) { /* A lock created before an uncertain send is intentionally retained. */ }
    }

    internal static bool IsMilkBusyOnServer(ZDOID target)
    {
        if (!IsMilkServer() || !MilkLocks.TryGetValue(target, out MilkServerRequest? request)) return false;
        TryObserveMilkWrite(request);
        return !request.Complete;
    }

    private static IEnumerator ObserveMilkWrite(MilkServerRequest request)
    {
        float started = Time.realtimeSinceStartup;
        WaitForSecondsRealtime wait = new(0.25f);
        while (!request.Complete && ReferenceEquals(_milkRpcInstance, request.Rpc) &&
               Time.realtimeSinceStartup - started < MilkRequestTimeout)
        {
            TryObserveMilkWrite(request);
            if (!request.Complete) yield return wait;
        }
        // Losing the owner or its acknowledgement is not proof that the cooldown was never applied.
        // Keep the lock; later requests/capture checks can still observe a delayed ZDO update.
    }

    private static void ReceiveMilkApplyOnOwner(long sender, ZPackage pkg)
    {
        if (!IsMilkServerMessage(sender) || !Ready || pkg.Size() > 512) return;
        string id = string.Empty;
        ZDOID targetId = ZDOID.None;
        try
        {
            id = pkg.ReadString();
            ZDOID playerId = pkg.ReadZDOID();
            targetId = pkg.ReadZDOID();
            long until = pkg.ReadLong();
            string previous = pkg.ReadString();
            long previousUntil = pkg.ReadLong();
            bool requireBreeding = pkg.ReadBool();
            if (!IsMilkGuid(id) || previous.Length > 160) return;
            if (!TryValidateOwnedMilkTarget(playerId, targetId, out Character? lox, out ZNetView? view, out string reason))
            {
                SendMilkOwnerResult(id, targetId, false, reason);
                return;
            }
            ZDO zdo = view!.GetZDO();
            if (ReadLastMilkRequest(zdo) == id)
            {
                SendMilkOwnerResult(id, targetId, ReadCooldownUntil(zdo) == until, "$flg_cuisine_milk_unavailable");
                return;
            }
            EnsureMilkState(lox!, view);
            long now = WorldNowTicks();
            if (ReadCooldownUntil(zdo) > now)
            {
                SendMilkOwnerResult(id, targetId, false, "$flg_cuisine_milk_cooldown");
                return;
            }
            if (ReadLastMilkRequest(zdo) != previous || ReadCooldownUntil(zdo) != previousUntil ||
                until <= now || until - now > 7201L * TimeSpan.TicksPerSecond)
            {
                SendMilkOwnerResult(id, targetId, false, "$flg_cuisine_milk_unavailable");
                return;
            }
            if (requireBreeding && !HasBreedingEligibility(zdo))
            {
                SendMilkOwnerResult(id, targetId, false, "$flg_cuisine_milk_breeding");
                return;
            }
            WriteMilkCooldown(zdo, until, id);
            ZDOMan.instance.ForceSendZDO(targetId);
            SendMilkOwnerResult(id, targetId, true, string.Empty);
        }
        catch (Exception)
        {
            // A partially completed owner write must remain ambiguous; do not send a negative acknowledgement.
        }
    }

    private static bool TryValidateMilkRequestOnServer(ZDOID playerId, ZDOID targetId, out ZDO? target, out string reason)
    {
        reason = "$flg_cuisine_milk_unavailable";
        target = ZDOMan.instance?.GetZDO(targetId);
        ZDO? player = ZDOMan.instance?.GetZDO(playerId);
        if (target == null || player == null || player.GetInt(ZDOVars.s_rightItem) != MilkBucketHash ||
            !target.GetBool(ZDOVars.s_tamed) || target.GetFloat(ZDOVars.s_health, 1f) <= 0f ||
            player.GetFloat(ZDOVars.s_health, 1f) <= 0f) return false;
        // Dedicated servers retain remote ZDOs without instantiating their Characters.
        Character? prefab = ZNetScene.instance?.GetPrefab(target.GetPrefab())?.GetComponent<Character>();
        if ((object?)prefab == null || !IsLox(prefab)) return false;
        if (Vector3.Distance(player.GetPosition(), target.GetPosition()) > 4f)
        {
            reason = "$flg_cuisine_milk_too_far";
            return false;
        }
        return true;
    }

    private static bool TryValidateOwnedMilkTarget(ZDOID playerId, ZDOID targetId,
        out Character? lox, out ZNetView? view, out string reason)
    {
        lox = null;
        view = null;
        reason = "$flg_cuisine_milk_unavailable";
        GameObject? target = ZNetScene.instance?.FindInstance(targetId);
        if ((object?)target == null) return false;
        lox = target.GetComponent<Character>();
        view = target.GetComponent<ZNetView>();
        ZDO? playerZdo = ZDOMan.instance?.GetZDO(playerId);
        if ((object?)lox == null || (object?)view == null || !view.IsValid() || !view.IsOwner() ||
            !IsLox(lox) || !lox.IsTamed() || lox.IsDead() || playerZdo == null ||
            playerZdo.GetInt(ZDOVars.s_rightItem) != MilkBucketHash) return false;
        Player? player = ZNetScene.instance?.FindInstance(playerId)?.GetComponent<Player>();
        if ((object?)player != null && player.IsDead()) return false;
        Vector3 playerPosition = (object?)player != null ? player.transform.position : playerZdo.GetPosition();
        if (Vector3.Distance(playerPosition, lox.transform.position) > 3f)
        {
            reason = "$flg_cuisine_milk_too_far";
            return false;
        }
        return true;
    }

    private static void SendMilkOwnerResult(string id, ZDOID target, bool success, string reason)
    {
        ZPackage pkg = new();
        pkg.Write(id);
        pkg.Write(target);
        pkg.Write(success);
        pkg.Write(reason);
        ZRoutedRpc.instance.InvokeRoutedRPC(ZRoutedRpc.instance.GetServerPeerID(), MilkResultServerRpc, pkg);
    }

    private static void ReceiveMilkResultOnServer(long sender, ZPackage pkg)
    {
        if (!IsMilkServer() || pkg.Size() > 512) return;
        try
        {
            string id = pkg.ReadString();
            ZDOID target = pkg.ReadZDOID();
            bool success = pkg.ReadBool();
            string reason = pkg.ReadString();
            if (!MilkLocks.TryGetValue(target, out MilkServerRequest? request) || request.Id != id || request.Owner != sender) return;
            if (TryObserveMilkWrite(request)) return;
            // Only an explicit refusal from the unchanged owner can unlock an unapplied operation.
            ZDO? zdo = ZDOMan.instance?.GetZDO(target);
            if (!success && zdo != null && zdo.GetOwner() == sender && ReadLastMilkRequest(zdo) != id)
                CompleteMilkRequest(request, false, IsMilkFailureToken(reason) ? reason : "$flg_cuisine_milk_unavailable");
        }
        catch (Exception) { }
    }

    private static bool TryObserveMilkWrite(MilkServerRequest request)
    {
        if (request.Complete) return request.Success;
        ZDO? zdo = ZDOMan.instance?.GetZDO(request.TargetId);
        if (request.Until <= 0L || zdo == null || ReadLastMilkRequest(zdo) != request.Id || ReadCooldownUntil(zdo) != request.Until) return false;
        CompleteMilkRequest(request, true, string.Empty);
        return true;
    }

    private static void CompleteMilkRequest(MilkServerRequest request, bool success, string reason)
    {
        if (request.Complete) return;
        request.Complete = true;
        request.Success = success;
        request.Reason = reason;
        if (MilkLocks.TryGetValue(request.TargetId, out MilkServerRequest? locked) && ReferenceEquals(locked, request))
            MilkLocks.Remove(request.TargetId);
        SendMilkResult(request);
    }

    private static void SendMilkResult(MilkServerRequest request)
    {
        if (!AuthenticateMilkPlayer(request.Sender, request.PlayerId) ||
            !MilkSessions.TryGetValue(request.Sender, out MilkPeerSession? session) || session.Cookie != request.Cookie) return;
        ZPackage pkg = new();
        pkg.Write(request.Cookie);
        pkg.Write(request.Sequence);
        pkg.Write(request.Id);
        pkg.Write(request.Success);
        pkg.Write(request.Amount);
        pkg.Write(request.Position);
        pkg.Write(request.Reason);
        ZRoutedRpc.instance.InvokeRoutedRPC(request.Sender, MilkResultClientRpc, pkg);
    }

    private static void ReceiveMilkResultOnClient(long sender, ZPackage pkg)
    {
        if (!IsMilkServerMessage(sender) || pkg.Size() > 512 || _pendingMilk == null) return;
        try
        {
            string cookie = pkg.ReadString();
            long sequence = pkg.ReadLong();
            string id = pkg.ReadString();
            bool success = pkg.ReadBool();
            int amount = pkg.ReadInt();
            Vector3 position = pkg.ReadVector3();
            string reason = pkg.ReadString();
            MilkClientRequest request = _pendingMilk;
            if (cookie != _milkCookie || sequence != request.Sequence || id != request.Id) return;
            // Consumed before inventory/effect calls, including exceptions: no duplicate grant on replay.
            _pendingMilk = null;
            Player? player = Player.m_localPlayer;
            if ((object?)player == null || !player.GetZDOID().Equals(request.PlayerId)) return;
            if (success && amount >= 1 && amount <= 20 && !player.IsDead()) GrantMilk(player, amount, position);
            else ShowMessage(player, IsMilkFailureToken(reason) ? reason : "$flg_cuisine_milk_unavailable");
        }
        catch (Exception) { }
    }

    private static bool AuthenticateMilkPlayer(long sender, ZDOID playerId)
    {
        if (!IsMilkServer() || sender == 0L || playerId.IsNone()) return false;
        if (sender == ZRoutedRpc.instance.PeerId())
            return (object?)Player.m_localPlayer != null && Player.m_localPlayer.GetZDOID().Equals(playerId);
        ZNetPeer? peer = ZNet.instance.GetPeer(sender);
        ZDO? player = ZDOMan.instance?.GetZDO(playerId);
        return peer != null && peer.m_characterID.Equals(playerId) && player != null && player.GetOwner() == sender;
    }

    private static bool IsMilkServer() => ZNet.instance != null && ZNet.instance.IsServer() && ZRoutedRpc.instance != null;
    private static bool IsMilkServerMessage(long sender) => ZRoutedRpc.instance != null && sender != 0L && sender == ZRoutedRpc.instance.GetServerPeerID();
    private static bool IsMilkGuid(string value) => Guid.TryParseExact(value, "N", out _);
    private static bool IsMilkFailureToken(string token) => token == "$flg_cuisine_milk_busy" || token == "$flg_cuisine_milk_unavailable" ||
        token == "$flg_cuisine_milk_cooldown" || token == "$flg_cuisine_milk_breeding" || token == "$flg_cuisine_milk_too_far";

    private sealed class MilkPeerSession
    {
        internal ZDOID PlayerId;
        internal string Nonce = string.Empty;
        internal string Cookie = string.Empty;
        internal long LastSequence;
        internal MilkServerRequest? LastRequest;
    }

    private sealed class MilkClientRequest
    {
        internal string Id = string.Empty;
        internal ZDOID PlayerId;
        internal ZDOID TargetId;
        internal long Sequence;
        internal float Started;
        internal ZRoutedRpc Rpc = null!;
    }

    private sealed class MilkServerRequest
    {
        internal string Id = string.Empty;
        internal string Cookie = string.Empty;
        internal ZDOID PlayerId;
        internal ZDOID TargetId;
        internal long Sequence;
        internal long Sender;
        internal long Owner;
        internal long Until;
        internal long ExpectedUntil;
        internal string ExpectedPrevious = string.Empty;
        internal int Amount;
        internal Vector3 Position;
        internal bool Complete;
        internal bool Success;
        internal string Reason = string.Empty;
        internal ZRoutedRpc Rpc = null!;
    }
}
