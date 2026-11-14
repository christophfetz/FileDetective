namespace FileDetective.Core;

/// <summary>What is known about whether a location can be reached.</summary>
public enum Reachability
{
    /// <summary>Not checked yet.</summary>
    Unknown,

    Reachable,

    /// <summary>The folder was not found, the check failed, or it took longer than the time limit.</summary>
    Unreachable,
}

/// <summary>Answers whether a location can be reached right now (abstraction for the <see cref="RefreshScheduler"/>).</summary>
public interface IReachability
{
    /// <summary>Checks the location; completes with false at the latest after the time limit. Never blocks the caller.</summary>
    Task<bool> CheckAsync(string rootPath, CancellationToken cancellationToken = default);
}

/// <summary>Real probe for locations: <see cref="Directory.Exists(string)"/> only, never opens anything.</summary>
public sealed class DirectoryProbe : IPathProbe
{
    public static DirectoryProbe Instance { get; } = new();

    public bool Exists(string path) => Directory.Exists(path);
}

/// <summary>
/// Knows which locations can be reached (PLAN.md 7.7). <c>Directory.Exists</c> can hang for more than 20 seconds on a
/// dead share, so every check runs on its own background thread with a time limit (default 5 s) and is never awaited on
/// the UI thread. The cached answer (<see cref="GetState"/>) never blocks.
/// </summary>
/// <remarks>
/// Hanging probes do not pile up: while a probe of a location is still running, a new check waits for that same probe
/// (with a fresh time limit) instead of starting another thread. At most one probe thread per location exists at any
/// time. When a probe that ran into the time limit finishes later after all, its answer still updates the state.
/// </remarks>
public sealed class ReachabilityMonitor : IReachability
{
    /// <summary>Time limit of a check (PLAN.md 7.7).</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(5);

    /// <summary>Opening the search bar checks again when the last full check is older than this (PLAN.md 7.7).</summary>
    public static readonly TimeSpan StaleAfter = TimeSpan.FromSeconds(60);

    private readonly IPathProbe _probe;
    private readonly TimeProvider _time;
    private readonly object _gate = new();
    private readonly Dictionary<string, ProbeRun> _probes = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Reachability> _states = new(StringComparer.OrdinalIgnoreCase);
    private DateTimeOffset? _lastCheckAll;
    private int _probesStarted;

    public ReachabilityMonitor(IPathProbe? probe = null, TimeProvider? time = null, TimeSpan? timeout = null)
    {
        _probe = probe ?? DirectoryProbe.Instance;
        _time = time ?? TimeProvider.System;
        Timeout = timeout ?? DefaultTimeout;
    }

    public TimeSpan Timeout { get; }

    /// <summary>Raised (on a background thread) when the state of a location changed.</summary>
    public event Action? Changed;

    /// <summary>Number of probe threads started so far (for tests).</summary>
    public int ProbesStarted => Volatile.Read(ref _probesStarted);

    /// <summary>Time of the last <see cref="CheckAllAsync"/>, or null.</summary>
    public DateTimeOffset? LastCheckAll
    {
        get
        {
            lock (_gate)
            {
                return _lastCheckAll;
            }
        }
    }

    /// <summary>The last known state; never blocks.</summary>
    public Reachability GetState(string rootPath)
    {
        lock (_gate)
        {
            return _states.TryGetValue(Key(rootPath), out var state) ? state : Reachability.Unknown;
        }
    }

    /// <summary>True only if the location was checked and found unreachable (unknown counts as reachable).</summary>
    public bool IsUnreachable(string rootPath) => GetState(rootPath) == Reachability.Unreachable;

