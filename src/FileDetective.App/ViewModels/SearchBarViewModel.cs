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
    private readonly Dispatcher _dispatcher;
    private readonly SearchCoordinator _coordinator;
    private long _appliedGeneration;
    private int _total;
    private bool _queryEmpty = true;
    private string? _transientMessage;
    private bool _busy;

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

    public SearchBarViewModel(IndexService index, Dispatcher dispatcher)
    {
        _index = index;
        _dispatcher = dispatcher;
        _coordinator = new SearchCoordinator(
            RunSearch,
            (generation, result) => _dispatcher.BeginInvoke(new Action(() => Apply(generation, result))),
            ex => DiagnosticLog.Error("Search failed", ex));
        _index.StateChanged += () => _dispatcher.BeginInvoke(new Action(OnIndexStateChanged));
        FooterText = BuildFooter();
    }

    /// <summary>Raised after a successful open or show in Explorer: the bar should hide.</summary>
    public event Action? CloseRequested;

    public string PlaceholderText => Strings.Get("Bar_Placeholder");

    public string ModeLabel => Strings.Get(Mode switch
    {
        SearchMode.Files => "Mode_Files",
        SearchMode.Folders => "Mode_Folders",
        _ => "Mode_All",
    });

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
        _transientMessage = null;
        StartSearch();
    }

    partial void OnHitsChanged(IReadOnlyList<HitItem> value) => OnPropertyChanged(nameof(HasHits));

    partial void OnSelectedIndexChanged(int value)
    {
        OnPropertyChanged(nameof(SelectedItem));
        OnPropertyChanged(nameof(CanOpenSelected));
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
    public void CycleMode(bool reverse) => Mode = BarState.CycleMode(Mode, reverse);

    public void MoveSelection(int delta)
    {
        if (Hits.Count == 0)
        {
            return;
        }

        SelectedIndex = Math.Clamp((SelectedIndex < 0 ? 0 : SelectedIndex) + delta, 0, Hits.Count - 1);
    }

    /// <summary>Enter / double click.</summary>
    public Task OpenSelectedAsync() => ActOnSelectedAsync(open: true);

    /// <summary>Ctrl+Enter.</summary>
    public Task RevealSelectedAsync() => ActOnSelectedAsync(open: false);

    /// <summary>Ctrl+C with no text selected in the input box, and the context menu.</summary>
    public void CopySelectedPath()
    {
        if (SelectedItem is not { } item)
        {
            return;
        }

        SetTransient(Strings.Get(ShellActions.CopyText(item.FullPath) ? "Footer_Copied" : "Footer_CopyFailed"));
    }

    private void StartSearch()
    {
        var query = SearchQuery.Parse(Input, Mode);
        _queryEmpty = query.IsEmpty;
        if (_queryEmpty)
        {
            // Nothing to search: respond at once. (Recently opened entries for an empty input come in stage 7.)
            _coordinator.CancelPending();
            _total = 0;
            Hits = [];
            SelectedIndex = -1;
            RefreshFooter();
            return;
        }

        _coordinator.Request(Input, Mode);
        RefreshFooter();
    }

    private SearchResult RunSearch(string input, SearchMode mode, CancellationToken token)
        => _index.Engine?.Search(input, mode, token) ?? SearchResult.Empty;

    private void Apply(long generation, SearchResult result)
    {
        if (generation < _appliedGeneration)
        {
            return;
        }

        _appliedGeneration = generation;
        if (_queryEmpty)
        {
            return; // the input was cleared while this search was on its way
        }

        var items = new List<HitItem>(result.Hits.Count);
        foreach (var hit in result.Hits)
        {
            items.Add(HitItem.From(hit, _index.IsLocationUnreachable(hit.Details.RootId)));
        }

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
        if (!_queryEmpty)
        {
            StartSearch();
        }

        RefreshFooter();
    }

    private async Task ActOnSelectedAsync(bool open)
    {
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
                    string? error = open ? ShellActions.Open(path) : ShellActions.RevealInExplorer(path);
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
                                DiagnosticLog.Error("Recording the opening failed", ex);
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
            DiagnosticLog.Error("Opening failed", ex);
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

        if (_queryEmpty)
        {
            if (scanPath is not null)
            {
                return string.Format(culture, Strings.Get("Footer_IdleScanning"), scanPath, scan!.Value.Entries);
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
