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

    /// <summary>Default minimum size of the duplicate search: 1 MB.</summary>
    public const long DuplicateMinSizeDefault = 1024 * 1024;

    /// <summary>Smallest allowed minimum size: 1 KB (smaller files are many and save next to nothing).</summary>
    public const long DuplicateMinSizeLowest = 1024;

    /// <summary>Largest allowed minimum size: 1 TB.</summary>
    public const long DuplicateMinSizeHighest = 1024L * 1024 * 1024 * 1024;

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

    /// <summary>Folder names skipped only on network locations (default <c>bin</c>, <c>obj</c>).</summary>
    [JsonPropertyName("excludedNetworkFolderNames")]
    public IReadOnlyList<string> ExcludedNetworkFolderNames { get; init; } = ExcludeRules.DefaultNetworkFolderNames;

    [JsonPropertyName("excludedPaths")]
    public IReadOnlyList<string> ExcludedPaths { get; init; } = ExcludeRules.DefaultPaths;

    [JsonPropertyName("excludedFileNames")]
    public IReadOnlyList<string> ExcludedFileNames { get; init; } = ExcludeRules.DefaultFileNames;

    [JsonPropertyName("firstRunCompleted")]
    public bool FirstRunCompleted { get; init; }

    /// <summary>
    /// True once the Start menu folders were checked and, where no location contained them, added as locations
    /// (PLAN.md 7.3). Happens only once, so a Start menu location the user removed stays removed.
    /// </summary>
    [JsonPropertyName("startMenuChecked")]
    public bool StartMenuChecked { get; init; }

    /// <summary>
    /// Stage 10 (PLAN.md 11.2): files smaller than this are left out of the duplicate search. Default 1 MB; kept between
    /// <see cref="DuplicateMinSizeLowest"/> and <see cref="DuplicateMinSizeHighest"/> by <see cref="Sanitized"/>.
    /// </summary>
    [JsonPropertyName("duplicateMinSize")]
    public long DuplicateMinSize { get; init; } = DuplicateMinSizeDefault;

    /// <summary>
    /// Duplicates window: hide groups of program and database files (<see cref="DuplicateFilter"/>). Switched in the
    /// window itself and remembered here. Default on.
    /// </summary>
    [JsonPropertyName("hideTechnicalDuplicates")]
    public bool HideTechnicalDuplicates { get; init; } = true;

    /// <summary>
    /// Stage 11 (PLAN.md 11.3): recognize text in the images of <see cref="OcrFolders"/>. Default off: after an update the
    /// program must not start reading every screenshot without being asked.
    /// </summary>
    [JsonPropertyName("ocrEnabled")]
    public bool OcrEnabled { get; init; }

    /// <summary>
    /// Local folders whose images are read for text. Null until the list was set once: then it stands for the Windows
    /// Screenshots folder (<see cref="ImageTextFolders.Effective"/>).
    /// </summary>
    [JsonPropertyName("ocrFolders")]
    public IReadOnlyList<string>? OcrFolders { get; init; }

    /// <summary>The one-time hint "Search the text in screenshots?" in the search bar was answered (switched on or dismissed).</summary>
    [JsonPropertyName("ocrHintDismissed")]
    public bool OcrHintDismissed { get; init; }

    /// <summary>
    /// Stage 12 (PLAN.md 11.3): global shortcut that opens the screenshot history, as text like <see cref="Hotkey"/>. Empty
    /// (the default) means none. It must differ from <see cref="Hotkey"/> (<see cref="Sanitized"/> clears it otherwise).
    /// </summary>
    [JsonPropertyName("historyHotkey")]
    public string HistoryHotkey { get; init; } = string.Empty;

    /// <summary>
    /// Stage 14 (PLAN.md 11.5): check mode of the quick scan. While true, a quick scan lists every folder anyway and only
    /// logs the folders it would have skipped wrongly. No field in the window; switched off in <c>settings.json</c> after
    /// the check period.
    /// </summary>
    [JsonPropertyName("quickScanShadow")]
    public bool QuickScanShadow { get; init; } = true;

    /// <summary>Stage 14: days after which an automatic scan of a location with quick scan is a full one again (1–90).</summary>
    [JsonPropertyName("fullScanIntervalDays")]
    public int FullScanIntervalDays { get; init; } = FullScanIntervalDefault;

    public const int FullScanIntervalDefault = 7;

    [JsonIgnore]
    public TimeSpan FullScanInterval => TimeSpan.FromDays(Math.Clamp(FullScanIntervalDays, 1, 90));

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; init; }

    /// <summary>The hotkey, or Alt+Space if the text is not a valid hotkey.</summary>
    [JsonIgnore]
    public HotkeyGesture HotkeyValue
        => HotkeyGesture.TryParse(Hotkey, out var gesture) ? gesture : HotkeyGesture.Default;

    /// <summary>The shortcut of the screenshot history, or null if none is set, it is not valid or it equals the one of the search bar.</summary>
    [JsonIgnore]
    public HotkeyGesture? HistoryHotkeyValue
        => HotkeyGesture.TryParse(HistoryHotkey, out var gesture) && !HotkeyRules.Conflicts(gesture, HotkeyValue) ? gesture : null;

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

    /// <summary>The exclusion lists reset to the defaults of PLAN.md 7.3.</summary>
    public AppSettings WithDefaultExclusions() => this with
    {
        ExcludedFolderNames = ExcludeRules.DefaultFolderNames,
        ExcludedNetworkFolderNames = ExcludeRules.DefaultNetworkFolderNames,
        ExcludedPaths = ExcludeRules.DefaultPaths,
        ExcludedFileNames = ExcludeRules.DefaultFileNames,
    };

    /// <summary>The exclusion rules these settings describe; applied at the next scan.</summary>
    /// <remarks>
    /// With the image text on, its folders are always recorded like the Start menu folders (PLAN.md 7.3), so a folder
    /// below an excluded path (e.g. in <c>AppData</c>) still gets its images into the directory.
    /// </remarks>
    /// <param name="kind">Kind of the location scanned; network locations also skip <see cref="ExcludedNetworkFolderNames"/>.</param>
    public ExcludeRules CreateExcludeRules(RootKind kind = RootKind.Local)
        => new(
            kind == RootKind.Network ? [.. ExcludedFolderNames, .. ExcludedNetworkFolderNames] : ExcludedFolderNames,
            ExcludedPaths, ExcludedFileNames,
            includedPaths: OcrEnabled && OcrFolders is { Count: > 0 } folders
                ? [.. ExcludeRules.AlwaysIncludedPaths, .. folders]
                : ExcludeRules.AlwaysIncludedPaths);

    /// <summary>
    /// Replaces invalid values by defaults (unknown hotkey, language or politeness; missing lists) and removes blank
    /// list entries; keeps the minimum size of the duplicate search in its range. A list that is present but empty stays empty:
    /// the user may clear it.
    /// </summary>
    public AppSettings Sanitized() => this with
    {
        Hotkey = HotkeyGesture.TryParse(Hotkey, out var gesture) ? gesture.Format() : HotkeyGesture.Default.Format(),
        HistoryHotkey = SanitizeHistoryHotkey(Hotkey, HistoryHotkey),
        Language = Language is LanguageGerman or LanguageEnglish ? Language : LanguageSystem,
        Politeness = Politeness == PolitenessVeryGentle ? PolitenessVeryGentle : PolitenessNormal,
        ExcludedFolderNames = Clean(ExcludedFolderNames, ExcludeRules.DefaultFolderNames),
        ExcludedNetworkFolderNames = Clean(ExcludedNetworkFolderNames, ExcludeRules.DefaultNetworkFolderNames),
        ExcludedPaths = Clean(ExcludedPaths, ExcludeRules.DefaultPaths),
        ExcludedFileNames = Clean(ExcludedFileNames, ExcludeRules.DefaultFileNames),
        DuplicateMinSize = SanitizeMinSize(DuplicateMinSize),
        FullScanIntervalDays = FullScanIntervalDays is >= 1 and <= 90 ? FullScanIntervalDays : FullScanIntervalDefault,
        OcrFolders = OcrFolders is null ? null : ImageTextFolders.Clean(OcrFolders),
    };

    private static string SanitizeHistoryHotkey(string searchHotkey, string historyHotkey)
    {
        if (!HotkeyGesture.TryParse(historyHotkey, out var gesture))
        {
            return string.Empty;
        }

        var search = HotkeyGesture.TryParse(searchHotkey, out var parsed) ? parsed : HotkeyGesture.Default;
        return HotkeyRules.Conflicts(gesture, search) ? string.Empty : gesture.Format();
    }

    /// <summary>Zero or less (also a missing value) becomes the default; other values are clamped to 1 KB … 1 TB.</summary>
    public static long SanitizeMinSize(long value)
        => value <= 0 ? DuplicateMinSizeDefault : Math.Clamp(value, DuplicateMinSizeLowest, DuplicateMinSizeHighest);

    private static IReadOnlyList<string> Clean(IReadOnlyList<string>? list, IReadOnlyList<string> fallback)
        => list is null ? fallback : [.. list.Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s.Trim())];
}
