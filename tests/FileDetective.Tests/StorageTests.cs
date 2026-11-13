using FileDetective.Core;
using Microsoft.Data.Sqlite;

namespace FileDetective.Tests;

public class StorageTests : IDisposable
{
    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    private StorageFixture NewFixture() => new(_dir);

    /// <summary>C:\Users\anna\Müller\Angebot_A.pdf, ...\Notizen.txt, C:\Users\anna\Archiv\Angebot Müller alt.pdf</summary>
    private static (RootInfo Root, int Mueller, int Archiv) BuildSample(StorageFixture f)
    {
        var root = f.Store.AddRoot(@"C:\Users\anna", null, RootKind.Local);
        int mueller = f.Add(root, root.RootEntryId, "Müller", true, 100);
        f.Add(root, mueller, "Angebot_A.pdf", false, 200, 1234);
        f.Add(root, mueller, "Notizen.txt", false, 300, 10);
        int archiv = f.Add(root, root.RootEntryId, "Archiv", true, 400);
        f.Add(root, archiv, "Angebot Müller alt.pdf", false, 500, 99);
        return (root, mueller, archiv);
    }

    [Fact]
    public void WriteCloseReload_GivesSameHits()
    {
        using var f = NewFixture();
        BuildSample(f);
        string[] queries = ["angebot", "mueller", "müller .pdf", "notizen", "archiv angebot", "anna", "xyzzy"];
        var before = queries.Select(q => f.Paths(q)).ToArray();
        Assert.Contains(@"C:\Users\anna\Müller\Angebot_A.pdf", before[0]);
        Assert.Equal(6, f.Index.Count); // root entry + 5 entries
        int count = f.Index.Count;

        f.Restart();
        Assert.Equal(0, f.Index.Count);
        Assert.Equal(count, f.Load());
        var after = queries.Select(q => f.Paths(q)).ToArray();

        for (int i = 0; i < queries.Length; i++)
        {
            Assert.Equal(before[i], after[i]);
        }

        Assert.True(f.Store.IsLoaded);
    }

    [Fact]
    public void DeleteSubtree_RemovesAllDescendantsInDatabaseAndMemory()
    {
        using var f = NewFixture();
        var (root, mueller, archiv) = BuildSample(f);
        int deep = f.Add(root, mueller, "Deep", true);
        int deeper = f.Add(root, deep, "Deeper", true);
        int leaf = f.Add(root, deeper, "angebot leaf.txt", false);
        int before = f.Index.Count;

        int removed = f.Store.DeleteSubtree(mueller);

        Assert.Equal(6, removed); // Müller, 2 files, Deep, Deeper, leaf
        Assert.Equal(before - 6, f.Index.Count);
        Assert.DoesNotContain(f.Paths("angebot"), p => p.Contains("Müller\\", StringComparison.Ordinal));
        Assert.Single(f.Paths("angebot"));
        Assert.Empty(f.Store.GetKnownChildren(mueller));
        Assert.Empty(f.Store.GetKnownChildren(deep));
        Assert.Null(f.Store.FindEntryId(@"C:\Users\anna\Müller\Deep\Deeper"));
        Assert.All(
            new DatabaseEntryResolver(f.Db).Resolve([mueller, deep, deeper, leaf], default), d => Assert.Null(d));
        Assert.NotNull(f.Store.FindEntryId(@"C:\Users\anna\Archiv"));

        // The same state after a restart proves that the database lost the rows as well.
        f.Restart();
        Assert.Equal(before - 6, f.Load());
        Assert.Single(f.Paths("angebot"));
        Assert.Equal(archiv, f.Store.FindEntryId(@"C:\Users\anna\archiv"));
    }

    [Fact]
    public void ForeignSchemaVersion_IsRenamedAndRecreated()
    {
        using (var db = IndexDatabase.Open(_dir.DbPath))
        {
            using var c = db.OpenConnection();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "UPDATE meta SET value = '99' WHERE key = 'schema_version'; INSERT INTO usage VALUES('x', 1, 1)";
            cmd.ExecuteNonQuery();
        }

        SqliteConnection.ClearAllPools();
        using var fresh = IndexDatabase.Open(_dir.DbPath);

        Assert.NotNull(fresh.BackupPath);
        Assert.True(File.Exists(fresh.BackupPath));
        Assert.StartsWith(_dir.DbPath + ".bak-", fresh.BackupPath, StringComparison.Ordinal);
        using var c2 = fresh.OpenConnection();
        using var cmd2 = c2.CreateCommand();
        cmd2.CommandText = "SELECT value FROM meta WHERE key = 'schema_version'";
        Assert.Equal("1", cmd2.ExecuteScalar());
        cmd2.CommandText = "SELECT count(*) FROM usage";
        Assert.Equal(0L, cmd2.ExecuteScalar());
    }

