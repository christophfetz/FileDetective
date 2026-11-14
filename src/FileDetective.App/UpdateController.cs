using System.IO;
using System.Reflection;
using System.Windows.Threading;
using FileDetective.Core;

namespace FileDetective.App;

/// <summary>Version of the running program, from the assembly (set in the csproj, or by the release workflow).</summary>
internal static class AppInfo
{
    /// <summary>Version text without a build suffix, e.g. <c>0.1.0</c>.</summary>
    public static string Version { get; } = ReadVersion();

    private static string ReadVersion()
    {
        string version = Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? "0.0.0";
        int plus = version.IndexOf('+', StringComparison.Ordinal);
        return plus > 0 ? version[..plus] : version;
    }
}

/// <summary>
/// Glue between the <see cref="UpdateChecker"/> / <see cref="UpdateInstaller"/> of the Core and the windows: asks
/// GitHub a few minutes after the start and then hourly whether a check is due (the checker itself allows one a day and
/// does nothing while the repository is the placeholder or the setting is off), keeps the newest known release for the
/// tray menu and the settings, and installs it on request. Lives on the UI thread.
/// </summary>
internal sealed class UpdateController : IDisposable
{
    private static readonly TimeSpan FirstCheckDelay = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan CheckInterval = TimeSpan.FromHours(1);

    private readonly UpdateChecker _checker;
    private readonly UpdateInstaller _installer;
    private readonly Func<bool> _enabled;
    private readonly DispatcherTimer _timer;
    private bool _installing;

    public UpdateController(Dispatcher dispatcher, Func<bool> enabled, string exePath)
    {
        _enabled = enabled;
        ExePath = exePath;
        _checker = new UpdateChecker(AppInfo.Version, stateFile: Path.Combine(AppPaths.DataDirectory, "update-state.json"));
        _installer = new UpdateInstaller(Path.Combine(AppPaths.DataDirectory, "update"), currentVersion: AppInfo.Version);
        _timer = new DispatcherTimer(DispatcherPriority.Background, dispatcher) { Interval = FirstCheckDelay };
        _timer.Tick += async (_, _) =>
        {
            _timer.Interval = CheckInterval;
            await CheckAsync();
        };
    }

    public string ExePath { get; }

    /// <summary>Raised on the UI thread when <see cref="Available"/> or <see cref="Installing"/> changed.</summary>
    public event Action? Changed;

    /// <summary>The newer release, or null. Null as long as the update check is off or the repository is the placeholder.</summary>
    public UpdateInfo? Available => _enabled() ? _checker.Available : null;

    public bool Installing => _installing;

    /// <summary>True if a click can install the update in place; otherwise the click opens the release page.</summary>
    public bool CanInstallInPlace => Available is { CanInstall: true } && UpdateInstaller.CanReplace(ExePath);

    public void Start() => _timer.Start();

    public async Task CheckAsync()
    {
        var result = await _checker.CheckIfDueAsync(_enabled());
        if (result.Outcome != UpdateCheckOutcome.Skipped)
        {
            Changed?.Invoke();
        }
    }

    /// <summary>Downloads and swaps the exe. On <see cref="UpdateInstallStatus.Installed"/> the caller restarts the program.</summary>
    public async Task<UpdateInstallResult> InstallAsync()
    {
        if (Available is not { } info || _installing)
        {
            return new UpdateInstallResult(UpdateInstallStatus.NotSupported);
        }

        _installing = true;
        Changed?.Invoke();
        try
        {
            return await _installer.InstallAsync(info, ExePath);
        }
        finally
        {
            _installing = false;
            Changed?.Invoke();
        }
    }

    public void Dispose()
    {
        _timer.Stop();
        _installer.Dispose();
        _checker.Dispose();
    }
}
