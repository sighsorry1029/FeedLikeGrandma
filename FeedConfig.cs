using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using UnityEngine;
using YamlDotNet.RepresentationModel;

namespace FeedLikeGrandma;

internal sealed class RawFeedConfig
{
    public Dictionary<string, RawFeedingRule> Rules { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, float> FoodNumbers { get; } = new(StringComparer.OrdinalIgnoreCase);
    public RawFeedValues Defaults { get; set; } = new();
}

internal sealed class RawFoodEntry
{
    public string PrefabName { get; init; } = string.Empty;
    public int? RequiredQuality { get; init; }
    public float? Value { get; init; }
}

internal sealed class RawFeedValues
{
    public float LevelUpConstant { get; init; } = 1f;
    public float TamingConstant { get; init; } = 1f;
    public float GrowthConstant { get; init; } = 1f;
    public float HealConstant { get; init; } = 1f;
    public float FedConstant { get; init; } = 60f;
}

internal sealed class RawTameValues
{
    public float FedDuration { get; init; }
    public float TamingTime { get; init; }
    public bool StartsTamed { get; init; }
    public bool Commandable { get; init; }
}

internal sealed class RawProcreationValues
{
    public int MaxCreatures { get; init; }
    public float TotalCheckRange { get; init; }
}

internal sealed class RawGrowValues
{
    public float GrowTime { get; init; }
}

internal sealed class RawEggGrowValues
{
    public float GrowTime { get; init; }
}

internal sealed class RawMountStaminaValues
{
    public float AttackStaminaCost { get; init; }
    public float JumpStaminaCost { get; init; }
    public float AttackCooldown { get; init; }
}

internal sealed class RawFeedingRule
{
    public string PrefabName { get; init; } = string.Empty;
    public RawFeedValues? Feed { get; set; }
    public RawTameValues? Tame { get; set; }
    public RawProcreationValues? Procreation { get; set; }
    public RawGrowValues? Grow { get; set; }
    public RawEggGrowValues? EggGrow { get; set; }
    public RawMountStaminaValues? MountStamina { get; set; }
    public bool HasConsumeItemsOverride { get; set; }
    public bool HasRestrictedBiomeOverride { get; set; }
    public List<string> RestrictedBiomes { get; } = new();
    public List<RawFoodEntry> Foods { get; } = new();
    public Dictionary<int, List<RawFoodEntry>> LevelUpItems { get; } = new();
}

internal sealed class ResolvedFoodEntry
{
    public string PrefabName { get; init; } = string.Empty;
    public int? RequiredQuality { get; init; }
    public float Value { get; init; }
    public Sprite Icon { get; init; } = null!;
}

internal sealed class ResolvedFeedingRule
{
    public GameObject Prefab { get; init; } = null!;
    public float LevelUpConstant { get; init; }
    public float TamingConstant { get; init; }
    public float GrowthConstant { get; init; }
    public float HealConstant { get; init; }
    public float FedConstant { get; init; }
    public List<ResolvedFoodEntry> Foods { get; init; } = new();
    public Dictionary<int, List<ResolvedFoodEntry>> LevelUpItems { get; init; } = new();
    public ResolvedMountStaminaValues? MountStamina { get; init; }
    public bool HasRestrictedBiomeOverride { get; init; }
    public IReadOnlyList<string> RestrictedBiomes { get; init; } = Array.Empty<string>();
}

internal sealed class ResolvedMountStaminaValues
{
    public float AttackStaminaCost { get; init; }
    public float JumpStaminaCost { get; init; }
    public float AttackCooldown { get; init; }
}

internal static class FeedingYamlParser
{
    private sealed class Diagnostics
    {
        private readonly List<string> _warnings;
        private readonly List<string> _errors;

        internal Diagnostics(List<string> warnings, List<string> errors)
        {
            _warnings = warnings;
            _errors = errors;
        }

        internal void Warning(string message)
        {
            _warnings.Add(message);
        }

        internal void Error(string message)
        {
            _errors.Add(message);
        }
    }

