using System;
using System.Globalization;
using LocalizationManager;

namespace FeedLikeGrandma;

internal static class FeedLocalization
{
    internal const string TamingOrbNameToken = "$flg_taming_orb";
    internal const string TamingOrbDescriptionToken = "$flg_taming_orb_description";
    internal const string FeedBarrelNameToken = "$flg_feed_barrel";
    internal const string FeedBarrelDescriptionToken = "$flg_feed_barrel_description";

    internal static void Load()
    {
        Localizer.Load();
    }

    internal static string Text(string token)
    {
        return Localization.instance == null ? token : Localization.instance.Localize(token);
    }

    internal static string Format(string token, params object[] args)
    {
        string text = Text(token);
        try
        {
            return string.Format(CultureInfo.InvariantCulture, text, args);
        }
        catch (FormatException)
        {
            return text;
        }
    }

    internal static void Message(Player player, string token, params object[] args)
    {
        player.Message(MessageHud.MessageType.Center, Format(token, args));
    }
}
