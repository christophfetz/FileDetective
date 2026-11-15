using System.Globalization;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using FileDetective.App.Interop;
using FileDetective.Core;

namespace FileDetective.App.ViewModels;

/// <summary>
/// State and actions of the search bar (PLAN.md 8.4): input, mode, hit list, selection, footer. Searching goes through
/// <see cref="SearchCoordinator"/> (30 ms debounce, cancel previous, thread pool); results are applied on the UI thread.
/// </summary>
internal sealed partial class SearchBarViewModel : ObservableObject
{
    /// <summary>Seconds a path lookup may take before the location counts as not reachable (PLAN.md 7.7: 5 s).</summary>
    private static readonly TimeSpan ExistenceTimeout = TimeSpan.FromSeconds(5);

    /// <summary>Characters of the scanned location shown in the footer before it is cut in the middle.</summary>
    private const int ScanPathDisplayLength = 40;

    private readonly IndexService _index;
    private readonly SettingsService _settings;
    private readonly Thumbnails _thumbnails;
    private readonly Dispatcher _dispatcher;
    private readonly Debouncer _imageTextRefresh;
    private readonly SearchCoordinator _coordinator;
    private long _appliedGeneration;
    private int _total;
    private bool _queryEmpty = true;
    private string? _transientMessage;
    private bool _busy;
    private bool _recentApplied;

    /// <summary>The parsed input of each request still on its way (UI thread only), to highlight with the right terms.</summary>
    private readonly Dictionary<long, SearchQuery> _requested = [];

    [ObservableProperty]
    private string _input = string.Empty;

    [ObservableProperty]
    private SearchMode _mode = SearchMode.All;

    [ObservableProperty]
    private IReadOnlyList<HitItem> _hits = [];

    [ObservableProperty]
    private int _selectedIndex = -1;

    [ObservableProperty]
    private string _footerText = string.Empty;

    public SearchBarViewModel(IndexService index, SettingsService settings, Thumbnails thumbnails, Dispatcher dispatcher)
    {
        _index = index;
        _settings = settings;
        _thumbnails = thumbnails;
        _dispatcher = dispatcher;
        _coordinator = new SearchCoordinator(
            RunSearch,
            (generation, result) => _dispatcher.BeginInvoke(new Action(() => Apply(generation, result))),
            ex => FileLog.Shared.Error("Search failed", ex));
        _index.StateChanged += () => _dispatcher.BeginInvoke(new Action(OnIndexStateChanged));
        // While images are read, the progress changes many times a second; the list follows at most twice a second.
        _imageTextRefresh = new Debouncer(TimeSpan.FromMilliseconds(500), () => _dispatcher.BeginInvoke(new Action(OnImageTextChanged)));
        _index.ImageTextChanged += _imageTextRefresh.Trigger;
        _settings.Changed += _ => _dispatcher.BeginInvoke(new Action(OnSettingsChanged));
        FooterText = BuildFooter();
    }

    /// <summary>Pixels per device-independent unit of the bar (for sharp previews); set by the window.</summary>
    public double PixelsPerDip { get; set; } = 1.0;

    /// <summary>Raised after a successful open or show in Explorer: the bar should hide.</summary>
    public event Action? CloseRequested;

    public string PlaceholderText => Strings.Get("Bar_Placeholder");

    public string ModeLabel => Strings.Get(Mode switch
    {
        SearchMode.Files => "Mode_Files",
        SearchMode.Folders => "Mode_Folders",
        SearchMode.Programs => "Mode_Programs",
        SearchMode.ImageText => "Mode_ImageText",
        _ => "Mode_All",
    });

    /// <summary>The image text is switched on: mode Image text is part of the Tab cycle (PLAN.md 11.3).</summary>
    public bool ImageTextOn => _settings.Current.OcrEnabled;

    /// <summary>
    /// The one-time hint "Search the text in screenshots? Switch on" in mode All (PLAN.md 11.3). Gone for good after
    /// switching on or dismissing it.
    /// </summary>
    public bool ShowImageTextHint
        => Mode == SearchMode.All && _settings.Current is { OcrEnabled: false, OcrHintDismissed: false } && !_settings.IsReadOnly;

