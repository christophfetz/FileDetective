using FileDetective.Core;

namespace FileDetective.Tests;

/// <summary>The queue of whole-location scans (PLAN.md 7.2): one at a time, local first, merging, cancellation.</summary>
public class ScanQueueTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    /// <summary>A runner whose scans block until released; records the order and counts parallel runs.</summary>
    private sealed class GatedRunner
    {
        private readonly object _gate = new();
        private readonly Dictionary<byte, ManualResetEventSlim> _release = [];
        private int _active;

        public List<ScanRequest> Started { get; } = [];

        public List<byte> Cancelled { get; } = [];

        public int MaxParallel { get; private set; }

        public void Run(ScanRequest request, CancellationToken token)
        {
            ManualResetEventSlim release;
            lock (_gate)
            {
                Started.Add(request);
                MaxParallel = Math.Max(MaxParallel, ++_active);
                release = Gate(request.RootId);
            }

            try
            {
                release.Wait(token);
            }
            catch (OperationCanceledException)
            {
                lock (_gate)
                {
                    Cancelled.Add(request.RootId);
                }

                throw;
            }
            finally
            {
                lock (_gate)
                {
                    _active--;
                }
            }
        }

        public void Release(byte rootId)
        {
            lock (_gate)
            {
                Gate(rootId).Set();
            }
        }

        public void ReleaseAll()
        {
            lock (_gate)
            {
                foreach (byte id in Enumerable.Range(0, 256).Select(i => (byte)i))
                {
                    Gate(id).Set();
                }
            }
        }

        public int StartedCount
        {
            get
            {
                lock (_gate)
                {
                    return Started.Count;
                }
            }
        }

        public byte[] StartedIds()
        {
            lock (_gate)
            {
                return [.. Started.Select(s => s.RootId)];
            }
        }

        private ManualResetEventSlim Gate(byte id)
        {
            if (!_release.TryGetValue(id, out var gate))
            {
                _release[id] = gate = new ManualResetEventSlim(false);
            }

            return gate;
        }
    }

    private static ScanRequest Local(byte id, ScanTrigger trigger = ScanTrigger.Startup) => new(id, RootKind.Local, trigger);

    private static ScanRequest Share(byte id, ScanTrigger trigger = ScanTrigger.Scheduled) => new(id, RootKind.Network, trigger);

    [Fact]
    public void LocalLocationsRunBeforeShares_InArrivalOrderWithinEachGroup()
    {
        var runner = new GatedRunner();
        using var queue = new ScanQueue(runner.Run);

        queue.Enqueue(Local(1)); // starts at once and blocks
        Poll.Until(() => runner.StartedCount == 1, "first scan started");

        queue.Enqueue(Share(10));
        queue.Enqueue(Local(2));
        queue.Enqueue(Share(11));
        queue.Enqueue(new ScanRequest(3, RootKind.Removable, ScanTrigger.Scheduled));
        queue.Enqueue(Local(4));

        Assert.Equal([2, 3, 4, 10, 11], queue.Queued.Select(r => (int)r.RootId));

        runner.ReleaseAll();
        Assert.True(queue.WaitIdle(Wait));
        Assert.Equal([1, 2, 3, 4, 10, 11], runner.StartedIds().Select(i => (int)i));
        Assert.Equal(1, runner.MaxParallel);
    }

    [Fact]
    public void NeverRunsTwoScansAtTheSameTime()
    {
        var runner = new GatedRunner();
        using var queue = new ScanQueue(runner.Run);
        for (byte id = 1; id <= 6; id++)
        {
            queue.Enqueue(id % 2 == 0 ? Share(id) : Local(id));
        }

        // Release one after the other while checking that only one ever runs.
        for (int n = 1; n <= 6; n++)
        {
            Poll.Until(() => runner.StartedCount == n, $"scan {n} started");
            Assert.Equal(1, runner.MaxParallel);
            runner.Release(runner.StartedIds()[n - 1]);
        }

        Assert.True(queue.WaitIdle(Wait));
        Assert.Equal(1, runner.MaxParallel);
    }

    [Fact]
    public void DuplicateRequestsForAQueuedLocationAreMerged()
    {
        var runner = new GatedRunner();
        using var queue = new ScanQueue(runner.Run);
        queue.Enqueue(Local(1));
        Poll.Until(() => runner.StartedCount == 1, "first scan started");

        Assert.True(queue.Enqueue(Share(5)));
        Assert.False(queue.Enqueue(Share(5)));
        Assert.False(queue.Enqueue(Share(5, ScanTrigger.Manual)));
        Assert.True(queue.Enqueue(Local(2)));
        Assert.False(queue.Enqueue(Local(2, ScanTrigger.Manual)));

        Assert.Equal(2, queue.Queued.Count);

        // A manual request upgrades a waiting scheduled one, so it does not count as an automatic attempt.
        Assert.Equal(ScanTrigger.Manual, queue.Queued.Single(r => r.RootId == 5).Trigger);

        runner.ReleaseAll();
        Assert.True(queue.WaitIdle(Wait));
        Assert.Equal([1, 2, 5], runner.StartedIds().Select(i => (int)i));
    }

    [Fact]
    public void RequestForTheRunningLocation_IsServedByTheRunningScan_ExceptAfterOverflow()
    {
        var runner = new GatedRunner();
        using var queue = new ScanQueue(runner.Run);
        queue.Enqueue(Local(1));
        Poll.Until(() => runner.StartedCount == 1, "scan started");
        Assert.Equal(QueuedScanState.Running, queue.GetState(1));

        Assert.False(queue.Enqueue(Local(1, ScanTrigger.Manual)));
        Assert.Empty(queue.Queued);

        // Events were lost while the scan may already have passed the folders: one more scan afterwards.
        Assert.False(queue.Enqueue(Local(1, ScanTrigger.Overflow)));
        runner.Release(1);
        Poll.Until(() => runner.StartedCount == 2, "second scan after overflow");
        Assert.Equal(ScanTrigger.Overflow, runner.Started[1].Trigger);
        Assert.True(queue.WaitIdle(Wait));
        Assert.Equal(2, runner.StartedCount);
    }

    [Fact]
    public void CancelAndBlock_CancelsTheRunningScan_DropsTheQueuedOne_AndRejectsNewRequestsUntilReleased()
    {
        var runner = new GatedRunner();
        using var queue = new ScanQueue(runner.Run);
        queue.Enqueue(Local(1));
        Poll.Until(() => runner.StartedCount == 1, "scan started");
        queue.Enqueue(Local(2));
        queue.Enqueue(Share(3));

        using (queue.CancelAndBlock(1))
        {
            // Returned only after the running scan of location 1 has ended.
            Assert.Equal([1], runner.Cancelled.Select(i => (int)i));
            Assert.NotEqual(QueuedScanState.Running, queue.GetState(1));
            Assert.False(queue.Enqueue(Local(1, ScanTrigger.Manual)));
        }

        Assert.True(queue.Enqueue(Local(1, ScanTrigger.Manual)));

        using (queue.CancelAndBlock(3))
        {
            Assert.DoesNotContain(queue.Queued, r => r.RootId == 3);
        }

        runner.ReleaseAll();
        Assert.True(queue.WaitIdle(Wait));
        Assert.Equal([1, 2, 1], runner.StartedIds().Select(i => (int)i));
    }

    [Fact]
    public void Shutdown_CancelsTheRunningScan_DropsTheRest_AndRejectsNewRequests()
    {
        var runner = new GatedRunner();
        var queue = new ScanQueue(runner.Run);
        queue.Enqueue(Share(1));
        Poll.Until(() => runner.StartedCount == 1, "scan started");
        queue.Enqueue(Local(2));

        Assert.True(queue.Shutdown(Wait));
        Assert.Equal([1], runner.Cancelled.Select(i => (int)i));
        Assert.False(queue.IsBusy);
        Assert.False(queue.Enqueue(Local(3)));
        Assert.Equal(1, runner.StartedCount);
        queue.Dispose();
    }

    [Fact]
    public void AFailingScanIsReported_AndTheQueueGoesOn()
    {
        var failures = new List<byte>();
        var ran = new List<byte>();
        using var queue = new ScanQueue((request, _) =>
        {
            lock (ran)
            {
                ran.Add(request.RootId);
            }

            if (request.RootId == 1)
            {
                throw new InvalidOperationException("boom");
            }
        });
        queue.Failed += (request, _) =>
        {
            lock (failures)
            {
                failures.Add(request.RootId);
            }
        };

        queue.Enqueue(Local(1));
        queue.Enqueue(Local(2));
        Assert.True(queue.WaitIdle(Wait));
        Poll.Until(() => ran.Count == 2, "both ran");
        Assert.Equal([1], failures.Select(i => (int)i));
    }
}
