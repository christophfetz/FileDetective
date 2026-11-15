using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using FileDetective.App.Interop;
using FileDetective.App.ViewModels;
using FileDetective.Core;
using Brush = System.Windows.Media.Brush;
using Button = System.Windows.Controls.Button;
using ComboBox = System.Windows.Controls.ComboBox;
using DataFormats = System.Windows.DataFormats;
using DataObject = System.Windows.DataObject;
using DragDrop = System.Windows.DragDrop;
using DragDropEffects = System.Windows.DragDropEffects;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using MenuItem = System.Windows.Controls.MenuItem;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using Point = System.Windows.Point;
using RichTextBox = System.Windows.Controls.RichTextBox;
using TextBox = System.Windows.Controls.TextBox;

namespace FileDetective.App;

/// <summary>
/// Screenshot history (PLAN.md 11.3, stage 12): the images of the image text folders as a grid of previews, newest first and
/// grouped by day, with the same text search as the search bar and a filter by folder. The list comes from the directory
/// (<see cref="ImageHistory"/>), no folder is listed for it; previews are made in the background for visible rows of local
/// images only, never for a cloud-only file. Nothing is deleted here. New images appear by themselves: changes of the directory
/// and of the image text trigger a refresh, at most every 2 seconds (<see cref="RefreshThrottle"/>), that keeps the place in the
/// list and the selection.
/// </summary>
public partial class HistoryWindow : Window
{
    private const double HeaderHeight = 40;
    private const double TileRowHeight = 132;
    private const double TileWidth = 148;
    private const double ImageAreaDip = 132;

    private static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan SearchDelay = TimeSpan.FromMilliseconds(200);
    private static readonly TimeSpan MessageTime = TimeSpan.FromSeconds(5);

    private readonly AppServices _services;
    private readonly Thumbnails _thumbs;
    private readonly RefreshThrottle _throttle;
    private readonly DispatcherTimer _searchTimer;
    private readonly DispatcherTimer _messageTimer;
    private readonly CancellationTokenSource _closing = new();
    private readonly Dictionary<int, HistoryTileItem> _tiles = [];
    private ImageHistorySnapshot _snapshot = ImageHistorySnapshot.Empty;
    private IReadOnlyList<HistoryImage> _shown = [];
    private IReadOnlyList<HistoryRow> _rows = [];
    private SearchQuery _query = SearchQuery.Parse(null, SearchMode.ImageText);
    private string? _folderFilter;
    private bool _updatingFolders;
    private bool _loadedOnce;
    private bool _resetScroll;
    private bool _again;
    private bool _againRead;
    private int _busy;
    private int _columns = 4;
    private int _thumbPixels = 160;
    private int? _selectedId;
    private string? _message;
    private string? _panelKey;
    private CancellationTokenSource? _panelCts;
    private HistoryTileItem? _pressTile;
    private Point _pressPoint;
    private bool _menuOnTile;

    internal HistoryWindow(AppServices services)
    {
        _services = services;
        InitializeComponent();
        Title = Strings.Get("Hist_Title");
        FillTexts();

        var drives = services.Drives;
        _thumbs = new Thumbnails(
            Dispatcher, path => drives.Describe(path) is { Kind: RootKind.Local }, cacheSize: 600, newestFirst: true);
        _throttle = new RefreshThrottle(RefreshInterval, () => Dispatcher.BeginInvoke(new Action(() => _ = RefreshAsync(true, false))));
        _throttle.Failed += ex => FileLog.Shared.Error("Refreshing the screenshot history failed", ex);
        _searchTimer = new DispatcherTimer(DispatcherPriority.Input, Dispatcher) { Interval = SearchDelay };
        _searchTimer.Tick += (_, _) =>
        {
            _searchTimer.Stop();
            _ = RefreshAsync(false, true);
        };
        _messageTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher) { Interval = MessageTime };
        _messageTimer.Tick += (_, _) =>
        {
            _messageTimer.Stop();
            _message = null;
            UpdateStatus();
        };

