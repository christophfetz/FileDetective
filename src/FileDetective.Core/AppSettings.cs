using System.Text.Json;
using System.Text.Json.Serialization;

namespace FileDetective.Core;

/// <summary>Language of the user interface (setting <c>language</c>).</summary>
public enum AppLanguage
{
    /// <summary>Follows the Windows display language.</summary>
    System = 0,
    German = 1,
    English = 2,
}

/// <summary>
/// Content of <c>settings.json</c> (PLAN.md 8.5). Immutable; change a copy with <c>with</c> and save it through
/// <see cref="SettingsService"/>. Unknown fields of a newer version are kept in <see cref="ExtensionData"/> and written
/// back unchanged. The locations are not here, they live in the database.
/// </summary>
public sealed record AppSettings
{
    /// <summary>Version of the file layout written by this program.</summary>
    public const int CurrentSchemaVersion = 1;

    public const string LanguageSystem = "system";
    public const string LanguageGerman = "de";
    public const string LanguageEnglish = "en";
    public const string PolitenessNormal = "normal";
    public const string PolitenessVeryGentle = "veryGentle";

    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    /// <summary>Hotkey as text, e.g. <c>Alt+Space</c>; see <see cref="Core.HotkeyGesture"/>.</summary>
    [JsonPropertyName("hotkey")]
    public string Hotkey { get; init; } = Core.HotkeyGesture.Default.Format();

    /// <summary><c>system</c>, <c>de</c> or <c>en</c>.</summary>
    [JsonPropertyName("language")]
    public string Language { get; init; } = LanguageSystem;

    /// <summary>The check itself arrives in stage 8; the setting is stored from now on.</summary>
    [JsonPropertyName("checkForUpdates")]
    public bool CheckForUpdates { get; init; } = true;

    /// <summary><c>normal</c> or <c>veryGentle</c> (PLAN.md 7.4).</summary>
    [JsonPropertyName("networkPoliteness")]
    public string Politeness { get; init; } = PolitenessNormal;

    [JsonPropertyName("excludedFolderNames")]
    public IReadOnlyList<string> ExcludedFolderNames { get; init; } = ExcludeRules.DefaultFolderNames;

    [JsonPropertyName("excludedPaths")]
    public IReadOnlyList<string> ExcludedPaths { get; init; } = ExcludeRules.DefaultPaths;

    [JsonPropertyName("excludedFileNames")]
    public IReadOnlyList<string> ExcludedFileNames { get; init; } = ExcludeRules.DefaultFileNames;

    [JsonPropertyName("firstRunCompleted")]
    public bool FirstRunCompleted { get; init; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; init; }

    /// <summary>The hotkey, or Alt+Space if the text is not a valid hotkey.</summary>
    [JsonIgnore]
    public HotkeyGesture HotkeyValue
        => HotkeyGesture.TryParse(Hotkey, out var gesture) ? gesture : HotkeyGesture.Default;

    [JsonIgnore]
    public AppLanguage LanguageChoice => Language switch
    {
        LanguageGerman => AppLanguage.German,
        LanguageEnglish => AppLanguage.English,
        _ => AppLanguage.System,
    };

    [JsonIgnore]
    public NetworkPoliteness NetworkPolitenessLevel
        => Politeness == PolitenessVeryGentle ? NetworkPoliteness.VeryGentle : NetworkPoliteness.Normal;

    public static string LanguageCode(AppLanguage language) => language switch
    {
        AppLanguage.German => LanguageGerman,
        AppLanguage.English => LanguageEnglish,
        _ => LanguageSystem,
    };

    public static string PolitenessCode(NetworkPoliteness level)
        => level == NetworkPoliteness.VeryGentle ? PolitenessVeryGentle : PolitenessNormal;

    /// <summary>The three exclusion lists reset to the defaults of PLAN.md 7.3.</summary>
    public AppSettings WithDefaultExclusions() => this with
    {
        ExcludedFolderNames = ExcludeRules.DefaultFolderNames,
        ExcludedPaths = ExcludeRules.DefaultPaths,
        ExcludedFileNames = ExcludeRules.DefaultFileNames,
    };

    /// <summary>The exclusion rules these settings describe; applied at the next scan.</summary>
    public ExcludeRules CreateExcludeRules() => new(ExcludedFolderNames, ExcludedPaths, ExcludedFileNames);

    /// <summary>
    /// Replaces invalid values by defaults (unknown hotkey, language or politeness; missing lists) and removes blank
    /// list entries. A list that is present but empty stays empty: the user may clear it.
    /// </summary>
    public AppSettings Sanitized() => this with
    {
        Hotkey = HotkeyGesture.TryParse(Hotkey, out var gesture) ? gesture.Format() : HotkeyGesture.Default.Format(),
        Language = Language is LanguageGerman or LanguageEnglish ? Language : LanguageSystem,
        Politeness = Politeness == PolitenessVeryGentle ? PolitenessVeryGentle : PolitenessNormal,
        ExcludedFolderNames = Clean(ExcludedFolderNames, ExcludeRules.DefaultFolderNames),
        ExcludedPaths = Clean(ExcludedPaths, ExcludeRules.DefaultPaths),
        ExcludedFileNames = Clean(ExcludedFileNames, ExcludeRules.DefaultFileNames),
    };

    private static IReadOnlyList<string> Clean(IReadOnlyList<string>? list, IReadOnlyList<string> fallback)
        => list is null ? fallback : [.. list.Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s.Trim())];
}
