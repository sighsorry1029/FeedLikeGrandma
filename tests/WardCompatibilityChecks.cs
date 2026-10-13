using System;
using System.IO;
using System.Linq;
using System.Reflection;

// Calls the real FLG prefixes with an earlier Harmony denial. No Unity,
// native detours, players, or Orb transactions are initialized.
internal static class WardCompatibilityChecks
{
    private static int Main(string[] args)
    {
        string modPath = Path.GetFullPath(args[0]);
        string core = Path.GetFullPath(args[1]);
        AppDomain.CurrentDomain.AssemblyResolve += (_, e) =>
        {
            foreach (string directory in new[] { Path.GetDirectoryName(modPath), core })
            {
                string path = Path.Combine(directory, new AssemblyName(e.Name).Name + ".dll");
                if (File.Exists(path)) return Assembly.LoadFrom(path);
            }
            return null;
        };
        Assembly mod = Assembly.LoadFrom(modPath);
        if (mod.GetReferencedAssemblies().Any(a => a.Name == "STUWard")) throw new Exception("Hard STUWard reference");
        int checks = 1;
        foreach (string name in new[] { "TameableUseItemPatch", "HumanoidUseItemPatch", "PlayerUseHotbarItemPatch" })
        {
            MethodInfo prefix = mod.GetType("FeedLikeGrandma." + name, true).GetMethod("Prefix", BindingFlags.NonPublic | BindingFlags.Static);
            // Deliberately null: any attempt to execute the body after an earlier
            // denial would fail or mutate __result, instead of silently passing.
            foreach (bool priorResult in new[] { false, true })
            {
                object[] values = prefix.GetParameters().Select(p => p.Name == "__runOriginal" ? (object)false :
                    p.Name == "__result" ? priorResult : p.ParameterType.IsValueType ? Activator.CreateInstance(p.ParameterType) : null).ToArray();
                if ((bool)prefix.Invoke(null, values)) throw new Exception(name + " ignored denial");
                int resultIndex = Array.FindIndex(prefix.GetParameters(), p => p.Name == "__result");
                if (resultIndex >= 0 && (bool)values[resultIndex] != priorResult) throw new Exception("Previous result overwritten");
                checks++;
            }
        }
        Console.WriteLine("PASS: " + checks + " actual-prefix/optional-reference checks. No Unity or network transactions executed.");
        return 0;
    }
}
