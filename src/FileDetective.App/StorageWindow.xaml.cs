using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using FileDetective.App.Interop;
using FileDetective.App.ViewModels;
using FileDetective.Core;
using Brush = System.Windows.Media.Brush;
using MessageBox = System.Windows.MessageBox;

namespace FileDetective.App;

/// <summary>
/// Storage space window (PLAN.md 11.1): drives with used and free space, a folder tree sorted by size and the largest
/// files. Shares show the state of the last directory scan and never touch the server. A local drive is walked once, on
/// click, in the background; its numbers live in memory only and are dropped when the window is closed.
/// </summary>
public partial class StorageWindow : Window
{
    private static readonly SystemRecycleBin SystemBin = new();
    private static readonly TimeSpan ProgressInterval = TimeSpan.FromMilliseconds(400);

    private readonly DriveDiscovery _driveDiscovery;
    private readonly IndexService? _index;
    private readonly bool _adminMode;
    private readonly bool _offerAdmin;
    private readonly StorageAnalysis _analysis;
    private readonly SpaceRecycler _recycler = new(SystemBin, Environment.GetFolderPath(Environment.SpecialFolder.Windows));
    private readonly ObservableCollection<StorageDriveItem> _drives = [];
    private readonly Dictionary<string, SpaceSnapshot> _snapshots = new(StringComparer.OrdinalIgnoreCase);
    private readonly CancellationTokenSource _closing = new();
    private readonly DispatcherTimer _progressTimer;
    private readonly object _progressGate = new();
    private SpaceProgress _progress;
    private CancellationTokenSource? _scanCts;
    private StorageDriveItem? _scanning;
    private StorageDriveItem? _shownItem;
    private SpaceSnapshot? _shown;
    private bool _recycling;
    private bool _launchingAdmin;

    /// <param name="drives">Finds the local drives.</param>
    /// <param name="index">The directory, for shares; null in the storage process with administrator rights (no shares).</param>
    /// <param name="mode">How the program was started; <see cref="StartupMode.StorageAdmin"/> is the storage process.</param>
    internal StorageWindow(DriveDiscovery drives, IndexService? index, StartupMode mode)
    {
        _driveDiscovery = drives;
        _index = index;
        _adminMode = mode == StartupMode.StorageAdmin;
        _offerAdmin = StorageAdminLauncher.CanOffer(mode, WindowsElevationProbe.Instance);
        _analysis = new StorageAnalysis(
            () => _index?.Store,
            FileSystemDirectoryLister.Instance,
            ReparseTagLinkDetector.Instance,
            drives,
            SystemDriveSpaceSource.Instance);
        InitializeComponent();
        Title = Strings.Get(_adminMode ? "Storage_TitleAdmin" : "Storage_Title");
        FillTexts();
        DriveList.ItemsSource = _drives;

        _progressTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher) { Interval = ProgressInterval };
        _progressTimer.Tick += (_, _) => ShowProgress();

        DriveList.SelectionChanged += (_, _) => OnDriveSelected();
        AnalyzeButton.Click += async (_, _) => await AnalyzeAsync();
        CancelButton.Click += (_, _) => _scanCts?.Cancel();
        AdminButton.Click += async (_, _) => await LaunchAdminAsync();
        RevealButton.Click += (_, _) => Reveal();
        CopyButton.Click += (_, _) => CopyPath();
        RecycleButton.Click += async (_, _) => await RecycleAsync();
        EmptyBinButton.Click += async (_, _) => await EmptyBinAsync();
        TreeEmptyBinItem.Click += async (_, _) => await EmptyBinAsync();
        TreeRevealItem.Click += (_, _) => Reveal();
        FileRevealItem.Click += (_, _) => Reveal();
        TreeCopyItem.Click += (_, _) => CopyPath();
        FileCopyItem.Click += (_, _) => CopyPath();
        TreeRecycleItem.Click += async (_, _) => await RecycleAsync();
        FileRecycleItem.Click += async (_, _) => await RecycleAsync();
        FolderTree.SelectedItemChanged += (_, _) => UpdateButtons();
        FileList.SelectionChanged += (_, _) => UpdateButtons();
        Tabs.SelectionChanged += (_, e) =>
        {
            if (ReferenceEquals(e.OriginalSource, Tabs))
            {
                UpdateButtons();
            }
        };
        TreeMenu.Opened += (_, _) => UpdateButtons();
        FileMenu.Opened += (_, _) => UpdateButtons();

