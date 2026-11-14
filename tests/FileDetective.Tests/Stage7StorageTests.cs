using System.Globalization;
using FileDetective.Core;
using Microsoft.Data.Sqlite;

namespace FileDetective.Tests;

/// <summary>Renumbering at start (PLAN.md 6.3), rebuilding the directory (8.5) and recently opened entries (6.4).</summary>
public class Stage7StorageTests : IDisposable
{
    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    private StorageFixture NewFixture() => new(_dir);

    // ---------------------------------------------------------------- helpers

    private static string? Meta(IndexDatabase db, string key)
    {
        using var c = db.OpenConnection();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT value FROM meta WHERE key = @k";
        cmd.Parameters.AddWithValue("@k", key);
        return cmd.ExecuteScalar() as string;
    }

    private static void SetMeta(IndexDatabase db, string key, string value)
    {
        using var c = db.OpenConnection();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "INSERT OR REPLACE INTO meta(key, value) VALUES(@k, @v)";
        cmd.Parameters.AddWithValue("@k", key);
        cmd.Parameters.AddWithValue("@v", value);
        cmd.ExecuteNonQuery();
    }

    private static void Exec(IndexDatabase db, string sql)
    {
        using var c = db.OpenConnection();
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    /// <summary>All (id, parent, name) rows.</summary>
    private static List<(int Id, int Parent, string Name)> Rows(IndexDatabase db)
    {
        var rows = new List<(int, int, string)>();
        using var c = db.OpenConnection();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT id, parent_id, name FROM entries ORDER BY id";
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            rows.Add((r.GetInt32(0), r.GetInt32(1), r.GetString(2)));
        }

        return rows;
    }

    /// <summary>The full path of every entry as the resolver builds it, sorted; the numbers are irrelevant.</summary>
    private static string[] AllPaths(IndexDatabase db)
    {
        var rows = Rows(db);
        var ids = rows.Select(r => r.Id).ToArray();
        var details = new DatabaseEntryResolver(db).Resolve(ids, default);
        return [.. details.Where(d => d is not null).Select(d => d!.FullPath).OrderBy(p => p, StringComparer.Ordinal)];
    }

    /// <summary>
    /// Builds a directory with gaps: two locations, folders and files, then deletes and re-adds so that the numbers
    /// reach far above the entry count. Returns the number of entries.
    /// </summary>
    private static int BuildSparse(StorageFixture f, int churn = 120_000)
    {
        var a = f.Store.AddRoot(@"C:\Users\anna", null, RootKind.Local);
        var b = f.Store.AddRoot(@"P:\Projekte", @"\\srv\projekte", RootKind.Network);

        // Parents with higher numbers than their children are covered by moving later.
        int docs = f.Add(a, a.RootEntryId, "Dokumente", true, 10);
        int mueller = f.Add(a, docs, "Müller", true, 20);
        f.Add(a, mueller, "Angebot Müller.pdf", false, 30, 100);
        f.Add(a, mueller, "Notizen.txt", false, 40, 5);
        int proj = f.Add(b, b.RootEntryId, "Alpha", true, 50);
        int deep = f.Add(b, proj, "src", true, 60);
        f.Add(b, deep, "main.cs", false, 70, 1);

        // Churn: allocate many numbers and drop the entries again (one folder with files, deleted).
        int tmp = f.Add(a, a.RootEntryId, "tmp", true);
        var batch = new ChangeBatch();
        for (int i = 0; i < churn; i++)
        {
            batch.Inserts.Add(new EntryInsert(f.Store.AllocateId(), tmp, "t" + i.ToString(CultureInfo.InvariantCulture), false, 0, 0, FileAttributes.Normal));
            if (batch.Inserts.Count == 5000)
            {
                f.Store.Apply(a.Id, batch);
                batch = new ChangeBatch();
            }
        }

        f.Store.Apply(a.Id, batch);
        f.Store.DeleteSubtree(tmp);

        // A few entries after the gap.
        int late = f.Add(a, docs, "Spät.txt", false, 80, 7);
        _ = late;
        return Rows(f.Db).Count;
    }

