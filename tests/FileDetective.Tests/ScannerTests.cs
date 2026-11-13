using FileDetective.Core;

namespace FileDetective.Tests;

/// <summary>Scanner against an in-memory fake of <see cref="IDirectoryLister"/> (PLAN.md 7.2, 7.4, stage 3 acceptance).</summary>
public class ScannerTests : IDisposable
{
    private const string RootPath = @"R:\root";

    private readonly TempDir _dir = new();
    private readonly StorageFixture _f;

    public ScannerTests()
    {
        _f = new StorageFixture(_dir);
    }

    public void Dispose()
    {
        _f.Dispose();
        _dir.Dispose();
    }

    public static TheoryData<Exception> ListingExceptions => new()
    {
        new UnauthorizedAccessException("denied"),
        new IOException("network name no longer available"),
        new DirectoryNotFoundException("gone"),
    };

    /// <summary>
    /// R:\root
    ///   Docs\ (a.txt, b.pdf, Sub\ (deep.txt, Deeper\ (deepest.txt)))
    ///   Music\ (song.mp3)
    ///   top.txt
    /// </summary>
    private static FakeFileSystem SampleTree()
    {
        var fs = new FakeFileSystem(RootPath);
        fs.Dir("Docs", mtimeOffset: 1)
            .File(@"Docs\a.txt", 10, 2)
            .File(@"Docs\b.pdf", 20, 3)
            .Dir(@"Docs\Sub", mtimeOffset: 4)
            .File(@"Docs\Sub\deep.txt", 30, 5)
            .Dir(@"Docs\Sub\Deeper", mtimeOffset: 6)
            .File(@"Docs\Sub\Deeper\deepest.txt", 40, 7)
            .Dir("Music", mtimeOffset: 8)
            .File(@"Music\song.mp3", 50, 9)
            .File("top.txt", 60, 10);
        return fs;
    }

    private (RootInfo Root, Scanner Scanner, FakeScanClock Clock) Setup(
        FakeFileSystem fs,
        RootKind kind = RootKind.Local,
        ExcludeRules? rules = null,
        int batchSize = Scanner.DefaultBatchSize,
        NetworkPoliteness politeness = NetworkPoliteness.Normal,
        ILinkDetector? links = null)
    {
        var root = _f.Store.AddRoot(RootPath, kind == RootKind.Network ? @"\\server\share" : null, kind);
        var clock = new FakeScanClock();
        fs.Clock = clock;
        var scanner = new Scanner(_f.Store, fs, rules ?? ExcludeRules.None, new ScannerOptions
        {
            BatchSize = batchSize,
            Clock = clock,
            Politeness = politeness,
            LinkDetector = links ?? new FakeLinkDetector(),
        });
        return (root, scanner, clock);
    }

    // ------------------------------------------------------------------ first and second scan

    [Fact]
    public void FirstScan_RecordsEverything_AndSetsState()
    {
        var fs = SampleTree();
        var (root, scanner, _) = Setup(fs);

        var stats = scanner.ScanRoot(root.Id);

        Assert.Equal(ScanOutcome.Complete, stats.Outcome);
        Assert.Equal(5, stats.DirectoriesListed); // root, Docs, Sub, Deeper, Music
        Assert.Equal(10, stats.Entries);
        Assert.Equal(10, stats.Inserted);
        Assert.Equal(0, stats.FailedDirectories);
        ScanAssert.TreeEquals(_f, root.Id, fs.Expected());

        var state = _f.Store.GetRoot(root.Id)!;
        Assert.Equal(ScanStatus.Complete, state.LastScanStatus);
        Assert.NotNull(state.LastScanStartedUtc);
        Assert.NotNull(state.LastScanCompletedUtc);
        Assert.Equal(0, state.LastScanFailedDirs);
    }

