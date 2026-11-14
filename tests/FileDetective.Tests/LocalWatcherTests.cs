using FileDetective.Core;

namespace FileDetective.Tests;

/// <summary>
/// The <see cref="LocalWatcher"/> (PLAN.md 7.6) against real temp folders with a real <see cref="FileSystemWatcher"/>,
/// and with a fake change source where events must be controlled exactly. Every wait polls for the end state.
/// </summary>
public class LocalWatcherTests : IDisposable
{
    private readonly TempDir _data = new();
    private readonly TempDir _tree = new();
    private readonly TempDir _outside = new();
    private readonly StorageFixture _f;
    private readonly LocationLocks _locks = new();
    private readonly List<byte> _fullScans = [];
    private readonly List<string> _log = [];
    private LocalWatcher? _watcher;

    public LocalWatcherTests()
    {
        _f = new StorageFixture(_data);
    }

    private string RootPath => _tree.Path;

    private static ExcludeRules Rules { get; } = ExcludeRules.CreateDefault();

    public void Dispose()
    {
        _watcher?.Dispose();
        _f.Dispose();
        _data.Dispose();
        _tree.Dispose();
        _outside.Dispose();
    }

    private string P(string relative) => Path.Combine(RootPath, relative);

    private static void WriteFile(string path, int size = 10)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[size]);
    }

    /// <summary>Location with a first scan, then a watcher on it.</summary>
    private RootInfo Start(Func<string, IChangeSource>? createSource = null, IDirectoryLister? lister = null, string? rootPath = null)
    {
        var root = _f.Store.AddRoot(rootPath ?? RootPath, null, RootKind.Local);
        new Scanner(_f.Store, lister ?? FileSystemDirectoryLister.Instance, Rules).ScanRoot(root.Id);
        _watcher = new LocalWatcher(_f.Store, new LocalWatcherOptions
        {
            Excludes = () => Rules,
            Lister = lister,
            Locks = _locks,
            CreateSource = createSource,
            FullScanNeeded = id =>
            {
                lock (_fullScans)
                {
                    _fullScans.Add(id);
                }
            },
            Log = (message, ex) =>
            {
                lock (_log)
                {
                    _log.Add(ex is null ? message : $"{message}: {ex.Message}");
                }
            },
        });
        Assert.True(_watcher.Watch(_f.Store.GetRoot(root.Id)!));
        return root;
    }

    private bool Known(string path) => _f.Store.FindEntryId(path) is not null;

    private void WaitKnown(string path) => Poll.Until(() => Known(path), $"{path} in the index");

    private void WaitGone(string path) => Poll.Until(() => !Known(path), $"{path} removed from the index");

    /// <summary>
    /// Creates a marker file and waits until the watcher has recorded it and is idle. Events that came before the
    /// marker have then been handled, so a test can assert that something did NOT happen without a fixed sleep.
    /// </summary>
    private void Settle()
    {
        string marker = P($"zz-marker-{Guid.NewGuid():N}.txt");
        WriteFile(marker);
        WaitKnown(marker);
        Poll.Until(() => _watcher!.IsIdle, "watcher idle");
    }

    /// <summary>The index content of the location as relative path to "is folder", compared with the disk.</summary>
    private void AssertIndexMatchesDisk()
    {
        Poll.Until(() => _watcher!.IsIdle, "watcher idle");
        var expected = ScanAssert.WalkFileSystem(RootPath, Rules).ToDictionary(kv => kv.Key, kv => kv.Value.IsDirectory);
        var actual = ScanAssert.ReadTree(_f, 1).ToDictionary(kv => kv.Key, kv => kv.Value.State.IsDirectory);
        Assert.Equal(expected.OrderBy(kv => kv.Key, StringComparer.Ordinal), actual.OrderBy(kv => kv.Key, StringComparer.Ordinal));
    }

    [Fact]
    public void NewFileIsRecorded_WithItsSize()
    {
        WriteFile(P(@"Docs\old.txt"));
        Start();

        WriteFile(P(@"Docs\Angebot Müller.pdf"), 1234);
        WaitKnown(P(@"Docs\Angebot Müller.pdf"));
        Assert.Contains(P(@"Docs\Angebot Müller.pdf"), _f.Paths("angebot mueller"));
        Poll.Until(() => _f.Store.GetEntry(_f.Store.FindEntryId(P(@"Docs\Angebot Müller.pdf"))!.Value)!.Size == 1234, "size");
        AssertIndexMatchesDisk();
    }

    [Fact]
    public void DeletedFileAndDeletedFolderWithContentAreRemoved()
    {
        WriteFile(P("a.txt"));
        WriteFile(P(@"Old\Deep\x.txt"));
        WriteFile(P(@"Old\y.txt"));
        Start();
        Assert.True(Known(P(@"Old\Deep\x.txt")));

        File.Delete(P("a.txt"));
        Directory.Delete(P("Old"), recursive: true);

        WaitGone(P("a.txt"));
        WaitGone(P("Old"));
        Assert.False(Known(P(@"Old\Deep\x.txt")));
        Assert.Empty(_f.Paths("x.txt"));
        AssertIndexMatchesDisk();
    }

    [Fact]
    public void RenamedFileKeepsItsEntry()
    {
        WriteFile(P("draft.txt"));
        Start();
        int id = _f.Store.FindEntryId(P("draft.txt"))!.Value;

        File.Move(P("draft.txt"), P("final.txt"));

        WaitKnown(P("final.txt"));
        WaitGone(P("draft.txt"));
        Assert.Equal(id, _f.Store.FindEntryId(P("final.txt"))); // moved directly, one row
        AssertIndexMatchesDisk();
    }

    [Fact]
    public void RenamedFolderKeepsItsSubtree_AndThePathHitsOfItsChildrenChange()
    {
        WriteFile(P(@"Projekt Alt\Sub\report.docx"));
        WriteFile(P(@"Projekt Alt\notes.txt"));
        Start();
        int folderId = _f.Store.FindEntryId(P("Projekt Alt"))!.Value;
        int fileId = _f.Store.FindEntryId(P(@"Projekt Alt\Sub\report.docx"))!.Value;

        Directory.Move(P("Projekt Alt"), P("Projekt Neu"));

        WaitKnown(P(@"Projekt Neu\Sub\report.docx"));
        WaitGone(P("Projekt Alt"));
        Assert.Equal(folderId, _f.Store.FindEntryId(P("Projekt Neu")));
        Assert.Equal(fileId, _f.Store.FindEntryId(P(@"Projekt Neu\Sub\report.docx")));
        Assert.Contains(P(@"Projekt Neu\Sub\report.docx"), _f.Paths("neu report"));
        Assert.Empty(_f.Paths("alt report"));
        AssertIndexMatchesDisk();
    }

    [Fact]
    public void FileMovedIntoAnotherFolderOfTheLocationIsFollowed()
    {
        // Windows reports a move between folders as "deleted" plus "created" (no rename), so the entry gets a new
        // number; what counts is the end state.
        WriteFile(P(@"In\letter.txt"));
        Directory.CreateDirectory(P("Archive"));
        Start();

        File.Move(P(@"In\letter.txt"), P(@"Archive\letter.txt"));

        WaitKnown(P(@"Archive\letter.txt"));
        WaitGone(P(@"In\letter.txt"));
        AssertIndexMatchesDisk();
    }

    [Fact]
    public void RenameEventAcrossFolders_MovesTheEntryDirectly_WhenBothListingsConfirmIt()
    {
        var fs = new FakeFileSystem(@"Q:\Data").Dir("In").Dir("Archive").File(@"In\letter.txt");
        var fake = new FakeChangeSource();
        Start(_ => fake, fs, fs.RootPath);
        int id = _f.Store.FindEntryId(@"Q:\Data\In\letter.txt")!.Value;

        fs.Remove(@"In\letter.txt").File(@"Archive\brief.txt");
        fake.RaiseRenamed(@"Q:\Data\In\letter.txt", @"Q:\Data\Archive\brief.txt");
        WaitKnown(@"Q:\Data\Archive\brief.txt");
        Assert.Equal(id, _f.Store.FindEntryId(@"Q:\Data\Archive\brief.txt"));
        Assert.False(Known(@"Q:\Data\In\letter.txt"));

        // A rename event whose old name is still there (e.g. recreated in between) is not taken as a move.
        fs.File(@"In\x.txt").File(@"In\y.txt");
        fake.RaiseChanged(@"Q:\Data\In\x.txt");
        WaitKnown(@"Q:\Data\In\x.txt");
        int x = _f.Store.FindEntryId(@"Q:\Data\In\x.txt")!.Value;
        fake.RaiseRenamed(@"Q:\Data\In\x.txt", @"Q:\Data\In\y.txt");
        WaitKnown(@"Q:\Data\In\y.txt");
        Assert.Equal(x, _f.Store.FindEntryId(@"Q:\Data\In\x.txt"));
        Assert.NotEqual(x, _f.Store.FindEntryId(@"Q:\Data\In\y.txt"));
    }

    [Fact]
    public void FolderWithContentMovedInIsScannedAsSubtree()
    {
        WriteFile(P("existing.txt"));
        Start();

        // Built outside the location, then moved in: the watcher reports only the folder itself.
        string source = Path.Combine(_outside.Path, "Import");
        WriteFile(Path.Combine(source, "a.txt"));
        WriteFile(Path.Combine(source, @"Sub\b.txt"));
        WriteFile(Path.Combine(source, @"Sub\Deeper\c.txt"));
        WriteFile(Path.Combine(source, @"node_modules\lib.js"));
        Directory.Move(source, P("Import"));

        WaitKnown(P(@"Import\Sub\Deeper\c.txt"));
        Assert.True(Known(P(@"Import\a.txt")));
        Assert.True(Known(P(@"Import\Sub\b.txt")));
        Assert.False(Known(P(@"Import\node_modules")));
        AssertIndexMatchesDisk();
    }

    [Fact]
    public void EventsInExcludedFoldersAndForExcludedFilesChangeNothing()
    {
        WriteFile(P(@"Code\main.cs"));
        WriteFile(P(@"Code\node_modules\keep.js"));
        Start();
        Assert.False(Known(P(@"Code\node_modules")));
        int before = ScanAssert.TotalEntries(_f);

        WriteFile(P(@"Code\node_modules\new.js"));
        WriteFile(P(@"Code\node_modules\pkg\index.js"));
        WriteFile(P(@"Code\.git\HEAD"));
        WriteFile(P(@"Code\scratch.tmp"));
        WriteFile(P(@"Code\~$Angebot.docx"));
        Settle();

        Assert.False(Known(P(@"Code\node_modules")));
        Assert.False(Known(P(@"Code\node_modules\new.js")));
        Assert.False(Known(P(@"Code\.git")));
        Assert.False(Known(P(@"Code\scratch.tmp")));
        Assert.Equal(before + 1, ScanAssert.TotalEntries(_f)); // only the marker
        AssertIndexMatchesDisk();
    }

    [Fact]
    public void RenameToAnExcludedNameRemovesTheEntry_RenameBackRecordsItAgain()
    {
        WriteFile(P("data.txt"));
        Start();

        File.Move(P("data.txt"), P("data.tmp"));
        WaitGone(P("data.txt"));
        Settle();
        Assert.False(Known(P("data.tmp")));

        File.Move(P("data.tmp"), P("data.txt"));
        WaitKnown(P("data.txt"));
        AssertIndexMatchesDisk();
    }

    [Fact]
    public void ChangeOfCaseOnlyIsRecorded()
    {
        WriteFile(P("readme.txt"));
        Start();
        int id = _f.Store.FindEntryId(P("readme.txt"))!.Value;

        File.Move(P("readme.txt"), P("README.txt"));
        Poll.Until(() => _f.Store.GetEntry(id)?.Name == "README.txt", "new spelling");
    }

    [Fact]
    public void ManyQuickChangesEndInTheRightState()
    {
        WriteFile(P("keep.txt"));
        Start();

        for (int i = 0; i < 40; i++)
        {
            WriteFile(P($@"Burst\f{i}.txt"));
        }

        for (int i = 0; i < 40; i += 2)
        {
            File.Delete(P($@"Burst\f{i}.txt"));
        }

        File.Move(P(@"Burst\f1.txt"), P(@"Burst\renamed.txt"));
        File.WriteAllText(P(@"Burst\f1.txt"), "again");

        WaitKnown(P(@"Burst\renamed.txt"));
        Settle();
        AssertIndexMatchesDisk();
    }

    [Fact]
    public void WhileAWholeScanHoldsTheLocation_TheWatcherWaits_ThenCatchesUp()
    {
        Start();
        var root = _f.Store.GetRoots()[0];

        using (_locks.Acquire(root.Id))
        {
            WriteFile(P("during-scan.txt"));

            // The watcher handles its batch only under the lock: it must not have touched the location yet. A short
            // fixed wait is unavoidable to show that something does NOT happen; it does not decide the outcome.
            Thread.Sleep(800);
            Assert.False(Known(P("during-scan.txt")));
            Assert.False(_watcher!.IsIdle);
        }

        WaitKnown(P("during-scan.txt"));
        AssertIndexMatchesDisk();
    }

    [Fact]
    public void BufferOverflow_TriggersAWholeScanOfTheLocation_ThroughTheQueue()
    {
        WriteFile(P("old.txt"));
        var fake = new FakeChangeSource();
        var root = Start(_ => fake);

        // Changes the watcher never heard about (events lost).
        WriteFile(P("missed.txt"));
        File.Delete(P("old.txt"));

        var executor = new ScanExecutor(_f.Store, _locks, _ => new Scanner(_f.Store, FileSystemDirectoryLister.Instance, Rules));
        using var queue = new ScanQueue((request, token) => executor.Run(request, token));

        fake.RaiseError(new InternalBufferOverflowException());
        lock (_fullScans)
        {
            Assert.Equal([root.Id], _fullScans);
        }

        queue.Enqueue(new ScanRequest(root.Id, RootKind.Local, ScanTrigger.Overflow));
        Assert.True(queue.WaitIdle(TimeSpan.FromSeconds(15)));
        Assert.True(Known(P("missed.txt")));
        Assert.False(Known(P("old.txt")));
    }

    [Fact]
    public void AFailedListingOfTheParentNeverRemovesAnything()
    {
        var fs = new FakeFileSystem(@"Q:\Data").Dir("Docs").File(@"Docs\a.txt").File(@"Docs\b.txt");
        var fake = new FakeChangeSource();
        Start(_ => fake, fs, fs.RootPath);
        Assert.True(Known(@"Q:\Data\Docs\a.txt"));

        // a.txt is gone, but the parent cannot be listed: nothing may be removed.
        fs.Remove(@"Docs\a.txt");
        fs.Fail("Docs", new UnauthorizedAccessException("denied"));
        fake.RaiseChanged(@"Q:\Data\Docs\a.txt");
        Poll.Until(() => _watcher!.IsIdle && CallCount(fs, @"Q:\Data\Docs") >= 2, "event handled");
        Assert.True(Known(@"Q:\Data\Docs\a.txt"));
        Assert.True(Known(@"Q:\Data\Docs\b.txt"));

        // Also not with an I/O error such as a broken connection.
        fs.Fail("Docs", new IOException("The network name is no longer available."));
        fake.RaiseChanged(@"Q:\Data\Docs\a.txt");
        Poll.Until(() => _watcher!.IsIdle && CallCount(fs, @"Q:\Data\Docs") >= 3, "event handled");
        Assert.True(Known(@"Q:\Data\Docs\a.txt"));

        // As soon as the listing works, it is removed.
        fs.ClearFailures();
        fake.RaiseChanged(@"Q:\Data\Docs\a.txt");
        WaitGone(@"Q:\Data\Docs\a.txt");
        Assert.True(Known(@"Q:\Data\Docs\b.txt"));
    }

    [Fact]
    public void AVanishedParentIsDecidedByItsOwnParent_OnlyWithASuccessfulListing()
    {
        var fs = new FakeFileSystem(@"Q:\Data").Dir("Docs").Dir(@"Docs\Sub").File(@"Docs\Sub\a.txt");
        var fake = new FakeChangeSource();
        Start(_ => fake, fs, fs.RootPath);

        // Sub is gone; the event names only the file inside it. Docs (the grandparent) can be listed.
        fs.Remove(@"Docs\Sub");
        fake.RaiseChanged(@"Q:\Data\Docs\Sub\a.txt");
        WaitGone(@"Q:\Data\Docs\Sub");
        Assert.True(Known(@"Q:\Data\Docs"));

        // Same again, but now the grandparent fails too: nothing is removed.
        fs.Dir(@"Docs\Sub2").File(@"Docs\Sub2\b.txt");
        fake.RaiseChanged(@"Q:\Data\Docs\Sub2");
        WaitKnown(@"Q:\Data\Docs\Sub2\b.txt");
        fs.Remove(@"Docs\Sub2");
        fs.Fail("Docs", new UnauthorizedAccessException("denied"));
        fake.RaiseChanged(@"Q:\Data\Docs\Sub2\b.txt");
        Settle_Fake(fake, fs); // events are handled in order: once the marker is in, the event before it was handled
        Assert.True(Known(@"Q:\Data\Docs\Sub2\b.txt"));
    }

    [Fact]
    public void EventsOfANeverScannedLocationAreLeftToItsFirstScan()
    {
        var fake = new FakeChangeSource();
        var root = _f.Store.AddRoot(RootPath, null, RootKind.Local);
        _watcher = new LocalWatcher(_f.Store, new LocalWatcherOptions { Locks = _locks, CreateSource = _ => fake });
        Assert.True(_watcher.Watch(root));

        WriteFile(P("a.txt"));
        fake.RaiseChanged(P("a.txt"));
        Poll.Until(() => _watcher.IsIdle, "watcher idle");
        Assert.False(Known(P("a.txt")));
    }

    [Fact]
    public void SharesNeverGetAWatcher()
    {
        _watcher = new LocalWatcher(_f.Store, new LocalWatcherOptions { CreateSource = _ => throw new InvalidOperationException("must not be called") });
        var share = _f.Store.AddRoot(@"P:\Projekte", @"\\server\projekte", RootKind.Network);
        var removable = _f.Store.AddRoot(@"E:\", null, RootKind.Removable);
        var uncAsLocal = _f.Store.AddRoot(@"\\server\other", null, RootKind.Local);

        Assert.Throws<ArgumentException>(() => _watcher.Watch(share));
        Assert.Throws<ArgumentException>(() => _watcher.Watch(removable));
        Assert.Throws<ArgumentException>(() => _watcher.Watch(uncAsLocal));
        _watcher.Sync(_f.Store.GetRoots());
        Assert.False(_watcher.IsWatching(share.Id));
        Assert.False(_watcher.IsWatching(removable.Id));
        Assert.False(_watcher.IsWatching(uncAsLocal.Id));
    }

    [Fact]
    public void UnwatchStopsHandlingTheLocation()
    {
        var root = Start();
        _watcher!.Unwatch(root.Id);
        Assert.False(_watcher.IsWatching(root.Id));

        WriteFile(P("after.txt"));
        Poll.Until(() => _watcher.IsIdle, "idle");
        Thread.Sleep(500); // nothing is expected to happen; the outcome does not depend on this wait
        Assert.False(Known(P("after.txt")));
    }

    private static int CallCount(FakeFileSystem fs, string path)
    {
        lock (fs.Calls)
        {
            return fs.Calls.Count(c => c == path);
        }
    }

    /// <summary>Settles a fake-driven watcher: one more known event, then idle.</summary>
    private void Settle_Fake(FakeChangeSource fake, FakeFileSystem fs)
    {
        string name = $"zz-{Guid.NewGuid():N}.txt";
        fs.File(name);
        fake.RaiseChanged(fs.P(name));
        WaitKnown(fs.P(name));
        Poll.Until(() => _watcher!.IsIdle, "watcher idle");
    }

    private sealed class FakeChangeSource : IChangeSource
    {
        public event Action<string>? PathChanged;

        public event Action<string, string>? PathRenamed;

        public event Action<Exception>? Error;

        public void RaiseChanged(string path) => PathChanged?.Invoke(path);

        public void RaiseRenamed(string oldPath, string newPath) => PathRenamed?.Invoke(oldPath, newPath);

        public void RaiseError(Exception ex) => Error?.Invoke(ex);

        public void Start()
        {
        }

        public void Dispose()
        {
        }
    }
}
