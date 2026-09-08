using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace FeedLikeGrandma;

internal static class FeedDragonFlightRestrictionSystem
{
    private const string FlightControlsTypeName = "BetterRiding.Behaviour.FlightControls";
    private const string FlightControlsPlayerSetControlsPatchTypeName = "BetterRiding.Behaviour.FlightControls+Ygg_PlayerSetControls";
    private const string MountControllerTypeName = "BetterRiding.Behaviour.MountController";
    private const string MountControllerPlayerUpdatePatchTypeName = "BetterRiding.Behaviour.MountController+Player_Update_Patch";
    private const string ExpandWorldBiomeManagerTypeName = "ExpandWorldData.BiomeManager";
    private const string BiomeRestrictedMessage = "$flg_dragon_flight_restricted";
    private const string EncumberedRestrictedMessage = "$flg_dragon_flight_encumbered";

    private static readonly Dictionary<int, RestrictionState> PlayerStates = new();
    private static readonly Dictionary<string, Heightmap.Biome> ResolvedBiomeNames = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, Type?> TypeCache = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, MethodInfo?> MethodCache = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, FieldInfo?> FieldCache = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, ConfigEntry<KeyCode>?> FlightKeyConfigCache = new(StringComparer.Ordinal);
    private static readonly Dictionary<KeyCode, string> JoystickButtonNameCache = new();

    private static float _lastMessageTime = -100f;
    private static bool _isDragonDelegateSearched;
    private static Func<Sadle, bool>? _isDragonDelegate;
    private static MethodInfo? _isDragonMethod;
    private static readonly object[] IsDragonArgs = new object[1];
    private static string _cachedDefaultRestrictedBiomeText = string.Empty;
    private static IReadOnlyList<string> _cachedDefaultRestrictedBiomes = Array.Empty<string>();

    internal static MethodBase? GetBetterRidingSetControlsPrefix()
    {
        return GetMethod(FlightControlsPlayerSetControlsPatchTypeName, "Prefix");
    }

    internal static MethodBase? GetBetterRidingPlayerUpdatePostfix()
    {
        return GetMethod(MountControllerPlayerUpdatePatchTypeName, "Postfix");
    }

    internal static void Shutdown()
    {
        PlayerStates.Clear();
        ResolvedBiomeNames.Clear();
        TypeCache.Clear();
        MethodCache.Clear();
        FieldCache.Clear();
        FlightKeyConfigCache.Clear();
        JoystickButtonNameCache.Clear();
        _lastMessageTime = -100f;
        _isDragonDelegateSearched = false;
        _isDragonDelegate = null;
        _isDragonMethod = null;
        IsDragonArgs[0] = null!;
        _cachedDefaultRestrictedBiomeText = string.Empty;
        _cachedDefaultRestrictedBiomes = Array.Empty<string>();
    }

    internal static bool BlockFlightToggleIfRestricted(Player player, ref bool jump)
    {
        if (jump || !IsFlightToggleKeyDown())
        {
            return true;
        }

        return !TryCancelRestrictedFlightToggle(player);
    }

    internal static void CancelFlightToggleAfterSetControls(Player player)
    {
        if (!IsFlightToggleKeyDown())
        {
            return;
        }

        TryCancelRestrictedFlightToggle(player);
    }

    private static bool TryCancelRestrictedFlightToggle(Player player)
    {
        if (!TryGetRidingDragon(player, out _, out Character? mount) || (object?)mount == null)
        {
            return false;
        }

        if (IsEncumberedFlightBlocked(player))
        {
            CancelFlying(mount);
            ResetBetterRidingFlightKeyState();
            ShowEncumberedRestrictedMessage(player);
            return true;
        }

        Heightmap.Biome biome = GetFreshBiome(player, mount);
        if (!RefreshRestrictionState(player, biome, cancelFlying: true, showWhenCanceled: false))
        {
            return false;
        }

        CancelFlying(mount);
        ResetBetterRidingFlightKeyState();
        ShowBiomeRestrictedMessage(player);
        return true;
    }