    // ---------------------------------------------------------------- trigger

    [Theory]
    [InlineData(0, 0, false)]
    [InlineData(100_000, 0, false)]
    [InlineData(100_001, 0, true)]
    [InlineData(1_000_000, 500_000, true)]  // > 850,000
    [InlineData(850_000, 500_000, false)]   // not above 1.5 x 500,000 + 100,000
    [InlineData(850_001, 500_000, true)]
    [InlineData(10, 10, false)]
    public void IsNeeded_FollowsThePlanFormula(long maxId, long count, bool expected)
        => Assert.Equal(expected, DirectoryRenumbering.IsNeeded(maxId, count));

    [Fact]
    public void NothingToDo_WhenNumbersAreDense()
    {
        using var f = NewFixture();
        var root = f.Store.AddRoot(@"C:\x", null, RootKind.Local);
        f.Add(root, root.RootEntryId, "a", false);
        int before = Rows(f.Db).Max(r => r.Id);

        var result = DirectoryRenumbering.RenumberIfNeeded(f.Db);

        Assert.False(result.Performed);
        Assert.Equal(before, Rows(f.Db).Max(r => r.Id));
    }

    // ---------------------------------------------------------------- renumbering

    [Fact]
    public void Renumber_MakesNumbersDense_KeepsEveryPath_AndSearchResults()
    {
        string[] queries = ["angebot", "mueller", "müller .pdf", "notizen", "main", "alpha src", "spaet", "spät", "dokumente", "anna", "xyzzy"];
        string[] pathsBefore;
        string[][] hitsBefore;
        int count;
        using (var f = NewFixture())
        {
            count = BuildSparse(f);
            Assert.True(f.Store.LastAllocatedId > 120_000);
            Assert.True(DirectoryRenumbering.IsNeeded(f.Store.LastAllocatedId, count));
            pathsBefore = AllPaths(f.Db);
            f.Load();
            hitsBefore = [.. queries.Select(q => f.Paths(q))];
            Assert.Contains(@"C:\Users\anna\Dokumente\Müller\Angebot Müller.pdf", hitsBefore[0]);
        }

        using (var f = NewFixture())
        {
            var result = DirectoryRenumbering.RenumberIfNeeded(f.Db);

            Assert.True(result.Performed);
            Assert.Equal(count, result.EntryCountBefore);
            Assert.Equal(count, result.MaxIdAfter);
            Assert.Equal(0, result.DroppedUnreachable);
            Assert.True(result.MaxIdBefore > 120_000);

            var rows = Rows(f.Db);
            Assert.Equal(count, rows.Count);
            Assert.Equal(Enumerable.Range(1, count), rows.Select(r => r.Id)); // dense 1..n
            Assert.Equal(count.ToString(CultureInfo.InvariantCulture), Meta(f.Db, "max_id"));

            // The parent chains are intact: every parent exists, roots have 0, no cycles.
            var ids = rows.Select(r => r.Id).ToHashSet();
            Assert.All(rows, r => Assert.True(r.Parent == 0 || ids.Contains(r.Parent)));
            Assert.Equal(2, rows.Count(r => r.Parent == 0));
            Assert.Equal(pathsBefore, AllPaths(f.Db));
        }

        // After a restart the store continues with the compact numbers, the search gives the same paths.
        using (var f = NewFixture())
        {
            Assert.Equal(count, f.Store.LastAllocatedId);
            Assert.Equal(count, f.Load());
            for (int i = 0; i < queries.Length; i++)
            {
                Assert.Equal(hitsBefore[i], f.Paths(queries[i]));
            }

            // New entries get numbers behind the compact ones, and a second run changes nothing.
            var root = f.Store.GetRoots()[0];
            int fresh = f.Add(root, root.RootEntryId, "neu.txt", false);
            Assert.Equal(count + 1, fresh);
            Assert.False(DirectoryRenumbering.RenumberIfNeeded(f.Db).Performed);
        }
    }