    public static RawFeedConfig Parse(string yamlText, List<string> warnings, List<string> errors)
    {
        Diagnostics diagnostics = new(warnings, errors);
        RawFeedConfig config = new();
        if (string.IsNullOrWhiteSpace(yamlText))
        {
            return config;
        }

        YamlStream stream = new();
        stream.Load(new StringReader(yamlText));
        if (stream.Documents.Count == 0 || stream.Documents[0].RootNode is not YamlMappingNode root)
        {
            diagnostics.Error("YAML root must be a mapping of defaults, foodNumbers, and prefab names.");
            return config;
        }

        foreach (KeyValuePair<YamlNode, YamlNode> entry in root.Children)
        {
            string key = ReadScalar(entry.Key).Trim();
            if (key.Length == 0)
            {
                diagnostics.Error($"{Location(entry.Key)}: top-level key cannot be empty.");
                continue;
            }

            if (key.Equals("defaults", StringComparison.OrdinalIgnoreCase))
            {
                ParseDefaults(config, entry.Value, diagnostics);
                continue;
            }

            if (key.Equals("foodNumbers", StringComparison.OrdinalIgnoreCase))
            {
                ParseFoodNumbers(config.FoodNumbers, entry.Value, "foodNumbers", diagnostics);
                continue;
            }

            RawFeedingRule rule = ParseRule(key, entry.Value, diagnostics);
            if (config.Rules.ContainsKey(key))
            {
                diagnostics.Warning($"{Location(entry.Key)}: prefab '{key}' was defined more than once. The last definition won.");
            }

            config.Rules[key] = rule;
        }

        return config;
    }

    private static void ParseDefaults(RawFeedConfig config, YamlNode node, Diagnostics diagnostics)
    {
        if (node is not YamlMappingNode mapping)
        {
            diagnostics.Error($"{Location(node)}: defaults must be a mapping.");
            return;
        }

        foreach (KeyValuePair<YamlNode, YamlNode> entry in mapping.Children)
        {
            string field = ReadScalar(entry.Key).Trim();
            if (field.Equals("feed", StringComparison.OrdinalIgnoreCase))
            {
                config.Defaults = ParseFeedValues(entry.Value, "defaults.feed", diagnostics);
            }
            else
            {
                diagnostics.Error($"{Location(entry.Key)}: unknown defaults field '{field}'.");
            }
        }
    }

    private static RawFeedingRule ParseRule(string prefabName, YamlNode node, Diagnostics diagnostics)
    {
        RawFeedingRule rule = new() { PrefabName = prefabName };
        if (node is not YamlMappingNode mapping)
        {
            diagnostics.Error($"{Location(node)}: prefab '{prefabName}' must contain a mapping.");
            return rule;
        }

        foreach (KeyValuePair<YamlNode, YamlNode> entry in mapping.Children)
        {
            string field = ReadScalar(entry.Key).Trim();
            string normalized = NormalizeField(field);
            switch (normalized)
            {
                case "feed":
                    rule.Feed = ParseFeedValues(entry.Value, $"{prefabName}.feed", diagnostics);
                    break;
                case "consumeitems":
                    rule.HasConsumeItemsOverride = true;
                    ParseConsumeItems(rule, entry.Value, diagnostics);
                    break;
                case "levelupitems":
                    ParseLevelUpItems(rule, entry.Value, $"{prefabName}.levelUpItems", diagnostics);
                    break;
                case "tame":
                    rule.Tame = ParseTameValues(entry.Value, $"{prefabName}.tame", diagnostics);
                    break;
                case "procreation":
                    rule.Procreation = ParseProcreationValues(entry.Value, $"{prefabName}.procreation", diagnostics);
                    break;
                case "grow":
                    rule.Grow = ParseGrowValues(entry.Value, $"{prefabName}.grow", diagnostics);
                    break;
                case "egggrow":
                    rule.EggGrow = ParseEggGrowValues(entry.Value, $"{prefabName}.eggGrow", diagnostics);
                    break;
                case "mountstamina":
                    rule.MountStamina = ParseMountStaminaValues(entry.Value, $"{prefabName}.mountStamina", diagnostics);
                    break;
                case "restrictedbiome":
                case "restrictedbiomes":
                    rule.HasRestrictedBiomeOverride = true;
                    rule.RestrictedBiomes.Clear();
                    rule.RestrictedBiomes.AddRange(ParseStringList(entry.Value)
                        .Select(value => Unquote(value).Trim())
                        .Where(value => value.Length > 0)
                        .Distinct(StringComparer.OrdinalIgnoreCase));
                    break;
                default:
                    diagnostics.Error($"{Location(entry.Key)}: unknown field '{field}' for prefab '{prefabName}'.");
                    break;
            }
        }

        return rule;
    }