    [Fact]
    public void SecondScan_AfterCreateDeleteRenameAndChanges_MatchesExactly()
    {
        var fs = SampleTree();
        fs.File(@"Music\keep.flac", 70, 11);
        var (root, scanner, _) = Setup(fs);
        scanner.ScanRoot(root.Id);
        var before = ScanAssert.ReadTree(_f, root.Id);

        fs.File(@"Docs\new.txt", 5, 20);                       // new file
        fs.Dir(@"Docs\NewDir", mtimeOffset: 21).File(@"Docs\NewDir\inside.txt", 6, 22); // new folder with content
        fs.Remove(@"Docs\b.pdf");                              // deleted file
        fs.Remove(@"Docs\Sub\Deeper");                         // deleted folder with subtree
        fs.Remove("top.txt").File("renamed-top.txt", 60, 10);  // renamed file
        fs.Remove("Music").Dir("Musik", mtimeOffset: 8)        // renamed folder with content
            .File(@"Musik\song.mp3", 50, 9).File(@"Musik\keep.flac", 70, 11);
        fs.File(@"Docs\a.txt", 11, 2);                         // size changed
        fs.File(@"Docs\Sub\deep.txt", 30, 99);                 // time changed
        fs.Remove(@"Docs\Sub").Dir(@"Docs\SUB", mtimeOffset: 4) // case-only rename of a folder
            .File(@"Docs\SUB\deep.txt", 30, 99);
        var stats = scanner.ScanRoot(root.Id);

        Assert.Equal(ScanOutcome.Complete, stats.Outcome);
        ScanAssert.TreeEquals(_f, root.Id, fs.Expected());

        // Unchanged and changed-in-place entries keep their numbers; the case-only rename keeps the folder.
        var after = ScanAssert.ReadTree(_f, root.Id);
        Assert.Equal(before["Docs"].Id, after["Docs"].Id);
        Assert.Equal(before[@"Docs\a.txt"].Id, after[@"Docs\a.txt"].Id);
        Assert.Equal(before[@"Docs\Sub"].Id, after[@"Docs\SUB"].Id);
        Assert.Equal(before[@"Docs\Sub\deep.txt"].Id, after[@"Docs\SUB\deep.txt"].Id);

        // A third scan without changes changes nothing.
        string snapshot = ScanAssert.EntriesSnapshot(_f);
        var third = scanner.ScanRoot(root.Id);
        Assert.Equal(0, third.Inserted + third.Updated + third.Deleted);
        Assert.Equal(snapshot, ScanAssert.EntriesSnapshot(_f));
    }

    [Fact]
    public void SecondScan_FileBecomesFolderAndAttributesChange()
    {
        var fs = SampleTree();
        fs.File("thing", 1);
        var (root, scanner, _) = Setup(fs);
        scanner.ScanRoot(root.Id);

        fs.Remove("thing").Dir("thing").File(@"thing\child.txt");
        fs.File(@"Docs\a.txt", 10, 2, FileAttributes.Archive | FileAttributes.Hidden);
        fs.Remove("Music").File("Music", 7);
        scanner.ScanRoot(root.Id);

        ScanAssert.TreeEquals(_f, root.Id, fs.Expected());
    }

    [Fact]
    public void ManyEntries_AreWrittenInSeveralBatches_AndMatchExactly()
    {
        var fs = new FakeFileSystem(RootPath);
        fs.Dir("big");
        for (int i = 0; i < 12_345; i++)
        {
            fs.File($@"big\file{i:D5}.txt", i, i % 100);
        }

        var (root, scanner, _) = Setup(fs);
        var stats = scanner.ScanRoot(root.Id);
        Assert.Equal(12_346, stats.Inserted);
        ScanAssert.TreeEquals(_f, root.Id, fs.Expected());

        for (int i = 0; i < 12_345; i += 3)
        {
            fs.Remove($@"big\file{i:D5}.txt");
        }

        stats = scanner.ScanRoot(root.Id);
        Assert.Equal(4_115, stats.Deleted);
        ScanAssert.TreeEquals(_f, root.Id, fs.Expected());
    }