    [Fact]
    public void Renumber_ParentWithHigherNumberThanChild_StaysConsistent()
    {
        using var f = NewFixture();
        var root = f.Store.AddRoot(@"C:\r", null, RootKind.Local);
        // Child inserted with a low number first, parent later with a higher number (moved below it afterwards).
        int child = f.Add(root, root.RootEntryId, "child.txt", false);
        int folder = f.Add(root, root.RootEntryId, "folder", true);
        Assert.True(f.Store.Move(child, folder));
        for (int i = 0; i < 5; i++)
        {
            f.Store.AllocateId(); // a gap
        }

        int top = f.Add(root, folder, "z.txt", false);
        string[] before = AllPaths(f.Db);

        var result = DirectoryRenumbering.RenumberIfNeeded(f.Db, force: true);

        Assert.True(result.Performed);
        Assert.Equal(before, AllPaths(f.Db));
        Assert.Equal(@"C:\r\folder\child.txt", AllPaths(f.Db).Single(p => p.EndsWith("child.txt", StringComparison.Ordinal)));
        _ = top;
    }

    [Fact]
    public void Renumber_KeepsUsage_AndRecentEntriesStillResolve()
    {
        using (var f = NewFixture())
        {
            BuildSparse(f, churn: 1000);
            f.Usage.RecordOpen(@"C:\Users\anna\Dokumente\Müller\Notizen.txt");
            f.Usage.RecordOpen(@"P:\Projekte\Alpha\src\main.cs");
        }

        using (var f = NewFixture())
        {
            Assert.True(DirectoryRenumbering.RenumberIfNeeded(f.Db, force: true).Performed);
        }

        using (var f = NewFixture())
        {
            f.Load();
            Assert.Equal(1, f.Usage.GetOpenCount(@"c:\users\anna\dokumente\müller\notizen.txt"));
            var recent = f.Engine.RecentlyOpened(SearchMode.All).Hits.Select(h => h.Details.FullPath).ToArray();
            Assert.Equal(2, recent.Length);
            Assert.Contains(@"C:\Users\anna\Dokumente\Müller\Notizen.txt", recent);
            Assert.Contains(@"P:\Projekte\Alpha\src\main.cs", recent);
        }
    }

    [Fact]
    public void Renumber_FailureBeforeCommit_LeavesTheOldDataUntouched()
    {
        string[] pathsBefore;
        List<(int Id, int Parent, string Name)> rowsBefore;
        string? maxIdBefore;
        using (var f = NewFixture())
        {
            BuildSparse(f, churn: 2000);
            pathsBefore = AllPaths(f.Db);
            rowsBefore = Rows(f.Db);
            maxIdBefore = Meta(f.Db, "max_id");
        }

        using (var f = NewFixture())
        {
            Assert.Throws<InvalidOperationException>(
                () => DirectoryRenumbering.RenumberIfNeeded(f.Db, force: true, beforeCommit: () => throw new InvalidOperationException("simulated")));

            Assert.Equal(rowsBefore, Rows(f.Db));
            Assert.Equal(pathsBefore, AllPaths(f.Db));
            Assert.Equal(maxIdBefore, Meta(f.Db, "max_id"));

            // Leftovers of the attempt do not exist, and the next attempt works.
            using (var c = f.Db.OpenConnection())
            using (var cmd = c.CreateCommand())
            {
                cmd.CommandText = "SELECT count(*) FROM sqlite_master WHERE name IN ('entries_new', 'renumber_map')";
                Assert.Equal(0L, cmd.ExecuteScalar());
            }

            Assert.True(DirectoryRenumbering.RenumberIfNeeded(f.Db, force: true).Performed);
            Assert.Equal(pathsBefore, AllPaths(f.Db));
        }
    }

    [Fact]
    public void Renumber_KeepsTheIndexOnTheParentColumn()
    {
        using var f = NewFixture();
        BuildSparse(f, churn: 500);
        DirectoryRenumbering.RenumberIfNeeded(f.Db, force: true);

        using var c = f.Db.OpenConnection();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT count(*) FROM sqlite_master WHERE type = 'index' AND name = 'ix_entries_parent'";
        Assert.Equal(1L, cmd.ExecuteScalar());
    }