        Loaded += async (_, _) => await LoadDrivesAsync();
        Closing += (_, _) =>
        {
            // The numbers of local drives are dropped with the window (PLAN.md 11.1); a running walk is cancelled.
            _closing.Cancel();
            _scanCts?.Cancel();
        };
        Closed += (_, _) =>
        {
            _progressTimer.Stop();
            FolderTree.ItemsSource = null;
            FileList.ItemsSource = null;
            _snapshots.Clear();
            _shown = null;
        };

        ShowItem(null);
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
        DrivesHeading.Text = Strings.Get("Storage_Drives");
        AnalyzeButton.Content = Strings.Get("Storage_Analyze");
        CancelButton.Content = Strings.Get("Button_Cancel");
        FoldersTab.Header = Strings.Get("Storage_TabFolders");
        FilesTab.Header = Strings.Get("Storage_TabFiles");
        HeaderName.Text = Strings.Get("Storage_ColName");
        HeaderSize.Text = Strings.Get("Storage_ColSize");
        HeaderShare.Text = Strings.Get("Storage_ColShare");
        HeaderFiles.Text = Strings.Get("Storage_ColFiles");
        HeaderFolders.Text = Strings.Get("Storage_ColFolders");
        HeaderModified.Text = Strings.Get("Storage_ColModified");
        FileNameColumn.Header = Strings.Get("Storage_ColName");
        FilePathColumn.Header = Strings.Get("Storage_ColFolder");
        FileSizeColumn.Header = Strings.Get("Storage_ColSize");
        FileDateColumn.Header = Strings.Get("Storage_ColModified");
        RevealButton.Content = Strings.Get("Ctx_Reveal");
        CopyButton.Content = Strings.Get("Ctx_CopyPath");
        RecycleButton.Content = Strings.Get("Storage_Recycle");
        TreeRevealItem.Header = Strings.Get("Ctx_Reveal");
        FileRevealItem.Header = Strings.Get("Ctx_Reveal");
        TreeCopyItem.Header = Strings.Get("Ctx_CopyPath");
        FileCopyItem.Header = Strings.Get("Ctx_CopyPath");
        TreeRecycleItem.Header = Strings.Get("Storage_Recycle");
        EmptyBinButton.Content = Strings.Get("Storage_EmptyBin");
        TreeEmptyBinItem.Header = Strings.Get("Storage_EmptyBin");
        FileRecycleItem.Header = Strings.Get("Storage_Recycle");
        LocalHint.Text = Strings.Get("Storage_LocalHint");
        DrivesEmpty.Text = Strings.Get("Settings_Loading");
        AdminButton.Content = Strings.Get("Storage_AnalyzeAsAdmin");
        AdminButton.ToolTip = Strings.Get("Storage_AnalyzeAsAdminTip");
        AdminButton.Visibility = _offerAdmin ? Visibility.Visible : Visibility.Collapsed;
        AdminBadge.Text = Strings.Get("Storage_AdminBadge");
        AdminBadgeBox.Visibility = _adminMode ? Visibility.Visible : Visibility.Collapsed;
        AdminHint.Text = Strings.Get("Storage_AdminNoShares");
        AdminHint.Visibility = _adminMode ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>
    /// "Analyze as administrator…" (PLAN.md 2.6, 11.1): starts this program once more with <c>--storage-admin</c> and the
    /// UAC prompt of Windows. Only on this click. A declined prompt changes nothing but a short hint.
    /// </summary>
    private async Task LaunchAdminAsync()
    {
        if (!_offerAdmin || _launchingAdmin)
        {
            return;
        }

        _launchingAdmin = true;
        AdminButton.IsEnabled = false;
        try
        {
            string? exe = Environment.ProcessPath;
            var result = await Task.Run(() => StorageAdminLauncher.Launch(ShellRunAsStarter.Instance, exe));
            switch (result.Status)
            {
                case AdminLaunchStatus.Started:
                    ShowMessage(Strings.Get("Storage_AdminStarted"), false);
                    break;
                case AdminLaunchStatus.Declined:
                    ShowMessage(Strings.Get("Storage_AdminDeclined"), false);
                    break;
                default:
                    FileLog.Shared.Error("Starting the storage analysis as administrator failed", result.Error);
                    ShowMessage(LocationUi.Format("Storage_AdminFailed", result.Error?.Message ?? string.Empty), true);
                    break;
            }
        }
        finally
        {
            _launchingAdmin = false;
            AdminButton.IsEnabled = true;
        }
    }