    [Fact]
    public void CorruptFile_IsRenamedAndRecreated()
    {
        File.WriteAllBytes(_dir.DbPath, [.. Enumerable.Range(0, 5000).Select(i => (byte)(i * 31 + 7))]);

        using var db = IndexDatabase.Open(_dir.DbPath);

        Assert.NotNull(db.BackupPath);
        Assert.Equal(5000, new FileInfo(db.BackupPath!).Length);
        using var store = new IndexStore(db, new MemoryIndex());
        Assert.Empty(store.GetRoots());
    }

    [Fact]
    public void FreshDatabase_HasSchemaWalAndNormalSync()
    {
        using var db = IndexDatabase.Open(_dir.DbPath);
        Assert.Null(db.BackupPath);
        using var c = db.OpenConnection();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "PRAGMA journal_mode";
        Assert.Equal("wal", cmd.ExecuteScalar());
        cmd.CommandText = "PRAGMA synchronous";
        Assert.Equal(1L, cmd.ExecuteScalar()); // NORMAL
        cmd.CommandText = "SELECT group_concat(name) FROM (SELECT name FROM sqlite_master WHERE type IN ('table','index') ORDER BY name)";
        var names = ((string)cmd.ExecuteScalar()!).Split(',');
        Assert.Contains("meta", names);
        Assert.Contains("roots", names);
        Assert.Contains("entries", names);
        Assert.Contains("usage", names);
        Assert.Contains("ix_entries_parent", names);
    }

    [Fact]
    public void ReopeningHealthyDatabase_KeepsFile()
    {
        using (var f = NewFixture())
        {
            BuildSample(f);
        }

        using var db = IndexDatabase.Open(_dir.DbPath);
        Assert.Null(db.BackupPath);
        Assert.Empty(Directory.GetFiles(_dir.Path, "*.bak-*"));
    }

    [Fact]
    public void EntryNumbers_ContinueAfterRestart_AndAreNeverReused()
    {
        using var f = NewFixture();
        var root = f.Store.AddRoot(@"C:\X", null, RootKind.Local);
        int a = f.Add(root, root.RootEntryId, "a", true);
        int b = f.Add(root, root.RootEntryId, "b", true);
        Assert.True(b > a);
        f.Store.DeleteSubtree(b); // the highest number is gone from the table
        int highest = f.Store.LastAllocatedId;
        Assert.Equal(b, highest);

        f.Restart();
        Assert.Equal(b, f.Store.LastAllocatedId);
        int c = f.Store.AllocateId();
        Assert.Equal(b + 1, c);
        Assert.Equal(c + 1, f.Store.AllocateId());
    }

    [Fact]
    public void RenameAndMove_UpdateDatabaseAndMemory()
    {
        using var f = NewFixture();
        var (root, mueller, archiv) = BuildSample(f);

        Assert.True(f.Store.Rename(mueller, "Schmidt"));
        Assert.Empty(f.Paths("mueller .txt"));
        Assert.Equal([@"C:\Users\anna\Schmidt\Notizen.txt"], f.Paths("schmidt notizen"));

        Assert.True(f.Store.Move(mueller, archiv));
        Assert.Equal([@"C:\Users\anna\Archiv\Schmidt\Notizen.txt"], f.Paths("notizen"));

        Assert.True(f.Store.MoveAndRename(mueller, root.RootEntryId, "Meier"));
        Assert.Equal([@"C:\Users\anna\Meier\Notizen.txt"], f.Paths("notizen"));
        Assert.Equal(mueller, f.Store.FindEntryId(@"C:\Users\anna\Meier"));

        f.Restart();
        f.Load();
        Assert.Equal([@"C:\Users\anna\Meier\Notizen.txt"], f.Paths("notizen"));
        Assert.False(f.Store.Rename(99999, "x"));
    }

