using FileDetective.Core;
using Microsoft.Data.Sqlite;

namespace FileDetective.Tests;

/// <summary>
/// Stage 14 (PLAN.md 11.5): continuing an interrupted scan, skipping unchanged folders without subfolders, the check
/// mode, the choice between quick and full scan and the queue giving way.
/// </summary>
public class QuickScanTests : IDisposable
{
    private const string RootPath = @"R:\root";

    private readonly TempDir _dir = new();
    private readonly StorageFixture _f;

    public QuickScanTests()
    {
        _f = new StorageFixture(_dir);
    }

    public void Dispose()
    {
        _f.Dispose();
        _dir.Dispose();
    }

    /// <summary>
    /// R:\root
    ///   Docs\ (a.txt, Sub\ (deep.txt, Deeper\ (deepest.txt)))
    ///   Music\ (song.mp3)
    ///   Empty\
    ///   top.txt
    /// Folders without subfolders: Docs\Sub\Deeper, Music, Empty.
    /// </summary>
    private static FakeFileSystem SampleTree()
    {
        var fs = new FakeFileSystem(RootPath);
        fs.Dir("Docs", mtimeOffset: 1)
            .File(@"Docs\a.txt", 10, 2)
            .Dir(@"Docs\Sub", mtimeOffset: 4)
            .File(@"Docs\Sub\deep.txt", 30, 5)
            .Dir(@"Docs\Sub\Deeper", mtimeOffset: 6)
            .File(@"Docs\Sub\Deeper\deepest.txt", 40, 7)
            .Dir("Music", mtimeOffset: 8)
            .File(@"Music\song.mp3", 50, 9)
            .Dir("Empty", mtimeOffset: 11)
            .File("top.txt", 60, 10);
        return fs;
    }

    private RootInfo AddRoot(RootKind kind = RootKind.Network)
        => _f.Store.AddRoot(RootPath, kind == RootKind.Network ? @"\\server\share" : null, kind);

    private Scanner NewScanner(
        FakeFileSystem fs,
        FakeScanClock clock,
        bool quick = false,
        bool shadow = false,
        ExcludeRules? rules = null,
        TimeSpan? checkpointInterval = null,
        Action<string>? shadowMiss = null,
        int batchSize = Scanner.DefaultBatchSize)
    {
        fs.Clock = clock;
        return new Scanner(_f.Store, fs, rules ?? ExcludeRules.None, new ScannerOptions
        {
            Clock = clock,
            LinkDetector = new FakeLinkDetector(),
            QuickScan = quick,
            QuickScanShadow = shadow,
            ShadowMiss = shadowMiss,
            CheckpointInterval = checkpointInterval ?? Scanner.DefaultCheckpointInterval,
            BatchSize = batchSize,
        });
    }

    private long? Verified(string relative)
    {
        int id = _f.Store.FindEntryId(Path.Join(RootPath, relative)) ?? throw new InvalidOperationException(relative);
        using var connection = _f.Db.OpenConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT mtime FROM dir_verified WHERE entry_id = @id";
        cmd.Parameters.AddWithValue("@id", id);
        return cmd.ExecuteScalar() is long ticks ? ticks : null;
    }

    private static long Ticks(int offset) => FakeFileSystem.BaseTime.AddSeconds(offset).Ticks;

    private static string[] Listed(FakeFileSystem fs) => [.. fs.Calls.Select(c => Path.GetRelativePath(RootPath, c))];

    // ------------------------------------------------------------------ verified folder dates

    [Fact]
    public void FullScan_VerifiesFoldersWithoutSubfolders_Only()
    {
        var fs = SampleTree();
        var root = AddRoot();
        var clock = new FakeScanClock();

        NewScanner(fs, clock).ScanRoot(root.Id);

        Assert.Equal(Ticks(6), Verified(@"Docs\Sub\Deeper"));
        Assert.Equal(Ticks(8), Verified("Music"));
        Assert.Equal(Ticks(11), Verified("Empty"));
        Assert.Null(Verified("Docs"));
        Assert.Null(Verified(@"Docs\Sub"));
    }

