using System;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Serialization;
using HarmonyLib;

// Uses the original game type and Mono runtime, but intercepts it before any Unity/game logic runs.
internal static class BreedingHarmonyChecks
{
    private static int intercepted;
    private static bool StopProcreation() { intercepted++; return false; }

    private static int Main(string[] args)
    {
        if (args.Length != 1 || Type.GetType("Mono.Runtime") == null)
        {
            Console.Error.WriteLine("Run under original game Mono with the built plugin path.");
            return 2;
        }
        try
        {
            string path = Path.GetFullPath(args[0]);
            AppDomain.CurrentDomain.AssemblyResolve += (_, e) =>
            {
                string dependency = Path.Combine(Path.GetDirectoryName(path), new AssemblyName(e.Name).Name + ".dll");
                return File.Exists(dependency) ? Assembly.LoadFrom(dependency) : null;
            };
            Assembly plugin = Assembly.LoadFrom(path);
            Type access = plugin.GetType("FeedLikeGrandma.GameAccess", true);
            // A delegate cached before patching must still pass through subsequent Harmony patches.
            RuntimeHelpers.RunClassConstructor(access.TypeHandle);
            Type procreation = Assembly.Load("assembly_valheim").GetType("Procreation", true);
            MethodInfo original = procreation.GetMethod("Procreate", BindingFlags.NonPublic | BindingFlags.Instance);
            MethodInfo prefix = typeof(BreedingHarmonyChecks).GetMethod("StopProcreation", BindingFlags.NonPublic | BindingFlags.Static);
            Harmony harmony = new Harmony("FeedLikeGrandma.BreedingChecks");
            harmony.Patch(original, prefix: new HarmonyMethod(prefix));
            object instance = FormatterServices.GetUninitializedObject(procreation);
            MethodInfo run = access.GetMethod("RunProcreation", BindingFlags.NonPublic | BindingFlags.Static);
            run.Invoke(null, new[] { instance });
            run.Invoke(null, new[] { instance });
            if (intercepted != 2) throw new InvalidOperationException("Cached game delegate bypassed Harmony.");
            harmony.UnpatchSelf();
            Console.WriteLine("PASS: cached private Procreate delegate executes Harmony on original game Mono (2 calls).");
            Console.WriteLine("Scope: method access/detour only. No Unity world, breeding or network transport executed.");
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            return 1;
        }
    }
}
