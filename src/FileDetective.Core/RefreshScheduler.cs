namespace FileDetective.Core;

/// <summary>
/// Decides when which location is scanned (PLAN.md 7.5):
/// <list type="bullet">
/// <item>Local locations: 60 s after every program start (catches up what happened while the program was not running);
/// afterwards the <see cref="LocalWatcher"/> keeps them current; and on "Update now".</item>
/// <item>Shares and removable drives: only if the last complete scan was on an earlier calendar day (local time) and
/// the location is reachable; checked 2 minutes after start, then every hour and after a network change; and on
/// "Update now".</item>
/// <item>An automatic scan of a share or removable drive that did not complete is retried at the next check, at most
/// three automatic attempts per calendar day (<c>roots.attempts_today</c>). "Update now" always runs and does not count.</item>
/// </list>
/// Clock and reachability are injected, so all of this is testable without waiting.
/// </summary>
/// <remarks>
/// <c>attempts_today</c> belongs to the calendar day of <c>last_scan_started_utc</c>. <see cref="PrepareStart"/> sets
/// that time at the start of every scan (also manual ones and also when the location then turns out unreachable), and
/// resets the counter when that day is over. Decision: network changes before the first check (2 minutes after start)
/// only refresh the reachability and start no share scan, so the start of the program stays quiet.
/// </remarks>
public sealed class RefreshScheduler : IDisposable
{
    public const int MaxAttemptsPerDay = 3;

    public static readonly TimeSpan LocalStartupDelay = TimeSpan.FromSeconds(60);

    public static readonly TimeSpan FirstShareCheckDelay = TimeSpan.FromMinutes(2);

    public static readonly TimeSpan ShareCheckInterval = TimeSpan.FromHours(1);

    /// <summary>Network changes come in bursts; one check this long after the first event of a burst.</summary>
    public static readonly TimeSpan NetworkDebounce = TimeSpan.FromSeconds(3);

    private readonly TimeProvider _time;
    private readonly Func<IReadOnlyList<RootInfo>> _roots;
    private readonly IReachability _reachability;
    private readonly Func<ScanRequest, bool> _enqueue;
    private readonly Debouncer _network;
    private readonly object _gate = new();
    private ITimer? _localTimer;
    private ITimer? _shareTimer;
    private int _checkRunning;
    private volatile bool _firstCheckDone;
    private bool _disposed;

    public RefreshScheduler(
        TimeProvider time,
        Func<IReadOnlyList<RootInfo>> roots,
        IReachability reachability,
        Func<ScanRequest, bool> enqueue)
    {
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(roots);
        ArgumentNullException.ThrowIfNull(reachability);
        ArgumentNullException.ThrowIfNull(enqueue);
        _time = time;
        _roots = roots;
        _reachability = reachability;
        _enqueue = enqueue;
        _network = new Debouncer(NetworkDebounce, () => _ = OnNetworkSettledAsync(), time);
    }

    /// <summary>Raised when a check failed unexpectedly.</summary>
    public event Action<Exception>? Failed;

    /// <summary>Starts the timers: local scans after 60 s, the first share check after 2 minutes, then hourly.</summary>
    public void Start()
    {
        lock (_gate)
        {
            if (_disposed || _localTimer is not null)
            {
                return;
            }

            _localTimer = _time.CreateTimer(_ => RunLocalStartupScans(), null, LocalStartupDelay, Timeout.InfiniteTimeSpan);
            _shareTimer = _time.CreateTimer(_ => OnShareTimer(), null, FirstShareCheckDelay, ShareCheckInterval);
        }
    }

    /// <summary>Queues every enabled local location (the scan 60 s after start). Returns the number queued.</summary>
    public int RunLocalStartupScans()
    {
        int count = 0;
        foreach (var root in SafeRoots())
        {
            if (root.Enabled && root.Kind == RootKind.Local && _enqueue(new ScanRequest(root.Id, root.Kind, ScanTrigger.Startup)))
            {
                count++;
            }
        }

        return count;
    }