    [Fact]
    public void FolderDateYoungerThanTheMargin_IsNotVerified()
    {
        var clock = new FakeScanClock();
        int recent = (int)(clock.UtcNow.UtcDateTime - FakeFileSystem.BaseTime).TotalSeconds - 60;
        var fs = SampleTree().Dir("Fresh", mtimeOffset: recent);
        var root = AddRoot();

        NewScanner(fs, clock).ScanRoot(root.Id);

        Assert.Null(Verified("Fresh"));
        Assert.NotNull(Verified("Music"));
    }

    [Fact]
    public void FolderThatGetsASubfolder_LosesItsVerifiedDate()
    {
        var fs = SampleTree();
        var root = AddRoot();
        var clock = new FakeScanClock();
        NewScanner(fs, clock).ScanRoot(root.Id);

        fs.Dir("Music", mtimeOffset: 100).Dir(@"Music\Live", mtimeOffset: 101);
        var stats = NewScanner(fs, clock, quick: true).ScanRoot(root.Id);

        Assert.Equal(ScanOutcome.Complete, stats.Outcome);
        Assert.Contains("Music", Listed(fs));
        Assert.Null(Verified("Music"));
        Assert.Equal(Ticks(101), Verified(@"Music\Live"));
        ScanAssert.TreeEquals(_f, root.Id, fs.Expected());
    }

    // ------------------------------------------------------------------ quick scan

    [Fact]
    public void QuickScan_SkipsUnchangedFoldersWithoutSubfolders_AndListsTheRest()
    {
        var fs = SampleTree();
        var root = AddRoot();
        var clock = new FakeScanClock();
        NewScanner(fs, clock).ScanRoot(root.Id);
        fs.Calls.Clear();
        fs.Duration = _ => TimeSpan.FromMilliseconds(10);
        int pausesBefore = clock.Pauses.Count;

        var stats = NewScanner(fs, clock, quick: true).ScanRoot(root.Id);

        Assert.Equal(ScanOutcome.Complete, stats.Outcome);
        Assert.True(stats.Quick);
        Assert.Equal(3, stats.SkippedUnchanged);
        Assert.Equal(new[] { ".", "Docs", @"Docs\Sub" }.Order(), Listed(fs).Order());
        Assert.Equal(3, clock.Pauses.Count - pausesBefore); // no pause for a folder that was not listed
        ScanAssert.TreeEquals(_f, root.Id, fs.Expected());
        Assert.Contains("3 unchanged folders skipped", stats.Describe(RootPath, ScanTrigger.Scheduled), StringComparison.Ordinal);
    }

    [Fact]
    public void QuickScan_ListsAFolderWhoseDateChanged()
    {
        var fs = SampleTree();
        var root = AddRoot();
        var clock = new FakeScanClock();
        NewScanner(fs, clock).ScanRoot(root.Id);

        fs.File(@"Music\new.mp3", 70, 20).Dir("Music", mtimeOffset: 20);
        fs.Remove(@"Docs\Sub\Deeper\deepest.txt").Dir(@"Docs\Sub\Deeper", mtimeOffset: 21);
        fs.Calls.Clear();
        var stats = NewScanner(fs, clock, quick: true).ScanRoot(root.Id);

        Assert.Equal(1, stats.SkippedUnchanged); // only Empty
        Assert.Contains("Music", Listed(fs));
        Assert.Contains(@"Docs\Sub\Deeper", Listed(fs));
        ScanAssert.TreeEquals(_f, root.Id, fs.Expected());
        Assert.Equal(Ticks(20), Verified("Music"));
    }

    [Fact]
    public void QuickScan_DoesNotSeeANameChangeWithoutNewFolderDate_ThatIsTheKnownLimit()
    {
        var fs = SampleTree();
        var root = AddRoot();
        var clock = new FakeScanClock();
        NewScanner(fs, clock).ScanRoot(root.Id);

        fs.File(@"Music\hidden.mp3", 70, 20); // folder date stays: a server that does not update it
        NewScanner(fs, clock, quick: true).ScanRoot(root.Id);

        Assert.Null(_f.Store.FindEntryId(Path.Join(RootPath, @"Music\hidden.mp3")));

        // A full scan finds it.
        NewScanner(fs, clock).ScanRoot(root.Id);
        Assert.NotNull(_f.Store.FindEntryId(Path.Join(RootPath, @"Music\hidden.mp3")));
    }