    // ---------------------------------------------------------------- drives and shares

    private async Task LoadDrivesAsync()
    {
        var discovered = await LocationUi.DiscoverAsync(_driveDiscovery);
        if (_closing.IsCancellationRequested)
        {
            return;
        }

        foreach (var drive in discovered.Where(d => d.Kind != RootKind.Network))
        {
            string title = drive.Label is { } label ? $"{drive.Path}  ({label})" : drive.Path;
            _drives.Add(new StorageDriveItem(drive.Path, title, drive.Kind, null)
            {
                Detail = Strings.Get("Storage_NotAnalyzed"),
            });
        }

        DrivesEmpty.Visibility = _drives.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        _ = LoadDriveSpaceAsync();
        await AddSharesAsync();
        DrivesEmpty.Visibility = _drives.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        DrivesEmpty.Text = Strings.Get("Storage_NoDrives");
        if (DriveList.SelectedItem is null && _drives.Count > 0)
        {
            DriveList.SelectedIndex = 0;
        }
    }

    /// <summary>Used and free space of the local drives; asks Windows about the volume only, in the background.</summary>
    private async Task LoadDriveSpaceAsync()
    {
        foreach (var item in _drives.Where(d => !d.IsShare).ToList())
        {
            DriveSpace? space;
            try
            {
                string path = item.Path;
                space = await Task.Run(() => _analysis.GetDriveSpace(path));
            }
            catch (Exception ex)
            {
                FileLog.Shared.Error("Reading the drive space failed", ex);
                space = null;
            }

            if (_closing.IsCancellationRequested)
            {
                return;
            }

            ApplySpace(item, space);
        }
    }

    /// <summary>Shares come from the locations of kind Network in the directory; nothing here asks the server.</summary>
    private async Task AddSharesAsync()
    {
        if (_index is not { } index)
        {
            return; // the storage process has no directory, so no shares (PLAN.md 11.1)
        }

        try
        {
            await index.LoadedTask;
        }
        catch (Exception)
        {
            return; // the directory could not be loaded: no shares to show (the failure is reported elsewhere)
        }

        if (_closing.IsCancellationRequested)
        {
            return;
        }

        foreach (var root in index.GetRoots().Where(r => r.Kind == RootKind.Network))
        {
            _drives.Add(new StorageDriveItem(root.Path, root.UncPath is { } unc ? $"{root.Path}  ({unc})" : root.Path, root.Kind, root.Id)
            {
                Detail = ShareStateText(root),
                SpaceText = Strings.Get("Storage_NoServerAccess"),
            });
        }
    }

    private static string ShareStateText(RootInfo root)
    {
        if (root.LastScanCompletedUtc is not { } unix)
        {
            return Strings.Get("Storage_ShareNeverScanned");
        }

        string when = DateTimeOffset.FromUnixTimeSeconds(unix).LocalDateTime.ToString("g", CultureInfo.CurrentCulture);
        return root.LastScanStatus == ScanStatus.Complete
            ? LocationUi.Format("Storage_ShareAsOf", when)
            : LocationUi.Format("Storage_ShareAsOfPartial", when);
    }

    private static void ApplySpace(StorageDriveItem item, DriveSpace? space)
    {
        if (space is not { } s || s.TotalBytes <= 0)
        {
            item.BarVisibility = Visibility.Collapsed;
            item.SpaceText = string.Empty;
            return;
        }

        item.UsedFraction = Math.Min(1, (double)s.UsedBytes / s.TotalBytes);
        item.BarVisibility = Visibility.Visible;
        item.SpaceText = LocationUi.Format(
            "Storage_DriveSpace", ByteSize.Format(s.UsedBytes), ByteSize.Format(s.TotalBytes), ByteSize.Format(s.FreeBytes));
    }

    private void OnDriveSelected()
    {
        ShowMessage(null, false);
        ShowItem(DriveList.SelectedItem as StorageDriveItem);
    }