    internal static void EnforceRestrictedFlightBeforeSaddleControls(Sadle saddle)
    {
        Player? player = Player.m_localPlayer;
        if ((object?)player == null ||
            (object?)saddle == null ||
            !ReferenceEquals(player.m_doodadController, saddle) ||
            !IsBetterRidingDragon(saddle))
        {
            return;
        }

        Character? mount = saddle.GetCharacter();
        if ((object?)mount == null)
        {
            return;
        }

        int playerId = GetInstanceId(player);
        int mountId = GetInstanceId(mount);
        Heightmap.Biome biome = player.GetCurrentBiome();
        bool biomeRestricted;
        if (playerId != 0 &&
            PlayerStates.TryGetValue(playerId, out RestrictionState state) &&
            state.MountId == mountId &&
            state.Biome == biome)
        {
            biomeRestricted = state.Restricted;
        }
        else
        {
            biomeRestricted = RefreshRestrictionState(player, biome, cancelFlying: false, showWhenCanceled: false);
        }

        if (!IsEncumberedFlightBlocked(player) && !biomeRestricted)
        {
            return;
        }

        if (!mount.m_flying && !IsFlightToggleKeyHeld())
        {
            return;
        }

        CancelFlying(mount);
        ResetBetterRidingFlightKeyState();
    }

    internal static bool AllowBetterRidingPlayerUpdate(Player player)
    {
        if (TryGetRidingDragon(player, out _, out Character? encumberedMount) &&
            (object?)encumberedMount != null &&
            IsEncumberedFlightBlocked(player))
        {
            bool wasFlying = encumberedMount.m_flying;
            bool flightInput = IsFlightToggleKeyHeld();
            if (wasFlying || flightInput)
            {
                CancelFlying(encumberedMount);
                ResetBetterRidingFlightKeyState();
                ShowEncumberedRestrictedMessage(player);
            }

            return false;
        }

        int playerId = GetInstanceId(player);
        if (playerId == 0 ||
            !PlayerStates.TryGetValue(playerId, out RestrictionState state) ||
            !state.Restricted)
        {
            if (IsFlightToggleKeyHeld() &&
                TryGetRestrictedRidingDragon(player, out Character? restrictedMount))
            {
                CancelFlying(restrictedMount);
                ResetBetterRidingFlightKeyState();
                ShowBiomeRestrictedMessage(player);
                return false;
            }

            return true;
        }

        if (!TryGetRidingDragon(player, out _, out Character? mount) || mount == null)
        {
            PlayerStates.Remove(playerId);
            return true;
        }

        bool restrictedFlightInput = IsFlightToggleKeyHeld();
        if (mount.m_flying || restrictedFlightInput)
        {
            CancelFlying(mount);
            ResetBetterRidingFlightKeyState();
            if (restrictedFlightInput)
            {
                ShowBiomeRestrictedMessage(player);
            }
        }

        return false;
    }

    internal static void OnBiomeChanged(Player player, Heightmap.Biome biome)
    {
        RefreshRestrictionState(player, biome, cancelFlying: true, showWhenCanceled: true);
    }

    internal static void OnStartDoodadControl(Player player)
    {
        if (!TryGetRidingDragon(player, out _, out Character? mount) || (object?)mount == null)
        {
            ClearRestrictionState(player);
            return;
        }

        Heightmap.Biome biome = GetFreshBiome(player, mount);
        RefreshRestrictionState(player, biome, cancelFlying: true, showWhenCanceled: false);
    }

    internal static void ClearRestrictionState(Player player)
    {
        int playerId = GetInstanceId(player);
        if (playerId != 0)
        {
            PlayerStates.Remove(playerId);
        }
    }

    internal static void OnRulesChanged()
    {
        PlayerStates.Clear();
        ResolvedBiomeNames.Clear();
        Player? localPlayer = Player.m_localPlayer;
        if ((object?)localPlayer != null && localPlayer.IsRiding())
        {
            OnStartDoodadControl(localPlayer);
        }
    }

    private static bool RefreshRestrictionState(Player player, Heightmap.Biome biome, bool cancelFlying, bool showWhenCanceled)
    {
        int playerId = GetInstanceId(player);
        if (playerId == 0)
        {
            return false;
        }

        if (!TryGetRidingDragon(player, out _, out Character? mount) || (object?)mount == null)
        {
            PlayerStates.Remove(playerId);
            return false;
        }

        bool restricted = IsBiomeRestrictedForDragon(mount, biome);
        PlayerStates[playerId] = new RestrictionState
        {
            MountId = GetInstanceId(mount),
            Biome = biome,
            Restricted = restricted
        };

        if (restricted && cancelFlying && mount.m_flying)
        {
            CancelFlying(mount);
            ResetBetterRidingFlightKeyState();
            if (showWhenCanceled)
            {
                ShowBiomeRestrictedMessage(player);
            }
        }

        return restricted;
    }