    [Fact]
    public void Renumber_DropsRowsWithoutAWayToTheirRoot_AndNothingElse()
    {
        string[] pathsBefore;
        using (var f = NewFixture())
        {
            BuildSparse(f, churn: 100);
            pathsBefore = AllPaths(f.Db);

            // Damage: an orphan, a child of the orphan, and a two-element cycle.
            Exec(f.Db, "INSERT INTO entries VALUES(900000, 899999, 1, 'orphan', 1, 0, 0, 16)");
            Exec(f.Db, "INSERT INTO entries VALUES(900001, 900000, 1, 'orphan child', 0, 0, 0, 0)");
            Exec(f.Db, "INSERT INTO entries VALUES(900002, 900003, 1, 'cycle a', 1, 0, 0, 16)");
            Exec(f.Db, "INSERT INTO entries VALUES(900003, 900002, 1, 'cycle b', 1, 0, 0, 16)");
        }

        using (var f = NewFixture())
        {
            var result = DirectoryRenumbering.RenumberIfNeeded(f.Db);

            Assert.True(result.Performed);
            Assert.Equal(4, result.DroppedUnreachable);
            Assert.Equal(pathsBefore, AllPaths(f.Db));
            var rows = Rows(f.Db);
            Assert.DoesNotContain(rows, r => r.Name.StartsWith("orphan", StringComparison.Ordinal) || r.Name.StartsWith("cycle", StringComparison.Ordinal));
        }
    }

    [Fact]
    public void Renumber_OnAnEmptyOrNewDatabase_DoesNothing()
    {
        using var f = NewFixture();
        Assert.False(DirectoryRenumbering.RenumberIfNeeded(f.Db).Performed);
        // Forced on an empty table it still works.
        var forced = DirectoryRenumbering.RenumberIfNeeded(f.Db, force: true);
        Assert.True(forced.Performed);
        Assert.Equal(0, forced.MaxIdAfter);
    }

    [Fact]
    public void Renumber_BigDirectory_IsFast()
    {
        // 300,000 entries with a gap: renumbering is one transaction of set-based SQL and must not take long.
        using (var f = NewFixture())
        {
            var root = f.Store.AddRoot(@"C:\big", null, RootKind.Local);
            int folder = f.Add(root, root.RootEntryId, "f", true);
            var batch = new ChangeBatch();
            for (int i = 0; i < 300_000; i++)
            {
                batch.Inserts.Add(new EntryInsert(f.Store.AllocateId(), folder, "file" + i.ToString(CultureInfo.InvariantCulture), false, 1, 1, FileAttributes.Normal));
                if (batch.Inserts.Count == 20_000)
                {
                    f.Store.Apply(root.Id, batch);
                    batch = new ChangeBatch();
                }
            }

            f.Store.Apply(root.Id, batch);
            SetMeta(f.Db, "max_id", "2000000");
            Exec(f.Db, "UPDATE entries SET id = id + 1000000 WHERE id > 100000");
            Exec(f.Db, "UPDATE entries SET parent_id = parent_id + 1000000 WHERE parent_id > 100000");
        }

        using (var f = NewFixture())
        {
            var timer = System.Diagnostics.Stopwatch.StartNew();
            var result = DirectoryRenumbering.RenumberIfNeeded(f.Db);
            timer.Stop();

            Assert.True(result.Performed);
            Assert.Equal(300_002, result.MaxIdAfter);
            Assert.True(timer.Elapsed < TimeSpan.FromSeconds(30), $"took {timer.Elapsed}");
            f.Load();
            Assert.Equal(300_002, f.Index.Count);
            Assert.Equal(300_002, f.Index.MaxId);
        }
    }

    // ---------------------------------------------------------------- rebuilding