    private static void ParseConsumeItems(RawFeedingRule rule, YamlNode node, Diagnostics diagnostics)
    {
        ParseFoodEntries(rule.Foods, rule.PrefabName, node, diagnostics);
    }

    private static void ParseFoodEntries(
        List<RawFoodEntry> foods,
        string context,
        YamlNode node,
        Diagnostics diagnostics)
    {
        if (node is YamlSequenceNode sequence)
        {
            foreach (YamlNode itemNode in sequence.Children)
            {
                AddFoodEntry(foods, context, itemNode, diagnostics);
            }

            return;
        }

        foreach (string item in ParseStringList(node))
        {
            AddFoodEntry(foods, context, item, node, diagnostics);
        }
    }

    private static void AddFoodEntry(
        List<RawFoodEntry> foods,
        string context,
        YamlNode node,
        Diagnostics diagnostics)
    {
        if (node is YamlMappingNode mapping && mapping.Children.Count == 1)
        {
            KeyValuePair<YamlNode, YamlNode> pair = mapping.Children.First();
            AddFoodEntry(
                foods,
                context,
                ReadScalar(pair.Key).Trim(),
                ReadScalar(pair.Value).Trim(),
                node,
                diagnostics);
            return;
        }

        if (node is YamlSequenceNode sequence)
        {
            List<string> parts = sequence.Children.Select(child => ReadScalar(child).Trim()).ToList();
            if (parts.Count >= 2)
            {
                AddFoodEntry(foods, context, parts[0], parts[1], node, diagnostics);
                return;
            }
        }

        AddFoodEntry(foods, context, ReadScalar(node).Trim(), node, diagnostics);
    }

    private static void AddFoodEntry(
        List<RawFoodEntry> foods,
        string context,
        string entryText,
        YamlNode node,
        Diagnostics diagnostics)
    {
        string foodName;
        string? valueText = null;
        if (TrySplitCommaValue(entryText, out string splitName, out string splitValue))
        {
            foodName = Unquote(splitName).Trim();
            valueText = splitValue;
        }
        else
        {
            foodName = Unquote(entryText).Trim();
        }

        AddFoodEntry(foods, context, foodName, valueText, node, diagnostics);
    }

    private static void AddFoodEntry(List<RawFoodEntry> foods, string ruleName, string foodName, string? valueText, YamlNode node, Diagnostics diagnostics)
    {
        if (string.IsNullOrWhiteSpace(foodName))
        {
            diagnostics.Error($"{Location(node)}: food item name cannot be empty.");
            return;
        }

        if (!TryParseFoodToken(foodName, node, diagnostics, out string prefabName, out int? requiredQuality))
        {
            return;
        }

        float? value = null;
        if (!string.IsNullOrWhiteSpace(valueText))
        {
            value = Math.Max(0f, ParseFloat(valueText!, $"{ruleName}.{FormatFoodKey(prefabName, requiredQuality)}", node, diagnostics));
        }

        int existingIndex = foods.FindIndex(entry =>
            entry.PrefabName.Equals(prefabName, StringComparison.OrdinalIgnoreCase) &&
            entry.RequiredQuality == requiredQuality);
        if (existingIndex >= 0)
        {
            diagnostics.Warning($"{Location(node)}: food '{FormatFoodKey(prefabName, requiredQuality)}' was defined more than once for '{ruleName}'. The last value won.");
            foods.RemoveAt(existingIndex);
        }

        foods.Add(new RawFoodEntry
        {
            PrefabName = prefabName,
            RequiredQuality = requiredQuality,
            Value = value
        });
    }

