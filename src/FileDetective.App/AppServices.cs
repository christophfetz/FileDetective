using System.IO;
using System.Globalization;
using FileDetective.Core;

namespace FileDetective.App;

/// <summary>What the setup and settings windows need from the program; created once in <see cref="App"/>.</summary>
internal sealed class AppServices
{
    public required SettingsService Settings { get; init; }

    public required IndexService Index { get; init; }

    public required DriveDiscovery Drives { get; init; }

    public required AutostartManager Autostart { get; init; }

    /// <summary>Path of the running exe, for the autostart entry.</summary>
    public required string ExePath { get; init; }

    /// <summary>Registers a new hotkey; returns null on success or the reason why it failed (the old one then stays).</summary>
    public required Func<HotkeyGesture, string?> TryApplyHotkey { get; init; }

    /// <summary>Gives the hotkey up (true) or takes it back (false) while the settings window records a new one.</summary>
    public required Action<bool> SuspendHotkey { get; init; }
}

/// <summary>Small helpers for locations in the windows.</summary>
internal static class LocationUi
{
    /// <summary>Time limit for anything that asks the file system about a path that may be on a share (PLAN.md 7.7).</summary>
    public static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// True if the folder exists. Runs in the background with a time limit because <c>Directory.Exists</c> can hang for
    /// more than 20 seconds on a dead share; on timeout the answer is false. Only asks for the folder, opens no file.
    /// </summary>
    public static async Task<bool> DirectoryExistsAsync(string path)
    {
        try
        {
            return await Task.Run(() => Directory.Exists(path)).WaitAsync(ProbeTimeout);
        }
        catch (TimeoutException)
        {
            return false;
        }
    }

    /// <summary>The drives for the setup, found in the background with a time limit; empty if it takes too long.</summary>
    public static async Task<IReadOnlyList<DiscoveredDrive>> DiscoverAsync(DriveDiscovery discovery)
    {
        try
        {
            return await Task.Run(discovery.Discover).WaitAsync(TimeSpan.FromSeconds(15));
        }
        catch (TimeoutException)
        {
            DiagnosticLog.Error("Finding the drives took too long.");
            return [];
        }
    }

    /// <summary>Folder selection dialog; null if cancelled.</summary>
    public static string? PickFolder(System.Windows.Window owner)
    {
        using var dialog = new System.Windows.Forms.FolderBrowserDialog
        {
            Description = Strings.Get("Dialog_PickFolder"),
            UseDescriptionForTitle = true,
            ShowNewFolderButton = false,
        };
        var handle = new System.Windows.Interop.WindowInteropHelper(owner).Handle;
        var result = dialog.ShowDialog(new Win32Owner(handle));
        return result == System.Windows.Forms.DialogResult.OK ? dialog.SelectedPath : null;
    }

    public static string KindText(RootKind kind) => Strings.Get(kind switch
    {
        RootKind.Network => "Kind_Network",
        RootKind.Removable => "Kind_Removable",
        _ => "Kind_Local",
    });

    public static string Format(string key, params object[] args)
        => string.Format(CultureInfo.CurrentCulture, Strings.Get(key), args);

    private sealed class Win32Owner(IntPtr handle) : System.Windows.Forms.IWin32Window
    {
        public IntPtr Handle { get; } = handle;
    }
}

/// <summary>Applies the language setting (PLAN.md 8.6): at program start, before any text is read.</summary>
internal static class LanguageSetup
{
    public static void Apply(AppLanguage language)
    {
        string? code = language switch
        {
            AppLanguage.German => "de",
            AppLanguage.English => "en",
            _ => null,
        };

        if (code is null)
        {
            return; // follow Windows
        }

        var culture = CultureInfo.GetCultureInfo(code);
        CultureInfo.DefaultThreadCurrentUICulture = culture;
        CultureInfo.CurrentUICulture = culture;
    }
}