    // ------------------------------------------------------------------ failures (PLAN.md 7.2 point 1, 2.5)

    [Theory]
    [MemberData(nameof(ListingExceptions))]
    public void ListingFailure_KeepsKnownSubtreeCompletelyUnchanged(Exception failure)
    {
        var fs = SampleTree();
        var (root, scanner, _) = Setup(fs);
        scanner.ScanRoot(root.Id);
        var before = ScanAssert.ReadTree(_f, root.Id);

        // Below the failing folder everything changes (or is gone), elsewhere too.
        fs.Remove(@"Docs\Sub\deep.txt");
        fs.Remove(@"Docs\Sub\Deeper");
        fs.File(@"Docs\Sub\fresh.txt");
        fs.Fail(@"Docs\Sub", failure);
        fs.Remove("top.txt");
        fs.File(@"Docs\a.txt", 999, 2);

        var stats = scanner.ScanRoot(root.Id);

        Assert.Equal(ScanOutcome.CompleteWithErrors, stats.Outcome);
        Assert.Equal(1, stats.FailedDirectories);
        var after = ScanAssert.ReadTree(_f, root.Id);
        foreach (var key in before.Keys.Where(k => k.StartsWith(@"Docs\Sub", StringComparison.Ordinal)))
        {
            Assert.Equal(before[key], after[key]); // same number, same parent, same data
        }

        Assert.Equal(4, after.Keys.Count(k => k.StartsWith(@"Docs\Sub", StringComparison.Ordinal)));
        Assert.DoesNotContain(@"Docs\Sub\fresh.txt", after.Keys);

        // Outside the failing folder the comparison ran normally.
        Assert.DoesNotContain("top.txt", after.Keys);
        Assert.Equal(999, after[@"Docs\a.txt"].State.Size);

        var state = _f.Store.GetRoot(root.Id)!;
        Assert.Equal(ScanStatus.CompleteWithErrors, state.LastScanStatus);
        Assert.Equal(1, state.LastScanFailedDirs);

        // Once readable again, the next scan brings the subtree up to date.
        fs.ClearFailures();
        Assert.Equal(ScanOutcome.Complete, scanner.ScanRoot(root.Id).Outcome);
        ScanAssert.TreeEquals(_f, root.Id, fs.Expected());
        Assert.Equal(0, _f.Store.GetRoot(root.Id)!.LastScanFailedDirs);
    }

