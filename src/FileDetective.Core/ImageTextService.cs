namespace FileDetective.Core;

/// <summary>Progress of the image text (shown in the settings: "Image text: 120 of 3,400 recognized").</summary>
/// <param name="Recognized">Images whose text is known (also an empty text).</param>
/// <param name="Total">Images in the folders that are read (after the size, ending and cloud rules).</param>
/// <param name="Pending">Images waiting in the queue.</param>
public readonly record struct ImageTextProgress(bool Enabled, int Recognized, int Total, int Pending);

/// <summary>Optional parts of <see cref="ImageTextService"/>.</summary>
public sealed class ImageTextServiceOptions
{
    /// <summary>
    /// True if a path lies on a local drive (not a share, not a network drive, not a removable drive). Folders and images
    /// for which it says false are never opened. Default: refuses UNC paths only.
    /// </summary>
    public Func<string, bool> IsLocalPath { get; init; } = path => !path.StartsWith(@"\\", StringComparison.Ordinal);

    /// <summary>Runs the queue on its own low-priority thread. Tests switch it off and call <see cref="ImageTextService.ProcessPending"/>.</summary>
    public bool RunWorker { get; init; } = true;

    /// <summary>Receives log lines and errors.</summary>
    public Action<string, Exception?>? Log { get; init; }
}

/// <summary>
/// Keeps the text of the images in the image text folders current (stage 11, PLAN.md 11.3). New and changed images arrive
/// through <see cref="IndexStore.Changed"/> (scans and the watcher); switching on and the program start fetch missing and
/// invalid texts. Reading runs on one thread with the lowest priority, can be cancelled at exit, and opens a file only if
/// it is local, an image of the allowed endings and sizes, and not in the cloud.
/// </summary>
/// <remarks>
/// While the image text is off nothing is queued, no image is read and nothing is in memory; switching off deletes every
/// stored text at once. Removing a folder deletes the texts of its images at once. A text is written only for the
/// configuration it was read for: a change of the settings while an image is being read drops that result.
/// </remarks>
public sealed class ImageTextService : IDisposable
{
    private readonly IndexStore _store;
    private readonly MemoryIndex _memory;
    private readonly ImageTextIndex _texts;
    private readonly IImageTextRecognizer _recognizer;
    private readonly ILocalFileAccess _files;
    private readonly IEntryResolver _resolver;
    private readonly ImageTextServiceOptions _options;
    private readonly object _gate = new();
    private readonly Queue<int> _jobs = new();
    private readonly HashSet<int> _queued = [];
    private readonly HashSet<int> _known = [];
    private readonly Queue<IndexChange> _changes = new();
    private readonly AutoResetEvent _signal = new(false);
    private IReadOnlyList<string> _folders = [];
    private HashSet<int>? _folderIds;
    private bool _enabled;
    private bool _catchUpRequested;
    private long _generation;
    private CancellationTokenSource? _workerCancel;
    private Task? _worker;
    private bool _disposed;

    public ImageTextService(
        IndexStore store,
        MemoryIndex memory,
        ImageTextIndex texts,
        IImageTextRecognizer recognizer,
        ILocalFileAccess files,
        IEntryResolver resolver,
        ImageTextServiceOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(memory);
        ArgumentNullException.ThrowIfNull(texts);
        ArgumentNullException.ThrowIfNull(recognizer);
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(resolver);
        _store = store;
        _memory = memory;
        _texts = texts;
        _recognizer = recognizer;
        _files = files;
        _resolver = resolver;
        _options = options ?? new ImageTextServiceOptions();
        _store.Changed += OnStoreChanged;
    }

    /// <summary>Raised on a background thread when the progress changed.</summary>
    public event Action? ProgressChanged;

    public bool Enabled
    {
        get
        {
            lock (_gate)
            {
                return _enabled;
            }
        }
    }

