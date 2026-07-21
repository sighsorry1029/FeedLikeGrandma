using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using UnityEngine;

namespace FeedLikeGrandma;

internal static class FeedReferenceWriter
{
    private static readonly Dictionary<string, ReferencePrefabSnapshot> ReferenceSnapshots = new(StringComparer.OrdinalIgnoreCase);

    internal static string BuildDefaultOverrideYaml()
    {
        StringBuilder builder = new();
        AppendTemplateComment(builder, "FeedLikeGrandma feed configuration.");
        AppendTemplateComment(builder, "Copy prefab blocks from feed.reference.yml. Omitted prefab fields keep the current game value.");
        AppendTemplateBlankLine(builder);
        AppendTemplateComment(builder, "Schema:");
        AppendTemplateLine(builder, 0, "Boar:                                  # creature prefab id.");
        AppendTemplateLine(builder, 1, "feed: [1, 1, 2, 0.5, 60]            # overrides defaults.feed for this prefab.");
        AppendTemplateLine(builder, 1, "consumeItems:                       # everyday foods; ItemName or ItemName, localFeedNumber.");
        AppendTemplateLine(builder, 2, "- Raspberry");
        AppendTemplateLine(builder, 2, "- Blueberries, 5");
        AppendTemplateLine(builder, 1, "levelUpItems:                       # level-up-only foods keyed by current Character level.");
        AppendTemplateLine(builder, 2, "4:                                  # Character level 4 -> 5. Stars: 3 -> 4.");
        AppendTemplateLine(builder, 3, "- Fish1@5, 500                      # requires Fish1 quality 5 and adds 500 level-up progress.");
        AppendTemplateLine(builder, 1, "tame: [30, 1800, false, true]       # fedDuration, tamingTime, startsTamed, commandable.");
        AppendTemplateLine(builder, 1, "procreation: [5, 10]                # maxCreatures, totalCheckRange.");
        AppendTemplateLine(builder, 1, "grow: [3000]                        # growTime.");
        AppendTemplateLine(builder, 1, "eggGrow: [1800]                     # EggGrow growTime for egg item prefabs.");
        AppendTemplateLine(builder, 1, "mountStamina: [150, 80, 0.75]       # BetterRiding attackStaminaCost, jumpStaminaCost, attackCooldown.");
        AppendTemplateLine(builder, 1, "restrictedBiome: [Ashlands]         # BetterRiding dragon flight block list; use [] to opt out.");
        AppendTemplateBlankLine(builder);
        builder.AppendLine("defaults:                            # Global defaults used when a prefab omits feed.");
        AppendLine(builder, 1, "feed: [20, 15, 30, 25, 120]         # levelupconstant, tamingconstant, growthconstant, healconstant, fedconstant.");
        AppendLine(builder, 1, "# effect = foodNumber * constant. levelup/taming/growth/fed are progress or seconds; heal is max-health percent.");
        AppendLine(builder, 1, "# levelUpItems override level-up food for that level; otherwise consumeItems can also add level progress.");
        AppendLine(builder, 1, "# level-up-only food still applies healconstant and fedconstant.");
        builder.AppendLine();
        builder.AppendLine("foodNumbers:                         # Global manual-feed numbers used by bare consumeItems.");
        AppendLine(builder, 1, "- NeckTail, 2");
        AppendLine(builder, 1, "- RawMeat, 2");
        AppendLine(builder, 1, "- DeerMeat, 3");
        AppendLine(builder, 1, "- FishRaw, 4");
        AppendLine(builder, 1, "- Sausages, 4");
        AppendLine(builder, 1, "- LoxMeat, 4");
        AppendLine(builder, 1, "- ChickenMeat, 6");
        builder.AppendLine();
        builder.AppendLine("Asksvin:");
        AppendLine(builder, 1, "levelUpItems:                       # level-up-only foods keyed by current Character level.");
        AppendLine(builder, 2, "2:                                  # Character level 2 -> 3. Stars: 1 -> 2.");
        AppendLine(builder, 3, "- Fish11, 300");
        builder.AppendLine();
        builder.AppendLine("#DModer_Ygg4_Elder:");
        AppendLine(builder, 0, "#  levelUpItems:");
        AppendLine(builder, 0, "#    1:");
        AppendLine(builder, 0, "#      - Fish4_cave@2, 300");
        return builder.ToString();
    }

    internal static string BuildReferenceYaml(bool refreshExternalSnapshot = true)
    {
        if (refreshExternalSnapshot)
        {
            FeedExternalReferenceSupport.RefreshSnapshot();
        }

        List<GameObject> externalPrefabs = FeedExternalReferenceSupport.GetExternalPrefabs().ToList();
        EnsureReferenceSnapshotAvailable(externalPrefabs);
        List<GameObject> prefabs = GetFeedPrefabs(externalPrefabs);
        if (prefabs.Count == 0)
        {
            return "[]\n";
        }

        StringBuilder builder = new();
        AppendGeneratedHeader(builder, "feed reference");
        FeedReferenceSections.AppendPrefabSections(builder, prefabs, AppendPrefabEntry);
        return builder.ToString();
    }

