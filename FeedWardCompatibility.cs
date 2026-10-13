using System;
using System.Reflection;
using BepInEx.Bootstrap;
using UnityEngine;

namespace FeedLikeGrandma;

internal static class FeedWardCompatibility
{
    internal const string PluginGuid = "sighsorry.STUWard";
    internal const string DeniedToken = "$flg_taming_orb_ward_denied";
    private static Type? _pluginType;
    private static Func<string, long, Vector3, Vector3, bool>? _check;
    private static bool _warned;

    internal static bool IsAllowed(Player player, Vector3 targetPosition) =>
        IsAllowed(player.GetPlayerID(), player.transform.position, targetPosition);

    // requester must come from an authenticated Orb request, never from a network owner ID.
    internal static bool IsAllowed(ZDO? requester, Vector3 targetPosition) =>
        requester != null && IsAllowed(requester.GetLong(ZDOVars.s_playerID), requester.GetPosition(), targetPosition);

    private static bool IsAllowed(long playerId, Vector3 playerPosition, Vector3 targetPosition)
    {
        if (!Chainloader.PluginInfos.TryGetValue(PluginGuid, out var plugin)) return true;
        try
        {
            Type? pluginType = plugin.Instance?.GetType();
            if (pluginType == null) return false;
            if (_pluginType != pluginType)
            {
                _pluginType = pluginType;
                _check = null;
                _warned = false;
                MethodInfo? method = pluginType.Assembly.GetType("STUWard.WardAccessApi")?.GetMethod(
                    "IsItemUseAllowed", BindingFlags.Public | BindingFlags.Static, null,
                    new[] { typeof(string), typeof(long), typeof(Vector3), typeof(Vector3) }, null);
                if (method?.ReturnType != typeof(bool))
                    throw new MissingMethodException("STUWard.WardAccessApi.IsItemUseAllowed");
                _check = (Func<string, long, Vector3, Vector3, bool>)Delegate.CreateDelegate(
                    typeof(Func<string, long, Vector3, Vector3, bool>), method);
            }
            return _check != null && _check(TamingOrbSystem.PrefabName, playerId, playerPosition, targetPosition);
        }
        catch (Exception exception)
        {
            if (!_warned)
            {
                _warned = true;
                FeedLikeGrandmaPlugin.Log.LogWarning(
                    $"STUWard Orb access check failed; capture/release is blocked. Update both mods on server and clients. {exception.Message}");
            }
            return false;
        }
    }
}
