using System.Drawing;
using System.Globalization;
using FileDetective.App.Interop;
using FileDetective.App.ViewModels;
using Forms = System.Windows.Forms;

namespace FileDetective.App;

public partial class App : System.Windows.Application
{
    private Forms.NotifyIcon? _trayIcon;
    private Forms.ToolStripMenuItem? _statusItem;
    private IndexService? _index;
    private HotkeyManager? _hotkey;
    private SearchBarWindow? _bar;

    private void OnStartup(object sender, System.Windows.StartupEventArgs e)
    {
        DiagnosticLog.Initialize();
        DiagnosticLog.Info($"FileDetective starts. Data folder: {AppPaths.DataDirectory}");

        // Nothing here touches the database or the file system of the locations: the index service does that
        // on a background thread.
        _index = new IndexService(AppPaths.DatabasePath);
        _index.Failed += ex => Dispatcher.BeginInvoke(new Action(() =>
            ShowBalloon(Strings.Get("Error_Title"), string.Format(
                CultureInfo.CurrentCulture, Strings.Get("Error_IndexFailed"), ex.Message), Forms.ToolTipIcon.Error)));

        var viewModel = new SearchBarViewModel(_index, Dispatcher);
        _bar = new SearchBarWindow(viewModel);

        CreateTrayIcon();
        RegisterHotkey();

        _index.Start();

        // PLAN.md 8.1: started without parameters the bar appears; --autostart starts quietly in the background.
        if (!e.Args.Contains("--autostart", StringComparer.OrdinalIgnoreCase))
        {
            _bar.ShowBar();
        }
    }

    private void CreateTrayIcon()
    {
        var menu = new Forms.ContextMenuStrip();
        var search = new Forms.ToolStripMenuItem(Strings.Get("Menu_Search"));
        search.Font = new Font(search.Font, System.Drawing.FontStyle.Bold);
        search.Click += (_, _) => _bar?.ShowBar();
        _statusItem = new Forms.ToolStripMenuItem(Strings.Get("Tray_StatusLoading")) { Enabled = false };
        var exit = new Forms.ToolStripMenuItem(Strings.Get("Menu_Exit"));
        exit.Click += (_, _) => Shutdown();

        menu.Items.Add(search);
        menu.Items.Add(_statusItem);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(exit);
        menu.Opening += (_, _) => _statusItem.Text = BuildStatusText();

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
                _bar?.ShowBar();
            }
        };
    }

    private void RegisterHotkey()
    {
        try
        {
            _hotkey = new HotkeyManager();
            _hotkey.Pressed += () => _bar?.ToggleBar();
            if (!_hotkey.TryRegisterAltSpace(out string error))
            {
                DiagnosticLog.Error($"Registering Alt+Space failed: {error}");
                ShowBalloon(
                    Strings.Get("Hotkey_FailedTitle"),
                    string.Format(CultureInfo.CurrentCulture, Strings.Get("Hotkey_FailedText"), error),
                    Forms.ToolTipIcon.Warning);
            }
        }
        catch (Exception ex)
        {
            DiagnosticLog.Error("Creating the hotkey window failed", ex);
            ShowBalloon(Strings.Get("Hotkey_FailedTitle"), ex.Message, Forms.ToolTipIcon.Warning);
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
        _hotkey?.Dispose();
        _hotkey = null;

        if (_trayIcon is not null)
        {
            _trayIcon.Visible = false;
            _trayIcon.Dispose();
            _trayIcon = null;
        }

        _bar?.Close();
        _index?.Dispose();
        _index = null;
        DiagnosticLog.Info("FileDetective stopped.");
    }
}
