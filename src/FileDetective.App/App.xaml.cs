using System.Drawing;
using System.Globalization;
using FileDetective.App.Interop;
using FileDetective.App.ViewModels;
using FileDetective.Core;
using Forms = System.Windows.Forms;

namespace FileDetective.App;

public partial class App : System.Windows.Application
{
    private Forms.NotifyIcon? _trayIcon;
    private Forms.ToolStripMenuItem? _statusItem;
    private IndexService? _index;
    private HotkeyManager? _hotkey;
    private SearchBarWindow? _bar;
    private SettingsService? _settings;
    private SingleInstanceGuard? _guard;
    private AppServices? _services;
    private FirstRunWindow? _firstRun;
    private SettingsWindow? _settingsWindow;
    private StorageWindow? _storageWindow;
    private DuplicatesWindow? _duplicatesWindow;
    private UpdateController? _updates;
    private Forms.ToolStripMenuItem? _updateItem;
    private Mutex? _runningMarker;
    private Thumbnails? _thumbnails;

    /// <summary>Start after an update: <c>--restart-after &lt;pid&gt;</c> waits for the old process to end first.</summary>
    private const string RestartAfterArgument = StartupOptions.RestartAfterArgument;

    private void OnStartup(object sender, System.Windows.StartupEventArgs e)
    {
        var options = StartupOptions.Parse(e.Args);
        if (options.Mode == StartupMode.StorageAdmin)
        {
            StartStorageOnly();
            return;
        }

        bool autostart = options.Mode == StartupMode.Autostart;

        // After an update the old process starts this one and ends; wait for it (up to 20 s) so that the single-instance
        // mutex is free. A restart after an update is quiet like an autostart.
        if (options.RestartAfterProcessId is { } oldPid)
        {
            WaitForProcessEnd(oldPid);
        }

        // PLAN.md 8.1: unhandled exceptions are logged and reported in the tray; on the UI thread the program goes on.
        // The log starts working as soon as FileLog.Initialize ran below; until then the shared log writes nothing.
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

        // Settings first: the language must be known before any text is read.
        _settings = new SettingsService(AppPaths.SettingsPath);
        var loadResult = _settings.Load();
        LanguageSetup.Apply(_settings.Current.LanguageChoice);

        // PLAN.md 8.1: one instance per user (and data folder). A second start wakes the first and ends.
        string userId = System.Security.Principal.WindowsIdentity.GetCurrent().User?.Value ?? Environment.UserName;
        _guard = new SingleInstanceGuard(SingleInstanceGuard.BuildName("FileDetective", userId, AppPaths.DataDirectory));
        if (!_guard.TryBecomePrimary())
        {
            if (!autostart)
            {
                _guard.SignalPrimary(); // a start by Windows while it runs already stays quiet
            }

            _guard.Dispose();
            _guard = null;
            Shutdown();
            return;
        }

        FileLog.Initialize(AppPaths.LogPath);
        FileLog.Shared.Info($"FileDetective {AppInfo.Version} starts. Data folder: {AppPaths.DataDirectory}");

        // Fixed-name marker for the installer (see SingleInstanceGuard.RunningMarkerName); held until the process ends.
        try
        {
            _runningMarker = new Mutex(false, SingleInstanceGuard.RunningMarkerName);
        }
        catch (Exception ex)
        {
            FileLog.Shared.Error("Creating the running marker failed", ex);
        }

        string exePath = Environment.ProcessPath ?? string.Empty;
        _ = Task.Run(() => UpdateInstaller.CleanupOld(exePath)); // the .old exe of a former update
        if (loadResult == SettingsLoadResult.CorruptRenamed)
        {
            FileLog.Shared.Info($"settings.json was unreadable and moved to {_settings.BackupPath}; defaults apply.");
        }

        // Nothing here touches the database or the file system of the locations: the index service does that
        // on a background thread.
        _index = new IndexService(AppPaths.DatabasePath, () => _settings.Current);
        _index.Failed += ex => Dispatcher.BeginInvoke(new Action(() =>
            ShowBalloon(Strings.Get("Error_Title"), string.Format(
                CultureInfo.CurrentCulture, Strings.Get("Error_IndexFailed"), ex.Message), Forms.ToolTipIcon.Error)));

        _updates = new UpdateController(Dispatcher, () => _settings.Current.CheckForUpdates, exePath);
        _updates.Changed += RefreshUpdateItem;

        _services = new AppServices
        {
            Settings = _settings,
            Index = _index,
            Drives = new DriveDiscovery(),
            Autostart = new AutostartManager(),
            Updates = _updates,
            InstallUpdate = InstallUpdateAsync,
            ExePath = exePath,
            TryApplyHotkey = TryApplyHotkey,
            SuspendHotkey = suspend =>
            {
                if (suspend)
                {
                    _hotkey?.Suspend();
                }
                else
                {
                    _hotkey?.Resume();
                }
            },
        };

        // Stage 11: a change of the image text settings (switch, folders) is applied in the background.
        var indexService = _index;
        _settings.Changed += changed => indexService.ApplyImageTextAsync().ContinueWith(
            t => FileLog.Shared.Error("Applying the image text settings failed", t.Exception!), TaskContinuationOptions.OnlyOnFaulted);

        var drives = _services.Drives;
        _thumbnails = new Thumbnails(Dispatcher, path => drives.Describe(path) is { Kind: RootKind.Local });
        var viewModel = new SearchBarViewModel(_index, _settings, _thumbnails, Dispatcher);
        _bar = new SearchBarWindow(viewModel);

        CreateTrayIcon();
        _guard.WakeRequested += () => Dispatcher.BeginInvoke(new Action(ShowSearch));
        _guard.StartListening();

        try
        {
            _hotkey = new HotkeyManager();
            _hotkey.Pressed += ToggleSearch;
        }
        catch (Exception ex)
        {
            FileLog.Shared.Error("Creating the hotkey window failed", ex);
            ShowBalloon(Strings.Get("Hotkey_FailedTitle"), ex.Message, Forms.ToolTipIcon.Warning);
        }

        var gesture = _settings.Current.HotkeyValue;
        string? hotkeyError = TryApplyHotkey(gesture);

        _index.Start();
        _updates.Start();
        _ = EnsureStartMenuLocationsAsync(); // existing installations: once, at the next start

        if (hotkeyError is not null)
        {
            // PLAN.md 8.3: report it in the tray and open the settings so that the shortcut can be changed.
            ShowBalloon(
                Strings.Get("Hotkey_FailedTitle"),
                string.Format(
                    CultureInfo.CurrentCulture, Strings.Get("Hotkey_FailedText"),
                    gesture.Format(CultureInfo.CurrentUICulture), hotkeyError),
                Forms.ToolTipIcon.Warning);
            OpenSettings(SettingsSection.General);
        }

        // PLAN.md 8.1: started without parameters the bar appears (the first time the set-up instead);
        // --autostart starts quietly in the background.
        if (!autostart)
        {
            ShowSearch();
        }
    }

