using FileDetective.Core;

namespace FileDetective.App;

/// <summary>State of a location with respect to the scan queue.</summary>
internal enum LocationScanState
{
    Idle,
    Queued,
    Running,
}

/// <summary>
/// Owns database, in-memory index, scanner and search engine. Opens the database and loads it on a background thread,
/// keeps the list of locations (add, remove) and runs scans one after the other on a single worker. Nothing here runs
/// on the UI thread. Events are raised on pool threads.
/// </summary>
/// <remarks>
/// Stage 5 decision: all scan starts go through <see cref="StartScan"/>; at program start it is called once for the
/// local locations. The real queue with the daily rule for shares, the 60 s / 2 min delays and the watcher arrive in
/// stage 6 and replace the inside of <see cref="StartScan"/> and <see cref="RunQueue"/> only.
/// </remarks>
internal sealed class IndexService : IDisposable
{
    private readonly CancellationTokenSource _cancel = new();
    private readonly string _databasePath;
    private readonly Func<AppSettings> _settings;
    private readonly object _gate = new();
    private readonly TaskCompletionSource _loadedSource = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly List<byte> _queue = [];
    private readonly HashSet<byte> _removing = [];
    private IndexDatabase? _database;
    private MemoryIndex? _index;
    private IndexStore? _store;
    private UsageTracker? _usage;
    private volatile SearchEngine? _engine;
    private Task? _loadWork;
    private Task? _worker;
    private byte? _current;
    private CancellationTokenSource? _currentCancel;
    private volatile bool _loaded;
    private volatile bool _anyScanCompleted;
    private long _lastCompletedUnix;

    public IndexService(string databasePath, Func<AppSettings> settings)
    {
        _databasePath = databasePath;
        _settings = settings;
    }

    /// <summary>Raised whenever loading or scanning changed the state (loaded, scan started/finished, location added/removed).</summary>
    public event Action? StateChanged;

    /// <summary>Raised with the error when the start-up work failed.</summary>
    public event Action<Exception>? Failed;

    /// <summary>The search engine; null until the directory is loaded.</summary>
    public SearchEngine? Engine => _engine;

    public IndexStore? Store => _store;

    public UsageTracker? Usage => _usage;

    public bool IsLoaded => _loaded;

    /// <summary>True while a scan runs or waits in the queue.</summary>
    public bool ScanPendingOrRunning
    {
        get
        {
            lock (_gate)
            {
                return _current is not null || _queue.Count > 0;
            }
        }
    }

    public bool AnyScanCompleted => _anyScanCompleted;

    /// <summary>Number of entries in memory right now (grows during a scan).</summary>
    public int EntryCount => _index?.Count ?? 0;

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
            UpdateScanState(store);
            _engine = engine;
            _loaded = true;
            _loadedSource.TrySetResult();

            // Stage 5: scan the local locations once at program start, but only after the first-run setup (without
            // it the user has not chosen anything yet). Shares are never scanned by themselves here (PLAN.md 2.4).
            if (_settings().FirstRunCompleted)
            {
                StartScan(store.GetRoots().Where(r => r.Enabled && r.Kind == RootKind.Local).Select(r => r.Id));
            }

