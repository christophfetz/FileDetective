using System.ComponentModel;
using System.Diagnostics;
using System.Security.Principal;

namespace FileDetective.Core;

/// <summary>How the program was started (PLAN.md 8.1, 11.1).</summary>
public enum StartupMode
{
    /// <summary>Started by the user: the search bar appears (the first time the set-up instead).</summary>
    Normal,

    /// <summary>Started by Windows (<c>--autostart</c>) or after an update (<c>--restart-after</c>): quiet in the background.</summary>
    Autostart,

    /// <summary>
    /// <c>--storage-admin</c>: only the storage space window, normally with administrator rights (PLAN.md 11.1, "Gesperrte
    /// Ordner und Admin-Analyse"). No search, no directory, no tray icon, no hotkey, no updates, nothing written to the data
    /// folder. Works without elevation too.
    /// </summary>
    StorageAdmin,
}

/// <summary>The parsed command line.</summary>
/// <param name="Mode">What the process does.</param>
/// <param name="RestartAfterProcessId">With <c>--restart-after &lt;pid&gt;</c>: the old process to wait for.</param>
public sealed record StartupOptions(StartupMode Mode, int? RestartAfterProcessId)
{
    public const string AutostartArgument = "--autostart";

    public const string RestartAfterArgument = "--restart-after";

    public const string StorageAdminArgument = "--storage-admin";

    /// <summary>
    /// Reads the arguments (case-insensitive). <c>--storage-admin</c> wins over everything else, so a storage process
    /// can never turn into a second search instance; <c>--restart-after</c> is quiet like <c>--autostart</c>.
    /// </summary>
    public static StartupOptions Parse(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);
        bool Has(string name) => args.Any(a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase));

        if (Has(StorageAdminArgument))
        {
            return new StartupOptions(StartupMode.StorageAdmin, null);
        }

        int? restartAfter = null;
        bool restart = false;
        for (int i = 0; i < args.Count; i++)
        {
            if (string.Equals(args[i], RestartAfterArgument, StringComparison.OrdinalIgnoreCase))
            {
                restart = true;
                if (i + 1 < args.Count && int.TryParse(args[i + 1], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int pid))
                {
                    restartAfter = pid;
                }
            }
        }

        var mode = restart || Has(AutostartArgument) ? StartupMode.Autostart : StartupMode.Normal;
        return new StartupOptions(mode, restartAfter);
    }
}

/// <summary>
/// What a process of a <see cref="StartupMode"/> sets up. The program follows these switches at start, so the rules of the
/// storage process (PLAN.md 2.6, 11.1) are written down in one place and tested.
/// </summary>
public sealed record StartupPlan(
    bool SingleInstance,
    bool HoldRunningMarker,
    bool WriteLog,
    bool WritableSettings,
    bool OpenDirectory,
    bool TrayIcon,
    bool Hotkey,
    bool UpdateCheck,
    bool FirstRunSetup,
    bool ShowShares,
    bool OnlyStorageWindow)
{
    public static StartupPlan For(StartupMode mode) => mode switch
    {
        // Only the storage window. It runs beside the normal instance, so it does not take the single-instance lock; the
        // fixed-name marker stays, so the installer closes it before an update. Nothing is written to the data folder:
        // no log, no settings (read only, for the language), no index.db. Shares would come from the directory, so none.
        StartupMode.StorageAdmin => new StartupPlan(
            SingleInstance: false,
            HoldRunningMarker: true,
            WriteLog: false,
            WritableSettings: false,
            OpenDirectory: false,
            TrayIcon: false,
            Hotkey: false,
            UpdateCheck: false,
            FirstRunSetup: false,
            ShowShares: false,
            OnlyStorageWindow: true),
        _ => new StartupPlan(
            SingleInstance: true,
            HoldRunningMarker: true,
            WriteLog: true,
            WritableSettings: true,
            OpenDirectory: true,
            TrayIcon: true,
            Hotkey: true,
            UpdateCheck: true,
            FirstRunSetup: mode == StartupMode.Normal,
            ShowShares: true,
            OnlyStorageWindow: false),
    };
}

