using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Windows;
using FileDetective.App.Interop;
using FileDetective.App.ViewModels;
using FileDetective.Core;

namespace FileDetective.App;

/// <summary>
/// First-run set-up (PLAN.md 8.5): check list of places (user folder preselected, every local drive, every connected
/// network drive with letter and UNC path), a path box with folder picker, autostart and the hotkey. Nested places are
/// not allowed: of two nested checked places only the outer one is added.
/// </summary>
public partial class FirstRunWindow : Window
{
    private readonly AppServices _services;
    private readonly ObservableCollection<LocationRow> _rows = [];
    private bool _busy;
    private bool _ready;

    internal FirstRunWindow(AppServices services)
    {
        _services = services;
        InitializeComponent();
        Title = Strings.Get("FirstRun_Title");
        TitleText.Text = Strings.Get("FirstRun_Heading");
        IntroText.Text = Strings.Get("FirstRun_Intro");
        LoadingText.Text = Strings.Get("FirstRun_Loading");
        BrowseButton.Content = Strings.Get("Button_Browse");
        AddButton.Content = Strings.Get("Button_Add");
        NetworkHintText.Text = Strings.Get("Hint_Network");
        AutostartBox.Content = Strings.Get("Option_Autostart");
        CancelButton.Content = Strings.Get("Button_Cancel");
        StartButton.Content = Strings.Get("FirstRun_Start");
        HotkeyText.Text = LocationUi.Format(
            "FirstRun_Hotkey", services.Settings.Current.HotkeyValue.Format(CultureInfo.CurrentUICulture));
        RowList.ItemsSource = _rows;
        StartButton.IsEnabled = false;

        Loaded += async (_, _) => await LoadRowsAsync();
        BrowseButton.Click += OnBrowse;
        AddButton.Click += async (_, _) => await AddTypedPathAsync(PathBox.Text);
        PathBox.KeyDown += async (_, e) =>
        {
            if (e.Key == System.Windows.Input.Key.Enter && PathBox.Text.Length > 0)
            {
                e.Handled = true;
                await AddTypedPathAsync(PathBox.Text);
            }
        };
        StartButton.Click += async (_, _) => await StartAsync();
        CancelButton.Click += (_, _) => Close();
    }

    /// <summary>True if the user finished the set-up.</summary>
    public bool Completed { get; private set; }

    protected override void OnClosing(CancelEventArgs e)
    {
        e.Cancel = _busy; // not while the locations are being saved
        base.OnClosing(e);
    }

