using System.IO;
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
    private ImageTextService? _imageText;
    private ImageHistory? _history;
    private ImageNoteService? _notes;
    private DriveDiscovery? _drives;
    private readonly SemaphoreSlim _imageTextGate = new(1, 1);
    private (bool Enabled, string Folders)? _imageTextApplied;
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
        _queue.Failed += (request, ex) => FileLog.Shared.Error($"Scan of location {request.RootId} failed", ex);
        _reachability.Changed += RaiseStateChanged;
        _usageReload = new Debouncer(UsageReloadDelay, ReloadUsage);
        _usageReload.Failed += ex => FileLog.Shared.Error("Reloading the usage map failed", ex);
    }

    /// <summary>Raised whenever loading, scanning, the watcher or the reachability changed the state.</summary>
    public event Action? StateChanged;

    /// <summary>Raised with the error when the start-up work failed.</summary>
    public event Action<Exception>? Failed;

    /// <summary>The search engine; null until the directory is loaded.</summary>
    public SearchEngine? Engine => _engine;

    public IndexStore? Store => _store;

    public UsageTracker? Usage => _usage;

    /// <summary>The image text (stage 11); null until the directory is loaded.</summary>
    public ImageTextService? ImageText => _imageText;

    /// <summary>Raised on a background thread when the progress of the image text changed.</summary>
    public event Action? ImageTextChanged;

    /// <summary>The screenshot history (stage 12); null until the directory is loaded.</summary>
    public ImageHistory? History => _history;

    /// <summary>The notes of images (stage 13); null until the directory is loaded.</summary>
    public ImageNoteService? Notes => _notes;

    /// <summary>
    /// Raised on a background thread when a change of the directory can alter the list of the history (an image came,
    /// went or moved). Only the history window listens, and only while it is open.
    /// </summary>
    public event Action? HistoryChanged;

    /// <summary>
    /// The local folders of the history: those of the image text settings (while the list was never set, the Screenshots
    /// folder), also when the image text is off, because the images are in the directory anyway. UNC paths and drives that
    /// are not local are left out.
    /// </summary>
    public IReadOnlyList<string> HistoryFolders()
    {
        var drives = _drives;
        return [.. ImageTextFolders.Effective(_settings(), KnownFolders.Screenshots)
            .Where(path => drives is not null && drives.Describe(path) is { Kind: RootKind.Local })];
    }

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
                FileLog.Shared.Info($"The previous database was unusable and moved to {database.BackupPath}.");
            }

            // PLAN.md 6.3: if the entry numbers have become sparse, compact them now, before anything is loaded into
            // memory. A failure only means that the old numbers stay (the transaction is rolled back).
            RenumberIfNeeded(database);

            var index = new MemoryIndex();
            _index = index;
            var store = new IndexStore(database, index);
            _store = store;
            var usage = new UsageTracker(database, store);
            _usage = usage;
            var resolver = new DatabaseEntryResolver(database);
            var texts = new ImageTextIndex();
            var drives = new DriveDiscovery();
            _drives = drives;
            var imageText = new ImageTextService(
                store, index, texts, new WindowsImageTextRecognizer(), SystemLocalFileAccess.Instance, resolver,
                new ImageTextServiceOptions
                {
                    // PLAN.md 2.1: only images on local drives are ever opened.
                    IsLocalPath = path => drives.Describe(path) is { Kind: RootKind.Local },
                    Log = (message, ex) =>
                    {
                        if (ex is null)
                        {
                            FileLog.Shared.Info(message);
                        }
                        else
                        {
                            FileLog.Shared.Error(message, ex);
                        }
                    },
                });
            imageText.ProgressChanged += RaiseImageTextChanged;
            _imageText = imageText;
            _history = new ImageHistory(new StoreImageHistorySource(store, resolver), texts, HistoryFolders);
            store.Changed += change =>
            {
                if (HistoryChanged is { } listener && ImageHistory.IsRelevant(change))
                {
                    try
                    {
                        listener();
                    }
                    catch (Exception ex)
                    {
                        FileLog.Shared.Error("A history listener failed", ex);
                    }
                }
            };
            var engine = new SearchEngine(
                index, resolver, usage,
                new ImageTextSearch(texts, resolver, store.GetImageTexts, () => imageText.Folders));

            var timer = System.Diagnostics.Stopwatch.StartNew();
            int loaded = store.LoadIntoMemory(token);
            FileLog.Shared.Info($"Directory loaded: {loaded} entries in {timer.ElapsedMilliseconds} ms.");
            usage.Reload();

            // Stage 13: notes are attached to the images of the loaded directory; a change shows like new image text.
            var notes = new ImageNoteService(
                store, texts, resolver, SystemLocalFileAccess.Instance, path => drives.Describe(path) is { Kind: RootKind.Local });
            try
            {
                notes.Load();
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                FileLog.Shared.Error("Loading the image notes failed", ex);
            }

            notes.Changed += RaiseImageTextChanged;
            _notes = notes;
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
                        FileLog.Shared.Info(message);
                    }
                    else
                    {
                        FileLog.Shared.Error(message, ex);
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
            scheduler.Failed += ex => FileLog.Shared.Error("Refresh check failed", ex);
            _scheduler = scheduler;
            scheduler.Start();

            NetworkChange.NetworkAddressChanged += OnNetworkAddressChanged;
            _networkSubscribed = true;

            RaiseStateChanged();

            // Stage 11: with the image text on, missing and invalid texts are fetched in the background.
            _ = ApplyImageTextAsync();
        }
        catch (OperationCanceledException)
        {
            // Program is exiting.
            _loadedSource.TrySetCanceled();
        }
        catch (Exception ex)
        {
            FileLog.Shared.Error("Start-up work failed", ex);
            _loadedSource.TrySetException(ex);
            Failed?.Invoke(ex);
            RaiseStateChanged();
        }
    }

    private static void RenumberIfNeeded(IndexDatabase database)
    {
        try
        {
            var timer = System.Diagnostics.Stopwatch.StartNew();
            var result = DirectoryRenumbering.RenumberIfNeeded(database);
            if (result.Performed)
            {
                FileLog.Shared.Info(
                    $"Entry numbers compacted: highest number {result.MaxIdBefore} -> {result.MaxIdAfter}, "
                    + $"{result.EntryCountBefore} rows, {result.DroppedUnreachable} unreachable rows dropped, {timer.ElapsedMilliseconds} ms.");
            }
        }
        catch (Exception ex)
        {
            FileLog.Shared.Error("Compacting the entry numbers failed; the old numbers stay", ex);
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
        var store = _store ?? throw new InvalidOperationException("The directory is not loaded.");
        var rules = settings.CreateExcludeRules(request.Kind);

        // Stage 14 (PLAN.md 11.5): quick or full is decided per scan from the location's state.
        bool quick = store.GetRoot(request.RootId) is { } root
            && RefreshScheduler.ShouldQuickScan(
                root, store.GetRootScanInfo(request.RootId), request.Trigger, rules.Fingerprint, DateTimeOffset.UtcNow,
                settings.FullScanInterval);
        int loggedMisses = 0;
        return new Scanner(
            store,
            FileSystemDirectoryLister.Instance,
            rules,
            new ScannerOptions
            {
                Politeness = settings.NetworkPolitenessLevel,
                Progress = entries => Interlocked.Exchange(ref _runningEntries, entries),
                QuickScan = quick,
                QuickScanShadow = settings.QuickScanShadow,
                ShadowMiss = path =>
                {
                    // At most 100 paths per scan in the log; the count is in the summary line.
                    if (++loggedMisses <= 100)
                    {
                        FileLog.Shared.Info($"Quick scan check: folder would have been skipped but changed: {path}");
                    }
                },
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
                // PLAN.md 7.4: directories listed, entries, duration, sum of pauses and errors of every scan go into the log.
                FileLog.Shared.Info(stats.Describe(root.Path, request.Trigger));
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
                FileLog.Shared.Error("Updating the state after a scan failed", ex);
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
            FileLog.Shared.Error("Checking the reachability failed", ex);
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
        FileLog.Shared.Info($"Location added: {root.Path} ({root.Kind}).");
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
                FileLog.Shared.Info($"Location {rootId} removed with {removed} entries.");
                return removed;
            }
        }
        finally
        {
            RaiseStateChanged();
        }
    }

    /// <summary>
    /// "Rebuild directory" (PLAN.md 8.5): cancels running scans, empties the entries of all locations (the locations and
    /// the usage history stay), reloads the memory and queues every enabled location for a scan. The scans count as
    /// manual requests: they do not count against the daily limit of a share, and the pauses of share scans stay as
    /// always. If emptying fails, the old directory is kept. Blocks while it waits for running scans to end; call from
    /// a background thread.
    /// </summary>
    /// <returns>Number of removed entries.</returns>
    public int RebuildDirectory()
    {
        var store = _store ?? throw new InvalidOperationException("The directory is not loaded.");
        try
        {
            var result = DirectoryRebuilder.Rebuild(
                store,
                _queue,
                _locks,
                unwatch: id => _watcher?.Unwatch(id),
                resumeWatching: roots => _watcher?.Sync(roots),
                afterReset: () =>
                {
                    RefreshRoots(store);
                    _usage?.Reload();
                    UpdateScanState(store);
                },
                log: FileLog.Shared.Info);
            FileLog.Shared.Info($"Rebuild: {result.Queued} locations queued for a scan.");
            return result.Removed;
        }
        catch (Exception ex)
        {
            FileLog.Shared.Error("Rebuilding the directory failed", ex);
            throw;
        }
        finally
        {
            RaiseStateChanged();
        }
    }

    // ---------------------------------------------------------------- image text (stage 11, PLAN.md 11.3)

    /// <summary>
    /// Applies the image text settings if they changed since the last call: switching off deletes the stored texts,
    /// switching on (or a new folder) adds every folder that lies in no location as a local location of its own (like the
    /// Start menu folders, PLAN.md 7.3) and starts the reading in the background. Runs on the thread pool; calls are
    /// serialized. Never throws (failures are logged).
    /// </summary>
    public async Task ApplyImageTextAsync()
    {
        if (_imageText is not { } service || _store is not { } store)
        {
            return;
        }

        await _imageTextGate.WaitAsync().ConfigureAwait(false);
        try
        {
            await Task.Run(() =>
            {
                var settings = _settings();
                var folders = ImageTextFolders.Effective(settings, KnownFolders.Screenshots);
                var key = (settings.OcrEnabled, string.Join('|', folders));
                if (_imageTextApplied == key)
                {
                    return;
                }

                if (settings.OcrEnabled)
                {
                    EnsureImageTextLocations(store, folders);
                }

                service.Configure(settings.OcrEnabled, folders);
                _imageTextApplied = key;
                FileLog.Shared.Info(settings.OcrEnabled
                    ? $"Image text on for {folders.Count} folders: {string.Join(", ", folders)}"
                    : "Image text off.");
            }).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            FileLog.Shared.Error("Applying the image text settings failed", ex);
        }
        finally
        {
            _imageTextGate.Release();
            RaiseImageTextChanged();
        }
    }

    /// <summary>
    /// Every image text folder that lies in no location and exists becomes a local location. A folder that lies in a
    /// location but is not in the directory (it was excluded so far) gets its location scanned again.
    /// </summary>
    private void EnsureImageTextLocations(IndexStore store, IReadOnlyList<string> folders)
    {
        foreach (string path in ImageTextFolders.MissingLocations(folders, store.GetRoots(), Directory.Exists))
        {
            var candidate = new LocationCandidate(path, null, RootKind.Local);
            if (LocationRules.Check(candidate, store.GetRoots()).Result == LocationCheckResult.Free)
            {
                AddLocation(candidate);
            }
        }

        var rescan = new HashSet<byte>();
        foreach (string folder in folders)
        {
            if (store.FindEntryId(folder) is null && Directory.Exists(folder))
            {
                var holder = store.GetRoots()
                    .Where(r => LocationRules.Normalize(r.Path) is { } root && LocationRules.ContainsOrEquals(root, folder))
                    .OrderByDescending(r => r.Path.Length)
                    .FirstOrDefault();
                if (holder is { LastScanStatus: not ScanStatus.Never })
                {
                    rescan.Add(holder.Id);
                }
            }
        }

        if (rescan.Count > 0)
        {
            StartScan(rescan);
        }
    }

    private void RaiseImageTextChanged()
    {
        try
        {
            ImageTextChanged?.Invoke();
        }
        catch (Exception ex)
        {
            FileLog.Shared.Error("An image text listener failed", ex);
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
            FileLog.Shared.Error("A state listener failed", ex);
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

        // The image text stops first: it waits up to 5 s for the image being read.
        _imageText?.Dispose();

        // The watcher and the scanner check their tokens between directories; both wait briefly so no batch is cut
        // off mid-write.
        _watcher?.Dispose();
        if (!_queue.Shutdown(TimeSpan.FromSeconds(5)))
        {
            FileLog.Shared.Info("The running scan did not end within 5 s at exit.");
        }

        _queue.Dispose();
        _usageReload.Dispose();
        _store?.Dispose();
        _database?.Dispose();
        _index?.Dispose();
        _cancel.Dispose();
    }
}