    private static bool TryGetRestrictedRidingDragon(Player player, out Character? mount)
    {
        mount = null;
        if (!TryGetRidingDragon(player, out _, out mount) || (object?)mount == null)
        {
            ClearRestrictionState(player);
            return false;
        }

        Heightmap.Biome biome = GetFreshBiome(player, mount);
        return RefreshRestrictionState(player, biome, cancelFlying: true, showWhenCanceled: false);
    }

    private static bool TryGetRidingDragon(Player player, out Sadle? saddle, out Character? mount)
    {
        saddle = null;
        mount = null;
        if ((object?)player == null || !player.IsRiding())
        {
            return false;
        }

        saddle = player.m_doodadController as Sadle;
        if ((object?)saddle == null || !IsBetterRidingDragon(saddle))
        {
            return false;
        }

        mount = saddle.GetCharacter();
        return (object?)mount != null;
    }

    private static bool IsBetterRidingDragon(Sadle saddle)
    {
        if (!_isDragonDelegateSearched)
        {
            _isDragonMethod = GetMethod(MountControllerTypeName, "isDragon", typeof(Sadle));
            if (_isDragonMethod != null)
            {
                _isDragonDelegate = Delegate.CreateDelegate(typeof(Func<Sadle, bool>), _isDragonMethod, throwOnBindFailure: false) as Func<Sadle, bool>;
            }

            _isDragonDelegateSearched = true;
        }

        if (_isDragonDelegate != null)
        {
            return _isDragonDelegate(saddle);
        }

        if (_isDragonMethod == null)
        {
            return false;
        }

        try
        {
            IsDragonArgs[0] = saddle;
            return _isDragonMethod.Invoke(null, IsDragonArgs) is true;
        }
        catch (Exception ex)
        {
            FeedLikeGrandmaPlugin.Log.LogDebug($"BetterRiding dragon check failed: {ex.Message}");
            return false;
        }
        finally
        {
            IsDragonArgs[0] = null!;
        }
    }

    private static bool IsBiomeRestrictedForDragon(Character mount, Heightmap.Biome biome)
    {
        IReadOnlyList<string> restrictedBiomes = GetRestrictedBiomeNames(mount);
        if (restrictedBiomes.Count == 0)
        {
            return false;
        }

        return restrictedBiomes.Any(name => MatchesBiome(biome, name));
    }

    private static IReadOnlyList<string> GetRestrictedBiomeNames(Character mount)
    {
        if (FeedSystem.TryGetRestrictedBiomes(mount, out IReadOnlyList<string> prefabRestrictedBiomes))
        {
            return prefabRestrictedBiomes;
        }

        return GetDefaultRestrictedBiomeNames();
    }

    private static IReadOnlyList<string> GetDefaultRestrictedBiomeNames()
    {
        string text = FeedLikeGrandmaPlugin.DefaultDragonRestrictedBiomes.Value ?? string.Empty;
        if (string.Equals(_cachedDefaultRestrictedBiomeText, text, StringComparison.Ordinal))
        {
            return _cachedDefaultRestrictedBiomes;
        }

        _cachedDefaultRestrictedBiomeText = text;
        _cachedDefaultRestrictedBiomes = SplitBiomeList(text);
        return _cachedDefaultRestrictedBiomes;
    }

    private static List<string> SplitBiomeList(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return new List<string>();
        }

