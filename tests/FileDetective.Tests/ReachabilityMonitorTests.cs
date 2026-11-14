using System.Diagnostics;
using FileDetective.Core;

namespace FileDetective.Tests;

/// <summary>Reachability checks with a time limit that never pile up threads (PLAN.md 7.7).</summary>
public class ReachabilityMonitorTests
{
    private const string Share = @"\\server\share";

    /// <summary>A probe that hangs (like Directory.Exists on a dead share) until released.</summary>
    private sealed class HangingProbe : IPathProbe
    {
        private readonly ManualResetEventSlim _release = new(false);
        private int _calls;
        private int _active;
        private int _maxActive;

        public bool Answer { get; set; } = true;

        public int Calls => Volatile.Read(ref _calls);

        public int MaxActive => Volatile.Read(ref _maxActive);

        public void Release() => _release.Set();

        public bool Exists(string path)
        {
            Interlocked.Increment(ref _calls);
            int now = Interlocked.Increment(ref _active);
            int max;
            while (now > (max = Volatile.Read(ref _maxActive)) && Interlocked.CompareExchange(ref _maxActive, now, max) != max)
            {
            }

            try
            {
                _release.Wait(TimeSpan.FromSeconds(30));
                return Answer;
            }
            finally
            {
                Interlocked.Decrement(ref _active);
            }
        }
    }

    private sealed class FuncProbe(Func<string, bool> exists) : IPathProbe
    {
        public bool Exists(string path) => exists(path);
    }

    [Fact]
    public async Task HangingProbe_AnswersFalseAfterTheTimeLimit_AndDoesNotStartMoreThreads()
    {
        var probe = new HangingProbe();
        var monitor = new ReachabilityMonitor(probe, timeout: TimeSpan.FromMilliseconds(200));

        var watch = Stopwatch.StartNew();
        Assert.False(await monitor.CheckAsync(Share));
        Assert.InRange(watch.Elapsed, TimeSpan.FromMilliseconds(150), TimeSpan.FromSeconds(5));
        Assert.Equal(Reachability.Unreachable, monitor.GetState(Share));

        // Ten more checks, five of them at the same time: all wait for the one hanging probe.
        for (int i = 0; i < 5; i++)
        {
            Assert.False(await monitor.CheckAsync(Share));
        }

        var parallel = Enumerable.Range(0, 5).Select(_ => monitor.CheckAsync(Share)).ToArray();
        Assert.All(await Task.WhenAll(parallel), Assert.False);

        Assert.Equal(1, monitor.ProbesStarted);
        Assert.Equal(1, probe.Calls);
        Assert.Equal(1, probe.MaxActive);

        // The hanging probe finishes after all: its answer updates the state.
        probe.Release();
        Poll.Until(() => monitor.GetState(Share) == Reachability.Reachable, "state from the late probe");

        // The next check starts a new probe (the old one is done).
        Assert.True(await monitor.CheckAsync(Share));
        Assert.Equal(2, monitor.ProbesStarted);
    }

    [Fact]
    public async Task GetStateNeverBlocks_WhileAProbeHangs()
    {
        var probe = new HangingProbe();
        var monitor = new ReachabilityMonitor(probe, timeout: TimeSpan.FromSeconds(30));
        var check = monitor.CheckAsync(Share);

        var watch = Stopwatch.StartNew();
        Assert.Equal(Reachability.Unknown, monitor.GetState(Share));
        Assert.False(monitor.IsUnreachable(Share));
        Assert.True(watch.Elapsed < TimeSpan.FromMilliseconds(500));

        probe.Release();
        Assert.True(await check);
    }

    [Fact]
    public async Task EachLocationHasItsOwnProbe_AndAnswersAreCached()
    {
        var monitor = new ReachabilityMonitor(new FuncProbe(p => p.StartsWith('C')));
        await monitor.CheckAllAsync([@"C:\Users\anna", @"P:\Projekte\", @"p:\projekte"]);

        Assert.Equal(Reachability.Reachable, monitor.GetState(@"C:\Users\anna"));
        Assert.Equal(Reachability.Unreachable, monitor.GetState(@"P:\Projekte"));
        Assert.True(monitor.IsUnreachable(@"p:\PROJEKTE\"));
        Assert.Equal(Reachability.Unknown, monitor.GetState(@"D:\other"));
        Assert.Equal(2, monitor.ProbesStarted); // the same place twice is probed once
    }

    [Fact]
    public async Task AThrowingProbeMeansUnreachable()
    {
        var monitor = new ReachabilityMonitor(new FuncProbe(_ => throw new IOException("network name no longer available")));
        Assert.False(await monitor.CheckAsync(Share));
        Assert.Equal(Reachability.Unreachable, monitor.GetState(Share));
    }

    [Fact]
    public void CheckAllIfStale_RunsOnlyWhenTheLastCheckIsOlderThan60Seconds()
    {
        var time = new ManualTimeProvider(new DateTimeOffset(2026, 11, 14, 8, 0, 0, TimeSpan.Zero));
        int calls = 0;
        var monitor = new ReachabilityMonitor(new FuncProbe(_ =>
        {
            Interlocked.Increment(ref calls);
            return true;
        }), time);

        Assert.True(monitor.CheckAllIfStale([Share]));
        Poll.Until(() => monitor.GetState(Share) == Reachability.Reachable, "first check");
        Assert.False(monitor.CheckAllIfStale([Share]));

        time.Advance(TimeSpan.FromSeconds(60));
        Assert.False(monitor.CheckAllIfStale([Share]));
        time.Advance(TimeSpan.FromSeconds(1));
        Assert.True(monitor.CheckAllIfStale([Share]));
        Poll.Until(() => Volatile.Read(ref calls) == 2, "second check");
    }

    [Fact]
    public async Task ChangedIsRaisedWhenTheStateChanges()
    {
        bool there = true;
        int changes = 0;
        var monitor = new ReachabilityMonitor(new FuncProbe(_ => there));
        monitor.Changed += () => Interlocked.Increment(ref changes);

        await monitor.CheckAsync(Share);
        await monitor.CheckAsync(Share);
        Poll.Until(() => Volatile.Read(ref changes) == 1, "one change");
        there = false;
        await monitor.CheckAsync(Share);
        Poll.Until(() => Volatile.Read(ref changes) == 2, "second change");
    }
}
