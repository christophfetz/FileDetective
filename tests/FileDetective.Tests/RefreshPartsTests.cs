using System.Diagnostics;
using FileDetective.Core;

namespace FileDetective.Tests;

/// <summary>Smaller parts of stage 6: the scan pause, the scan executor, the existence check, debouncing, locks.</summary>
public class RefreshPartsTests : IDisposable
{
    private readonly TempDir _data = new();
    private readonly StorageFixture _f;

    public RefreshPartsTests()
    {
        _f = new StorageFixture(_data);
    }

    public void Dispose()
    {
        _f.Dispose();
        _data.Dispose();
    }

    // ---------------------------------------------------------------- SystemScanClock (fix of the wait path)

    [Theory]
    [InlineData(0.3)]
    [InlineData(0.9)]
    [InlineData(2.6)]
    public void SystemScanClock_PausesAtLeastTheMeasuredDuration_AlsoBelowOneMillisecond(double milliseconds)
    {
        // Before the fix, WaitOne(TimeSpan) cut the pause to whole milliseconds: 0.3 and 0.9 ms did not wait at all.
        var duration = TimeSpan.FromMilliseconds(milliseconds);
        for (int i = 0; i < 5; i++)
        {
            long start = Stopwatch.GetTimestamp();
            SystemScanClock.Instance.Pause(duration, CancellationToken.None);
            Assert.True(Stopwatch.GetElapsedTime(start) >= duration, $"paused less than {milliseconds} ms");
        }
    }

