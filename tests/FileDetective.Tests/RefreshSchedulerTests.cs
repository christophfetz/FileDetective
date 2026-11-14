using FileDetective.Core;

namespace FileDetective.Tests;

/// <summary>When locations are scanned (PLAN.md 7.5), with an injected clock and reachability.</summary>
public class RefreshSchedulerTests : IDisposable
{
    private const byte LocalId = 1;
    private const byte ShareId = 2;
    private const byte RemovableId = 3;
    private const string SharePath = @"P:\Projekte";

    // 2026-11-14 08:00 local time in a zone UTC+1.
    private static readonly DateTimeOffset Start = new(2026, 11, 14, 7, 0, 0, TimeSpan.Zero);

    private readonly ManualTimeProvider _time = new(Start, ManualTimeProvider.UtcPlusOne);
    private readonly FakeReachability _reach = new();
    private readonly Dictionary<byte, RootInfo> _roots = [];
    private readonly List<ScanRequest> _enqueued = [];
    private readonly RefreshScheduler _scheduler;

    public RefreshSchedulerTests()
    {
        _roots[LocalId] = NewRoot(LocalId, @"C:\Users\anna", RootKind.Local);
        _roots[ShareId] = NewRoot(ShareId, SharePath, RootKind.Network);
        _scheduler = new RefreshScheduler(_time, () => [.. _roots.Values], _reach, r =>
        {
            _enqueued.Add(r);
            return true;
        });
    }

    public void Dispose() => _scheduler.Dispose();

    private static RootInfo NewRoot(byte id, string path, RootKind kind)
        => new(id, path, null, kind, true, null, null, ScanStatus.Never, 0, 0, 100 + id);

    private TimeZoneInfo Zone => _time.LocalTimeZone;

    /// <summary>What the scan executor and the scanner store around one scan, without a file system.</summary>
    private void Run(ScanRequest request, bool complete)
    {
        var root = RefreshScheduler.PrepareStart(_roots[request.RootId], request, _time.GetUtcNow(), Zone);
        _roots[request.RootId] = complete
            ? root with { LastScanStatus = ScanStatus.Complete, LastScanCompletedUtc = _time.GetUtcNow().ToUnixTimeSeconds() }
            : root with { LastScanStatus = ScanStatus.Aborted };
    }

    private int Check() => _scheduler.CheckSharesAsync().GetAwaiter().GetResult();

    private List<ScanRequest> TakeEnqueued()
    {
        var list = _enqueued.ToList();
        _enqueued.Clear();
        return list;
    }

    [Fact]
    public void ShareIsNotScannedAutomaticallyASecondTimeOnTheSameDay()
    {
        Assert.Equal(1, Check());
        var request = Assert.Single(TakeEnqueued());
        Assert.Equal(new ScanRequest(ShareId, RootKind.Network, ScanTrigger.Scheduled), request);
        Run(request, complete: true);

        // Every hour until the end of the local day: nothing.
        for (int hour = 1; hour <= 15; hour++)
        {
            _time.Advance(TimeSpan.FromHours(1));
            Assert.Equal(0, Check());
        }

        // 23:59 local time is still the same day.
        _time.AdvanceTo(new DateTimeOffset(2026, 11, 14, 22, 59, 0, TimeSpan.Zero));
        Assert.Equal(0, Check());
        Assert.Empty(_enqueued);
    }

    [Fact]
    public void TheCalendarDayIsTheLocalOne()
    {
        // Completed at 23:30 UTC on the 14th = 00:30 local time on the 15th.
        _roots[ShareId] = _roots[ShareId] with
        {
            LastScanStatus = ScanStatus.Complete,
            LastScanCompletedUtc = new DateTimeOffset(2026, 11, 14, 23, 30, 0, TimeSpan.Zero).ToUnixTimeSeconds(),
        };
        _time.AdvanceTo(new DateTimeOffset(2026, 11, 15, 9, 0, 0, TimeSpan.Zero));
        Assert.Equal(0, Check());

        // Completed at 22:30 UTC on the 14th = 23:30 local time on the 14th: due on the 15th.
        _roots[ShareId] = _roots[ShareId] with
        {
            LastScanCompletedUtc = new DateTimeOffset(2026, 11, 14, 22, 30, 0, TimeSpan.Zero).ToUnixTimeSeconds(),
        };
        Assert.Equal(1, Check());
    }