    /// <summary>Shows what is known about the item: its numbers, or the invitation to analyze it, or the share being read.</summary>
    private void ShowItem(StorageDriveItem? item)
    {
        _shownItem = item;
        UpdateButtons();
        if (item is null)
        {
            ShowPlaceholder(Strings.Get("Storage_Select"));
            return;
        }

        if (_snapshots.TryGetValue(item.Path, out var snapshot) && !IsStale(item, snapshot))
        {
            ShowSnapshot(item, snapshot);
            return;
        }

        if (item.IsShare)
        {
            ShowPlaceholder(Strings.Get("Storage_Reading"));
            _ = LoadShareAsync(item);
        }
        else if (ReferenceEquals(_scanning, item))
        {
            ShowPlaceholder(Strings.Get("Storage_Working"));
            ShowProgress();
        }
        else
        {
            ShowPlaceholder(Strings.Get("Storage_NotAnalyzedHint"));
        }
    }

    /// <summary>A share snapshot is out of date when a newer scan finished since it was read.</summary>
    private bool IsStale(StorageDriveItem item, SpaceSnapshot snapshot)
    {
        if (!item.IsShare || item.RootId is not { } id || _index is null)
        {
            return false;
        }

        long? current = _index.GetRoots().FirstOrDefault(r => r.Id == id)?.LastScanCompletedUtc;
        return current != snapshot.AsOf?.ToUnixTimeSeconds();
    }

    private async Task LoadShareAsync(StorageDriveItem item)
    {
        if (item.RootId is not { } id)
        {
            return;
        }

        SpaceSnapshot snapshot;
        try
        {
            var token = _closing.Token;
            snapshot = await Task.Run(() => _analysis.ReadShare(id, token), token);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            FileLog.Shared.Error("Reading the directory for the storage analysis failed", ex);
            if (ReferenceEquals(_shownItem, item))
            {
                ShowPlaceholder(LocationUi.Format("Storage_ReadFailed", ex.Message));
            }

            return;
        }

        _snapshots[item.Path] = snapshot;
        item.SpaceText = LocationUi.Format("Storage_ShareTotal", ByteSize.Format(snapshot.ReadBytes), snapshot.Root.FileCount);
        if (ReferenceEquals(_shownItem, item))
        {
            ShowSnapshot(item, snapshot);
        }
    }

    // ---------------------------------------------------------------- the walk of a local drive

    private async Task AnalyzeAsync()
    {
        if (DriveList.SelectedItem is not StorageDriveItem item || item.IsShare || _scanning is not null)
        {
            return;
        }

        var cts = CancellationTokenSource.CreateLinkedTokenSource(_closing.Token);
        _scanCts = cts;
        _scanning = item;
        SetProgress(default);
        _progressTimer.Start();
        ShowMessage(null, false);
        UpdateButtons();
        if (!_snapshots.ContainsKey(item.Path))
        {
            ShowPlaceholder(Strings.Get("Storage_Working"));
        }

        ShowProgress();

        SpaceSnapshot? snapshot = null;
        Exception? error = null;
        string path = item.Path;
        try
        {
            snapshot = await Task.Run(() => _analysis.ScanLocal(path, SetProgress, cts.Token));
        }
        catch (Exception ex)
        {
            error = ex;
            FileLog.Shared.Error($"Storage analysis of {path} failed", ex);
        }

        _progressTimer.Stop();
        _scanning = null;
        _scanCts = null;
        cts.Dispose();
        if (_closing.IsCancellationRequested)
        {
            return;
        }

        string? status = null;
        if (error is not null)
        {
            status = LocationUi.Format("Storage_ScanFailed", error.Message);
        }
        else if (snapshot!.Outcome == ScanOutcome.Cancelled)
        {
            status = Strings.Get("Storage_ScanCancelled"); // partial numbers are dropped
        }
        else if (snapshot.Outcome == ScanOutcome.RootUnreachable)
        {
            status = Strings.Get("Storage_ScanUnreachable");
        }
        else
        {
            _snapshots[item.Path] = snapshot;
            item.Detail = LocationUi.Format(
                "Storage_ItemAnalyzed",
                (snapshot.AsOf ?? DateTimeOffset.Now).LocalDateTime.ToString("g", CultureInfo.CurrentCulture),
                snapshot.Root.FileCount);
            if (snapshot.DriveSpace is { } space)
            {
                ApplySpace(item, space);
            }
        }

        if (ReferenceEquals(_shownItem, item))
        {
            ShowItem(item);
        }
        else
        {
            UpdateButtons();
        }

        if (status is not null)
        {
            StatusText.Text = status;
        }
    }