    /// <summary>
    /// The daily check of shares and removable drives: queues every location that is due and reachable. Returns the
    /// number of queued scans. A check that is already running makes this call return 0 at once.
    /// </summary>
    public async Task<int> CheckSharesAsync(CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref _checkRunning, 1) != 0)
        {
            return 0;
        }

        try
        {
            int count = 0;
            foreach (var root in SafeRoots())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!IsShareDue(root, _time.GetUtcNow(), _time.LocalTimeZone))
                {
                    continue;
                }

                // Only due locations are probed: an up-to-date share is not even asked whether it is there.
                if (!await _reachability.CheckAsync(root.Path, cancellationToken).ConfigureAwait(false))
                {
                    continue;
                }

                if (_enqueue(new ScanRequest(root.Id, root.Kind, ScanTrigger.Scheduled)))
                {
                    count++;
                }
            }

            return count;
        }
        finally
        {
            _firstCheckDone = true;
            Volatile.Write(ref _checkRunning, 0);
        }
    }

    /// <summary>"Update now": queues the given locations regardless of day, attempts and reachability.</summary>
    public int RequestNow(IEnumerable<RootInfo> roots)
    {
        ArgumentNullException.ThrowIfNull(roots);
        int count = 0;
        foreach (var root in roots)
        {
            if (_enqueue(new ScanRequest(root.Id, root.Kind, ScanTrigger.Manual)))
            {
                count++;
            }
        }

        return count;
    }

    /// <summary>
    /// Called on every <c>NetworkChange.NetworkAddressChanged</c>. Debounced: shortly after a burst all locations are
    /// checked for reachability, then (once the first check after start has happened) shares are checked as at the hour.
    /// </summary>
    public void NotifyNetworkChanged() => _network.Trigger();

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            _localTimer?.Dispose();
            _shareTimer?.Dispose();
        }

        _network.Dispose();
    }

    // ---------------------------------------------------------------- rules (pure, tested directly)

    /// <summary>The calendar day of a UTC time in the given time zone.</summary>
    public static DateOnly LocalDay(DateTimeOffset utc, TimeZoneInfo zone)
        => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(utc, zone).DateTime);

    /// <summary>The automatic attempts of today: <c>attempts_today</c> if the last start was today, otherwise 0.</summary>
    public static int AttemptsToday(RootInfo root, DateTimeOffset now, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(root);
        return root.LastScanStartedUtc is { } started
            && LocalDay(DateTimeOffset.FromUnixTimeSeconds(started), zone) == LocalDay(now, zone)
            ? root.AttemptsToday
            : 0;
    }

    /// <summary>
    /// True if an automatic scan of a share or removable drive is due (reachability is checked separately): enabled, not
    /// completed today, fewer than three automatic attempts today. Local locations are never "due" here.
    /// </summary>
    public static bool IsShareDue(RootInfo root, DateTimeOffset now, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(zone);
        if (!root.Enabled || root.Kind == RootKind.Local)
        {
            return false;
        }

        // "Complete" includes "complete with errors": every readable folder was compared.
        if (root.LastScanCompletedUtc is { } completed
            && LocalDay(DateTimeOffset.FromUnixTimeSeconds(completed), zone) >= LocalDay(now, zone))
        {
            return false;
        }

        return AttemptsToday(root, now, zone) < MaxAttemptsPerDay;
    }

    /// <summary>True if a request counts as an automatic attempt in <c>attempts_today</c>.</summary>
    public static bool CountsAsAttempt(ScanRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return request.Trigger == ScanTrigger.Scheduled && request.Kind != RootKind.Local;
    }

    /// <summary>
    /// The location state to store right before a scan starts: start time = now, and the attempt counter of today
    /// (reset on a new day, plus one for an automatic attempt).
    /// </summary>
    public static RootInfo PrepareStart(RootInfo root, ScanRequest request, DateTimeOffset now, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(request);
        int attempts = AttemptsToday(root, now, zone) + (CountsAsAttempt(request) ? 1 : 0);
        return root with { LastScanStartedUtc = now.ToUnixTimeSeconds(), AttemptsToday = attempts };
    }

    // ---------------------------------------------------------------- timers

    private IReadOnlyList<RootInfo> SafeRoots()
    {
        try
        {
            return _roots();
        }
        catch (Exception ex)
        {
            Failed?.Invoke(ex);
            return [];
        }
    }

    private void OnShareTimer() => _ = RunCheckSafelyAsync();

    private async Task RunCheckSafelyAsync()
    {
        try
        {
            await CheckSharesAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Failed?.Invoke(ex);
        }
    }

    private async Task OnNetworkSettledAsync()
    {
        try
        {
            var roots = SafeRoots().Where(r => r.Enabled).ToList();
            await Task.WhenAll(roots.Select(r => _reachability.CheckAsync(r.Path))).ConfigureAwait(false);
            if (_firstCheckDone)
            {
                await CheckSharesAsync().ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            Failed?.Invoke(ex);
        }
    }
}