    internal static void CaptureMissingReferenceSnapshots(IEnumerable<GameObject> prefabs)
    {
        foreach (GameObject prefab in prefabs
                     .Where(prefab => prefab != null && !string.IsNullOrWhiteSpace(prefab.name))
                     .GroupBy(prefab => prefab.name, StringComparer.OrdinalIgnoreCase)
                     .Select(group => group.First())
                     .Where(prefab => !FeedSystem.ShouldIgnorePrefab(prefab)))
        {
            if (ReferenceSnapshots.ContainsKey(prefab.name))
            {
                continue;
            }

            ReferencePrefabSnapshot snapshot = CaptureReferenceSnapshot(prefab);
            if (snapshot.HasData)
            {
                ReferenceSnapshots[prefab.name] = snapshot;
            }
        }
    }

    internal static void RestoreReferenceSnapshots(IEnumerable<GameObject> prefabs)
    {
        if (ReferenceSnapshots.Count == 0)
        {
            return;
        }

        foreach (GameObject prefab in prefabs
                     .Where(prefab => prefab != null && !string.IsNullOrWhiteSpace(prefab.name))
                     .GroupBy(prefab => prefab.name, StringComparer.OrdinalIgnoreCase)
                     .Select(group => group.First()))
        {
            if (ReferenceSnapshots.TryGetValue(prefab.name, out ReferencePrefabSnapshot? snapshot) &&
                snapshot != null)
            {
                RestoreReferenceSnapshot(prefab, snapshot);
            }
        }
    }

    private static void EnsureReferenceSnapshotAvailable(List<GameObject>? externalPrefabs = null)
    {
        bool hasZNetScenePrefabs = ZNetScene.instance?.m_prefabs != null;
        externalPrefabs ??= FeedExternalReferenceSupport.GetExternalPrefabs().ToList();
        if (!hasZNetScenePrefabs && externalPrefabs.Count == 0)
        {
            return;
        }

        CaptureMissingReferenceSnapshots(EnumerateReferencePrefabs(externalPrefabs));
    }

    internal static void Shutdown()
    {
        ReferenceSnapshots.Clear();
    }

