using System;
using System.Globalization;
using System.Reflection;
using BepInEx.Bootstrap;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace FeedLikeGrandma;

// Optional ValheimCuisine bridge, activated by its milking API rather than version.
// The local SE in the inspected Cuisine 2.3.2 is not a durable or
// network-wide cooldown. Only the current animal owner writes this ZDO record.
internal static partial class FeedCuisineCompatibility
{
    internal const string PluginGuid = "XutzBR.ValheimCuisine";
    private const string StateKey = "flg.vc.milk.v1";
    private const long MaxCooldownTicks = 2 * TimeSpan.TicksPerHour;
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;
    private static ConfigEntry<float>? _cooldown;
    private static ConfigEntry<int>? _amount;
    private static ConfigEntry<bool>? _breeding;
    private static ConfigEntry<KeyboardShortcut>? _key;
    private static bool _installed;
    private static float _lastInputTime;
    private static Patch? _originalUpdatePatch;
    private static Patch? _originalHoverPatch;
    private static MethodBase? _playerUpdate;
    private static MethodBase? _tameableHover;
    private static MethodBase? _milkHelper;
    private static Harmony? _compatHarmony;
    internal static bool Ready { get; private set; }
    internal static float CooldownSeconds => Math.Max(60f, Math.Min(7200f,
        _cooldown != null && !float.IsNaN(_cooldown.Value) ? _cooldown.Value : 1800f));
    internal static int MilkAmount => Math.Max(1, Math.Min(20, _amount?.Value ?? 5));
    internal static bool RequireBreeding => _breeding?.Value ?? true;

    internal static void Initialize(Harmony harmony)
    {
        if (!Chainloader.PluginInfos.TryGetValue(PluginGuid, out var plugin)) return;
        _installed = true;
        _compatHarmony = harmony;
        try
        {
            Type type = plugin.Instance.GetType();
            _cooldown = ReadConfig<float>(type, "CooldownDuration");
            _amount = ReadConfig<int>(type, "MilkAmount");
            _breeding = ReadConfig<bool>(type, "RequireBreeding");
            _key = ReadConfig<KeyboardShortcut>(type, "MilkingKey");
            Type update = type.GetNestedType("LoxMilkingUpdatePatch", BindingFlags.Public | BindingFlags.NonPublic)
                ?? throw new MissingMemberException(type.FullName, "LoxMilkingUpdatePatch");
            Type hover = type.GetNestedType("LoxMilkingHoverTextPatch", BindingFlags.Public | BindingFlags.NonPublic)
                ?? throw new MissingMemberException(type.FullName, "LoxMilkingHoverTextPatch");
            MethodInfo updatePostfix = ReadMilkingMethod(update, "Postfix", typeof(Player));
            MethodInfo hoverPostfix = ReadMilkingMethod(hover, "Postfix", typeof(Tameable), typeof(string).MakeByRefType());
            MethodInfo milk = ReadMilkingMethod(update, "MilkLox", typeof(Player), typeof(Character), typeof(Tameable));
            _milkHelper = milk;
            _playerUpdate = AccessTools.DeclaredMethod(typeof(Player), "Update");
            _tameableHover = AccessTools.DeclaredMethod(typeof(Tameable), "GetHoverText");
            _originalUpdatePatch = FindPostfix(_playerUpdate, updatePostfix);
            _originalHoverPatch = FindPostfix(_tameableHover, hoverPostfix);
            if (_originalUpdatePatch == null || _originalHoverPatch == null)
                throw new InvalidOperationException("ValheimCuisine milking patches are not installed.");

            // Replace only the two inspected postfixes. Patching an already compiled
            // patch method itself can miss calls inlined into Harmony's wrappers.
            harmony.Unpatch(_playerUpdate, updatePostfix);
            harmony.Unpatch(_tameableHover, hoverPostfix);
            // Preserve the owner ID as well: other mods can order against Cuisine.
            new Harmony(_originalUpdatePatch.owner).Patch(_playerUpdate, postfix: Replacement(nameof(OnPlayerUpdate), _originalUpdatePatch));
            new Harmony(_originalHoverPatch.owner).Patch(_tameableHover, postfix: Replacement(nameof(OnHoverText), _originalHoverPatch));
            // Keep direct reflective callers of Cuisine's helper on the same path.
            harmony.Patch(milk, prefix: new HarmonyMethod(typeof(FeedCuisineCompatibility), nameof(RouteMilk)));
            Ready = true;
            FeedLikeGrandmaPlugin.Log.LogInfo($"ValheimCuisine {plugin.Metadata.Version}: compatible milking API found; persistent, owner-validated Lox milking enabled.");
        }
        catch (Exception ex)
        {
            FeedLikeGrandmaPlugin.Log.LogError($"ValheimCuisine {plugin.Metadata.Version} compatibility unavailable; Lox Orb transactions are blocked: {ex}");
            RemoveReplacement(harmony, _playerUpdate, nameof(OnPlayerUpdate));
            RemoveReplacement(harmony, _tameableHover, nameof(OnHoverText));
            RemoveReplacement(harmony, _milkHelper, nameof(RouteMilk));
            RestorePostfixes();
        }
    }