/// <summary>Tells whether this process runs with administrator rights; tests use a stand-in.</summary>
public interface IElevationProbe
{
    bool IsElevated { get; }
}

/// <summary>
/// Real <see cref="IElevationProbe"/>: the token of the process is in the Administrators role. With UAC on, this is only
/// true for an elevated process (the filtered token of a normal start does not count as administrator).
/// </summary>
public sealed class WindowsElevationProbe : IElevationProbe
{
    public static WindowsElevationProbe Instance { get; } = new();

    public bool IsElevated
    {
        get
        {
            try
            {
                using var identity = WindowsIdentity.GetCurrent();
                return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
            }
            catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException)
            {
                return false;
            }
        }
    }
}

/// <summary>Starts a program with the request for administrator rights (UAC). The real one is <see cref="ShellRunAsStarter"/>.</summary>
public interface IElevatedStarter
{
    /// <summary>Starts <paramref name="exePath"/> with <paramref name="arguments"/> elevated.</summary>
    /// <exception cref="Win32Exception">Windows refused; code 1223 means the user declined the UAC prompt.</exception>
    void Start(string exePath, string arguments);
}

/// <summary>Real <see cref="IElevatedStarter"/>: shell start with the verb <c>runas</c>, which shows the UAC prompt.</summary>
public sealed class ShellRunAsStarter : IElevatedStarter
{
    public static ShellRunAsStarter Instance { get; } = new();

    public void Start(string exePath, string arguments)
    {
        ArgumentException.ThrowIfNullOrEmpty(exePath);
        using var process = Process.Start(new ProcessStartInfo(exePath)
        {
            Arguments = arguments,
            UseShellExecute = true,
            Verb = "runas",
        });
    }
}

/// <summary>How the request for the storage analysis with administrator rights ended.</summary>
public enum AdminLaunchStatus
{
    /// <summary>The elevated storage process was started.</summary>
    Started,

    /// <summary>The user said No in the UAC prompt (error 1223). Nothing happened.</summary>
    Declined,

    /// <summary>It could not be started; <see cref="AdminLaunchResult.Error"/> says why.</summary>
    Failed,
}

public sealed record AdminLaunchResult(AdminLaunchStatus Status, Exception? Error = null);

/// <summary>
/// "Analyze as administrator…" in the storage window (PLAN.md 2.6, 11.1): starts this program once more with
/// <see cref="StartupOptions.StorageAdminArgument"/> and the UAC prompt. Only on an explicit click; never automatically.
/// </summary>
public static class StorageAdminLauncher
{
    /// <summary>ERROR_CANCELLED: the user declined the UAC prompt.</summary>
    public const int ErrorCancelled = 1223;

    /// <summary>
    /// True if the button is offered: not in the storage process itself and not when this process already has
    /// administrator rights (then the normal window already reads everything).
    /// </summary>
    public static bool CanOffer(StartupMode mode, IElevationProbe elevation)
    {
        ArgumentNullException.ThrowIfNull(elevation);
        return mode != StartupMode.StorageAdmin && !elevation.IsElevated;
    }

    /// <summary>Starts the elevated storage process. Never throws: a refusal or failure is in the result.</summary>
    public static AdminLaunchResult Launch(IElevatedStarter starter, string? exePath)
    {
        ArgumentNullException.ThrowIfNull(starter);
        if (string.IsNullOrEmpty(exePath))
        {
            return new AdminLaunchResult(AdminLaunchStatus.Failed, new InvalidOperationException("The path of the program is unknown."));
        }

        try
        {
            starter.Start(exePath, StartupOptions.StorageAdminArgument);
            return new AdminLaunchResult(AdminLaunchStatus.Started);
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == ErrorCancelled)
        {
            return new AdminLaunchResult(AdminLaunchStatus.Declined);
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            return new AdminLaunchResult(AdminLaunchStatus.Failed, ex);
        }
    }
}
