using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using FileDetective.App.Interop;
using FileDetective.App.ViewModels;
using FileDetective.Core;
using Brush = System.Windows.Media.Brush;
using MessageBox = System.Windows.MessageBox;

namespace FileDetective.App;

/// <summary>
/// Duplicates window (PLAN.md 11.2). Local files of the same size are compared by content on a low-priority thread;
/// shares only get "possible duplicates, not checked" from the directory and can only be shown in Explorer. Marked local
/// files go to the Recycle Bin after an explicit yes (default No), each checked again for size and date right before.
/// </summary>
public partial class DuplicatesWindow : Window
{
    private static readonly TimeSpan ProgressInterval = TimeSpan.FromMilliseconds(300);
    private static readonly long[] MinSizeChoices =
        [64 * 1024, 256 * 1024, 1024 * 1024, 10 * 1024 * 1024, 100 * 1024 * 1024, 1024L * 1024 * 1024];

    private readonly AppServices _services;
    private readonly ObservableCollection<DuplicateRow> _localRows = [];
    private readonly ObservableCollection<DuplicateRow> _shareRows = [];
    private readonly CancellationTokenSource _closing = new();
    private readonly DispatcherTimer _progressTimer;
    private readonly object _progressGate = new();
    private IReadOnlyList<DuplicateGroup> _localGroups = [];
    private IReadOnlyList<DuplicateGroup> _shareGroups = [];
    private DuplicateProgress? _progress;
    private CancellationTokenSource? _searchCts;
    private bool _searched;
    private bool _recycling;

    internal DuplicatesWindow(AppServices services)
    {
        _services = services;
        InitializeComponent();
        Title = Strings.Get("Dup_Title");
        FillTexts();
        FillMinSizes();
        LocalList.ItemsSource = _localRows;
        ShareList.ItemsSource = _shareRows;

        _progressTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher) { Interval = ProgressInterval };
        _progressTimer.Tick += (_, _) => ShowProgress();

        SearchButton.Click += async (_, _) => await SearchAsync();
        CancelButton.Click += (_, _) => _searchCts?.Cancel();
        MinSizeBox.SelectionChanged += (_, _) => OnMinSizeChanged();
        RevealButton.Click += (_, _) => Reveal();
        CopyButton.Click += (_, _) => CopyPath();
        LocalRevealItem.Click += (_, _) => Reveal();
        LocalCopyItem.Click += (_, _) => CopyPath();
        ShareRevealItem.Click += (_, _) => Reveal();
        ShareCopyItem.Click += (_, _) => CopyPath();
        NewestButton.Click += (_, _) => MarkAllExceptNewest();
        FolderButton.Click += (_, _) => MarkAllExceptFolder();
        LocalFolderItem.Click += (_, _) => MarkAllExceptFolder();
        ClearButton.Click += (_, _) => ClearMarks();
        RecycleButton.Click += async (_, _) => await RecycleAsync();
        LocalList.SelectionChanged += (_, _) => UpdateButtons();
        ShareList.SelectionChanged += (_, _) => UpdateButtons();
        LocalMenu.Opened += (_, _) => UpdateButtons();
        ShareMenu.Opened += (_, _) => UpdateButtons();
        Tabs.SelectionChanged += (_, e) =>
        {
            if (ReferenceEquals(e.OriginalSource, Tabs))
            {
                UpdateButtons();
            }
        };

        Closing += (_, _) =>
        {
            _closing.Cancel();
            _searchCts?.Cancel();
        };
        Closed += (_, _) =>
        {
            _progressTimer.Stop();
            _localRows.Clear();
            _shareRows.Clear();
        };