    [Fact]
    public void ShareIsScannedAgainOnTheNextDay()
    {
        Run(new ScanRequest(ShareId, RootKind.Network, ScanTrigger.Scheduled), complete: true);
        Assert.Equal(0, Check());

        // 00:00 local time on the 15th = 23:00 UTC on the 14th.
        _time.AdvanceTo(new DateTimeOffset(2026, 11, 14, 23, 0, 0, TimeSpan.Zero));
        Assert.Equal(1, Check());
    }

    [Fact]
    public void CompleteWithErrorsCountsAsComplete()
    {
        _roots[ShareId] = _roots[ShareId] with
        {
            LastScanStatus = ScanStatus.CompleteWithErrors,
            LastScanCompletedUtc = _time.GetUtcNow().ToUnixTimeSeconds(),
        };
        Assert.Equal(0, Check());
    }

    [Fact]
    public void AfterThreeAbortedAttemptsTheDayIsOver_UpdateNowStillRuns_AndTheNextDayStartsAfresh()
    {
        for (int attempt = 1; attempt <= RefreshScheduler.MaxAttemptsPerDay; attempt++)
        {
            Assert.Equal(1, Check());
            Run(Assert.Single(TakeEnqueued()), complete: false);
            Assert.Equal(attempt, _roots[ShareId].AttemptsToday);
            _time.Advance(TimeSpan.FromHours(1));
        }

        Assert.Equal(0, Check());
        _time.Advance(TimeSpan.FromHours(1));
        Assert.Equal(0, Check());
        Assert.Empty(_enqueued);

        // "Update now" runs anyway and does not count as an automatic attempt.
        Assert.Equal(1, _scheduler.RequestNow([_roots[ShareId]]));
        var manual = Assert.Single(TakeEnqueued());
        Assert.Equal(ScanTrigger.Manual, manual.Trigger);
        Run(manual, complete: false);
        Assert.Equal(RefreshScheduler.MaxAttemptsPerDay, _roots[ShareId].AttemptsToday);

        // Next local day: the counter starts again.
        _time.AdvanceTo(new DateTimeOffset(2026, 11, 14, 23, 5, 0, TimeSpan.Zero));
        Assert.Equal(0, RefreshScheduler.AttemptsToday(_roots[ShareId], _time.GetUtcNow(), Zone));
        Assert.Equal(1, Check());
        Run(Assert.Single(TakeEnqueued()), complete: false);
        Assert.Equal(1, _roots[ShareId].AttemptsToday);
    }

    [Fact]
    public void ManualScanOnANewDayResetsTheCounterOfTheDayBefore()
    {
        for (int attempt = 1; attempt <= 3; attempt++)
        {
            Run(new ScanRequest(ShareId, RootKind.Network, ScanTrigger.Scheduled), complete: false);
        }

        _time.Advance(TimeSpan.FromDays(1));
        Run(new ScanRequest(ShareId, RootKind.Network, ScanTrigger.Manual), complete: false);
        Assert.Equal(0, _roots[ShareId].AttemptsToday);
        Assert.Equal(1, Check());
    }

    [Fact]
    public void UpdateNowRunsEvenWhenTheShareWasScannedToday()
    {
        Run(new ScanRequest(ShareId, RootKind.Network, ScanTrigger.Scheduled), complete: true);
        Assert.Equal(0, Check());
        Assert.Equal(2, _scheduler.RequestNow(_roots.Values));
        Assert.All(_enqueued, r => Assert.Equal(ScanTrigger.Manual, r.Trigger));
    }

    [Fact]
    public void UnreachableShareIsNotScanned_AndAnUpToDateShareIsNotEvenProbed()
    {
        _reach.Unreachable.Add(SharePath);
        Assert.Equal(0, Check());
        Assert.Empty(_enqueued);
        Assert.Equal([SharePath], _reach.Calls);

        _reach.Unreachable.Clear();
        Assert.Equal(1, Check());
        Run(Assert.Single(TakeEnqueued()), complete: true);

        _reach.Calls.Clear();
        _time.Advance(TimeSpan.FromHours(1));
        Assert.Equal(0, Check());
        Assert.Empty(_reach.Calls);
    }