        return text
            .Trim()
            .Trim('[', ']')
            .Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(part => part.Trim().Trim('\'', '"'))
            .Where(part => part.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static bool MatchesBiome(Heightmap.Biome currentBiome, string restrictedName)
    {
        if (currentBiome == Heightmap.Biome.None || string.IsNullOrWhiteSpace(restrictedName))
        {
            return false;
        }

        if (TryResolveBiomeName(restrictedName, out Heightmap.Biome restrictedBiome) &&
            restrictedBiome != Heightmap.Biome.None &&
            (currentBiome == restrictedBiome || (currentBiome & restrictedBiome) != 0))
        {
            return true;
        }

        string normalizedRestrictedName = NormalizeBiomeName(restrictedName);
        if (NormalizeBiomeName(currentBiome.ToString()).Equals(normalizedRestrictedName, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return TryGetExpandWorldBiomeDisplayName(currentBiome, out string displayName) &&
               NormalizeBiomeName(displayName).Equals(normalizedRestrictedName, StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryResolveBiomeName(string name, out Heightmap.Biome biome)
    {
        biome = Heightmap.Biome.None;
        string normalizedName = name.Trim();
        if (normalizedName.Length == 0)
        {
            return false;
        }

        if (ResolvedBiomeNames.TryGetValue(normalizedName, out biome))
        {
            return true;
        }

        if (Enum.TryParse(normalizedName, ignoreCase: true, out Heightmap.Biome vanillaBiome))
        {
            biome = vanillaBiome;
            ResolvedBiomeNames[normalizedName] = biome;
            return true;
        }

        if (int.TryParse(normalizedName, out int rawBiome))
        {
            biome = (Heightmap.Biome)rawBiome;
            ResolvedBiomeNames[normalizedName] = biome;
            return true;
        }

        if (TryGetExpandWorldBiome(normalizedName, out biome))
        {
            ResolvedBiomeNames[normalizedName] = biome;
            return true;
        }

        return false;
    }

    private static bool TryGetExpandWorldBiome(string name, out Heightmap.Biome biome)
    {
        biome = Heightmap.Biome.None;
        Type? type = FindType(ExpandWorldBiomeManagerTypeName);
        MethodInfo? method = type == null
            ? null
            : AccessTools.Method(type, "TryGetBiome", new[] { typeof(string), typeof(Heightmap.Biome).MakeByRefType() });
        if (method == null)
        {
            return false;
        }

        object[] args = { name, Heightmap.Biome.None };
        try
        {
            if (method.Invoke(null, args) is true)
            {
                biome = (Heightmap.Biome)args[1];
                return true;
            }
        }
        catch (Exception ex)
        {
            FeedLikeGrandmaPlugin.Log.LogDebug($"Expand World Data biome lookup failed for '{name}': {ex.Message}");
        }

        return false;
    }

    private static bool TryGetExpandWorldBiomeDisplayName(Heightmap.Biome biome, out string displayName)
    {
        displayName = string.Empty;
        Type? type = FindType(ExpandWorldBiomeManagerTypeName);
        MethodInfo? method = type == null
            ? null
            : AccessTools.Method(type, "TryGetDisplayName", new[] { typeof(Heightmap.Biome), typeof(string).MakeByRefType() });
        if (method == null)
        {
            return false;
        }

        object[] args = { biome, string.Empty };
        try
        {
            if (method.Invoke(null, args) is true)
            {
                displayName = args[1] as string ?? string.Empty;
                return displayName.Length > 0;
            }
        }
        catch (Exception ex)
        {
            FeedLikeGrandmaPlugin.Log.LogDebug($"Expand World Data biome display lookup failed for '{biome}': {ex.Message}");
        }

        return false;
    }

    private static string NormalizeBiomeName(string name)
    {
        return new string(name.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
    }

    private static Heightmap.Biome GetFreshBiome(Player player, Character mount)
    {
        Heightmap.Biome biome = Heightmap.FindBiome(((Component)mount).transform.position);
        return biome == Heightmap.Biome.None ? player.GetCurrentBiome() : biome;
    }

    private static void CancelFlying(Character? mount)
    {
        if ((object?)mount == null)
        {
            return;
        }

        mount.m_flying = false;
        Animator? animator = ((Component)mount).GetComponentInChildren<Animator>();
        if ((object?)animator != null)
        {
            animator.SetBool("IsAscending", false);
            animator.SetBool("IsDescending", false);
        }
    }

    private static bool IsFlightToggleKeyDown()
    {
        return GetKeyDown(GetBetterRidingKey("FlightToggleKey")) ||
               GetKeyDown(GetBetterRidingKey("FlightToggleKeyController"));
    }

    private static bool IsFlightToggleKeyHeld()
    {
        return GetKey(GetBetterRidingKey("FlightToggleKey")) ||
               GetKey(GetBetterRidingKey("FlightToggleKeyController")) ||
               GetBetterRidingWasKeyPressed();
    }

    private static KeyCode GetBetterRidingKey(string fieldName)
    {
        if (!FlightKeyConfigCache.TryGetValue(fieldName, out ConfigEntry<KeyCode>? configEntry))
        {
            FieldInfo? field = GetField(FlightControlsTypeName, fieldName);
            configEntry = field?.GetValue(null) as ConfigEntry<KeyCode>;
            FlightKeyConfigCache[fieldName] = configEntry;
        }

        return configEntry?.Value ?? KeyCode.None;
    }

    private static bool GetKeyDown(KeyCode keyCode)
    {
        if (keyCode == KeyCode.None)
        {
            return false;
        }

        if (IsJoystickButton(keyCode))
        {
            return ZInput.GetButtonDown(GetJoystickButtonName(keyCode));
        }

        return Input.GetKeyDown(keyCode) || ZInput.GetKeyDown(keyCode, true);
    }

    private static bool GetKey(KeyCode keyCode)
    {
        if (keyCode == KeyCode.None)
        {
            return false;
        }

        if (IsJoystickButton(keyCode))
        {
            return ZInput.GetButton(GetJoystickButtonName(keyCode));
        }

        return Input.GetKey(keyCode) || ZInput.GetKey(keyCode, true);
    }

    private static bool IsJoystickButton(KeyCode keyCode)
    {
        int raw = (int)keyCode;
        return raw >= 330 && raw <= 509;
    }

    private static string GetJoystickButtonName(KeyCode keyCode)
    {
        if (JoystickButtonNameCache.TryGetValue(keyCode, out string? cachedName))
        {
            return cachedName;
        }

        int buttonNumber = GetButtonNumber(keyCode);
        string buttonName = buttonNumber switch
        {
            0 => "JoyJump",
            1 => "JoyAttack",
            2 => "JoyBlock",
            3 => "JoyUse",
            4 => "JoyLTrigger",
            5 => "JoyRTrigger",
            6 => "JoyMap",
            7 => "JoyMenu",
            8 => "JoyLStick",
            9 => "JoyRStick",
            10 => "JoyDPadLeft",
            11 => "JoyDPadRight",
            12 => "JoyDPadUp",
            13 => "JoyDPadDown",
            >= 0 => $"JoyButton{buttonNumber}",
            _ => "JoyJump"
        };
        JoystickButtonNameCache[keyCode] = buttonName;
        return buttonName;
    }

    private static int GetButtonNumber(KeyCode keyCode)
    {
        string text = keyCode.ToString();
        int index = text.LastIndexOf("Button", StringComparison.OrdinalIgnoreCase);
        if (index < 0)
        {
            return -1;
        }

        return int.TryParse(text.Substring(index + 6), out int result) ? result : -1;
    }

    private static bool GetBetterRidingWasKeyPressed()
    {
        FieldInfo? field = GetBetterRidingWasKeyPressedField();
        return field?.GetValue(null) is true;
    }

    private static void ResetBetterRidingFlightKeyState()
    {
        GetBetterRidingWasKeyPressedField()?.SetValue(null, false);
    }

    private static FieldInfo? GetBetterRidingWasKeyPressedField()
    {
        return GetField(MountControllerPlayerUpdatePatchTypeName, "wasKeyPressed");
    }

    private static bool IsEncumberedFlightBlocked(Player player)
    {
        return FeedLikeGrandmaPlugin.BlockEncumberedDragonFlight.Value == FeedLikeGrandmaPlugin.Toggle.On &&
               (object?)player != null &&
               FeedRidingSystem.IsPlayerEncumberedCached(player);
    }

    private static void ShowBiomeRestrictedMessage(Player player)
    {
        ShowRestrictedMessage(player, BiomeRestrictedMessage);
    }

    private static void ShowEncumberedRestrictedMessage(Player player)
    {
        ShowRestrictedMessage(player, EncumberedRestrictedMessage);
    }

    private static void ShowRestrictedMessage(Player player, string message)
    {
        if ((object?)player == null || Time.time - _lastMessageTime < 1f)
        {
            return;
        }

        _lastMessageTime = Time.time;
        FeedLocalization.Message(player, message);
    }

    private static int GetInstanceId(UnityEngine.Object? obj)
    {
        return (object?)obj == null ? 0 : obj.GetInstanceID();
    }

    private static Type? FindType(string typeName)
    {
        if (TypeCache.TryGetValue(typeName, out Type? cachedType))
        {
            return cachedType;
        }

        foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            Type? type = assembly.GetType(typeName, throwOnError: false);
            if (type != null)
            {
                TypeCache[typeName] = type;
                return type;
            }
        }

        TypeCache[typeName] = null;
        return null;
    }

    private static MethodInfo? GetMethod(string typeName, string methodName, params Type[] parameterTypes)
    {
        string key = parameterTypes.Length == 0
            ? $"{typeName}.{methodName}"
            : $"{typeName}.{methodName}({string.Join(",", parameterTypes.Select(type => type.FullName))})";
        if (MethodCache.TryGetValue(key, out MethodInfo? cachedMethod))
        {
            return cachedMethod;
        }

        Type? type = FindType(typeName);
        MethodInfo? method = type == null
            ? null
            : parameterTypes.Length == 0
                ? AccessTools.Method(type, methodName)
                : AccessTools.Method(type, methodName, parameterTypes);
        MethodCache[key] = method;
        return method;
    }

    private static FieldInfo? GetField(string typeName, string fieldName)
    {
        string key = $"{typeName}.{fieldName}";
        if (FieldCache.TryGetValue(key, out FieldInfo? cachedField))
        {
            return cachedField;
        }

        Type? type = FindType(typeName);
        FieldInfo? field = type == null ? null : AccessTools.Field(type, fieldName);
        FieldCache[key] = field;
        return field;
    }

    private sealed class RestrictionState
    {
        public int MountId { get; init; }
        public Heightmap.Biome Biome { get; init; }
        public bool Restricted { get; init; }
    }
}

[HarmonyPatch]
internal static class BetterRidingFlightToggleRestrictionPatch
{
    private static bool Prepare()
    {
        return FeedDragonFlightRestrictionSystem.GetBetterRidingSetControlsPrefix() != null;
    }

    private static MethodBase? TargetMethod()
    {
        return FeedDragonFlightRestrictionSystem.GetBetterRidingSetControlsPrefix();
    }

    [HarmonyPriority(Priority.First)]
    private static bool Prefix(Player __instance, ref bool jump)
    {
        return FeedDragonFlightRestrictionSystem.BlockFlightToggleIfRestricted(__instance, ref jump);
    }
}

[HarmonyPatch]
internal static class BetterRidingPlayerUpdateFlightRestrictionPatch
{
    private static bool Prepare()
    {
        return FeedDragonFlightRestrictionSystem.GetBetterRidingPlayerUpdatePostfix() != null;
    }

    private static MethodBase? TargetMethod()
    {
        return FeedDragonFlightRestrictionSystem.GetBetterRidingPlayerUpdatePostfix();
    }

    [HarmonyPriority(Priority.First)]
    private static bool Prefix(Player __instance)
    {
        return FeedDragonFlightRestrictionSystem.AllowBetterRidingPlayerUpdate(__instance);
    }
}

[HarmonyPatch(typeof(Player), nameof(Player.SetControls))]
internal static class PlayerSetControlsDragonFlightRestrictionPatch
{
    private static bool Prepare()
    {
        return FeedBetterRidingCompatibility.HasBetterRiding;
    }

    [HarmonyPriority(Priority.Last)]
    [HarmonyAfter("Yggdrah.BetterRiding")]
    private static void Prefix(Player __instance)
    {
        FeedDragonFlightRestrictionSystem.CancelFlightToggleAfterSetControls(__instance);
    }
}

[HarmonyPatch(typeof(Sadle), nameof(Sadle.ApplyControlls))]
internal static class SaddleApplyControlsDragonFlightRestrictionPatch
{
    private static bool Prepare()
    {
        return FeedBetterRidingCompatibility.HasBetterRiding;
    }

    [HarmonyPriority(Priority.First)]
    [HarmonyBefore("Yggdrah.BetterRiding")]
    private static void Prefix(Sadle __instance)
    {
        FeedDragonFlightRestrictionSystem.EnforceRestrictedFlightBeforeSaddleControls(__instance);
    }
}

[HarmonyPatch(typeof(Player), nameof(Player.AddKnownBiome))]
internal static class PlayerAddKnownBiomeDragonFlightRestrictionPatch
{
    private static bool Prepare()
    {
        return FeedBetterRidingCompatibility.HasBetterRiding;
    }

    private static void Postfix(Player __instance, Heightmap.Biome biome)
    {
        FeedDragonFlightRestrictionSystem.OnBiomeChanged(__instance, biome);
    }
}

[HarmonyPatch(typeof(Player), nameof(Player.StartDoodadControl))]
internal static class PlayerStartDoodadControlDragonFlightRestrictionPatch
{
    private static bool Prepare()
    {
        return FeedBetterRidingCompatibility.HasBetterRiding;
    }

    private static void Postfix(Player __instance)
    {
        FeedDragonFlightRestrictionSystem.OnStartDoodadControl(__instance);
    }
}

[HarmonyPatch(typeof(Player), nameof(Player.StopDoodadControl))]
internal static class PlayerStopDoodadControlDragonFlightRestrictionPatch
{
    private static bool Prepare()
    {
        return FeedBetterRidingCompatibility.HasBetterRiding;
    }

    private static void Prefix(Player __instance)
    {
        FeedDragonFlightRestrictionSystem.ClearRestrictionState(__instance);
    }
}
