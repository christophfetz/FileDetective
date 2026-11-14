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

    private void OnStartup(object sender, System.Windows.StartupEventArgs e)
    {
        bool autostart = e.Args.Contains("--autostart", StringComparer.OrdinalIgnoreCase);

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

        DiagnosticLog.Initialize();
        DiagnosticLog.Info($"FileDetective starts. Data folder: {AppPaths.DataDirectory}");
        if (loadResult == SettingsLoadResult.CorruptRenamed)
        {
            DiagnosticLog.Info($"settings.json was unreadable and moved to {_settings.BackupPath}; defaults apply.");
        }

        // Nothing here touches the database or the file system of the locations: the index service does that
        // on a background thread.
        _index = new IndexService(AppPaths.DatabasePath, () => _settings.Current);
        _index.Failed += ex => Dispatcher.BeginInvoke(new Action(() =>
            ShowBalloon(Strings.Get("Error_Title"), string.Format(
                CultureInfo.CurrentCulture, Strings.Get("Error_IndexFailed"), ex.Message), Forms.ToolTipIcon.Error)));

        _services = new AppServices
        {
            Settings = _settings,
            Index = _index,
            Drives = new DriveDiscovery(),
            Autostart = new AutostartManager(),
            ExePath = Environment.ProcessPath ?? System.Reflection.Assembly.GetEntryAssembly()?.Location ?? string.Empty,
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

        var viewModel = new SearchBarViewModel(_index, Dispatcher);
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
            DiagnosticLog.Error("Creating the hotkey window failed", ex);
            ShowBalloon(Strings.Get("Hotkey_FailedTitle"), ex.Message, Forms.ToolTipIcon.Warning);
        }

        var gesture = _settings.Current.HotkeyValue;
        string? hotkeyError = TryApplyHotkey(gesture);

        _index.Start();

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

        DiagnosticLog.Error($"Registering {gesture.Format()} failed: {error}");
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

    private void CreateTrayIcon()
    {
        var menu = new Forms.ContextMenuStrip();
        var search = new Forms.ToolStripMenuItem(Strings.Get("Menu_Search"));
        search.Font = new Font(search.Font, System.Drawing.FontStyle.Bold);
        search.Click += (_, _) => ShowSearch();
        var update = new Forms.ToolStripMenuItem(Strings.Get("Menu_UpdateNow"));
        update.Click += (_, _) => _index?.StartScanAll();
        var settingsItem = new Forms.ToolStripMenuItem(Strings.Get("Menu_Settings"));
        settingsItem.Click += (_, _) => OpenSettings(SettingsSection.Locations);
        _statusItem = new Forms.ToolStripMenuItem(Strings.Get("Tray_StatusLoading")) { Enabled = false };
        var exit = new Forms.ToolStripMenuItem(Strings.Get("Menu_Exit"));
        exit.Click += (_, _) => Shutdown();

        menu.Items.Add(search);
        menu.Items.Add(update);
        menu.Items.Add(settingsItem);
        menu.Items.Add(_statusItem);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(exit);
        menu.Opening += (_, _) =>
        {
            _statusItem.Text = BuildStatusText();
            update.Enabled = _index is { IsLoaded: true };
        };

        _trayIcon = new Forms.NotifyIcon
        {
            Icon = SystemIcons.Application,
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
        if (_index.ScanPendingOrRunning)
        {
            stamp = Strings.Get("Tray_Updating");
        }
        else if (_index.LastCompleted is { } completed)
        {
            var local = completed.LocalDateTime;
            stamp = string.Format(
                culture,
                Strings.Get(local.Date == DateTime.Today ? "Tray_UpdatedToday" : "Tray_UpdatedOn"),
                local);
        }
        else
        {
            stamp = Strings.Get("Tray_NeverUpdated");
        }

        return string.Format(culture, Strings.Get("Tray_Status"), _index.EntryCount, stamp);
    }

    private void OnExit(object sender, System.Windows.ExitEventArgs e)
    {
        _guard?.Dispose();
        _guard = null;
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
        _bar?.Close();
        _index?.Dispose();
        _index = null;
        DiagnosticLog.Info("FileDetective stopped.");
    }
}
