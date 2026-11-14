using FileDetective.Core;

namespace FileDetective.Tests;

/// <summary>Stage 9 acceptance (PLAN.md 11.1): the storage analysis with fakes for file system, drives, clock and Recycle Bin.</summary>
public class StorageAnalysisTests : IDisposable
{
    private const string Drive = @"R:\";
    private const string Share = @"N:\";
    private static readonly FileAttributes Cloud = SpaceCollector.RecallOnDataAccess | FileAttributes.Archive;

    private readonly TempDir _dir = new();
    private readonly StorageFixture _f;

    public StorageAnalysisTests()
    {
        _f = new StorageFixture(_dir);
    }

    public void Dispose()
    {
        _f.Dispose();
        _dir.Dispose();
    }

    private sealed class FakeDriveSource : IDriveSource
    {
        public IReadOnlyList<DriveSnapshot> GetDrives()
            => [new DriveSnapshot(Drive, DriveType.Fixed), new DriveSnapshot(Share, DriveType.Network), new DriveSnapshot(@"U:\", DriveType.Removable)];

        public string? GetNetworkPath(string driveLetter) => driveLetter.StartsWith('N') ? @"\\server\share" : null;

        public string? GetLabel(string driveRoot) => null;
    }

    private sealed class FakeSpace(long used = 0) : IDriveSpaceSource
    {
        public const long Total = 1_000_000;

        public List<string> Asked { get; } = [];

        public DriveSpace? GetSpace(string driveRoot)
        {
            Asked.Add(driveRoot);
            return new DriveSpace(Total, Total - used);
        }
    }

    private sealed class FakeBin : IRecycleBin
    {
        public List<string> Files { get; } = [];

        public List<string> Directories { get; } = [];

        public void MoveFile(string path) => Files.Add(path);

        public void MoveDirectory(string path) => Directories.Add(path);
    }

    private StorageAnalysis Analysis(FakeFileSystem fs, FakeSpace? space = null, FakeLinkDetector? links = null)
        => new(() => _f.Store, fs, links ?? new FakeLinkDetector(), new DriveDiscovery(new FakeDriveSource()), space ?? new FakeSpace());

    /// <summary>
    /// R:\ Docs\ (a.txt 100, b.txt 200, Sub\ (c.txt 400, Deep\ (d.txt 800))), Music\ (e.mp3 1600), top.txt 3200
    /// </summary>
    private static FakeFileSystem Sample()
    {
        var fs = new FakeFileSystem(Drive);
        fs.Dir("Docs", mtimeOffset: 1)
            .File(@"Docs\a.txt", 100, 2)
            .File(@"Docs\b.txt", 200, 3)
            .Dir(@"Docs\Sub", mtimeOffset: 4)
            .File(@"Docs\Sub\c.txt", 400, 5)
            .Dir(@"Docs\Sub\Deep", mtimeOffset: 6)
            .File(@"Docs\Sub\Deep\d.txt", 800, 7)
            .Dir("Music", mtimeOffset: 8)
            .File(@"Music\e.mp3", 1600, 9)
            .File("top.txt", 3200, 10);
        return fs;
    }

    private static SpaceFolder Child(SpaceFolder parent, string name) => parent.Children.Single(c => c.Name == name);

    [Fact]
    public void FolderSizes_AddUpOverSubfolders_AndAreSortedBySize()
    {
        var fs = Sample();

        var snap = Analysis(fs).ScanLocal(Drive);

        Assert.Equal(ScanOutcome.Complete, snap.Outcome);
        Assert.Equal(SpaceSource.LiveScan, snap.Source);
        Assert.Equal(100 + 200 + 400 + 800 + 1600 + 3200, snap.Root.Size);
        Assert.Equal(6, snap.Root.FileCount);
        Assert.Equal(4, snap.Root.FolderCount);

        var docs = Child(snap.Root, "Docs");
        Assert.Equal(1500, docs.Size);
        Assert.Equal(4, docs.FileCount);
        Assert.Equal(2, docs.FolderCount);
        Assert.Equal(1200, Child(docs, "Sub").Size);
        Assert.Equal(800, Child(Child(docs, "Sub"), "Deep").Size);
        Assert.Equal(FakeFileSystem.BaseTime.AddSeconds(1), docs.LastWriteUtc);

        Assert.Equal(["Music", "Docs"], snap.Root.Children.Select(c => c.Name)); // 1600 before 1500
        Assert.Equal(@"R:\Docs\Sub\Deep", Child(Child(docs, "Sub"), "Deep").FullPath);
        Assert.Equal(0, snap.FailedDirectories);
    }

    [Fact]
    public void CloudFiles_AreFlagged_DoNotCount_AndAreNeverOpened()
    {
        var fs = Sample();
        fs.File(@"Docs\cloud.bin", 5_000_000, 11, Cloud)
            .File(@"Docs\offline.bin", 7_000_000, 12, FileAttributes.Offline)
            .File(@"Music\cloud2.bin", 9_000_000, 13, Cloud);
        var space = new FakeSpace();

        var snap = Analysis(fs, space).ScanLocal(Drive);

        Assert.Equal(6300, snap.Root.Size); // unchanged by the cloud files
        Assert.Equal(9, snap.Root.FileCount);
        Assert.Equal(3, snap.Root.CloudFileCount);
        Assert.Equal(21_000_000, snap.Root.CloudSize);
        Assert.Equal(2, Child(snap.Root, "Docs").CloudFileCount);
        Assert.Equal(1, Child(snap.Root, "Music").CloudFileCount);
        Assert.DoesNotContain(snap.TopFiles, f => f.Name.Contains("cloud", StringComparison.Ordinal) || f.Name == "offline.bin");

        // The only access to the file system are listings of folders; no file path was ever requested.
        Assert.All(fs.Calls, c => Assert.DoesNotContain(".bin", c, StringComparison.Ordinal));
        Assert.Equal(
            [Drive, @"R:\Docs", @"R:\Docs\Sub", @"R:\Docs\Sub\Deep", @"R:\Music"],
            fs.Calls.OrderBy(c => c, StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public void UnreadableFolder_DoesNotAbort_AndShowsUpAsUnreadableBytes()
    {
        var fs = Sample();
        fs.Dir("Locked").File(@"Locked\secret.dat", 4000);
        fs.Fail("Locked", new UnauthorizedAccessException("denied"));
        // The drive reports 6300 bytes of readable files plus 4000 in the folder that cannot be read plus 250 of system files.
        var space = new FakeSpace(used: 6300 + 4000 + 250);

        var snap = Analysis(fs, space).ScanLocal(Drive);

        Assert.Equal(ScanOutcome.CompleteWithErrors, snap.Outcome);
        Assert.Equal(1, snap.FailedDirectories);
        Assert.True(Child(snap.Root, "Locked").IsUnreadable);
        Assert.Equal(6300, snap.ReadBytes);
        Assert.Equal(4250, snap.UnreadableBytes);
        Assert.Equal(1500, Child(snap.Root, "Docs").Size); // the rest was read normally
    }

    [Fact]
    public void UnreadableBytes_AreNeverNegative()
    {
        var snap = Analysis(Sample(), new FakeSpace(used: 10)).ScanLocal(Drive); // logical sizes exceed the "used" space (e.g. compression)

        Assert.Equal(0, snap.UnreadableBytes);
    }

    [Fact]
    public void LargestFiles_AreSortedAndLimitedTo1000()
    {
        var fs = new FakeFileSystem(Drive);
        for (int d = 0; d < 4; d++)
        {
            fs.Dir($"d{d}");
            for (int i = 0; i < 300; i++)
            {
                // sizes 1..1200, each once
                fs.File($@"d{d}\f{i}.dat", (d * 300) + i + 1);
            }
        }

        var snap = Analysis(fs).ScanLocal(Drive);

        Assert.Equal(1000, snap.TopFiles.Count);
        Assert.Equal(SpaceCollector.TopFilesLimit, snap.TopFiles.Count);
        Assert.Equal(Enumerable.Range(201, 1000).Reverse().Select(i => (long)i), snap.TopFiles.Select(f => f.Size));
        Assert.Equal(1200, snap.TopFiles[0].Size);
        Assert.Equal(@"R:\d3\f299.dat", snap.TopFiles[0].FullPath);
        Assert.Equal(1_200L * 1_201 / 2, snap.Root.Size); // all files count, also those that are not in the list
    }

    [Fact]
    public void LargestFiles_WithEqualSizes_AreOrderedByPath()
    {
        var fs = new FakeFileSystem(Drive);
        fs.File("b.txt", 5).File("a.txt", 5).File("c.txt", 9);

        var snap = Analysis(fs).ScanLocal(Drive);

        Assert.Equal(["c.txt", "a.txt", "b.txt"], snap.TopFiles.Select(f => f.Name));
    }

    [Fact]
    public void Share_ComesFromTheDirectory_WithoutTouchingTheLister()
    {
        var fs = new FakeFileSystem(@"\\server\share");
        fs.Dir("Projects", mtimeOffset: 1).File(@"Projects\plan.doc", 700, 2).Dir(@"Projects\Old")
            .File(@"Projects\Old\x.doc", 300, 3).File("readme.txt", 50, 4)
            .File("cloud.bin", 1_000_000, 5, Cloud);
        var root = _f.Store.AddRoot(@"\\server\share", null, RootKind.Network);
        var clock = new FakeScanClock();
        new Scanner(_f.Store, fs, ExcludeRules.None, new ScannerOptions { Clock = clock, LinkDetector = new FakeLinkDetector() })
            .ScanRoot(root.Id);
        fs.Calls.Clear();
        var space = new FakeSpace();

        var snap = Analysis(fs, space).ReadShare(root.Id);

        Assert.Empty(fs.Calls);        // not a single listing, so no access to the server
        Assert.Empty(space.Asked);     // and no question about the space of the share
        Assert.Equal(SpaceSource.Directory, snap.Source);
        Assert.Equal(RootKind.Network, snap.Kind);
        Assert.Equal(1050, snap.Root.Size);
        Assert.Equal(4, snap.Root.FileCount);
        Assert.Equal(1, snap.Root.CloudFileCount);
        Assert.Equal(1000, Child(snap.Root, "Projects").Size);
        Assert.Equal(["plan.doc", "x.doc", "readme.txt"], snap.TopFiles.Select(f => f.Name));
        Assert.Equal(@"\\server\share\Projects\Old\x.doc", snap.TopFiles[1].FullPath);
        Assert.Equal(ScanStatus.Complete, snap.DirectoryStatus);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(_f.Store.GetRoot(root.Id)!.LastScanCompletedUtc!.Value), snap.AsOf);
        Assert.Null(snap.UnreadableBytes);
        Assert.False(snap.CanMoveToRecycleBin);
    }

    [Fact]
    public void Share_NeverScanned_HasNoDateAndNoNumbers()
    {
        var root = _f.Store.AddRoot(@"\\server\empty", null, RootKind.Network);

        var snap = Analysis(new FakeFileSystem(Drive)).ReadShare(root.Id);

        Assert.Null(snap.AsOf);
        Assert.Equal(ScanStatus.Never, snap.DirectoryStatus);
        Assert.Equal(0, snap.Root.Size);
        Assert.Equal(@"\\server\empty", snap.Root.FullPath);
    }

    [Fact]
    public void Share_ChildWithHigherNumberThanItsParent_IsCounted()
    {
        var root = _f.Store.AddRoot(@"\\server\odd", null, RootKind.Network);
        int fileId = _f.Store.AllocateId();    // lower number than its folder
        int folderId = _f.Store.AllocateId();
        var batch = new ChangeBatch();
        batch.Inserts.Add(new EntryInsert(fileId, folderId, "late.bin", false, 321, 1_700_000_000, FileAttributes.Normal));
        batch.Inserts.Add(new EntryInsert(folderId, root.RootEntryId, "Folder", true, 0, 1_700_000_000, FileAttributes.Directory));
        _f.Store.Apply(root.Id, batch);

        var snap = Analysis(new FakeFileSystem(Drive)).ReadShare(root.Id);

        Assert.Equal(321, Child(snap.Root, "Folder").Size);
    }

    [Fact]
    public void NetworkPaths_AreNeverWalked_AndNeverAskedForSpace()
    {
        var fs = new FakeFileSystem(Share);
        var space = new FakeSpace();
        var analysis = Analysis(fs, space);

        Assert.Throws<InvalidOperationException>(() => analysis.ScanLocal(Share));
        Assert.Throws<InvalidOperationException>(() => analysis.ScanLocal(@"\\server\share"));
        Assert.Null(analysis.GetDriveSpace(Share));
        Assert.Empty(fs.Calls);
        Assert.Empty(space.Asked);

        Assert.NotNull(analysis.GetDriveSpace(Drive));
    }

    [Fact]
    public void ReadShare_RefusesLocalLocations()
    {
        var root = _f.Store.AddRoot(@"C:\Users\x", null, RootKind.Local);

        Assert.Throws<InvalidOperationException>(() => Analysis(Sample()).ReadShare(root.Id));
    }

    [Fact]
    public void Cancel_StopsTheWalk_AndReportsCancelled()
    {
        var fs = Sample();
        using var cts = new CancellationTokenSource();
        fs.OnList = _ =>
        {
            if (fs.Calls.Count >= 2)
            {
                cts.Cancel();
            }
        };

        var snap = Analysis(fs).ScanLocal(Drive, cancellationToken: cts.Token);

        Assert.Equal(ScanOutcome.Cancelled, snap.Outcome);
        Assert.Equal(2, fs.Calls.Count); // the walk stopped before the next folder
        Assert.Null(snap.DriveSpace);
    }

    [Fact]
    public void Cancel_BeforeTheStart_ListsNothing()
    {
        var fs = Sample();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var snap = Analysis(fs).ScanLocal(Drive, cancellationToken: cts.Token);

        Assert.Equal(ScanOutcome.Cancelled, snap.Outcome);
        Assert.Empty(fs.Calls);
    }

    [Fact]
    public void Progress_IsReportedAfterEveryFolder()
    {
        var reports = new List<SpaceProgress>();

        Analysis(Sample()).ScanLocal(Drive, reports.Add);

        Assert.Equal(5, reports.Count);
        Assert.Equal(new SpaceProgress(5, 6, 6300), reports[^1]);
    }

    [Fact]
    public void UnreachableDrive_ReportsRootUnreachable()
    {
        var fs = Sample();
        fs.Fail(string.Empty, new IOException("not ready"));

        var snap = Analysis(fs).ScanLocal(Drive);

        Assert.Equal(ScanOutcome.RootUnreachable, snap.Outcome);
        Assert.Equal(0, snap.Root.Size);
        Assert.Null(snap.DriveSpace);
    }

    [Fact]
    public void LocalWalk_WritesNothingToTheIndexStoreOrTheDatabase()
    {
        // A location with content in the index, then a live analysis of another (fake) drive.
        var indexed = new FakeFileSystem(@"S:\indexed");
        indexed.Dir("one").File(@"one\a.txt", 10);
        var root = _f.Store.AddRoot(@"S:\indexed", null, RootKind.Local);
        new Scanner(_f.Store, indexed, ExcludeRules.None, new ScannerOptions { Clock = new FakeScanClock(), LinkDetector = new FakeLinkDetector() })
            .ScanRoot(root.Id);
        string database = ScanAssert.DatabaseSnapshot(_f);
        int lastId = _f.Store.LastAllocatedId;
        int inMemory = _f.Index.Count;
        string[] hitsBefore = _f.Paths("a.txt");

        var fs = Sample();
        var snap = Analysis(fs).ScanLocal(Drive);

        Assert.Equal(6300, snap.Root.Size);
        Assert.Equal(database, ScanAssert.DatabaseSnapshot(_f));
        Assert.Equal(lastId, _f.Store.LastAllocatedId);
        Assert.Equal(inMemory, _f.Index.Count);
        Assert.Equal(hitsBefore, _f.Paths("a.txt"));
        Assert.DoesNotContain(_f.Paths("d.txt"), _ => true); // nothing of the analysed drive got into the search
    }

    [Fact]
    public void LocalWalk_RunsOnARealFolder_AndWritesNothingThere()
    {
        using var dir = new TempDir();
        Directory.CreateDirectory(Path.Combine(dir.Path, "sub"));
        File.WriteAllBytes(Path.Combine(dir.Path, "a.bin"), new byte[1000]);
        File.WriteAllBytes(Path.Combine(dir.Path, "sub", "b.bin"), new byte[234]);
        string[] before = Directory.EnumerateFileSystemEntries(dir.Path, "*", SearchOption.AllDirectories).Order().ToArray();

        var scanner = new LocalSpaceScanner(FileSystemDirectoryLister.Instance, ReparseTagLinkDetector.Instance, new FakeSpace());
        var snap = scanner.Scan(dir.Path, RootKind.Local);

        Assert.Equal(ScanOutcome.Complete, snap.Outcome);
        Assert.Equal(1234, snap.Root.Size);
        Assert.Equal(2, snap.Root.FileCount);
        Assert.Equal(1, snap.Root.FolderCount);
        Assert.Equal(before, Directory.EnumerateFileSystemEntries(dir.Path, "*", SearchOption.AllDirectories).Order().ToArray());
    }

    [Fact]
    public void Links_AreRecordedButNotEntered()
    {
        var fs = Sample();
        fs.Dir("Link", FileAttributes.ReparsePoint).File(@"Link\huge.dat", 999_999);
        var links = new FakeLinkDetector(@"R:\Link");

        var snap = Analysis(fs, links: links).ScanLocal(Drive);

        Assert.True(Child(snap.Root, "Link").IsLink);
        Assert.Equal(1, snap.LinksNotEntered);
        Assert.Equal(6300, snap.Root.Size);
        Assert.DoesNotContain(@"R:\Link", fs.Calls);
    }

    // ---------------------------------------------------------------- Recycle Bin

    [Fact]
    public void Recycle_LocalFolder_GoesToTheBin_AndTheTotalsShrink()
    {
        var snap = Analysis(Sample(), new FakeSpace(used: 7000)).ScanLocal(Drive);
        Assert.Equal(700, snap.UnreadableBytes);
        var bin = new FakeBin();
        var recycler = new SpaceRecycler(bin);
        var docs = Child(snap.Root, "Docs");
        var deepFile = snap.TopFiles.Single(f => f.Name == "d.txt");

        recycler.Recycle(snap, Child(docs, "Sub"));

        Assert.Equal([@"R:\Docs\Sub"], bin.Directories);
        Assert.Equal(300, docs.Size);
        Assert.Equal(2, docs.FileCount);
        Assert.Equal(0, docs.FolderCount);
        Assert.Equal(6300 - 1200, snap.Root.Size);
        Assert.Equal(2, snap.Root.FolderCount);
        Assert.DoesNotContain(snap.TopFiles, f => f.Name is "c.txt" or "d.txt");
        Assert.DoesNotContain(deepFile, snap.TopFiles);
        Assert.Equal(1200, snap.RecycledBytes);
        Assert.Equal(700, snap.UnreadableBytes); // the bin still holds the bytes, so the "unreadable" line does not grow
    }

    [Fact]
    public void Recycle_LocalFile_GoesToTheBin_AndTheTotalsShrink()
    {
        var snap = Analysis(Sample()).ScanLocal(Drive);
        var bin = new FakeBin();
        var file = snap.TopFiles.Single(f => f.Name == "top.txt");

        new SpaceRecycler(bin).Recycle(snap, file);

        Assert.Equal([@"R:\top.txt"], bin.Files);
        Assert.Equal(3100, snap.Root.Size);
        Assert.Equal(5, snap.Root.FileCount);
        Assert.DoesNotContain(file, snap.TopFiles);
    }

    [Fact]
    public void Recycle_IsRefusedFor_Shares_RemovableDrives_Links_AndTheTopFolder()
    {
        var bin = new FakeBin();
        var recycler = new SpaceRecycler(bin);

        // a share (numbers from the directory)
        var fs = new FakeFileSystem(@"\\server\s");
        fs.Dir("d").File(@"d\f.txt", 5);
        var shareRoot = _f.Store.AddRoot(@"\\server\s", null, RootKind.Network);
        new Scanner(_f.Store, fs, ExcludeRules.None, new ScannerOptions { Clock = new FakeScanClock(), LinkDetector = new FakeLinkDetector() })
            .ScanRoot(shareRoot.Id);
        var share = Analysis(fs).ReadShare(shareRoot.Id);
        Assert.Throws<InvalidOperationException>(() => recycler.Recycle(share, Child(share.Root, "d")));
        Assert.Throws<InvalidOperationException>(() => recycler.Recycle(share, share.TopFiles[0]));

        // a removable drive has no Recycle Bin: a delete would be final
        var usb = new FakeFileSystem(@"U:\");
        usb.Dir("d").File(@"d\f.txt", 5);
        var removable = Analysis(usb).ScanLocal(@"U:\");
        Assert.Equal(RootKind.Removable, removable.Kind);
        Assert.Throws<InvalidOperationException>(() => recycler.Recycle(removable, Child(removable.Root, "d")));

        // a link and the top folder of a local drive
        var fs2 = Sample();
        fs2.Dir("Link", FileAttributes.ReparsePoint);
        var local = Analysis(fs2, links: new FakeLinkDetector(@"R:\Link")).ScanLocal(Drive);
        Assert.Throws<InvalidOperationException>(() => recycler.Recycle(local, Child(local.Root, "Link")));
        Assert.Throws<InvalidOperationException>(() => recycler.Recycle(local, local.Root));

        Assert.Empty(bin.Files);
        Assert.Empty(bin.Directories);
    }

    [Fact]
    public void Recycle_DoesNotChangeTheSnapshot_WhenTheBinFails()
    {
        var snap = Analysis(Sample()).ScanLocal(Drive);
        var recycler = new SpaceRecycler(new ThrowingBin());

        Assert.Throws<IOException>(() => recycler.Recycle(snap, Child(snap.Root, "Docs")));

        Assert.Equal(6300, snap.Root.Size);
        Assert.Equal(2, snap.Root.Children.Count);
    }

    private sealed class ThrowingBin : IRecycleBin
    {
        public void MoveFile(string path) => throw new IOException("in use");

        public void MoveDirectory(string path) => throw new IOException("in use");
    }

    // ---------------------------------------------------------------- formatting

    [Theory]
    [InlineData(0L, "0 B")]
    [InlineData(1023L, "1,023 B")]
    [InlineData(1024L, "1.00 KB")]
    [InlineData(1536L, "1.50 KB")]
    [InlineData(10L * 1024 * 1024, "10.0 MB")]
    [InlineData(250L * 1024 * 1024 * 1024, "250 GB")]
    public void ByteSize_UsesBinaryUnits(long bytes, string expected)
        => Assert.Equal(expected, ByteSize.Format(bytes, System.Globalization.CultureInfo.InvariantCulture));
}