        GridView.Focusable = true;
        GridView.FocusVisualStyle = null;
        GridView.RowRealized += OnRowRealized;
        GridView.RowReleased += OnRowReleased;
        GridView.SizeChanged += (_, _) => OnGridSizeChanged();
        GridView.PreviewMouseLeftButtonDown += OnGridMouseDown;
        GridView.PreviewMouseMove += OnGridMouseMove;
        GridView.PreviewMouseLeftButtonUp += (_, _) => _pressTile = null;
        GridView.PreviewMouseRightButtonDown += OnGridRightMouseDown;
        GridView.ContextMenuOpening += (_, e) => e.Handled = !_menuOnTile;
        TileMenu.Closed += (_, _) => _menuOnTile = false;

        SearchBox.TextChanged += (_, _) =>
        {
            SearchPlaceholder.Visibility = SearchBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            _searchTimer.Stop();
            _searchTimer.Start();
        };
        FolderBox.SelectionChanged += OnFolderChanged;
        BannerButton.Click += (_, _) => SwitchOn();
        CopyImageButton.Click += (_, _) => CopyImage();
        CopyTextButton.Click += async (_, _) => await CopyTextAsync();
        OpenButton.Click += async (_, _) => await OpenSelectedAsync(reveal: false);
        RevealButton.Click += async (_, _) => await OpenSelectedAsync(reveal: true);
        MenuCopyImage.Click += (_, _) => CopyImage();
        MenuCopyText.Click += async (_, _) => await CopyTextAsync();
        MenuOpen.Click += async (_, _) => await OpenSelectedAsync(reveal: false);
        MenuReveal.Click += async (_, _) => await OpenSelectedAsync(reveal: true);
        MenuCopyPath.Click += (_, _) => CopyPath();
        PreviewKeyDown += OnKeyDown;

        _services.Index.ImageTextChanged += OnIndexSignal;
        _services.Index.HistoryChanged += OnIndexSignal;
        _services.Settings.Changed += OnSettingsChanged;
        Loaded += (_, _) =>
        {
            _thumbPixels = ThumbnailPixels();
            OnGridSizeChanged();
            SearchBox.Focus();
        };
        Closed += OnClosed;