    [Fact]
    public void Move_RejectsCycleRootEntryAndOtherLocation()
    {
        using var f = NewFixture();
        var (root, mueller, _) = BuildSample(f);
        int child = f.Add(root, mueller, "sub", true);
        var other = f.Store.AddRoot(@"D:\Data", null, RootKind.Local);

        Assert.Throws<ArgumentException>(() => f.Store.Move(mueller, child));
        Assert.Throws<ArgumentException>(() => f.Store.Move(mueller, mueller));
        Assert.Throws<ArgumentException>(() => f.Store.Move(mueller, other.RootEntryId));
        Assert.Throws<InvalidOperationException>(() => f.Store.Rename(root.RootEntryId, "x"));
        Assert.Throws<InvalidOperationException>(() => f.Store.DeleteSubtree(root.RootEntryId));
    }

    [Fact]
    public void RemoveRoot_RemovesAllEntriesOfThatLocationOnly()
    {
        using var f = NewFixture();
        var (rootA, _, _) = BuildSample(f);
        var rootB = f.Store.AddRoot(@"\\srv\share\proj", @"\\srv\share\proj", RootKind.Network);
        int d = f.Add(rootB, rootB.RootEntryId, "Angebot Ordner", true);
        f.Add(rootB, d, "angebot.pdf", false);
        Assert.Equal(2, f.Store.GetRoots().Count);
        int countA = f.Index.Count - 3;

        int removed = f.Store.RemoveRoot(rootB.Id);

        Assert.Equal(3, removed);
        Assert.Equal(countA, f.Index.Count);
        Assert.All(f.Paths("angebot"), p => Assert.StartsWith(@"C:\", p, StringComparison.Ordinal));
        Assert.Single(f.Store.GetRoots());
        Assert.Null(f.Store.GetRoot(rootB.Id));
        Assert.Equal(0, f.Store.RemoveRoot(rootB.Id));

        f.Restart();
        Assert.Equal(countA, f.Load());

        // The freed location number is used again, with a new entry number.
        var again = f.Store.AddRoot(@"E:\New", null, RootKind.Removable);
        Assert.Equal(rootB.Id, again.Id);
        Assert.True(again.RootEntryId > rootB.RootEntryId);
        Assert.Equal(rootA.Id, f.Store.GetRoots()[0].Id);
    }

    [Fact]
    public void Roots_CanBeAddedReadAndUpdated()
    {
        using var f = NewFixture();
        var root = f.Store.AddRoot(@"P:\Projekte", @"\\srv\proj", RootKind.Network);
        Assert.Equal(1, root.Id);
        Assert.Throws<InvalidOperationException>(() => f.Store.AddRoot(@"p:\projekte", null, RootKind.Local));

        var read = f.Store.GetRoot(root.Id)!;
        Assert.Equal(root, read);
        Assert.Equal(ScanStatus.Never, read.LastScanStatus);
        Assert.True(read.Enabled);

        var changed = read with
        {
            Enabled = false,
            LastScanStartedUtc = 1000,
            LastScanCompletedUtc = 2000,
            LastScanStatus = ScanStatus.CompleteWithErrors,
            LastScanFailedDirs = 3,
            AttemptsToday = 2,
        };
        Assert.True(f.Store.UpdateRoot(changed));
        Assert.False(f.Store.UpdateRoot(changed with { Id = 77 }));

        f.Restart();
        Assert.Equal(changed, f.Store.GetRoot(1));
    }

    [Fact]
    public void KnownChildren_AndApply_SupportTheScannersComparison()
    {
        using var f = NewFixture();
        var root = f.Store.AddRoot(@"C:\R", null, RootKind.Local);
        int a = f.Add(root, root.RootEntryId, "a.txt", false, 10, 5);
        int dir = f.Add(root, root.RootEntryId, "dir", true, 20);
        f.Add(root, dir, "inner.txt", false, 30, 1);

        var kids = f.Store.GetKnownChildren(root.RootEntryId).OrderBy(k => k.Name).ToList();
        Assert.Equal(2, kids.Count);
        Assert.Equal(new KnownChild(a, "a.txt", false, 5, 10, FileAttributes.Normal), kids[0]);
        Assert.True(kids[1].IsDirectory);

        // One batch with insert, update and subtree delete.
        int fresh = f.Store.AllocateId();
        int freshChild = f.Store.AllocateId();
        var batch = new ChangeBatch();
        batch.Inserts.Add(new EntryInsert(freshChild, fresh, "child.doc", false, 7, 70, FileAttributes.Archive)); // child before parent in the list
        batch.Inserts.Add(new EntryInsert(fresh, root.RootEntryId, "neu", true, 0, 60, FileAttributes.Directory));
        batch.Updates.Add(new EntryUpdate(a, 99, 1234, FileAttributes.ReadOnly));
        batch.DeleteSubtrees.Add(dir);
        var result = f.Store.Apply(root.Id, batch);

        Assert.Equal(new ApplyResult(2, 1, 2), result);
        var after = f.Store.GetKnownChildren(root.RootEntryId).OrderBy(k => k.Name).ToList();
        Assert.Equal(["a.txt", "neu"], after.Select(k => k.Name));
        Assert.Equal(new KnownChild(a, "a.txt", false, 99, 1234, FileAttributes.ReadOnly), after[0]);
        Assert.Equal([@"C:\R\neu\child.doc"], f.Paths("child"));
        Assert.Empty(f.Paths("inner"));
        Assert.Equal(1234.0, f.Engine.Search("a.txt", SearchMode.All).Hits[0].Details.LastWriteUtc.Subtract(DateTime.UnixEpoch).TotalSeconds);
    }

    [Fact]
    public void Apply_FailingBatch_ChangesNeitherDatabaseNorMemory()
    {
        using var f = NewFixture();
        var root = f.Store.AddRoot(@"C:\R", null, RootKind.Local);
        int existing = f.Add(root, root.RootEntryId, "keep.txt", false);
        int count = f.Index.Count;

        int ok = f.Store.AllocateId();
        var batch = new ChangeBatch();
        batch.Inserts.Add(new EntryInsert(ok, root.RootEntryId, "new.txt", false, 1, 1, FileAttributes.Normal));
        batch.Inserts.Add(new EntryInsert(existing, root.RootEntryId, "dup.txt", false, 1, 1, FileAttributes.Normal)); // primary key clash
        Assert.ThrowsAny<Exception>(() => f.Store.Apply(root.Id, batch));

        Assert.Equal(count, f.Index.Count);
        Assert.Single(f.Store.GetKnownChildren(root.RootEntryId));
        Assert.Empty(f.Paths("new"));

        var unallocated = new ChangeBatch();
        unallocated.Inserts.Add(new EntryInsert(f.Store.LastAllocatedId + 5, root.RootEntryId, "x", false, 0, 0, FileAttributes.Normal));
        Assert.Throws<ArgumentException>(() => f.Store.Apply(root.Id, unallocated));
    }

    [Fact]
    public void Resolver_ReturnsPathAndDisplayData()
    {
        using var f = NewFixture();
        var root = f.Store.AddRoot(@"C:\", null, RootKind.Local); // root path with trailing backslash
        int dir = f.Add(root, root.RootEntryId, "Docs", true, 50);
        int file = f.Add(root, dir, "Bericht.PDF", false, 1_700_000_000, 4321);
        var resolver = new DatabaseEntryResolver(f.Db);

        var d = resolver.Resolve([file, 424242, root.RootEntryId, dir], default);

        Assert.Equal(@"C:\Docs\Bericht.PDF", d[0]!.FullPath);
        Assert.Equal("Bericht.PDF", d[0]!.Name);
        Assert.False(d[0]!.IsDirectory);
        Assert.Equal(4321, d[0]!.Size);
        Assert.Equal(new DateTime(2023, 11, 14, 22, 13, 20, DateTimeKind.Utc), d[0]!.LastWriteUtc);
        Assert.Equal(FileAttributes.Normal, d[0]!.Attributes);
        Assert.Null(d[1]);
        Assert.Equal(@"C:\", d[2]!.FullPath);
        Assert.True(d[2]!.IsDirectory);
        Assert.Equal(@"C:\Docs", d[3]!.FullPath);
        Assert.Equal(FileAttributes.Directory, d[3]!.Attributes);
        Assert.Empty(resolver.Resolve([], default));
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.Throws<OperationCanceledException>(() => resolver.Resolve([file], cts.Token));
    }

    [Fact]
    public void Resolver_ParentWithHigherNumberThanChildStillResolves()
    {
        using var f = NewFixture();
        var root = f.Store.AddRoot(@"C:\R", null, RootKind.Local);
        int child = f.Store.AllocateId();
        int parent = f.Store.AllocateId();
        var batch = new ChangeBatch();
        batch.Inserts.Add(new EntryInsert(child, parent, "x.txt", false, 0, 0, FileAttributes.Normal));
        batch.Inserts.Add(new EntryInsert(parent, root.RootEntryId, "late", true, 0, 0, FileAttributes.Directory));
        f.Store.Apply(root.Id, batch);

        Assert.Equal([@"C:\R\late\x.txt"], f.Paths("x.txt"));
    }

    [Fact]
    public void UsageTracker_RecordsOpensAndAnswersByEntryNumber()
    {
        using var f = NewFixture();
        var (root, mueller, _) = BuildSample(f);
        int a = f.Store.FindEntryId(@"C:\Users\anna\Müller\Angebot_A.pdf")!.Value;

        Assert.False(f.Usage.TryGetLastOpened(a, out _));
        f.Usage.RecordOpen(@"c:\users\ANNA\müller\angebot_a.PDF"); // different case
        f.Usage.RecordOpen(@"C:\Users\anna\Müller\Angebot_A.pdf");

        Assert.True(f.Usage.TryGetLastOpened(a, out long t));
        Assert.InRange(t, DateTimeOffset.UtcNow.AddMinutes(-1).ToUnixTimeSeconds(), DateTimeOffset.UtcNow.AddMinutes(1).ToUnixTimeSeconds());
        Assert.Equal(2, f.Usage.GetOpenCount(@"C:\Users\anna\Müller\Angebot_A.pdf"));
        Assert.False(f.Usage.TryGetLastOpened(mueller, out _));
        Assert.Equal(root.RootEntryId + 2, a);
    }

    [Fact]
    public void UsageTracker_SurvivesRestart_AndInfluencesRanking()
    {
        using var f = NewFixture();
        var root = f.Store.AddRoot(@"C:\R", null, RootKind.Local);
        f.Add(root, root.RootEntryId, "report old.txt", false, 100);
        f.Add(root, root.RootEntryId, "report new.txt", false, 900); // newer, but never opened
        Assert.Equal(@"C:\R\report new.txt", f.Paths("report")[0]);

        f.Usage.RecordOpen(@"C:\R\report old.txt");
        Assert.Equal(@"C:\R\report old.txt", f.Paths("report")[0]);

        f.Restart();
        f.Load();
        Assert.Equal(1, f.Usage.MappedCount);
        Assert.Equal(@"C:\R\report old.txt", f.Paths("report")[0]);
    }

    [Fact]
    public void UsageTracker_ReloadPicksUpEntriesThatAppearedLater_AndIsSafeToReadInParallel()
    {
        using var f = NewFixture();
        var root = f.Store.AddRoot(@"C:\R", null, RootKind.Local);
        f.Usage.RecordOpen(@"C:\R\later.txt"); // not in the directory yet
        Assert.Equal(0, f.Usage.MappedCount);

        int id = f.Add(root, root.RootEntryId, "later.txt", false);
        f.Usage.Reload();
        Assert.True(f.Usage.TryGetLastOpened(id, out _));

        var errors = 0;
        Parallel.For(0, 8, w =>
        {
            for (int i = 0; i < 2000; i++)
            {
                if (w == 0 && i % 100 == 0)
                {
                    f.Usage.Reload();
                }
                else if (!f.Usage.TryGetLastOpened(id, out _))
                {
                    Interlocked.Increment(ref errors);
                }
            }
        });
        Assert.Equal(0, errors);
    }

    [Fact]
    public void FindEntryId_ResolvesPathsCaseInsensitively()
    {
        using var f = NewFixture();
        var (root, mueller, _) = BuildSample(f);

        Assert.Equal(root.RootEntryId, f.Store.FindEntryId(@"C:\Users\anna"));
        Assert.Equal(root.RootEntryId, f.Store.FindEntryId(@"c:\users\ANNA\"));
        Assert.Equal(mueller, f.Store.FindEntryId(@"C:\USERS\anna\MÜLLER"));
        Assert.Null(f.Store.FindEntryId(@"C:\Users\anna2"));
        Assert.Null(f.Store.FindEntryId(@"C:\Users\anna\nope"));
        Assert.Null(f.Store.FindEntryId(@"D:\x"));
    }
}
