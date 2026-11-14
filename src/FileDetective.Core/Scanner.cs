namespace FileDetective.Core;

/// <summary>How gently network shares are scanned (PLAN.md 7.4, setting <c>networkPoliteness</c>).</summary>
public enum NetworkPoliteness
{
    /// <summary>Pause = listing duration, at most 250 ms.</summary>
    Normal = 0,

    /// <summary>Pause = four times the listing duration, at least 20 ms, at most 1 s.</summary>
    VeryGentle = 1,
}

/// <summary>How a scan ended.</summary>
public enum ScanOutcome
{
    /// <summary>Every directory was listed and compared.</summary>
    Complete,

    /// <summary>Done, but some directories could not be listed; their known subtrees were left unchanged.</summary>
    CompleteWithErrors,

    /// <summary>Cancelled; the batches written so far stay valid.</summary>
    Cancelled,

    /// <summary>The location (or the start folder of a subtree scan) could not be listed; nothing was changed.</summary>
    RootUnreachable,
}

/// <summary>
/// Statistics of one scan (PLAN.md 7.4): listed directories, entries, duration, sum of pauses, errors; plus the changes
/// written to the index. <see cref="Entries"/> counts the entries listed and kept (after exclusions),
/// <see cref="Excluded"/> the skipped ones; <see cref="Updated"/> includes case-only renames.
/// </summary>
public sealed record ScanStatistics(
    ScanOutcome Outcome,
    int DirectoriesListed,
    int FailedDirectories,
    long Entries,
    long Excluded,
    int LinksNotEntered,
    TimeSpan Duration,
    TimeSpan TotalPause,
    int Inserted,
    int Updated,
    int Deleted);

/// <summary>Optional settings of a <see cref="Scanner"/>.</summary>
public sealed class ScannerOptions
{
    /// <summary>Changes per <see cref="IndexStore.Apply"/> transaction (PLAN.md 7.2: 5,000).</summary>
    public int BatchSize { get; init; } = Scanner.DefaultBatchSize;

    public NetworkPoliteness Politeness { get; init; } = NetworkPoliteness.Normal;

    /// <summary>Decides whether a reparse-point folder is a link; default <see cref="ReparseTagLinkDetector"/>.</summary>
    public ILinkDetector? LinkDetector { get; init; }

    /// <summary>Clock and pause; default <see cref="SystemScanClock"/>.</summary>
    public IScanClock? Clock { get; init; }

    /// <summary>
    /// Called on the scanning thread after every processed directory with the number of entries counted so far
    /// (stage 6: progress in the tray status line and the footer). Must be cheap and must not throw.
    /// </summary>
    public Action<long>? Progress { get; init; }
}

/// <summary>
/// Walks a location or a subtree with its own stack (no recursion) and brings the index in line with the file system
/// (PLAN.md 7.2). Strictly single-threaded; one instance rejects a second call while a scan runs.
/// </summary>
/// <remarks>
/// Safety rules (PLAN.md 2.5, 7.2):
/// <list type="bullet">
/// <item>A directory that cannot be listed is counted and its known subtree stays exactly as it is. This includes
/// <see cref="DirectoryNotFoundException"/> for a folder that its parent listing still showed: the disappearance is
/// detected by the next successful listing of the parent.</item>
/// <item>Only a successfully and completely listed directory removes known children that are missing.</item>
/// <item>If the location itself cannot be listed, the scan stops at once and changes nothing (not even the state in
/// <c>roots</c>).</item>
/// </list>
/// Entries are inserted parent first, so every written batch is consistent on its own; cancelling between directories
/// writes the pending batch and leaves a valid index.
/// </remarks>
public sealed class Scanner
{
    public const int DefaultBatchSize = 5_000;

    /// <summary>Upper limit of a pause at the normal level.</summary>
    public static readonly TimeSpan NormalMaxPause = TimeSpan.FromMilliseconds(250);

    /// <summary>Lower and upper limit of a pause at the "very gentle" level.</summary>
    public static readonly TimeSpan GentleMinPause = TimeSpan.FromMilliseconds(20);

    public static readonly TimeSpan GentleMaxPause = TimeSpan.FromSeconds(1);

    private readonly IndexStore _store;
    private readonly IDirectoryLister _lister;
    private readonly ExcludeRules _excludes;
    private readonly ILinkDetector _linkDetector;
    private readonly IScanClock _clock;
    private readonly int _batchSize;
    private readonly NetworkPoliteness _politeness;
    private readonly Action<long>? _progress;
    private int _running;