    private async Task LoadRowsAsync()
    {
        try
        {
            await _services.Index.LoadedTask;
        }
        catch (Exception)
        {
            LoadingText.Text = Strings.Get("FirstRun_LoadFailed");
            return;
        }

        var existing = _services.Index.GetRoots();
        foreach (var root in existing)
        {
            var candidate = LocationCandidate.FromRoot(root);
            _rows.Add(new LocationRow(candidate, root.Path, DetailOf(candidate), root) { IsChecked = true });
        }

        // The user folder, preselected (PLAN.md 3).
        string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (_services.Drives.Describe(profile) is { } profileCandidate && !HasSamePlace(profileCandidate))
        {
            _rows.Add(new LocationRow(profileCandidate, Strings.Get("Row_UserFolder"), profileCandidate.Path) { IsChecked = true });
        }

        var drives = await LocationUi.DiscoverAsync(_services.Drives);
        foreach (var kind in new[] { RootKind.Local, RootKind.Removable, RootKind.Network })
        {
            foreach (var drive in drives.Where(d => d.Kind == kind))
            {
                var candidate = drive.ToCandidate();
                if (HasSamePlace(candidate))
                {
                    continue;
                }

                string title = kind == RootKind.Network
                    ? $"{drive.Letter}\\  {drive.UncPath}".TrimEnd()
                    : drive.Label is null ? drive.Path : $"{drive.Path}  ({drive.Label})";
                _rows.Add(new LocationRow(candidate, title, LocationUi.KindText(kind)));
            }
        }

        foreach (var row in _rows)
        {
            row.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(LocationRow.IsChecked))
                {
                    LocationRow.Recompute(_rows);
                }
            };
        }

        LoadingText.Visibility = Visibility.Collapsed;
        LocationRow.Recompute(_rows);
        _ready = true;
        StartButton.IsEnabled = true;
    }

    private bool HasSamePlace(LocationCandidate candidate)
        => _rows.Any(r => LocationRules.Compare(r.Candidate, candidate) == LocationRelation.Same);

    private static string DetailOf(LocationCandidate candidate)
        => candidate.UncPath is null
            ? LocationUi.KindText(candidate.Kind)
            : $"{candidate.UncPath} · {LocationUi.KindText(candidate.Kind)}";

    private void OnBrowse(object sender, RoutedEventArgs e)
    {
        if (LocationUi.PickFolder(this) is { } folder)
        {
            PathBox.Text = folder;
            _ = AddTypedPathAsync(folder);
        }
    }

    private async Task AddTypedPathAsync(string text)
    {
        if (!_ready || _busy)
        {
            return;
        }

        ShowMessage(null);
        var candidate = _services.Drives.Describe(text);
        if (candidate is null)
        {
            ShowMessage(Strings.Get("Msg_InvalidPath"));
            return;
        }

        if (HasSamePlace(candidate))
        {
            ShowMessage(Strings.Get("Msg_AlreadyInList"));
            return;
        }

        SetBusy(true);
        bool exists = await LocationUi.DirectoryExistsAsync(candidate.Path);
        SetBusy(false);
        if (!exists)
        {
            ShowMessage(LocationUi.Format("Msg_NotReachable", candidate.Path));
            return;
        }

        var row = new LocationRow(candidate, candidate.Path, DetailOf(candidate)) { IsChecked = true };
        row.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(LocationRow.IsChecked))
            {
                LocationRow.Recompute(_rows);
            }
        };
        _rows.Add(row);
        LocationRow.Recompute(_rows);
        PathBox.Clear();
        RowList.ScrollIntoView(row);
    }

    private async Task StartAsync()
    {
        if (!_ready || _busy)
        {
            return;
        }

        // Only rows that are checked, new and not swallowed by an outer location are added.
        var chosen = _rows.Where(r => r is { IsChecked: true, IsExisting: false, IsShadowed: false })
            .Select(r => r.Candidate).ToList();
        bool autostart = AutostartBox.IsChecked == true;
        SetBusy(true);
        try
        {
            await Task.Run(() =>
            {
                foreach (var candidate in chosen)
                {
                    var check = LocationRules.Check(candidate, _services.Index.GetRoots());
                    if (check.Result is LocationCheckResult.Free or LocationCheckResult.ContainsExisting)
                    {
                        _services.Index.AddLocation(candidate, check.Related);
                    }
                }
            });

            ApplyAutostart(autostart);
            _services.Settings.Update(s => s with { FirstRunCompleted = true });
            Completed = true;
            _busy = false;
            Close();
        }
        catch (Exception ex)
        {
            DiagnosticLog.Error("First-run set-up failed", ex);
            SetBusy(false);
            ShowMessage(LocationUi.Format("Msg_SetupFailed", ex.Message));
        }
    }

    private void ApplyAutostart(bool enable)
    {
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
            // Not fatal: it can be switched in the settings later.
            DiagnosticLog.Error("Setting the autostart failed", ex);
        }
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        StartButton.IsEnabled = !busy && _ready;
        AddButton.IsEnabled = !busy;
        BrowseButton.IsEnabled = !busy;
        PathBox.IsEnabled = !busy;
        RowList.IsEnabled = !busy;
        Cursor = busy ? System.Windows.Input.Cursors.Wait : null;
    }

    private void ShowMessage(string? text)
    {
        MessageText.Text = text ?? string.Empty;
        MessageText.Visibility = text is null ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>Shows the window in front, also when the program is not the active one.</summary>
    internal void ShowInFront()
    {
        Show();
        Activate();
        WindowPositioner.ForceForeground(this);
    }
}
