using FileDetective.Core;

namespace FileDetective.Tests;

public class DirectoryRebuilderTests : IDisposable
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    private (RootInfo Local, RootInfo Share) Build(StorageFixture f)
    {
        var local = f.Store.AddRoot(@"C:\Users\anna", null, RootKind.Local);
        var share = f.Store.AddRoot(@"P:\Projekte", @"\\srv\projekte", RootKind.Network);
        int docs = f.Add(local, local.RootEntryId, "Dokumente", true);
        f.Add(local, docs, "Angebot.pdf", false);
        int a = f.Add(share, share.RootEntryId, "Alpha", true);
        f.Add(share, a, "plan.xlsx", false);
        return (local, share);
    }

    /// <summary>A queue whose scans run until they are cancelled (or released) and record what they saw.</summary>
    private sealed class FakeScans : IDisposable
    {
        private readonly object _gate = new();
        private readonly Func<int> _entryCount;
        private readonly HashSet<byte> _blockUntilCancelled = [];

        public FakeScans(Func<int> entryCount)
        {
            _entryCount = entryCount;
            Queue = new ScanQueue(Run);
        }

        public ScanQueue Queue { get; }

        public List<(ScanRequest Request, int EntriesAtStart, bool Cancelled)> Runs { get; } = [];

        public ManualResetEventSlim Started { get; } = new(false);

        public void BlockUntilCancelled(byte rootId)
        {
            lock (_gate)
            {
                _blockUntilCancelled.Add(rootId);
            }
        }

        private void Run(ScanRequest request, CancellationToken token)
        {
            int entries = _entryCount();
            bool block;
            lock (_gate)
            {
                block = _blockUntilCancelled.Remove(request.RootId);
            }

            if (block)
            {
                Started.Set();
                token.WaitHandle.WaitOne(Wait);
            }

            lock (_gate)
            {
                Runs.Add((request, entries, token.IsCancellationRequested));
            }
        }

        public void Dispose() => Queue.Dispose();
    }

    [Fact]
    public void Rebuild_CancelsRunningScan_EmptiesEntries_AndQueuesEveryLocationManually()
    {
        using var f = new StorageFixture(_dir);
        var (local, share) = Build(f);
        using var scans = new FakeScans(() => f.Index.Count);
        scans.BlockUntilCancelled(local.Id);
        var locks = new LocationLocks();
        var unwatched = new List<byte>();
        IReadOnlyList<RootInfo>? resumed = null;
        bool afterResetSawEmpty = false;

        // A scan of the local location is running, the share waits behind it.
        scans.Queue.Enqueue(new ScanRequest(local.Id, local.Kind, ScanTrigger.Startup));
        scans.Queue.Enqueue(new ScanRequest(share.Id, share.Kind, ScanTrigger.Scheduled));
        Assert.True(scans.Started.Wait(Wait));
        Assert.Equal(QueuedScanState.Running, scans.Queue.GetState(local.Id));
        Assert.Equal(QueuedScanState.Queued, scans.Queue.GetState(share.Id));
        int before = f.Index.Count;

        var result = DirectoryRebuilder.Rebuild(
            f.Store, scans.Queue, locks,
            unwatch: unwatched.Add,
            resumeWatching: r => resumed = r,
            afterReset: () => afterResetSawEmpty = f.Index.Count == 2 && locks.IsHeld(local.Id) && locks.IsHeld(share.Id));

        Assert.Equal(before, result.Removed);
        Assert.Equal(2, result.Queued);
        Assert.True(afterResetSawEmpty, "afterReset runs with the locks held and the memory already emptied");
        Assert.Equal([local.Id, share.Id], unwatched);
        Assert.Equal(2, resumed!.Count);
        Assert.False(locks.IsHeld(local.Id));
        Assert.False(locks.IsHeld(share.Id));

        Assert.True(scans.Queue.WaitIdle(Wait));
        var runs = scans.Runs.ToArray();
        // 1. the cancelled first scan (it saw all entries and was cancelled), then the two new ones, local first.
        Assert.Equal(3, runs.Length);
        Assert.Equal(ScanTrigger.Startup, runs[0].Request.Trigger);
        Assert.True(runs[0].Cancelled);
        Assert.Equal(before, runs[0].EntriesAtStart);
        Assert.Equal(local.Id, runs[1].Request.RootId);
        Assert.Equal(share.Id, runs[2].Request.RootId);
        Assert.All(runs.Skip(1), r =>
        {
            Assert.Equal(ScanTrigger.Manual, r.Request.Trigger); // manual: no attempt against the daily limit
            Assert.Equal(2, r.EntriesAtStart); // only the fresh root entries: the old entries were gone before the scan
            Assert.False(r.Cancelled);
        });
    }

    [Fact]
    public void Rebuild_KeepsLocationsUsageAndScanCounters_AndTheLocationsStayScannable()
    {
        using var f = new StorageFixture(_dir);
        var (local, share) = Build(f);
        f.Usage.RecordOpen(@"C:\Users\anna\Dokumente\Angebot.pdf");
        f.Store.UpdateRoot(share with { AttemptsToday = 3, LastScanStartedUtc = 5, LastScanCompletedUtc = 6, LastScanStatus = ScanStatus.Complete });
        using var scans = new FakeScans(() => f.Index.Count);

        DirectoryRebuilder.Rebuild(f.Store, scans.Queue, new LocationLocks());
        Assert.True(scans.Queue.WaitIdle(Wait));

        Assert.Equal(2, f.Store.GetRoots().Count);
        Assert.Equal(1, f.Usage.GetOpenCount(@"C:\Users\anna\Dokumente\Angebot.pdf"));
        var s = f.Store.GetRoot(share.Id)!;
        Assert.Equal(3, s.AttemptsToday);
        Assert.Equal(ScanStatus.Never, s.LastScanStatus);
        Assert.Null(s.LastScanCompletedUtc);

        // The queue accepts requests again afterwards (nothing stays blocked).
        Assert.True(scans.Queue.Enqueue(new ScanRequest(local.Id, local.Kind, ScanTrigger.Manual)));
    }

    [Fact]
    public void Rebuild_DoesNotQueueDisabledLocations()
    {
        using var f = new StorageFixture(_dir);
        var (local, share) = Build(f);
        f.Store.UpdateRoot(share with { Enabled = false });
        using var scans = new FakeScans(() => f.Index.Count);

        var result = DirectoryRebuilder.Rebuild(f.Store, scans.Queue, new LocationLocks());

        Assert.Equal(1, result.Queued);
        Assert.True(scans.Queue.WaitIdle(Wait));
        Assert.Equal([local.Id], scans.Runs.Select(r => r.Request.RootId).ToArray());
    }

    [Fact]
    public void Rebuild_Failure_KeepsTheOldDirectory_ReleasesLocksAndQueue_AndResumesWatching()
    {
        using var f = new StorageFixture(_dir);
        var (local, share) = Build(f);
        f.Load();
        int before = f.Index.Count;
        string[] pathsBefore = f.Paths("angebot");

        // The database refuses to delete entries.
        using (var c = f.Db.OpenConnection())
        using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = "CREATE TRIGGER no_delete BEFORE DELETE ON entries BEGIN SELECT RAISE(ABORT, 'refused'); END";
            cmd.ExecuteNonQuery();
        }

        using var scans = new FakeScans(() => f.Index.Count);
        var locks = new LocationLocks();
        bool resumed = false;

        Assert.ThrowsAny<Exception>(() => DirectoryRebuilder.Rebuild(
            f.Store, scans.Queue, locks, resumeWatching: _ => resumed = true));

        Assert.Equal(before, f.Index.Count);
        Assert.Equal(pathsBefore, f.Paths("angebot"));
        Assert.Equal(before, f.Store.CountEntriesByRoot().Values.Sum());
        Assert.False(locks.IsHeld(local.Id));
        Assert.False(locks.IsHeld(share.Id));
        Assert.True(resumed);
        Assert.Empty(scans.Runs); // nothing was queued

        // The queue is not blocked any more.
        Assert.True(scans.Queue.Enqueue(new ScanRequest(local.Id, local.Kind, ScanTrigger.Manual)));
        Assert.True(scans.Queue.WaitIdle(Wait));
    }

    [Fact]
    public async Task Rebuild_WaitsForWhoeverHoldsALocationLock()
    {
        using var f = new StorageFixture(_dir);
        var (local, _) = Build(f);
        using var scans = new FakeScans(() => f.Index.Count);
        var locks = new LocationLocks();
        var held = locks.Acquire(local.Id); // e.g. the watcher in the middle of a batch

        var task = Task.Run(() => DirectoryRebuilder.Rebuild(f.Store, scans.Queue, locks));
        Assert.NotEqual(task, await Task.WhenAny(task, Task.Delay(400))); // the rebuild must wait for the lock
        Assert.True(f.Index.Count > 2, "nothing was deleted while the lock was held");

        held.Dispose();
        Assert.Equal(task, await Task.WhenAny(task, Task.Delay(Wait)));
        Assert.Equal(2, (await task).Queued);
        Assert.True(scans.Queue.WaitIdle(Wait));
    }

    [Fact]
    public void Rebuild_WithoutLocations_DoesNothing()
    {
        using var f = new StorageFixture(_dir);
        using var scans = new FakeScans(() => f.Index.Count);
        var result = DirectoryRebuilder.Rebuild(f.Store, scans.Queue, new LocationLocks());
        Assert.Equal(new RebuildResult(0, 0), result);
    }
}
