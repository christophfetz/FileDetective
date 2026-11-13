using System.Diagnostics;
using FileDetective.Core;
using Microsoft.Data.Sqlite;

namespace FileDetective.Tests;

/// <summary>Clock for the scanner that never waits: listings and pauses only move the time forward.</summary>
internal sealed class FakeScanClock : IScanClock
{
    private readonly object _gate = new();
    private TimeSpan _elapsed;

    public List<TimeSpan> Pauses { get; } = [];

    public DateTimeOffset UtcNow => new DateTimeOffset(2026, 11, 13, 8, 0, 0, TimeSpan.Zero) + Elapsed;

    public TimeSpan Elapsed
    {
        get
        {
            lock (_gate)
            {
                return _elapsed;
            }
        }
    }

    /// <summary>Called after a pause with its index; lets a test cancel during a pause.</summary>
    public Action<int>? OnPause { get; set; }

    public void Advance(TimeSpan by)
    {
        lock (_gate)
        {
            _elapsed += by;
        }
    }

    public void Pause(TimeSpan duration, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            Pauses.Add(duration);
            _elapsed += duration;
        }

        OnPause?.Invoke(Pauses.Count - 1);
        cancellationToken.ThrowIfCancellationRequested();
    }
}

/// <summary>Link detector for fakes: folders whose full path is in the set are links.</summary>
internal sealed class FakeLinkDetector(params string[] links) : ILinkDetector
{
    private readonly HashSet<string> _links = new(links, StringComparer.OrdinalIgnoreCase);

    public int Calls { get; private set; }

    public bool IsLink(string directoryPath)
    {
        Calls++;
        return _links.Contains(directoryPath);
    }
}

/// <summary>
/// An in-memory file system behind <see cref="IDirectoryLister"/>: folders and files by full path, injectable
/// failures, listing durations on a <see cref="FakeScanClock"/>, and a check that it is never called in parallel.
/// </summary>
internal sealed class FakeFileSystem : IDirectoryLister
{
    public static readonly DateTime BaseTime = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private readonly Dictionary<string, List<RawEntry>> _children = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Exception> _failures = new(StringComparer.OrdinalIgnoreCase);
    private int _active;

    public FakeFileSystem(string rootPath)
    {
        RootPath = rootPath;
        _children[rootPath] = [];
    }

    public string RootPath { get; }

    public FakeScanClock? Clock { get; set; }

    /// <summary>How long a listing takes on <see cref="Clock"/>; default 0.</summary>
    public Func<string, TimeSpan>? Duration { get; set; }

    /// <summary>Called inside every listing (before the result is returned).</summary>
    public Action<string>? OnList { get; set; }

    public List<string> Calls { get; } = [];

    public int MaxConcurrent { get; private set; }

    public string P(string relative) => relative.Length == 0 ? RootPath : Path.Join(RootPath, relative);

    public FakeFileSystem Dir(string relative, FileAttributes extra = 0, int mtimeOffset = 0)
    {
        string path = P(relative);
        Put(path, new RawEntry(Path.GetFileName(path), true, 0, BaseTime.AddSeconds(mtimeOffset), FileAttributes.Directory | extra));
        _children.TryAdd(path, []);
        return this;
    }

    public FakeFileSystem File(string relative, long size = 100, int mtimeOffset = 0, FileAttributes attributes = FileAttributes.Archive)
    {
        string path = P(relative);
        Put(path, new RawEntry(Path.GetFileName(path), false, size, BaseTime.AddSeconds(mtimeOffset), attributes));
        return this;
    }

    /// <summary>Removes a file or a folder with everything below it.</summary>
    public FakeFileSystem Remove(string relative)
    {
        string path = P(relative);
        string parent = Path.GetDirectoryName(path)!;
        _children[parent].RemoveAll(e => string.Equals(e.Name, Path.GetFileName(path), StringComparison.OrdinalIgnoreCase));
        foreach (string key in _children.Keys.Where(k => IsSameOrBelow(k, path)).ToList())
        {
            _children.Remove(key);
        }

        return this;
    }

    /// <summary>Removes only the listing of a folder, while its parent still shows it (vanished between the two listings).</summary>
    public void RemoveListingOnly(string relative) => _children.Remove(P(relative));

    public void Fail(string relative, Exception exception) => _failures[P(relative)] = exception;

    public void ClearFailures() => _failures.Clear();

