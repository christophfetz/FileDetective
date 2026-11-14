using System.Runtime.InteropServices;
using FileDetective.Core;
using Microsoft.Data.Sqlite;

namespace FileDetective.Tests;

/// <summary>
/// Stage 10: the table <c>hashes</c> and the index on <c>entries(size)</c> come into existing databases without any loss,
/// and the duplicate search works on real files in a temp folder (real hard link, real reads, no Recycle Bin).
/// </summary>
public class DuplicateDatabaseTests : IDisposable
{
    /// <summary>The schema exactly as databases of stages 2 to 9 have it (schema version 1, no hashes, no size index).</summary>
    private const string OldSchema = """
        CREATE TABLE meta(key TEXT PRIMARY KEY, value TEXT NOT NULL);
        CREATE TABLE roots(
          id INTEGER PRIMARY KEY, path TEXT NOT NULL, unc_path TEXT, kind INTEGER NOT NULL, enabled INTEGER NOT NULL,
          last_scan_started_utc INTEGER, last_scan_completed_utc INTEGER, last_scan_status INTEGER NOT NULL,
          last_scan_failed_dirs INTEGER NOT NULL DEFAULT 0, attempts_today INTEGER NOT NULL DEFAULT 0);
        CREATE TABLE entries(
          id INTEGER PRIMARY KEY, parent_id INTEGER NOT NULL, root_id INTEGER NOT NULL, name TEXT NOT NULL,
          is_dir INTEGER NOT NULL, size INTEGER NOT NULL, mtime INTEGER NOT NULL, attrs INTEGER NOT NULL);
        CREATE INDEX ix_entries_parent ON entries(parent_id);
        CREATE TABLE usage(path TEXT PRIMARY KEY COLLATE NOCASE, open_count INTEGER NOT NULL, last_opened_utc INTEGER NOT NULL);
        INSERT INTO meta(key, value) VALUES('schema_version', '1'), ('max_id', '40');
        INSERT INTO roots(id, path, unc_path, kind, enabled, last_scan_started_utc, last_scan_completed_utc, last_scan_status)
          VALUES(1, 'R:\', NULL, 0, 1, 1700000000, 1700000100, 2);
        INSERT INTO entries VALUES(10, 0, 1, 'R:\', 1, 0, 0, 16);
        INSERT INTO entries VALUES(11, 10, 1, 'Docs', 1, 0, 1700000000, 16);
        INSERT INTO entries VALUES(12, 11, 1, 'Angebot Müller.pdf', 0, 2000000, 1700000000, 32);
        INSERT INTO entries VALUES(13, 10, 1, 'copy.pdf', 0, 2000000, 1700000001, 32);
        INSERT INTO entries VALUES(40, 10, 1, 'last.txt', 0, 12, 1700000002, 32);
        INSERT INTO usage VALUES('R:\Docs\Angebot Müller.pdf', 3, 1700000500);
        """;

    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    private void CreateOldDatabase()
    {
        using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = _dir.DbPath, Pooling = false }.ToString()))
        {
            connection.Open();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = OldSchema;
            cmd.ExecuteNonQuery();
        }

        SqliteConnection.ClearAllPools();
    }

    private static long Scalar(IndexDatabase db, string sql)
    {
        using var c = db.OpenConnection();
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToInt64(cmd.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static long CountObject(IndexDatabase db, string type, string name)
        => Scalar(db, $"SELECT COUNT(*) FROM sqlite_master WHERE type = '{type}' AND name = '{name}'");

    [Fact]
    public void ExistingDatabase_GetsTheTableAndTheIndex_WithoutLosingAnything()
    {
        CreateOldDatabase();
        long sizeBefore = new FileInfo(_dir.DbPath).Length;

        var f = new StorageFixture(_dir);
        try
        {
            Assert.Null(f.Db.BackupPath); // not renamed, not recreated
            Assert.Empty(Directory.GetFiles(_dir.Path, "index.db.bak-*"));
            Assert.True(sizeBefore > 0);
            Assert.Equal("1", ScalarText(f.Db, "SELECT value FROM meta WHERE key = 'schema_version'"));
            Assert.Equal(5, Scalar(f.Db, "SELECT COUNT(*) FROM entries"));
            Assert.Equal(1, Scalar(f.Db, "SELECT COUNT(*) FROM usage"));
            Assert.Equal(1, CountObject(f.Db, "table", "hashes"));
            Assert.Equal(1, CountObject(f.Db, "index", "ix_entries_size"));
            Assert.Equal(1, CountObject(f.Db, "trigger", "tr_entries_delete_hashes"));

            // The directory works as before: loaded, searchable, numbers continue after max_id.
            Assert.Equal(5, f.Load());
            Assert.Equal([@"R:\Docs\Angebot Müller.pdf"], f.Paths("mueller"));
            Assert.Equal(41, f.Store.AllocateId());
            var root = Assert.Single(f.Store.GetRoots());
            Assert.Equal(ScanStatus.Complete, root.LastScanStatus);
            Assert.True(f.Usage.TryGetLastOpened(12, out _));

            // The new table is usable and the size index is used for the candidates.
            Assert.True(f.Store.SaveHash(new StoredHash(12, 2_000_000, 5, [1, 2], null)));
            Assert.Equal([1, 2], f.Store.GetHashes([12])[12].Partial);
            var sizes = new List<long>();
            f.Store.ReadFilesOfMinSize(1_000_000, new HashSet<byte> { 1 }, e => sizes.Add(e.Size));
            Assert.Equal([2_000_000, 2_000_000], sizes);
            Assert.Contains("ix_entries_size", QueryPlan(f.Db), StringComparison.Ordinal);
        }
        finally
        {
            f.Dispose();
        }

        // Opening a second time changes nothing and keeps the stored hash.
        var again = new StorageFixture(_dir);
        try
        {
            Assert.Null(again.Db.BackupPath);
            Assert.Equal(5, Scalar(again.Db, "SELECT COUNT(*) FROM entries"));
            Assert.Equal(1, Scalar(again.Db, "SELECT COUNT(*) FROM hashes"));
        }
        finally
        {
            again.Dispose();
        }
    }

    [Fact]
    public void NewDatabase_HasTheExtensionsToo()
    {
        using var db = IndexDatabase.Open(_dir.DbPath);
        Assert.Equal(1, CountObject(db, "table", "hashes"));
        Assert.Equal(1, CountObject(db, "index", "ix_entries_size"));
        Assert.Equal(1, CountObject(db, "trigger", "tr_entries_delete_hashes"));
        Assert.Equal(1, IndexDatabase.SchemaVersion);
    }

    [Fact]
    public void Renumbering_MovesTheHashesWithTheirEntries_AndKeepsIndexAndTrigger()
    {
        var f = new StorageFixture(_dir);
        int fileId;
        try
        {
            var root = f.Store.AddRoot(@"R:\", null, RootKind.Local);
            for (int i = 0; i < 50; i++)
            {
                f.Store.AllocateId(); // a gap
            }

            fileId = f.Add(root, root.RootEntryId, "kept.bin", false, size: 5_000);
            int gone = f.Add(root, root.RootEntryId, "gone.bin", false, size: 5_000);
            f.Store.SaveHash(new StoredHash(fileId, 5_000, 77, [9], [8]));
            f.Store.SaveHash(new StoredHash(gone, 5_000, 77, [7], null));
            f.Store.DeleteSubtree(gone);
        }
        finally
        {
            f.Dispose();
        }

        using (var db = IndexDatabase.Open(_dir.DbPath))
        {
            var result = DirectoryRenumbering.RenumberIfNeeded(db, force: true);
            Assert.True(result.Performed);
            Assert.Equal(1, CountObject(db, "index", "ix_entries_size"));
            Assert.Equal(1, CountObject(db, "trigger", "tr_entries_delete_hashes"));
        }

        var after = new StorageFixture(_dir);
        try
        {
            int newId = after.Store.FindEntryId(@"R:\kept.bin")!.Value;
            Assert.NotEqual(fileId, newId);
            var hash = Assert.Single(after.Store.GetHashes([newId]).Values);
            Assert.Equal([9], hash.Partial);
            Assert.Equal(1, Scalar(after.Db, "SELECT COUNT(*) FROM hashes"));

            after.Store.DeleteSubtree(newId); // the trigger works on the rebuilt table
            Assert.Equal(0, Scalar(after.Db, "SELECT COUNT(*) FROM hashes"));
        }
        finally
        {
            after.Dispose();
        }
    }

    [Fact]
    public void HashesWithoutTheirTrigger_AreDropped_BecauseTheNumbersMayBelongToOtherFiles()
    {
        var f = new StorageFixture(_dir);
        try
        {
            var root = f.Store.AddRoot(@"R:\", null, RootKind.Local);
            int id = f.Add(root, root.RootEntryId, "a.bin", false, size: 5_000);
            f.Store.SaveHash(new StoredHash(id, 5_000, 77, [9], null));
            using var c = f.Db.OpenConnection();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "DROP TRIGGER tr_entries_delete_hashes"; // as after an older version rebuilt the table
            cmd.ExecuteNonQuery();
        }
        finally
        {
            f.Dispose();
        }

        var again = new StorageFixture(_dir);
        try
        {
            Assert.Equal(0, Scalar(again.Db, "SELECT COUNT(*) FROM hashes"));
            Assert.Equal(2, Scalar(again.Db, "SELECT COUNT(*) FROM entries"));
            Assert.Equal(1, CountObject(again.Db, "trigger", "tr_entries_delete_hashes"));
        }
        finally
        {
            again.Dispose();
        }
    }

    // ---------------------------------------------------------------- real files

#pragma warning disable SYSLIB1054 // a test helper; LibraryImport would need AllowUnsafeBlocks
    [DllImport("kernel32.dll", EntryPoint = "CreateHardLinkW", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLink(string fileName, string existingFileName, nint securityAttributes);
#pragma warning restore SYSLIB1054

    [Fact]
    public void RealFiles_InATempFolder_WithARealHardLink()
    {
        string folder = Path.Combine(_dir.Path, "data");
        string sub = Path.Combine(folder, "sub");
        Directory.CreateDirectory(sub);
        var content = Content.Random(300_000, 42);
        File.WriteAllBytes(Path.Combine(folder, "original.bin"), content);
        File.WriteAllBytes(Path.Combine(sub, "copy.bin"), content);
        Assert.True(
            CreateHardLink(Path.Combine(sub, "hardlink.bin"), Path.Combine(folder, "original.bin"), 0),
            $"CreateHardLink failed: {Marshal.GetLastWin32Error()}");
        File.WriteAllBytes(Path.Combine(folder, "same-start.bin"), Content.ChangedAt(content, 299_999));
        File.WriteAllBytes(Path.Combine(folder, "other.bin"), Content.Random(300_000, 43));
        File.WriteAllBytes(Path.Combine(folder, "small.bin"), Content.Random(100, 44));

        var f = new StorageFixture(_dir);
        try
        {
            var drives = new DriveDiscovery();
            var root = f.Store.AddRoot(folder, null, RootKind.Local);
            Assert.Equal(DuplicateArea.LocalFixed, DuplicateFinder.Classify(root, drives));
            Fill(f, root, folder);
            var counting = new CountingAccess(SystemLocalFileAccess.Instance);

            var result = new DuplicateFinder(f.Store, counting, drives).FindLocal(1024);

            var group = Assert.Single(result.Groups);
            Assert.Equal(2, group.Files.Count);
            Assert.Contains(group.Files, x => x.FullPath.EndsWith(@"sub\copy.bin", StringComparison.OrdinalIgnoreCase));
            Assert.Equal(1, result.Stats.HardLinks);
            Assert.Equal(300_000, group.WastedBytes);
            Assert.DoesNotContain(@"small.bin", counting.Touched); // below the minimum size
            Assert.True(group.Files.All(x => x.CanRecycle));

            // Again: every hash comes from the table.
            counting.Opens = 0;
            Assert.Single(new DuplicateFinder(f.Store, counting, drives).FindLocal(1024).Groups);
            Assert.Equal(0, counting.Opens);

            // The re-check before moving (the bin only records; the real Recycle Bin is not used).
            var marked = group.Files.Single(x => x.FullPath.EndsWith(@"sub\copy.bin", StringComparison.OrdinalIgnoreCase));
            Assert.True(group.SetMarked(marked, true));
            File.SetLastWriteTimeUtc(marked.FullPath, DateTime.UtcNow.AddMinutes(-5));
            var bin = new RecordingBin();
            var report = new DuplicateRecycler(bin, SystemLocalFileAccess.Instance).Recycle(result.Groups);
            Assert.Empty(bin.Files);
            Assert.Equal(DuplicateRecycleOutcome.Changed, Assert.Single(report).Outcome);
            Assert.True(File.Exists(marked.FullPath));
        }
        finally
        {
            f.Dispose();
        }
    }

    [Fact]
    public void RealFileAccess_ProbeAndIdentity()
    {
        string a = Path.Combine(_dir.Path, "a.bin");
        string b = Path.Combine(_dir.Path, "b.bin");
        File.WriteAllBytes(a, [1, 2, 3]);
        File.WriteAllBytes(b, [1, 2, 3]);
        Assert.True(CreateHardLink(Path.Combine(_dir.Path, "a-link.bin"), a, 0));
        var access = SystemLocalFileAccess.Instance;

        var facts = access.Probe(a)!.Value;
        Assert.Equal(3, facts.Size);
        Assert.Equal(File.GetLastWriteTimeUtc(a).Ticks, facts.LastWriteUtcTicks);
        Assert.Null(access.Probe(Path.Combine(_dir.Path, "missing.bin")));
        Assert.Null(access.Probe(_dir.Path)); // a folder is no file

        Assert.Equal(access.GetIdentity(a), access.GetIdentity(Path.Combine(_dir.Path, "a-link.bin")));
        Assert.NotEqual(access.GetIdentity(a), access.GetIdentity(b));
        Assert.Null(access.GetIdentity(Path.Combine(_dir.Path, "missing.bin")));
    }

    /// <summary>Puts the files and folders below <paramref name="folder"/> into the directory, as a scan would.</summary>
    private static void Fill(StorageFixture f, RootInfo root, string folder)
    {
        var stack = new Stack<(string Path, int Id)>();
        stack.Push((folder, root.RootEntryId));
        while (stack.Count > 0)
        {
            var (path, id) = stack.Pop();
            var batch = new ChangeBatch();
            foreach (var e in FileSystemDirectoryLister.Instance.List(path))
            {
                int child = f.Store.AllocateId();
                batch.Inserts.Add(new EntryInsert(
                    child, id, e.Name, e.IsDirectory, e.Size, IndexStore.ToUnixSeconds(e.LastWriteUtc), e.Attributes));
                if (e.IsDirectory)
                {
                    stack.Push((Path.Combine(path, e.Name), child));
                }
            }

            f.Store.Apply(root.Id, batch);
        }
    }

    private static string ScalarText(IndexDatabase db, string sql)
    {
        using var c = db.OpenConnection();
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        return (string)cmd.ExecuteScalar()!;
    }

    private static string QueryPlan(IndexDatabase db)
    {
        using var c = db.OpenConnection();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "EXPLAIN QUERY PLAN SELECT id FROM entries WHERE size >= 1000000 AND is_dir = 0 ORDER BY size";
        using var r = cmd.ExecuteReader();
        var text = new System.Text.StringBuilder();
        while (r.Read())
        {
            text.Append(r.GetString(3)).Append('\n');
        }

        return text.ToString();
    }

    private sealed class CountingAccess(ILocalFileAccess inner) : ILocalFileAccess
    {
        public int Opens { get; set; }

        public List<string> Touched { get; } = [];

        public LocalFileFacts? Probe(string path)
        {
            Touched.Add(Path.GetFileName(path));
            return inner.Probe(path);
        }

        public FileIdentity? GetIdentity(string path)
        {
            Touched.Add(Path.GetFileName(path));
            return inner.GetIdentity(path);
        }

        public Stream OpenRead(string path)
        {
            Opens++;
            Touched.Add(Path.GetFileName(path));
            return inner.OpenRead(path);
        }
    }
}
