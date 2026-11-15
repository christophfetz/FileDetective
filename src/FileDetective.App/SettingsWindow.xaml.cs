using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using FileDetective.App.Interop;
using FileDetective.App.ViewModels;
using FileDetective.Core;
using Brush = System.Windows.Media.Brush;
using MessageBox = System.Windows.MessageBox;

namespace FileDetective.App;

/// <summary>Areas of the settings window (PLAN.md 8.5).</summary>
internal enum SettingsSection
{
    Locations = 0,
    Exclusions = 1,
    General = 2,

    /// <summary>Stage 11: text in images (PLAN.md 11.3).</summary>
    ImageText = 3,
    About = 4,
}

/// <summary>
/// Settings window with the areas Locations, Exclusions, General and About (PLAN.md 8.5). Changes are saved as soon as
/// they are made; there is no OK button. "Rebuild directory" (stage 7) is in General.
/// </summary>
public partial class SettingsWindow : Window
{
    /// <summary>Link to the repository, derived from <c>UpdateChecker.Repository</c>; empty while that is the placeholder (the line is hidden then).</summary>
    internal static string RepositoryUrl => UpdateChecker.RepositoryUrl ?? string.Empty;

    private readonly AppServices _services;
    private readonly ObservableCollection<LocationItem> _items = [];
    private readonly DispatcherTimer _refreshTimer;
    private bool _refreshQueued;
    private bool _refreshing;
    private bool _loadingControls;
    private bool _busy;

    internal SettingsWindow(AppServices services, SettingsSection section)
    {
        _services = services;
        InitializeComponent();
        Title = Strings.Get("Settings_Title");
        FillTexts();
        LocationList.ItemsSource = _items;
        LoadControls();

        _refreshTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher) { Interval = TimeSpan.FromSeconds(4) };
        _refreshTimer.Tick += (_, _) =>
        {
            if (_services.Index.ScanPendingOrRunning)
            {
                QueueRefresh();
            }
        };

        Nav.SelectionChanged += (_, _) => ShowSection((SettingsSection)Math.Max(Nav.SelectedIndex, 0));
        Nav.SelectedIndex = (int)section;
        Loaded += (_, _) =>
        {
            _services.Index.StateChanged += OnIndexStateChanged;
            _services.Updates.Changed += OnUpdatesChanged;
            _services.Index.ImageTextChanged += OnImageTextChanged;
            _refreshTimer.Start();
            QueueRefresh();
            RefreshImageText();
        };
        Closed += OnClosed;