    public Scanner(IndexStore store, IDirectoryLister lister, ExcludeRules excludeRules, ScannerOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(lister);
        ArgumentNullException.ThrowIfNull(excludeRules);
        options ??= new ScannerOptions();
        ArgumentOutOfRangeException.ThrowIfLessThan(options.BatchSize, 1);
        _store = store;
        _lister = lister;
        _excludes = excludeRules;
        _linkDetector = options.LinkDetector ?? ReparseTagLinkDetector.Instance;
        _clock = options.Clock ?? SystemScanClock.Instance;
        _batchSize = options.BatchSize;
        _politeness = options.Politeness;
        _progress = options.Progress;
    }

    /// <summary>True while a scan of this instance runs.</summary>
    public bool IsRunning => Volatile.Read(ref _running) != 0;

    /// <summary>
    /// The pause after listing one directory (PLAN.md 7.4). Network locations only; decision: removable drives are
    /// local hardware without a server to protect and get no pauses, like local locations.
    /// </summary>
    public static TimeSpan PauseFor(RootKind kind, NetworkPoliteness politeness, TimeSpan listingDuration)
    {
        if (kind != RootKind.Network)
        {
            return TimeSpan.Zero;
        }

        if (listingDuration < TimeSpan.Zero)
        {
            listingDuration = TimeSpan.Zero;
        }

        return politeness == NetworkPoliteness.VeryGentle
            ? Clamp(listingDuration * 4, GentleMinPause, GentleMaxPause)
            : Clamp(listingDuration, TimeSpan.Zero, NormalMaxPause);
    }

    /// <summary>
    /// Scans a whole location and keeps its scan state in <c>roots</c> up to date: Running at the start, then
    /// Complete, CompleteWithErrors or Aborted together with the number of failed directories. An unreachable location
    /// changes nothing.
    /// </summary>
    /// <exception cref="ArgumentException">The location does not exist.</exception>
    /// <exception cref="InvalidOperationException">This scanner is already running.</exception>
    public ScanStatistics ScanRoot(byte rootId, CancellationToken cancellationToken = default)
    {
        EnterRun();
        try
        {
            var root = _store.GetRoot(rootId) ?? throw new ArgumentException($"Location {rootId} does not exist.", nameof(rootId));
            var run = new Run(this, root, cancellationToken);
            return run.Execute(root.RootEntryId, NormalizeDirectoryPath(root.Path), updateRootState: true);
        }
        finally
        {
            Volatile.Write(ref _running, 0);
        }
    }

    /// <summary>
    /// Scans the subtree below a known folder (e.g. a folder the watcher reports, stage 6). Does not touch the scan
    /// state of the location. If the start folder cannot be listed, nothing changes.
    /// </summary>
    /// <exception cref="ArgumentException">The path is not a known folder of the index.</exception>
    /// <exception cref="InvalidOperationException">This scanner is already running.</exception>
    public ScanStatistics ScanSubtree(string directoryPath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(directoryPath);
        EnterRun();
        try
        {
            string path = NormalizeDirectoryPath(directoryPath);
            var entry = (_store.FindEntryId(path) is { } id ? _store.GetEntry(id) : null)
                ?? throw new ArgumentException($"'{directoryPath}' is not in the index.", nameof(directoryPath));
            if (!entry.IsDirectory)
            {
                throw new ArgumentException($"'{directoryPath}' is not a folder.", nameof(directoryPath));
            }

            var root = _store.GetRoot(entry.RootId)
                ?? throw new ArgumentException($"The location of '{directoryPath}' does not exist.", nameof(directoryPath));
            var run = new Run(this, root, cancellationToken);

            // A link (junction, symbolic link) is never entered, also not as the start of a subtree scan.
            if (entry.ParentId != 0 && (entry.Attributes & FileAttributes.ReparsePoint) != 0 && _linkDetector.IsLink(path))
            {
                return run.Finish(ScanOutcome.Complete);
            }

            return run.Execute(entry.Id, path, updateRootState: false);
        }
        finally
        {
            Volatile.Write(ref _running, 0);
        }
    }

    private void EnterRun()
    {
        if (Interlocked.CompareExchange(ref _running, 1, 0) != 0)
        {
            throw new InvalidOperationException("A scan of this scanner is already running.");
        }
    }

    private static TimeSpan Clamp(TimeSpan value, TimeSpan min, TimeSpan max)
        => value < min ? min : value > max ? max : value;

    private static string NormalizeDirectoryPath(string path)
    {
        // "C:" alone would mean the current directory of drive C.
        return path.Length == 2 && path[1] == ':' ? path + "\\" : path;
    }

    /// <summary>
    /// Listing failures that count as "directory could not be listed" (PLAN.md 7.2 point 1). IOException covers
    /// DirectoryNotFoundException, PathTooLongException and network errors; the others come from odd names or
    /// security settings. Anything else is a bug and propagates.
    /// </summary>
    private static bool IsListingFailure(Exception ex)
        => ex is IOException or UnauthorizedAccessException or System.Security.SecurityException
            or ArgumentException or NotSupportedException;

