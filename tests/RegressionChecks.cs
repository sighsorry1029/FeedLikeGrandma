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
            CheckOrbCuisineSaveContract(assembly);
            CheckCuisineMethodContract(assembly);
            CheckCuisineMissingStatePolicy(assembly);
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
        Check((int)Get(payload, "SerializationVersion") == 3 && (string)Get(payload, "CuisineState") == "",
              "legacy v3 has no invented Cuisine state");
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
        foreach (int version in new[] { 2, 5 })
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

    private static void CheckOrbCuisineSaveContract(Assembly assembly)
    {
        Type payloadType = assembly.GetType("FeedLikeGrandma.TamingOrbSystem+CapturedTamePayload", true);
        MethodInfo deserialize = payloadType.GetMethod("TryDeserialize");
        MethodInfo serialize = payloadType.GetMethod("Serialize");
        object fresh = Activator.CreateInstance(payloadType, true);
        Check((int)Get(fresh, "SerializationVersion") == 4, "new captures default to v4");

        // Build independently with the documented ZPackage binary primitives: v3
        // fields followed by one versioned Cuisine string. The original fixture stays intact.
        foreach (string state in new[] { "", "1|7200000000|2|1", "1|0|0|0", "1|72000000000|2147483647|1" })
        {
            string fixture = BuildOrbV4(state);
            object[] arguments = { fixture, null, false };
            Check((bool)deserialize.Invoke(null, arguments), "load v4 Cuisine state '" + state + "'");
            object payload = arguments[1];
            Check((int)Get(payload, "SerializationVersion") == 4 && (string)Get(payload, "CuisineState") == state &&
                  (string)Get(payload, "Prefab") == "Wolf" && (float)Get(payload, "SaddleStamina") == 76.5f &&
                  (bool)Get(payload, "OdinHorseHasArmor"), "preserve v4 state and existing optional fields");
            Check((string)serialize.Invoke(payload, null) == fixture, "v4 exact serialization round trip");
        }

        byte[] complete = Convert.FromBase64String(BuildOrbV4("1|7200000000|2|1"));
        foreach (byte[] damaged in new[]
        {
            complete.Take(complete.Length - 1).ToArray(),
            complete.Concat(new byte[] { 0 }).ToArray(),
            complete.Take(Convert.FromBase64String(OrbV3).Length).ToArray()
        })
        {
            object[] arguments = { Convert.ToBase64String(damaged), null, false };
            Check(!(bool)deserialize.Invoke(null, arguments) && arguments[1] == null,
                  "reject missing, truncated, or trailing v4 Cuisine bytes");
        }

        foreach (string invalid in new[]
        {
            "not-state", "2|7200000000|2|1", "1|-1|2|1", "1|72000000001|2|1",
            "1|NaN|2|1", "1|Infinity|2|1", "1|9223372036854775808|2|1", "1|0|-1|0",
            "1|0|2147483648|0", "1|0|0|2", "1|0|0|true", "1|0|0", "1|0|0|1|extra"
        })
        {
            object[] arguments = { BuildOrbV4(invalid), null, false };
            Check(!(bool)deserialize.Invoke(null, arguments) && arguments[1] == null,
                  "reject invalid embedded Cuisine state '" + invalid + "'");
        }

        payloadType.GetProperty("CuisineState").SetValue(fresh, "1|-1|0|0");
        bool rejected = false;
        try { serialize.Invoke(fresh, null); }
        catch (TargetInvocationException error) { rejected = error.InnerException is InvalidOperationException; }
        Check(rejected, "do not write invalid captured Cuisine state");
    }

    private static void CheckCuisineMethodContract(Assembly assembly)
    {
        MethodInfo resolve = assembly.GetType("FeedLikeGrandma.FeedCuisineCompatibility", true)
            .GetMethod("ReadMilkingMethod", BindingFlags.NonPublic | BindingFlags.Static);
        object resolved = resolve.Invoke(null, new object[] { typeof(CompatibleCuisineMethods), "Postfix", new[] { typeof(string) } });
        Check(resolved is MethodInfo, "Cuisine hook binding uses member structure without a plugin version input");
        foreach (Type incompatible in new[]
        {
            typeof(InstanceCuisineMethods), typeof(ReturnValueCuisineMethods), typeof(ChangedParameterCuisineMethods),
            typeof(BroadParameterCuisineMethods), typeof(GenericCuisineMethods), typeof(MissingCuisineMethods)
        })
        {
            bool rejected = false;
            try { resolve.Invoke(null, new object[] { incompatible, "Postfix", new[] { typeof(string) } }); }
            catch (TargetInvocationException error) { rejected = error.InnerException is MissingMethodException; }
            Check(rejected, "reject incompatible Cuisine hook " + incompatible.Name);
        }
    }

    private static class CompatibleCuisineMethods { private static void Postfix(string value) { } }
    private sealed class InstanceCuisineMethods { private void Postfix(string value) { } }
    private static class ReturnValueCuisineMethods { private static bool Postfix(string value) => true; }
    private static class ChangedParameterCuisineMethods { private static void Postfix(int value) { } }
    private static class BroadParameterCuisineMethods { private static void Postfix(object value) { } }
    private static class GenericCuisineMethods { private static void Postfix<T>(string value) { } }
    private static class MissingCuisineMethods { }

    private static void CheckCuisineMissingStatePolicy(Assembly assembly)
    {
        Type bridge = assembly.GetType("FeedLikeGrandma.FeedCuisineCompatibility", true);
        MethodInfo parseRecord = bridge.GetMethod("TryParseRecord", BindingFlags.NonPublic | BindingFlags.Static);
        object[] missing = { "", 0L, null };
        Check((bool)parseRecord.Invoke(null, missing) && (long)missing[1] == 0L && (string)missing[2] == "",
              "missing live Cuisine record is ready without an artificial cooldown or receipt");
        object[] saved = { "1|7200000000|receipt", 0L, null };
        Check((bool)parseRecord.Invoke(null, saved) && (long)saved[1] == 7200000000L && (string)saved[2] == "receipt",
              "existing Cuisine expiry and receipt remain exact");
        foreach (string corrupt in new[] { "broken", "2|0|", "1|-1|", "1|NaN|", "1|0" })
            Check(!(bool)parseRecord.Invoke(null, new object[] { corrupt, 0L, null }),
                  "corrupt Cuisine record is not treated as absent: " + corrupt);
        MethodInfo parseState = bridge.GetMethod("TryParseState", BindingFlags.NonPublic | BindingFlags.Static);
        object[] legacy = { "", 0L, 0, false };
        Check((bool)parseState.Invoke(null, legacy) && (long)legacy[1] == 0L && (int)legacy[2] == 0 && !(bool)legacy[3],
              "old Orb without Cuisine data has zero cooldown and no invented breeding history");
    }

    private static string BuildOrbV4(string cuisineState)
    {
        byte[] existing = Convert.FromBase64String(OrbV3);
        using (var stream = new MemoryStream())
        using (var writer = new BinaryWriter(stream))
        {
            writer.Write(4);
            writer.Write(existing, sizeof(int), existing.Length - sizeof(int));
            writer.Write(cuisineState);
            writer.Flush();
            return Convert.ToBase64String(stream.ToArray());
        }
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