        WireLocations();
        WireExclusions();
        WireGeneral();
        WireImageText();
        WireHistoryHotkey();
    }

    /// <summary>Selects an area (also when the window is already open).</summary>
    internal void SelectSection(SettingsSection section) => Nav.SelectedIndex = (int)section;

    /// <summary>Shows the window in front, also when the program is not the active one.</summary>
    internal void ShowInFront()
    {
        if (!IsVisible)
        {
            Show();
        }

        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }

        Activate();
        WindowPositioner.ForceForeground(this);
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _refreshTimer.Stop();
        _services.Index.StateChanged -= OnIndexStateChanged;
        _services.Updates.Changed -= OnUpdatesChanged;
        _services.Index.ImageTextChanged -= OnImageTextChanged;
        CommitExclusions(); // a box that still has the focus has not been saved yet
        _services.SuspendHotkey(false);
    }

    private void FillTexts()
    {
        foreach (string key in new[] { "Section_Locations", "Section_Exclusions", "Section_General", "Section_ImageText", "Section_About" })
        {
            Nav.Items.Add(new TextBlock { Text = Strings.Get(key), TextWrapping = TextWrapping.NoWrap });
        }

        LocationsHeading.Text = Strings.Get("Section_Locations");
        HeaderPath.Text = Strings.Get("Col_Location");
        HeaderKind.Text = Strings.Get("Col_Kind");
        HeaderStatus.Text = Strings.Get("Col_Status");
        HeaderUpdated.Text = Strings.Get("Col_Updated");
        HeaderEntries.Text = Strings.Get("Col_Entries");
        BrowseButton.Content = Strings.Get("Button_Browse");
        AddButton.Content = Strings.Get("Button_Add");
        UpdateButton.Content = Strings.Get("Button_UpdateNow");
        RemoveButton.Content = Strings.Get("Button_Remove");
        LocationsHint.Text = Strings.Get("Hint_Network");

        ExclusionsHeading.Text = Strings.Get("Section_Exclusions");
        ExclusionsIntro.Text = Strings.Get("Excl_Intro");
        FoldersLabel.Text = Strings.Get("Excl_Folders");
        FoldersHint.Text = Strings.Get("Excl_FoldersHint");
        PathsLabel.Text = Strings.Get("Excl_Paths");
        PathsHint.Text = Strings.Get("Excl_PathsHint");
        FilesLabel.Text = Strings.Get("Excl_Files");
        FilesHint.Text = Strings.Get("Excl_FilesHint");
        SystemHint.Text = Strings.Get("Excl_SystemHint");
        ResetExclusionsButton.Content = Strings.Get("Button_ResetExclusions");

        GeneralHeading.Text = Strings.Get("Section_General");
        HotkeyLabel.Text = Strings.Get("Gen_Hotkey");
        HotkeyResetButton.Content = Strings.Get("Button_Reset");
        StartupLabel.Text = Strings.Get("Gen_Startup");
        AutostartBox.Content = Strings.Get("Option_Autostart");
        LanguageLabel.Text = Strings.Get("Gen_Language");
        LanguageHint.Text = Strings.Get("Gen_LanguageHint");
        LanguageBox.Items.Add(Strings.Get("Lang_System"));
        LanguageBox.Items.Add("Deutsch");
        LanguageBox.Items.Add("English");
        UpdatesLabel.Text = Strings.Get("Gen_Updates");
        UpdatesBox.Content = Strings.Get("Option_CheckUpdates");
        PolitenessLabel.Text = Strings.Get("Gen_Politeness");
        PolitenessBox.Items.Add(Strings.Get("Polite_Normal"));
        PolitenessBox.Items.Add(Strings.Get("Polite_VeryGentle"));
        PolitenessHint.Text = Strings.Get("Gen_PolitenessHint");
        RebuildLabel.Text = Strings.Get("Gen_Rebuild");
        RebuildButton.Content = Strings.Get("Button_Rebuild");
        RebuildHint.Text = Strings.Get("Gen_RebuildHint");

        OcrHeading.Text = Strings.Get("Section_ImageText");
        OcrIntro.Text = Strings.Get("Ocr_Intro");
        OcrBox.Content = Strings.Get("Option_Ocr");
        OcrLanguageText.Text = Strings.Get("Ocr_NoLanguage");
        OcrLanguageButton.Content = Strings.Get("Button_OpenLanguageSettings");
        OcrFoldersLabel.Text = Strings.Get("Ocr_Folders");
        OcrBrowseButton.Content = Strings.Get("Button_Browse");
        OcrAddButton.Content = Strings.Get("Button_Add");
        OcrRemoveButton.Content = Strings.Get("Button_Remove");
        OcrFoldersHint.Text = Strings.Get("Ocr_FoldersHint");
        HistoryHotkeyLabel.Text = Strings.Get("Hist_HotkeyLabel");
        HistoryHotkeyClearButton.Content = Strings.Get("Button_Remove");
        HistoryHotkeyHint.Text = Strings.Get("Hist_HotkeyHint");

        AboutHeading.Text = Strings.Get("Section_About");
        VersionText.Text = LocationUi.Format("About_Version", AppInfo.Version);
        LicenseText.Text = Strings.Get("About_License");
        if (RepositoryUrl.Length == 0)
        {
            RepositoryText.Visibility = Visibility.Collapsed;
        }
        else
        {
            RepositoryLink.NavigateUri = new Uri(RepositoryUrl);
            RepositoryLink.Inlines.Add(RepositoryUrl);
            RepositoryLink.RequestNavigate += (_, e) =>
                Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
        }

        RefreshUpdateArea();
        DataLabel.Text = Strings.Get("About_DataFolder");
        DataPathBox.Text = AppPaths.DataDirectory;
        OpenDataButton.Content = Strings.Get("Button_OpenFolder");
        PrivacyText.Text = Strings.Get("About_Privacy");
    }

    private void ShowSection(SettingsSection section)
    {
        LocationsPanel.Visibility = section == SettingsSection.Locations ? Visibility.Visible : Visibility.Collapsed;
        ExclusionsPanel.Visibility = section == SettingsSection.Exclusions ? Visibility.Visible : Visibility.Collapsed;
        GeneralPanel.Visibility = section == SettingsSection.General ? Visibility.Visible : Visibility.Collapsed;
        ImageTextPanel.Visibility = section == SettingsSection.ImageText ? Visibility.Visible : Visibility.Collapsed;
        AboutPanel.Visibility = section == SettingsSection.About ? Visibility.Visible : Visibility.Collapsed;
        if (section != SettingsSection.Exclusions)
        {
            CommitExclusions();
        }
    }

    private void LoadControls()
    {
        _loadingControls = true;
        var s = _services.Settings.Current;
        FoldersBox.Text = string.Join(Environment.NewLine, s.ExcludedFolderNames);
        PathsBox.Text = string.Join(Environment.NewLine, s.ExcludedPaths);
        FilesBox.Text = string.Join(Environment.NewLine, s.ExcludedFileNames);
        HotkeyBox.Show(s.HotkeyValue);
        AutostartBox.IsChecked = SafeAutostartState();
        LanguageBox.SelectedIndex = (int)s.LanguageChoice;
        UpdatesBox.IsChecked = s.CheckForUpdates;
        PolitenessBox.SelectedIndex = (int)s.NetworkPolitenessLevel;
        OcrBox.IsChecked = s.OcrEnabled;
        ShowHistoryHotkey(s.HistoryHotkeyValue);
        _loadingControls = false;
    }

    private bool SafeAutostartState()
    {
        try
        {
            return _services.Autostart.IsEnabled(); // the registry is the only truth (PLAN.md 8.1)
        }
        catch (Exception ex)
        {
            FileLog.Shared.Error("Reading the autostart entry failed", ex);
            return false;
        }
    }

    // ---------------------------------------------------------------- locations

    private void WireLocations()
    {
        BrowseButton.Click += async (_, _) =>
        {
            if (LocationUi.PickFolder(this) is { } folder)
            {
                PathBox.Text = folder;
                await AddLocationAsync(folder);
            }
        };
        AddButton.Click += async (_, _) => await AddLocationAsync(PathBox.Text);
        PathBox.KeyDown += async (_, e) =>
        {
            if (e.Key == System.Windows.Input.Key.Enter && PathBox.Text.Length > 0)
            {
                e.Handled = true;
                await AddLocationAsync(PathBox.Text);
            }
        };
        UpdateButton.Click += (_, _) =>
        {
            if (LocationList.SelectedItem is LocationItem item)
            {
                _services.Index.StartScan([item.Id]);
                ShowLocationsMessage(null, false);
            }
        };
        RemoveButton.Click += async (_, _) => await RemoveSelectedAsync();
        LocationList.SelectionChanged += (_, _) => UpdateLocationButtons();
    }

    private void UpdateLocationButtons()
    {
        bool has = LocationList.SelectedItem is LocationItem;
        UpdateButton.IsEnabled = has && !_busy;
        RemoveButton.IsEnabled = has && !_busy;
        AddButton.IsEnabled = !_busy && _services.Index.IsLoaded;
        BrowseButton.IsEnabled = !_busy && _services.Index.IsLoaded;
    }

    private void OnIndexStateChanged() => Dispatcher.BeginInvoke(new Action(QueueRefresh));

    private void QueueRefresh()
    {
        if (_refreshing)
        {
            _refreshQueued = true;
            return;
        }

        _ = RefreshLocationsAsync();
    }

    private async Task RefreshLocationsAsync()
    {
        _refreshing = true;
        try
        {
            do
            {
                _refreshQueued = false;
                var index = _services.Index;
                if (!index.IsLoaded || index.Store is not { } store)
                {
                    LocationsEmpty.Text = Strings.Get("Settings_Loading");
                    LocationsEmpty.Visibility = Visibility.Visible;
                    UpdateLocationButtons();
                    continue;
                }

                var roots = index.GetRoots();
                IReadOnlyDictionary<byte, int>? counts = null;
                try
                {
                    counts = await Task.Run(store.CountEntriesByRoot);
                }
                catch (Exception ex)
                {
                    FileLog.Shared.Error("Counting the entries failed", ex);
                }

                byte? selected = (LocationList.SelectedItem as LocationItem)?.Id;
                _items.Clear();
                foreach (var root in roots)
                {
                    int? count = counts is null ? null : counts.GetValueOrDefault(root.Id);
                    _items.Add(new LocationItem(root, index.GetScanState(root.Id), count, index.IsLocationUnreachable(root.Id)));
                }

                if (selected is { } id && _items.FirstOrDefault(i => i.Id == id) is { } again)
                {
                    LocationList.SelectedItem = again;
                }

                LocationsEmpty.Text = Strings.Get("Locations_None");
                LocationsEmpty.Visibility = _items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
                UpdateLocationButtons();
            }
            while (_refreshQueued);
        }
        finally
        {
            _refreshing = false;
        }
    }

    private async Task AddLocationAsync(string text)
    {
        if (_busy || !_services.Index.IsLoaded)
        {
            return;
        }

        var candidate = _services.Drives.Describe(text);
        if (candidate is null)
        {
            ShowLocationsMessage(Strings.Get("Msg_InvalidPath"), true);
            return;
        }

        var check = LocationRules.Check(candidate, _services.Index.GetRoots());
        switch (check.Result)
        {
            case LocationCheckResult.AlreadyExists:
                ShowLocationsMessage(Strings.Get("Msg_AlreadyInList"), true);
                return;
            case LocationCheckResult.InsideExisting:
                ShowLocationsMessage(LocationUi.Format("Msg_InsideExisting", check.Related[0].Path), true);
                return;
            case LocationCheckResult.ContainsExisting:
                string list = string.Join(Environment.NewLine, check.Related.Select(r => r.Path));
                var answer = MessageBox.Show(
                    this,
                    LocationUi.Format("Confirm_Replace", candidate.Path, list),
                    Strings.Get("Settings_Title"),
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question);
                if (answer != MessageBoxResult.Yes)
                {
                    return;
                }

                break;
        }

        SetBusy(true);
        try
        {
            ShowLocationsMessage(Strings.Get("Msg_Checking"), false);
            if (!await LocationUi.DirectoryExistsAsync(candidate.Path))
            {
                ShowLocationsMessage(LocationUi.Format("Msg_NotReachable", candidate.Path), true);
                return;
            }

            var related = check.Related;
            await Task.Run(() => _services.Index.AddLocation(candidate, related));
            PathBox.Clear();
            ShowLocationsMessage(LocationUi.Format("Msg_Added", candidate.Path), false);
        }
        catch (Exception ex)
        {
            FileLog.Shared.Error("Adding a location failed", ex);
            ShowLocationsMessage(LocationUi.Format("Msg_AddFailed", ex.Message), true);
        }
        finally
        {
            SetBusy(false);
            QueueRefresh();
        }
    }

    private async Task RemoveSelectedAsync()
    {
        if (_busy || LocationList.SelectedItem is not LocationItem item)
        {
            return;
        }

        var answer = MessageBox.Show(
            this,
            LocationUi.Format("Confirm_Remove", item.Path),
            Strings.Get("Settings_Title"),
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);
        if (answer != MessageBoxResult.Yes)
        {
            return;
        }

        SetBusy(true);
        try
        {
            ShowLocationsMessage(Strings.Get("Msg_Removing"), false);
            byte id = item.Id;
            await Task.Run(() => _services.Index.RemoveLocation(id));
            ShowLocationsMessage(LocationUi.Format("Msg_Removed", item.Path), false);
        }
        catch (Exception ex)
        {
            FileLog.Shared.Error("Removing a location failed", ex);
            ShowLocationsMessage(LocationUi.Format("Msg_RemoveFailed", ex.Message), true);
        }
        finally
        {
            SetBusy(false);
            QueueRefresh();
        }
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        PathBox.IsEnabled = !busy;
        UpdateLocationButtons();
        Cursor = busy ? System.Windows.Input.Cursors.Wait : null;
    }

    private void ShowLocationsMessage(string? text, bool warning)
    {
        LocationsMessage.Text = text ?? string.Empty;
        LocationsMessage.Foreground = warning
            ? (Brush)FindResource("WarnBrush")
            : (Brush)FindResource("MutedBrush");
        LocationsMessage.Visibility = text is null ? Visibility.Collapsed : Visibility.Visible;
    }

    // ---------------------------------------------------------------- exclusions

    private void WireExclusions()
    {
        FoldersBox.LostFocus += (_, _) => CommitExclusions();
        PathsBox.LostFocus += (_, _) => CommitExclusions();
        FilesBox.LostFocus += (_, _) => CommitExclusions();
        ResetExclusionsButton.Click += (_, _) =>
        {
            try
            {
                _services.Settings.Update(s => s.WithDefaultExclusions());
            }
            catch (Exception ex)
            {
                FileLog.Shared.Error("Saving the settings failed", ex);
            }

            LoadControls();
        };
    }

    private static List<string> Lines(string text)
        => [.. text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];

    /// <summary>Saves the three lists if they differ from the saved ones. Takes effect at the next scan.</summary>
    private void CommitExclusions()
    {
        if (_loadingControls)
        {
            return;
        }

        var folders = Lines(FoldersBox.Text);
        var paths = Lines(PathsBox.Text);
        var files = Lines(FilesBox.Text);
        var current = _services.Settings.Current;
        if (folders.SequenceEqual(current.ExcludedFolderNames) && paths.SequenceEqual(current.ExcludedPaths)
            && files.SequenceEqual(current.ExcludedFileNames))
        {
            return;
        }

        try
        {
            _services.Settings.Update(s => s with
            {
                ExcludedFolderNames = folders,
                ExcludedPaths = paths,
                ExcludedFileNames = files,
            });
        }
        catch (Exception ex)
        {
            FileLog.Shared.Error("Saving the settings failed", ex);
        }
    }

    // ---------------------------------------------------------------- general

    private void WireGeneral()
    {
        HotkeyBox.GotKeyboardFocus += (_, _) =>
        {
            _services.SuspendHotkey(true);
            ShowHotkeyMessage(Strings.Get("Hotkey_Press"), false);
        };
        HotkeyBox.LostKeyboardFocus += (_, _) =>
        {
            _services.SuspendHotkey(false);
            HotkeyMessage.Text = string.Empty;
        };
        HotkeyBox.Captured += ChangeHotkey;
        HotkeyBox.Rejected += () => ShowHotkeyMessage(Strings.Get("Hotkey_Invalid"), true);
        HotkeyResetButton.Click += (_, _) => ChangeHotkey(HotkeyGesture.Default);

        AutostartBox.Click += (_, _) => ChangeAutostart(AutostartBox.IsChecked == true);

        LanguageBox.SelectionChanged += (_, _) =>
        {
            if (!_loadingControls && LanguageBox.SelectedIndex >= 0)
            {
                string code = AppSettings.LanguageCode((AppLanguage)LanguageBox.SelectedIndex);
                SaveSettings(s => s with { Language = code });
            }
        };
        UpdatesBox.Click += (_, _) =>
        {
            bool on = UpdatesBox.IsChecked == true;
            SaveSettings(s => s with { CheckForUpdates = on });
            RefreshUpdateArea();
        };
        UpdateActionButton.Click += (_, _) => OnUpdateActionClicked();
        PolitenessBox.SelectionChanged += (_, _) =>
        {
            if (!_loadingControls && PolitenessBox.SelectedIndex >= 0)
            {
                string code = AppSettings.PolitenessCode((NetworkPoliteness)PolitenessBox.SelectedIndex);
                SaveSettings(s => s with { Politeness = code });
            }
        };

        RebuildButton.Click += async (_, _) => await RebuildAsync();

        OpenDataButton.Click += (_, _) =>
        {
            try
            {
                Process.Start(new ProcessStartInfo(AppPaths.DataDirectory) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                FileLog.Shared.Error("Opening the data folder failed", ex);
            }
        };
    }

    /// <summary>"Rebuild directory" (PLAN.md 8.5): asks first, then runs in the background and shows the outcome.</summary>
    private async Task RebuildAsync()
    {
        if (_busy || !_services.Index.IsLoaded)
        {
            return;
        }

        var answer = MessageBox.Show(
            this, Strings.Get("Confirm_Rebuild"), Strings.Get("Settings_Title"),
            MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
        if (answer != MessageBoxResult.Yes)
        {
            return;
        }

        SetBusy(true);
        RebuildButton.IsEnabled = false;
        ShowRebuildMessage(Strings.Get("Msg_Rebuilding"), false);
        try
        {
            int removed = await Task.Run(_services.Index.RebuildDirectory);
            ShowRebuildMessage(LocationUi.Format("Msg_Rebuilt", removed), false);
        }
        catch (Exception ex)
        {
            FileLog.Shared.Error("Rebuilding the directory failed", ex);
            ShowRebuildMessage(LocationUi.Format("Msg_RebuildFailed", ex.Message), true);
        }
        finally
        {
            RebuildButton.IsEnabled = true;
            SetBusy(false);
            QueueRefresh();
        }
    }

    private void OnUpdatesChanged() => Dispatcher.BeginInvoke(new Action(RefreshUpdateArea));

    /// <summary>Shows the hint about a newer version with the button to install it or to open the download page.</summary>
    private void RefreshUpdateArea()
    {
        var updates = _services.Updates;
        if (updates.Available is not { } info)
        {
            UpdateAvailableText.Visibility = Visibility.Collapsed;
            UpdateActionButton.Visibility = Visibility.Collapsed;
            return;
        }

        UpdateAvailableText.Text = LocationUi.Format("Update_Available", info.Version);
        UpdateAvailableText.Visibility = Visibility.Visible;
        bool inPlace = updates.CanInstallInPlace;
        UpdateActionButton.Content = Strings.Get(inPlace ? "Update_Install" : "Update_OpenPage");
        UpdateActionButton.IsEnabled = !updates.Installing;
        UpdateActionButton.Visibility = Visibility.Visible;
        if (!inPlace && UpdateMessage.Visibility != Visibility.Visible)
        {
            ShowUpdateMessage(Strings.Get("Update_ManualHint"), false);
        }
    }

    private async void OnUpdateActionClicked()
    {
        if (_services.Updates.Available is not { } info)
        {
            return;
        }

        if (!_services.Updates.CanInstallInPlace)
        {
            Process.Start(new ProcessStartInfo(info.ReleaseUrl) { UseShellExecute = true });
            return;
        }

        ShowUpdateMessage(Strings.Get("Update_Installing"), false);
        if (await _services.InstallUpdate() is { } message)
        {
            ShowUpdateMessage(message, true);
        }
    }

    private void ShowUpdateMessage(string text, bool warning)
    {
        UpdateMessage.Text = text;
        UpdateMessage.Foreground = warning ? (Brush)FindResource("WarnBrush") : (Brush)FindResource("MutedBrush");
        UpdateMessage.Visibility = Visibility.Visible;
    }

    private void ShowRebuildMessage(string text, bool warning)
    {
        RebuildMessage.Text = text;
        RebuildMessage.Foreground = warning ? (Brush)FindResource("WarnBrush") : (Brush)FindResource("MutedBrush");
        RebuildMessage.Visibility = Visibility.Visible;
    }

    private void SaveSettings(Func<AppSettings, AppSettings> change)
    {
        try
        {
            _services.Settings.Update(change);
        }
        catch (Exception ex)
        {
            FileLog.Shared.Error("Saving the settings failed", ex);
            MessageBox.Show(
                this, LocationUi.Format("Msg_SaveFailed", ex.Message), Strings.Get("Settings_Title"),
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void ChangeHotkey(HotkeyGesture gesture)
    {
        string text = gesture.Format(CultureInfo.CurrentUICulture);
        if (HotkeyRules.Conflicts(gesture, _services.Settings.Current.HistoryHotkeyValue))
        {
            // The shortcut of the screenshot history: both would fight for the same keys.
            HotkeyBox.Show(_services.Settings.Current.HotkeyValue);
            ShowHotkeyMessage(LocationUi.Format("Hist_HotkeyConflictSearch", text), true);
            return;
        }

        string? error = _services.TryApplyHotkey(gesture);
        if (error is not null)
        {
            // The old shortcut stays in force.
            HotkeyBox.Show(_services.Settings.Current.HotkeyValue);
            ShowHotkeyMessage(LocationUi.Format("Hotkey_Refused", text, error), true);
            return;
        }

        SaveSettings(s => s with { Hotkey = gesture.Format() });
        HotkeyBox.Show(gesture);
        ShowHotkeyMessage(LocationUi.Format("Hotkey_Changed", text), false);
    }

    private void ShowHotkeyMessage(string text, bool warning)
    {
        HotkeyMessage.Text = text;
        HotkeyMessage.Foreground = warning ? (Brush)FindResource("WarnBrush") : (Brush)FindResource("MutedBrush");
    }

    // ---------------------------------------------------------------- shortcut of the screenshot history (stage 12)

    private void WireHistoryHotkey()
    {
        HistoryHotkeyBox.GotKeyboardFocus += (_, _) =>
        {
            _services.SuspendHotkey(true);
            ShowHistoryHotkeyMessage(Strings.Get("Hotkey_Press"), false);
        };
        HistoryHotkeyBox.LostKeyboardFocus += (_, _) =>
        {
            _services.SuspendHotkey(false);
            HistoryHotkeyMessage.Text = string.Empty;
        };
        HistoryHotkeyBox.Captured += gesture => ChangeHistoryHotkey(gesture);
        HistoryHotkeyBox.Rejected += () => ShowHistoryHotkeyMessage(Strings.Get("Hotkey_Invalid"), true);
        HistoryHotkeyClearButton.Click += (_, _) => ChangeHistoryHotkey(null);
    }

    private void ShowHistoryHotkey(HotkeyGesture? gesture)
    {
        if (gesture is { } set)
        {
            HistoryHotkeyBox.Show(set);
        }
        else
        {
            HistoryHotkeyBox.Text = Strings.Get("Hist_HotkeyNone");
        }

        HistoryHotkeyClearButton.IsEnabled = gesture is not null;
    }

    /// <summary>A new shortcut for the history, or none (null). The one of the search bar is refused; the old one stays then.</summary>
    private void ChangeHistoryHotkey(HotkeyGesture? gesture)
    {
        var current = _services.Settings.Current;
        if (gesture is { } wanted)
        {
            string text = wanted.Format(CultureInfo.CurrentUICulture);
            if (HotkeyRules.Conflicts(wanted, current.HotkeyValue))
            {
                ShowHistoryHotkey(current.HistoryHotkeyValue);
                ShowHistoryHotkeyMessage(LocationUi.Format("Hist_HotkeyConflict", text), true);
                return;
            }

            if (_services.TryApplyHistoryHotkey(wanted) is { } error)
            {
                ShowHistoryHotkey(current.HistoryHotkeyValue);
                ShowHistoryHotkeyMessage(LocationUi.Format("Hotkey_Refused", text, error), true);
                return;
            }

            SaveSettings(s => s with { HistoryHotkey = wanted.Format() });
            ShowHistoryHotkey(wanted);
            ShowHistoryHotkeyMessage(LocationUi.Format("Hist_HotkeyChanged", text), false);
            return;
        }

        _services.TryApplyHistoryHotkey(null);
        SaveSettings(s => s with { HistoryHotkey = string.Empty });
        ShowHistoryHotkey(null);
        ShowHistoryHotkeyMessage(Strings.Get("Hist_HotkeyRemoved"), false);
    }

    private void ShowHistoryHotkeyMessage(string text, bool warning)
    {
        HistoryHotkeyMessage.Text = text;
        HistoryHotkeyMessage.Foreground = warning ? (Brush)FindResource("WarnBrush") : (Brush)FindResource("MutedBrush");
    }

    // ---------------------------------------------------------------- image text (stage 11, PLAN.md 11.3)

    private void WireImageText()
    {
        OcrBox.Click += (_, _) => ChangeImageText(OcrBox.IsChecked == true);
        OcrBrowseButton.Click += async (_, _) =>
        {
            if (LocationUi.PickFolder(this) is { } folder)
            {
                OcrPathBox.Text = folder;
                await AddImageTextFolderAsync(folder);
            }
        };
        OcrAddButton.Click += async (_, _) => await AddImageTextFolderAsync(OcrPathBox.Text);
        OcrPathBox.KeyDown += async (_, e) =>
        {
            if (e.Key == System.Windows.Input.Key.Enter && OcrPathBox.Text.Length > 0)
            {
                e.Handled = true;
                await AddImageTextFolderAsync(OcrPathBox.Text);
            }
        };
        OcrRemoveButton.Click += (_, _) => RemoveImageTextFolder();
        OcrFolderList.SelectionChanged += (_, _) => OcrRemoveButton.IsEnabled = OcrFolderList.SelectedItem is string;
        OcrLanguageButton.Click += (_, _) =>
        {
            try
            {
                Process.Start(new ProcessStartInfo("ms-settings:regionlanguage") { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                FileLog.Shared.Error("Opening the language settings failed", ex);
            }
        };
    }

    private void OnImageTextChanged() => Dispatcher.BeginInvoke(new Action(RefreshImageText));

    private IReadOnlyList<string> ImageTextFolderList()
        => ImageTextFolders.Effective(_services.Settings.Current, KnownFolders.Screenshots);

    /// <summary>Switch, folder list, progress and the hint about a missing recognition language.</summary>
    private void RefreshImageText()
    {
        var settings = _services.Settings.Current;
        OcrBox.IsChecked = settings.OcrEnabled;
        string? selected = OcrFolderList.SelectedItem as string;
        OcrFolderList.ItemsSource = ImageTextFolderList();
        if (selected is not null)
        {
            OcrFolderList.SelectedItem = ImageTextFolderList().FirstOrDefault(f => string.Equals(f, selected, StringComparison.OrdinalIgnoreCase));
        }

        OcrRemoveButton.IsEnabled = OcrFolderList.SelectedItem is string;
        bool available = _services.Index.ImageText?.RecognizerAvailable ?? WindowsImageTextRecognizer.AnyLanguageAvailable();
        OcrLanguagePanel.Visibility = available ? Visibility.Collapsed : Visibility.Visible;

        if (!settings.OcrEnabled)
        {
            OcrProgressText.Text = Strings.Get("Ocr_Off");
        }
        else if (_services.Index.ImageText is { } service && service.Enabled)
        {
            var progress = service.Progress;
            OcrProgressText.Text = LocationUi.Format("Ocr_Progress", progress.Recognized, progress.Total);
        }
        else
        {
            OcrProgressText.Text = Strings.Get("Ocr_Starting");
        }
    }

    private void ChangeImageText(bool enable)
    {
        if (!enable)
        {
            var answer = MessageBox.Show(
                this, Strings.Get("Confirm_OcrOff"), Strings.Get("Settings_Title"),
                MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
            if (answer != MessageBoxResult.Yes)
            {
                OcrBox.IsChecked = true;
                return;
            }
        }

        SaveSettings(s => s with
        {
            OcrEnabled = enable,
            OcrFolders = s.OcrFolders ?? ImageTextFolders.Effective(s, KnownFolders.Screenshots),
            OcrHintDismissed = true,
        });
        ShowImageTextMessage(Strings.Get(enable ? "Ocr_MsgOn" : "Ocr_MsgOff"), false);
        RefreshImageText();
    }

    private async Task AddImageTextFolderAsync(string text)
    {
        var current = ImageTextFolderList();
        switch (ImageTextFolders.Check(text, _services.Drives, current, out string? path))
        {
            case ImageFolderCheck.Invalid:
                ShowImageTextMessage(Strings.Get("Msg_InvalidPath"), true);
                return;
            case ImageFolderCheck.NotLocal:
                ShowImageTextMessage(Strings.Get("Ocr_MsgNotLocal"), true);
                return;
            case ImageFolderCheck.AlreadyInList:
                ShowImageTextMessage(Strings.Get("Msg_AlreadyInList"), true);
                return;
        }

        if (path is null || !await LocationUi.DirectoryExistsAsync(path))
        {
            ShowImageTextMessage(LocationUi.Format("Msg_NotReachable", path ?? text), true);
            return;
        }

        SaveSettings(s => s with { OcrFolders = [.. ImageTextFolders.Effective(s, KnownFolders.Screenshots), path] });
        OcrPathBox.Clear();
        ShowImageTextMessage(LocationUi.Format(
            _services.Settings.Current.OcrEnabled ? "Ocr_MsgFolderAdded" : "Ocr_MsgFolderAddedOff", path), false);
        RefreshImageText();
        QueueRefresh();
    }

    private void RemoveImageTextFolder()
    {
        if (OcrFolderList.SelectedItem is not string folder)
        {
            return;
        }

        // The texts of its images are deleted at once (PLAN.md 11.3); the location itself stays.
        SaveSettings(s => s with
        {
            OcrFolders = [.. ImageTextFolders.Effective(s, KnownFolders.Screenshots)
                .Where(f => !string.Equals(f, folder, StringComparison.OrdinalIgnoreCase))],
        });
        ShowImageTextMessage(LocationUi.Format("Ocr_MsgFolderRemoved", folder), false);
        RefreshImageText();
    }

    private void ShowImageTextMessage(string text, bool warning)
    {
        OcrMessage.Text = text;
        OcrMessage.Foreground = warning ? (Brush)FindResource("WarnBrush") : (Brush)FindResource("MutedBrush");
        OcrMessage.Visibility = Visibility.Visible;
    }

    private void ChangeAutostart(bool enable)
    {
        AutostartMessage.Visibility = Visibility.Collapsed;
        try
        {
            if (enable)
            {
                _services.Autostart.Enable(_services.ExePath);
            }
            else
            {
                _services.Autostart.Disable();
            }
        }
        catch (Exception ex)
        {
            FileLog.Shared.Error("Changing the autostart failed", ex);
            AutostartMessage.Text = LocationUi.Format("Msg_AutostartFailed", ex.Message);
            AutostartMessage.Visibility = Visibility.Visible;
        }

        AutostartBox.IsChecked = SafeAutostartState(); // show what the registry says
    }
}
