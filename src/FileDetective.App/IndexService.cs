using FileDetective.Core;

namespace FileDetective.App;

/// <summary>
/// Owns database, in-memory index, scanner and search engine and runs the start-up work on a background thread:
/// open the database, load it into memory, make sure the user folder is a location, scan it once, reload the usage
/// map (PLAN.md 10, stage 4). Nothing here runs on the UI thread. Events are raised on pool threads.
/// </summary>
internal sealed class IndexService : IDisposable
{
    private readonly CancellationTokenSource _cancel = new();
    private readonly string _databasePath;
    private IndexDatabase? _database;
    private MemoryIndex? _index;
    private IndexStore? _store;
    private UsageTracker? _usage;
    private volatile SearchEngine? _engine;
    private Task? _work;
    private volatile bool _loaded;
    private volatile bool _scanPendingOrRunning = true;
    private volatile bool _anyScanCompleted;
    private long _lastCompletedUnix;

    public IndexService(string databasePath)
    {
        _databasePath = databasePath;
    }

    /// <summary>Raised whenever loading or scanning changed the state (loaded, scan started/finished).</summary>
    public event Action? StateChanged;

    /// <summary>Raised with a message when the start-up work failed.</summary>
    public event Action<Exception>? Failed;

    /// <summary>The search engine; null until the directory is loaded.</summary>
    public SearchEngine? Engine => _engine;

    public IndexStore? Store => _store;

    public UsageTracker? Usage => _usage;

    public bool IsLoaded => _loaded;

    public bool ScanPendingOrRunning => _scanPendingOrRunning;

    public bool AnyScanCompleted => _anyScanCompleted;

    /// <summary>Number of entries in memory right now (grows during the first scan).</summary>
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

    public IndexPhase Phase => BarState.Resolve(_loaded, _scanPendingOrRunning, _anyScanCompleted);

    public void Start()
    {
        _work = Task.Run(Run);
    }

    private void Run()
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
            StateChanged?.Invoke();

            // Decision (stage 4): the only location is the user folder, created on first start.
            string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var root = store.GetRoots().FirstOrDefault(r => string.Equals(
                r.Path.TrimEnd('\\'), profile.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
                ?? store.AddRoot(profile, null, RootKind.Local);

            token.ThrowIfCancellationRequested();
            var scanner = new Scanner(store, FileSystemDirectoryLister.Instance, ExcludeRules.CreateDefault());
            var stats = scanner.ScanRoot(root.Id, token);
            DiagnosticLog.Info(
                $"Scan of {root.Path}: {stats.Outcome}, {stats.DirectoriesListed} directories, {stats.Entries} entries, "
                + $"{stats.FailedDirectories} failed, +{stats.Inserted} ~{stats.Updated} -{stats.Deleted}, "
                + $"{stats.Duration.TotalSeconds:F1} s, pauses {stats.TotalPause.TotalSeconds:F1} s.");

            usage.Reload();
            UpdateScanState(store);
            _scanPendingOrRunning = false;
            StateChanged?.Invoke();
        }
        catch (OperationCanceledException)
        {
            // Program is exiting.
        }
        catch (Exception ex)
        {
            DiagnosticLog.Error("Start-up work failed", ex);
            _scanPendingOrRunning = false;
            Failed?.Invoke(ex);
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
            _work?.Wait(TimeSpan.FromSeconds(5));
        }
        catch (AggregateException)
        {
            // Already logged in Run.
        }

        _store?.Dispose();
        _database?.Dispose();
        _index?.Dispose();
        _cancel.Dispose();
    }
}
