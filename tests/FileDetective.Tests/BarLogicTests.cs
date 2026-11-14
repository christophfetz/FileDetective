using FileDetective.Core;

namespace FileDetective.Tests;

public class PathShortenerTests
{
    [Fact]
    public void ShortPath_IsUnchanged()
    {
        Assert.Equal(@"C:\Users\anna\", PathShortener.ShortenMiddle(@"C:\Users\anna\", 40));
        Assert.Equal(@"C:\abc", PathShortener.ShortenMiddle(@"C:\abc", 6));
    }

    [Fact]
    public void LongPath_KeepsDriveAndWholeTrailingFolders()
    {
        string result = PathShortener.ShortenMiddle(@"C:\Users\anna\Documents\Projects\Offers\2026\", 30);
        Assert.Equal(@"C:\…\Projects\Offers\2026\", result);
        Assert.True(result.Length <= 30);
    }

    [Fact]
    public void UncPath_KeepsServerAndShare()
    {
        string result = PathShortener.ShortenMiddle(@"\\server\share\Customers\Mueller\Offers\2026\", 34);
        Assert.StartsWith(@"\\server\share\…", result);
        Assert.EndsWith(@"\Offers\2026\", result);
        Assert.True(result.Length <= 34);
    }

    [Fact]
    public void LastFolderTooLong_IsCutByCharacters()
    {
        string result = PathShortener.ShortenMiddle(@"C:\a\" + new string('x', 80), 20);
        Assert.StartsWith(@"C:\…", result);
        Assert.Equal(20, result.Length);
    }

    [Fact]
    public void NoRecognizableStart_CutsInTheMiddle()
    {
        string result = PathShortener.ShortenMiddle(new string('a', 30) + new string('b', 30), 11);
        Assert.Equal(11, result.Length);
        Assert.Equal("aaaaa…bbbbb", result);
    }

    [Theory]
    [InlineData(3)]
    [InlineData(10)]
    [InlineData(25)]
    [InlineData(44)]
    public void Result_NeverExceedsMaximum(int max)
    {
        string[] paths =
        [
            @"C:\Users\anna\Documents\Projects\Offers\2026\",
            @"\\srv\share\" + new string('q', 200) + @"\x\",
            @"D:\" + string.Join('\\', Enumerable.Repeat("folder", 30)) + @"\",
            new string('z', 100),
        ];
        foreach (var path in paths)
        {
            Assert.True(PathShortener.ShortenMiddle(path, max).Length <= max);
        }
    }
}

public class BarPlacementTests
{
    [Fact]
    public void CentersHorizontally_AndSitsInUpperPart()
    {
        var work = new PixelRect(0, 0, 1920, 1040);
        var (x, y) = BarPlacement.Compute(work, 752, 500);
        Assert.Equal((1920 - 752) / 2, x);
        Assert.Equal((int)(1040 * BarPlacement.TopFraction), y);
    }

    [Fact]
    public void SecondMonitor_WithNegativeOrigin_IsRespected()
    {
        var work = new PixelRect(-2560, -200, 0, 1240);
        var (x, y) = BarPlacement.Compute(work, 1128, 780);
        Assert.Equal(-2560 + (2560 - 1128) / 2, x);
        Assert.Equal(-200 + (int)(1440 * BarPlacement.TopFraction), y);
    }

    [Fact]
    public void ScaledWindow_UsesPixelWidth()
    {
        // 150 % scaling: 752 DIP are 1128 px, reserved 520 DIP are 780 px.
        var work = new PixelRect(0, 0, 2560, 1400);
        var (x, _) = BarPlacement.Compute(work, 1128, 780);
        Assert.Equal((2560 - 1128) / 2, x);
    }

    [Fact]
    public void LowMonitor_MovesUpSoReservedHeightFits()
    {
        var work = new PixelRect(0, 0, 1366, 728);
        var (_, y) = BarPlacement.Compute(work, 752, 600);
        Assert.Equal(728 - 600, y);
    }

    [Fact]
    public void WindowLargerThanArea_StaysAtTopLeft()
    {
        var work = new PixelRect(100, 50, 600, 350);
        var (x, y) = BarPlacement.Compute(work, 900, 900);
        Assert.Equal(100, x);
        Assert.Equal(50, y);
    }
}

public class BarStateTests
{
    [Theory]
    [InlineData(false, true, false, IndexPhase.Loading)]
    [InlineData(false, false, true, IndexPhase.Loading)]
    [InlineData(true, true, false, IndexPhase.FirstScan)]
    [InlineData(true, true, true, IndexPhase.Ready)]
    [InlineData(true, false, false, IndexPhase.Ready)]
    [InlineData(true, false, true, IndexPhase.Ready)]
    public void Resolve_ChoosesPhase(bool loaded, bool scanning, bool everCompleted, IndexPhase expected)
        => Assert.Equal(expected, BarState.Resolve(loaded, scanning, everCompleted));

    [Fact]
    public void CycleMode_GoesAllFilesFoldersProgramsAndAround()
    {
        Assert.Equal(SearchMode.Files, BarState.CycleMode(SearchMode.All, false));
        Assert.Equal(SearchMode.Folders, BarState.CycleMode(SearchMode.Files, false));
        Assert.Equal(SearchMode.Programs, BarState.CycleMode(SearchMode.Folders, false));
        Assert.Equal(SearchMode.All, BarState.CycleMode(SearchMode.Programs, false));
        Assert.Equal(SearchMode.Programs, BarState.CycleMode(SearchMode.All, true));
        Assert.Equal(SearchMode.Folders, BarState.CycleMode(SearchMode.Programs, true));
        Assert.Equal(SearchMode.Files, BarState.CycleMode(SearchMode.Folders, true));
        Assert.Equal(SearchMode.All, BarState.CycleMode(SearchMode.Files, true));
    }
}

public class ExistenceCheckTests
{
    private sealed class FakeProbe(params string[] existing) : IPathProbe
    {
        private readonly HashSet<string> _existing = new(existing, StringComparer.OrdinalIgnoreCase);

        public bool Exists(string path) => _existing.Contains(path);
    }

    private static readonly string[] Roots = [@"P:\Projects", @"C:\Users\anna"];

    [Fact]
    public void ExistingPath_Exists()
        => Assert.Equal(
            ExistenceResult.Exists,
            ExistenceCheck.Evaluate(@"C:\Users\anna\a.txt", Roots, new FakeProbe(@"C:\Users\anna\a.txt")));

    [Fact]
    public void MissingFile_WithReadableParentAndRoot_IsVanished()
        => Assert.Equal(
            ExistenceResult.Vanished,
            ExistenceCheck.Evaluate(@"C:\Users\anna\docs\a.txt", Roots, new FakeProbe(@"C:\Users\anna", @"C:\Users\anna\docs")));

    [Fact]
    public void DeadShare_IsUnknown_NeverVanished()
        => Assert.Equal(
            ExistenceResult.Unknown,
            ExistenceCheck.Evaluate(@"P:\Projects\x\a.txt", Roots, new FakeProbe()));

    [Fact]
    public void MissingParent_IsUnknown()
        => Assert.Equal(
            ExistenceResult.Unknown,
            ExistenceCheck.Evaluate(@"P:\Projects\x\a.txt", Roots, new FakeProbe(@"P:\Projects")));

    [Fact]
    public void PathOutsideAllLocations_IsUnknown()
        => Assert.Equal(
            ExistenceResult.Unknown,
            ExistenceCheck.Evaluate(@"D:\other\a.txt", Roots, new FakeProbe(@"D:\other")));

    [Fact]
    public void LocationItself_IsNeverVanished()
        => Assert.Equal(
            ExistenceResult.Unknown,
            ExistenceCheck.Evaluate(@"C:\Users\anna", Roots, new FakeProbe(@"C:\Users")));

    [Fact]
    public void LongestMatchingLocationIsUsed_NotAPrefixOfAnotherName()
    {
        // "C:\Users\anna2\..." must not be treated as inside "C:\Users\anna".
        Assert.Equal(
            ExistenceResult.Unknown,
            ExistenceCheck.Evaluate(@"C:\Users\anna2\a.txt", Roots, new FakeProbe(@"C:\Users\anna", @"C:\Users\anna2")));
    }
}

public class SearchCoordinatorTests
{
    // A delay that the test releases by hand, but still honours cancellation.
    private sealed class ManualDelay
    {
        private readonly object _gate = new();
        private readonly List<TaskCompletionSource> _waiting = [];

        public int Calls { get; private set; }

        public Task Wait(TimeSpan _, CancellationToken token)
        {
            var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            token.Register(() => tcs.TrySetCanceled(token));
            lock (_gate)
            {
                Calls++;
                _waiting.Add(tcs);
            }

            return tcs.Task;
        }

        public void ReleaseAll()
        {
            lock (_gate)
            {
                foreach (var t in _waiting)
                {
                    t.TrySetResult();
                }
            }
        }
    }

    private static SearchResult ResultFor(string input)
        => new([new SearchHit(1, new EntryDetails(input, input, false, 0, DateTime.UnixEpoch, 0), 2, 0)], 1);

    private static async Task Eventually(Func<bool> condition)
    {
        for (int i = 0; i < 500 && !condition(); i++)
        {
            await Task.Delay(10);
        }

        Assert.True(condition());
    }

    [Fact]
    public async Task RapidRequests_OnlyNewestSearchRuns()
    {
        var delay = new ManualDelay();
        var searched = new List<string>();
        var delivered = new List<(long, SearchResult)>();
        var coordinator = new SearchCoordinator(
            (input, _, _) =>
            {
                lock (searched)
                {
                    searched.Add(input);
                }

                return ResultFor(input);
            },
            (g, r) =>
            {
                lock (delivered)
                {
                    delivered.Add((g, r));
                }
            },
            delay: delay.Wait);

        coordinator.Request("a", SearchMode.All);
        coordinator.Request("ab", SearchMode.All);
        long last = coordinator.Request("abc", SearchMode.All);
        await Eventually(() => delay.Calls == 3);
        delay.ReleaseAll();
        await Eventually(() =>
        {
            lock (delivered)
            {
                return delivered.Count == 1;
            }
        });

        lock (searched)
        {
            Assert.Equal(["abc"], searched);
        }

        lock (delivered)
        {
            Assert.Equal(last, delivered[0].Item1);
            Assert.Equal("abc", delivered[0].Item2.Hits[0].Details.FullPath);
        }
    }

    [Fact]
    public async Task NewRequest_CancelsRunningSearch_AndDropsItsResult()
    {
        var started = new ManualResetEventSlim();
        var proceed = new ManualResetEventSlim();
        CancellationToken firstToken = default;
        var delivered = new List<string>();
        var coordinator = new SearchCoordinator(
            (input, _, token) =>
            {
                if (input == "first")
                {
                    firstToken = token;
                    started.Set();
                    proceed.Wait(TimeSpan.FromSeconds(10));
                    // A well-behaved search notices the cancellation.
                    token.ThrowIfCancellationRequested();
                }

                return ResultFor(input);
            },
            (_, r) =>
            {
                lock (delivered)
                {
                    delivered.Add(r.Hits[0].Details.FullPath);
                }
            },
            debounce: TimeSpan.Zero);

        coordinator.Request("first", SearchMode.All);
        Assert.True(started.Wait(TimeSpan.FromSeconds(10)));
        coordinator.Request("second", SearchMode.All);
        Assert.True(firstToken.IsCancellationRequested);
        proceed.Set();

        await Eventually(() =>
        {
            lock (delivered)
            {
                return delivered.Count >= 1;
            }
        });
        await Task.Delay(100);
        lock (delivered)
        {
            Assert.Equal(["second"], delivered);
        }
    }

    [Fact]
    public async Task SearchThatIgnoresCancellation_StillDoesNotDeliverStaleResult()
    {
        var started = new ManualResetEventSlim();
        var proceed = new ManualResetEventSlim();
        var delivered = new List<string>();
        var coordinator = new SearchCoordinator(
            (input, _, _) =>
            {
                if (input == "slow")
                {
                    started.Set();
                    proceed.Wait(TimeSpan.FromSeconds(10));
                }

                return ResultFor(input);
            },
            (_, r) =>
            {
                lock (delivered)
                {
                    delivered.Add(r.Hits[0].Details.FullPath);
                }
            },
            debounce: TimeSpan.Zero);

        coordinator.Request("slow", SearchMode.All);
        Assert.True(started.Wait(TimeSpan.FromSeconds(10)));
        coordinator.Request("fast", SearchMode.All);
        await Eventually(() =>
        {
            lock (delivered)
            {
                return delivered.Count == 1;
            }
        });
        proceed.Set();
        await Task.Delay(100);
        lock (delivered)
        {
            Assert.Equal(["fast"], delivered);
        }
    }

    [Fact]
    public async Task CancelPending_DeliversNothing()
    {
        var delay = new ManualDelay();
        int delivered = 0;
        var coordinator = new SearchCoordinator(
            (input, _, _) => ResultFor(input), (_, _) => Interlocked.Increment(ref delivered), delay: delay.Wait);

        coordinator.Request("x", SearchMode.All);
        await Eventually(() => delay.Calls == 1);
        coordinator.CancelPending();
        delay.ReleaseAll();
        await Task.Delay(100);
        Assert.Equal(0, delivered);
    }

    [Fact]
    public async Task Failure_IsReported_NotThrown()
    {
        Exception? failure = null;
        var coordinator = new SearchCoordinator(
            (_, _, _) => throw new InvalidOperationException("boom"),
            (_, _) => { },
            ex => failure = ex,
            TimeSpan.Zero);

        coordinator.Request("x", SearchMode.All);
        await Eventually(() => failure is not null);
        Assert.IsType<InvalidOperationException>(failure);
    }

    [Fact]
    public async Task ModeIsPassedThrough()
    {
        SearchMode? seen = null;
        var coordinator = new SearchCoordinator(
            (input, mode, _) =>
            {
                seen = mode;
                return ResultFor(input);
            },
            (_, _) => { },
            debounce: TimeSpan.Zero);

        coordinator.Request("x", SearchMode.Folders);
        await Eventually(() => seen is not null);
        Assert.Equal(SearchMode.Folders, seen);
    }
}