    [Fact]
    public void TheShareCheckNeverTouchesLocalLocations_RemovableDrivesFollowTheShareRule()
    {
        _roots[RemovableId] = NewRoot(RemovableId, @"E:\", RootKind.Removable);
        _roots[4] = NewRoot(4, @"\\server\off", RootKind.Network) with { Enabled = false };
        Assert.Equal(2, Check());
        Assert.Equal([ShareId, RemovableId], _enqueued.Select(r => r.RootId).Order());
        Assert.DoesNotContain(@"C:\Users\anna", _reach.Calls);
    }

    [Fact]
    public void Timers_LocalAfter60Seconds_SharesAfter2MinutesThenHourly()
    {
        _scheduler.Start();
        _time.Advance(TimeSpan.FromSeconds(59));
        Assert.Empty(_enqueued);

        _time.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal([new ScanRequest(LocalId, RootKind.Local, ScanTrigger.Startup)], TakeEnqueued());

        _time.Advance(TimeSpan.FromSeconds(59));
        Assert.Empty(_enqueued);
        _time.Advance(TimeSpan.FromSeconds(1)); // 2 minutes after start
        var first = Assert.Single(TakeEnqueued());
        Assert.Equal(ShareId, first.RootId);
        Run(first, complete: false);

        // The aborted attempt is repeated at the next hourly check, not before.
        _time.Advance(TimeSpan.FromMinutes(59));
        Assert.Empty(_enqueued);
        _time.Advance(TimeSpan.FromMinutes(1));
        Run(Assert.Single(TakeEnqueued()), complete: true);

        // Local locations are scanned only once after start; the watcher does the rest.
        _time.Advance(TimeSpan.FromHours(5));
        Assert.Empty(_enqueued);
    }

    [Fact]
    public void NetworkChange_IsDebounced_ChecksReachability_AndChecksSharesOnlyAfterTheFirstCheck()
    {
        _scheduler.Start();
        _time.Advance(TimeSpan.FromSeconds(61));
        TakeEnqueued(); // the local start-up scan

        // Before the first check (2 minutes after start): reachability only.
        _scheduler.NotifyNetworkChanged();
        _scheduler.NotifyNetworkChanged();
        _scheduler.NotifyNetworkChanged();
        _time.Advance(RefreshScheduler.NetworkDebounce);
        Assert.Equal(2, _reach.Calls.Count); // both locations once, not three times each
        Assert.Empty(_enqueued);

        _reach.Unreachable.Add(SharePath);
        _time.Advance(TimeSpan.FromMinutes(2)); // first check: share unreachable
        Assert.Empty(_enqueued);

        // VPN connected: the network change brings the share check forward instead of waiting for the hour.
        _reach.Unreachable.Clear();
        _reach.Calls.Clear();
        _scheduler.NotifyNetworkChanged();
        _time.Advance(RefreshScheduler.NetworkDebounce);
        var request = Assert.Single(TakeEnqueued());
        Assert.Equal(ShareId, request.RootId);
    }

    [Fact]
    public void RunLocalStartupScans_QueuesOnlyEnabledLocalLocations()
    {
        _roots[5] = NewRoot(5, @"D:\Daten", RootKind.Local) with { Enabled = false };
        Assert.Equal(1, _scheduler.RunLocalStartupScans());
        Assert.Equal(LocalId, Assert.Single(_enqueued).RootId);
    }

    [Fact]
    public void PrepareStart_CountsOnlyAutomaticShareAttempts()
    {
        var now = _time.GetUtcNow();
        var share = _roots[ShareId];
        Assert.Equal(1, RefreshScheduler.PrepareStart(share, new ScanRequest(ShareId, RootKind.Network, ScanTrigger.Scheduled), now, Zone).AttemptsToday);
        Assert.Equal(0, RefreshScheduler.PrepareStart(share, new ScanRequest(ShareId, RootKind.Network, ScanTrigger.Manual), now, Zone).AttemptsToday);
        Assert.Equal(0, RefreshScheduler.PrepareStart(share, new ScanRequest(ShareId, RootKind.Network, ScanTrigger.Added), now, Zone).AttemptsToday);
        Assert.Equal(0, RefreshScheduler.PrepareStart(_roots[LocalId], new ScanRequest(LocalId, RootKind.Local, ScanTrigger.Startup), now, Zone).AttemptsToday);
        Assert.Equal(now.ToUnixTimeSeconds(), RefreshScheduler.PrepareStart(share, new ScanRequest(ShareId, RootKind.Network, ScanTrigger.Manual), now, Zone).LastScanStartedUtc);
    }

    /// <summary>Reachability without a file system: everything reachable except the listed paths; records the calls.</summary>
    private sealed class FakeReachability : IReachability
    {
        public HashSet<string> Unreachable { get; } = new(StringComparer.OrdinalIgnoreCase);

        public List<string> Calls { get; } = [];

        public Task<bool> CheckAsync(string rootPath, CancellationToken cancellationToken = default)
        {
            lock (Calls)
            {
                Calls.Add(rootPath);
            }

            return Task.FromResult(!Unreachable.Contains(rootPath));
        }
    }
}