    [Fact]
    public void QuickScan_NeverSkipsAFolderNewInThisScan_OrAnUnknownOne()
    {
        var fs = SampleTree();
        var root = AddRoot();
        var clock = new FakeScanClock();

        var first = NewScanner(fs, clock, quick: true).ScanRoot(root.Id);

        Assert.Equal(0, first.SkippedUnchanged);
        ScanAssert.TreeEquals(_f, root.Id, fs.Expected());
    }

    [Fact]
    public void FullScan_ListsEverything_EvenWithVerifiedDates()
    {
        var fs = SampleTree();
        var root = AddRoot();
        var clock = new FakeScanClock();
        NewScanner(fs, clock).ScanRoot(root.Id);
        fs.Calls.Clear();

        var stats = NewScanner(fs, clock).ScanRoot(root.Id);

        Assert.Equal(0, stats.SkippedUnchanged);
        Assert.Equal(6, fs.Calls.Count);
    }

    [Fact]
    public void QuickScan_FailedListing_KeepsTheOldVerifiedDate_AndDeletesNothing()
    {
        var fs = SampleTree();
        var root = AddRoot();
        var clock = new FakeScanClock();
        NewScanner(fs, clock).ScanRoot(root.Id);

        fs.Dir("Music", mtimeOffset: 30);
        fs.Fail("Music", new IOException("network name no longer available"));
        var stats = NewScanner(fs, clock, quick: true).ScanRoot(root.Id);

        Assert.Equal(ScanOutcome.CompleteWithErrors, stats.Outcome);
        Assert.Equal(Ticks(8), Verified("Music"));
        Assert.NotNull(_f.Store.FindEntryId(Path.Join(RootPath, @"Music\song.mp3")));

        // Next time the folder is listed again (its date differs from the verified one).
        fs.ClearFailures();
        fs.Calls.Clear();
        NewScanner(fs, clock, quick: true).ScanRoot(root.Id);
        Assert.Contains("Music", Listed(fs));
        Assert.Equal(Ticks(30), Verified("Music"));
    }

    // ------------------------------------------------------------------ check mode

