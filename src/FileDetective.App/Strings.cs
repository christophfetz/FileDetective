using System.Resources;

namespace FileDetective.App;

/// <summary>Access to localized UI texts from Strings.resx / Strings.de.resx.</summary>
internal static class Strings
{
    private static readonly ResourceManager Manager =
        new("FileDetective.App.Strings", typeof(Strings).Assembly);

    public static string Get(string key) => Manager.GetString(key) ?? key;
}