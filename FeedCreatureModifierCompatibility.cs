using System;
using System.Linq;
using System.Reflection;

namespace FeedLikeGrandma;

internal static class FeedCreatureModifierCompatibility
{
    private static bool _cllcApiResolved;
    private static bool _cllcApiAvailable;
    private static Type? _cllcInfusionType;
    private static Type? _cllcExtraEffectType;
    private static MethodInfo? _cllcGetInfusionCreature;
    private static MethodInfo? _cllcGetExtraEffectCreature;
    private static MethodInfo? _cllcSetInfusionCreature;
    private static MethodInfo? _cllcSetExtraEffectCreature;
    private static bool _creatureManagerApiResolved;
    private static MethodInfo? _creatureManagerInheritModifiers;

    internal static void Shutdown()
    {
        _cllcApiResolved = false;
        _cllcApiAvailable = false;
        _cllcInfusionType = null;
        _cllcExtraEffectType = null;
        _cllcGetInfusionCreature = null;
        _cllcGetExtraEffectCreature = null;
        _cllcSetInfusionCreature = null;
        _cllcSetExtraEffectCreature = null;
        _creatureManagerApiResolved = false;
        _creatureManagerInheritModifiers = null;
    }

    internal static void CaptureCllcModifiers(Character character, out string infusion, out string extraEffect)
    {
        infusion = string.Empty;
        extraEffect = string.Empty;
        if (character == null || !TryResolveCllcApi())
        {
            return;
        }

        try
        {
            infusion = _cllcGetInfusionCreature?.Invoke(null, new object[] { character })?.ToString() ?? string.Empty;
            extraEffect = _cllcGetExtraEffectCreature?.Invoke(null, new object[] { character })?.ToString() ?? string.Empty;
        }
        catch (Exception ex)
        {
            FeedLikeGrandmaPlugin.Log.LogDebug($"Could not capture CL&LC data for {character.name}: {ex.Message}");
            infusion = string.Empty;
            extraEffect = string.Empty;
        }
    }

    internal static void RestoreCllcModifiers(Character character, string infusion, string extraEffect)
    {
        if (character == null || !TryResolveCllcApi())
        {
            return;
        }

        TrySetCllcEnum(_cllcSetInfusionCreature, _cllcInfusionType, character, infusion, "infusion");
        TrySetCllcEnum(_cllcSetExtraEffectCreature, _cllcExtraEffectType, character, extraEffect, "extra effect");
    }

    internal static void SetLevelPreservingCllcModifiers(Character character, int level)
    {
        CaptureCllcModifiers(character, out string infusion, out string extraEffect);
        try
        {
            character.SetLevel(level);
        }
        finally
        {
            RestoreCllcModifiers(character, infusion, extraEffect);
        }
    }

    internal static void InheritCreatureManagerModifiers(Character source, Character target)
    {
        ResolveCreatureManagerApi();
        if (_creatureManagerInheritModifiers == null)
        {
            return;
        }

        try
        {
            _creatureManagerInheritModifiers.Invoke(null, new object[] { source, target });
        }
        catch (Exception ex)
        {
            FeedLikeGrandmaPlugin.Log.LogDebug($"Could not inherit CreatureManager modifiers during growth: {ex.Message}");
        }
    }

    private static void TrySetCllcEnum(MethodInfo? method, Type? enumType, Character character, string value, string label)
    {
        if (method == null || enumType == null || string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        try
        {
            object enumValue = Enum.Parse(enumType, value, ignoreCase: true);
            method.Invoke(null, new[] { character, enumValue });
        }
        catch (Exception ex)
        {
            FeedLikeGrandmaPlugin.Log.LogDebug($"Could not restore CL&LC {label} '{value}' for {character.name}: {ex.Message}");
        }
    }

    private static bool TryResolveCllcApi()
    {
        if (_cllcApiResolved)
        {
            return _cllcApiAvailable;
        }

        _cllcApiResolved = true;
        try
        {
            Assembly? assembly = AppDomain.CurrentDomain
                .GetAssemblies()
                .FirstOrDefault(loaded => loaded.GetName().Name == "CreatureLevelControl");
            Type? apiType = assembly?.GetType("CreatureLevelControl.API");
            _cllcInfusionType = assembly?.GetType("CreatureLevelControl.CreatureInfusion");
            _cllcExtraEffectType = assembly?.GetType("CreatureLevelControl.CreatureExtraEffect");

            if (apiType == null || _cllcInfusionType == null || _cllcExtraEffectType == null)
            {
                _cllcApiAvailable = false;
                return false;
            }

            _cllcGetInfusionCreature = FindCllcGetter(apiType, "GetInfusionCreature");
            _cllcGetExtraEffectCreature = FindCllcGetter(apiType, "GetExtraEffectCreature");
            _cllcSetInfusionCreature = FindCllcSetter(apiType, "SetInfusionCreature", _cllcInfusionType);
            _cllcSetExtraEffectCreature = FindCllcSetter(apiType, "SetExtraEffectCreature", _cllcExtraEffectType);
            _cllcApiAvailable =
                _cllcGetInfusionCreature != null &&
                _cllcGetExtraEffectCreature != null &&
                _cllcSetInfusionCreature != null &&
                _cllcSetExtraEffectCreature != null;
            return _cllcApiAvailable;
        }
        catch (Exception ex)
        {
            FeedLikeGrandmaPlugin.Log.LogDebug($"Could not resolve CL&LC API: {ex.Message}");
            _cllcApiAvailable = false;
            return false;
        }
    }

    private static MethodInfo? FindCllcGetter(Type apiType, string name)
    {
        return apiType
            .GetMethods(BindingFlags.Public | BindingFlags.Static)
            .FirstOrDefault(method =>
            {
                if (method.Name != name)
                {
                    return false;
                }

                ParameterInfo[] parameters = method.GetParameters();
                return parameters.Length == 1 && parameters[0].ParameterType.IsAssignableFrom(typeof(Character));
            });
    }

    private static MethodInfo? FindCllcSetter(Type apiType, string name, Type enumType)
    {
        return apiType
            .GetMethods(BindingFlags.Public | BindingFlags.Static)
            .FirstOrDefault(method =>
            {
                if (method.Name != name)
                {
                    return false;
                }

                ParameterInfo[] parameters = method.GetParameters();
                return parameters.Length == 2 &&
                       parameters[0].ParameterType.IsAssignableFrom(typeof(Character)) &&
                       parameters[1].ParameterType == enumType;
            });
    }

    private static void ResolveCreatureManagerApi()
    {
        if (_creatureManagerApiResolved)
        {
            return;
        }

        _creatureManagerApiResolved = true;
        Assembly? assembly = AppDomain.CurrentDomain
            .GetAssemblies()
            .FirstOrDefault(loaded => loaded.GetName().Name == "CreatureManager");
        Type? modifierManager = assembly?.GetType("CreatureManager.CreatureModifierManager");
        _creatureManagerInheritModifiers = modifierManager?.GetMethod(
            "InheritModifiers",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static,
            null,
            new[] { typeof(Character), typeof(Character) },
            null);
    }
}