    private static ConfigEntry<T> ReadConfig<T>(Type type, string name)
    {
        FieldInfo? field = AccessTools.Field(type, name);
        if (field == null || !field.IsStatic || field.FieldType != typeof(ConfigEntry<T>))
            throw new MissingFieldException(type.FullName, name);
        return field.GetValue(null) as ConfigEntry<T>
            ?? throw new InvalidOperationException($"ValheimCuisine config {name} is not initialized.");
    }

    private static MethodInfo ReadMilkingMethod(Type type, string name, params Type[] parameters)
    {
        MethodInfo? method = AccessTools.DeclaredMethod(type, name, parameters);
        if (method == null || !method.IsStatic || method.ContainsGenericParameters || method.ReturnType != typeof(void))
            throw new MissingMethodException(type.FullName, name);
        ParameterInfo[] actual = method.GetParameters();
        if (actual.Length != parameters.Length) throw new MissingMethodException(type.FullName, name);
        for (int i = 0; i < actual.Length; i++)
            if (actual[i].ParameterType != parameters[i]) throw new MissingMethodException(type.FullName, name);
        return method;
    }

    private static Patch? FindPostfix(MethodBase original, MethodInfo method)
    {
        var patches = Harmony.GetPatchInfo(original);
        if (patches != null)
            foreach (Patch patch in patches.Postfixes)
                if (patch.PatchMethod == method) return patch;
        return null;
    }

    private static HarmonyMethod Replacement(string name, Patch patch) => new(typeof(FeedCuisineCompatibility), name)
    { priority = patch.priority, before = patch.before, after = patch.after };

    private static void RemoveReplacement(Harmony harmony, MethodBase? original, string name)
    {
        if (original != null) harmony.Unpatch(original, AccessTools.DeclaredMethod(typeof(FeedCuisineCompatibility), name));
    }

    private static void RestorePostfixes()
    {
        try { RestorePostfix(_playerUpdate, _originalUpdatePatch); }
        catch (Exception ex) { FeedLikeGrandmaPlugin.Log.LogError($"Could not restore Cuisine input patch: {ex}"); }
        try { RestorePostfix(_tameableHover, _originalHoverPatch); }
        catch (Exception ex) { FeedLikeGrandmaPlugin.Log.LogError($"Could not restore Cuisine hover patch: {ex}"); }
        _originalUpdatePatch = _originalHoverPatch = null;
    }

    private static void RestorePostfix(MethodBase? original, Patch? patch)
    {
        if (original == null || patch == null || FindPostfix(original, patch.PatchMethod) != null) return;
        new Harmony(patch.owner).Patch(original, postfix: new HarmonyMethod(patch.PatchMethod)
        { priority = patch.priority, before = patch.before, after = patch.after });
    }

    internal static void Shutdown()
    {
        Ready = false;
        EndNetworkSession();
        if (_compatHarmony != null)
        {
            RemoveReplacement(_compatHarmony, _playerUpdate, nameof(OnPlayerUpdate));
            RemoveReplacement(_compatHarmony, _tameableHover, nameof(OnHoverText));
            RemoveReplacement(_compatHarmony, _milkHelper, nameof(RouteMilk));
        }
        RestorePostfixes();
        _installed = false;
        _cooldown = null;
        _amount = null;
        _breeding = null;
        _key = null;
        _compatHarmony = null;
        _lastInputTime = -1f;
    }

    private static void OnPlayerUpdate(Player __instance)
    {
        if (!Ready || __instance != Player.m_localPlayer) return;
        if (_key == null || !IsMilkingKeyDown(_key.Value) || Time.unscaledTime - _lastInputTime < 0.5f) return;
        _lastInputTime = Time.unscaledTime;
        if (!HasBucket(__instance)) return;
        GameObject hover = __instance.GetHoverObject();
        Tameable? tameable = hover == null ? null : hover.GetComponentInParent<Tameable>();
        Character? character = tameable == null ? null : tameable.GetComponent<Character>();
        if (character != null && IsLox(character) && character.IsTamed() &&
            Vector3.Distance(__instance.transform.position, character.transform.position) <= 3f)
            RequestMilk(__instance, character);
    }