    // ---------------------------------------------------------------- storage process (PLAN.md 2.6, 11.1)

    /// <summary>
    /// <c>--storage-admin</c>: only the storage space window, normally started elevated by "Analyze as administrator…".
    /// Follows <see cref="StartupPlan.For"/>: no single-instance lock (it runs beside the normal instance), the running
    /// marker for the installer, settings read only for the language, no log, no directory, no tray icon, no hotkey, no
    /// update check, no autostart, no first-run set-up. Nothing is written to the data folder. Ends when the window closes.
    /// </summary>
    private void StartStorageOnly()
    {
        var plan = StartupPlan.For(StartupMode.StorageAdmin);
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

        // FileLog.Initialize is not called (plan.WriteLog is false): the shared log writes nothing.
        var settings = new SettingsService(AppPaths.SettingsPath, readOnly: !plan.WritableSettings);
        settings.Load();
        LanguageSetup.Apply(settings.Current.LanguageChoice);

        if (plan.HoldRunningMarker)
        {
            try
            {
                _runningMarker = new Mutex(false, SingleInstanceGuard.RunningMarkerName);
            }
            catch (Exception)
            {
                // Without the marker the installer cannot see this process; the window still works.
            }
        }

        var window = new StorageWindow(new DriveDiscovery(), index: null, StartupMode.StorageAdmin);
        _storageWindow = window;
        window.Closed += (_, _) =>
        {
            _storageWindow = null;
            Shutdown();
        };
        window.ShowInFront();
    }