        ShowEmptyTexts();
        UpdateButtons();
    }

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

    private void FillTexts()
    {
        Heading.Text = Strings.Get("Dup_Title");
        IntroText.Text = Strings.Get("Dup_Intro");
        MinSizeLabel.Text = Strings.Get("Dup_MinSize");
        SearchButton.Content = Strings.Get("Dup_Search");
        CancelButton.Content = Strings.Get("Button_Cancel");
        LocalTab.Header = Strings.Get("Dup_TabLocal");
        SharesTab.Header = Strings.Get("Dup_TabShares");
        LocalHint.Text = Strings.Get("Dup_LocalHint");
        SharesHint.Text = Strings.Get("Dup_SharesHint");
        RevealButton.Content = Strings.Get("Ctx_Reveal");
        CopyButton.Content = Strings.Get("Ctx_CopyPath");
        LocalRevealItem.Header = Strings.Get("Ctx_Reveal");
        LocalCopyItem.Header = Strings.Get("Ctx_CopyPath");
        ShareRevealItem.Header = Strings.Get("Ctx_Reveal");
        ShareCopyItem.Header = Strings.Get("Ctx_CopyPath");
        NewestButton.Content = Strings.Get("Dup_MarkExceptNewest");
        FolderButton.Content = Strings.Get("Dup_MarkExceptFolder");
        LocalFolderItem.Header = Strings.Get("Dup_MarkExceptFolder");
        FolderButton.ToolTip = Strings.Get("Dup_MarkExceptFolderTip");
        ClearButton.Content = Strings.Get("Dup_ClearMarks");
        RecycleButton.Content = Strings.Get("Dup_Recycle");
    }

    // ---------------------------------------------------------------- minimum size

    private sealed record MinSizeChoice(long Bytes)
    {
        public override string ToString() => ByteSize.Format(Bytes);
    }

    private void FillMinSizes()
    {
        long current = AppSettings.SanitizeMinSize(_services.Settings.Current.DuplicateMinSize);
        var choices = MinSizeChoices.ToList();
        if (!choices.Contains(current))
        {
            choices.Add(current); // a value edited in settings.json stays selectable
            choices.Sort();
        }

        foreach (long bytes in choices)
        {
            MinSizeBox.Items.Add(new MinSizeChoice(bytes));
        }

        MinSizeBox.SelectedIndex = choices.IndexOf(current);
    }

    private void OnMinSizeChanged()
    {
        if (MinSizeBox.SelectedItem is not MinSizeChoice choice
            || choice.Bytes == _services.Settings.Current.DuplicateMinSize)
        {
            return;
        }

        try
        {
            _services.Settings.Update(s => s with { DuplicateMinSize = choice.Bytes });
        }
        catch (Exception ex)
        {
            FileLog.Shared.Error("Saving the minimum size failed", ex);
        }
    }

    // ---------------------------------------------------------------- search

    private async Task SearchAsync()
    {
        if (_searchCts is not null || _recycling)
        {
            return;
        }

        var cts = CancellationTokenSource.CreateLinkedTokenSource(_closing.Token);
        _searchCts = cts;
        ShowMessage(null, false);
        StatusText.Text = Strings.Get("Dup_Loading");
        UpdateButtons();
        try
        {
            try
            {
                await _services.Index.LoadedTask.WaitAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                StatusText.Text = LocationUi.Format("Dup_Failed", ex.Message);
                return;
            }

            if (_services.Index.Store is not { } store)
            {
                StatusText.Text = Strings.Get("Dup_Loading");
                return;
            }

            long minSize = AppSettings.SanitizeMinSize(_services.Settings.Current.DuplicateMinSize);
            var finder = new DuplicateFinder(store, SystemLocalFileAccess.Instance, _services.Drives);
            var token = cts.Token;

            // Shares first: from the directory only, quick, nothing on the server is touched.
            _shareGroups = await Task.Run(() => finder.FindOnShares(minSize, token), token);
            FillRows(_shareRows, _shareGroups);
            _searched = true;

            SetProgress(new DuplicateProgress(DuplicatePhase.Checking, 0, 0, 0));
            _progressTimer.Start();
            ShowProgress();
            var result = await LowPriorityWork.Run(() => finder.FindLocal(minSize, SetProgress, token));
            _localGroups = result.Groups;
            FillRows(_localRows, _localGroups);
            StatusText.Text = SummaryText(result);
        }
        catch (OperationCanceledException)
        {
            if (!_closing.IsCancellationRequested)
            {
                StatusText.Text = Strings.Get("Dup_Cancelled");
            }
        }
        catch (Exception ex)
        {
            FileLog.Shared.Error("The duplicate search failed", ex);
            StatusText.Text = LocationUi.Format("Dup_Failed", ex.Message);
        }
        finally
        {
            _progressTimer.Stop();
            SetProgress(null);
            _searchCts = null;
            cts.Dispose();
            if (!_closing.IsCancellationRequested)
            {
                ShowEmptyTexts();
                UpdateButtons();
            }
        }
    }

    private static string SummaryText(DuplicateScanResult result)
    {
        var s = result.Stats;
        string text = LocationUi.Format(
            "Dup_Done", result.Groups.Count, ByteSize.Format(result.Groups.Sum(g => g.WastedBytes)), s.Candidates,
            ByteSize.Format(s.BytesRead), s.HashesReused);
        if (s.CloudSkipped + s.LinksSkipped + s.Changed + s.Unreadable + s.HardLinks > 0)
        {
            text += " " + LocationUi.Format("Dup_Skipped", s.CloudSkipped, s.LinksSkipped, s.Changed, s.Unreadable, s.HardLinks);
        }

        return text;
    }

    private void SetProgress(DuplicateProgress progress) => SetProgress((DuplicateProgress?)progress);

    private void SetProgress(DuplicateProgress? progress)
    {
        lock (_progressGate)
        {
            _progress = progress;
        }
    }

    private void ShowProgress()
    {
        DuplicateProgress? p;
        lock (_progressGate)
        {
            p = _progress;
        }

        if (p is not { } progress)
        {
            return;
        }

        StatusText.Text = progress.Phase switch
        {
            DuplicatePhase.Checking => LocationUi.Format("Dup_ProgressChecking", progress.Done, progress.Total),
            DuplicatePhase.Partial => LocationUi.Format(
                "Dup_ProgressPartial", progress.Done, progress.Total, ByteSize.Format(progress.BytesRead)),
            _ => LocationUi.Format("Dup_ProgressFull", progress.Done, progress.Total, ByteSize.Format(progress.BytesRead)),
        };
    }

    private void FillRows(ObservableCollection<DuplicateRow> rows, IReadOnlyList<DuplicateGroup> groups)
    {
        rows.Clear();
        foreach (var group in groups.Where(g => g.Files.Count > 1))
        {
            rows.Add(new DuplicateGroupRow(group));
            foreach (var file in group.Files)
            {
                rows.Add(new DuplicateFileRow(file, UpdateMarked));
            }
        }

        UpdateMarked();
    }

    private void ShowEmptyTexts()
    {
        LocalEmpty.Text = _searched ? Strings.Get("Dup_NoneLocal") : Strings.Get("Dup_NotSearched");
        SharesEmpty.Text = _searched ? Strings.Get("Dup_NoneShares") : Strings.Get("Dup_NotSearched");
        LocalEmpty.Visibility = _localRows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        SharesEmpty.Visibility = _shareRows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    // ---------------------------------------------------------------- marks

    private void MarkAllExceptNewest()
    {
        DuplicateMarking.MarkAllExceptNewest(_localGroups);
        RefreshMarks();
    }

    private void MarkAllExceptFolder()
    {
        if (LocalList.SelectedItem is not DuplicateFileRow row)
        {
            ShowMessage(Strings.Get("Dup_SelectFileFirst"), true);
            return;
        }

        string folder = row.File.Folder;
        int changed = DuplicateMarking.MarkAllExceptIn(_localGroups, folder);
        RefreshMarks();
        ShowMessage(LocationUi.Format("Dup_FolderKept", folder, changed), false);
    }

    private void ClearMarks()
    {
        DuplicateMarking.ClearMarks(_localGroups);
        RefreshMarks();
    }

    private void RefreshMarks()
    {
        foreach (var row in _localRows.OfType<DuplicateFileRow>())
        {
            row.Refresh();
        }

        UpdateMarked();
    }

    private void UpdateMarked()
    {
        int count = DuplicateMarking.MarkedCount(_localGroups);
        MarkedText.Text = LocationUi.Format("Dup_MarkedSummary", count, ByteSize.Format(DuplicateMarking.MarkedBytes(_localGroups)));
        UpdateButtons();
    }

    // ---------------------------------------------------------------- actions

    private bool LocalTabShown => Tabs.SelectedItem == LocalTab;

    private DuplicateFileRow? SelectedRow
        => (LocalTabShown ? LocalList.SelectedItem : ShareList.SelectedItem) as DuplicateFileRow;

    private void UpdateButtons()
    {
        bool searching = _searchCts is not null;
        SearchButton.IsEnabled = !searching && !_recycling;
        CancelButton.IsEnabled = searching;
        MinSizeBox.IsEnabled = !searching && !_recycling;

        bool has = SelectedRow is not null;
        RevealButton.IsEnabled = has;
        CopyButton.IsEnabled = has;
        LocalRevealItem.IsEnabled = LocalCopyItem.IsEnabled = LocalList.SelectedItem is DuplicateFileRow;
        ShareRevealItem.IsEnabled = ShareCopyItem.IsEnabled = ShareList.SelectedItem is DuplicateFileRow;

        // PLAN.md 11.2: shares have no delete function at all; the marking helpers only exist for the local tab.
        var localOnly = LocalTabShown ? Visibility.Visible : Visibility.Collapsed;
        NewestButton.Visibility = FolderButton.Visibility = ClearButton.Visibility = RecycleButton.Visibility = localOnly;
        MarkedText.Visibility = localOnly;
        bool idle = !searching && !_recycling && _localGroups.Count > 0;
        NewestButton.IsEnabled = idle;
        FolderButton.IsEnabled = idle && LocalList.SelectedItem is DuplicateFileRow;
        LocalFolderItem.IsEnabled = FolderButton.IsEnabled;
        ClearButton.IsEnabled = idle && DuplicateMarking.MarkedCount(_localGroups) > 0;
        RecycleButton.IsEnabled = ClearButton.IsEnabled;
        LocalList.IsEnabled = !_recycling;
    }

    /// <summary>A right click selects the line under the mouse first, like the left click does.</summary>
    private void OnRowRightClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is ListBoxItem item)
        {
            item.IsSelected = true;
        }
    }

    private void Reveal()
    {
        // On a share this opens Explorer at the file, only on this explicit click (as in the storage window).
        if (SelectedRow is { } row && ShellActions.RevealInExplorer(row.File.FullPath) is { } error)
        {
            ShowMessage(LocationUi.Format("Storage_ActionFailed", error), true);
        }
    }

    private void CopyPath()
    {
        if (SelectedRow is { } row)
        {
            ShowMessage(
                ShellActions.CopyText(row.File.FullPath) ? Strings.Get("Storage_Copied") : Strings.Get("Storage_CopyFailed"),
                false);
        }
    }

    /// <summary>
    /// "Move to Recycle Bin" (PLAN.md 11.2): shows number and total size, asks with No as the default, checks size and
    /// date of every file again right before moving it, and removes moved files from the directory afterwards.
    /// </summary>
    private async Task RecycleAsync()
    {
        int count = DuplicateMarking.MarkedCount(_localGroups);
        if (_recycling || _searchCts is not null || count == 0)
        {
            return;
        }

        var answer = MessageBox.Show(
            this,
            LocationUi.Format("Dup_Confirm", count, ByteSize.Format(DuplicateMarking.MarkedBytes(_localGroups))),
            Strings.Get("Dup_Title"),
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);
        if (answer != MessageBoxResult.Yes)
        {
            return;
        }

        _recycling = true;
        UpdateButtons();
        Cursor = System.Windows.Input.Cursors.Wait;
        try
        {
            var groups = _localGroups;
            var store = _services.Index.Store;
            var recycler = new DuplicateRecycler(new SystemRecycleBin(), SystemLocalFileAccess.Instance);
            var report = await Task.Run(() => recycler.Recycle(groups, file => Forget(store, file)));

            _localGroups = [.. groups.Where(g => g.Files.Count > 1)];
            FillRows(_localRows, _localGroups);
            ShowMessage(ReportText(report), report.Any(r => r.Outcome != DuplicateRecycleOutcome.Moved));
        }
        catch (Exception ex)
        {
            FileLog.Shared.Error("Moving duplicates to the Recycle Bin failed", ex);
            ShowMessage(LocationUi.Format("Storage_RecycleFailed", ex.Message), true);
        }
        finally
        {
            _recycling = false;
            Cursor = null;
            ShowEmptyTexts();
            UpdateButtons();
        }
    }

    /// <summary>Takes a moved file out of the directory, the same way a hit that cannot be found is removed.</summary>
    private static void Forget(IndexStore? store, DuplicateFile file)
    {
        if (store is null)
        {
            return; // the watcher or the next scan will notice it
        }

        try
        {
            DuplicateRecycler.ForgetIfVanished(store, file, FileSystemPathProbe.Instance);
        }
        catch (Exception ex)
        {
            FileLog.Shared.Error("Removing a moved duplicate from the directory failed", ex);
        }
    }

    private static string ReportText(IReadOnlyList<DuplicateRecycleItem> report)
    {
        var parts = new List<string>
        {
            LocationUi.Format("Dup_RecycleMoved", report.Count(r => r.Outcome == DuplicateRecycleOutcome.Moved)),
        };
        var skipped = report.Where(r => r.Outcome is DuplicateRecycleOutcome.Changed or DuplicateRecycleOutcome.KeptCopyChanged).ToList();
        if (skipped.Count > 0)
        {
            parts.Add(LocationUi.Format(
                "Dup_RecycleSkipped", skipped.Count, string.Join(", ", skipped.Take(5).Select(r => r.File.FullPath))));
        }

        var failed = report.Where(r => r.Outcome is DuplicateRecycleOutcome.Failed or DuplicateRecycleOutcome.NotAllowed).ToList();
        if (failed.Count > 0)
        {
            parts.Add(LocationUi.Format(
                "Dup_RecycleFailed", failed.Count, failed[0].Error ?? failed[0].File.FullPath));
        }

        return string.Join(" ", parts);
    }

    private void ShowMessage(string? text, bool warning)
    {
        ActionMessage.Text = text ?? string.Empty;
        ActionMessage.Foreground = warning ? (Brush)FindResource("WarnBrush") : (Brush)FindResource("MutedBrush");
        ActionMessage.Visibility = text is null ? Visibility.Collapsed : Visibility.Visible;
    }
}
