namespace FileDetective.Core;

/// <summary>Change notifications for one folder tree (abstraction over <see cref="FileSystemWatcher"/> for tests).</summary>
public interface IChangeSource : IDisposable
{
    /// <summary>A file or folder was created, deleted or changed (full path).</summary>
    event Action<string>? PathChanged;

    /// <summary>A file or folder was renamed (old full path, new full path).</summary>
    event Action<string, string>? PathRenamed;

    /// <summary>Events were lost (<see cref="InternalBufferOverflowException"/>) or the watcher failed.</summary>
    event Action<Exception>? Error;

    void Start();
}

/// <summary>
/// <see cref="FileSystemWatcher"/> as PLAN.md 7.6 wants it: whole tree, 64 KB buffer, names of files and folders only
/// (size and time changes are not watched, they come with the scan after the next start).
/// </summary>
public sealed class FileSystemChangeSource : IChangeSource
{
    public const int BufferSize = 64 * 1024;

    private readonly FileSystemWatcher _watcher;

    public FileSystemChangeSource(string path)
    {
        _watcher = new FileSystemWatcher(path)
        {
            IncludeSubdirectories = true,
            InternalBufferSize = BufferSize,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName,
        };
        _watcher.Created += (_, e) => PathChanged?.Invoke(e.FullPath);
        _watcher.Deleted += (_, e) => PathChanged?.Invoke(e.FullPath);
        _watcher.Changed += (_, e) => PathChanged?.Invoke(e.FullPath);
        _watcher.Renamed += (_, e) => PathRenamed?.Invoke(e.OldFullPath, e.FullPath);
        _watcher.Error += (_, e) => Error?.Invoke(e.GetException());
    }

    public event Action<string>? PathChanged;

    public event Action<string, string>? PathRenamed;

    public event Action<Exception>? Error;

    public void Start() => _watcher.EnableRaisingEvents = true;

    public void Dispose()
    {
        _watcher.EnableRaisingEvents = false;
        _watcher.Dispose();
    }
}

/// <summary>Settings of a <see cref="LocalWatcher"/>.</summary>
public sealed class LocalWatcherOptions
{
    /// <summary>Events per path are collected this long before they are handled (PLAN.md 7.6: 300 ms).</summary>
    public TimeSpan SettleTime { get; init; } = TimeSpan.FromMilliseconds(300);

    /// <summary>Default <see cref="FileSystemDirectoryLister"/>.</summary>
    public IDirectoryLister? Lister { get; init; }

    /// <summary>The current exclusion rules, read for every batch (settings may change). Default: the standard rules.</summary>
    public Func<ExcludeRules>? Excludes { get; init; }

    /// <summary>Default <see cref="ReparseTagLinkDetector"/>.</summary>
    public ILinkDetector? LinkDetector { get; init; }

    /// <summary>The locks shared with the scan queue. Required in the program; a private set when null (tests).</summary>
    public LocationLocks? Locks { get; init; }

    /// <summary>Creates the change source of a location; default <see cref="FileSystemChangeSource"/>.</summary>
    public Func<string, IChangeSource>? CreateSource { get; init; }

    /// <summary>Called when a location must be scanned completely (events lost).</summary>
    public Action<byte>? FullScanNeeded { get; init; }

    /// <summary>Called for problems worth a log line.</summary>
    public Action<string, Exception?>? Log { get; init; }

    /// <summary>Default <see cref="TimeProvider.System"/>.</summary>
    public TimeProvider? Time { get; init; }
}

