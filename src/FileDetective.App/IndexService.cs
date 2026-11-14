using System.Net.NetworkInformation;
using FileDetective.Core;

namespace FileDetective.App;

/// <summary>
/// Owns database, in-memory index, search engine and the machinery that keeps the directory current (stage 6): the
/// <see cref="ScanQueue"/> (one whole-location scan at a time, local before shares), the <see cref="RefreshScheduler"/>
/// (when to scan), the <see cref="ReachabilityMonitor"/> (which locations are there) and the <see cref="LocalWatcher"/>
/// (live changes on local locations). Nothing here runs on the UI thread. Events are raised on background threads.
/// </summary>
internal sealed class IndexService : IDisposable
{
    /// <summary>The usage map is rebuilt at most this often after the watcher changed paths.</summary>
    private static readonly TimeSpan UsageReloadDelay = TimeSpan.FromSeconds(2);

    private readonly CancellationTokenSource _cancel = new();
    private readonly string _databasePath;
    private readonly Func<AppSettings> _settings;
    private readonly TaskCompletionSource _loadedSource = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly LocationLocks _locks = new();
    private readonly ScanQueue _queue;
    private readonly ReachabilityMonitor _reachability = new();
    private readonly Debouncer _usageReload;
    private IndexDatabase? _database;
    private MemoryIndex? _index;
    private IndexStore? _store;
    private UsageTracker? _usage;
    private volatile SearchEngine? _engine;
    private ScanExecutor? _executor;
    private RefreshScheduler? _scheduler;
    private LocalWatcher? _watcher;
    private Task? _loadWork;
    private volatile bool _loaded;
    private volatile bool _anyScanCompleted;
    private volatile IReadOnlyDictionary<byte, RootInfo> _roots = new Dictionary<byte, RootInfo>();
    private volatile string? _runningPath;
    private long _runningEntries;
    private long _lastCompletedUnix;
    private bool _networkSubscribed;

    public IndexService(string databasePath, Func<AppSettings> settings)
    {
        _databasePath = databasePath;
        _settings = settings;
        _queue = new ScanQueue(RunScan);
        _queue.Changed += RaiseStateChanged;
        _queue.Failed += (request, ex) => DiagnosticLog.Error($"Scan of location {request.RootId} failed", ex);
        _reachability.Changed += RaiseStateChanged;
        _usageReload = new Debouncer(UsageReloadDelay, ReloadUsage);
        _usageReload.Failed += ex => DiagnosticLog.Error("Reloading the usage map failed", ex);
    }

    /// <summary>Raised whenever loading, scanning, the watcher or the reachability changed the state.</summary>
    public event Action? StateChanged;

    /// <summary>Raised with the error when the start-up work failed.</summary>
    public event Action<Exception>? Failed;

    /// <summary>The search engine; null until the directory is loaded.</summary>
    public SearchEngine? Engine => _engine;

    public IndexStore? Store => _store;

    public UsageTracker? Usage => _usage;

    public bool IsLoaded => _loaded;

    /// <summary>True while a scan runs or waits in the queue.</summary>
    public bool ScanPendingOrRunning => _queue.IsBusy;

    public bool AnyScanCompleted => _anyScanCompleted;

    /// <summary>Number of entries in memory right now (grows during a scan).</summary>
    public int EntryCount => _index?.Count ?? 0;

    /// <summary>The location being scanned and the entries counted by that scan so far, or null.</summary>
    public (string Path, long Entries)? CurrentScan
        => _runningPath is { } path ? (path, Interlocked.Read(ref _runningEntries)) : null;

    /// <summary>Time of the newest completed scan of any location, or null if none.</summary>
    public DateTimeOffset? LastCompleted
    {
        get
        {
            long value = Interlocked.Read(ref _lastCompletedUnix);
            return value == 0 ? null : DateTimeOffset.FromUnixTimeSeconds(value);
        }
    }

    /// <summary>Completes when the directory is loaded; faults if loading failed.</summary>
    public Task LoadedTask => _loadedSource.Task;

    public IndexPhase Phase => BarState.Resolve(_loaded, ScanPendingOrRunning, _anyScanCompleted);

    public void Start()
    {
        _loadWork = Task.Run(Load);
    }