    private static bool RouteMilk(Player __0, Character __1)
    {
        if (Ready && __0 == Player.m_localPlayer) RequestMilk(__0, __1);
        return !Ready;
    }

    private static bool HasBucket(Player player) => player.RightItem?.m_dropPrefab != null &&
        player.RightItem.m_dropPrefab.name == "VC_MilkinBucket";

    private static bool IsMilkingKeyDown(KeyboardShortcut key)
    {
        // Same modifier policy as Cuisine.KeyboardExtensions.IsKeyDown.
        if (key.MainKey == KeyCode.None || !Input.GetKeyDown(key.MainKey)) return false;
        foreach (KeyCode modifier in key.Modifiers)
            if (!Input.GetKey(modifier)) return false;
        return true;
    }

    private static void OnHoverText(Tameable __instance, ref string __result)
    {
        if (!Ready || _key == null || Player.m_localPlayer == null || !HasBucket(Player.m_localPlayer)) return;
        Character? character = __instance.GetComponent<Character>();
        if (character == null || !IsLox(character) || !character.IsTamed()) return;
        ZDO? zdo = character.GetComponent<ZNetView>()?.GetZDO();
        if (zdo == null) return;
        long until = ReadCooldownUntil(zdo);
        string token = RequireBreeding && !HasBreedingEligibility(zdo) ? "$lox_milking_hover_nobaby" :
            until == long.MaxValue ? "$flg_cuisine_milk_unavailable" :
            until > WorldNowTicks() ? "$lox_milking_hover_cooldown" : "$lox_milking_hover_action";
        __result += token == "$lox_milking_hover_action"
            ? "\n[<color=yellow><b>" + _key.Value.MainKey + "</b></color>] " + FeedLocalization.Text(token)
            : "\n<color=orange>" + FeedLocalization.Text(token) + "</color>";
    }

    internal static bool IsLox(Character character) => character.name.StartsWith("Lox", StringComparison.Ordinal);
    internal static bool HasBreedingEligibility(ZDO zdo) => zdo.GetInt("LoxBirthCount") > 0 ||
        zdo.GetFloat("pregnant") > 0f || zdo.GetBool("LoxHasBeenPregnant");
    internal static long WorldNowTicks() => ZNet.instance != null ? ZNet.instance.GetTime().Ticks : 0L;

    // One string write carries expiry and receipt together. Missing history means
    // no cooldown; a present but corrupt record must still fail closed.
    private static bool TryParseRecord(string record, out long until, out string receipt)
    {
        until = 0;
        receipt = string.Empty;
        if (record.Length == 0) return true;
        string[] parts = record.Split('|');
        if (parts.Length != 3 || parts[0] != "1" ||
            !long.TryParse(parts[1], NumberStyles.None, Invariant, out until) || until < 0 ||
            parts[2].Length > 160) return false;
        receipt = parts[2];
        return true;
    }

    internal static long ReadCooldownUntil(ZDO zdo) => TryParseRecord(zdo.GetString(StateKey), out long until, out _) ? until : long.MaxValue;
    internal static string ReadLastMilkRequest(ZDO zdo) => TryParseRecord(zdo.GetString(StateKey), out _, out string receipt) ? receipt : string.Empty;
    internal static void WriteMilkCooldown(ZDO zdo, long until, string requestId)
    {
        if (until < 0 || requestId.Length > 160 || requestId.IndexOf('|') >= 0)
            throw new ArgumentException("Invalid Cuisine cooldown record.");
        zdo.Set(StateKey, "1|" + until.ToString(Invariant) + "|" + requestId);
    }

    internal static void EnsureMilkState(Character character, ZNetView view)
    {
        if (!view.IsValid() || !view.IsOwner() || !IsLox(character))
            throw new InvalidOperationException("Milking state access requires the Lox owner.");
        ZDO zdo = view.GetZDO();
        if (!TryParseRecord(zdo.GetString(StateKey), out _, out _))
            throw new InvalidOperationException("Corrupt Cuisine cooldown record; milking/capture refused.");
    }