    private void SetProgress(SpaceProgress progress)
    {
        lock (_progressGate)
        {
            _progress = progress;
        }
    }

    private void ShowProgress()
    {
        if (_scanning is null)
        {
            return;
        }

        SpaceProgress p;
        lock (_progressGate)
        {
            p = _progress;
        }

        StatusText.Text = LocationUi.Format(
            "Storage_Scanning", _scanning.Path, p.Directories, p.Files, ByteSize.Format(p.Bytes));
    }

    // ---------------------------------------------------------------- showing numbers

    private void ShowPlaceholder(string text)
    {
        _shown = null;
        FolderTree.ItemsSource = null;
        FileList.ItemsSource = null;
        Tabs.Visibility = Visibility.Collapsed;
        PlaceholderText.Text = text;
        PlaceholderText.Visibility = Visibility.Visible;
        if (_scanning is null)
        {
            StatusText.Text = string.Empty;
        }

        UpdateButtons();
    }

    private void ShowSnapshot(StorageDriveItem item, SpaceSnapshot snapshot)
    {
        _shown = snapshot;
        PlaceholderText.Visibility = Visibility.Collapsed;
        Tabs.Visibility = Visibility.Visible;
        FolderTree.ItemsSource = new[] { SpaceRow.CreateRoot(new SpaceTreeContext(snapshot, folder => LoadFilesAsync(snapshot, folder))) };
        FileList.ItemsSource = snapshot.TopFiles.Select(f => new SpaceFileRow(f)).ToList();
        if (_scanning is null)
        {
            StatusText.Text = SummaryText(snapshot);
        }

        UpdateButtons();
    }

    /// <summary>
    /// The files of one expanded folder (PLAN.md 11.1), on a background thread: a local folder is listed (no file is
    /// opened), a share's files come from the directory and the server is not asked.
    /// </summary>
    private Task<FolderFileListing> LoadFilesAsync(SpaceSnapshot snapshot, SpaceFolder folder)
        => Task.Run(() => _analysis.LoadFiles(snapshot, folder), _closing.Token);

    private static string SummaryText(SpaceSnapshot snapshot)
    {
        var culture = CultureInfo.CurrentCulture;
        string when = snapshot.AsOf?.LocalDateTime.ToString("g", culture) ?? "–";
        if (snapshot.IsLocal)
        {
            string text = LocationUi.Format(
                "Storage_ScanDone", snapshot.Root.Name, when, snapshot.Root.FileCount, snapshot.Root.FolderCount,
                ByteSize.Format(snapshot.ReadBytes));
            if (snapshot.FailedDirectories > 0)
            {
                text += " " + LocationUi.Format("Storage_ScanErrors", snapshot.FailedDirectories);
            }

            return text;
        }

        if (snapshot.AsOf is null)
        {
            return Strings.Get("Storage_ShareNeverScanned");
        }

        string state = snapshot.DirectoryStatus == ScanStatus.Complete
            ? LocationUi.Format("Storage_ShareAsOf", when)
            : LocationUi.Format("Storage_ShareAsOfPartial", when);
        if (snapshot.FailedDirectories > 0)
        {
            state += " " + LocationUi.Format("Storage_ShareErrors", snapshot.FailedDirectories);
        }

        return state + " " + Strings.Get("Storage_NoServerAccess");
    }

    // ---------------------------------------------------------------- selection and actions

    /// <summary>What the current tab has selected: the path and the folder or file behind it. Null for the line "Unreadable".</summary>
    private (string Path, SpaceFolder? Folder, SpaceFile? File)? Target()
    {
        if (_shown is null)
        {
            return null;
        }

        if (Tabs.SelectedItem == FoldersTab)
        {
            return FolderTree.SelectedItem switch
            {
                SpaceRow { Folder: { } folder } => (folder.FullPath, folder, null),
                SpaceRow { File: { } file } => (file.FullPath, null, file),
                SpaceRow { LockedPath: { } locked } => (locked, null, null), // only Show in Explorer and Copy path
                _ => null,
            };
        }

        return FileList.SelectedItem is SpaceFileRow row ? (row.File.FullPath, null, row.File) : null;
    }