    [Fact]
    public void ResetAllEntries_EmptiesEveryLocation_KeepsLocationsAndUsage_AndRescanWorks()
    {
        using var f = NewFixture();
        BuildSparse(f, churn: 100);
        f.Load();
        f.Usage.RecordOpen(@"C:\Users\anna\Dokumente\Müller\Notizen.txt");
        var roots = f.Store.GetRoots();
        f.Store.UpdateRoot(roots[1] with
        {
            LastScanCompletedUtc = 1000,
            LastScanStartedUtc = 900,
            LastScanStatus = ScanStatus.CompleteWithErrors,
            LastScanFailedDirs = 3,
            AttemptsToday = 2,
        });
        int before = f.Index.Count;
        int lastId = f.Store.LastAllocatedId;

        int removed = f.Store.ResetAllEntries();

        Assert.Equal(before, removed);
        Assert.Equal(2, f.Index.Count); // only the two root entries
        Assert.Empty(f.Paths("angebot"));
        Assert.Equal(["C:\\Users\\anna"], f.Paths("anna"));
        Assert.Equal(2, Rows(f.Db).Count);

        var after = f.Store.GetRoots();
        Assert.Equal(2, after.Count);
        Assert.Equal(roots.Select(r => r.Path), after.Select(r => r.Path));
        Assert.Equal(roots.Select(r => r.UncPath), after.Select(r => r.UncPath));
        Assert.Equal(roots.Select(r => r.Kind), after.Select(r => r.Kind));
        Assert.All(after, r => Assert.True(r.RootEntryId > lastId)); // numbers keep growing
        Assert.Equal(ScanStatus.Never, after[1].LastScanStatus);
        Assert.Null(after[1].LastScanCompletedUtc);
        Assert.Equal(0, after[1].LastScanFailedDirs);
        Assert.Equal(2, after[1].AttemptsToday); // manual rebuild is not an attempt, the counter stays

        // usage stays (path based) and finds its entry again once the entries are back.
        Assert.Equal(1, f.Usage.GetOpenCount(@"C:\Users\anna\Dokumente\Müller\Notizen.txt"));

        // A scan can fill the location again (root entry exists).
        int folder = f.Add(after[0], after[0].RootEntryId, "Dokumente", true);
        int m = f.Add(after[0], folder, "Müller", true);
        f.Add(after[0], m, "Notizen.txt", false);
        f.Usage.Reload();
        Assert.Equal(new[] { @"C:\Users\anna\Dokumente\Müller\Notizen.txt" }, f.Paths("notizen"));
        Assert.Equal(new[] { @"C:\Users\anna\Dokumente\Müller\Notizen.txt" }, f.Engine.RecentlyOpened(SearchMode.All).Hits.Select(h => h.Details.FullPath).ToArray());
    }

    [Fact]
    public void ResetAllEntries_SurvivesARestart()
    {
        using var f = NewFixture();
        BuildSparse(f, churn: 10);
        f.Store.ResetAllEntries();
        f.Restart();
        Assert.Equal(2, f.Load());
        Assert.Equal(2, f.Store.GetRoots().Count);
        Assert.All(f.Store.GetRoots(), r => Assert.NotEqual(0, r.RootEntryId));
    }

    [Fact]
    public void ResetAllEntries_WithoutLocations_IsHarmless()
    {
        using var f = NewFixture();
        Assert.Equal(0, f.Store.ResetAllEntries());
        Assert.Equal(0, f.Index.Count);
    }

    [Fact]
    public void MemoryIndex_Clear_RemovesEverything_AndAcceptsNewEntries()
    {
        using var index = new MemoryIndex();
        for (int i = 1; i <= 100_000; i++)
        {
            index.Set(i, i == 1 ? 0 : 1, 1, "name" + i.ToString(CultureInfo.InvariantCulture), false, 5);
        }

        Assert.Equal(100_000, index.Count);
        index.Clear();
        Assert.Equal(0, index.Count);
        Assert.Equal(0, index.MaxId);
        Assert.Empty(index.Search(SearchQuery.Parse("name"), NoUsage.Instance, 10, default).Matches);

        index.Set(1, 0, 1, "root", true, 0);
        index.Set(2, 1, 1, "Neu.txt", false, 0);
        Assert.Equal(2, index.Count);
        Assert.Single(index.Search(SearchQuery.Parse("neu"), NoUsage.Instance, 10, default).Matches);
    }

    // ---------------------------------------------------------------- recently opened