            StateChanged?.Invoke();
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
            StateChanged?.Invoke();
        }
    }

    // ---------------------------------------------------------------- scans

    /// <summary>
    /// The one place that starts scans: puts the locations in the queue (once each, in the given order) and starts the
    /// worker if it is idle. Returns at once. Locations that are being removed are ignored.
    /// </summary>
    public void StartScan(IEnumerable<byte> rootIds)
    {
        ArgumentNullException.ThrowIfNull(rootIds);
        lock (_gate)
        {
            foreach (byte id in rootIds)
            {
                if (_removing.Contains(id) || _current == id || _queue.Contains(id))
                {
                    continue;
                }

                _queue.Add(id);
            }

            if (_queue.Count > 0 && _worker is null && !_cancel.IsCancellationRequested)
            {
                _worker = Task.Run(RunQueue);
            }
        }

        StateChanged?.Invoke();
    }

    /// <summary>Starts a scan of every enabled location (tray menu "Update now").</summary>
    public void StartScanAll()
    {
        if (_store is { } store)
        {
            StartScan(store.GetRoots().Where(r => r.Enabled).Select(r => r.Id));
        }
    }

    public LocationScanState GetScanState(byte rootId)
    {
        lock (_gate)
        {
            return _current == rootId ? LocationScanState.Running
                : _queue.Contains(rootId) ? LocationScanState.Queued
                : LocationScanState.Idle;
        }
    }

    private void RunQueue()
    {
        while (true)
        {
            byte id;
            CancellationTokenSource cts;
            lock (_gate)
            {
                if (_queue.Count == 0 || _cancel.IsCancellationRequested)
                {
                    _worker = null;
                    Monitor.PulseAll(_gate);
                    break;
                }

                id = _queue[0];
                _queue.RemoveAt(0);
                _current = id;
                cts = CancellationTokenSource.CreateLinkedTokenSource(_cancel.Token);
                _currentCancel = cts;
            }

            StateChanged?.Invoke();
            try
            {
                ScanOne(id, cts.Token);
            }
            catch (OperationCanceledException)
            {
                // cancelled by exit or by removing the location
            }
            catch (Exception ex)
            {
                DiagnosticLog.Error($"Scan of location {id} failed", ex);
            }
            finally
            {
                lock (_gate)
                {
                    _current = null;
                    _currentCancel = null;
                    Monitor.PulseAll(_gate);
                }

                cts.Dispose();
            }

            if (_store is { } store)
            {
                UpdateScanState(store);
            }

            StateChanged?.Invoke();
        }

        StateChanged?.Invoke();
    }

    private void ScanOne(byte rootId, CancellationToken token)
    {
        var store = _store ?? throw new InvalidOperationException("The directory is not loaded.");
        if (store.GetRoot(rootId) is not { } root)
        {
            return;
        }

        // Exclusions and politeness are read here, so changes in the settings apply from the next scan on.
        var settings = _settings();
        var scanner = new Scanner(
            store,
            FileSystemDirectoryLister.Instance,
            settings.CreateExcludeRules(),
            new ScannerOptions { Politeness = settings.NetworkPolitenessLevel });
        var stats = scanner.ScanRoot(rootId, token);
        DiagnosticLog.Info(
            $"Scan of {root.Path}: {stats.Outcome}, {stats.DirectoriesListed} directories, {stats.Entries} entries, "
            + $"{stats.FailedDirectories} failed, +{stats.Inserted} ~{stats.Updated} -{stats.Deleted}, "
            + $"{stats.Duration.TotalSeconds:F1} s, pauses {stats.TotalPause.TotalSeconds:F1} s.");
        _usage?.Reload();
    }

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
        StartScan([root.Id]);
        return root;
    }

    /// <summary>
    /// Removes a location with all its entries: a running scan of it is cancelled first and waited for, queued scans are
    /// dropped, then the usage map is reloaded. Blocks; call from a background thread.
    /// </summary>
    public int RemoveLocation(byte rootId)
    {
        var store = _store ?? throw new InvalidOperationException("The directory is not loaded.");
        lock (_gate)
        {
            _removing.Add(rootId);
            _queue.Remove(rootId);
            if (_current == rootId)
            {
                _currentCancel?.Cancel();
            }

            while (_current == rootId)
            {
                Monitor.Wait(_gate);
            }
        }

        try
        {
            int removed = store.RemoveRoot(rootId);
            _usage?.Reload();
            UpdateScanState(store);
            DiagnosticLog.Info($"Location {rootId} removed with {removed} entries.");
            return removed;
        }
        finally
        {
            lock (_gate)
            {
                _removing.Remove(rootId);
            }

            StateChanged?.Invoke();
        }
    }

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

    public void Dispose()
    {
        _cancel.Cancel();
        try
        {
            // The scanner checks the token between directories; wait briefly so no batch is cut off mid-write.
            _loadWork?.Wait(TimeSpan.FromSeconds(5));
            Task? worker;
            lock (_gate)
            {
                worker = _worker;
            }

            worker?.Wait(TimeSpan.FromSeconds(5));
        }
        catch (AggregateException)
        {
            // Already logged.
        }

        _store?.Dispose();
        _database?.Dispose();
        _index?.Dispose();
        _cancel.Dispose();
    }
}