    private void UpdateButtons()
    {
        bool local = _shownItem is { IsShare: false };
        AnalyzeButton.IsEnabled = local && _scanning is null && !_recycling;
        CancelButton.IsEnabled = _scanning is not null;
        AnalyzeButton.Visibility = _shownItem is { IsShare: true } ? Visibility.Collapsed : Visibility.Visible;
        CancelButton.Visibility = AnalyzeButton.Visibility;

        var target = Target();
        bool has = target is not null;
        RevealButton.IsEnabled = has;
        CopyButton.IsEnabled = has;
        bool canRecycle = target is { } t && _shown is { } snapshot && !_recycling
            && (t.Folder is { } folder ? _recycler.CanRecycle(snapshot, folder) : t.File is { } file && _recycler.CanRecycle(snapshot, file));
        RecycleButton.IsEnabled = canRecycle;

        // PLAN.md 11.1: "Move to Recycle Bin" exists for local entries only; for shares it is not even offered. The
        // Recycle Bin line has "Empty Recycle Bin" instead.
        bool isBin = target is { } binTarget && binTarget.Folder is { IsRecycleBin: true } && _shown is { CanMoveToRecycleBin: true };
        bool canEmpty = isBin && !_recycling && target is { } t2 && t2.Folder is { } binFolder && _shown is { } binSnapshot
            && _recycler.CanEmptyRecycleBin(binSnapshot, binFolder);
        var recycleVisibility = _shown is { CanMoveToRecycleBin: false } ? Visibility.Collapsed : Visibility.Visible;
        var treeRecycleVisibility = isBin ? Visibility.Collapsed : recycleVisibility;
        RecycleButton.Visibility = treeRecycleVisibility;
        TreeRecycleItem.Visibility = treeRecycleVisibility;
        FileRecycleItem.Visibility = recycleVisibility;
        var emptyVisibility = isBin ? Visibility.Visible : Visibility.Collapsed;
        EmptyBinButton.Visibility = emptyVisibility;
        TreeEmptyBinItem.Visibility = emptyVisibility;
        EmptyBinButton.IsEnabled = canEmpty;
        TreeEmptyBinItem.IsEnabled = canEmpty;
        TreeRevealItem.IsEnabled = FileRevealItem.IsEnabled = has;
        TreeCopyItem.IsEnabled = FileCopyItem.IsEnabled = has;
        TreeRecycleItem.IsEnabled = FileRecycleItem.IsEnabled = canRecycle;
    }

