using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;

internal static class RegressionChecks
{
    // Version 3 save fixture: every persisted field is populated, including optional mod state.
    private const string OrbV3 = "AwAAAARXb2xmB0dyYW5kbWEDAAAAAAAqQgAAyEIBDGludmVudG9yeS12MRXNWwcAAAAAsWjeOgAAAAAAAEhBAAA1QgIAAAAAAJlCBEZpcmUHQXJtb3JlZAE=";
    private static MethodInfo parse;
    private static int passed;

    private static int Main(string[] args)
    {
        if (args.Length != 1)
        {
            Console.Error.WriteLine("Usage: RegressionChecks.exe <built FeedLikeGrandma.dll>");
            return 2;
        }

        string assemblyPath = Path.GetFullPath(args[0]);
        string directory = Path.GetDirectoryName(assemblyPath);
        string[] searchDirectories = { directory };
        // Never resolve publicized copies.
        AppDomain.CurrentDomain.AssemblyResolve += (_, eventArgs) =>
        {
            string name = new AssemblyName(eventArgs.Name).Name;
            foreach (string searchDirectory in searchDirectories)
            {
                string path = Path.Combine(searchDirectory, name + ".dll");
                if (File.Exists(path)) return Assembly.LoadFrom(path);
            }
            return null;
        };

        try
        {
            Assembly assembly = Assembly.LoadFrom(assemblyPath);
            parse = assembly.GetType("FeedLikeGrandma.FeedingYamlParser", true).GetMethod("Parse");
            CheckOrbSaveContract(assembly);
            CheckYamlContracts();
            Console.WriteLine("Passed " + passed + " checks (.NET " + Environment.Version + ").");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            return 1;
        }
    }

    private static void CheckYamlContracts()
    {
        Parsed result = Parse("Wolf: {}");
        Check(!result.HasErrors && !(bool)Get(Rule(result), "HasConsumeItemsOverride"), "omitted food override");
        result = Parse("Wolf:\n  consumeItems: []\n  restrictedBiome: []");
        Check(!result.HasErrors && (bool)Get(Rule(result), "HasConsumeItemsOverride") &&
              (bool)Get(Rule(result), "HasRestrictedBiomeOverride") && Foods(result).Count == 0,
              "explicit empty food and biome overrides");

        result = Parse("Wolf:\n  consumeItems:\n    - Fish1@5, 500\n  levelUpItems:\n    2:\n      - Fish2, 300");
        object firstFood = Foods(result)[0];
        IList levelFoods = (IList)((IDictionary)Get(Rule(result), "LevelUpItems"))[2];
        Check(!result.HasErrors && (int)Get(firstFood, "RequiredQuality") == 5 &&
              (float)Get(firstFood, "Value") == 500f && (float)Get(levelFoods[0], "Value") == 300f,
              "quality and current-level foods");
        Check(Parse("Wolf:\n  consumeItems:\n    - Fish1@0, 1").HasErrors, "reject zero quality");
        Check(Parse("defaults:\n  feed: [1, 2]").HasErrors, "reject wrong tuple size");
        Check(Parse("Wolf:\n  unknownPolicy: 1").HasErrors, "reject unknown field");

        CultureInfo previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
            result = Parse("foodNumbers:\n  - Fish1, 1.5");
            Check(!result.HasErrors && (float)((IDictionary)Get(result.Config, "FoodNumbers"))["Fish1"] == 1.5f,
                  "invariant decimal input");
        }
        finally { CultureInfo.CurrentCulture = previous; }

        result = Parse("Wolf:\n  consumeItems:\n    - Fish1, 1\n    - Fish1, 2");
        Check(!result.HasErrors && result.Warnings.Count == 1 && Foods(result).Count == 1 &&
              (float)Get(Foods(result)[0], "Value") == 2f, "duplicate food last value wins");