    [Fact]
    public void CheckMode_ListsEverything_AndReportsFoldersThatWouldHaveBeenSkippedWrongly()
    {
        var fs = SampleTree();
        var root = AddRoot();
        var clock = new FakeScanClock();
        NewScanner(fs, clock).ScanRoot(root.Id);

        fs.File(@"Music\hidden.mp3", 70, 20); // name change without a new folder date
        fs.File(@"Docs\Sub\Deeper\deepest.txt", 99, 30); // size change only: expected, not a miss
        fs.Calls.Clear();
        var misses = new List<string>();
        var stats = NewScanner(fs, clock, quick: true, shadow: true, shadowMiss: misses.Add).ScanRoot(root.Id);

        Assert.Equal(6, fs.Calls.Count);
        Assert.Equal(3, stats.SkippedUnchanged);
        Assert.Equal(1, stats.ShadowMisses);
        Assert.Equal([Path.Join(RootPath, "Music")], misses);
        ScanAssert.TreeEquals(_f, root.Id, fs.Expected());
        Assert.Contains("check mode: 3 folders would be skipped, 1 of them changed", stats.Describe(RootPath, ScanTrigger.Scheduled), StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ continuing

    private static FakeFileSystem WideTree(int folders = 8)
    {
        var fs = new FakeFileSystem(RootPath);
        for (int d = 0; d < folders; d++)
        {
            fs.Dir($"D{d}", mtimeOffset: d);
            for (int s = 0; s < 3; s++)
            {
                fs.Dir($@"D{d}\S{s}", mtimeOffset: 100 + s).File($@"D{d}\S{s}\f.txt", 10 + s, 200);
            }
        }

        return fs;
    }

    [Fact]
    public void CancelledScan_SavesItsStand_AndTheNextScanContinuesThere()
    {
        var fs = WideTree();
        var root = AddRoot();
        var clock = new FakeScanClock();
        using var cts = new CancellationTokenSource();
        fs.OnList = _ =>
        {
            if (fs.Calls.Count == 10)
            {
                cts.Cancel();
            }
        };

        var first = NewScanner(fs, clock, batchSize: 3).ScanRoot(root.Id, cts.Token);

        Assert.Equal(ScanOutcome.Cancelled, first.Outcome);
        var info = _f.Store.GetRootScanInfo(root.Id);
        Assert.NotNull(info.Resume);
        Assert.True(info.Resume!.OpenFolders > 0);
        var listedFirst = fs.Calls.ToList();

        fs.OnList = null;
        fs.Calls.Clear();
        var second = NewScanner(fs, clock, batchSize: 3).ScanRoot(root.Id);

        Assert.Equal(ScanOutcome.Complete, second.Outcome);
        Assert.Equal(info.Resume.OpenFolders, second.ResumedFolders);
        Assert.Contains("Resumed with", second.Describe(RootPath, ScanTrigger.Scheduled), StringComparison.Ordinal);
        ScanAssert.TreeEquals(_f, root.Id, fs.Expected());
        Assert.Equal(ScanAssert.TotalEntries(_f), _f.Index.Count);

        // Only the location itself (reachability) is listed twice; every other folder exactly once over both runs.
        var all = listedFirst.Concat(fs.Calls).Where(c => c != RootPath).ToList();
        Assert.Equal(all.Count, all.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Equal(8 * 4, all.Count);
        Assert.Null(_f.Store.GetRootScanInfo(root.Id).Resume);
        Assert.Equal(ScanStatus.Complete, _f.Store.GetRoot(root.Id)!.LastScanStatus);
    }

    [Fact]
    public void CancelDuringThePause_KeepsTheFolderInTheStand()
    {
        var fs = WideTree(3);
        var root = AddRoot(RootKind.Network);
        var clock = new FakeScanClock();
        fs.Duration = _ => TimeSpan.FromMilliseconds(10);
        using var cts = new CancellationTokenSource();
        clock.OnPause = n =>
        {
            if (n == 4)
            {
                cts.Cancel();
            }
        };

        var first = NewScanner(fs, clock).ScanRoot(root.Id, cts.Token);
        Assert.Equal(ScanOutcome.Cancelled, first.Outcome);

        clock.OnPause = null;
        var second = NewScanner(fs, clock).ScanRoot(root.Id);
        Assert.Equal(ScanOutcome.Complete, second.Outcome);
        ScanAssert.TreeEquals(_f, root.Id, fs.Expected());
    }

    [Fact]
    public void UnexpectedError_AfterAPeriodicStand_TheNextScanContinuesFromIt_WithoutLosingChanges()
    {
        var fs = WideTree();
        var root = AddRoot();
        var clock = new FakeScanClock();
        NewScanner(fs, clock).ScanRoot(root.Id);

        // Changes everywhere, then a "crash" (an error that is no listing failure) in the middle of the next scan.
        for (int d = 0; d < 8; d++)
        {
            fs.File($@"D{d}\S1\added.txt", 5, 300).Dir($@"D{d}\S1", mtimeOffset: 400);
            fs.Remove($@"D{d}\S2\f.txt").Dir($@"D{d}\S2", mtimeOffset: 401);
        }

        fs.OnList = _ =>
        {
            if (fs.Calls.Count == 20)
            {
                throw new InvalidOperationException("simulated crash");
            }
        };
        fs.Calls.Clear();
        Assert.Throws<InvalidOperationException>(() => NewScanner(fs, clock, checkpointInterval: TimeSpan.Zero, batchSize: 2).ScanRoot(root.Id));
        Assert.Equal(ScanStatus.Aborted, _f.Store.GetRoot(root.Id)!.LastScanStatus);
        Assert.NotNull(_f.Store.GetRootScanInfo(root.Id).Resume);

        fs.OnList = null;
        var second = NewScanner(fs, clock, batchSize: 2).ScanRoot(root.Id);

        Assert.Equal(ScanOutcome.Complete, second.Outcome);
        Assert.True(second.ResumedFolders > 0);
        ScanAssert.TreeEquals(_f, root.Id, fs.Expected());
        Assert.Equal(ScanAssert.TotalEntries(_f), _f.Index.Count);
    }

    [Fact]
    public void StandUnderOtherExclusionRules_IsDropped_AndTheScanStartsFromTheLocation()
    {
        var fs = WideTree();
        var root = AddRoot();
        var clock = new FakeScanClock();
        using var cts = new CancellationTokenSource();
        fs.OnList = _ =>
        {
            if (fs.Calls.Count == 5)
            {
                cts.Cancel();
            }
        };
        NewScanner(fs, clock).ScanRoot(root.Id, cts.Token);
        fs.OnList = null;

        var other = new ExcludeRules(["S0"], [], [], excludeSystem: false);
        var second = NewScanner(fs, clock, rules: other).ScanRoot(root.Id);

        Assert.Equal(0, second.ResumedFolders);
        Assert.Equal(ScanOutcome.Complete, second.Outcome);
        Assert.Null(_f.Store.FindEntryId(Path.Join(RootPath, @"D0\S0")));
        Assert.Null(_f.Store.GetRootScanInfo(root.Id).Resume);
    }

    [Fact]
    public void StandOlderThanSevenDays_IsDropped()
    {
        var fs = WideTree();
        var root = AddRoot();
        var clock = new FakeScanClock();
        using var cts = new CancellationTokenSource();
        fs.OnList = _ =>
        {
            if (fs.Calls.Count == 5)
            {
                cts.Cancel();
            }
        };
        NewScanner(fs, clock).ScanRoot(root.Id, cts.Token);
        fs.OnList = null;

        clock.Advance(TimeSpan.FromDays(8));
        var second = NewScanner(fs, clock).ScanRoot(root.Id);

        Assert.Equal(0, second.ResumedFolders);
        ScanAssert.TreeEquals(_f, root.Id, fs.Expected());
    }

    [Fact]
    public void UnreachableLocation_KeepsTheStand()
    {
        var fs = WideTree();
        var root = AddRoot();
        var clock = new FakeScanClock();
        using var cts = new CancellationTokenSource();
        fs.OnList = _ =>
        {
            if (fs.Calls.Count == 5)
            {
                cts.Cancel();
            }
        };
        NewScanner(fs, clock).ScanRoot(root.Id, cts.Token);
        fs.OnList = null;
        int open = _f.Store.GetRootScanInfo(root.Id).Resume!.OpenFolders;

        fs.Fail("", new IOException("unreachable"));
        Assert.Equal(ScanOutcome.RootUnreachable, NewScanner(fs, clock).ScanRoot(root.Id).Outcome);
        Assert.Equal(open, _f.Store.GetRootScanInfo(root.Id).Resume!.OpenFolders);

        fs.ClearFailures();
        Assert.Equal(open, NewScanner(fs, clock).ScanRoot(root.Id).ResumedFolders);
    }

    [Fact]
    public void FolderOfTheStandThatIsGone_IsLeftOut()
    {
        var fs = WideTree();
        var root = AddRoot();
        var clock = new FakeScanClock();
        NewScanner(fs, clock).ScanRoot(root.Id);
        int d7 = _f.Store.FindEntryId(Path.Join(RootPath, "D7"))!.Value;
        int s0 = _f.Store.FindEntryId(Path.Join(RootPath, @"D7\S0"))!.Value;
        _f.Store.SaveResume(
            root.Id, 1, clock.UtcNow.ToUnixTimeSeconds(), quick: false, ExcludeRules.None.Fingerprint,
            [new ResumeFolder(s0, Path.Join(RootPath, @"D7\S0")), new ResumeFolder(d7, Path.Join(RootPath, "D7"))]);
        _f.Store.DeleteSubtree(s0);

        Assert.Equal([new ResumeFolder(d7, Path.Join(RootPath, "D7"))], _f.Store.LoadResumeFolders(root.Id));
    }

    // ------------------------------------------------------------------ full-scan mark and cleanup

    [Fact]
    public void CompleteFullScan_IsMarked_QuickScanIsNot()
    {
        var fs = SampleTree();
        var root = AddRoot();
        var clock = new FakeScanClock();

        NewScanner(fs, clock).ScanRoot(root.Id);
        var info = _f.Store.GetRootScanInfo(root.Id);
        Assert.Equal(clock.UtcNow.ToUnixTimeSeconds(), info.LastFullScanUtc);
        Assert.Equal(ExcludeRules.None.Fingerprint, info.FullScanRules);

        clock.Advance(TimeSpan.FromDays(1));
        NewScanner(fs, clock, quick: true).ScanRoot(root.Id);
        Assert.Equal(info.LastFullScanUtc, _f.Store.GetRootScanInfo(root.Id).LastFullScanUtc);
    }

    [Fact]
    public void FullScanContinuingAQuickStand_IsNotMarkedFull()
    {
        var fs = WideTree();
        var root = AddRoot();
        var clock = new FakeScanClock();
        NewScanner(fs, clock).ScanRoot(root.Id);
        long full = _f.Store.GetRootScanInfo(root.Id).LastFullScanUtc!.Value;
        clock.Advance(TimeSpan.FromDays(1));

        using var cts = new CancellationTokenSource();
        fs.OnList = _ =>
        {
            if (fs.Calls.Count == 5)
            {
                cts.Cancel();
            }
        };
        fs.Calls.Clear();
        NewScanner(fs, clock, quick: true).ScanRoot(root.Id, cts.Token);
        fs.OnList = null;
        Assert.True(_f.Store.GetRootScanInfo(root.Id).Resume!.Quick);

        NewScanner(fs, clock).ScanRoot(root.Id);
        Assert.Equal(full, _f.Store.GetRootScanInfo(root.Id).LastFullScanUtc);
    }

    [Fact]
    public void RemoveRoot_AndRebuild_DropStandsAndVerifiedDates_ButKeepTheSwitch()
    {
        var fs = WideTree();
        var root = AddRoot();
        var clock = new FakeScanClock();
        NewScanner(fs, clock).ScanRoot(root.Id);
        _f.Store.SetQuickScanEnabled(root.Id, false);
        _f.Store.SaveResume(root.Id, 1, 2, quick: true, "x", [new ResumeFolder(root.RootEntryId, RootPath)]);

        _f.Store.ResetAllEntries();
        var info = _f.Store.GetRootScanInfo(root.Id);
        Assert.Null(info.Resume);
        Assert.Null(info.LastFullScanUtc);
        Assert.False(info.QuickEnabled);
        Assert.Equal(0L, Count("SELECT COUNT(*) FROM dir_verified"));

        _f.Store.SaveResume(root.Id, 1, 2, quick: true, "x", []);
        _f.Store.RemoveRoot(root.Id);
        Assert.Equal(0L, Count("SELECT COUNT(*) FROM root_scan"));
        Assert.Equal(0L, Count("SELECT COUNT(*) FROM scan_resume"));
    }

    [Fact]
    public void DeletedFolder_LosesItsVerifiedDate()
    {
        var fs = SampleTree();
        var root = AddRoot();
        var clock = new FakeScanClock();
        NewScanner(fs, clock).ScanRoot(root.Id);
        Assert.Equal(3L, Count("SELECT COUNT(*) FROM dir_verified"));

        fs.Remove("Music");
        NewScanner(fs, clock).ScanRoot(root.Id);

        Assert.Equal(2L, Count("SELECT COUNT(*) FROM dir_verified"));
    }

    [Fact]
    public void ExistingDatabase_GetsTheNewTables_AndAMissingTriggerDropsTheVerifiedDates()
    {
        var fs = SampleTree();
        var root = AddRoot();
        NewScanner(fs, new FakeScanClock()).ScanRoot(root.Id);
        Assert.Equal(3L, Count("SELECT COUNT(*) FROM dir_verified"));
        Execute($"DROP TRIGGER {IndexDatabase.VerifiedTrigger}");

        _f.Restart();

        Assert.Equal(0L, Count("SELECT COUNT(*) FROM dir_verified"));
        Assert.Equal(1L, Count($"SELECT COUNT(*) FROM sqlite_master WHERE name = '{IndexDatabase.VerifiedTrigger}'"));
    }

    private long Count(string sql)
    {
        using var connection = _f.Db.OpenConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToInt64(cmd.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private void Execute(string sql)
    {
        using var connection = _f.Db.OpenConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    // ------------------------------------------------------------------ quick or full

    private static readonly DateTimeOffset Now = new(2026, 11, 20, 12, 0, 0, TimeSpan.Zero);

    private static RootInfo Root(RootKind kind) => new(1, RootPath, null, kind, true, null, null, ScanStatus.Complete, 0, 0, 1);

    private static RootScanInfo Info(bool? quick = null, double fullDaysAgo = 1, string rules = "R")
        => new(1, quick, Now.AddDays(-fullDaysAgo).ToUnixTimeSeconds(), rules, null);

    [Theory]
    [InlineData(ScanTrigger.Scheduled, true)]
    [InlineData(ScanTrigger.Continue, true)]
    [InlineData(ScanTrigger.Overflow, true)]
    [InlineData(ScanTrigger.Startup, true)]
    [InlineData(ScanTrigger.Manual, false)]
    [InlineData(ScanTrigger.Added, false)]
    public void ShouldQuickScan_OnlyForAutomaticScans(ScanTrigger trigger, bool expected)
        => Assert.Equal(expected, RefreshScheduler.ShouldQuickScan(Root(RootKind.Network), Info(), trigger, "R", Now, TimeSpan.FromDays(7)));

    [Fact]
    public void ShouldQuickScan_Rules()
    {
        var week = TimeSpan.FromDays(7);
        var share = Root(RootKind.Network);
        Assert.True(RefreshScheduler.ShouldQuickScan(share, Info(), ScanTrigger.Scheduled, "R", Now, week));
        Assert.False(RefreshScheduler.ShouldQuickScan(share, Info(fullDaysAgo: 7.5), ScanTrigger.Scheduled, "R", Now, week));
        Assert.False(RefreshScheduler.ShouldQuickScan(share, Info(rules: "other"), ScanTrigger.Scheduled, "R", Now, week));
        Assert.False(RefreshScheduler.ShouldQuickScan(share, Info(quick: false), ScanTrigger.Scheduled, "R", Now, week));
        Assert.False(RefreshScheduler.ShouldQuickScan(share, new RootScanInfo(1, null, null, null, null), ScanTrigger.Scheduled, "R", Now, week));

        // Default by kind: on for shares, off for local and removable locations, unless switched on.
        Assert.False(RefreshScheduler.ShouldQuickScan(Root(RootKind.Local), Info(), ScanTrigger.Startup, "R", Now, week));
        Assert.False(RefreshScheduler.ShouldQuickScan(Root(RootKind.Removable), Info(), ScanTrigger.Scheduled, "R", Now, week));
        Assert.True(RefreshScheduler.ShouldQuickScan(Root(RootKind.Local), Info(quick: true), ScanTrigger.Startup, "R", Now, week));
    }

    [Fact]
    public void ContinueTrigger_IsNoAutomaticAttempt()
        => Assert.False(RefreshScheduler.CountsAsAttempt(new ScanRequest(1, RootKind.Network, ScanTrigger.Continue)));

    [Fact]
    public void ExclusionFingerprint_ChangesWithTheRules_NotWithOrderOrCase()
    {
        var a = new ExcludeRules(["bin", "obj"], [@"C:\Temp"], ["*.tmp"]);
        var b = new ExcludeRules(["OBJ", "bin"], [@"c:\temp"], ["*.TMP"]);
        var c = new ExcludeRules(["bin"], [@"C:\Temp"], ["*.tmp"]);
        var d = new ExcludeRules(["bin", "obj"], [@"C:\Temp"], ["*.tmp"], excludeSystem: false);
        var e = new ExcludeRules(["bin", "obj"], [@"C:\Temp"], ["*.tmp"], includedPaths: [@"C:\Temp\Keep"]);

        Assert.Equal(a.Fingerprint, b.Fingerprint);
        Assert.NotEqual(a.Fingerprint, c.Fingerprint);
        Assert.NotEqual(a.Fingerprint, d.Fingerprint);
        Assert.NotEqual(a.Fingerprint, e.Fingerprint);
    }

    // ------------------------------------------------------------------ queue gives way

    [Fact]
    public void Queue_AutomaticShareScanGivesWayToLocalAndManual_AndContinuesAfterwards()
    {
        var started = new List<ScanRequest>();
        var cancelled = new List<byte>();
        using var shareRunning = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var queue = new ScanQueue((request, token) =>
        {
            lock (started)
            {
                started.Add(request);
            }

            if (request.RootId == 2 && request.Trigger == ScanTrigger.Scheduled)
            {
                shareRunning.Set();
                try
                {
                    release.Wait(token);
                }
                catch (OperationCanceledException)
                {
                    lock (started)
                    {
                        cancelled.Add(request.RootId);
                    }
                }
            }
        });

        queue.Enqueue(new ScanRequest(2, RootKind.Network, ScanTrigger.Scheduled));
        Assert.True(shareRunning.Wait(TimeSpan.FromSeconds(10)));
        queue.Enqueue(new ScanRequest(1, RootKind.Local, ScanTrigger.Startup));
        Assert.True(queue.WaitIdle(TimeSpan.FromSeconds(10)));

        Assert.Equal([(byte)2], cancelled);
        Assert.Equal(
            [(2, ScanTrigger.Scheduled), (1, ScanTrigger.Startup), (2, ScanTrigger.Continue)],
            started.Select(s => ((int)s.RootId, s.Trigger)).ToArray());
    }

    [Fact]
    public void Queue_GivingWay_Rules()
    {
        var scheduledShare = new ScanRequest(2, RootKind.Network, ScanTrigger.Scheduled);
        Assert.True(ScanQueue.Preempts(new ScanRequest(1, RootKind.Local, ScanTrigger.Startup), scheduledShare));
        Assert.True(ScanQueue.Preempts(new ScanRequest(3, RootKind.Network, ScanTrigger.Manual), scheduledShare));
        Assert.True(ScanQueue.Preempts(new ScanRequest(1, RootKind.Local, ScanTrigger.Overflow), scheduledShare with { Trigger = ScanTrigger.Continue }));
        Assert.False(ScanQueue.Preempts(new ScanRequest(3, RootKind.Network, ScanTrigger.Scheduled), scheduledShare));
        Assert.False(ScanQueue.Preempts(new ScanRequest(1, RootKind.Local, ScanTrigger.Startup), scheduledShare with { Trigger = ScanTrigger.Manual }));
        Assert.False(ScanQueue.Preempts(new ScanRequest(1, RootKind.Local, ScanTrigger.Startup), new ScanRequest(4, RootKind.Local, ScanTrigger.Startup)));
        Assert.False(ScanQueue.Preempts(new ScanRequest(2, RootKind.Network, ScanTrigger.Manual), scheduledShare));
    }

    [Fact]
    public void Queue_CancellingTheShareThatGaveWay_DoesNotQueueItAgain()
    {
        using var running = new ManualResetEventSlim();
        var started = new List<ScanRequest>();
        using var queue = new ScanQueue((request, token) =>
        {
            lock (started)
            {
                started.Add(request);
            }

            if (request.RootId == 2)
            {
                running.Set();
                token.WaitHandle.WaitOne(TimeSpan.FromSeconds(10));
                Thread.Sleep(50); // the cancel of the location arrives while this scan is still ending
            }
        });

        queue.Enqueue(new ScanRequest(2, RootKind.Network, ScanTrigger.Scheduled));
        Assert.True(running.Wait(TimeSpan.FromSeconds(10)));
        queue.Enqueue(new ScanRequest(1, RootKind.Local, ScanTrigger.Startup));
        queue.Cancel(2);
        Assert.True(queue.WaitIdle(TimeSpan.FromSeconds(10)));

        Assert.DoesNotContain(started, s => s.Trigger == ScanTrigger.Continue);
    }
}