    /// <summary>A right click selects the line under the mouse first, like the left click does.</summary>
    private void OnTreeItemRightClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is TreeViewItem item)
        {
            item.IsSelected = true;
        }
    }

    private void Reveal()
    {
        if (Target() is not { } target)
        {
            return;
        }

        if (ShellActions.RevealInExplorer(target.Path) is { } error)
        {
            ShowMessage(LocationUi.Format("Storage_ActionFailed", error), true);
        }
    }

    private void CopyPath()
    {
        if (Target() is not { } target)
        {
            return;
        }

        ShowMessage(
            ShellActions.CopyText(target.Path) ? Strings.Get("Storage_Copied") : Strings.Get("Storage_CopyFailed"),
            false);
    }

    /// <summary>
    /// "Move to Recycle Bin" (PLAN.md 11.1): only for local entries, only after an explicit yes, never permanently. The
    /// answer defaults to No. The numbers of the window are adjusted afterwards; the bytes stay on the drive until the
    /// Recycle Bin is emptied.
    /// </summary>
    private async Task RecycleAsync()
    {
        if (_recycling || Target() is not { } target || _shown is not { } snapshot || (target.Folder is null && target.File is null))
        {
            return;
        }

        string question = target.Folder is { } folder
            ? LocationUi.Format("Storage_ConfirmFolder", target.Path, folder.FileCount, ByteSize.Format(folder.Size))
            : LocationUi.Format("Storage_ConfirmFile", target.Path, ByteSize.Format(target.File!.Size));
        if (_adminMode)
        {
            question += Environment.NewLine + Environment.NewLine + Strings.Get("Storage_AdminRecycleNote");
        }

        var answer = MessageBox.Show(
            this, question, Strings.Get("Storage_Title"), MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
        if (answer != MessageBoxResult.Yes)
        {
            return;
        }

        _recycling = true;
        UpdateButtons();
        Cursor = System.Windows.Input.Cursors.Wait;
        try
        {
            if (target.Folder is { } f)
            {
                var row = (SpaceRow?)FolderTree.SelectedItem;
                await Task.Run(() => _recycler.MoveToBin(snapshot, f));
                snapshot.RemoveFolder(f);
                row?.RemoveFromTree();
            }
            else
            {
                var file = target.File!;
                var fileRow = Tabs.SelectedItem == FoldersTab ? FolderTree.SelectedItem as SpaceRow : null;
                await Task.Run(() => _recycler.MoveToBin(snapshot, file));
                snapshot.RemoveFile(file);
                var top = FolderTree.Items.Count > 0 ? FolderTree.Items[0] as SpaceRow : null;
                if (fileRow is not null)
                {
                    fileRow.RemoveFromTree();
                }
                else
                {
                    top?.RemoveRowsOf(file); // the file may be shown in the tree as well
                }

                top?.RefreshAll();
            }

            FileList.ItemsSource = snapshot.TopFiles.Select(x => new SpaceFileRow(x)).ToList();
            ShowMessage(LocationUi.Format("Storage_Recycled", target.Path), false);
        }
        catch (Exception ex)
        {
            FileLog.Shared.Error("Moving to the Recycle Bin failed", ex);
            ShowMessage(LocationUi.Format("Storage_RecycleFailed", ex.Message), true);
        }
        finally
        {
            _recycling = false;
            Cursor = null;
            UpdateButtons();
        }
    }

    /// <summary>
    /// "Empty Recycle Bin" (PLAN.md 11.1) for the Recycle Bin line of a local drive: Windows (<c>SHEmptyRecycleBin</c> for
    /// this drive only) asks the user and shows its own progress. If the user declines nothing changes. Afterwards the
    /// folder <c>$Recycle.Bin</c> is listed again (a local run over just that subtree) and the numbers are corrected.
    /// </summary>
    private async Task EmptyBinAsync()
    {
        if (_recycling || Target() is not { } target || target.Folder is not { } folder || _shown is not { } snapshot
            || !_recycler.CanEmptyRecycleBin(snapshot, folder))
        {
            return;
        }

        var row = FolderTree.SelectedItem as SpaceRow;
        nint owner = new WindowInteropHelper(this).Handle;
        string driveRoot = snapshot.Root.Name;
        _recycling = true;
        UpdateButtons();
        try
        {
            var result = await Task.Run(() => _recycler.EmptyRecycleBin(snapshot, folder, owner));
            if (result == RecycleBinEmptyResult.Cancelled)
            {
                ShowMessage(Strings.Get("Storage_BinNotEmptied"), false);
                return;
            }

            var fresh = await Task.Run(() => _analysis.ScanFolder(snapshot, folder));
            bool readable = fresh.Outcome is ScanOutcome.Complete or ScanOutcome.CompleteWithErrors;
            if (readable && fresh.Root.Size == folder.Size && folder.Size > 0)
            {
                // Windows reported success but the bin has the same size: the user declined, nothing happened.
                ShowMessage(Strings.Get("Storage_BinNotEmptied"), false);
                return;
            }

            var space = await Task.Run(() => _analysis.GetDriveSpace(driveRoot));
            if (_closing.IsCancellationRequested)
            {
                return;
            }

            if (snapshot.ReplaceFolder(folder, fresh))
            {
                row?.ReplaceFolderInTree(fresh.Root);
            }

            snapshot.RecycleBinWasEmptied(space);
            if (_shownItem is { } item && snapshot.DriveSpace is { } driveSpace)
            {
                ApplySpace(item, driveSpace);
            }

            (FolderTree.Items.Count > 0 ? FolderTree.Items[0] as SpaceRow : null)?.RefreshAll();
            FileList.ItemsSource = snapshot.TopFiles.Select(x => new SpaceFileRow(x)).ToList();
            ShowMessage(LocationUi.Format("Storage_BinEmptied", driveRoot), false);
        }
        catch (Exception ex)
        {
            FileLog.Shared.Error("Emptying the Recycle Bin failed", ex);
            ShowMessage(LocationUi.Format("Storage_BinFailed", ex.Message), true);
        }
        finally
        {
            _recycling = false;
            UpdateButtons();
        }
    }

    private void ShowMessage(string? text, bool warning)
    {
        ActionMessage.Text = text ?? string.Empty;
        ActionMessage.Foreground = warning ? (Brush)FindResource("WarnBrush") : (Brush)FindResource("MutedBrush");
        ActionMessage.Visibility = text is null ? Visibility.Collapsed : Visibility.Visible;
    }
}