    private static void ParseLevelUpItems(RawFeedingRule rule, YamlNode node, string context, Diagnostics diagnostics)
    {
        if (node is not YamlMappingNode mapping)
        {
            diagnostics.Error($"{Location(node)}: {context} must be a mapping of current Character level to consumeItems.");
            return;
        }

        foreach (KeyValuePair<YamlNode, YamlNode> entry in mapping.Children)
        {
            int currentLevel = Math.Max(1, ParseInt(entry.Key, $"{context}.level", diagnostics));
            List<RawFoodEntry> foods = new();
            ParseFoodEntries(foods, $"{rule.PrefabName}.levelUpItems.{currentLevel}", entry.Value, diagnostics);

            if (rule.LevelUpItems.ContainsKey(currentLevel))
            {
                diagnostics.Warning($"{Location(entry.Key)}: levelUpItems for Character level {currentLevel} was defined more than once for prefab '{rule.PrefabName}'. The last definition won.");
            }

            rule.LevelUpItems[currentLevel] = foods;
        }
    }

    private static void ParseFoodNumbers(Dictionary<string, float> foodNumbers, YamlNode node, string context, Diagnostics diagnostics)
    {
        if (node is YamlSequenceNode sequence)
        {
            foreach (YamlNode itemNode in sequence.Children)
            {
                AddFoodNumber(foodNumbers, itemNode, context, diagnostics);
            }

            return;
        }

        diagnostics.Error($"{Location(node)}: {context} must be a sequence. Use entries like '- Raspberry' or '- Raspberry, 3'.");
    }

    private static void AddFoodNumber(Dictionary<string, float> foodNumbers, YamlNode node, string context, Diagnostics diagnostics)
    {
        string entryText = ReadScalar(node).Trim();
        bool hasExplicitValue = TrySplitCommaValue(entryText, out string splitName, out string splitValue);

        string foodNameFromText = Unquote(hasExplicitValue ? splitName : entryText).Trim();
        if (!TryParseFoodToken(foodNameFromText, node, diagnostics, out string prefabName, out int? requiredQuality))
        {
            return;
        }

        string foodKey = FormatFoodKey(prefabName, requiredQuality);
        foodNumbers[foodKey] = hasExplicitValue
            ? Math.Max(0f, ParseFloat(splitValue, $"{context}.{foodKey}", node, diagnostics))
            : 1f;
    }

    internal static string FormatFoodKey(string prefabName, int? requiredQuality)
    {
        return requiredQuality.HasValue
            ? $"{prefabName}@{requiredQuality.Value.ToString(CultureInfo.InvariantCulture)}"
            : prefabName;
    }

    private static bool TryParseFoodToken(string foodName, YamlNode node, Diagnostics diagnostics, out string prefabName, out int? requiredQuality)
    {
        prefabName = Unquote(foodName).Trim();
        requiredQuality = null;
        if (prefabName.Length == 0)
        {
            diagnostics.Error($"{Location(node)}: food item name cannot be empty.");
            return false;
        }

        int qualitySeparator = prefabName.LastIndexOf('@');
        if (qualitySeparator < 0)
        {
            return true;
        }

        string itemName = prefabName.Substring(0, qualitySeparator).Trim();
        string qualityText = prefabName.Substring(qualitySeparator + 1).Trim();
        if (itemName.Length == 0 ||
            !int.TryParse(qualityText, NumberStyles.Integer, CultureInfo.InvariantCulture, out int quality) ||
            quality <= 0)
        {
            diagnostics.Error($"{Location(node)}: food item '{foodName}' must use ItemName@quality with a positive integer quality.");
            prefabName = string.Empty;
            return false;
        }

        prefabName = itemName;
        requiredQuality = quality;
        return true;
    }

    private static RawFeedValues ParseFeedValues(YamlNode node, string field, Diagnostics diagnostics)
    {
        List<string> parts = ReadTupleParts(node);
        if (parts.Count != 5)
        {
            diagnostics.Error($"{Location(node)}: field '{field}' must contain 5 values: levelupconstant, tamingconstant, growthconstant, healconstant, fedconstant.");
            return new RawFeedValues();
        }

        return new RawFeedValues
        {
            LevelUpConstant = Math.Max(0f, ParseFloat(parts[0], $"{field}.levelupconstant", node, diagnostics)),
            TamingConstant = Math.Max(0f, ParseFloat(parts[1], $"{field}.tamingconstant", node, diagnostics)),
            GrowthConstant = Math.Max(0f, ParseFloat(parts[2], $"{field}.growthconstant", node, diagnostics)),
            HealConstant = Math.Max(0f, ParseFloat(parts[3], $"{field}.healconstant", node, diagnostics)),
            FedConstant = Math.Max(0f, ParseFloat(parts[4], $"{field}.fedconstant", node, diagnostics))
        };
    }