    public string ImageTextHintText => Strings.Get("Bar_OcrHint");

    public string ImageTextHintEnable => Strings.Get("Bar_OcrHintEnable");

    public string ImageTextHintDismiss => Strings.Get("Bar_OcrHintDismiss");

    /// <summary>"Switch on" of the hint: the image text starts for the Screenshots folder (or the folders already chosen).</summary>
    public void EnableImageText()
    {
        try
        {
            _settings.Update(s => s with
            {
                OcrEnabled = true,
                OcrFolders = s.OcrFolders ?? ImageTextFolders.Effective(s, KnownFolders.Screenshots),
                OcrHintDismissed = true,
            });
            SetTransient(Strings.Get("Footer_OcrEnabled"));
        }
        catch (Exception ex)
        {
            FileLog.Shared.Error("Switching the image text on failed", ex);
        }

        OnPropertyChanged(nameof(ShowImageTextHint));
    }

    /// <summary>The cross of the hint: it never comes back.</summary>
    public void DismissImageTextHint()
    {
        try
        {
            _settings.Update(s => s with { OcrHintDismissed = true });
        }
        catch (Exception ex)
        {
            FileLog.Shared.Error("Saving the settings failed", ex);
        }

        OnPropertyChanged(nameof(ShowImageTextHint));
    }

    /// <summary>The selected row is an image of mode Image text.</summary>
    public bool SelectedIsImage => SelectedItem is ImageHitItem;

    public string TabHint => Strings.Get("Bar_TabKey");

    public bool HasHits => Hits.Count > 0;

    /// <summary>The selected row or null.</summary>
    public HitItem? SelectedItem => SelectedIndex >= 0 && SelectedIndex < Hits.Count ? Hits[SelectedIndex] : null;

    /// <summary>Open and Show in Explorer are possible: a row is selected and its location is not known as unreachable.</summary>
    public bool CanOpenSelected => SelectedItem is { IsUnreachable: false };

    partial void OnInputChanged(string value)
    {
        _transientMessage = null;
        StartSearch();
    }

    partial void OnModeChanged(SearchMode value)
    {
        OnPropertyChanged(nameof(ModeLabel));
        OnPropertyChanged(nameof(ShowImageTextHint));
        _transientMessage = null;
        StartSearch();
    }

    partial void OnHitsChanged(IReadOnlyList<HitItem> value) => OnPropertyChanged(nameof(HasHits));

    partial void OnSelectedIndexChanged(int value)
    {
        OnPropertyChanged(nameof(SelectedItem));
        OnPropertyChanged(nameof(CanOpenSelected));
        OnPropertyChanged(nameof(SelectedIsImage));
    }

    /// <summary>Called each time the bar is shown: refreshes the results (the directory may have grown) and the footer.</summary>
    public void OnShown()
    {
        _transientMessage = null;

        // PLAN.md 7.7: check the locations again if the last check is older than 60 s; in the background, the bar
        // does not wait for it. A change re-runs the search through StateChanged.
        _index.OnBarShown();
        StartSearch();
    }

    /// <summary>Called about once a second while the bar is visible: progress of the first scan or of a running scan.</summary>
    public void Tick()
    {
        if (_index.Phase == IndexPhase.Ready)
        {
            if (_index.CurrentScan is not null)
            {
                RefreshFooter();
            }

            return;
        }

        if (_index.Phase == IndexPhase.FirstScan && !_queryEmpty)
        {
            StartSearch(); // new entries may have arrived
        }

        RefreshFooter();
    }

    /// <summary>Tab / Shift+Tab: next or previous mode.</summary>
    public void CycleMode(bool reverse) => Mode = BarState.CycleMode(Mode, reverse, ImageTextOn);

    public void MoveSelection(int delta)
    {
        if (Hits.Count == 0)
        {
            return;
        }

        SelectedIndex = Math.Clamp((SelectedIndex < 0 ? 0 : SelectedIndex) + delta, 0, Hits.Count - 1);
    }