    /// <summary>The folders in force (normalized, local only).</summary>
    public IReadOnlyList<string> Folders
    {
        get
        {
            lock (_gate)
            {
                return _folders;
            }
        }
    }

    /// <summary>True if Windows can read text (a recognition language exists for the user profile).</summary>
    public bool RecognizerAvailable => _recognizer.IsAvailable;

    public ImageTextProgress Progress
    {
        get
        {
            lock (_gate)
            {
                return new ImageTextProgress(_enabled, _enabled ? _texts.Count : 0, _enabled ? _known.Count : 0, _jobs.Count);
            }
        }
    }

    /// <summary>
    /// Applies the settings. Off: the queue is emptied, the memory cleared and every stored text deleted at once. On: texts of
    /// images outside the new folders are deleted at once, then missing and invalid texts are fetched in the background.
    /// Folders that are not local are dropped. Does database work; call it from a background thread.
    /// </summary>
    public void Configure(bool enabled, IReadOnlyList<string> folders)
    {
        ArgumentNullException.ThrowIfNull(folders);
        ObjectDisposedException.ThrowIf(_disposed, this);
        var clean = ImageTextFolders.Clean(folders).Where(_options.IsLocalPath).ToList();
        lock (_gate)
        {
            _generation++;
            _enabled = enabled;
            _folders = enabled ? clean : [];
            _folderIds = null;
            _jobs.Clear();
            _queued.Clear();
            _changes.Clear();
            _known.Clear();
            _catchUpRequested = enabled;
            if (!enabled)
            {
                _texts.Clear();
                int removed = _store.DeleteAllImageTexts();
                if (removed > 0)
                {
                    Log($"Image text switched off: {removed} stored texts deleted.");
                }
            }
            else
            {
                int removed = PurgeOutsideFolders(clean);
                if (removed > 0)
                {
                    Log($"Image text: {removed} texts of images outside the folders deleted.");
                }
            }
        }

        if (enabled)
        {
            StartWorker();
            _signal.Set();
        }
        else
        {
            StopWorker();
        }

        RaiseProgress();
    }

    /// <summary>
    /// Works off everything waiting: the catch-up after switching on or the start, the reported changes and the queued images,
    /// one after the other, until nothing is left or <paramref name="cancellationToken"/> is cancelled. Runs on the worker
    /// thread; tests call it directly.
    /// </summary>
    public void ProcessPending(CancellationToken cancellationToken = default)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            bool catchUp;
            IndexChange? change = null;
            int job = 0;
            long generation;
            lock (_gate)
            {
                if (!_enabled)
                {
                    return;
                }

                generation = _generation;
                catchUp = _catchUpRequested;
                _catchUpRequested = false;
                if (!catchUp && !_changes.TryDequeue(out change))
                {
                    if (!_recognizer.IsAvailable || !_jobs.TryDequeue(out job))
                    {
                        LogBatch();
                        return;
                    }

                    _queued.Remove(job);
                }
            }

