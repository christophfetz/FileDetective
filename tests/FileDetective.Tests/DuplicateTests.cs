using FileDetective.Core;

namespace FileDetective.Tests;

/// <summary>Stage 10 acceptance (PLAN.md 11.2): the duplicate search with fakes for drives, file access and Recycle Bin.</summary>
public class DuplicateTests : IDisposable
{
    private const long MinSize = 1024;
    private const int Big = 200_000; // more than the 64 KB of the partial check

    private readonly TempDir _dir = new();
    private readonly StorageFixture _f;
    private readonly FakeLocalFiles _files = new();
    private readonly DriveDiscovery _drives = new(new DuplicateDriveSource());

    public DuplicateTests()
    {
        _f = new StorageFixture(_dir);
    }

    public void Dispose()
    {
        _f.Dispose();
        _dir.Dispose();
    }

    private DuplicateFinder Finder() => new(_f.Store, _files, _drives);

    private int AddFile(RootInfo root, int parent, string name, long size, FileAttributes attributes = FileAttributes.Archive)
    {
        int id = _f.Store.AllocateId();
        var batch = new ChangeBatch();
        batch.Inserts.Add(new EntryInsert(id, parent, name, false, size, 1_767_268_800, attributes));
        _f.Store.Apply(root.Id, batch);
        return id;
    }

    private int AddDir(RootInfo root, int parent, string name)
    {
        int id = _f.Store.AllocateId();
        var batch = new ChangeBatch();
        batch.Inserts.Add(new EntryInsert(id, parent, name, true, 0, 1_767_268_800, FileAttributes.Directory));
        _f.Store.Apply(root.Id, batch);
        return id;
    }

    /// <summary>A file in the directory and in the fake file system at once.</summary>
    private int File(RootInfo root, int parent, string fullPath, byte[] content, FileAttributes attributes = FileAttributes.Archive, long? ticks = null)
    {
        _files.Add(fullPath, content, ticks, attributes);
        return AddFile(root, parent, Path.GetFileName(fullPath), content.Length, attributes);
    }

