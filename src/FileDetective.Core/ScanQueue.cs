namespace FileDetective.Core;

/// <summary>Why a whole location is scanned.</summary>
public enum ScanTrigger
{
    /// <summary>Local location, 60 s after program start (PLAN.md 7.5).</summary>
    Startup,

    /// <summary>Share or removable drive, by the daily check of the <see cref="RefreshScheduler"/>; counts in <c>attempts_today</c>.</summary>
    Scheduled,

    /// <summary>"Update now"; always runs, never counts as an automatic attempt.</summary>
    Manual,

    /// <summary>First scan of a location that was just added.</summary>
    Added,

    /// <summary>The watcher lost events (buffer overflow): the whole location is scanned again.</summary>
    Overflow,
}

/// <summary>A request to scan one whole location.</summary>
public sealed record ScanRequest(byte RootId, RootKind Kind, ScanTrigger Trigger)
{
    /// <summary>Queue order: local locations (and removable drives) before network shares (PLAN.md 7.2).</summary>
    public int Priority => Kind == RootKind.Network ? 1 : 0;
}

/// <summary>Where a location stands in the queue.</summary>
public enum QueuedScanState
{
    Idle,
    Queued,
    Running,
}

/// <summary>
/// The queue of whole-location scans (PLAN.md 7.2): exactly one scan at a time on one worker thread, local locations
/// before shares (first come, first served within each group), one entry per location, cancellation of a single
/// location and of everything.
/// </summary>
/// <remarks>
/// Merging: a second request for a queued location keeps the one entry (a manual request upgrades the trigger, so it
/// does not count as an automatic attempt). A request for the running location is dropped, the running scan serves it;
/// only <see cref="ScanTrigger.Overflow"/> queues it once more, because events were lost while the scan may already have
/// passed the folders concerned.
/// </remarks>
public sealed class ScanQueue : IDisposable
{
    private readonly Action<ScanRequest, CancellationToken> _run;
    private readonly object _gate = new();
    private readonly List<ScanRequest> _queue = [];
    private readonly HashSet<byte> _blocked = [];
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Thread _worker;
    private ScanRequest? _running;
    private CancellationTokenSource? _runningCancel;
    private ScanRequest? _rerun;
    private bool _stopping;

    /// <param name="run">Runs one scan on the worker thread. Exceptions are reported through <see cref="Failed"/>.</param>
    public ScanQueue(Action<ScanRequest, CancellationToken> run)
    {
        ArgumentNullException.ThrowIfNull(run);
        _run = run;
        _worker = new Thread(WorkerLoop) { IsBackground = true, Name = "FileDetective scan queue" };
        _worker.Start();
    }

    /// <summary>Raised (outside the lock, on any thread) when the queue or the running scan changed.</summary>
    public event Action? Changed;

    /// <summary>Raised on the worker thread when a scan threw something other than a cancellation.</summary>
    public event Action<ScanRequest, Exception>? Failed;

    /// <summary>The running scan or null.</summary>
    public ScanRequest? Running
    {
        get
        {
            lock (_gate)
            {
                return _running;
            }
        }
    }

    /// <summary>The waiting requests in the order they will run.</summary>
    public IReadOnlyList<ScanRequest> Queued
    {
        get
        {
            lock (_gate)
            {
                return [.. _queue];
            }
        }
    }

    /// <summary>True while a scan runs or waits.</summary>
    public bool IsBusy
    {
        get
        {
            lock (_gate)
            {
                return _running is not null || _queue.Count > 0;
            }
        }
    }

    public QueuedScanState GetState(byte rootId)
    {
        lock (_gate)
        {
            return _running?.RootId == rootId ? QueuedScanState.Running
                : _queue.Exists(r => r.RootId == rootId) ? QueuedScanState.Queued
                : QueuedScanState.Idle;
        }
    }

    /// <summary>
    /// Adds a request. Returns true if a new entry was queued, false if it was merged with a queued or running scan of
    /// the same location or rejected (location blocked, queue stopped).
    /// </summary>
    public bool Enqueue(ScanRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        bool added;
        lock (_gate)
        {
            added = EnqueueLocked(request);
            if (added)
            {
                Monitor.PulseAll(_gate);
            }
        }

        RaiseChanged();
        return added;
    }