    /// <summary>Enter / double click.</summary>
    public Task OpenSelectedAsync() => ActOnSelectedAsync(HitAction.Open);

    /// <summary>Context menu "Open with …" (stage 13): the Windows dialog to choose an app; files only.</summary>
    public Task OpenSelectedWithAsync() => SelectedItem is { IsDirectory: false } ? ActOnSelectedAsync(HitAction.OpenWith) : Task.CompletedTask;

    /// <summary>Ctrl+Enter.</summary>
    public Task RevealSelectedAsync() => ActOnSelectedAsync(HitAction.Reveal);

    /// <summary>Ctrl+C with no text selected in the input box, and the context menu.</summary>
    public void CopySelectedPath()
    {
        if (SelectedItem is not { } item)
        {
            return;
        }

        SetTransient(Strings.Get(ShellActions.CopyText(item.FullPath) ? "Footer_Copied" : "Footer_CopyFailed"));
    }

    /// <summary>Ctrl+C on an image of mode Image text: the image itself (PLAN.md 11.3).</summary>
    public void CopySelectedImage()
    {
        if (SelectedItem is not ImageHitItem item)
        {
            return;
        }

        string? error = ShellActions.CopyImage(item.FullPath);
        SetTransient(error is null
            ? Strings.Get("Footer_ImageCopied")
            : string.Format(CultureInfo.CurrentCulture, Strings.Get("Footer_ImageCopyFailed"), error));
    }

    /// <summary>Ctrl+Shift+C on an image of mode Image text: the recognized text.</summary>
    public async Task CopySelectedTextAsync()
    {
        if (SelectedItem is not ImageHitItem item || _index.Store is not { } store)
        {
            return;
        }

        int id = item.EntryId;
        string text;
        try
        {
            text = await Task.Run(() => store.GetImageTexts([id]).TryGetValue(id, out var row) ? row.Text : string.Empty);
        }
        catch (Exception ex)
        {
            FileLog.Shared.Error("Reading the image text failed", ex);
            text = string.Empty;
        }

        if (text.Length == 0)
        {
            SetTransient(Strings.Get("Footer_NoTextToCopy"));
            return;
        }

        SetTransient(Strings.Get(ShellActions.CopyText(text.Replace("\n", Environment.NewLine, StringComparison.Ordinal))
            ? "Footer_TextCopied" : "Footer_CopyFailed"));
    }

    private void OnSettingsChanged()
    {
        // Switched off while in mode Image text: back to All; the mode is no longer in the cycle.
        if (!ImageTextOn && Mode == SearchMode.ImageText)
        {
            Mode = SearchMode.All;
        }

        OnPropertyChanged(nameof(ShowImageTextHint));
    }

    private void OnImageTextChanged()
    {
        // New texts arrive while the bar shows the image text: the list follows; otherwise only the footer.
        if (Mode == SearchMode.ImageText)
        {
            StartSearch();
        }
        else
        {
            RefreshFooter();
        }
    }

    private void StartSearch()
    {
        var query = SearchQuery.Parse(Input, Mode);
        _queryEmpty = query.IsEmpty;

        // PLAN.md 6.4: an empty input shows the entries opened last through the tool (stage 7). Nothing to show before
        // the directory is loaded; the footer says so, and the list fills when loading is done (OnIndexStateChanged).
        if (_queryEmpty && !_index.IsLoaded)
        {
            _coordinator.CancelPending();
            _requested.Clear();
            _total = 0;
            Hits = [];
            SelectedIndex = -1;
            RefreshFooter();
            return;
        }

        long generation = _coordinator.Request(Input, Mode);
        _requested[generation] = query; // Apply highlights with the query of this very request
        RefreshFooter();
    }

    private SearchResult RunSearch(string input, SearchMode mode, CancellationToken token)
    {
        var engine = _index.Engine;
        if (engine is null)
        {
            return SearchResult.Empty;
        }

        return SearchQuery.Parse(input, mode).IsEmpty
            ? engine.RecentlyOpened(mode, SearchEngine.MaxResults, token)
            : engine.Search(input, mode, token);
    }