    private void Load()
    {
        var token = _cancel.Token;
        try
        {
            var database = IndexDatabase.Open(_databasePath);
            _database = database;
            if (database.BackupPath is not null)
            {
                DiagnosticLog.Info($"The previous database was unusable and moved to {database.BackupPath}.");
            }

            var index = new MemoryIndex();
            _index = index;
            var store = new IndexStore(database, index);
            _store = store;
            var usage = new UsageTracker(database, store);
            _usage = usage;
            var engine = new SearchEngine(index, new DatabaseEntryResolver(database), usage);

            var timer = System.Diagnostics.Stopwatch.StartNew();
            int loaded = store.LoadIntoMemory(token);
            DiagnosticLog.Info($"Directory loaded: {loaded} entries in {timer.ElapsedMilliseconds} ms.");

            usage.Reload();
            RefreshRoots(store);
            UpdateScanState(store);

            _executor = new ScanExecutor(store, _locks, CreateScanner);
            _watcher = new LocalWatcher(store, new LocalWatcherOptions
            {
                Excludes = () => _settings().CreateExcludeRules(),
                Locks = _locks,
                FullScanNeeded = RequestFullScan,
                Log = (message, ex) =>
                {
                    if (ex is null)
                    {
                        DiagnosticLog.Info(message);
                    }
                    else
                    {
                        DiagnosticLog.Error(message, ex);
                    }
                },
            });
            _watcher.Changed += () =>
            {
                _usageReload.Trigger();
                RaiseStateChanged();
            };

            _engine = engine;
            _loaded = true;
            _loadedSource.TrySetResult();

            // Start-up: reachability of all locations at once (in the background), watchers for the local ones, and
            // the scheduler (local scans after 60 s, the first share check after 2 minutes).
            var roots = store.GetRoots();
            _ = CheckReachabilitySafelyAsync(roots.Where(r => r.Enabled).Select(r => r.Path));
            _watcher.Sync(roots);

            var scheduler = new RefreshScheduler(TimeProvider.System, store.GetRoots, _reachability, _queue.Enqueue);
            scheduler.Failed += ex => DiagnosticLog.Error("Refresh check failed", ex);
            _scheduler = scheduler;
            scheduler.Start();

            NetworkChange.NetworkAddressChanged += OnNetworkAddressChanged;
            _networkSubscribed = true;

            RaiseStateChanged();
        }
        catch (OperationCanceledException)
        {
            // Program is exiting.
            _loadedSource.TrySetCanceled();
        }
        catch (Exception ex)
        {
            DiagnosticLog.Error("Start-up work failed", ex);
            _loadedSource.TrySetException(ex);
            Failed?.Invoke(ex);
            RaiseStateChanged();
        }
    }

    // ---------------------------------------------------------------- scans

    /// <summary>"Update now" for the given locations: always queued, regardless of day, attempts and reachability.</summary>
    public void StartScan(IEnumerable<byte> rootIds)
    {
        ArgumentNullException.ThrowIfNull(rootIds);
        if (_scheduler is not { } scheduler)
        {
            return;
        }

        var roots = _roots;
        scheduler.RequestNow(rootIds.Select(id => roots.GetValueOrDefault(id)).OfType<RootInfo>());
    }

    /// <summary>Starts a scan of every enabled location (tray menu "Update now").</summary>
    public void StartScanAll() => StartScan(_roots.Values.Where(r => r.Enabled).Select(r => r.Id));

    public QueuedScanState GetScanState(byte rootId) => _queue.GetState(rootId);

    private Scanner CreateScanner(ScanRequest request)
    {
        // Exclusions and politeness are read here, so changes in the settings apply from the next scan on.
        var settings = _settings();
        return new Scanner(
            _store ?? throw new InvalidOperationException("The directory is not loaded."),
            FileSystemDirectoryLister.Instance,
            settings.CreateExcludeRules(),
            new ScannerOptions
            {
                Politeness = settings.NetworkPolitenessLevel,
                Progress = entries => Interlocked.Exchange(ref _runningEntries, entries),
            });
    }

    /// <summary>Runs on the queue's worker thread, one location at a time.</summary>
    private void RunScan(ScanRequest request, CancellationToken token)
    {
        if (_store is not { } store || _executor is not { } executor || store.GetRoot(request.RootId) is not { } root)
        {
            return;
        }

        Interlocked.Exchange(ref _runningEntries, 0);
        _runningPath = root.Path;
        RaiseStateChanged();
        try
        {
            var stats = executor.Run(request, token);
            if (stats is not null)
            {
                DiagnosticLog.Info(
                    $"Scan of {root.Path} ({request.Trigger}): {stats.Outcome}, {stats.DirectoriesListed} directories, "
                    + $"{stats.Entries} entries, {stats.FailedDirectories} failed, +{stats.Inserted} ~{stats.Updated} "
                    + $"-{stats.Deleted}, {stats.Duration.TotalSeconds:F1} s, pauses {stats.TotalPause.TotalSeconds:F1} s.");
            }
        }
        finally
        {
            _runningPath = null;
            try
            {
                RefreshRoots(store);
                UpdateScanState(store);
                _usage?.Reload();
            }
            catch (Exception ex)
            {
                DiagnosticLog.Error("Updating the state after a scan failed", ex);
            }
        }
    }

    private void RequestFullScan(byte rootId)
    {
        if (_roots.GetValueOrDefault(rootId) is { } root)
        {
            _queue.Enqueue(new ScanRequest(root.Id, root.Kind, ScanTrigger.Overflow));
        }
    }

    private void ReloadUsage() => _usage?.Reload();

    // ---------------------------------------------------------------- reachability

    /// <summary>True if the location is known to be unreachable right now (cached; never blocks).</summary>
    public bool IsLocationUnreachable(byte rootId)
        => _roots.GetValueOrDefault(rootId) is { } root && _reachability.IsUnreachable(root.Path);