    // ---------------------------------------------------------------- Start menu (PLAN.md 7.3)

    private int _startMenuCheckRunning;

    /// <summary>
    /// Once after the first-run set-up: every Start menu folder that no location contains becomes a location of its own.
    /// Afterwards <see cref="AppSettings.StartMenuChecked"/> is set, so a removed Start menu location stays removed.
    /// </summary>
    private async Task EnsureStartMenuLocationsAsync()
    {
        if (_settings is not { Current: { FirstRunCompleted: true, StartMenuChecked: false } } settings
            || _index is not { } index
            || Interlocked.Exchange(ref _startMenuCheckRunning, 1) == 1)
        {
            return;
        }

        try
        {
            await index.LoadedTask;
            await Task.Run(() =>
            {
                foreach (string path in StartMenuLocations.Missing(index.GetRoots(), System.IO.Directory.Exists))
                {
                    var candidate = new LocationCandidate(path, null, RootKind.Local);
                    if (LocationRules.Check(candidate, index.GetRoots()).Result == LocationCheckResult.Free)
                    {
                        index.AddLocation(candidate);
                    }
                }
            });
            settings.Update(s => s with { StartMenuChecked = true });
        }
        catch (Exception ex)
        {
            // Tried again at the next start.
            FileLog.Shared.Error("Adding the Start menu locations failed", ex);
        }
        finally
        {
            Interlocked.Exchange(ref _startMenuCheckRunning, 0);
        }
    }

    // ---------------------------------------------------------------- unhandled exceptions (PLAN.md 8.1)

    /// <summary>At most one balloon per this time, so a repeating error cannot flood the screen.</summary>
    private static readonly TimeSpan ErrorBalloonInterval = TimeSpan.FromSeconds(10);

    private long _lastErrorBalloonTicks = long.MinValue;

    private void OnDispatcherUnhandledException(object sender, System.Windows.Threading.DispatcherUnhandledExceptionEventArgs e)
    {
        FileLog.Shared.Error("Unhandled exception on the UI thread", e.Exception);
        ReportUnhandled(e.Exception);
        e.Handled = true; // the program keeps running
    }