    public IReadOnlyList<RawEntry> List(string directoryPath)
    {
        int now = Interlocked.Increment(ref _active);
        try
        {
            MaxConcurrent = Math.Max(MaxConcurrent, now);
            lock (Calls)
            {
                Calls.Add(directoryPath);
            }

            if (Duration is { } d)
            {
                Clock?.Advance(d(directoryPath));
            }

            OnList?.Invoke(directoryPath);
            if (_failures.TryGetValue(directoryPath, out var ex))
            {
                throw ex;
            }

            if (!_children.TryGetValue(directoryPath, out var list))
            {
                throw new DirectoryNotFoundException(directoryPath);
            }

            return [.. list];
        }
        finally
        {
            Interlocked.Decrement(ref _active);
        }
    }

    /// <summary>The expected index content: relative path to (is folder, size, mtime, attributes), links not entered.</summary>
    public SortedDictionary<string, EntryState> Expected(params string[] links)
    {
        var linkSet = new HashSet<string>(links.Select(P), StringComparer.OrdinalIgnoreCase);
        var result = new SortedDictionary<string, EntryState>(StringComparer.Ordinal);
        var stack = new Stack<string>();
        stack.Push(RootPath);
        while (stack.Count > 0)
        {
            string dir = stack.Pop();
            foreach (var e in _children[dir])
            {
                string path = Path.Join(dir, e.Name);
                result[Path.GetRelativePath(RootPath, path)] = new EntryState(
                    e.IsDirectory, e.IsDirectory ? 0 : e.Size, IndexStore.ToUnixSeconds(e.LastWriteUtc), e.Attributes);
                if (e.IsDirectory && !linkSet.Contains(path) && _children.ContainsKey(path))
                {
                    stack.Push(path);
                }
            }
        }

        return result;
    }

    private static bool IsSameOrBelow(string candidate, string path)
        => string.Equals(candidate, path, StringComparison.OrdinalIgnoreCase)
            || candidate.StartsWith(path + "\\", StringComparison.OrdinalIgnoreCase);

    private void Put(string path, RawEntry entry)
    {
        string parent = Path.GetDirectoryName(path)!;
        if (!_children.TryGetValue(parent, out var list))
        {
            throw new InvalidOperationException($"Parent folder of {path} missing in the fake.");
        }

        int index = list.FindIndex(e => string.Equals(e.Name, entry.Name, StringComparison.OrdinalIgnoreCase));
        if (index >= 0)
        {
            list[index] = entry;
        }
        else
        {
            list.Add(entry);
        }
    }
}

/// <summary>What the index stores about one entry, without its number.</summary>
internal readonly record struct EntryState(bool IsDirectory, long Size, long MTime, FileAttributes Attributes);

/// <summary>One row of <c>entries</c> with its path relative to the location.</summary>
internal readonly record struct DbRow(int Id, int ParentId, string RelativePath, EntryState State);

internal static class ScanAssert
{
    /// <summary>
    /// Reads all entries of a location from the database, checks the structure (one root entry, every parent exists
    /// and is a folder, no orphans) and that the memory index holds the same number of live entries.
    /// </summary>
    public static Dictionary<string, DbRow> ReadTree(StorageFixture f, byte rootId)
    {
        var rows = new Dictionary<int, (int Parent, string Name, EntryState State)>();
        using (var c = f.Db.OpenConnection())
        using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = "SELECT id, parent_id, name, is_dir, size, mtime, attrs FROM entries WHERE root_id = @r";
            cmd.Parameters.AddWithValue("@r", (int)rootId);
            using SqliteDataReader r = cmd.ExecuteReader();
            while (r.Read())
            {
                rows[r.GetInt32(0)] = (r.GetInt32(1), r.GetString(2),
                    new EntryState(r.GetInt32(3) != 0, r.GetInt64(4), r.GetInt64(5), (FileAttributes)r.GetInt32(6)));
            }
        }

        var roots = rows.Where(kv => kv.Value.Parent == 0).ToList();
        Assert.Single(roots);
        int rootEntry = roots[0].Key;

        var result = new Dictionary<string, DbRow>(StringComparer.Ordinal);
        foreach (var (id, row) in rows)
        {
            if (id == rootEntry)
            {
                continue;
            }

            var parts = new List<string>();
            int cur = id;
            for (int guard = 0; cur != rootEntry; guard++)
            {
                Assert.True(guard < 10_000, "Cycle in the parent chain.");
                Assert.True(rows.TryGetValue(cur, out var node), $"Entry {id} has a missing ancestor {cur} (orphan).");
                if (cur != id)
                {
                    Assert.True(node.State.IsDirectory, $"Ancestor {cur} of {id} is not a folder.");
                }

                parts.Add(node.Name);
                cur = node.Parent;
            }

            parts.Reverse();
            string rel = string.Join('\\', parts);
            Assert.True(result.TryAdd(rel, new DbRow(id, row.Parent, rel, row.State)), $"Duplicate path {rel}.");
        }