    [Fact]
    public void RecentlyOpened_NewestFirst_OnlyExisting_ModeAndLimit()
    {
        var clock = new ManualClock(new DateTimeOffset(2026, 11, 1, 10, 0, 0, TimeSpan.Zero));
        using var f = NewFixture();
        var usage = new UsageTracker(f.Db, f.Store, clock);
        var engine = new SearchEngine(f.Index, new DatabaseEntryResolver(f.Db), usage);
        var root = f.Store.AddRoot(@"C:\u", null, RootKind.Local);
        int docs = f.Add(root, root.RootEntryId, "Docs", true);
        f.Add(root, docs, "a.txt", false);
        f.Add(root, docs, "b.txt", false);
        int gone = f.Add(root, docs, "gone.txt", false);
        f.Add(root, docs, "never.txt", false);

        string Open(string path)
        {
            usage.RecordOpen(path);
            clock.Advance(TimeSpan.FromMinutes(1));
            return path;
        }

        Open(@"C:\u\Docs\a.txt");
        Open(@"C:\u\Docs\gone.txt");
        Open(@"C:\u\Docs");
        Open(@"C:\u\Docs\b.txt");
        Open(@"C:\u\Docs\a.txt"); // a again: now newest
        Open(@"C:\u\not in directory.txt"); // not in the directory: never listed
        f.Store.DeleteSubtree(gone);

        var all = engine.RecentlyOpened(SearchMode.All).Hits.Select(h => h.Details.FullPath).ToArray();
        Assert.Equal([@"C:\u\Docs\a.txt", @"C:\u\Docs\b.txt", @"C:\u\Docs"], all);

        Assert.Equal([@"C:\u\Docs\a.txt", @"C:\u\Docs\b.txt"], engine.RecentlyOpened(SearchMode.Files).Hits.Select(h => h.Details.FullPath).ToArray());
        Assert.Equal([@"C:\u\Docs"], engine.RecentlyOpened(SearchMode.Folders).Hits.Select(h => h.Details.FullPath).ToArray());
        Assert.Equal(2, engine.RecentlyOpened(SearchMode.All, 2).Hits.Count);
        Assert.Equal(3, engine.RecentlyOpened(SearchMode.All).TotalCount);
    }

    [Fact]
    public void RecentlyOpened_Max100_AndSkipsManyVanishedOnes()
    {
        var clock = new ManualClock(new DateTimeOffset(2026, 11, 1, 10, 0, 0, TimeSpan.Zero));
        using var f = NewFixture();
        var usage = new UsageTracker(f.Db, f.Store, clock);
        var engine = new SearchEngine(f.Index, new DatabaseEntryResolver(f.Db), usage);
        var root = f.Store.AddRoot(@"C:\u", null, RootKind.Local);
        var ids = new List<int>();
        for (int i = 0; i < 400; i++)
        {
            ids.Add(f.Add(root, root.RootEntryId, "f" + i.ToString("D3", CultureInfo.InvariantCulture) + ".txt", false));
        }

        for (int i = 0; i < 400; i++)
        {
            usage.RecordOpen(@"C:\u\f" + i.ToString("D3", CultureInfo.InvariantCulture) + ".txt");
            clock.Advance(TimeSpan.FromSeconds(1));
        }

        // The 250 newest vanish; the 100 returned are the newest of the rest.
        for (int i = 150; i < 400; i++)
        {
            f.Store.DeleteSubtree(ids[i]);
        }

        var hits = engine.RecentlyOpened(SearchMode.All).Hits;
        Assert.Equal(100, hits.Count);
        Assert.Equal(@"C:\u\f149.txt", hits[0].Details.FullPath);
        Assert.Equal(@"C:\u\f050.txt", hits[99].Details.FullPath);
    }

    [Fact]
    public void RecentlyOpened_WithoutUsage_IsEmpty()
    {
        using var f = NewFixture();
        Assert.Empty(f.Engine.RecentlyOpened(SearchMode.All).Hits);
        var engine = new SearchEngine(f.Index, new DatabaseEntryResolver(f.Db));
        Assert.Empty(engine.RecentlyOpened(SearchMode.All).Hits);
    }

    private sealed class ManualClock(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }
}