    private RootInfo LocalRoot() => _f.Store.AddRoot(@"R:\", null, RootKind.Local);

    private long HashRows() => Scalar("SELECT COUNT(*) FROM hashes");

    private long Scalar(string sql)
    {
        using var c = _f.Db.OpenConnection();
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToInt64(cmd.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static string[] Paths(DuplicateGroup g) => [.. g.Files.Select(f => f.FullPath)];

    // ---------------------------------------------------------------- comparison

    [Fact]
    public void SameSize_DifferentContent_IsNoDuplicate_EvenWithEqualFirst64KB()
    {
        var root = LocalRoot();
        var a = Content.Random(Big, 1);
        File(root, root.RootEntryId, @"R:\a.bin", a);
        File(root, root.RootEntryId, @"R:\b.bin", Content.ChangedAt(a, Big - 1)); // same first 64 KB, differs at the end
        File(root, root.RootEntryId, @"R:\c.bin", Content.ChangedAt(a, 10)); // differs in the first 64 KB

        var result = Finder().FindLocal(MinSize);

        Assert.Empty(result.Groups);
        // a and b needed the whole read; c dropped out after the first 64 KB.
        Assert.Equal(2, _files.OpensOf(@"R:\a.bin"));
        Assert.Equal(2, _files.OpensOf(@"R:\b.bin"));
        Assert.Equal(1, _files.OpensOf(@"R:\c.bin"));
    }

    [Fact]
    public void EqualContent_IsADuplicate_WithWasteAndSorting()
    {
        var root = LocalRoot();
        var small = Content.Random(5_000, 2);
        var large = Content.Random(Big, 3);
        File(root, root.RootEntryId, @"R:\s1.bin", small);
        File(root, root.RootEntryId, @"R:\s2.bin", small);
        File(root, root.RootEntryId, @"R:\s3.bin", small);
        File(root, root.RootEntryId, @"R:\l1.bin", large);
        File(root, root.RootEntryId, @"R:\l2.bin", large);
        File(root, root.RootEntryId, @"R:\alone.bin", Content.Random(Big + 1, 4));

        var result = Finder().FindLocal(MinSize);

        Assert.Equal(2, result.Groups.Count);
        Assert.Equal([@"R:\l1.bin", @"R:\l2.bin"], Paths(result.Groups[0])); // 200,000 wasted before 10,000
        Assert.Equal(Big, result.Groups[0].WastedBytes);
        Assert.Equal(10_000, result.Groups[1].WastedBytes);
        Assert.All(result.Groups, g => Assert.True(g.IsVerified));
        Assert.Equal(0, _files.CallsOf(@"R:\alone.bin")); // no other file of its size: not even probed
        Assert.Equal(1, _files.OpensOf(@"R:\s1.bin")); // up to 64 KB: the first read is the whole file
    }

    [Fact]
    public void MinimumSize_LeavesSmallerFilesOut()
    {
        var root = LocalRoot();
        var small = Content.Random(2_000, 5);
        File(root, root.RootEntryId, @"R:\a.bin", small);
        File(root, root.RootEntryId, @"R:\b.bin", small);

        Assert.Empty(Finder().FindLocal(4_096).Groups);
        Assert.Equal(0, _files.TotalCalls);
        Assert.Single(Finder().FindLocal(1_024).Groups);
    }

    [Fact]
    public void HardLink_IsNoDuplicate()
    {
        var root = LocalRoot();
        var a = Content.Random(Big, 6);
        File(root, root.RootEntryId, @"R:\a.bin", a);
        _files.HardLink(@"R:\link.bin", @"R:\a.bin");
        AddFile(root, root.RootEntryId, "link.bin", Big);

        var result = Finder().FindLocal(MinSize);

        Assert.Empty(result.Groups);
        Assert.Equal(1, result.Stats.HardLinks);
        Assert.Equal(0, _files.OpenCount); // nothing left to compare, nothing is read
    }

    [Fact]
    public void HardLink_PlusRealCopy_CountsTheFileOnce()
    {
        var root = LocalRoot();
        var a = Content.Random(Big, 7);
        File(root, root.RootEntryId, @"R:\a.bin", a);
        _files.HardLink(@"R:\link.bin", @"R:\a.bin");
        AddFile(root, root.RootEntryId, "link.bin", Big);
        File(root, root.RootEntryId, @"R:\copy.bin", (byte[])a.Clone());

        var result = Finder().FindLocal(MinSize);

        var group = Assert.Single(result.Groups);
        Assert.Equal(2, group.Files.Count);
        Assert.Contains(@"R:\copy.bin", Paths(group));
        Assert.Equal(Big, group.WastedBytes);
        Assert.Equal(1, result.Stats.HardLinks);
    }

    [Fact]
    public void CloudAttribute_PreventsEveryRead()
    {
        var root = LocalRoot();
        var a = Content.Random(Big, 8);
        File(root, root.RootEntryId, @"R:\local.bin", a);
        // According to the directory already in the cloud: not even probed.
        File(root, root.RootEntryId, @"R:\recall.bin", a, SpaceCollector.RecallOnDataAccess | FileAttributes.Archive);
        File(root, root.RootEntryId, @"R:\offline.bin", a, FileAttributes.Offline);
        File(root, root.RootEntryId, @"R:\onopen.bin", a, DuplicateFinder.RecallOnOpen);
        // Freed up after the last scan: the probe (a directory listing) shows it, and then nothing more happens.
        File(root, root.RootEntryId, @"R:\freed.bin", a);
        _files.Change(@"R:\freed.bin", attributes: SpaceCollector.RecallOnDataAccess);

        var result = Finder().FindLocal(MinSize);

        Assert.Empty(result.Groups); // only local.bin is left, alone
        foreach (var path in new[] { @"R:\recall.bin", @"R:\offline.bin", @"R:\onopen.bin" })
        {
            Assert.Equal(0, _files.CallsOf(path));
        }

        Assert.Equal(0, _files.Identities.GetValueOrDefault(@"R:\freed.bin"));
        Assert.Equal(0, _files.OpensOf(@"R:\freed.bin"));
        Assert.Equal(0, _files.OpenCount);
        Assert.Equal(4, result.Stats.CloudSkipped);
    }

    [Fact]
    public void SymbolicLinkFile_IsNeverFollowed()
    {
        var root = LocalRoot();
        var a = Content.Random(Big, 9);
        File(root, root.RootEntryId, @"R:\a.bin", a);
        _files.Add(@"R:\symlink.bin", a, attributes: FileAttributes.ReparsePoint, tag: 0xA000000C);
        AddFile(root, root.RootEntryId, "symlink.bin", Big, FileAttributes.ReparsePoint);

        var result = Finder().FindLocal(MinSize);

        Assert.Empty(result.Groups);
        Assert.Equal(1, result.Stats.LinksSkipped);
        Assert.Equal(0, _files.Identities.GetValueOrDefault(@"R:\symlink.bin"));
        Assert.Equal(0, _files.OpensOf(@"R:\symlink.bin"));
    }

    [Fact]
    public void StoredHashes_AreReused_WithoutReadingAgain()
    {
        var root = LocalRoot();
        var a = Content.Random(Big, 10);
        File(root, root.RootEntryId, @"R:\a.bin", a);
        File(root, root.RootEntryId, @"R:\b.bin", (byte[])a.Clone());
        Assert.Single(Finder().FindLocal(MinSize).Groups);
        Assert.Equal(2, HashRows());
        _files.ResetCounters();

        var again = Finder().FindLocal(MinSize);

        Assert.Single(again.Groups);
        Assert.Equal(0, _files.OpenCount);
        Assert.Equal(0, _files.BytesRead);
        Assert.Equal(4, again.Stats.HashesReused); // partial and full of both
    }

    [Fact]
    public void StoredHashes_SurviveARestart()
    {
        var root = LocalRoot();
        var a = Content.Random(Big, 11);
        File(root, root.RootEntryId, @"R:\a.bin", a);
        File(root, root.RootEntryId, @"R:\b.bin", (byte[])a.Clone());
        Finder().FindLocal(MinSize);
        _f.Restart();
        _files.ResetCounters();

        Assert.Single(Finder().FindLocal(MinSize).Groups);
        Assert.Equal(0, _files.OpenCount);
    }

    [Fact]
    public void ChangedFile_MakesTheStoredHashInvalid()
    {
        var root = LocalRoot();
        var a = Content.Random(Big, 12);
        File(root, root.RootEntryId, @"R:\a.bin", a);
        File(root, root.RootEntryId, @"R:\b.bin", (byte[])a.Clone());
        Assert.Single(Finder().FindLocal(MinSize).Groups);

        // Same size, new content and a new time; the directory still has the old values (the watcher ignores times).
        _files.Change(@"R:\b.bin", Content.ChangedAt(a, 100), FakeLocalFiles.BaseTicks + 1);
        _files.ResetCounters();

        var result = Finder().FindLocal(MinSize);

        Assert.Empty(result.Groups);
        Assert.Equal(0, _files.OpensOf(@"R:\a.bin")); // still valid
        Assert.Equal(1, _files.OpensOf(@"R:\b.bin")); // read again; the first 64 KB differ now
    }

    [Fact]
    public void ChangedSize_SinceTheScan_IsSkipped()
    {
        var root = LocalRoot();
        var a = Content.Random(Big, 13);
        File(root, root.RootEntryId, @"R:\a.bin", a);
        File(root, root.RootEntryId, @"R:\b.bin", (byte[])a.Clone());
        _files.Change(@"R:\b.bin", Content.Random(Big + 5, 14));

        var result = Finder().FindLocal(MinSize);

        Assert.Empty(result.Groups);
        Assert.Equal(1, result.Stats.Changed);
        Assert.Equal(0, _files.OpenCount);
    }

    [Fact]
    public void Cancel_StopsTheCheck_AndKeepsWhatWasComputed()
    {
        var root = LocalRoot();
        var a = Content.Random(Big, 15);
        for (int i = 0; i < 6; i++)
        {
            File(root, root.RootEntryId, $@"R:\f{i}.bin", (byte[])a.Clone());
        }

        using var cts = new CancellationTokenSource();
        Assert.ThrowsAny<OperationCanceledException>(() => Finder().FindLocal(
            MinSize,
            p =>
            {
                if (p.Phase == DuplicatePhase.Partial && p.Done == 3)
                {
                    cts.Cancel();
                }
            },
            cts.Token));

        Assert.Equal(3, _files.OpenCount); // stopped before the fourth file
        Assert.Equal(3, HashRows());

        _files.ResetCounters();
        var result = Finder().FindLocal(MinSize);
        Assert.Equal(6, Assert.Single(result.Groups).Files.Count);
        Assert.Equal(3 + 6, _files.OpenCount); // three partial reads left, then six whole reads
    }

    [Fact]
    public void Progress_GoesThroughThePhases()
    {
        var root = LocalRoot();
        var a = Content.Random(Big, 16);
        File(root, root.RootEntryId, @"R:\a.bin", a);
        File(root, root.RootEntryId, @"R:\b.bin", (byte[])a.Clone());
        var seen = new List<DuplicateProgress>();

        Finder().FindLocal(MinSize, seen.Add);

        Assert.Equal([DuplicatePhase.Checking, DuplicatePhase.Partial, DuplicatePhase.Full], seen.Select(p => p.Phase).Distinct());
        Assert.Equal((2L * DuplicateFinder.PartialLength) + (2L * Big), seen[^1].BytesRead);
    }

    [Fact]
    public async Task LowPriorityWork_RunsAndPassesCancellationOn()
    {
        Assert.Equal(42, await LowPriorityWork.Run(() => 42));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => LowPriorityWork.Run<int>(() => throw new OperationCanceledException()));
        await Assert.ThrowsAsync<IOException>(() => LowPriorityWork.Run<int>(() => throw new IOException("x")));
    }

    // ---------------------------------------------------------------- shares

    [Fact]
    public void NetworkLocations_NeverOpenAFile()
    {
        var share = _f.Store.AddRoot(@"N:\", @"\\server\share", RootKind.Network);
        var unc = _f.Store.AddRoot(@"\\other\data", null, RootKind.Local); // a UNC path, whatever kind is stored
        var mapped = _f.Store.AddRoot(@"N:\Mapped", null, RootKind.Local); // a mapped network drive stored as local
        var a = Content.Random(Big, 17);
        foreach (var (root, prefix) in new[] { (share, @"N:\"), (unc, @"\\other\data\"), (mapped, @"N:\Mapped\") })
        {
            int d1 = AddDir(root, root.RootEntryId, "One");
            int d2 = AddDir(root, root.RootEntryId, "Two");
            File(root, d1, prefix + @"One\Report.pdf", a);
            File(root, d2, prefix + @"Two\report.PDF", a);
        }

        var local = Finder().FindLocal(MinSize);
        var possible = Finder().FindOnShares(MinSize);

        Assert.Empty(local.Groups);
        Assert.Equal(0, local.Stats.Candidates);
        Assert.Equal(0, _files.TotalCalls);
        var group = Assert.Single(possible); // same name and size on all three: one group over the locations
        Assert.False(group.IsVerified);
        Assert.Equal(6, group.Files.Count);
        Assert.All(group.Files, f => Assert.False(f.CanRecycle));
    }

    [Fact]
    public void ShareGroups_UseTheSearchForm_AndTheSize()
    {
        var share = _f.Store.AddRoot(@"N:\", @"\\server\share", RootKind.Network);
        int d1 = AddDir(share, share.RootEntryId, "A");
        int d2 = AddDir(share, share.RootEntryId, "B");
        int d3 = AddDir(share, share.RootEntryId, "C");
        AddFile(share, d1, "Müller.docx", 50_000);
        AddFile(share, d2, "MUELLER.docx", 50_000);
        AddFile(share, d3, "Mueller.docx", 50_001); // other size
        AddFile(share, d3, "Schmidt.docx", 50_000); // other name

        var group = Assert.Single(Finder().FindOnShares(MinSize));

        Assert.Equal([@"N:\A\Müller.docx", @"N:\B\MUELLER.docx"], Paths(group));
        Assert.Equal(0, _files.TotalCalls);
    }

    [Fact]
    public void ShareGroups_CannotBeMarked_NorMoved()
    {
        var share = _f.Store.AddRoot(@"N:\", @"\\server\share", RootKind.Network);
        int d1 = AddDir(share, share.RootEntryId, "A");
        int d2 = AddDir(share, share.RootEntryId, "B");
        AddFile(share, d1, "x.bin", 50_000);
        AddFile(share, d2, "x.bin", 50_000);
        var group = Assert.Single(Finder().FindOnShares(MinSize));

        Assert.False(group.SetMarked(group.Files[0], true));
        Assert.False(group.MarkAllExceptNewest());
        Assert.False(group.MarkAllExceptIn(@"N:\A"));
        Assert.Equal(0, group.MarkedCount);

        var bin = new RecordingBin();
        group.Files[0].IsMarked = true; // even if something got around the rules
        var report = new DuplicateRecycler(bin, _files).Recycle([group]);

        Assert.Empty(bin.Files);
        Assert.Equal(DuplicateRecycleOutcome.NotAllowed, Assert.Single(report).Outcome);
        Assert.Equal(0, _files.TotalCalls);
    }

    [Fact]
    public void RemovableDrive_IsCompared_ButNotMarkable()
    {
        var stick = _f.Store.AddRoot(@"U:\", null, RootKind.Removable);
        var a = Content.Random(Big, 18);
        File(stick, stick.RootEntryId, @"U:\a.bin", a);
        File(stick, stick.RootEntryId, @"U:\b.bin", (byte[])a.Clone());

        var group = Assert.Single(Finder().FindLocal(MinSize).Groups);

        Assert.All(group.Files, f => Assert.False(f.CanRecycle));
        Assert.False(group.SetMarked(group.Files[0], true));
        Assert.False(group.MarkAllExceptNewest());
    }

    // ---------------------------------------------------------------- marking

    private DuplicateGroup LocalGroup(int count, string[]? folders = null)
    {
        var root = LocalRoot();
        var a = Content.Random(Big, 19);
        var dirIds = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < count; i++)
        {
            string folder = folders?[i] ?? "D";
            if (!dirIds.TryGetValue(folder, out int dir))
            {
                dirIds[folder] = dir = AddDir(root, root.RootEntryId, folder);
            }

            File(root, dir, $@"R:\{folder}\f{i}.bin", (byte[])a.Clone(), ticks: FakeLocalFiles.BaseTicks + i);
        }

        return Assert.Single(Finder().FindLocal(MinSize).Groups);
    }

    [Fact]
    public void NeverAllFilesOfAGroup_CanBeMarked_OneByOne()
    {
        var group = LocalGroup(3);

        Assert.True(group.SetMarked(group.Files[0], true));
        Assert.True(group.SetMarked(group.Files[1], true));
        Assert.False(group.CanMark(group.Files[2]));
        Assert.False(group.SetMarked(group.Files[2], true));
        Assert.False(group.Files[2].IsMarked);
        Assert.Equal(2, group.MarkedCount);

        Assert.True(group.SetMarked(group.Files[0], false));
        Assert.True(group.SetMarked(group.Files[2], true));
        Assert.Equal(1, group.Files.Count(f => !f.IsMarked));
    }

    [Fact]
    public void MarkAllExceptNewest_KeepsTheNewest()
    {
        var group = LocalGroup(3);

        Assert.True(group.MarkAllExceptNewest());

        Assert.Equal([true, true, false], group.Files.Select(f => f.IsMarked));
        Assert.False(group.SetMarked(group.Files[2], true));
        Assert.False(group.MarkAllExceptNewest()); // nothing changes the second time
    }

    [Fact]
    public void MarkAllExceptInFolder_KeepsThoseInTheFolderAndBelow_AndNeverMarksAll()
    {
        var group = LocalGroup(4, ["Keep", @"Keep\Sub", "Other", "Third"]);

        Assert.True(group.MarkAllExceptIn(@"R:\Keep"));
        Assert.Equal([false, false, true, true], group.Files.Select(f => f.IsMarked));

        // A folder without any file of the group: nothing changes (all marked would break the rule).
        Assert.False(group.MarkAllExceptIn(@"R:\Elsewhere"));
        Assert.Equal(2, group.MarkedCount);

        // "R:\Kee" is not the folder "R:\Keep".
        Assert.False(group.MarkAllExceptIn(@"R:\Kee"));
    }

    [Fact]
    public void NoSequenceOfActions_MarksAllFiles()
    {
        var group = LocalGroup(4, ["A", "A", "B", "C"]);
        var random = new Random(1234);
        string[] folders = [@"R:\A", @"R:\B", @"R:\C", @"R:\X", @"R:\"];
        for (int step = 0; step < 2_000; step++)
        {
            switch (random.Next(5))
            {
                case 0:
                    group.SetMarked(group.Files[random.Next(group.Files.Count)], true);
                    break;
                case 1:
                    group.SetMarked(group.Files[random.Next(group.Files.Count)], false);
                    break;
                case 2:
                    group.MarkAllExceptNewest();
                    break;
                case 3:
                    group.MarkAllExceptIn(folders[random.Next(folders.Length)]);
                    break;
                default:
                    DuplicateMarking.MarkAllExceptIn([group], folders[random.Next(folders.Length)]);
                    break;
            }

            Assert.True(group.Files.Any(f => !f.IsMarked), $"all files marked after step {step}");
        }
    }

    [Fact]
    public void MarkedCountAndBytes_AddUpOverGroups()
    {
        var root = LocalRoot();
        var a = Content.Random(Big, 20);
        var b = Content.Random(Big + 10, 21);
        File(root, root.RootEntryId, @"R:\a1.bin", a);
        File(root, root.RootEntryId, @"R:\a2.bin", (byte[])a.Clone(), ticks: FakeLocalFiles.BaseTicks + 1);
        File(root, root.RootEntryId, @"R:\b1.bin", b);
        File(root, root.RootEntryId, @"R:\b2.bin", (byte[])b.Clone());
        File(root, root.RootEntryId, @"R:\b3.bin", (byte[])b.Clone());
        var groups = Finder().FindLocal(MinSize).Groups;

        Assert.Equal(2, DuplicateMarking.MarkAllExceptNewest(groups));

        Assert.Equal(3, DuplicateMarking.MarkedCount(groups));
        Assert.Equal(Big + (2L * (Big + 10)), DuplicateMarking.MarkedBytes(groups));
        DuplicateMarking.ClearMarks(groups);
        Assert.Equal(0, DuplicateMarking.MarkedCount(groups));
    }

    // ---------------------------------------------------------------- moving to the Recycle Bin

    [Fact]
    public void Recycle_MovesMarkedFiles_AndChecksSizeAndTimeAgain()
    {
        var group = LocalGroup(3);
        group.MarkAllExceptNewest(); // f0 and f1 marked, f2 kept
        _files.Change(@"R:\D\f1.bin", ticks: FakeLocalFiles.BaseTicks + 99); // touched after the comparison
        var bin = new RecordingBin();
        var moved = new List<string>();

        var report = new DuplicateRecycler(bin, _files).Recycle([group], f => moved.Add(f.FullPath));

        Assert.Equal([@"R:\D\f0.bin"], bin.Files);
        Assert.Equal([@"R:\D\f0.bin"], moved);
        Assert.Equal(DuplicateRecycleOutcome.Moved, report.Single(r => r.File.FullPath == @"R:\D\f0.bin").Outcome);
        Assert.Equal(DuplicateRecycleOutcome.Changed, report.Single(r => r.File.FullPath == @"R:\D\f1.bin").Outcome);
        Assert.Equal([@"R:\D\f1.bin", @"R:\D\f2.bin"], Paths(group)); // the moved file left the group
    }

    [Fact]
    public void Recycle_SkipsAGroupWhoseKeptCopyChanged()
    {
        var group = LocalGroup(2);
        group.MarkAllExceptNewest(); // f0 marked, f1 kept
        _files.Remove(@"R:\D\f1.bin"); // the kept copy is gone
        var bin = new RecordingBin();

        var report = new DuplicateRecycler(bin, _files).Recycle([group]);

        Assert.Empty(bin.Files);
        Assert.Equal(DuplicateRecycleOutcome.KeptCopyChanged, Assert.Single(report).Outcome);
    }

    [Fact]
    public void Recycle_ReportsAFailure_AndGoesOn()
    {
        var group = LocalGroup(3);
        group.MarkAllExceptNewest();
        var bin = new RecordingBin
        {
            OnMove = p =>
            {
                if (p.EndsWith("f0.bin", StringComparison.Ordinal))
                {
                    throw new IOException("in use");
                }
            },
        };

        var report = new DuplicateRecycler(bin, _files).Recycle([group]);

        Assert.Equal([@"R:\D\f1.bin"], bin.Files);
        var failed = report.Single(r => r.Outcome == DuplicateRecycleOutcome.Failed);
        Assert.Equal("in use", failed.Error);
        Assert.True(failed.File.IsMarked); // still in the group, still marked
    }

    [Fact]
    public void ForgetIfVanished_RemovesTheEntry_OnlyWhenTheFileIsReallyGone()
    {
        var group = LocalGroup(2);
        var file = group.Files[0];

        Assert.False(DuplicateRecycler.ForgetIfVanished(_f.Store, file, new SetProbe(@"R:\", @"R:\D", file.FullPath)));
        Assert.NotNull(_f.Store.GetEntry(file.EntryId));
        Assert.False(DuplicateRecycler.ForgetIfVanished(_f.Store, file, new SetProbe(@"R:\"))); // parent unknown: unclear
        Assert.NotNull(_f.Store.GetEntry(file.EntryId));

        Assert.True(DuplicateRecycler.ForgetIfVanished(_f.Store, file, new SetProbe(@"R:\", @"R:\D")));

        Assert.Null(_f.Store.GetEntry(file.EntryId));
        Assert.Equal(1, HashRows()); // its hash went with it
    }

    // ---------------------------------------------------------------- the hashes table

    [Fact]
    public void Hashes_OfDeletedEntries_DoNotStay()
    {
        var root = LocalRoot();
        int dir = AddDir(root, root.RootEntryId, "D");
        int a = AddFile(root, dir, "a.bin", 5);
        int b = AddFile(root, root.RootEntryId, "b.bin", 5);
        int c = AddFile(root, root.RootEntryId, "c.bin", 5);
        foreach (int id in new[] { a, b, c })
        {
            Assert.True(_f.Store.SaveHash(new StoredHash(id, 5, 1, [1], [2])));
        }

        _f.Store.DeleteSubtree(dir); // a with its folder
        Assert.Equal(2, HashRows());

        var batch = new ChangeBatch();
        batch.DeleteSubtrees.Add(b); // as the scanner does it
        _f.Store.Apply(root.Id, batch);
        Assert.Equal(1, HashRows());

        _f.Store.ResetAllEntries();
        Assert.Equal(0, HashRows());

        Assert.False(_f.Store.SaveHash(new StoredHash(c, 5, 1, [1], null))); // the entry is gone: nothing is written
        Assert.Equal(0, HashRows());
    }

    [Fact]
    public void Hashes_GoWithARemovedLocation()
    {
        var root = LocalRoot();
        int a = AddFile(root, root.RootEntryId, "a.bin", 5);
        _f.Store.SaveHash(new StoredHash(a, 5, 1, [1], null));

        _f.Store.RemoveRoot(root.Id);

        Assert.Equal(0, HashRows());
    }

    [Fact]
    public void StoredHash_IsValidOnlyForTheSameSizeAndTime()
    {
        var hash = new StoredHash(1, 100, 555, [1], null);

        Assert.True(hash.IsValidFor(100, 555));
        Assert.False(hash.IsValidFor(101, 555));
        Assert.False(hash.IsValidFor(100, 556));
    }

    // ---------------------------------------------------------------- settings

    [Theory]
    [InlineData(0, AppSettings.DuplicateMinSizeDefault)]
    [InlineData(-5, AppSettings.DuplicateMinSizeDefault)]
    [InlineData(10, AppSettings.DuplicateMinSizeLowest)]
    [InlineData(5_000_000, 5_000_000)]
    [InlineData(long.MaxValue, AppSettings.DuplicateMinSizeHighest)]
    public void MinimumSize_IsSanitized(long stored, long expected)
    {
        Assert.Equal(expected, (new AppSettings { DuplicateMinSize = stored }).Sanitized().DuplicateMinSize);
    }

    [Fact]
    public void MinimumSize_DefaultsTo1MB_AndIsStored()
    {
        Assert.Equal(1024 * 1024, new AppSettings().DuplicateMinSize);
        string path = Path.Combine(_dir.Path, "settings.json");
        var service = new SettingsService(path);
        service.Load();
        service.Update(s => s with { DuplicateMinSize = 10 * 1024 * 1024 });

        var reloaded = new SettingsService(path);
        reloaded.Load();
        Assert.Equal(10 * 1024 * 1024, reloaded.Current.DuplicateMinSize);
        Assert.Contains("\"duplicateMinSize\"", System.IO.File.ReadAllText(path), StringComparison.Ordinal);
    }
}