    [Fact]
    public void SystemScanClock_PauseEndsAtOnceWhenCancelled()
    {
        using var cts = new CancellationTokenSource();
        var watch = Stopwatch.StartNew();
        cts.CancelAfter(TimeSpan.FromMilliseconds(50));
        Assert.ThrowsAny<OperationCanceledException>(() => SystemScanClock.Instance.Pause(TimeSpan.FromSeconds(30), cts.Token));
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5));

        Assert.ThrowsAny<OperationCanceledException>(() => SystemScanClock.Instance.Pause(TimeSpan.Zero, cts.Token));
    }

    // ---------------------------------------------------------------- ScanExecutor

    [Fact]
    public void ScanExecutor_StoresTheAttempt_AlsoWhenTheShareIsUnreachable()
    {
        var root = _f.Store.AddRoot(@"\\server\share", null, RootKind.Network);
        var fs = new FakeFileSystem(@"\\server\share");
        fs.Fail(string.Empty, new IOException("The network path was not found."));
        var time = new ManualTimeProvider(new DateTimeOffset(2026, 11, 14, 8, 0, 0, TimeSpan.Zero));
        var executor = new ScanExecutor(
            _f.Store, new LocationLocks(), _ => new Scanner(_f.Store, fs, ExcludeRules.None, new ScannerOptions { Clock = new FakeScanClock() }), time);

        var request = new ScanRequest(root.Id, RootKind.Network, ScanTrigger.Scheduled);
        for (int i = 1; i <= 3; i++)
        {
            var stats = executor.Run(request, CancellationToken.None);
            Assert.Equal(ScanOutcome.RootUnreachable, stats!.Outcome);
            Assert.Equal(i, _f.Store.GetRoot(root.Id)!.AttemptsToday);
        }

        var stored = _f.Store.GetRoot(root.Id)!;
        Assert.Equal(time.GetUtcNow().ToUnixTimeSeconds(), stored.LastScanStartedUtc);
        Assert.False(RefreshScheduler.IsShareDue(stored, time.GetUtcNow(), time.LocalTimeZone));

        // Manual: runs, and does not count.
        executor.Run(request with { Trigger = ScanTrigger.Manual }, CancellationToken.None);
        Assert.Equal(3, _f.Store.GetRoot(root.Id)!.AttemptsToday);
    }

    [Fact]
    public async Task ScanExecutor_WaitsForTheLocationLock()
    {
        var root = _f.Store.AddRoot(@"Q:\Data", null, RootKind.Local);
        var fs = new FakeFileSystem(@"Q:\Data").File("a.txt");
        var locks = new LocationLocks();
        var executor = new ScanExecutor(_f.Store, locks, _ => new Scanner(_f.Store, fs, ExcludeRules.None));

        Task<ScanStatistics?> run;
        using (locks.Acquire(root.Id))
        {
            run = Task.Run(() => executor.Run(new ScanRequest(root.Id, RootKind.Local, ScanTrigger.Manual), CancellationToken.None));
            Assert.NotSame(run, await Task.WhenAny(run, Task.Delay(300)));
            Assert.Empty(fs.Calls);
        }

        var stats = await run.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(ScanOutcome.Complete, stats!.Outcome);

        // Cancelled while waiting for the lock: nothing runs.
        using (locks.Acquire(root.Id))
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
            Assert.ThrowsAny<OperationCanceledException>(
                () => executor.Run(new ScanRequest(root.Id, RootKind.Local, ScanTrigger.Manual), cts.Token));
        }
    }

    [Fact]
    public void ScanExecutor_ReturnsNullForARemovedLocation()
    {
        var executor = new ScanExecutor(_f.Store, new LocationLocks(), _ => throw new InvalidOperationException("no scanner"));
        Assert.Null(executor.Run(new ScanRequest(42, RootKind.Local, ScanTrigger.Manual), CancellationToken.None));
    }

    // ---------------------------------------------------------------- ExistenceCheck with reachability

    [Fact]
    public void ExistenceCheck_NeverReportsVanishedForAnUnreachableLocation_AndDoesNotProbeIt()
    {
        string[] roots = [@"C:\Users\anna", @"P:\Projects"];
        int probes = 0;
        var probe = new CountingProbe(_ =>
        {
            probes++;
            return true; // even if the folders seemed to be there
        });

        // The file looks gone, but the location is known as unreachable: unknown, and no probe at all (it could hang).
        var result = ExistenceCheck.Evaluate(@"P:\Projects\x\a.txt", roots, probe, p => p == @"P:\Projects");
        Assert.Equal(ExistenceResult.Unknown, result);
        Assert.Equal(0, probes);

        // Another, reachable location still works as before.
        var gone = new CountingProbe(p => p != @"C:\Users\anna\a.txt");
        Assert.Equal(ExistenceResult.Vanished, ExistenceCheck.Evaluate(@"C:\Users\anna\a.txt", roots, gone, p => p == @"P:\Projects"));
    }

    private sealed class CountingProbe(Func<string, bool> exists) : IPathProbe
    {
        public bool Exists(string path) => exists(path);
    }

    // ---------------------------------------------------------------- resolver knows the location of a hit

    [Fact]
    public void SearchHitsCarryTheirLocation()
    {
        var a = _f.Store.AddRoot(@"C:\A", null, RootKind.Local);
        var b = _f.Store.AddRoot(@"P:\B", null, RootKind.Network);
        _f.Add(a, a.RootEntryId, "angebot-a.txt", false);
        _f.Add(b, b.RootEntryId, "angebot-b.txt", false);

        var hits = _f.Engine.Search("angebot", SearchMode.All).Hits;
        Assert.Equal(a.Id, hits.Single(h => h.Details.Name == "angebot-a.txt").Details.RootId);
        Assert.Equal(b.Id, hits.Single(h => h.Details.Name == "angebot-b.txt").Details.RootId);
    }

    // ---------------------------------------------------------------- Debouncer and LocationLocks

    [Fact]
    public void Debouncer_RunsOnceAfterTheFirstTriggerOfABurst_AndCannotBePostponedForever()
    {
        var time = new ManualTimeProvider(DateTimeOffset.UnixEpoch);
        int runs = 0;
        using var debouncer = new Debouncer(TimeSpan.FromSeconds(2), () => runs++, time);

        debouncer.Trigger();
        time.Advance(TimeSpan.FromSeconds(1));
        debouncer.Trigger();
        debouncer.Trigger();
        Assert.Equal(0, runs);
        time.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(1, runs);

        // A steady stream every 500 ms still runs it every 2 s.
        for (int i = 0; i < 8; i++)
        {
            debouncer.Trigger();
            time.Advance(TimeSpan.FromMilliseconds(500));
        }

        Assert.Equal(3, runs);
        time.Advance(TimeSpan.FromSeconds(10));
        Assert.Equal(3, runs);
    }

    [Fact]
    public void LocationLocks_AreExclusivePerLocation_AndIndependentBetweenLocations()
    {
        var locks = new LocationLocks();
        using (locks.Acquire(1))
        {
            Assert.True(locks.IsHeld(1));
            Assert.Null(locks.TryAcquire(1, TimeSpan.FromMilliseconds(50)));
            using var other = locks.TryAcquire(2, TimeSpan.Zero);
            Assert.NotNull(other);
        }

        Assert.False(locks.IsHeld(1));
        using var again = locks.TryAcquire(1, TimeSpan.Zero);
        Assert.NotNull(again);
    }
}