    // Orb state contains remaining time (frozen in the Orb), not the source world's
    // absolute clock. This also keeps transfers between worlds well-defined.
    internal static bool IsValidState(string text) => TryParseState(text, out _, out _, out _);
    private static bool TryParseState(string text, out long remaining, out int births, out bool bred)
    {
        remaining = 0;
        births = 0;
        bred = false;
        if (text == string.Empty) return true;
        if (text == null || text.Length > 80) return false;
        string[] parts = text.Split('|');
        if (parts.Length != 4 || parts[0] != "1" ||
            !long.TryParse(parts[1], NumberStyles.None, Invariant, out remaining) || remaining < 0 || remaining > MaxCooldownTicks ||
            !int.TryParse(parts[2], NumberStyles.None, Invariant, out births) || births < 0 ||
            (parts[3] != "0" && parts[3] != "1")) return false;
        bred = parts[3] == "1";
        return true;
    }

    internal static string CaptureState(Character character, ZNetView view)
    {
        if (!IsLox(character)) return string.Empty;
        if (_installed && !Ready) throw new InvalidOperationException("Unsupported Cuisine milking contract.");
        ZDO zdo = view.GetZDO();
        if (Ready) EnsureMilkState(character, view);
        else if (zdo.GetString(StateKey).Length == 0) return string.Empty;
        if (!TryParseRecord(zdo.GetString(StateKey), out long until, out _)) throw new InvalidOperationException("Invalid Cuisine cooldown.");
        long remaining = Math.Max(0L, Math.Min(MaxCooldownTicks, until - WorldNowTicks()));
        return "1|" + remaining.ToString(Invariant) + "|" + Math.Max(0, zdo.GetInt("LoxBirthCount")).ToString(Invariant) + "|" +
            (zdo.GetBool("LoxHasBeenPregnant") || zdo.GetFloat("pregnant") > 0f ? "1" : "0");
    }

    internal static void RestoreState(string state, Character character, ZNetView view)
    {
        if (!TryParseState(state, out long remaining, out int births, out bool bred))
            throw new InvalidOperationException("Invalid Cuisine Orb state.");
        if (!IsLox(character))
        {
            if (state.Length != 0) throw new InvalidOperationException("Cuisine state on a non-Lox payload.");
            return;
        }
        if (_installed && !Ready) throw new InvalidOperationException("Unsupported Cuisine milking contract.");
        if (state.Length == 0 && !Ready) return;
        if (!view.IsValid() || !view.IsOwner()) throw new InvalidOperationException("Lox restore requires ownership.");
        // Empty legacy/optional-mod state already parses as zero remaining time.
        // Breeding eligibility is not invented when the old Orb did not save it.
        ZDO zdo = view.GetZDO();
        WriteMilkCooldown(zdo, checked(WorldNowTicks() + remaining), string.Empty);
        if (state.Length != 0)
        {
            zdo.Set("LoxBirthCount", births);
            zdo.Set("LoxHasBeenPregnant", bred);
        }
    }

    internal static void ShowMessage(Player player, string token) => FeedLocalization.Message(player, token);
    internal static void GrantMilk(Player player, int amount, Vector3 position)
    {
        GameObject? prefab = ObjectDB.instance?.GetItemPrefab("VC_LoxMilk");
        ItemDrop? source = prefab == null ? null : prefab.GetComponent<ItemDrop>();
        if (source == null) throw new InvalidOperationException("ValheimCuisine milk prefab is unavailable.");
        bool overflow = false;
        for (int left = amount; left > 0;)
        {
            int count = Math.Min(left, Math.Max(1, source.m_itemData.m_shared.m_maxStackSize));
            ItemDrop.ItemData item = source.m_itemData.Clone();
            item.m_dropPrefab = prefab;
            item.m_worldLevel = (byte)Game.m_worldLevel;
            item.m_stack = count;
            // AddItem can partially merge before returning false; drop only remainder.
            if (!player.GetInventory().AddItem(item) && item.m_stack > 0)
            {
                ItemDrop.DropItem(item, item.m_stack, player.transform.position + player.transform.forward * 0.5f + Vector3.up * 0.5f,
                    player.transform.rotation);
                overflow = true;
            }
            left -= count;
        }
        if (overflow) ShowMessage(player, "$lox_milking_inventory_full");
        ShowMessage(player, "$lox_milking_success");
        if (ZNetScene.instance == null) return;
        GameObject? sound = ZNetScene.instance.GetPrefab("VC_sfx_LoxMilking");
        GameObject? visual = ZNetScene.instance.GetPrefab("vfx_lootspawn");
        if (sound != null) UnityEngine.Object.Instantiate(sound, position, Quaternion.identity);
        if (visual != null) UnityEngine.Object.Instantiate(visual, position + Vector3.up, Quaternion.identity);
    }
}
