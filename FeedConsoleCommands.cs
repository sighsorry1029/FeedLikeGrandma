using System.Collections.Generic;

namespace FeedLikeGrandma;

internal static class FeedConsoleCommands
{
    private static readonly List<string> EmptyOptions = new();
    private static bool Registered;

    internal static void Register()
    {
        if (Registered)
        {
            return;
        }

        Registered = true;
        new Terminal.ConsoleCommand(
            "flg:reference",
            "Write generated FeedLikeGrandma reference YAML. Usage: flg:reference",
            WriteReference,
            optionsFetcher: GetEmptyOptions);

    }

    private static List<string> GetEmptyOptions()
    {
        return EmptyOptions;
    }

    private static void WriteReference(Terminal.ConsoleEventArgs args)
    {
        if (FeedLikeGrandmaPlugin.TryWriteReferenceConfigurationFile(out string path, out string error))
        {
            args.Context?.AddString($"Wrote FeedLikeGrandma reference to {path}");
        }
        else
        {
            args.Context?.AddString(error);
        }
    }

}