    private readonly record struct WorkItem(int EntryId, string Path, bool IsNew);

    /// <summary>State of one scan.</summary>
    private sealed class Run(Scanner owner, RootInfo root, CancellationToken cancellationToken)
    {
        private readonly Stack<WorkItem> _stack = new();
        private readonly TimeSpan _start = owner._clock.Elapsed;
        private ChangeBatch _batch = new();
        private int _listed;
        private int _failed;
        private long _entries;
        private long _excluded;
        private int _links;
        private TimeSpan _pauses;
        private int _inserted;
        private int _updated;
        private int _deleted;

        public ScanStatistics Execute(int startEntryId, string startPath, bool updateRootState)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return Finish(ScanOutcome.Cancelled);
            }

            DateTimeOffset startedUtc = owner._clock.UtcNow;

            // The start folder is listed before anything is written: if the location is not reachable, nothing changes.
            IReadOnlyList<RawEntry> startListing;
            try
            {
                startListing = ListWithPause(startPath);
            }
            catch (Exception ex) when (IsListingFailure(ex))
            {
                _failed++;
                return Finish(ScanOutcome.RootUnreachable);
            }
            catch (OperationCanceledException)
            {
                return Finish(ScanOutcome.Cancelled);
            }

            if (updateRootState)
            {
                owner._store.UpdateRoot(CurrentRoot() with
                {
                    LastScanStartedUtc = startedUtc.ToUnixTimeSeconds(),
                    LastScanStatus = ScanStatus.Running,
                });
            }

            ScanOutcome outcome;
            bool done = false;
            try
            {
                Process(new WorkItem(startEntryId, startPath, IsNew: false), startListing);
                while (_stack.Count > 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var item = _stack.Pop();
                    IReadOnlyList<RawEntry> listing;
                    try
                    {
                        listing = ListWithPause(item.Path);
                    }
                    catch (Exception ex) when (IsListingFailure(ex))
                    {
                        // PLAN.md 7.2 point 1: count it, leave the known subtree exactly as it is, go on.
                        _failed++;
                        continue;
                    }

                    Process(item, listing);
                }

                Flush();
                outcome = _failed > 0 ? ScanOutcome.CompleteWithErrors : ScanOutcome.Complete;
                done = true;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // Cancellation is only observed between directories (and during a pause, before the listing is used),
                // so the pending batch holds complete directories and is written.
                Flush();
                outcome = ScanOutcome.Cancelled;
                done = true;
            }
            finally
            {
                if (!done && updateRootState)
                {
                    // Unexpected error (bug, database failure): the batches written so far stay valid, the state says aborted.
                    TryWriteState(ScanStatus.Aborted, completedUtc: null);
                }
            }

            if (updateRootState)
            {
                if (outcome == ScanOutcome.Cancelled)
                {
                    TryWriteState(ScanStatus.Aborted, completedUtc: null);
                }
                else
                {
                    TryWriteState(
                        outcome == ScanOutcome.Complete ? ScanStatus.Complete : ScanStatus.CompleteWithErrors,
                        owner._clock.UtcNow.ToUnixTimeSeconds());
                }
            }