/// <summary>
/// Keeps local locations current while the program runs (PLAN.md 7.6). One change source per local location, never
/// for shares; events are collected per path for 300 ms and handled by one worker thread.
/// </summary>
/// <remarks>
/// <para>Every event means "reconcile this path": the parent folder is listed completely (one listing per parent and
/// batch); if the name is there, the entry is inserted or updated, if not, it is removed with its subtree. This is
/// repeatable and insensitive to lost or doubled events.</para>
/// <para>Safety (PLAN.md 2.5, CLAUDE.md rule 4): an entry is only removed when the listing of its parent succeeded and
/// does not show it. A listing that fails never removes anything; if the parent itself is gone
/// (<see cref="DirectoryNotFoundException"/>), the parent is reconciled instead, which again needs a successful listing of
/// its own parent. The location itself is never removed here.</para>
/// <para>A new folder is scanned as a subtree (moving a folder in reports only the folder). A rename whose old path is
/// known and whose new path is not moves the entry directly (one row) after both parent listings confirmed it; otherwise
/// both paths are reconciled. Exclusions apply. A buffer overflow or a failing watcher asks for a full scan.</para>
/// <para>Concurrency decision: the worker holds the <see cref="LocationLocks"/> lock of the location while it handles a
/// batch, including the subtree scans of new folders. Whole-location scans of the <see cref="ScanQueue"/> hold the same
/// lock (<see cref="ScanExecutor"/>), so the two never compare and write the same location at the same time. The subtree
/// scans deliberately do not go through the scan queue: a share scan can run for an hour with pauses, and local changes
/// must still show within seconds; the "one scan at a time" rule protects shares, and the watcher never touches one.
/// Events of a location that has never been scanned are dropped: its first scan is still to come and will see them.</para>
/// </remarks>
public sealed class LocalWatcher : IDisposable
{
    private readonly IndexStore _store;
    private readonly TimeSpan _settle;
    private readonly IDirectoryLister _lister;
    private readonly Func<ExcludeRules> _excludes;
    private readonly ILinkDetector _linkDetector;
    private readonly LocationLocks _locks;
    private readonly Func<string, IChangeSource> _createSource;
    private readonly Action<byte>? _fullScanNeeded;
    private readonly Action<string, Exception?>? _log;
    private readonly TimeProvider _time;
    private readonly object _gate = new();
    private readonly Dictionary<byte, Watched> _watched = [];
    private readonly List<PendingItem> _pending = [];
    private readonly HashSet<string> _pendingPaths = new(StringComparer.OrdinalIgnoreCase);
    private readonly CancellationTokenSource _cancel = new();
    private readonly Thread _worker;
    private bool _processing;
    private bool _disposed;

    public LocalWatcher(IndexStore store, LocalWatcherOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        options ??= new LocalWatcherOptions();
        _store = store;
        _settle = options.SettleTime;
        _lister = options.Lister ?? FileSystemDirectoryLister.Instance;
        _excludes = options.Excludes ?? ExcludeRules.CreateDefault;
        _linkDetector = options.LinkDetector ?? ReparseTagLinkDetector.Instance;
        _locks = options.Locks ?? new LocationLocks();
        _createSource = options.CreateSource ?? (path => new FileSystemChangeSource(path));
        _fullScanNeeded = options.FullScanNeeded;
        _log = options.Log;
        _time = options.Time ?? TimeProvider.System;
        _worker = new Thread(WorkerLoop) { IsBackground = true, Name = "FileDetective watcher" };
        _worker.Start();
    }

    /// <summary>Raised (on the worker thread) after a batch that changed the index: paths came, went or moved.</summary>
    public event Action? Changed;

    /// <summary>True while nothing waits and nothing is being handled (for tests).</summary>
    public bool IsIdle
    {
        get
        {
            lock (_gate)
            {
                return _pending.Count == 0 && !_processing;
            }
        }
    }

    public bool IsWatching(byte rootId)
    {
        lock (_gate)
        {
            return _watched.ContainsKey(rootId);
        }
    }

    /// <summary>Watches exactly the enabled local locations of <paramref name="roots"/> and stops watching all others.</summary>
    public void Sync(IEnumerable<RootInfo> roots)
    {
        ArgumentNullException.ThrowIfNull(roots);
        var wanted = roots.Where(IsWatchable).ToDictionary(r => r.Id);
        List<byte> stale;
        lock (_gate)
        {
            stale = [.. _watched.Keys.Where(id => !wanted.ContainsKey(id))];
        }

        foreach (byte id in stale)
        {
            Unwatch(id);
        }

        foreach (var root in wanted.Values)
        {
            if (!IsWatching(root.Id))
            {
                Watch(root);
            }
        }
    }