    private static RawTameValues? ParseTameValues(YamlNode node, string field, Diagnostics diagnostics)
    {
        List<string> parts = ReadTupleParts(node);
        if (parts.Count != 4)
        {
            diagnostics.Error($"{Location(node)}: field '{field}' must contain 4 values: fedDuration, tamingTime, startsTamed, commandable.");
            return null;
        }

        return new RawTameValues
        {
            FedDuration = Math.Max(0f, ParseFloat(parts[0], $"{field}.fedDuration", node, diagnostics)),
            TamingTime = Math.Max(0f, ParseFloat(parts[1], $"{field}.tamingTime", node, diagnostics)),
            StartsTamed = ParseBool(parts[2], $"{field}.startsTamed", node, diagnostics),
            Commandable = ParseBool(parts[3], $"{field}.commandable", node, diagnostics)
        };
    }

    private static RawProcreationValues? ParseProcreationValues(YamlNode node, string field, Diagnostics diagnostics)
    {
        List<string> parts = ReadTupleParts(node);
        if (parts.Count != 2)
        {
            diagnostics.Error($"{Location(node)}: field '{field}' must contain 2 values: maxCreatures, totalCheckRange.");
            return null;
        }

        return new RawProcreationValues
        {
            MaxCreatures = Math.Max(0, ParseInt(parts[0], $"{field}.maxCreatures", node, diagnostics)),
            TotalCheckRange = Math.Max(0f, ParseFloat(parts[1], $"{field}.totalCheckRange", node, diagnostics))
        };
    }

    private static RawGrowValues? ParseGrowValues(YamlNode node, string field, Diagnostics diagnostics)
    {
        List<string> parts = ReadTupleParts(node);
        if (parts.Count != 1)
        {
            diagnostics.Error($"{Location(node)}: field '{field}' must contain 1 value: growTime.");
            return null;
        }

        return new RawGrowValues
        {
            GrowTime = Math.Max(0f, ParseFloat(parts[0], $"{field}.growTime", node, diagnostics))
        };
    }

    private static RawEggGrowValues? ParseEggGrowValues(YamlNode node, string field, Diagnostics diagnostics)
    {
        List<string> parts = ReadTupleParts(node);
        if (parts.Count != 1)
        {
            diagnostics.Error($"{Location(node)}: field '{field}' must contain 1 value: growTime.");
            return null;
        }

        return new RawEggGrowValues
        {
            GrowTime = Math.Max(0f, ParseFloat(parts[0], $"{field}.growTime", node, diagnostics))
        };
    }

    private static RawMountStaminaValues? ParseMountStaminaValues(YamlNode node, string field, Diagnostics diagnostics)
    {
        List<string> parts = ReadTupleParts(node);
        if (parts.Count != 3)
        {
            diagnostics.Error($"{Location(node)}: field '{field}' must contain 3 values: attackStaminaCost, jumpStaminaCost, attackCooldown.");
            return null;
        }

        return new RawMountStaminaValues
        {
            AttackStaminaCost = Math.Max(0f, ParseFloat(parts[0], $"{field}.attackStaminaCost", node, diagnostics)),
            JumpStaminaCost = Math.Max(0f, ParseFloat(parts[1], $"{field}.jumpStaminaCost", node, diagnostics)),
            AttackCooldown = Math.Max(0f, ParseFloat(parts[2], $"{field}.attackCooldown", node, diagnostics))
        };
    }

    private static List<string> ParseStringList(YamlNode node)
    {
        if (node is YamlSequenceNode sequence)
        {
            return sequence.Children.Select(child => ReadScalar(child).Trim()).Where(value => value.Length > 0).ToList();
        }

        string scalar = ReadScalar(node).Trim();
        if (scalar.Length == 0)
        {
            return new List<string>();
        }

        if (scalar.StartsWith("[", StringComparison.Ordinal) && scalar.EndsWith("]", StringComparison.Ordinal))
        {
            return SplitCommaText(scalar.Substring(1, scalar.Length - 2))
                .Select(part => Unquote(part).Trim())
                .Where(value => value.Length > 0)
                .ToList();
        }

        return new List<string> { Unquote(scalar) };
    }