    private void OnDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        var exception = e.ExceptionObject as Exception;
        FileLog.Shared.Error(
            $"Unhandled exception (the program {(e.IsTerminating ? "ends" : "goes on")})",
            exception ?? new InvalidOperationException(e.ExceptionObject?.ToString() ?? "unknown error object"));
        ReportUnhandled(exception);
    }

    private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        FileLog.Shared.Error("Unobserved exception of a background task", e.Exception);
        e.SetObserved();
        ReportUnhandled(e.Exception.GetBaseException());
    }

    /// <summary>Shows the balloon (on the UI thread, rate limited). Never throws.</summary>
    private void ReportUnhandled(Exception? exception)
    {
        try
        {
            long now = Environment.TickCount64;
            long last = Interlocked.Read(ref _lastErrorBalloonTicks);
            if (last != long.MinValue && now - last < ErrorBalloonInterval.TotalMilliseconds)
            {
                return;
            }

            Interlocked.Exchange(ref _lastErrorBalloonTicks, now);
            string reason = exception?.Message ?? string.Empty;
            if (reason.Length > 200)
            {
                reason = reason[..200] + "…";
            }

            Dispatcher.BeginInvoke(new Action(() =>
            {
                try
                {
                    ShowBalloon(
                        Strings.Get("Error_Title"),
                        string.Format(CultureInfo.CurrentCulture, Strings.Get("Error_Unhandled"), reason),
                        Forms.ToolTipIcon.Error);
                }
                catch (Exception ex)
                {
                    FileLog.Shared.Error("Showing the error balloon failed", ex);
                }
            }));
        }
        catch (Exception)
        {
            // Reporting an error must never cause another one.
        }
    }

    /// <summary>The program icon in the size the notification area asks for; the system icon if that fails.</summary>
    private static Icon LoadTrayIcon()
    {
        try
        {
            var info = GetResourceStream(new Uri("pack://application:,,,/Assets/FileDetective.ico", UriKind.Absolute));
            if (info is not null)
            {
                using var stream = info.Stream;
                return new Icon(stream, Forms.SystemInformation.SmallIconSize);
            }
        }
        catch (Exception ex)
        {
            FileLog.Shared.Error("Loading the tray icon failed", ex);
        }

        return SystemIcons.Application;
    }

    /// <summary>Registers the hotkey; returns null on success or the reason. The previous hotkey stays on failure.</summary>
    private string? TryApplyHotkey(HotkeyGesture gesture)
    {
        if (_hotkey is null)
        {
            return Strings.Get("Hotkey_NoWindow");
        }

        if (_hotkey.TryRegister(gesture, out string error))
        {
            return null;
        }

        FileLog.Shared.Error($"Registering {gesture.Format()} failed: {error}");
        return error;
    }

    /// <summary>Shows the search bar; before the first-run set-up is done, the set-up instead.</summary>
    private void ShowSearch()
    {
        if (_settings is { Current.FirstRunCompleted: false })
        {
            ShowFirstRun();
            return;
        }

        _bar?.ShowBar();
    }

    private void ToggleSearch()
    {
        if (_settings is { Current.FirstRunCompleted: false })
        {
            ShowFirstRun();
            return;
        }

        _bar?.ToggleBar();
    }

    private void ShowFirstRun()
    {
        if (_services is null)
        {
            return;
        }

        if (_firstRun is not null)
        {
            _firstRun.ShowInFront();
            return;
        }

        var window = new FirstRunWindow(_services);
        _firstRun = window;
        window.Closed += (_, _) =>
        {
            _firstRun = null;
            if (window.Completed)
            {
                _ = EnsureStartMenuLocationsAsync();
                _bar?.ShowBar();
            }
        };
        window.ShowInFront();
    }

    private void OpenSettings(SettingsSection section)
    {
        if (_services is null)
        {
            return;
        }

        if (_settingsWindow is not null)
        {
            _settingsWindow.SelectSection(section);
            _settingsWindow.ShowInFront();
            return;
        }

        var window = new SettingsWindow(_services, section);
        _settingsWindow = window;
        window.Closed += (_, _) => _settingsWindow = null;
        window.ShowInFront();
    }

    /// <summary>Opens the storage space window (PLAN.md 11.1), or brings the open one to the front.</summary>
    private void OpenStorage()
    {
        if (_services is null)
        {
            return;
        }

        if (_storageWindow is not null)
        {
            _storageWindow.ShowInFront();
            return;
        }

        var window = new StorageWindow(_services.Drives, _services.Index, StartupMode.Normal);
        _storageWindow = window;
        window.Closed += (_, _) => _storageWindow = null;
        window.ShowInFront();
    }

    /// <summary>Opens the duplicates window (PLAN.md 11.2), or brings the open one to the front.</summary>
    private void OpenDuplicates()
    {
        if (_services is null)
        {
            return;
        }

        if (_duplicatesWindow is not null)
        {
            _duplicatesWindow.ShowInFront();
            return;
        }

        var window = new DuplicatesWindow(_services);
        _duplicatesWindow = window;
        window.Closed += (_, _) => _duplicatesWindow = null;
        window.ShowInFront();
    }

    private void CreateTrayIcon()
    {
        var menu = new Forms.ContextMenuStrip();
        var search = new Forms.ToolStripMenuItem(Strings.Get("Menu_Search"));
        search.Font = new Font(search.Font, System.Drawing.FontStyle.Bold);
        search.Click += (_, _) => ShowSearch();
        var update = new Forms.ToolStripMenuItem(Strings.Get("Menu_UpdateNow"));
        update.Click += (_, _) => _index?.StartScanAll();
        var storageItem = new Forms.ToolStripMenuItem(Strings.Get("Menu_Storage"));
        storageItem.Click += (_, _) => OpenStorage();
        var duplicatesItem = new Forms.ToolStripMenuItem(Strings.Get("Menu_Duplicates"));
        duplicatesItem.Click += (_, _) => OpenDuplicates();
        var settingsItem = new Forms.ToolStripMenuItem(Strings.Get("Menu_Settings"));
        settingsItem.Click += (_, _) => OpenSettings(SettingsSection.Locations);
        _statusItem = new Forms.ToolStripMenuItem(Strings.Get("Tray_StatusLoading")) { Enabled = false };
        var exit = new Forms.ToolStripMenuItem(Strings.Get("Menu_Exit"));
        exit.Click += (_, _) => Shutdown();

        _updateItem = new Forms.ToolStripMenuItem { Visible = false };
        _updateItem.Click += async (_, _) => await OnUpdateItemClickedAsync();

        menu.Items.Add(search);
        menu.Items.Add(update);
        menu.Items.Add(storageItem);
        menu.Items.Add(duplicatesItem);
        menu.Items.Add(settingsItem);
        menu.Items.Add(_updateItem);
        menu.Items.Add(_statusItem);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(exit);
        menu.Opening += (_, _) =>
        {
            RefreshUpdateItem();
            _statusItem.Text = BuildStatusText();
            update.Enabled = _index is { IsLoaded: true };
        };

        _trayIcon = new Forms.NotifyIcon
        {
            Icon = LoadTrayIcon(),
            Text = Strings.Get("Tray_Tooltip"),
            ContextMenuStrip = menu,
            Visible = true,
        };
        _trayIcon.MouseClick += (_, args) =>
        {
            if (args.Button == Forms.MouseButtons.Left)
            {
                ShowSearch();
            }
        };
    }

    // ---------------------------------------------------------------- updates (PLAN.md 9)

    /// <summary>Shows or hides the "new version" entry of the tray menu. Hidden while there is no newer release.</summary>
    private void RefreshUpdateItem()
    {
        if (_updateItem is null || _updates is null)
        {
            return;
        }

        if (_updates.Available is not { } info)
        {
            _updateItem.Visible = false;
            return;
        }

        _updateItem.Text = LocationUi.Format(_updates.CanInstallInPlace ? "Menu_NewVersion" : "Menu_NewVersionPage", info.Version);
        _updateItem.Enabled = !_updates.Installing;
        _updateItem.Visible = true;
    }

    private async Task OnUpdateItemClickedAsync()
    {
        if (_updates?.Available is not { } info)
        {
            return;
        }

        if (!_updates.CanInstallInPlace)
        {
            OpenInBrowser(info.ReleaseUrl);
            return;
        }

        ShowBalloon(Strings.Get("Error_Title"), Strings.Get("Update_Installing"), Forms.ToolTipIcon.Info);
        if (await InstallUpdateAsync() is { } message)
        {
            ShowBalloon(Strings.Get("Error_Title"), message, Forms.ToolTipIcon.Warning);
        }
    }

    /// <summary>Installs the update; null on success (the program then restarts), otherwise the text to show.</summary>
    private async Task<string?> InstallUpdateAsync()
    {
        if (_updates is null)
        {
            return Strings.Get("Update_Failed");
        }

        var result = await _updates.InstallAsync();
        if (result.Status == UpdateInstallStatus.Installed)
        {
            RestartAfterUpdate();
            return null;
        }

        return Strings.Get(result.Status == UpdateInstallStatus.ChecksumMismatch ? "Update_ChecksumFailed" : "Update_Failed");
    }

    /// <summary>Starts the new exe (it waits for this process to end) and ends this one.</summary>
    private void RestartAfterUpdate()
    {
        try
        {
            string exe = _updates?.ExePath ?? string.Empty;
            using var started = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(exe)
            {
                Arguments = $"{RestartAfterArgument} {Environment.ProcessId}",
                UseShellExecute = false,
            });
            FileLog.Shared.Info("Restarting after the update.");
        }
        catch (Exception ex)
        {
            FileLog.Shared.Error("Restarting after the update failed", ex);
            ShowBalloon(Strings.Get("Error_Title"), ex.Message, Forms.ToolTipIcon.Warning);
            return;
        }

        Shutdown();
    }

    private static void OpenInBrowser(string url)
    {
        try
        {
            using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            FileLog.Shared.Error("Opening the release page failed", ex);
        }
    }

    private static void WaitForProcessEnd(int pid)
    {
        try
        {
            using var old = System.Diagnostics.Process.GetProcessById(pid);
            old.WaitForExit(20_000);
        }
        catch (Exception)
        {
            // The process is already gone (ArgumentException) or cannot be watched: just go on.
        }
    }

    private void ShowBalloon(string title, string text, Forms.ToolTipIcon icon)
    {
        if (_trayIcon is not null)
        {
            _trayIcon.ShowBalloonTip(8000, title, text, icon);
        }
    }

    /// <summary>"1,234,567 entries · updated today 08:12" (PLAN.md 8.2). Reads cached state only, no database access.</summary>
    private string BuildStatusText()
    {
        if (_index is null || !_index.IsLoaded)
        {
            return Strings.Get("Tray_StatusLoading");
        }

        var culture = CultureInfo.CurrentCulture;
        string stamp;
        if (_index.CurrentScan is { } scan)
        {
            // Stage 6: the running scan with its location and the entries counted so far.
            stamp = string.Format(
                culture, Strings.Get("Tray_Scanning"), PathShortener.ShortenMiddle(scan.Path, 40), scan.Entries);
        }
        else if (_index.ScanPendingOrRunning)
        {
            stamp = Strings.Get("Tray_Updating");
        }
        else if (_index.LastCompleted is { } completed)
        {
            stamp = SearchBarViewModel.UpdatedText(completed);
        }
        else
        {
            stamp = Strings.Get("Tray_NeverUpdated");
        }

        return string.Format(culture, Strings.Get("Tray_Status"), _index.EntryCount, stamp);
    }

    private void OnExit(object sender, System.Windows.ExitEventArgs e)
    {
        _updates?.Dispose();
        _updates = null;
        _guard?.Dispose();
        _guard = null;
        _runningMarker?.Dispose();
        _runningMarker = null;
        _hotkey?.Dispose();
        _hotkey = null;

        if (_trayIcon is not null)
        {
            _trayIcon.Visible = false;
            _trayIcon.Dispose();
            _trayIcon = null;
        }

        _firstRun?.Close();
        _settingsWindow?.Close();
        _storageWindow?.Close();
        _duplicatesWindow?.Close();
        _bar?.Close();
        _thumbnails?.Dispose();
        _thumbnails = null;
        _index?.Dispose();
        _index = null;
        FileLog.Shared.Info("FileDetective stopped.");
    }
}