    private void Apply(long generation, SearchResult result)
    {
        if (generation < _appliedGeneration)
        {
            return;
        }

        _appliedGeneration = generation;
        if (!_requested.TryGetValue(generation, out var query))
        {
            return;
        }

        foreach (long old in _requested.Keys.Where(g => g <= generation).ToList())
        {
            _requested.Remove(old);
        }

        if (query.IsEmpty != _queryEmpty)
        {
            return; // the input changed while this search was on its way; the newer request delivers
        }

        var items = new List<HitItem>(result.Hits.Count);
        foreach (var hit in result.Hits)
        {
            items.Add(HitItem.From(hit, _index.IsLocationUnreachable(hit.Details.RootId), query.IsEmpty ? null : query));
        }

        // Previews of image rows load in the background, newest list first (PLAN.md 11.3).
        _thumbnails.CancelPending();
        int pixels = (int)Math.Round(Thumbnails.Size * PixelsPerDip);
        foreach (var image in items.OfType<ImageHitItem>())
        {
            var row = image;
            _thumbnails.Request(row.FullPath, row.LastWriteUtc, pixels, source => row.Thumbnail = source);
        }

        _recentApplied = _queryEmpty;

        // Keep the selected entry when the list is refreshed for the same input (e.g. while the first scan runs).
        int keep = -1;
        if (SelectedItem is { } previous)
        {
            keep = items.FindIndex(i => i.EntryId == previous.EntryId);
        }

        _total = result.TotalCount;
        Hits = items;
        SelectedIndex = items.Count == 0 ? -1 : Math.Max(keep, 0);
        RefreshFooter();
    }

    private void OnIndexStateChanged()
    {
        // With input the list follows the directory. With an empty input the recently opened list is built once after
        // loading and again each time the bar is shown, not on every scan or watcher event.
        if (!_queryEmpty || (_index.IsLoaded && !_recentApplied))
        {
            StartSearch();
        }

        RefreshFooter();
    }

    private enum HitAction
    {
        Open,
        OpenWith,
        Reveal,
    }

    private async Task ActOnSelectedAsync(HitAction action)
    {
        bool open = action != HitAction.Reveal;
        if (_busy || SelectedItem is not { } item || _index.Store is not { } store)
        {
            return;
        }

        if (item.IsUnreachable)
        {
            // PLAN.md 8.4: for unreachable locations only "Copy path" is possible.
            SetTransient(Strings.Get("Footer_NotConnected"));
            return;
        }

        _busy = true;
        try
        {
            string path = item.FullPath;
            var check = Task.Run(() => ExistenceCheck.Evaluate(
                path, [.. store.GetRoots().Select(r => r.Path)], FileSystemPathProbe.Instance, _index.IsLocationPathUnreachable));
            var finished = await Task.WhenAny(check, Task.Delay(ExistenceTimeout));
            var result = finished == check ? await check : ExistenceResult.Unknown;
            if (result == ExistenceResult.Unknown)
            {
                // Maybe the location went away: check it, so its hits are greyed out if so.
                _index.RecheckLocation(item.RootId);
            }

            switch (result)
            {
                case ExistenceResult.Exists:
                    string? error = null;
                    if (action == HitAction.OpenWith)
                    {
                        // The dialog is modal: the bar goes first, the chosen app then comes to the front.
                        CloseRequested?.Invoke();
                        if (!ShellActions.OpenWith(IntPtr.Zero, path, out error))
                        {
                            if (error is not null)
                            {
                                FileLog.Shared.Error($"Open with failed: {error}");
                            }

                            break;
                        }
                    }
                    else
                    {
                        error = action == HitAction.Open ? ShellActions.Open(path) : ShellActions.RevealInExplorer(path);
                    }

                    if (error is not null)
                    {
                        SetTransient(string.Format(CultureInfo.CurrentCulture, Strings.Get("Footer_OpenFailed"), error));
                        break;
                    }

                    if (open && _index.Usage is { } usage)
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

                    CloseRequested?.Invoke();
                    break;

                case ExistenceResult.Vanished:
                    // Parent folder and location are readable, only the entry is gone: remove it from the directory.
                    await Task.Run(() => store.DeleteSubtree(item.EntryId));
                    SetTransient(Strings.Get("Footer_Vanished"));
                    StartSearch();
                    break;

                default:
                    SetTransient(Strings.Get("Footer_Unreachable"));
                    break;
            }
        }
        catch (Exception ex)
        {
            FileLog.Shared.Error("Opening failed", ex);
            SetTransient(string.Format(CultureInfo.CurrentCulture, Strings.Get("Footer_OpenFailed"), ex.Message));
        }
        finally
        {
            _busy = false;
        }
    }