    public async Task<bool> CheckAsync(string rootPath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        string key = Key(rootPath);
        ProbeRun run;
        lock (_gate)
        {
            // A probe that has not answered yet is reused: no second thread for the same location.
            if (!_probes.TryGetValue(key, out run!) || run.Answered)
            {
                run = StartProbe(key, rootPath);
                _probes[key] = run;
            }
        }

        var probe = run.Task;
        if (!probe.IsCompleted)
        {
            using var delayCancel = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var delay = Task.Delay(Timeout, _time, delayCancel.Token);
            var first = await Task.WhenAny(probe, delay).ConfigureAwait(false);
            delayCancel.Cancel();
            cancellationToken.ThrowIfCancellationRequested();
            if (first != probe)
            {
                // Still hanging: unreachable for now. The probe keeps its thread and sets the state when it ends.
                bool changed;
                lock (_gate)
                {
                    changed = !run.Answered && Store(key, Reachability.Unreachable);
                }

                if (changed)
                {
                    RaiseChanged();
                }

                return false;
            }
        }

        return await probe.ConfigureAwait(false);
    }
    /// <summary>Checks all given locations in parallel (one probe each).</summary>
    public async Task CheckAllAsync(IEnumerable<string> rootPaths, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(rootPaths);
        lock (_gate)
        {
            _lastCheckAll = _time.GetUtcNow();
        }

        var checks = rootPaths.Distinct(StringComparer.OrdinalIgnoreCase).Select(p => CheckAsync(p, cancellationToken)).ToList();
        await Task.WhenAll(checks).ConfigureAwait(false);
    }

    /// <summary>
    /// Starts <see cref="CheckAllAsync"/> in the background if the last full check is older than
    /// <see cref="StaleAfter"/> (opening the search bar). Returns at once; true if a check was started.
    /// </summary>
    public bool CheckAllIfStale(IEnumerable<string> rootPaths)
    {
        ArgumentNullException.ThrowIfNull(rootPaths);
        lock (_gate)
        {
            if (_lastCheckAll is { } last && _time.GetUtcNow() - last <= StaleAfter)
            {
                return false;
            }

            _lastCheckAll = _time.GetUtcNow(); // claimed now, so a second call right after does not start another round
        }

        var list = rootPaths.ToList();
        _ = Task.Run(() => CheckAllAsync(list));
        return true;
    }

    /// <summary>Forgets a location (after it was removed).</summary>
    public void Forget(string rootPath)
    {
        lock (_gate)
        {
            _states.Remove(Key(rootPath));
        }
    }

    private static string Key(string rootPath)
    {
        string trimmed = rootPath.TrimEnd('\\', '/');
        return trimmed.Length == 0 ? rootPath : trimmed;
    }

    private ProbeRun StartProbe(string key, string rootPath)
    {
        Interlocked.Increment(ref _probesStarted);
        var run = new ProbeRun();

        // LongRunning: a dedicated thread, so a hanging probe never blocks a thread-pool thread for long.
        // The answer is stored inside the probe task, so whoever sees the task completed also sees the state.
        run.Task = Task.Factory.StartNew(
            () =>
            {
                bool exists;
                try
                {
                    exists = _probe.Exists(rootPath);
                }
                catch (Exception)
                {
                    exists = false;
                }

                bool changed;
                lock (_gate)
                {
                    run.Answered = true;
                    changed = Store(key, exists ? Reachability.Reachable : Reachability.Unreachable);
                }

                if (changed)
                {
                    RaiseChanged();
                }

                return exists;
            },
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);
        return run;
    }

    /// <summary>Stores a state (caller holds the lock); true if it changed.</summary>
    private bool Store(string key, Reachability state)
    {
        bool changed = !_states.TryGetValue(key, out var old) || old != state;
        _states[key] = state;
        return changed;
    }

    private void RaiseChanged()
    {
        try
        {
            Changed?.Invoke();
        }
        catch (Exception)
        {
            // Listeners must not break the monitor.
        }
    }

    /// <summary>One probe of one location; <see cref="Answered"/> is set under the lock when it has its answer.</summary>
    private sealed class ProbeRun
    {
        public Task<bool> Task { get; set; } = null!;

        public bool Answered { get; set; }
    }
}