        foreach (string number in new[] { "NaN", "Infinity", "-Infinity", "1e100" })
        {
            Check(Parse("defaults:\n  feed: [" + number + ", 1, 1, 1, 60]").HasErrors,
                  "reject non-finite default " + number);
            Check(Parse("foodNumbers:\n  - Fish1, " + number).HasErrors,
                  "reject non-finite food number " + number);
            Check(Parse("Wolf:\n  consumeItems:\n    - Fish1, " + number).HasErrors,
                  "reject non-finite local food " + number);
            Check(Parse("Wolf:\n  grow: [" + number + "]").HasErrors,
                  "reject non-finite growth " + number);
        }
        Check(!Parse("defaults:\n  feed: [0, 1.5, 2, 3, 60]\nWolf:\n  eggGrow: [0]").HasErrors,
              "finite zero and fractional values remain valid");
    }

    private static void CheckOrbSaveContract(Assembly assembly)
    {
        Type payloadType = assembly.GetType("FeedLikeGrandma.TamingOrbSystem+CapturedTamePayload", true);
        MethodInfo deserialize = payloadType.GetMethod("TryDeserialize");
        object[] arguments = { OrbV3, null, false };
        Check((bool)deserialize.Invoke(null, arguments), "load existing v3 Orb fixture");
        object payload = arguments[1];
        var expected = new Dictionary<string, object>
        {
            { "Prefab", "Wolf" }, { "Name", "Grandma" }, { "Level", 3 },
            { "Health", 42.5f }, { "MaxHealth", 100f }, { "HasSaddle", true },
            { "Inventory", "inventory-v1" }, { "LastFedAgeTicks", 123456789L },
            { "SpawnAgeTicks", 987654321L }, { "StarProgress", 12.5f },
            { "GrowthElapsed", 45.25f }, { "CllcEffect", 2 }, { "SaddleStamina", 76.5f },
            { "CllcInfusion", "Fire" }, { "CllcExtraEffect", "Armored" }, { "OdinHorseHasArmor", true }
        };
        Check(expected.All(pair => Equals(Get(payload, pair.Key), pair.Value)), "preserve all v3 fields");
        Check((string)payloadType.GetMethod("Serialize").Invoke(payload, null) == OrbV3,
              "v3 binary format unchanged");

        foreach (string text in new[] { "", "not base64", Convert.ToBase64String(new byte[] { 3, 0, 0, 0 }) })
        {
            Check(!(bool)deserialize.Invoke(null, new object[] { text, null, false }), "reject damaged Orb fixture");
        }
        foreach (int version in new[] { 2, 4 })
        {
            byte[] bytes = Convert.FromBase64String(OrbV3);
            Array.Copy(BitConverter.GetBytes(version), bytes, 4);
            Check(!(bool)deserialize.Invoke(null, new object[] { Convert.ToBase64String(bytes), null, false }),
                  "reject unsupported Orb version " + version);
        }

        MethodInfo decodeInventory = payloadType.GetMethod("DecodeInventory");
        payloadType.GetProperty("Inventory").SetValue(payload, "");
        Check(((byte[])decodeInventory.Invoke(payload, null)).Length == 0, "empty Orb inventory remains empty");
        byte[] inventory = { 109, 0, 0, 0, 0, 0, 0, 0, 0, 127, 255 };
        payloadType.GetProperty("Inventory").SetValue(payload, Convert.ToBase64String(inventory));
        Check(((byte[])decodeInventory.Invoke(payload, null)).SequenceEqual(inventory), "preserve opaque 1.0 inventory bytes");
        arguments = new object[] { payloadType.GetMethod("Serialize").Invoke(payload, null), null, false };
        Check((bool)deserialize.Invoke(null, arguments) &&
              ((byte[])decodeInventory.Invoke(arguments[1], null)).SequenceEqual(inventory),
              "inventory bytes survive v3 serialization round trip");
        payloadType.GetProperty("Inventory").SetValue(payload, "damaged inventory!");
        bool rejected = false;
        try { decodeInventory.Invoke(payload, null); }
        catch (TargetInvocationException error) { rejected = error.InnerException is FormatException; }
        Check(rejected && (string)Get(payload, "Inventory") == "damaged inventory!",
              "reject damaged inventory base64 without changing stored data");
    }

    private static Parsed Parse(string yaml)
    {
        var warnings = new List<string>();
        var errors = new List<string>();
        object config = parse.Invoke(null, new object[] { yaml, warnings, errors });
        return new Parsed { Config = config, Warnings = warnings, Errors = errors };
    }

    private static object Get(object instance, string property) => instance.GetType().GetProperty(property).GetValue(instance);
    private static object Rule(Parsed result) => ((IDictionary)Get(result.Config, "Rules"))["Wolf"];
    private static IList Foods(Parsed result) => (IList)Get(Rule(result), "Foods");

    private static void Check(bool condition, string name)
    {
        if (!condition) throw new InvalidOperationException("FAIL: " + name);
        passed++;
        Console.WriteLine("PASS: " + name);
    }

    private sealed class Parsed
    {
        public object Config;
        public List<string> Warnings;
        public List<string> Errors;
        public bool HasErrors => Errors.Count != 0;
    }
}