            try
            {
                if (catchUp)
                {
                    CatchUp(generation, cancellationToken);
                }
                else if (change is not null)
                {
                    HandleChange(change, generation);
                }
                else
                {
                    ProcessImage(job, generation, cancellationToken);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                Log("Image text: a step failed", ex);
            }

            RaiseProgress();
        }
    }

    private void OnStoreChanged(IndexChange change)
    {
        lock (_gate)
        {
            if (!_enabled)
            {
                return; // no queue while switched off
            }

            _changes.Enqueue(change);
        }

        _signal.Set();
    }

    // ---------------------------------------------------------------- steps

    /// <summary>
    /// Reads the images of all folders from the directory (no file access), puts valid stored texts into memory, queues
    /// the images without a valid text and deletes stored texts of images that are no longer in the folders.
    /// </summary>
    private void CatchUp(long generation, CancellationToken cancellationToken)
    {
        IReadOnlyList<string> folders;
        lock (_gate)
        {
            folders = _folders;
        }

        var localRoots = _store.GetRoots().Where(r => r.Kind == RootKind.Local).Select(r => r.Id).ToHashSet();
        var candidates = new Dictionary<int, StoredEntry>();
        var folderIds = new HashSet<int>();
        foreach (string folder in folders)
        {
            if (_store.FindEntryId(folder) is not { } folderId)
            {
                continue; // not in the directory yet; its images arrive with the scan
            }

            folderIds.Add(folderId);
            _store.ReadFilesBelow(folderId, entry =>
            {
                if (localRoots.Contains(entry.RootId)
                    && ImageTextRules.Check(entry.Name, entry.Size, entry.Attributes) == ImageSkipReason.None)
                {
                    candidates[entry.Id] = entry;
                }
            }, cancellationToken);
        }

        var stored = _store.ReadImageTexts(cancellationToken);
        var stale = new List<int>();
        var valid = new List<StoredImageText>();
        var missing = new List<int>();
        foreach (var row in stored)
        {
            if (!candidates.TryGetValue(row.EntryId, out var entry))
            {
                stale.Add(row.EntryId);
            }
            else if (row.MatchesEntry(entry.Size, entry.MTimeUnixSeconds))
            {
                valid.Add(row);
            }
        }

        var validIds = valid.Select(v => v.EntryId).ToHashSet();
        foreach (var id in candidates.Keys.Order())
        {
            if (!validIds.Contains(id))
            {
                missing.Add(id);
            }
        }

        lock (_gate)
        {
            if (generation != _generation || !_enabled)
            {
                return;
            }

            _folderIds = folderIds;
            if (stale.Count > 0)
            {
                _store.DeleteImageTexts(stale);
                _texts.RemoveAll(stale);
            }

            foreach (var row in valid)
            {
                _texts.Set(row.EntryId, row.Text, row.MTimeTicks);
            }

            // Texts in memory whose image was not found any more (e.g. it became too large).
            _texts.RemoveAll(_texts.Ids().Where(id => !candidates.ContainsKey(id)).ToList());
            foreach (var id in candidates.Keys)
            {
                _known.Add(id);
            }

            foreach (var id in missing)
            {
                // An invalid text stays out of memory until the image is read again.
                _texts.Remove(id);
                Enqueue(id);
            }
        }

        Log($"Image text: {candidates.Count} images in {folders.Count} folders, {valid.Count} texts valid, {missing.Count} to read, {stale.Count} dropped.");
    }

    private void HandleChange(IndexChange change, long generation)
    {
        if (change.Reset)
        {
            lock (_gate)
            {
                _texts.Clear();
                _known.Clear();
                _jobs.Clear();
                _queued.Clear();
                _folderIds = null;
                _catchUpRequested = true;
            }

            return;
        }

        if (change.Deleted.Count > 0)
        {
            lock (_gate)
            {
                _texts.RemoveAll(change.Deleted);
                foreach (int id in change.Deleted)
                {
                    _known.Remove(id);
                }

                // A removed folder of the list may come back with a new number.
                if (_folderIds is { } ids && change.Deleted.Any(ids.Contains))
                {
                    _folderIds = null;
                }
            }
        }

        if (change.Relocated)
        {
            // A moved or renamed folder can bring images into a folder or take them out: check everything again
            // (directory only, no file is touched).
            lock (_gate)
            {
                _folderIds = null;
                _catchUpRequested = true;
            }
        }

        var written = change.Written.Where(w => w.Name is null || ImageTextRules.IsImageName(w.Name)).ToList();
        if (written.Count == 0)
        {
            return;
        }

        var folderIds = FolderIds();
        lock (_gate)
        {
            if (generation != _generation || !_enabled)
            {
                return;
            }

            foreach (var file in written)
            {
                if (_memory.IsBelowAny(file.Id, folderIds))
                {
                    _known.Add(file.Id);
                    Enqueue(file.Id);
                }
            }
        }
    }

    /// <summary>The entry numbers of the folders; looked up again after a folder was removed, moved or not found yet.</summary>
    private HashSet<int> FolderIds()
    {
        IReadOnlyList<string> folders;
        lock (_gate)
        {
            if (_folderIds is { } known && known.Count == _folders.Count)
            {
                return known;
            }

            folders = _folders;
        }

        var ids = new HashSet<int>();
        foreach (string folder in folders)
        {
            if (_store.FindEntryId(folder) is { } id)
            {
                ids.Add(id);
            }
        }

        lock (_gate)
        {
            _folderIds = ids;
        }

        return ids;
    }

    /// <summary>Reads one image if it has no valid text. The only place that opens an image.</summary>
    private void ProcessImage(int id, long generation, CancellationToken cancellationToken)
    {
        if (_store.GetEntry(id) is not { IsDirectory: false } entry)
        {
            Forget(id, generation, dropText: false);
            return;
        }

        // From the directory first: a cloud file is never even looked at again.
        var reason = ImageTextRules.Check(entry.Name, ImageTextRules.MinSize, entry.Attributes); // size: checked below with the current one
        if (reason != ImageSkipReason.None || _store.GetRoot(entry.RootId) is not { Kind: RootKind.Local })
        {
            Forget(id, generation, dropText: true);
            return;
        }

        string? path = _resolver.Resolve([id], cancellationToken)[0]?.FullPath;
        IReadOnlyList<string> folders;
        lock (_gate)
        {
            folders = _folders;
        }

        if (path is null || !ImageTextFolders.IsBelowAny(path, folders) || !_options.IsLocalPath(path))
        {
            Forget(id, generation, dropText: true);
            return;
        }

        // Current size, time and attributes from the listing of the folder (the file is not opened).
        if (_files.Probe(path) is not { } facts)
        {
            Forget(id, generation, dropText: false);
            return;
        }

        if (ImageTextRules.Check(entry.Name, facts.Size, facts.Attributes) != ImageSkipReason.None)
        {
            Forget(id, generation, dropText: true);
            return;
        }

        if (_store.GetImageTexts([id]).TryGetValue(id, out var row) && row.IsValidFor(facts.Size, facts.LastWriteUtcTicks))
        {
            lock (_gate)
            {
                if (generation == _generation && _enabled)
                {
                    _texts.Set(id, row.Text, row.MTimeTicks);
                }
            }

            return;
        }

        string text;
        var timer = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            using var stream = _files.OpenRead(path);
            text = _recognizer.Recognize(stream, cancellationToken);
        }
        catch (InvalidDataException ex)
        {
            // A damaged or unsupported image: stored with an empty text, so it is not read again until it changes.
            Log($"Image text: {path} cannot be decoded", ex);
            text = string.Empty;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Locked or gone: tried again with the next change or at the next start.
            Log($"Image text: {path} cannot be read", ex);
            return;
        }

        // The image must not have changed while it was read.
        if (_files.Probe(path) is not { } after || after.Size != facts.Size || after.LastWriteUtcTicks != facts.LastWriteUtcTicks)
        {
            lock (_gate)
            {
                if (generation == _generation && _enabled)
                {
                    Enqueue(id);
                }
            }

            return;
        }

        lock (_gate)
        {
            if (generation != _generation || !_enabled)
            {
                return; // the settings changed meanwhile: the result belongs to an older configuration
            }

            if (_store.SaveImageText(new StoredImageText(id, facts.Size, facts.LastWriteUtcTicks, text)))
            {
                _texts.Set(id, text, facts.LastWriteUtcTicks);
                _known.Add(id);
            }
        }

        Interlocked.Increment(ref _batchCount);
        Interlocked.Add(ref _batchTicks, timer.Elapsed.Ticks);
    }

    private int _batchCount;
    private long _batchTicks;

    /// <summary>One log line when the queue has run empty: how many images were read and how long one took on average.</summary>
    private void LogBatch()
    {
        int count = Interlocked.Exchange(ref _batchCount, 0);
        long ticks = Interlocked.Exchange(ref _batchTicks, 0);
        if (count > 0)
        {
            var total = TimeSpan.FromTicks(ticks);
            Log($"Image text: {count} images read in {total.TotalSeconds:0.0} s, {total.TotalMilliseconds / count:0} ms per image.");
        }
    }

    private void Forget(int id, long generation, bool dropText)
    {
        lock (_gate)
        {
            if (generation != _generation)
            {
                return;
            }

            _known.Remove(id);
            if (dropText)
            {
                _texts.Remove(id);
                _store.DeleteImageTexts([id]);
            }
        }
    }

    /// <summary>Deletes the stored texts (and memory) of images that do not lie in <paramref name="folders"/>. Under <see cref="_gate"/>.</summary>
    private int PurgeOutsideFolders(IReadOnlyList<string> folders)
    {
        var rows = _store.ReadImageTexts();
        if (rows.Count == 0)
        {
            _texts.Clear();
            return 0;
        }

        var ids = rows.Select(r => r.EntryId).ToList();
        var details = _resolver.Resolve(ids, CancellationToken.None);
        var outside = new List<int>();
        for (int i = 0; i < ids.Count; i++)
        {
            if (details[i] is not { } d || !ImageTextFolders.IsBelowAny(d.FullPath, folders))
            {
                outside.Add(ids[i]);
            }
        }

        _texts.RemoveAll(outside);
        return _store.DeleteImageTexts(outside);
    }

    private void Enqueue(int id)
    {
        if (_queued.Add(id))
        {
            _jobs.Enqueue(id);
        }
    }

    // ---------------------------------------------------------------- worker

    private void StartWorker()
    {
        if (!_options.RunWorker)
        {
            return;
        }

        lock (_gate)
        {
            if (_workerCancel is not null)
            {
                return; // a worker runs and has not been told to stop
            }

            var cancel = new CancellationTokenSource();
            _workerCancel = cancel;
            _worker = LowPriorityWork.Run(() =>
            {
                WorkerLoop(cancel.Token);
                return 0;
            });
        }
    }

    private void StopWorker()
    {
        CancellationTokenSource? cancel;
        lock (_gate)
        {
            cancel = _workerCancel;
            _workerCancel = null;
        }

        cancel?.Cancel();
        _signal.Set();
    }

    private void WorkerLoop(CancellationToken token)
    {
        var handles = new[] { _signal, token.WaitHandle };
        while (!token.IsCancellationRequested)
        {
            ProcessPending(token);
            WaitHandle.WaitAny(handles);
        }
    }

    private void RaiseProgress()
    {
        try
        {
            ProgressChanged?.Invoke();
        }
        catch (Exception ex)
        {
            Log("Image text: a progress listener failed", ex);
        }
    }

    private void Log(string message, Exception? ex = null)
    {
        try
        {
            _options.Log?.Invoke(message, ex);
        }
        catch (Exception)
        {
            // Logging must never break the work.
        }
    }

    /// <summary>Stops the worker (waits up to 5 s for the image being read) and stops listening to the directory.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _store.Changed -= OnStoreChanged;
        Task? worker;
        CancellationTokenSource? cancel;
        lock (_gate)
        {
            worker = _worker;
            cancel = _workerCancel;
            _workerCancel = null;
        }

        cancel?.Cancel();
        _signal.Set();
        try
        {
            worker?.Wait(TimeSpan.FromSeconds(5));
        }
        catch (AggregateException)
        {
            // Cancelled or already logged.
        }

        // A worker that did not end in time still waits on the signal: then both are left to the end of the process.
        if (worker is null || worker.IsCompleted)
        {
            cancel?.Dispose();
            _signal.Dispose();
        }
    }
}