    private void SetTransient(string message)
    {
        _transientMessage = message;
        RefreshFooter();
    }

    private void RefreshFooter() => FooterText = BuildFooter();

    private string BuildFooter()
    {
        if (_transientMessage is not null)
        {
            return _transientMessage;
        }

        var culture = CultureInfo.CurrentCulture;
        switch (_index.Phase)
        {
            case IndexPhase.Loading:
                return Strings.Get("Footer_Loading");
            case IndexPhase.FirstScan:
                return string.Format(culture, Strings.Get("Footer_FirstScan"), _index.EntryCount);
        }

        // Stage 6: a running scan is shown with its location and the entries counted so far.
        var scan = _index.CurrentScan;
        string? scanPath = scan is { } s ? PathShortener.ShortenMiddle(s.Path, ScanPathDisplayLength) : null;

        if (Mode == SearchMode.ImageText)
        {
            return BuildImageTextFooter(culture);
        }

        if (_queryEmpty)
        {
            if (scanPath is not null)
            {
                return string.Format(culture, Strings.Get("Footer_IdleScanning"), scanPath, scan!.Value.Entries);
            }

            if (Hits.Count > 0)
            {
                return string.Format(culture, Strings.Get("Footer_Recent"), Hits.Count);
            }

            return _index.LastCompleted is { } completed
                ? string.Format(culture, Strings.Get("Footer_IdleUpdated"), UpdatedText(completed))
                : Strings.Get("Footer_Idle");
        }

        if (_total == 0)
        {
            return Strings.Get("Footer_None");
        }

        if (scanPath is not null)
        {
            return string.Format(culture, Strings.Get("Footer_HitsScanning"), _total, scanPath, scan!.Value.Entries);
        }

        return _total > Hits.Count
            ? string.Format(culture, Strings.Get("Footer_HitsLimited"), _total, Hits.Count)
            : string.Format(culture, Strings.Get("Footer_Hits"), _total);
    }

    /// <summary>Footer of mode Image text: hits and keys, or the progress while nothing is found yet.</summary>
    private string BuildImageTextFooter(CultureInfo culture)
    {
        if (Hits.Count > 0)
        {
            string keys = Strings.Get("Footer_ImageKeys");
            if (_queryEmpty)
            {
                return string.Format(culture, Strings.Get("Footer_ImageNewest"), Hits.Count, keys);
            }

            return _total > Hits.Count
                ? string.Format(culture, Strings.Get("Footer_ImageHitsLimited"), _total, Hits.Count, keys)
                : string.Format(culture, Strings.Get("Footer_ImageHits"), _total, keys);
        }

        if (_index.ImageText is { } service)
        {
            var progress = service.Progress;
            if (!service.RecognizerAvailable)
            {
                return Strings.Get("Footer_ImageNoLanguage");
            }

            if (progress.Pending > 0 || progress.Recognized < progress.Total)
            {
                return string.Format(culture, Strings.Get("Ocr_Progress"), progress.Recognized, progress.Total);
            }
        }

        return Strings.Get(_queryEmpty ? "Footer_ImageEmpty" : "Footer_None");
    }

    /// <summary>"updated today 08:12" or "updated 12.11.2026 08:12" (shared with the tray status line).</summary>
    public static string UpdatedText(DateTimeOffset completed)
    {
        var local = completed.LocalDateTime;
        return string.Format(
            CultureInfo.CurrentCulture,
            Strings.Get(local.Date == DateTime.Today ? "Tray_UpdatedToday" : "Tray_UpdatedOn"),
            local);
    }
}