        UpdateBanner();
        UpdateButtons();
        UpdateEmptyText();
        UpdateStatus();
        ShowPanelPlaceholder(Strings.Get("Hist_SelectImage"));
        _ = RefreshAsync(true, false);
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
        SearchBox.Focus();
    }

    private void FillTexts()
    {
        SearchPlaceholder.Text = Strings.Get("Hist_SearchPlaceholder");
        BannerButton.Content = Strings.Get("Bar_OcrHintEnable");
        CopyImageButton.Content = Strings.Get("Ctx_CopyImage");
        CopyTextButton.Content = Strings.Get("Ctx_CopyText");
        OpenButton.Content = Strings.Get("Ctx_Open");
        RevealButton.Content = Strings.Get("Ctx_Reveal");
        MenuCopyImage.Header = Strings.Get("Ctx_CopyImage");
        MenuCopyText.Header = Strings.Get("Ctx_CopyText");
        MenuOpen.Header = Strings.Get("Ctx_Open");
        MenuReveal.Header = Strings.Get("Ctx_Reveal");
        MenuCopyPath.Header = Strings.Get("Ctx_CopyPath");
        TextHeading.Text = Strings.Get("Hist_TextHeading");
        KeysText.Text = Strings.Get("Hist_Keys");
        FolderBox.DisplayMemberPath = nameof(FolderChoice.Text);
        SetFolderChoices([]);
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _services.Index.ImageTextChanged -= OnIndexSignal;
        _services.Index.HistoryChanged -= OnIndexSignal;
        _services.Settings.Changed -= OnSettingsChanged;
        _closing.Cancel();
        _panelCts?.Cancel();
        _searchTimer.Stop();
        _messageTimer.Stop();
        _throttle.Dispose();
        _thumbs.Dispose();
        foreach (var tile in _tiles.Values)
        {
            tile.IsRealized = false;
            tile.Thumbnail = null;
        }

        _tiles.Clear();
    }

    // ---------------------------------------------------------------- loading

    /// <summary>Any change of the directory or of the image text: a refresh at most every 2 s. Called on background threads.</summary>
    private void OnIndexSignal() => _throttle.Trigger();

    private void OnSettingsChanged(AppSettings settings) => Dispatcher.BeginInvoke(new Action(() =>
    {
        UpdateBanner();
        _throttle.Trigger();
    }));

    /// <summary>
    /// Reads the directory (<paramref name="readDirectory"/>) and applies the search text and the folder filter. One run at a
    /// time; a request during a run makes it go round once more, so the last wish always wins.
    /// </summary>
    private async Task RefreshAsync(bool readDirectory, bool resetScroll)
    {
        _resetScroll |= resetScroll;
        _againRead |= readDirectory;
        if (Interlocked.Exchange(ref _busy, 1) == 1)
        {
            _again = true;
            return;
        }

        try
        {
            do
            {
                _again = false;
                bool read = _againRead;
                _againRead = false;
                var index = _services.Index;
                if (index.History is null)
                {
                    try
                    {
                        await index.LoadedTask;
                    }
                    catch (Exception ex) when (ex is not OutOfMemoryException)
                    {
                        return; // the start-up work failed; reported elsewhere
                    }

                    read = true;
                }

                if (index.History is not { } history || _closing.IsCancellationRequested)
                {
                    return;
                }

                string text = SearchBox.Text;
                string? folder = _folderFilter;
                var current = _snapshot;
                var token = _closing.Token;
                var (snapshot, result) = await Task.Run(() =>
                {
                    var s = read || !_loadedOnce ? history.Load(token) : current;
                    return (s, history.Filter(s, text, folder, token));
                }, token);
                if (_closing.IsCancellationRequested)
                {
                    return;
                }

                Apply(snapshot, result);
            }
            while (_again && !_closing.IsCancellationRequested);
        }
        catch (OperationCanceledException)
        {
            // The window is closing.
        }
        catch (Exception ex)
        {
            FileLog.Shared.Error("Loading the screenshot history failed", ex);
        }
        finally
        {
            Interlocked.Exchange(ref _busy, 0);
        }
    }

    private void Apply(ImageHistorySnapshot snapshot, ImageHistoryResult result)
    {
        _snapshot = snapshot;
        _loadedOnce = true;
        _query = result.Query;
        UpdateFolderChoices(snapshot.Folders);

        bool same = SameList(_shown, result.Images);
        if (!same || _resetScroll || _rows.Count == 0 && result.Images.Count > 0)
        {
            BuildLayout(result.Images, keepPlace: !_resetScroll);
        }

        _resetScroll = false;
        UpdateEmptyText();
        UpdateStatus();
        UpdateButtons();
        _ = ShowPanelAsync();
    }

    private static bool SameList(IReadOnlyList<HistoryImage> a, IReadOnlyList<HistoryImage> b)
    {
        if (a.Count != b.Count)
        {
            return false;
        }

        for (int i = 0; i < a.Count; i++)
        {
            if (a[i].EntryId != b[i].EntryId || a[i].LastWriteUtc != b[i].LastWriteUtc
                || !string.Equals(a[i].FullPath, b[i].FullPath, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Builds days, rows and the position map for the images and gives them to the grid. The place is kept: the image at the
    /// top of the view before stays there (unless the view was at the very top, then new images show). Tiles of images that
    /// stay are kept, so their previews and the selection survive.
    /// </summary>
    private void BuildLayout(IReadOnlyList<HistoryImage> images, bool keepPlace)
    {
        var anchor = keepPlace ? GridView.Map.AnchorAt(GridView.VerticalOffset) : null;
        _shown = images;

        var present = new HashSet<int>(images.Count);
        foreach (var image in images)
        {
            present.Add(image.EntryId);
            if (_tiles.TryGetValue(image.EntryId, out var tile)
                && tile.Image.LastWriteUtc == image.LastWriteUtc
                && string.Equals(tile.Image.FullPath, image.FullPath, StringComparison.Ordinal))
            {
                tile.Image = image;
                continue;
            }

            if (tile is not null)
            {
                tile.IsRealized = false;
                tile.Thumbnail = null;
            }

            _tiles[image.EntryId] = new HistoryTileItem(image) { IsSelected = image.EntryId == _selectedId };
        }

        foreach (int id in _tiles.Keys.Where(id => !present.Contains(id)).ToList())
        {
            _tiles[id].IsRealized = false;
            _tiles[id].Thumbnail = null;
            _tiles.Remove(id);
        }

        if (_selectedId is { } selected && !present.Contains(selected))
        {
            _selectedId = null; // the image left the list (deleted, moved or filtered out)
            _panelKey = null;
        }

        var days = HistoryLayout.GroupByDay(images, TimeZoneInfo.Local);
        _rows = HistoryLayout.BuildRows(days, _columns);
        var map = new HistoryRowMap(_rows, HeaderHeight, TileRowHeight);
        var today = HistoryLayout.Today(TimeProvider.System, TimeZoneInfo.Local);
        GridView.SetLayout(map, row => ContentOf(row, today), map.OffsetOf(anchor));
    }

    private object ContentOf(HistoryRow row, DateOnly today)
    {
        if (row is HistoryHeaderRow header)
        {
            string name = HistoryLayout.KindOf(header.Day, today) switch
            {
                HistoryDayKind.Today => Strings.Get("Hist_Today"),
                HistoryDayKind.Yesterday => Strings.Get("Hist_Yesterday"),
                _ => header.Day.ToString("D", CultureInfo.CurrentCulture),
            };
            return new HistoryHeaderItem(LocationUi.Format("Hist_DayHeader", name, header.Count));
        }

        var tiles = ((HistoryTileRow)row).Images
            .Select(i => _tiles.TryGetValue(i.EntryId, out var t) ? t : null)
            .OfType<HistoryTileItem>()
            .ToList();
        return new HistoryTileRowItem(tiles);
    }

    private void OnGridSizeChanged()
    {
        double width = GridView.ActualWidth - SystemParameters.VerticalScrollBarWidth - 10;
        int columns = HistoryLayout.ColumnsFor(width, TileWidth);
        if (columns == _columns)
        {
            return;
        }

        _columns = columns;
        if (_shown.Count > 0)
        {
            BuildLayout(_shown, keepPlace: true);
        }
    }

    private int ThumbnailPixels()
    {
        double scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
        return (int)Math.Ceiling(ImageAreaDip * scale / 32.0) * 32;
    }

    // ---------------------------------------------------------------- previews

    private void OnRowRealized(HistoryRow row)
    {
        if (row is not HistoryTileRow tiles)
        {
            return;
        }

        foreach (var image in tiles.Images)
        {
            if (_tiles.TryGetValue(image.EntryId, out var tile))
            {
                tile.IsRealized = true;
                RequestThumbnail(tile);
            }
        }
    }

    private void OnRowReleased(HistoryRow row)
    {
        if (row is not HistoryTileRow tiles)
        {
            return;
        }

        // The preview is let go; Thumbnails keeps the latest ones, so scrolling back is quick.
        foreach (var image in tiles.Images)
        {
            if (_tiles.TryGetValue(image.EntryId, out var tile))
            {
                tile.IsRealized = false;
                tile.Thumbnail = null;
            }
        }
    }

    private void RequestThumbnail(HistoryTileItem tile)
    {
        var image = tile.Image;
        if (tile.Thumbnail is not null || !image.CanThumbnail)
        {
            return; // a cloud-only image keeps its placeholder: Windows would have to download it
        }

        _thumbs.Request(
            image.FullPath, image.LastWriteUtc, _thumbPixels,
            bitmap =>
            {
                if (tile.IsRealized)
                {
                    tile.Thumbnail = bitmap;
                }
            },
            () => tile.IsRealized);
    }

    // ---------------------------------------------------------------- filter and search

    private void OnFolderChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updatingFolders || FolderBox.SelectedItem is not FolderChoice choice || choice.Path == _folderFilter)
        {
            return;
        }

        _folderFilter = choice.Path;
        _ = RefreshAsync(false, true);
    }

    private void UpdateFolderChoices(IReadOnlyList<string> folders)
    {
        var current = FolderBox.Items.OfType<FolderChoice>().Skip(1).Select(c => c.Path).ToList();
        if (current.SequenceEqual(folders, StringComparer.OrdinalIgnoreCase))
        {
            return;
        }

        SetFolderChoices(folders);
        if (_folderFilter is not null && !folders.Contains(_folderFilter, StringComparer.OrdinalIgnoreCase))
        {
            _folderFilter = null;
            _resetScroll = true;
            _again = true; // the filter changed: once more without it
        }
    }

    private void SetFolderChoices(IReadOnlyList<string> folders)
    {
        _updatingFolders = true;
        try
        {
            FolderBox.Items.Clear();
            FolderBox.Items.Add(new FolderChoice(null, Strings.Get("Hist_AllFolders")));
            FolderChoice? selected = null;
            foreach (string folder in folders)
            {
                var choice = new FolderChoice(folder, PathShortener.ShortenMiddle(folder, 36));
                FolderBox.Items.Add(choice);
                if (string.Equals(folder, _folderFilter, StringComparison.OrdinalIgnoreCase))
                {
                    selected = choice;
                }
            }

            FolderBox.SelectedItem = selected ?? FolderBox.Items[0];
            FolderBox.IsEnabled = folders.Count > 1;
        }
        finally
        {
            _updatingFolders = false;
        }
    }

    private sealed record FolderChoice(string? Path, string Text);

    // ---------------------------------------------------------------- banner, empty state, status

    private bool OcrOn => _services.Settings.Current.OcrEnabled;

    private void UpdateBanner()
    {
        bool on = OcrOn;
        SearchBox.IsEnabled = on;
        if (!on)
        {
            BannerText.Text = Strings.Get("Hist_OffHint");
            BannerButton.Visibility = Visibility.Visible;
            Banner.Visibility = Visibility.Visible;
        }
        else if (_services.Index.ImageText is { RecognizerAvailable: false })
        {
            BannerText.Text = Strings.Get("Footer_ImageNoLanguage");
            BannerButton.Visibility = Visibility.Collapsed;
            Banner.Visibility = Visibility.Visible;
        }
        else
        {
            Banner.Visibility = Visibility.Collapsed;
        }
    }

    /// <summary>"Switch on" of the banner: the same as in the search bar (the folders in force stay).</summary>
    private void SwitchOn()
    {
        try
        {
            _services.Settings.Update(s => s with
            {
                OcrEnabled = true,
                OcrFolders = s.OcrFolders ?? ImageTextFolders.Effective(s, KnownFolders.Screenshots),
                OcrHintDismissed = true,
            });
            ShowMessage(Strings.Get("Ocr_MsgOn"));
        }
        catch (Exception ex)
        {
            FileLog.Shared.Error("Switching the image text on failed", ex);
        }

        UpdateBanner();
    }

    private void UpdateEmptyText()
    {
        string? text = null;
        if (!_loadedOnce)
        {
            text = Strings.Get("Hist_Loading");
        }
        else if (_shown.Count == 0)
        {
            text = _snapshot.Folders.Count == 0
                ? Strings.Get("Hist_NoFolders")
                : Strings.Get(_snapshot.Images.Count == 0 ? "Hist_Empty" : "Hist_NoMatch");
        }

        EmptyText.Text = text ?? string.Empty;
        EmptyText.Visibility = text is null ? Visibility.Collapsed : Visibility.Visible;
    }

    private void UpdateStatus()
    {
        if (_message is not null)
        {
            StatusText.Text = _message;
            return;
        }

        if (!_loadedOnce)
        {
            StatusText.Text = Strings.Get("Hist_Loading");
            return;
        }

        string count = _shown.Count == _snapshot.Images.Count
            ? LocationUi.Format("Hist_Count", _shown.Count)
            : LocationUi.Format("Hist_CountOf", _shown.Count, _snapshot.Images.Count);
        if (OcrOn && _services.Index.ImageText is { Enabled: true } service)
        {
            var progress = service.Progress;
            count += " · " + LocationUi.Format("Ocr_Progress", progress.Recognized, progress.Total);
        }

        StatusText.Text = count;
    }

    private void ShowMessage(string text)
    {
        _message = text;
        StatusText.Text = text;
        _messageTimer.Stop();
        _messageTimer.Start();
    }

    // ---------------------------------------------------------------- selection

    private HistoryImage? SelectedImage
        => _selectedId is { } id && _tiles.TryGetValue(id, out var tile) ? tile.Image : null;

    private void Select(int? id, bool reveal)
    {
        if (id is { } wanted && !_tiles.ContainsKey(wanted))
        {
            id = null;
        }

        if (_selectedId != id)
        {
            if (_selectedId is { } old && _tiles.TryGetValue(old, out var oldTile))
            {
                oldTile.IsSelected = false;
            }

            _selectedId = id;
            if (id is { } now && _tiles.TryGetValue(now, out var tile))
            {
                tile.IsSelected = true;
            }

            UpdateButtons();
            _ = ShowPanelAsync();
        }

        if (reveal && _selectedId is { } shown)
        {
            GridView.Reveal(GridView.Map.RowOfImage(shown));
        }
    }

    private void UpdateButtons()
    {
        var image = SelectedImage;
        bool any = image is not null;
        CopyImageButton.IsEnabled = image is { CanCopyImage: true };
        CopyTextButton.IsEnabled = image is { IsCloud: false };
        OpenButton.IsEnabled = any;
        RevealButton.IsEnabled = any;
        MenuCopyImage.IsEnabled = CopyImageButton.IsEnabled;
        MenuCopyText.IsEnabled = CopyTextButton.IsEnabled;
        MenuOpen.IsEnabled = any;
        MenuReveal.IsEnabled = any;
        MenuCopyPath.IsEnabled = any;
    }

    private static HistoryTileItem? TileAt(DependencyObject? source)
    {
        while (source is not null)
        {
            if (source is FrameworkElement { DataContext: HistoryTileItem tile })
            {
                return tile;
            }

            if (source is FrameworkContentElement { DataContext: HistoryTileItem contentTile })
            {
                return contentTile;
            }

            source = source is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(source)
                : LogicalTreeHelper.GetParent(source);
        }

        return null;
    }

    private void OnGridMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (TileAt(e.OriginalSource as DependencyObject) is not { } tile)
        {
            return;
        }

        _pressTile = tile;
        _pressPoint = e.GetPosition(this);
        Select(tile.Image.EntryId, reveal: false);
        GridView.Focus();
        if (e.ClickCount == 2)
        {
            _pressTile = null;
            _ = OpenSelectedAsync(reveal: false);
        }
    }

    private void OnGridRightMouseDown(object sender, MouseButtonEventArgs e)
    {
        var tile = TileAt(e.OriginalSource as DependencyObject);
        _menuOnTile = tile is not null;
        if (tile is not null)
        {
            Select(tile.Image.EntryId, reveal: false);
            GridView.Focus();
        }
    }

    /// <summary>Pulling a tile out of the window hands the image file to the target (a mail, a ticket): a file drop.</summary>
    private void OnGridMouseMove(object sender, MouseEventArgs e)
    {
        if (_pressTile is not { } tile || e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        var position = e.GetPosition(this);
        if (Math.Abs(position.X - _pressPoint.X) < SystemParameters.MinimumHorizontalDragDistance
            && Math.Abs(position.Y - _pressPoint.Y) < SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }

        _pressTile = null;
        string path = tile.Image.FullPath;
        if (!File.Exists(path))
        {
            ShowMessage(Strings.Get("Footer_Vanished"));
            _throttle.Trigger();
            return;
        }

        var data = new DataObject(DataFormats.FileDrop, new[] { path });
        DragDrop.DoDragDrop(GridView, data, DragDropEffects.Copy);
    }

    // ---------------------------------------------------------------- keys

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        var focus = Keyboard.FocusedElement as DependencyObject;
        bool inSearch = ReferenceEquals(focus, SearchBox);
        bool inText = focus is DependencyObject d && (ReferenceEquals(d, TextView) || IsInside(d, TextView));
        bool onControl = focus is Button or ComboBox or ComboBoxItem or MenuItem || inText;
        var modifiers = Keyboard.Modifiers;
        bool ctrl = (modifiers & ModifierKeys.Control) != 0;
        bool shift = (modifiers & ModifierKeys.Shift) != 0;
        bool alt = (modifiers & ModifierKeys.Alt) != 0;
        if (alt)
        {
            return;
        }

        var key = e.Key;
        switch (key)
        {
            case Key.Escape:
                if (focus is ComboBox or ComboBoxItem or MenuItem)
                {
                    return; // closes the drop-down or the menu
                }

                e.Handled = true;
                if (inSearch && SearchBox.Text.Length > 0)
                {
                    SearchBox.Clear();
                }
                else
                {
                    Close();
                }

                return;
            case Key.C when ctrl && shift:
                if (!inText)
                {
                    e.Handled = true;
                    _ = CopyTextAsync();
                }

                return;
            case Key.C when ctrl:
                if (inText || (inSearch && SearchBox.SelectionLength > 0))
                {
                    return; // the text that is marked
                }

                e.Handled = true;
                CopyImage();
                return;
            case Key.Enter when !onControl:
                e.Handled = true;
                _ = OpenSelectedAsync(reveal: ctrl);
                return;
        }

        if (onControl || ctrl)
        {
            return;
        }

        HistoryMove? move = key switch
        {
            Key.Left when !inSearch => HistoryMove.Left,
            Key.Right when !inSearch => HistoryMove.Right,
            Key.Up => HistoryMove.Up,
            Key.Down => HistoryMove.Down,
            Key.PageUp => HistoryMove.PageUp,
            Key.PageDown => HistoryMove.PageDown,
            Key.Home when !inSearch => HistoryMove.Home,
            Key.End when !inSearch => HistoryMove.End,
            _ => null,
        };
        if (move is not { } m)
        {
            return;
        }

        e.Handled = true;
        int pageRows = Math.Max(1, (int)(GridView.ViewHeight / TileRowHeight) - 1);
        if (HistoryLayout.Move(_rows, _selectedId, m, pageRows) is { } next)
        {
            Select(next, reveal: true);
        }
    }

    private static bool IsInside(DependencyObject child, DependencyObject ancestor)
    {
        for (var current = child; current is not null;
             current = current is Visual or System.Windows.Media.Media3D.Visual3D
                 ? VisualTreeHelper.GetParent(current)
                 : LogicalTreeHelper.GetParent(current))
        {
            if (ReferenceEquals(current, ancestor))
            {
                return true;
            }
        }

        return false;
    }

    // ---------------------------------------------------------------- actions

    private void CopyImage()
    {
        if (SelectedImage is not { } image)
        {
            return;
        }

        if (!image.CanCopyImage)
        {
            ShowMessage(Strings.Get("Hist_CloudNoCopy")); // decoding it would start a download
            return;
        }

        string? error = ShellActions.CopyImage(image.FullPath);
        ShowMessage(error is null
            ? Strings.Get("Footer_ImageCopied")
            : LocationUi.Format("Footer_ImageCopyFailed", error));
    }

    private void CopyPath()
    {
        if (SelectedImage is { } image)
        {
            ShowMessage(Strings.Get(ShellActions.CopyText(image.FullPath) ? "Footer_Copied" : "Footer_CopyFailed"));
        }
    }

    private async Task CopyTextAsync()
    {
        if (SelectedImage is not { IsCloud: false } image)
        {
            return;
        }

        string? text = await LoadTextAsync(image, CancellationToken.None);
        if (string.IsNullOrWhiteSpace(text))
        {
            ShowMessage(Strings.Get("Footer_NoTextToCopy"));
            return;
        }

        ShowMessage(Strings.Get(ShellActions.CopyText(text.Replace("\n", Environment.NewLine, StringComparison.Ordinal))
            ? "Footer_TextCopied" : "Footer_CopyFailed"));
    }

    /// <summary>Opens the image (or shows it in Explorer) after the same check as the search bar: a gone image leaves the directory.</summary>
    private async Task OpenSelectedAsync(bool reveal)
    {
        if (SelectedImage is not { } image || _services.Index.Store is not { } store)
        {
            return;
        }

        try
        {
            string path = image.FullPath;
            var roots = store.GetRoots().Select(r => r.Path).ToList();
            var index = _services.Index;
            var result = await Task.Run(() => ExistenceCheck.Evaluate(path, roots, FileSystemPathProbe.Instance, index.IsLocationPathUnreachable));
            switch (result)
            {
                case ExistenceResult.Exists:
                    string? error = reveal ? ShellActions.RevealInExplorer(path) : ShellActions.Open(path);
                    if (error is not null)
                    {
                        ShowMessage(LocationUi.Format("Footer_OpenFailed", error));
                    }
                    else if (!reveal && index.Usage is { } usage)
                    {
                        _ = Task.Run(() =>
                        {
                            try
                            {
                                usage.RecordOpen(path);
                            }
                            catch (Exception ex)
                            {
                                FileLog.Shared.Error("Recording the opening failed", ex);
                            }
                        });
                    }

                    break;
                case ExistenceResult.Vanished:
                    await Task.Run(() => store.DeleteSubtree(image.EntryId));
                    ShowMessage(Strings.Get("Footer_Vanished"));
                    _throttle.Trigger();
                    break;
                default:
                    ShowMessage(Strings.Get("Footer_Unreachable"));
                    break;
            }
        }
        catch (Exception ex)
        {
            FileLog.Shared.Error("Opening an image of the history failed", ex);
            ShowMessage(LocationUi.Format("Footer_OpenFailed", ex.Message));
        }
    }

    // ---------------------------------------------------------------- text panel

    /// <summary>
    /// The recognized text of the image from the database; null if it is not read yet or the image changed since
    /// (the stored text then belongs to an older version of the file).
    /// </summary>
    private async Task<string?> LoadTextAsync(HistoryImage image, CancellationToken token)
    {
        if (_services.Index.Store is not { } store)
        {
            return null;
        }

        long seconds = IndexStore.ToUnixSeconds(DateTime.SpecifyKind(image.LastWriteUtc, DateTimeKind.Utc));
        return await Task.Run(() =>
        {
            token.ThrowIfCancellationRequested();
            return store.GetImageTexts([image.EntryId]).TryGetValue(image.EntryId, out var row) && row.MatchesEntry(image.Size, seconds)
                ? row.Text
                : null;
        }, token);
    }

    private async Task ShowPanelAsync()
    {
        _panelCts?.Cancel();
        var cts = new CancellationTokenSource();
        _panelCts = cts;
        try
        {
            if (SelectedImage is not { } image)
            {
                PanelName.Text = string.Empty;
                PanelDate.Text = string.Empty;
                PanelFolder.Text = string.Empty;
                if (_panelKey != "none")
                {
                    _panelKey = "none";
                    ShowPanelPlaceholder(Strings.Get("Hist_SelectImage"));
                }

                return;
            }

            PanelName.Text = image.Name;
            PanelDate.Text = image.LastWriteUtc.ToLocalTime().ToString("g", CultureInfo.CurrentCulture);
            PanelFolder.Text = Path.GetDirectoryName(image.FullPath) ?? string.Empty;
            PanelFolder.ToolTip = image.FullPath;

            string? text = null;
            string state;
            if (image.IsCloud)
            {
                state = Strings.Get("Hist_CloudNote");
            }
            else if (!OcrOn)
            {
                state = Strings.Get("Hist_TextOff");
            }
            else
            {
                text = await LoadTextAsync(image, cts.Token);
                state = text is null ? Strings.Get("Hist_NotReadYet") : Strings.Get("Hit_NoText");
            }

            if (cts.IsCancellationRequested)
            {
                return;
            }

            // The document is only rebuilt when something shows differently, so a text the user is marking stays marked
            // while the list refreshes.
            string terms = string.Join('\u001f', _query.Terms);
            string key = $"{image.EntryId}|{image.LastWriteUtc.Ticks}|{terms}|{state}|{text}";
            if (key == _panelKey)
            {
                return;
            }

            _panelKey = key;
            if (string.IsNullOrWhiteSpace(text))
            {
                ShowPanelPlaceholder(state);
            }
            else
            {
                ShowPanelText(text, _query);
            }
        }
        catch (OperationCanceledException)
        {
            // A newer selection took over.
        }
        catch (Exception ex)
        {
            FileLog.Shared.Error("Showing the recognized text failed", ex);
        }
    }

    private void ShowPanelPlaceholder(string text)
    {
        var paragraph = new Paragraph(new Run(text)) { Foreground = (Brush)FindResource("MutedBrush") };
        TextView.Document = new FlowDocument(paragraph) { PagePadding = new Thickness(0) };
    }

    /// <summary>The whole text, line by line, with the search terms marked; selectable, so it can be copied.</summary>
    private void ShowPanelText(string text, SearchQuery query)
    {
        var paragraph = new Paragraph { Margin = new Thickness(0) };
        var highlight = (Brush)FindResource("AccentBrush");
        var highlightBackground = (Brush)FindResource("AccentSoftBrush");
        foreach (var segment in MatchHighlighter.Split(text.Replace("\r", string.Empty, StringComparison.Ordinal), query))
        {
            string[] lines = segment.Text.Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                if (i > 0)
                {
                    paragraph.Inlines.Add(new LineBreak());
                }

                if (lines[i].Length == 0)
                {
                    continue;
                }

                var run = new Run(lines[i]);
                if (segment.Highlighted)
                {
                    run.FontWeight = FontWeights.SemiBold;
                    run.Foreground = highlight;
                    run.Background = highlightBackground;
                }

                paragraph.Inlines.Add(run);
            }
        }

        TextView.Document = new FlowDocument(paragraph) { PagePadding = new Thickness(0) };
    }
}