    /// <summary>The same for a location path.</summary>
    public bool IsLocationPathUnreachable(string rootPath) => _reachability.IsUnreachable(rootPath);

    /// <summary>Opening the search bar: checks all locations again if the last check is older than 60 s (in the background).</summary>
    public void OnBarShown()
    {
        if (_loaded)
        {
            _reachability.CheckAllIfStale(_roots.Values.Where(r => r.Enabled).Select(r => r.Path));
        }
    }

    /// <summary>Checks one location again in the background (e.g. after a hit could not be found).</summary>
    public void RecheckLocation(byte rootId)
    {
        if (_roots.GetValueOrDefault(rootId) is { } root)
        {
            _ = CheckReachabilitySafelyAsync([root.Path]);
        }
    }

    private async Task CheckReachabilitySafelyAsync(IEnumerable<string> paths)
    {
        try
        {
            await _reachability.CheckAllAsync(paths.ToList()).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            DiagnosticLog.Error("Checking the reachability failed", ex);
        }
    }

    private void OnNetworkAddressChanged(object? sender, EventArgs e) => _scheduler?.NotifyNetworkChanged();

    // ---------------------------------------------------------------- locations

    public IReadOnlyList<RootInfo> GetRoots() => _store?.GetRoots() ?? [];

    /// <summary>
    /// Adds a location and queues its first scan. <paramref name="replace"/> are existing locations that the new one
    /// contains: they are removed first (see <see cref="LocationRules.Check"/>). Blocks while removing; call from a
    /// background thread.
    /// </summary>
    public RootInfo AddLocation(LocationCandidate candidate, IReadOnlyList<RootInfo>? replace = null)
    {
        var store = _store ?? throw new InvalidOperationException("The directory is not loaded.");
        foreach (var old in replace ?? [])
        {
            RemoveLocation(old.Id);
        }

        var root = store.AddRoot(candidate.Path, candidate.UncPath, candidate.Kind);
        DiagnosticLog.Info($"Location added: {root.Path} ({root.Kind}).");
        RefreshRoots(store);

        // The watcher starts before the first scan, so nothing that happens during the scan is missed.
        _watcher?.Sync(store.GetRoots());
        _queue.Enqueue(new ScanRequest(root.Id, root.Kind, ScanTrigger.Added));
        _ = CheckReachabilitySafelyAsync([root.Path]);
        RaiseStateChanged();
        return root;
    }

    /// <summary>
    /// Removes a location with all its entries: queued scans are dropped, a running scan is cancelled and waited for,
    /// the watcher stops, then the entries are deleted under the location lock and the usage map is reloaded. Blocks;
    /// call from a background thread.
    /// </summary>
    public int RemoveLocation(byte rootId)
    {
        var store = _store ?? throw new InvalidOperationException("The directory is not loaded.");
        string? path = _roots.GetValueOrDefault(rootId)?.Path;
        try
        {
            using (_queue.CancelAndBlock(rootId))
            {
                _watcher?.Unwatch(rootId);
                int removed;
                using (_locks.Acquire(rootId))
                {
                    removed = store.RemoveRoot(rootId);
                }

                if (path is not null)
                {
                    _reachability.Forget(path);
                }

                RefreshRoots(store);
                _usage?.Reload();
                UpdateScanState(store);
                DiagnosticLog.Info($"Location {rootId} removed with {removed} entries.");
                return removed;
            }
        }
        finally
        {
            RaiseStateChanged();
        }
    }

    private void RefreshRoots(IndexStore store)
        => _roots = store.GetRoots().ToDictionary(r => r.Id);

    private void UpdateScanState(IndexStore store)
    {
        long newest = 0;
        foreach (var root in store.GetRoots())
        {
            if (root.LastScanCompletedUtc is { } completed && completed > newest)
            {
                newest = completed;
            }
        }

        Interlocked.Exchange(ref _lastCompletedUnix, newest);
        _anyScanCompleted = newest != 0;
    }

    private void RaiseStateChanged()
    {
        try
        {
            StateChanged?.Invoke();
        }
        catch (Exception ex)
        {
            DiagnosticLog.Error("A state listener failed", ex);
        }
    }

    public void Dispose()
    {
        if (_networkSubscribed)
        {
            NetworkChange.NetworkAddressChanged -= OnNetworkAddressChanged;
        }

        _scheduler?.Dispose();
        _cancel.Cancel();
        try
        {
            _loadWork?.Wait(TimeSpan.FromSeconds(5));
        }
        catch (AggregateException)
        {
            // Already logged.
        }

        // The watcher and the scanner check their tokens between directories; both wait briefly so no batch is cut
        // off mid-write.
        _watcher?.Dispose();
        if (!_queue.Shutdown(TimeSpan.FromSeconds(5)))
        {
            DiagnosticLog.Info("The running scan did not end within 5 s at exit.");
        }

        _queue.Dispose();
        _usageReload.Dispose();
        _store?.Dispose();
        _database?.Dispose();
        _index?.Dispose();
        _cancel.Dispose();
    }
}