    private static List<string> ReadTupleParts(YamlNode node)
    {
        if (node is YamlSequenceNode sequence)
        {
            return sequence.Children.Select(child => ReadScalar(child).Trim()).ToList();
        }

        string normalized = ReadScalar(node).Trim();
        if (normalized.StartsWith("[", StringComparison.Ordinal) && normalized.EndsWith("]", StringComparison.Ordinal))
        {
            normalized = normalized.Substring(1, normalized.Length - 2);
        }

        return SplitCommaText(normalized).Select(part => Unquote(part).Trim()).ToList();
    }

    private static int ParseInt(YamlNode node, string field, Diagnostics diagnostics)
    {
        return ParseInt(ReadScalar(node), field, node, diagnostics);
    }

    private static int ParseInt(string valueText, string field, YamlNode node, Diagnostics diagnostics)
    {
        string normalized = Unquote(valueText).Trim();
        if (int.TryParse(normalized, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value))
        {
            return value;
        }

        diagnostics.Error($"{Location(node)}: field '{field}' must be an integer.");
        return 0;
    }

    private static float ParseFloat(YamlNode node, string field, Diagnostics diagnostics)
    {
        return ParseFloat(ReadScalar(node), field, node, diagnostics);
    }

    private static float ParseFloat(string valueText, string field, YamlNode node, Diagnostics diagnostics)
    {
        string normalized = Unquote(valueText).Trim();
        if (float.TryParse(normalized, NumberStyles.Float, CultureInfo.InvariantCulture, out float value))
        {
            return value;
        }

        diagnostics.Error($"{Location(node)}: field '{field}' must be a number.");
        return 0f;
    }

    private static bool ParseBool(string valueText, string field, YamlNode node, Diagnostics diagnostics)
    {
        string normalized = Unquote(valueText).Trim();
        if (bool.TryParse(normalized, out bool value))
        {
            return value;
        }

        switch (normalized.ToLowerInvariant())
        {
            case "1":
            case "yes":
            case "y":
            case "on":
                return true;
            case "0":
            case "no":
            case "n":
            case "off":
                return false;
            default:
                diagnostics.Error($"{Location(node)}: field '{field}' must be true or false.");
                return false;
        }
    }

    private static string ReadScalar(YamlNode node)
    {
        return node is YamlScalarNode scalar ? scalar.Value ?? string.Empty : node.ToString() ?? string.Empty;
    }

    private static string NormalizeField(string field)
    {
        return field.Replace("_", string.Empty).Replace("-", string.Empty).Trim().ToLowerInvariant();
    }

    private static bool TrySplitCommaValue(string text, out string key, out string value)
    {
        List<string> parts = SplitCommaText(text);
        if (parts.Count < 2)
        {
            key = string.Empty;
            value = string.Empty;
            return false;
        }

        key = parts[0].Trim();
        value = parts[1].Trim();
        return key.Length > 0;
    }

    private static List<string> SplitCommaText(string text)
    {
        List<string> parts = new();
        bool inSingleQuote = false;
        bool inDoubleQuote = false;
        int startIndex = 0;

        for (int index = 0; index < text.Length; index++)
        {
            char current = text[index];
            if (current == '\'' && !inDoubleQuote)
            {
                inSingleQuote = !inSingleQuote;
                continue;
            }

            if (current == '"' && !inSingleQuote)
            {
                inDoubleQuote = !inDoubleQuote;
                continue;
            }

            if (current != ',' || inSingleQuote || inDoubleQuote)
            {
                continue;
            }

            parts.Add(text.Substring(startIndex, index - startIndex).Trim());
            startIndex = index + 1;
        }

        parts.Add(text.Substring(startIndex).Trim());
        return parts;
    }

    private static string Unquote(string text)
    {
        text = text.Trim();
        if (text.Length >= 2)
        {
            if ((text[0] == '"' && text[text.Length - 1] == '"') ||
                (text[0] == '\'' && text[text.Length - 1] == '\''))
            {
                return text.Substring(1, text.Length - 2).Replace("''", "'");
            }
        }

        return text;
    }

    private static string Location(YamlNode node)
    {
        return node.Start.Line > 0 ? $"Line {node.Start.Line}" : "YAML";
    }
}