    private static List<GameObject> GetFeedPrefabs(List<GameObject>? externalPrefabs = null)
    {
        externalPrefabs ??= FeedExternalReferenceSupport.GetExternalPrefabs().ToList();
        if ((ZNetScene.instance == null || ZNetScene.instance.m_prefabs == null) &&
            externalPrefabs.Count == 0)
        {
            return new List<GameObject>();
        }

        return EnumerateReferencePrefabs(externalPrefabs)
            .Where(prefab => prefab != null && !string.IsNullOrWhiteSpace(prefab.name))
            .GroupBy(prefab => prefab.name, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .Where(prefab => !FeedSystem.ShouldIgnorePrefab(prefab))
            .Where(prefab => ReferenceSnapshots.TryGetValue(prefab.name, out ReferencePrefabSnapshot? snapshot) &&
                             ShouldIncludeInReference(snapshot))
            .OrderBy(prefab => prefab.name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static void AppendPrefabEntry(StringBuilder builder, GameObject prefab)
    {
        if (!ReferenceSnapshots.TryGetValue(prefab.name, out ReferencePrefabSnapshot? snapshot))
        {
            return;
        }

        bool emitProcreation = ShouldEmitProcreation(snapshot);
        bool emitGrowup = ShouldEmitGrowup(snapshot);
        bool includeCreatureFields = snapshot.HasTameable ||
                                     emitProcreation ||
                                     emitGrowup ||
                                     snapshot.ConsumeItemNames.Count > 0;
        bool emitEggGrow = snapshot.HasEggGrow;
        if (!includeCreatureFields && !emitEggGrow)
        {
            return;
        }

        AppendLine(builder, 0, $"{FormatKey(prefab.name)}:");

        if (includeCreatureFields)
        {
            AppendConsumeItems(builder, snapshot.ConsumeItemNames);

            if (snapshot.HasTameable)
            {
                AppendTameable(builder, snapshot);
            }

            if (emitProcreation)
            {
                AppendProcreation(builder, snapshot);
            }

            if (emitGrowup)
            {
                AppendGrowup(builder, snapshot);
            }

        }

        if (emitEggGrow)
        {
            AppendEggGrow(builder, snapshot);
        }
    }

    private static bool ShouldIncludeInReference(ReferencePrefabSnapshot snapshot)
    {
        return snapshot.HasTameable ||
               ShouldEmitProcreation(snapshot) ||
               ShouldEmitGrowup(snapshot) ||
               snapshot.HasEggGrow ||
               snapshot.ConsumeItemNames.Count > 0;
    }

    private static bool ShouldEmitProcreation(ReferencePrefabSnapshot snapshot)
    {
        return snapshot.HasProcreation &&
               (snapshot.MaxCreatures != 0 || Math.Abs(snapshot.TotalCheckRange) > 0.0001f);
    }

    private static bool ShouldEmitGrowup(ReferencePrefabSnapshot snapshot)
    {
        return snapshot.HasGrowup && Math.Abs(snapshot.GrowTime) > 0.0001f;
    }

    private static ReferencePrefabSnapshot CaptureReferenceSnapshot(GameObject prefab)
    {
        Tameable? tameable = prefab.GetComponent<Tameable>();
        Procreation? procreation = prefab.GetComponent<Procreation>();
        Growup? growup = prefab.GetComponent<Growup>();
        EggGrow? eggGrow = prefab.GetComponent<EggGrow>();
        MonsterAI? monsterAI = prefab.GetComponent<MonsterAI>();
        FeedConsumeData consumeData = FeedSystem.ResolveConsumeData(prefab);
        List<string> consumeItemNames = consumeData.ConsumeItems
            .Where(item => item != null)
            .Select(FeedExternalReferenceSupport.ExtractItemDropPrefabName)
            .Concat(FeedExternalReferenceSupport.GetConfiguredConsumeItemNames(prefab))
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new ReferencePrefabSnapshot
        {
            HasMonsterAI = monsterAI != null,
            HasTameable = tameable != null,
            FedDuration = tameable?.m_fedDuration ?? 0f,
            TamingTime = tameable?.m_tamingTime ?? 0f,
            StartsTamed = tameable?.m_startsTamed ?? false,
            Commandable = tameable?.m_commandable ?? false,
            HasProcreation = procreation != null,
            MaxCreatures = procreation?.m_maxCreatures ?? 0,
            TotalCheckRange = procreation?.m_totalCheckRange ?? 0f,
            HasGrowup = growup != null,
            GrowTime = growup?.m_growTime ?? 0f,
            HasEggGrow = eggGrow != null,
            EggGrowTime = eggGrow?.m_growTime ?? 0f,
            ConsumeItemNames = consumeItemNames
        };
    }

    private static IEnumerable<GameObject> EnumerateReferencePrefabs(IEnumerable<GameObject>? externalPrefabs = null)
    {
        if (ZNetScene.instance?.m_prefabs != null)
        {
            foreach (GameObject prefab in ZNetScene.instance.m_prefabs)
            {
                yield return prefab;
            }
        }

        foreach (GameObject prefab in externalPrefabs ?? FeedExternalReferenceSupport.GetExternalPrefabs())
        {
            yield return prefab;
        }
    }

    private static void RestoreReferenceSnapshot(GameObject prefab, ReferencePrefabSnapshot snapshot)
    {
        if (snapshot.HasMonsterAI)
        {
            MonsterAI? monsterAI = prefab.GetComponent<MonsterAI>();
            if ((object?)monsterAI != null)
            {
                monsterAI.m_consumeItems = ResolveItemDrops(snapshot.ConsumeItemNames);
            }
        }

        if (snapshot.HasTameable)
        {
            Tameable? tameable = prefab.GetComponent<Tameable>();
            if ((object?)tameable != null)
            {
                tameable.m_fedDuration = snapshot.FedDuration;
                tameable.m_tamingTime = snapshot.TamingTime;
                tameable.m_startsTamed = snapshot.StartsTamed;
                tameable.m_commandable = snapshot.Commandable;
            }
        }

        if (snapshot.HasProcreation)
        {
            Procreation? procreation = prefab.GetComponent<Procreation>();
            if ((object?)procreation != null)
            {
                procreation.m_maxCreatures = snapshot.MaxCreatures;
                procreation.m_totalCheckRange = snapshot.TotalCheckRange;
            }
        }

        if (snapshot.HasGrowup)
        {
            Growup? growup = prefab.GetComponent<Growup>();
            if ((object?)growup != null)
            {
                growup.m_growTime = snapshot.GrowTime;
            }
        }

        if (snapshot.HasEggGrow)
        {
            EggGrow? eggGrow = prefab.GetComponent<EggGrow>();
            if ((object?)eggGrow != null)
            {
                eggGrow.m_growTime = snapshot.EggGrowTime;
            }
        }
    }

    private static List<ItemDrop> ResolveItemDrops(IEnumerable<string> itemNames)
    {
        List<ItemDrop> itemDrops = new();
        if ((object?)ObjectDB.instance == null)
        {
            return itemDrops;
        }

        foreach (string itemName in itemNames)
        {
            GameObject? itemPrefab = ObjectDB.instance.GetItemPrefab(itemName);
            ItemDrop? itemDrop = itemPrefab == null ? null : itemPrefab.GetComponent<ItemDrop>();
            if ((object?)itemDrop != null)
            {
                itemDrops.Add(itemDrop);
            }
        }

        return itemDrops;
    }

    private static void AppendConsumeItems(StringBuilder builder, List<string> itemNames)
    {
        if (itemNames.Count == 0)
        {
            AppendLine(builder, 1, "consumeItems: []");
            return;
        }

        AppendLine(builder, 1, "consumeItems:");
        foreach (string itemName in itemNames)
        {
            AppendLine(builder, 2, $"- {FormatYamlString(itemName)}");
        }
    }

    private static void AppendTameable(StringBuilder builder, ReferencePrefabSnapshot snapshot)
    {
        AppendLine(builder, 1, $"tame: {FormatInlineRawList(FormatFloat(snapshot.FedDuration), FormatFloat(snapshot.TamingTime), FormatBool(snapshot.StartsTamed), FormatBool(snapshot.Commandable))}");
    }

    private static void AppendProcreation(StringBuilder builder, ReferencePrefabSnapshot snapshot)
    {
        AppendLine(builder, 1, $"procreation: {FormatInlineRawList(snapshot.MaxCreatures.ToString(CultureInfo.InvariantCulture), FormatFloat(snapshot.TotalCheckRange))}");
    }

    private static void AppendGrowup(StringBuilder builder, ReferencePrefabSnapshot snapshot)
    {
        AppendLine(builder, 1, $"grow: [{FormatFloat(snapshot.GrowTime)}]");
    }

    private static void AppendEggGrow(StringBuilder builder, ReferencePrefabSnapshot snapshot)
    {
        AppendLine(builder, 1, $"eggGrow: [{FormatFloat(snapshot.EggGrowTime)}]");
    }

    private static void AppendGeneratedHeader(StringBuilder builder, string artifactKind)
    {
        builder.AppendLine($"# Generated by FeedLikeGrandma ({artifactKind}).");
        builder.AppendLine("# Owner sections are best-effort guesses from the Valheim manifest and loaded asset bundles.");
        builder.AppendLine("# Includes prefabs with Tameable, Procreation, Growup, or EggGrow components.");
        builder.AppendLine("# Copy blocks into feed.yml and remove fields you do not want to override.");
        builder.AppendLine();
    }

    private static void AppendTemplateComment(StringBuilder builder, string text)
    {
        builder.Append("# ");
        builder.AppendLine(text);
    }

    private static void AppendTemplateBlankLine(StringBuilder builder)
    {
        builder.AppendLine("#");
    }

    private static void AppendTemplateLine(StringBuilder builder, int indent, string text)
    {
        builder.Append("# ");
        builder.Append(' ', indent * 2);
        builder.AppendLine(text);
    }

    private static void AppendLine(StringBuilder builder, int indent, string text)
    {
        builder.Append(' ', indent * 2);
        builder.AppendLine(text);
    }

    private static string FormatInlineRawList(params string[] values)
    {
        return "[" + string.Join(", ", values) + "]";
    }

    private static string FormatFloat(float value)
    {
        return value.ToString("0.###", CultureInfo.InvariantCulture);
    }

    private static string FormatBool(bool value)
    {
        return value ? "true" : "false";
    }

    private static string FormatKey(string value)
    {
        return FormatYamlString(value);
    }

    private static string FormatYamlString(string? value)
    {
        string text = value ?? "";
        if (text.Length > 0 && text.All(character => char.IsLetterOrDigit(character) || character is '_' or '-' or '.' or '$' or '/'))
        {
            return text;
        }

        return "'" + text.Replace("'", "''") + "'";
    }

    private sealed class ReferencePrefabSnapshot
    {
        public bool HasMonsterAI { get; init; }
        public bool HasTameable { get; init; }
        public float FedDuration { get; init; }
        public float TamingTime { get; init; }
        public bool StartsTamed { get; init; }
        public bool Commandable { get; init; }
        public bool HasProcreation { get; init; }
        public int MaxCreatures { get; init; }
        public float TotalCheckRange { get; init; }
        public bool HasGrowup { get; init; }
        public float GrowTime { get; init; }
        public bool HasEggGrow { get; init; }
        public float EggGrowTime { get; init; }
        public List<string> ConsumeItemNames { get; init; } = new();

        private bool HasTameRelatedComponent =>
            HasTameable ||
            HasProcreation ||
            HasGrowup ||
            HasEggGrow;

        public bool HasData =>
            HasTameRelatedComponent;
    }
}