    /// <summary>
    /// Removes the queued request of a location, cancels its running scan and waits until that scan has ended. Until the
    /// returned handle is disposed, new requests for the location are rejected (used while a location is removed).
    /// </summary>
    public IDisposable CancelAndBlock(byte rootId, TimeSpan? timeout = null)
    {
        lock (_gate)
        {
            _blocked.Add(rootId);
            _queue.RemoveAll(r => r.RootId == rootId);
            if (_rerun?.RootId == rootId)
            {
                _rerun = null;
            }

            if (_running?.RootId == rootId)
            {
                _runningCancel?.Cancel();
            }

            var deadline = DateTime.UtcNow + (timeout ?? Timeout.InfiniteTimeSpan);
            while (_running?.RootId == rootId)
            {
                if (timeout is null)
                {
                    Monitor.Wait(_gate);
                }
                else
                {
                    var left = deadline - DateTime.UtcNow;
                    if (left <= TimeSpan.Zero || !Monitor.Wait(_gate, left))
                    {
                        break;
                    }
                }
            }
        }

        RaiseChanged();
        return new Unblocker(this, rootId);
    }

    /// <summary>Removes the queued request of a location and cancels its running scan without waiting.</summary>
    public void Cancel(byte rootId)
    {
        lock (_gate)
        {
            _queue.RemoveAll(r => r.RootId == rootId);
            if (_rerun?.RootId == rootId)
            {
                _rerun = null;
            }

            if (_running?.RootId == rootId)
            {
                _runningCancel?.Cancel();
            }
        }

        RaiseChanged();
    }

    /// <summary>
    /// Stops the queue (program exit): drops all waiting requests, cancels the running scan, rejects new requests and
    /// waits up to <paramref name="timeout"/> for the worker to end. Returns true if it ended in time.
    /// </summary>
    public bool Shutdown(TimeSpan timeout)
    {
        lock (_gate)
        {
            _stopping = true;
            _queue.Clear();
            _rerun = null;
            _shutdown.Cancel();
            Monitor.PulseAll(_gate);
        }

        RaiseChanged();
        return _worker.Join(timeout);
    }

    /// <summary>Waits until no scan runs or waits (for tests). Returns false on timeout.</summary>
    public bool WaitIdle(TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        lock (_gate)
        {
            while (_running is not null || _queue.Count > 0)
            {
                var left = deadline - DateTime.UtcNow;
                if (left <= TimeSpan.Zero || !Monitor.Wait(_gate, left))
                {
                    return _running is null && _queue.Count == 0;
                }
            }

            return true;
        }
    }

    public void Dispose()
    {
        Shutdown(TimeSpan.FromSeconds(5));
        _shutdown.Dispose();
    }

    private bool EnqueueLocked(ScanRequest request)
    {
        if (_stopping || _blocked.Contains(request.RootId))
        {
            return false;
        }

        if (_running?.RootId == request.RootId)
        {
            if (request.Trigger == ScanTrigger.Overflow)
            {
                _rerun = request;
            }

            return false;
        }

        int index = _queue.FindIndex(r => r.RootId == request.RootId);
        if (index >= 0)
        {
            if (request.Trigger == ScanTrigger.Manual && _queue[index].Trigger == ScanTrigger.Scheduled)
            {
                _queue[index] = _queue[index] with { Trigger = ScanTrigger.Manual };
            }

            return false;
        }

        // Insert after the last request of the same or a better priority: local first, then first come first served.
        int position = _queue.FindLastIndex(r => r.Priority <= request.Priority) + 1;
        _queue.Insert(position, request);
        return true;
    }

    private void WorkerLoop()
    {
        while (true)
        {
            ScanRequest request;
            CancellationTokenSource cancel;
            lock (_gate)
            {
                while (_queue.Count == 0 && !_stopping)
                {
                    Monitor.Wait(_gate);
                }

                if (_stopping)
                {
                    Monitor.PulseAll(_gate);
                    return;
                }

                request = _queue[0];
                _queue.RemoveAt(0);
                _running = request;
                cancel = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
                _runningCancel = cancel;
            }

            RaiseChanged();
            try
            {
                _run(request, cancel.Token);
            }
            catch (OperationCanceledException) when (cancel.IsCancellationRequested)
            {
                // Cancelled by removing the location or by the program exit.
            }
            catch (Exception ex)
            {
                try
                {
                    Failed?.Invoke(request, ex);
                }
                catch (Exception)
                {
                    // A failing error handler must not stop the queue.
                }
            }
            finally
            {
                lock (_gate)
                {
                    _running = null;
                    _runningCancel = null;
                    if (_rerun is { } again)
                    {
                        _rerun = null;
                        EnqueueLocked(again);
                    }

                    Monitor.PulseAll(_gate);
                }

                cancel.Dispose();
            }

            RaiseChanged();
        }
    }

    private void RaiseChanged()
    {
        try
        {
            Changed?.Invoke();
        }
        catch (Exception)
        {
            // Listeners must not break the queue.
        }
    }

    private sealed class Unblocker(ScanQueue owner, byte rootId) : IDisposable
    {
        private int _done;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _done, 1) == 0)
            {
                lock (owner._gate)
                {
                    owner._blocked.Remove(rootId);
                }
            }
        }
    }
}