        return result;
    }

    /// <summary>Asserts that the index content of the location equals <paramref name="expected"/> exactly (all paths, case-sensitive, and all fields).</summary>
    public static void TreeEquals(StorageFixture f, byte rootId, IDictionary<string, EntryState> expected)
    {
        var actual = ReadTree(f, rootId);
        var missing = expected.Keys.Where(k => !actual.ContainsKey(k)).ToList();
        var extra = actual.Keys.Where(k => !expected.ContainsKey(k)).ToList();
        Assert.True(missing.Count == 0 && extra.Count == 0,
            $"Missing in index: [{string.Join(", ", missing)}]; extra in index: [{string.Join(", ", extra)}]");
        foreach (var (path, state) in expected)
        {
            Assert.True(state == actual[path].State, $"{path}: expected {state}, index has {actual[path].State}");
        }

        Assert.Equal(TotalEntries(f), f.Index.Count);
    }

    public static int TotalEntries(StorageFixture f)
    {
        using var c = f.Db.OpenConnection();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM entries";
        return Convert.ToInt32(cmd.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>Snapshot of all rows (with numbers) and the roots table, to prove that nothing changed.</summary>
    public static string DatabaseSnapshot(StorageFixture f)
        => Snapshot(
            f,
            "SELECT id, parent_id, root_id, name, is_dir, size, mtime, attrs FROM entries ORDER BY id",
            "SELECT id, path, unc_path, kind, enabled, last_scan_started_utc, last_scan_completed_utc, last_scan_status, last_scan_failed_dirs, attempts_today FROM roots ORDER BY id");

    /// <summary>Snapshot of all entry rows with their numbers.</summary>
    public static string EntriesSnapshot(StorageFixture f)
        => Snapshot(f, "SELECT id, parent_id, root_id, name, is_dir, size, mtime, attrs FROM entries ORDER BY id");

    private static string Snapshot(StorageFixture f, params string[] queries)
    {
        using var c = f.Db.OpenConnection();
        var sb = new System.Text.StringBuilder();
        foreach (string sql in queries)
        {
            using var cmd = c.CreateCommand();
            cmd.CommandText = sql;
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                for (int i = 0; i < r.FieldCount; i++)
                {
                    sb.Append(r.IsDBNull(i) ? "null" : Convert.ToString(r.GetValue(i), System.Globalization.CultureInfo.InvariantCulture)).Append('|');
                }

                sb.AppendLine();
            }
        }

        return sb.ToString();
    }

    /// <summary>
    /// Walks a real folder independently of the scanner (DirectoryInfo), with the same exclusion rules, not entering
    /// reparse-point folders, and returns the expected index content.
    /// </summary>
    public static SortedDictionary<string, EntryState> WalkFileSystem(string rootPath, ExcludeRules rules)
    {
        var result = new SortedDictionary<string, EntryState>(StringComparer.Ordinal);
        var stack = new Stack<DirectoryInfo>();
        stack.Push(new DirectoryInfo(rootPath));
        while (stack.Count > 0)
        {
            var dir = stack.Pop();
            foreach (var info in dir.EnumerateFileSystemInfos("*", new EnumerationOptions { AttributesToSkip = 0, IgnoreInaccessible = false }))
            {
                bool isDir = (info.Attributes & FileAttributes.Directory) != 0;
                if (rules.IsExcluded(dir.FullName, info.Name, isDir, info.Attributes))
                {
                    continue;
                }

                result[Path.GetRelativePath(rootPath, info.FullName)] = new EntryState(
                    isDir, isDir ? 0 : ((FileInfo)info).Length, IndexStore.ToUnixSeconds(info.LastWriteTimeUtc), info.Attributes);
                if (isDir && (info.Attributes & FileAttributes.ReparsePoint) == 0)
                {
                    stack.Push((DirectoryInfo)info);
                }
            }
        }

        return result;
    }

    /// <summary>Creates a junction with <c>mklink /J</c> (no admin rights needed). False if that is not possible here.</summary>
    public static bool TryCreateJunction(string linkPath, string targetPath)
    {
        try
        {
            var psi = new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{linkPath}\" \"{targetPath}\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using var p = Process.Start(psi);
            if (p is null)
            {
                return false;
            }

            p.StandardOutput.ReadToEnd();
            p.StandardError.ReadToEnd();
            p.WaitForExit(10_000);
            return p.HasExited && p.ExitCode == 0 && Directory.Exists(linkPath)
                && (File.GetAttributes(linkPath) & FileAttributes.ReparsePoint) != 0;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or InvalidOperationException)
        {
            return false;
        }
    }
}
