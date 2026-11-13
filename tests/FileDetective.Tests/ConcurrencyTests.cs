using System.Collections.Concurrent;
using System.Diagnostics;
using FileDetective.Core;

namespace FileDetective.Tests;

public class ConcurrencyTests
{
    private const int StableCount = 20_000;

    [Fact]
    public void SearchingAndChangingFromSeveralThreads_HasNoErrors()
    {
        using var index = new MemoryIndex();
        using (var u = index.BeginUpdate())
        {
            u.Set(1, 0, 1, @"C:\r", true, 0);
            for (int i = 0; i < StableCount; i++)
            {
                u.Set(2 + i, 1, 1, $"stable {i}.txt", false, i);
            }
        }

        var errors = new ConcurrentQueue<Exception>();
        var stop = Stopwatch.StartNew();
        var duration = TimeSpan.FromSeconds(1.5);
        int searches = 0;
        int writes = 0;

        void Run(Action body)
        {
            try
            {
                while (stop.Elapsed < duration)
                {
                    body();
                }
            }
            catch (Exception ex)
            {
                errors.Enqueue(ex);
            }
        }

        var threads = new List<Thread>();

        // Searchers: the untouched entries must always be found completely.
        for (int s = 0; s < 4; s++)
        {
            threads.Add(new Thread(() => Run(() =>
            {
                var r = index.Search(SearchQuery.Parse("stable .txt"));
                if (r.TotalCount != StableCount)
                {
                    throw new InvalidOperationException($"Expected {StableCount} stable hits, got {r.TotalCount}.");
                }

                _ = index.Search(SearchQuery.Parse("volatile"));
                Interlocked.Increment(ref searches);
            })));
        }

        // Writer 1: single changes (add, rename, delete) in its own id range.
        var rnd1 = new Random(1);
        threads.Add(new Thread(() => Run(() =>
        {
            int id = 100_000 + rnd1.Next(50_000);
            index.Set(id, 1, 1, "volatile " + id, rnd1.Next(2) == 0, id);
            index.Rename(id, "volatile renamed " + id);
            if (rnd1.Next(3) == 0)
            {
                index.Delete(id);
            }

            Interlocked.Increment(ref writes);
        })));

        // Writer 2: batches with folders that are created and moved around, in a higher block.
        var rnd2 = new Random(2);
        threads.Add(new Thread(() => Run(() =>
        {
            using (var u = index.BeginUpdate())
            {
                for (int k = 0; k < 50; k++)
                {
                    int id = 200_000 + rnd2.Next(50_000);
                    int parent = rnd2.Next(4) == 0 ? 1 : 200_000 + rnd2.Next(50_000);
                    if (parent != id)
                    {
                        u.Set(id, parent, 2, "volatile dir " + k, true, k);
                    }
                }
            }

            Interlocked.Increment(ref writes);
        })));

        threads.ForEach(t => t.Start());
        threads.ForEach(t => t.Join());

        Assert.Empty(errors);
        Assert.True(searches > 0 && writes > 0, $"searches {searches}, writes {writes}");
    }

    [Fact]
    public void CancelledBeforeStart_Throws()
    {
        using var t = new TestTree();
        t.Root(1, @"C:\r");
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => t.Index.Search(SearchQuery.Parse("r"), cancellationToken: cts.Token));
        Assert.ThrowsAny<OperationCanceledException>(
            () => new SearchEngine(t.Index, t).Search("r", SearchMode.All, cts.Token));
    }

    [Fact]
    public void CancelledDuringSearch_Throws_AndReleasesTheLock()
    {
        using var index = new MemoryIndex();
        using (var u = index.BeginUpdate())
        {
            u.Set(1, 0, 1, @"C:\r", true, 0);
            for (int id = 2; id < 300_000; id++)
            {
                u.Set(id, 1, 1, "match " + id, false, 0);
            }
        }

        using var cts = new CancellationTokenSource();
        var cancelling = new CancellingUsage(cts);
        Assert.ThrowsAny<OperationCanceledException>(
            () => index.Search(SearchQuery.Parse("match"), cancelling, cancellationToken: cts.Token));

        // The read lock was released: writing and searching still work.
        index.Set(300_000, 1, 1, "after", false, 0);
        Assert.Equal(1, index.Search(SearchQuery.Parse("after")).TotalCount);
    }

    private sealed class CancellingUsage(CancellationTokenSource cts) : IUsageLookup
    {
        public bool TryGetLastOpened(int entryId, out long lastOpenedUnixSeconds)
        {
            cts.Cancel(); // first match cancels the running search
            lastOpenedUnixSeconds = 0;
            return false;
        }
    }
}