    /// <summary>
    /// Starts watching a local location. Returns false if the folder cannot be watched (e.g. it does not exist).
    /// </summary>
    /// <exception cref="ArgumentException">The location is not local (shares never get a watcher, PLAN.md 7.6).</exception>
    public bool Watch(RootInfo root)
    {
        ArgumentNullException.ThrowIfNull(root);
        if (!IsWatchable(root) && root.Enabled)
        {
            throw new ArgumentException("Only local locations are watched; shares never get a watcher.", nameof(root));
        }

        if (!root.Enabled)
        {
            return false;
        }

        IChangeSource source;
        try
        {
            source = _createSource(root.Path);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            _log?.Invoke($"Watching {root.Path} is not possible", ex);
            return false;
        }

        byte id = root.Id;
        source.PathChanged += path => Add(id, path, null);
        source.PathRenamed += (oldPath, newPath) => Add(id, oldPath, newPath);
        source.Error += ex => OnSourceError(id, ex);

        lock (_gate)
        {
            if (_disposed)
            {
                source.Dispose();
                return false;
            }

            if (_watched.Remove(id, out var old))
            {
                old.Source.Dispose();
            }

            _watched[id] = new Watched(root.Path, source);
        }

        try
        {
            source.Start();
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            _log?.Invoke($"Watching {root.Path} is not possible", ex);
            Unwatch(id);
            return false;
        }
    }

    /// <summary>
    /// Stops watching a location and drops its waiting events. A batch of it that is being handled right now ends on
    /// its own; take the location lock to wait for it.
    /// </summary>
    public void Unwatch(byte rootId)
    {
        Watched? watched;
        lock (_gate)
        {
            _watched.Remove(rootId, out watched);
            for (int i = _pending.Count - 1; i >= 0; i--)
            {
                if (_pending[i].RootId == rootId)
                {
                    _pendingPaths.Remove(_pending[i].Key);
                    _pending.RemoveAt(i);
                }
            }

            Monitor.PulseAll(_gate);
        }

        watched?.Source.Dispose();
    }

    /// <summary>Waits until no event waits or is being handled (for tests). Returns false on timeout.</summary>
    public bool WaitIdle(TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        lock (_gate)
        {
            while (_pending.Count > 0 || _processing)
            {
                var left = deadline - DateTime.UtcNow;
                if (left <= TimeSpan.Zero || !Monitor.Wait(_gate, left))
                {
                    return _pending.Count == 0 && !_processing;
                }
            }

            return true;
        }
    }

    public void Dispose()
    {
        List<Watched> all;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            all = [.. _watched.Values];
            _watched.Clear();
            _pending.Clear();
            _pendingPaths.Clear();
            Monitor.PulseAll(_gate);
        }

        _cancel.Cancel();
        foreach (var w in all)
        {
            w.Source.Dispose();
        }