    [Fact]
    public void ListingFailure_InFirstScan_RecordsFolderWithoutChildren()
    {
        var fs = SampleTree();
        fs.Fail("Docs", new UnauthorizedAccessException());
        var (root, scanner, _) = Setup(fs);

        var stats = scanner.ScanRoot(root.Id);

        Assert.Equal(ScanOutcome.CompleteWithErrors, stats.Outcome);
        var tree = ScanAssert.ReadTree(_f, root.Id);
        Assert.Contains("Docs", tree.Keys);
        Assert.DoesNotContain(tree.Keys, k => k.StartsWith(@"Docs\", StringComparison.Ordinal));
        Assert.Contains(@"Music\song.mp3", tree.Keys);
    }

    [Fact]
    public void SubfolderVanishedBetweenParentAndOwnListing_IsNotDeleted()
    {
        var fs = SampleTree();
        var (root, scanner, _) = Setup(fs);
        scanner.ScanRoot(root.Id);
        var before = ScanAssert.ReadTree(_f, root.Id);

        // The parent listing still shows "Sub", but listing "Sub" itself throws DirectoryNotFoundException.
        fs.RemoveListingOnly(@"Docs\Sub");
        var stats = scanner.ScanRoot(root.Id);

        Assert.Equal(ScanOutcome.CompleteWithErrors, stats.Outcome);
        Assert.Equal(0, stats.Deleted);
        Assert.Equal(before, ScanAssert.ReadTree(_f, root.Id));

        // The next listing of the parent that no longer shows it removes it.
        fs.Remove(@"Docs\Sub");
        Assert.Equal(ScanOutcome.Complete, scanner.ScanRoot(root.Id).Outcome);
        ScanAssert.TreeEquals(_f, root.Id, fs.Expected());
    }

    [Theory]
    [MemberData(nameof(ListingExceptions))]
    public void RootUnreachable_ChangesNothing(Exception failure)
    {
        var fs = SampleTree();
        var (root, scanner, _) = Setup(fs);
        scanner.ScanRoot(root.Id);
        int memoryCount = _f.Index.Count;
        string snapshot = ScanAssert.DatabaseSnapshot(_f);

        fs.Remove("Docs");
        fs.Fail("", failure);
        var stats = scanner.ScanRoot(root.Id);

        Assert.Equal(ScanOutcome.RootUnreachable, stats.Outcome);
        Assert.Equal(snapshot, ScanAssert.DatabaseSnapshot(_f)); // entries and roots row (state, times) unchanged
        Assert.Equal(memoryCount, _f.Index.Count);
        Assert.Equal(new[] { RootPath }, fs.Calls.Skip(5));
    }

    [Fact]
    public void RootUnreachable_OnFirstScan_LeavesStateNever()
    {
        var fs = SampleTree();
        fs.Fail("", new IOException("offline"));
        var (root, scanner, _) = Setup(fs, RootKind.Network);
        string snapshot = ScanAssert.DatabaseSnapshot(_f);

        Assert.Equal(ScanOutcome.RootUnreachable, scanner.ScanRoot(root.Id).Outcome);
        Assert.Equal(snapshot, ScanAssert.DatabaseSnapshot(_f));
        Assert.Equal(ScanStatus.Never, _f.Store.GetRoot(root.Id)!.LastScanStatus);
    }

    [Fact]
    public void UnexpectedException_Propagates_AndMarksScanAborted()
    {
        var fs = SampleTree();
        fs.Fail("Music", new InvalidOperationException("bug"));
        var (root, scanner, _) = Setup(fs);

        Assert.Throws<InvalidOperationException>(() => scanner.ScanRoot(root.Id));
        Assert.Equal(ScanStatus.Aborted, _f.Store.GetRoot(root.Id)!.LastScanStatus);
        Assert.False(scanner.IsRunning);
        ScanAssert.ReadTree(_f, root.Id); // structure still valid
    }

    // ------------------------------------------------------------------ links

    [Fact]
    public void Link_IsRecordedButNotEntered()
    {
        var fs = SampleTree();
        fs.Dir("Loop", FileAttributes.ReparsePoint).File(@"Loop\inside.txt");
        fs.Dir("OneDrive", FileAttributes.ReparsePoint).File(@"OneDrive\cloud.docx");
        var links = new FakeLinkDetector(fs.P("Loop"));
        var (root, scanner, _) = Setup(fs, links: links);

        var stats = scanner.ScanRoot(root.Id);

        Assert.Equal(1, stats.LinksNotEntered);
        Assert.DoesNotContain(fs.P("Loop"), fs.Calls);
        Assert.Contains(fs.P("OneDrive"), fs.Calls); // reparse point without link target: entered
        Assert.Equal(2, links.Calls); // only folders with the ReparsePoint attribute are asked
        ScanAssert.TreeEquals(_f, root.Id, fs.Expected("Loop"));
    }

    [Fact]
    public void KnownFolderThatBecameALink_LosesItsOldChildren()
    {
        var fs = SampleTree();
        var links = new FakeLinkDetector(fs.P("Music"));
        var (root, scanner, _) = Setup(fs, links: links);
        scanner.ScanRoot(root.Id);
        Assert.Contains(@"Music\song.mp3", ScanAssert.ReadTree(_f, root.Id).Keys);

        fs.Dir("Music", FileAttributes.ReparsePoint, 8);
        scanner.ScanRoot(root.Id);

        ScanAssert.TreeEquals(_f, root.Id, fs.Expected("Music"));
    }

    // ------------------------------------------------------------------ network: one thread, pauses (PLAN.md 7.4)

    [Theory]
    [InlineData(NetworkPoliteness.Normal)]
    [InlineData(NetworkPoliteness.VeryGentle)]
    public void Network_PausesAfterEveryDirectory_AndNeverListsInParallel(NetworkPoliteness politeness)
    {
        var fs = SampleTree();
        // Listing durations per folder: root 100 ms, Docs 400 ms, Sub 2 ms, Deeper 0 ms, Music 3 s.
        var durations = new Dictionary<string, TimeSpan>(StringComparer.OrdinalIgnoreCase)
        {
            [fs.P("")] = TimeSpan.FromMilliseconds(100),
            [fs.P("Docs")] = TimeSpan.FromMilliseconds(400),
            [fs.P(@"Docs\Sub")] = TimeSpan.FromMilliseconds(2),
            [fs.P(@"Docs\Sub\Deeper")] = TimeSpan.Zero,
            [fs.P("Music")] = TimeSpan.FromSeconds(3),
        };
        fs.Duration = p => durations[p];
        var (root, scanner, clock) = Setup(fs, RootKind.Network, politeness: politeness);

        // Each pause must come right after the listing it belongs to.
        var pauseAfter = new List<string>();
        clock.OnPause = _ => pauseAfter.Add(fs.Calls[^1]);
        var stats = scanner.ScanRoot(root.Id);

        Assert.Equal(1, fs.MaxConcurrent);
        Assert.Equal(ScanOutcome.Complete, stats.Outcome);
        var expected = fs.Calls
            .Select(c => (Path: c, Pause: Scanner.PauseFor(RootKind.Network, politeness, durations[c])))
            .Where(x => x.Pause > TimeSpan.Zero)
            .ToList();
        Assert.Equal(expected.Select(x => x.Pause), clock.Pauses);
        Assert.Equal(expected.Select(x => x.Path), pauseAfter);
        Assert.Equal(clock.Pauses.Aggregate(TimeSpan.Zero, (a, b) => a + b), stats.TotalPause);
        Assert.Equal(durations.Values.Aggregate(TimeSpan.Zero, (a, b) => a + b) + stats.TotalPause, stats.Duration);

        var ms = clock.Pauses.Select(p => p.TotalMilliseconds).Order().ToArray();
        if (politeness == NetworkPoliteness.Normal)
        {
            // = listing duration, at most 250 ms (400 ms and 3 s are capped); a 0 ms listing needs no pause.
            Assert.Equal(new double[] { 2, 100, 250, 250 }, ms);
        }
        else
        {
            // = 4 x listing duration, at least 20 ms (2 ms and 0 ms), at most 1 s (400 ms and 3 s).
            Assert.Equal(new double[] { 20, 20, 400, 1000, 1000 }, ms);
        }
    }

    [Theory]
    [InlineData(RootKind.Local)]
    [InlineData(RootKind.Removable)]
    public void LocalAndRemovable_HaveNoPauses(RootKind kind)
    {
        var fs = SampleTree();
        fs.Duration = _ => TimeSpan.FromMilliseconds(100);
        var (root, scanner, clock) = Setup(fs, kind);

        var stats = scanner.ScanRoot(root.Id);

        Assert.Empty(clock.Pauses);
        Assert.Equal(TimeSpan.Zero, stats.TotalPause);
    }

    [Fact]
    public void Network_PauseAlsoAfterFailedListing()
    {
        var fs = SampleTree();
        fs.Fail("Music", new IOException());
        fs.Duration = _ => TimeSpan.FromMilliseconds(10);
        var (root, scanner, clock) = Setup(fs, RootKind.Network);

        scanner.ScanRoot(root.Id);

        Assert.Equal(fs.Calls.Count, clock.Pauses.Count);
    }

    [Fact]
    public void PauseRule_Values()
    {
        static double Ms(RootKind k, NetworkPoliteness p, double listing)
            => Scanner.PauseFor(k, p, TimeSpan.FromMilliseconds(listing)).TotalMilliseconds;

        Assert.Equal(0, Ms(RootKind.Network, NetworkPoliteness.Normal, 0));
        Assert.Equal(120, Ms(RootKind.Network, NetworkPoliteness.Normal, 120));
        Assert.Equal(250, Ms(RootKind.Network, NetworkPoliteness.Normal, 251));
        Assert.Equal(20, Ms(RootKind.Network, NetworkPoliteness.VeryGentle, 0));
        Assert.Equal(20, Ms(RootKind.Network, NetworkPoliteness.VeryGentle, 5));
        Assert.Equal(400, Ms(RootKind.Network, NetworkPoliteness.VeryGentle, 100));
        Assert.Equal(1000, Ms(RootKind.Network, NetworkPoliteness.VeryGentle, 251));
        Assert.Equal(0, Ms(RootKind.Local, NetworkPoliteness.VeryGentle, 500));
        Assert.Equal(0, Ms(RootKind.Removable, NetworkPoliteness.Normal, 500));
    }

    [Fact]
    public async Task SecondCallWhileRunning_IsRejected()
    {
        var fs = SampleTree();
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        fs.OnList = p =>
        {
            if (p == fs.P("Docs"))
            {
                entered.Set();
                release.Wait(TimeSpan.FromSeconds(30));
            }
        };
        var (root, scanner, _) = Setup(fs, RootKind.Network);

        var first = Task.Run(() => scanner.ScanRoot(root.Id));
        Assert.True(entered.Wait(TimeSpan.FromSeconds(30)));
        Assert.True(scanner.IsRunning);
        Assert.Throws<InvalidOperationException>(() => scanner.ScanRoot(root.Id));
        Assert.Throws<InvalidOperationException>(() => scanner.ScanSubtree(RootPath));
        release.Set();

        Assert.Equal(ScanOutcome.Complete, (await first).Outcome);
        Assert.False(scanner.IsRunning);
        Assert.Equal(1, fs.MaxConcurrent);
        ScanAssert.TreeEquals(_f, root.Id, fs.Expected());
    }

    // ------------------------------------------------------------------ cancellation

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(4)]
    public void CancelMidScan_LeavesValidIndex_ThenFullScanCompletes(int cancelAfterListings)
    {
        var fs = SampleTree();
        for (int i = 0; i < 30; i++)
        {
            fs.File($@"Docs\Sub\many{i:D2}.txt", i);
        }

        var (root, scanner, _) = Setup(fs, batchSize: 7);
        using var cts = new CancellationTokenSource();
        int listings = 0;
        fs.OnList = _ =>
        {
            if (++listings == cancelAfterListings)
            {
                cts.Cancel();
            }
        };

        var stats = scanner.ScanRoot(root.Id, cts.Token);

        Assert.Equal(ScanOutcome.Cancelled, stats.Outcome);
        Assert.Equal(cancelAfterListings, stats.DirectoriesListed);
        var state = _f.Store.GetRoot(root.Id)!;
        Assert.Equal(ScanStatus.Aborted, state.LastScanStatus);
        Assert.Null(state.LastScanCompletedUtc);

        // Valid: structure intact (checked by ReadTree), every recorded entry exists with the right data, memory agrees.
        var expected = fs.Expected();
        var partial = ScanAssert.ReadTree(_f, root.Id);
        Assert.NotEmpty(partial);
        foreach (var (path, row) in partial)
        {
            Assert.Equal(expected[path], row.State);
        }

        Assert.Equal(ScanAssert.TotalEntries(_f), _f.Index.Count);

        fs.OnList = null;
        Assert.Equal(ScanOutcome.Complete, scanner.ScanRoot(root.Id).Outcome);
        ScanAssert.TreeEquals(_f, root.Id, expected);
        Assert.Equal(ScanStatus.Complete, _f.Store.GetRoot(root.Id)!.LastScanStatus);
    }

    [Fact]
    public void CancelDuringPause_AbortsAndKeepsIndexValid()
    {
        var fs = SampleTree();
        fs.Duration = _ => TimeSpan.FromMilliseconds(50);
        var (root, scanner, clock) = Setup(fs, RootKind.Network);
        using var cts = new CancellationTokenSource();
        clock.OnPause = i =>
        {
            if (i == 1)
            {
                cts.Cancel();
            }
        };

        var stats = scanner.ScanRoot(root.Id, cts.Token);

        Assert.Equal(ScanOutcome.Cancelled, stats.Outcome);
        Assert.Equal(ScanStatus.Aborted, _f.Store.GetRoot(root.Id)!.LastScanStatus);
        ScanAssert.ReadTree(_f, root.Id);

        clock.OnPause = null;
        scanner.ScanRoot(root.Id);
        ScanAssert.TreeEquals(_f, root.Id, fs.Expected());
    }

    [Fact]
    public void CancelledBeforeStart_ChangesNothing()
    {
        var fs = SampleTree();
        var (root, scanner, _) = Setup(fs);
        string snapshot = ScanAssert.DatabaseSnapshot(_f);

        var stats = scanner.ScanRoot(root.Id, new CancellationToken(canceled: true));

        Assert.Equal(ScanOutcome.Cancelled, stats.Outcome);
        Assert.Empty(fs.Calls);
        Assert.Equal(snapshot, ScanAssert.DatabaseSnapshot(_f));
    }

    // ------------------------------------------------------------------ subtree scan

    [Fact]
    public void SubtreeScan_UpdatesOnlyBelowTheFolder_AndKeepsRootState()
    {
        var fs = SampleTree();
        var (root, scanner, _) = Setup(fs);
        scanner.ScanRoot(root.Id);
        var stateBefore = _f.Store.GetRoot(root.Id)!;

        fs.File(@"Docs\Sub\added.txt");
        fs.Remove(@"Docs\Sub\Deeper");
        fs.File("outside-new.txt");

        var stats = scanner.ScanSubtree(fs.P(@"Docs\Sub"));

        Assert.Equal(ScanOutcome.Complete, stats.Outcome);
        Assert.Equal(1, stats.DirectoriesListed);
        var tree = ScanAssert.ReadTree(_f, root.Id);
        Assert.Contains(@"Docs\Sub\added.txt", tree.Keys);
        Assert.DoesNotContain(@"Docs\Sub\Deeper", tree.Keys);
        Assert.DoesNotContain("outside-new.txt", tree.Keys);
        Assert.Equal(stateBefore, _f.Store.GetRoot(root.Id));

        // The whole location as a subtree, path spelled in another case.
        scanner.ScanSubtree(RootPath.ToUpperInvariant());
        ScanAssert.TreeEquals(_f, root.Id, fs.Expected());
    }

    [Fact]
    public void SubtreeScan_StartFolderUnreachable_ChangesNothing()
    {
        var fs = SampleTree();
        var (root, scanner, _) = Setup(fs);
        scanner.ScanRoot(root.Id);
        string snapshot = ScanAssert.DatabaseSnapshot(_f);

        fs.Remove("Docs");
        var stats = scanner.ScanSubtree(fs.P("Docs"));

        Assert.Equal(ScanOutcome.RootUnreachable, stats.Outcome);
        Assert.Equal(snapshot, ScanAssert.DatabaseSnapshot(_f));
    }

    [Fact]
    public void SubtreeScan_UnknownPathOrFile_Throws()
    {
        var fs = SampleTree();
        var (root, scanner, _) = Setup(fs);
        scanner.ScanRoot(root.Id);

        Assert.Throws<ArgumentException>(() => scanner.ScanSubtree(fs.P("Nope")));
        Assert.Throws<ArgumentException>(() => scanner.ScanSubtree(fs.P("top.txt")));
        Assert.Throws<ArgumentException>(() => scanner.ScanSubtree(@"Q:\elsewhere"));
        Assert.False(scanner.IsRunning);
    }

    [Fact]
    public void SubtreeScan_OfALink_DoesNotEnterIt()
    {
        var fs = SampleTree();
        fs.Dir("Loop", FileAttributes.ReparsePoint).File(@"Loop\x.txt");
        var (root, scanner, _) = Setup(fs, links: new FakeLinkDetector(fs.P("Loop")));
        scanner.ScanRoot(root.Id);
        int calls = fs.Calls.Count;

        var stats = scanner.ScanSubtree(fs.P("Loop"));

        Assert.Equal(0, stats.DirectoriesListed);
        Assert.Equal(calls, fs.Calls.Count);
    }

    // ------------------------------------------------------------------ exclusions and search

    [Fact]
    public void Exclusions_AreNotRecorded_AndExcludedFoldersNotListed()
    {
        var fs = SampleTree();
        fs.Dir("node_modules").File(@"node_modules\pkg.js");
        fs.Dir(@"Docs\.git").File(@"Docs\.git\HEAD");
        fs.File(@"Docs\~$a.docx").File(@"Docs\Thumbs.db").File(@"Docs\x.TMP").File(@"Docs\desktop.ini");
        fs.File(@"Docs\sys.bin", attributes: FileAttributes.System | FileAttributes.Hidden);
        fs.File(@"Docs\hidden.txt", attributes: FileAttributes.Hidden);
        var rules = new ExcludeRules(
            ExcludeRules.DefaultFolderNames, [@"R:\root\Music"], ExcludeRules.DefaultFileNames);
        var (root, scanner, _) = Setup(fs, rules: rules);

        var stats = scanner.ScanRoot(root.Id);

        var tree = ScanAssert.ReadTree(_f, root.Id);
        Assert.Equal(8, stats.Excluded);
        Assert.DoesNotContain(fs.P("node_modules"), fs.Calls);
        Assert.DoesNotContain(fs.P("Music"), fs.Calls);
        Assert.Contains(@"Docs\hidden.txt", tree.Keys);
        Assert.Equal(
            new[] { "Docs", @"Docs\a.txt", @"Docs\b.pdf", @"Docs\hidden.txt", @"Docs\Sub", @"Docs\Sub\deep.txt", @"Docs\Sub\Deeper", @"Docs\Sub\Deeper\deepest.txt", "top.txt" },
            tree.Keys.Order(StringComparer.OrdinalIgnoreCase));

        // Rules that change later remove what is excluded now.
        var all = new Scanner(_f.Store, fs, ExcludeRules.None, new ScannerOptions { LinkDetector = new FakeLinkDetector() });
        all.ScanRoot(root.Id);
        Assert.Contains(@"Music\song.mp3", ScanAssert.ReadTree(_f, root.Id).Keys);
        scanner.ScanRoot(root.Id);
        Assert.DoesNotContain(ScanAssert.ReadTree(_f, root.Id).Keys, k => k.StartsWith("Music", StringComparison.Ordinal));
    }

    [Fact]
    public void AfterScan_SearchFindsTheFiles()
    {
        var fs = SampleTree();
        fs.File(@"Docs\Angebot Müller.pdf");
        var (root, scanner, _) = Setup(fs);
        scanner.ScanRoot(root.Id);

        Assert.Equal(new[] { @"R:\root\Docs\Sub\Deeper\deepest.txt" }, _f.Paths("deepest"));
        Assert.Equal(new[] { @"R:\root\Docs\Angebot Müller.pdf" }, _f.Paths("mueller angebot"));
        Assert.Contains(@"R:\root\Docs\Sub\deep.txt", _f.Paths("sub deep .txt", SearchMode.Files));

        fs.Remove(@"Docs\Sub");
        scanner.ScanRoot(root.Id);
        Assert.Empty(_f.Paths("deepest"));
    }

}