            return Finish(outcome);
        }

        public ScanStatistics Finish(ScanOutcome outcome)
            => new(outcome, _listed, _failed, _entries, _excluded, _links, owner._clock.Elapsed - _start, _pauses, _inserted, _updated, _deleted);

        private RootInfo CurrentRoot() => owner._store.GetRoot(root.Id) ?? root;

        private void TryWriteState(ScanStatus status, long? completedUtc)
        {
            try
            {
                var current = CurrentRoot();
                owner._store.UpdateRoot(current with
                {
                    LastScanStatus = status,
                    LastScanCompletedUtc = completedUtc ?? current.LastScanCompletedUtc,
                    LastScanFailedDirs = _failed,
                });
            }
            catch (Exception) when (status == ScanStatus.Aborted)
            {
                // Writing the aborted state after an unexpected error must not hide that error.
            }
        }

        private IReadOnlyList<RawEntry> ListWithPause(string path)
        {
            TimeSpan before = owner._clock.Elapsed;
            IReadOnlyList<RawEntry>? listing = null;
            try
            {
                listing = owner._lister.List(path);
                _listed++;
            }
            finally
            {
                // PLAN.md 7.4: after every directory (also a failed one, it cost the server as much) a pause as long
                // as the measured listing.
                TimeSpan pause = PauseFor(root.Kind, owner._politeness, owner._clock.Elapsed - before);
                if (pause > TimeSpan.Zero)
                {
                    _pauses += pause;
                    owner._clock.Pause(pause, cancellationToken);
                }
            }

            return listing;
        }

        private void Process(WorkItem dir, IReadOnlyList<RawEntry> listing)
        {
            // Step 2: exclusions.
            var children = new List<RawEntry>(listing.Count);
            foreach (var e in listing)
            {
                if (owner._excludes.IsExcluded(dir.Path, e))
                {
                    _excluded++;
                }
                else
                {
                    children.Add(e);
                }
            }

            _entries += children.Count;

            // Step 3: compare with the known children. Skipped for folders inserted by this scan (in particular the
            // whole first scan of a location), because nothing can be known below them.
            Dictionary<string, List<KnownChild>>? known = null;
            if (!dir.IsNew)
            {
                var stored = owner._store.GetKnownChildren(dir.EntryId);
                known = new Dictionary<string, List<KnownChild>>(stored.Count, StringComparer.OrdinalIgnoreCase);
                foreach (var k in stored)
                {
                    if (!known.TryGetValue(k.Name, out var bucket))
                    {
                        known[k.Name] = bucket = new List<KnownChild>(1);
                    }

                    bucket.Add(k);
                }
            }

            foreach (var e in children)
            {
                long mtime = IndexStore.ToUnixSeconds(e.LastWriteUtc);
                long size = e.IsDirectory ? 0 : e.Size;
                string path = Path.Join(dir.Path, e.Name);
                KnownChild? match = known is null ? null : TakeMatch(known, e.Name);

                if (match is { } k && k.IsDirectory != e.IsDirectory)
                {
                    // A file became a folder or the other way round: replace it.
                    AddDelete(k.Id);
                    match = null;
                }

                if (match is { } m)
                {
                    if (!string.Equals(m.Name, e.Name, StringComparison.Ordinal))
                    {
                        // Only the case of the name changed (Name matched OrdinalIgnoreCase). Rare; one row, written directly.
                        owner._store.Rename(m.Id, e.Name);
                        _updated++;
                    }

                    if (m.Size != size || m.MTimeUnixSeconds != mtime || m.Attributes != e.Attributes)
                    {
                        _batch.Updates.Add(new EntryUpdate(m.Id, size, mtime, e.Attributes));
                        FlushIfFull();
                    }

                    if (e.IsDirectory)
                    {
                        Descend(m.Id, path, e, isNew: false);
                    }
                }
                else
                {
                    int id = owner._store.AllocateId();
                    _batch.Inserts.Add(new EntryInsert(id, dir.EntryId, e.Name, e.IsDirectory, size, mtime, e.Attributes));
                    FlushIfFull();
                    if (e.IsDirectory)
                    {
                        Descend(id, path, e, isNew: true);
                    }
                }
            }

            if (known is not null)
            {
                // Known children that the complete listing does not show any more (or that are excluded now).
                foreach (var bucket in known.Values)
                {
                    foreach (var k in bucket)
                    {
                        AddDelete(k.Id);
                    }
                }
            }

            owner._progress?.Invoke(_entries);
        }

        private static KnownChild? TakeMatch(Dictionary<string, List<KnownChild>> known, string name)
        {
            if (!known.TryGetValue(name, out var bucket))
            {
                return null;
            }

            // Prefer the exact spelling (case-sensitive folders can hold "a" and "A").
            int index = bucket.FindIndex(k => string.Equals(k.Name, name, StringComparison.Ordinal));
            if (index < 0)
            {
                index = 0;
            }

            var result = bucket[index];
            bucket.RemoveAt(index);
            if (bucket.Count == 0)
            {
                known.Remove(name);
            }

            return result;
        }

        private void Descend(int id, string path, in RawEntry entry, bool isNew)
        {
            if ((entry.Attributes & FileAttributes.ReparsePoint) != 0 && owner._linkDetector.IsLink(path))
            {
                // PLAN.md 7.2 point 4: recorded, never entered. Children a known folder had before it became a link
                // are removed, because they are no longer below this folder in the index's sense.
                _links++;
                if (!isNew)
                {
                    foreach (var k in owner._store.GetKnownChildren(id))
                    {
                        AddDelete(k.Id);
                    }
                }

                return;
            }

            _stack.Push(new WorkItem(id, path, isNew));
        }

        private void AddDelete(int id)
        {
            _batch.DeleteSubtrees.Add(id);
            FlushIfFull();
        }

        private void FlushIfFull()
        {
            if (_batch.Count >= owner._batchSize)
            {
                Flush();
            }
        }

        private void Flush()
        {
            if (_batch.Count == 0)
            {
                return;
            }

            var result = owner._store.Apply(root.Id, _batch);
            _inserted += result.Inserted;
            _updated += result.Updated;
            _deleted += result.Deleted;
            _batch = new ChangeBatch();
        }
    }
}