        _worker.Join(TimeSpan.FromSeconds(5));
    }

    private static bool IsWatchable(RootInfo root)
        => root.Enabled && root.Kind == RootKind.Local && !root.Path.StartsWith(@"\\", StringComparison.Ordinal)
            && !root.Path.StartsWith("//", StringComparison.Ordinal);

    // ---------------------------------------------------------------- collecting

    private void Add(byte rootId, string path, string? newPath)
    {
        if (string.IsNullOrEmpty(path))
        {
            return;
        }

        lock (_gate)
        {
            if (_disposed || !_watched.ContainsKey(rootId))
            {
                return;
            }

            var item = new PendingItem(rootId, path, newPath, _time.GetUtcNow() + _settle);
            if (newPath is null)
            {
                // Collected per path: a second event for a waiting path changes nothing, the reconcile reads the
                // state of the moment it runs.
                if (!_pendingPaths.Add(item.Key))
                {
                    return;
                }
            }

            _pending.Add(item);
            Monitor.PulseAll(_gate);
        }
    }

    private void OnSourceError(byte rootId, Exception ex)
    {
        _fullScanNeeded?.Invoke(rootId);
        if (ex is InternalBufferOverflowException)
        {
            // The watcher keeps running; only events were lost. The full scan brings the location back in line.
            _log?.Invoke($"Watcher buffer overflow on location {rootId}; the location is scanned again", null);
            return;
        }

        // The watcher stopped (e.g. the folder was removed or renamed). Try once to start it again.
        _log?.Invoke($"Watcher of location {rootId} failed; restarting it", ex);
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(1), _time, _cancel.Token).ConfigureAwait(false);
                if (_store.GetRoot(rootId) is { } root && IsWatching(rootId))
                {
                    Watch(root);
                }
            }
            catch (OperationCanceledException)
            {
                // disposed
            }
            catch (Exception restartError)
            {
                _log?.Invoke($"Restarting the watcher of location {rootId} failed", restartError);
            }
        });
    }

    // ---------------------------------------------------------------- worker

    private void WorkerLoop()
    {
        while (true)
        {
            List<PendingItem> batch;
            lock (_gate)
            {
                while (true)
                {
                    if (_disposed)
                    {
                        _processing = false;
                        Monitor.PulseAll(_gate);
                        return;
                    }

                    if (_pending.Count == 0)
                    {
                        Monitor.Wait(_gate);
                        continue;
                    }

                    // Due times grow with the arrival order, so the first item is the earliest.
                    var wait = _pending[0].Due - _time.GetUtcNow();
                    if (wait > TimeSpan.Zero)
                    {
                        Monitor.Wait(_gate, wait < TimeSpan.FromMilliseconds(1) ? TimeSpan.FromMilliseconds(1) : wait);
                        continue;
                    }

                    break;
                }

                var now = _time.GetUtcNow();
                int count = _pending.FindIndex(p => p.Due > now);
                if (count < 0)
                {
                    count = _pending.Count;
                }

                batch = _pending.GetRange(0, count);
                _pending.RemoveRange(0, count);
                foreach (var item in batch)
                {
                    if (item.NewPath is null)
                    {
                        _pendingPaths.Remove(item.Key);
                    }
                }

                _processing = true;
            }

            bool changed = false;
            try
            {
                foreach (var group in batch.GroupBy(i => i.RootId))
                {
                    changed |= ProcessLocation(group.Key, [.. group]);
                }
            }
            catch (OperationCanceledException) when (_cancel.IsCancellationRequested)
            {
                // disposed while working
            }
            finally
            {
                lock (_gate)
                {
                    _processing = false;
                    Monitor.PulseAll(_gate);
                }
            }

            if (changed)
            {
                try
                {
                    Changed?.Invoke();
                }
                catch (Exception ex)
                {
                    _log?.Invoke("A listener of the watcher failed", ex);
                }
            }
        }
    }

    private bool ProcessLocation(byte rootId, List<PendingItem> items)
    {
        if (!IsWatching(rootId))
        {
            return false; // unwatched (e.g. being removed) while the events waited
        }

        using var hold = _locks.Acquire(rootId, _cancel.Token);
        if (_store.GetRoot(rootId) is not { } root || root.LastScanStatus == ScanStatus.Never)
        {
            return false;
        }

        var work = new LocationWork(this, root, _excludes());
        foreach (var item in items)
        {
            _cancel.Token.ThrowIfCancellationRequested();
            try
            {
                if (item.NewPath is { } newPath)
                {
                    work.Rename(item.Path, newPath);
                }
                else
                {
                    work.Reconcile(item.Path);
                }
            }
            catch (OperationCanceledException) when (_cancel.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // One broken event must not stop the watcher. The next scan corrects whatever was missed.
                _log?.Invoke($"Handling the change of {item.Path} failed", ex);
            }
        }

        return work.Changed;
    }

    private readonly record struct PendingItem(byte RootId, string Path, string? NewPath, DateTimeOffset Due)
    {
        public string Key => $"{RootId}|{Path}";
    }

    private sealed record Watched(string Path, IChangeSource Source);

    /// <summary>The work on one location within one batch (runs under the location lock).</summary>
    private sealed class LocationWork(LocalWatcher owner, RootInfo root, ExcludeRules excludes)
    {
        private readonly string _rootPath = TrimPath(root.Path);
        private readonly Dictionary<string, (IReadOnlyList<RawEntry>? Listing, Exception? Error)> _listings =
            new(StringComparer.OrdinalIgnoreCase);

        private readonly HashSet<string> _done = new(StringComparer.OrdinalIgnoreCase);

        public bool Changed { get; private set; }

        private IndexStore Store => owner._store;

        /// <summary>Reconciles a path and, if needed, its ancestors (when the event came before the folder was known).</summary>
        public void Reconcile(string path)
        {
            var todo = new Stack<string>();
            todo.Push(TrimPath(path));
            while (todo.Count > 0)
            {
                string current = todo.Pop();
                if (_done.Add(current) && ReconcileOne(current) is { } parent)
                {
                    todo.Push(parent);
                }
            }
        }

        public void Rename(string oldPath, string newPath)
        {
            oldPath = TrimPath(oldPath);
            newPath = TrimPath(newPath);
            bool oldInside = IsStrictlyBelow(oldPath);
            bool newInside = IsStrictlyBelow(newPath);
            if (oldInside && newInside && !PrefilterExcluded(oldPath) && !PrefilterExcluded(newPath) && TryMoveDirectly(oldPath, newPath))
            {
                _done.Add(oldPath);
                _done.Add(newPath);
                return;
            }

            // Not possible directly: reconcile both paths. They may have been reconciled earlier in this batch, before
            // the rename happened, so they are checked again.
            _done.Remove(oldPath);
            _done.Remove(newPath);
            if (oldInside)
            {
                Reconcile(oldPath);
            }

            if (newInside)
            {
                Reconcile(newPath);
            }
        }

        /// <summary>Brings one path in line. Returns a parent path that must be reconciled instead, or null.</summary>
        private string? ReconcileOne(string path)
        {
            if (!IsStrictlyBelow(path) || PrefilterExcluded(path))
            {
                return null;
            }

            string parent = TrimPath(Path.GetDirectoryName(path) ?? string.Empty);
            string name = Path.GetFileName(path);
            if (parent.Length == 0 || name.Length == 0)
            {
                return null;
            }

            if (Store.FindEntryId(parent) is not { } parentId)
            {
                // The folder above is not known (yet): its own event may be lost or still waiting. Reconciling it covers
                // this path too, because a new folder is scanned as a subtree. Ends at the location, which is known.
                return IsStrictlyBelow(parent) ? parent : null;
            }

            if (Store.GetEntry(parentId) is not { IsDirectory: true })
            {
                // Known as a file but now the parent of something: reconcile the parent itself.
                return IsStrictlyBelow(parent) ? parent : null;
            }

            var (listing, error) = List(parent);
            if (listing is null)
            {
                // Never remove anything after a failed listing (PLAN.md 2.5). If the parent is gone, its own parent
                // decides about it.
                return error is DirectoryNotFoundException && IsStrictlyBelow(parent) ? parent : null;
            }

            var known = Store.GetKnownChildren(parentId)
                .Where(k => string.Equals(k.Name, name, StringComparison.OrdinalIgnoreCase)).ToList();
            RawEntry? raw = Find(listing, name);

            if (raw is not { } entry || excludes.IsExcluded(parent, entry))
            {
                // The complete listing of the parent does not show it (or it is excluded): remove what is known.
                foreach (var k in known)
                {
                    Store.DeleteSubtree(k.Id);
                    Changed = true;
                }

                return null;
            }

            var match = known.Find(k => string.Equals(k.Name, entry.Name, StringComparison.Ordinal));
            if (match == default && known.Count > 0)
            {
                match = known[0];
            }

            if (match != default && match.IsDirectory == entry.IsDirectory)
            {
                if (!string.Equals(match.Name, entry.Name, StringComparison.Ordinal))
                {
                    Store.Rename(match.Id, entry.Name); // only the case changed
                    Changed = true;
                }

                UpdateIfDifferent(match.Id, match.Size, match.MTimeUnixSeconds, match.Attributes, entry);
                return null;
            }

            if (match != default)
            {
                Store.DeleteSubtree(match.Id); // a file became a folder or the other way round
                Changed = true;
            }

            Insert(parentId, Path.Join(parent, entry.Name), entry);
            return null;
        }

        private bool TryMoveDirectly(string oldPath, string newPath)
        {
            if (Store.FindEntryId(oldPath) is not { } id || Store.FindEntryId(newPath) is not null)
            {
                return false; // old path unknown, or new path known already (this includes a change of case only)
            }

            string oldParent = TrimPath(Path.GetDirectoryName(oldPath) ?? string.Empty);
            string newParent = TrimPath(Path.GetDirectoryName(newPath) ?? string.Empty);
            if (Store.FindEntryId(newParent) is not { } newParentId || Store.GetEntry(newParentId) is not { IsDirectory: true }
                || Store.GetEntry(id) is not { ParentId: > 0 } stored)
            {
                return false;
            }

            // Both listings must confirm the move: the old name is gone, the new one is there with the same kind.
            var (oldListing, _) = List(oldParent);
            var (newListing, _) = List(newParent);
            if (oldListing is null || newListing is null || Find(oldListing, Path.GetFileName(oldPath)) is not null)
            {
                return false;
            }

            if (Find(newListing, Path.GetFileName(newPath)) is not { } entry || excludes.IsExcluded(newParent, entry)
                || entry.IsDirectory != stored.IsDirectory)
            {
                return false;
            }

            try
            {
                bool moved = newParentId == stored.ParentId
                    ? Store.Rename(id, entry.Name)
                    : Store.MoveAndRename(id, newParentId, entry.Name);
                if (!moved)
                {
                    return false;
                }
            }
            catch (ArgumentException)
            {
                return false; // e.g. the new parent is in another location; reconciling both paths handles it
            }

            Changed = true;
            UpdateIfDifferent(id, stored.Size, stored.MTimeUnixSeconds, stored.Attributes, entry);
            return true;
        }

        private void Insert(int parentId, string path, RawEntry entry)
        {
            int id = Store.AllocateId();
            var batch = new ChangeBatch();
            batch.Inserts.Add(new EntryInsert(
                id, parentId, entry.Name, entry.IsDirectory, entry.IsDirectory ? 0 : entry.Size,
                IndexStore.ToUnixSeconds(entry.LastWriteUtc), entry.Attributes));
            Store.Apply(root.Id, batch);
            Changed = true;

            bool isLink = (entry.Attributes & FileAttributes.ReparsePoint) != 0 && owner._linkDetector.IsLink(path);
            if (entry.IsDirectory && !isLink)
            {
                // Moving a folder in reports only the folder: scan what is inside (PLAN.md 7.6).
                var scanner = new Scanner(Store, owner._lister, excludes, new ScannerOptions { LinkDetector = owner._linkDetector });
                scanner.ScanSubtree(path, owner._cancel.Token);
            }
        }

        private void UpdateIfDifferent(int id, long size, long mtime, FileAttributes attributes, RawEntry entry)
        {
            long newSize = entry.IsDirectory ? 0 : entry.Size;
            long newMtime = IndexStore.ToUnixSeconds(entry.LastWriteUtc);
            if (size == newSize && mtime == newMtime && attributes == entry.Attributes)
            {
                return;
            }

            var batch = new ChangeBatch();
            batch.Updates.Add(new EntryUpdate(id, newSize, newMtime, entry.Attributes));
            Store.Apply(root.Id, batch);
            Changed = true;
        }

        private (IReadOnlyList<RawEntry>? Listing, Exception? Error) List(string directory)
        {
            if (!_listings.TryGetValue(directory, out var result))
            {
                try
                {
                    result = (owner._lister.List(ForListing(directory)), null);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                    or System.Security.SecurityException or ArgumentException or NotSupportedException)
                {
                    result = (null, ex);
                }

                _listings[directory] = result;
            }

            return result;
        }

        private static RawEntry? Find(IReadOnlyList<RawEntry> listing, string name)
        {
            RawEntry? loose = null;
            foreach (var e in listing)
            {
                if (string.Equals(e.Name, name, StringComparison.Ordinal))
                {
                    return e;
                }

                if (loose is null && string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    loose = e;
                }
            }

            return loose;
        }

        /// <summary>True if a folder between the location and the path is excluded (cheap check without listing).</summary>
        private bool PrefilterExcluded(string path)
        {
            string rest = path[_rootPath.Length..].TrimStart('\\', '/');
            string[] parts = rest.Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries);
            string current = ForListing(_rootPath);
            for (int i = 0; i < parts.Length - 1; i++)
            {
                if (excludes.IsExcluded(current, parts[i], true, FileAttributes.Directory))
                {
                    return true;
                }

                current = Path.Join(current, parts[i]);
            }

            return false;
        }

        private bool IsStrictlyBelow(string path)
            => path.Length > _rootPath.Length
                && path.StartsWith(_rootPath, StringComparison.OrdinalIgnoreCase)
                && path[_rootPath.Length] is '\\' or '/';

        /// <summary>"C:" means the current directory of drive C; a drive root is listed as "C:\".</summary>
        private static string ForListing(string path) => path.Length == 2 && path[1] == ':' ? path + "\\" : path;
    }

    /// <summary>Removes trailing separators (a drive root "C:\" becomes "C:").</summary>
    private static string TrimPath(string path) => path.TrimEnd('\\', '/');
}
