using System.Globalization;
using Microsoft.Data.Sqlite;

namespace FileDetective.Core;

/// <summary>
/// The only place that accepts changes to the directory (PLAN.md 5.2). Every change is written to the database in
/// one transaction first and only then applied to the <see cref="MemoryIndex"/> (under one write lock through
/// <see cref="MemoryIndex.BeginUpdate"/>). If the database write fails, the memory stays untouched.
/// Thread-safe: writers are serialized, reads use their own connection.
/// </summary>
/// <remarks>
/// Entry numbers are handed out by <see cref="AllocateId"/>, never reused, and the highest one is kept in
/// <c>meta.max_id</c> (decision: the plan only says "never reused"; a stored high-water mark is needed because the
/// highest entry may have been deleted before a restart).
/// </remarks>
public sealed class IndexStore : IDisposable
{
    /// <summary>Rows applied to the memory per write lock while loading, so that searches are not blocked for long.</summary>
    private const int LoadChunkSize = 65_536;

    private const string MaxIdKey = "max_id";

    private readonly IndexDatabase _database;
    private readonly MemoryIndex _index;
    private readonly object _writeLock = new();
    private readonly object _readLock = new();
    private readonly SqliteConnection _writeConnection;
    private readonly SqliteConnection _readConnection;
    private int _lastId;
    private volatile bool _loaded;

    public IndexStore(IndexDatabase database, MemoryIndex index)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(index);
        _database = database;
        _index = index;
        _writeConnection = database.OpenConnection();
        _readConnection = database.OpenConnection();

        long fromMeta = 0;
        using (var cmd = _readConnection.CreateCommand())
        {
            cmd.CommandText = "SELECT value FROM meta WHERE key = @k";
            cmd.Parameters.AddWithValue("@k", MaxIdKey);
            if (cmd.ExecuteScalar() is string s)
            {
                fromMeta = long.Parse(s, CultureInfo.InvariantCulture);
            }
        }

        long fromEntries = 0;
        using (var cmd = _readConnection.CreateCommand())
        {
            cmd.CommandText = "SELECT COALESCE(MAX(id), 0) FROM entries";
            fromEntries = Convert.ToInt64(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
        }

        _lastId = (int)Math.Min(Math.Max(fromMeta, fromEntries), MemoryIndex.MaxEntryId);
    }

    /// <summary>The database this store writes to.</summary>
    public IndexDatabase Database => _database;

    /// <summary>The highest entry number handed out so far.</summary>
    public int LastAllocatedId => Volatile.Read(ref _lastId);

    /// <summary>True once <see cref="LoadIntoMemory"/> has finished.</summary>
    public bool IsLoaded => _loaded;

    /// <summary>Converts a UTC time to the Unix seconds stored in the database.</summary>
    public static long ToUnixSeconds(DateTime utc)
        => new DateTimeOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc)).ToUnixTimeSeconds();

    /// <summary>Hands out the next entry number. Numbers are never given out twice, not even after a restart.</summary>
    public int AllocateId()
    {
        int id = Interlocked.Increment(ref _lastId);
        if (id < 1 || id > MemoryIndex.MaxEntryId)
        {
            throw new InvalidOperationException("The entry numbers are used up.");
        }

        return id;
    }

    // ---------------------------------------------------------------- loading

    /// <summary>
    /// Loads all entries from the database into the (empty) memory index in one pass, in chunks of 65,536 rows
    /// per write lock. The caller decides on which thread this runs (the app uses a background thread).
    /// </summary>
    /// <returns>Number of entries loaded.</returns>
    public int LoadIntoMemory(CancellationToken cancellationToken = default)
    {
        using var connection = _database.OpenConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT id, parent_id, root_id, name, is_dir, mtime FROM entries";
        using var reader = cmd.ExecuteReader();
        int total = 0;
        bool more = reader.Read();
        while (more)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var scope = _index.BeginUpdate();
            int n = 0;
            while (more && n < LoadChunkSize)
            {
                scope.Set(
                    reader.GetInt32(0), reader.GetInt32(1), (byte)reader.GetInt32(2),
                    reader.GetString(3), reader.GetInt32(4) != 0, reader.GetInt64(5));
                n++;
                more = reader.Read();
            }

            total += n;
        }

        _loaded = true;
        return total;
    }

    // ---------------------------------------------------------------- locations

    /// <summary>
    /// Creates a location with its root entry (name = whole path, PLAN.md 6.1). Uses the smallest free number 1..255.
    /// </summary>
    /// <exception cref="InvalidOperationException">The path exists already (compared case-insensitively) or all 255 numbers are taken.</exception>
    public RootInfo AddRoot(string path, string? uncPath, RootKind kind)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        lock (_writeLock)
        {
            int entryId;
            byte rootId;
            using (var tx = _writeConnection.BeginTransaction())
            {
                var used = new HashSet<int>();
                using (var cmd = Command(_writeConnection, tx, "SELECT id, path FROM roots"))
                using (var r = cmd.ExecuteReader())
                {
                    while (r.Read())
                    {
                        used.Add(r.GetInt32(0));
                        if (string.Equals(r.GetString(1), path, StringComparison.OrdinalIgnoreCase))
                        {
                            throw new InvalidOperationException($"The location '{path}' exists already.");
                        }
                    }
                }

                int free = 1;
                while (free <= 255 && used.Contains(free))
                {
                    free++;
                }

                if (free > 255)
                {
                    throw new InvalidOperationException("At most 255 locations are possible.");
                }

                rootId = (byte)free;
                entryId = AllocateId();

                using (var cmd = Command(
                    _writeConnection, tx,
                    "INSERT INTO roots(id, path, unc_path, kind, enabled, last_scan_status) VALUES(@id, @path, @unc, @kind, 1, 0)"))
                {
                    cmd.Parameters.AddWithValue("@id", (int)rootId);
                    cmd.Parameters.AddWithValue("@path", path);
                    cmd.Parameters.AddWithValue("@unc", (object?)uncPath ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@kind", (int)kind);
                    cmd.ExecuteNonQuery();
                }

                using (var cmd = Command(
                    _writeConnection, tx,
                    "INSERT INTO entries(id, parent_id, root_id, name, is_dir, size, mtime, attrs) VALUES(@id, 0, @root, @name, 1, 0, 0, @attrs)"))
                {
                    cmd.Parameters.AddWithValue("@id", entryId);
                    cmd.Parameters.AddWithValue("@root", (int)rootId);
                    cmd.Parameters.AddWithValue("@name", path);
                    cmd.Parameters.AddWithValue("@attrs", (int)FileAttributes.Directory);
                    cmd.ExecuteNonQuery();
                }

                PersistMaxId(tx);
                tx.Commit();
            }

            _index.Set(entryId, 0, rootId, path, true, 0);
            return new RootInfo(rootId, path, uncPath, kind, true, null, null, ScanStatus.Never, 0, 0, entryId);
        }
    }

    /// <summary>All locations ordered by number.</summary>
    public IReadOnlyList<RootInfo> GetRoots()
    {
        lock (_readLock)
        {
            return ReadRoots(null);
        }
    }

    /// <summary>One location or null.</summary>
    public RootInfo? GetRoot(byte rootId)
    {
        lock (_readLock)
        {
            var list = ReadRoots(rootId);
            return list.Count == 0 ? null : list[0];
        }
    }

    /// <summary>
    /// Writes the changeable fields of a location (UNC path, kind, enabled and the scan state). The path and the
    /// number do not change. Returns false if the location does not exist.
    /// </summary>
    public bool UpdateRoot(RootInfo root)
    {
        ArgumentNullException.ThrowIfNull(root);
        lock (_writeLock)
        {
            using var cmd = Command(
                _writeConnection, null,
                """
                UPDATE roots SET unc_path = @unc, kind = @kind, enabled = @enabled,
                  last_scan_started_utc = @started, last_scan_completed_utc = @completed,
                  last_scan_status = @status, last_scan_failed_dirs = @failed, attempts_today = @attempts
                WHERE id = @id
                """);
            cmd.Parameters.AddWithValue("@id", (int)root.Id);
            cmd.Parameters.AddWithValue("@unc", (object?)root.UncPath ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@kind", (int)root.Kind);
            cmd.Parameters.AddWithValue("@enabled", root.Enabled ? 1 : 0);
            cmd.Parameters.AddWithValue("@started", root.LastScanStartedUtc.HasValue ? root.LastScanStartedUtc.Value : DBNull.Value);
            cmd.Parameters.AddWithValue("@completed", root.LastScanCompletedUtc.HasValue ? root.LastScanCompletedUtc.Value : DBNull.Value);
            cmd.Parameters.AddWithValue("@status", (int)root.LastScanStatus);
            cmd.Parameters.AddWithValue("@failed", root.LastScanFailedDirs);
            cmd.Parameters.AddWithValue("@attempts", root.AttemptsToday);
            return cmd.ExecuteNonQuery() > 0;
        }
    }

    /// <summary>Removes a location with all its entries from the database and the memory.</summary>
    /// <returns>Number of removed entries (0 if the location does not exist).</returns>
    public int RemoveRoot(byte rootId)
    {
        var ids = new List<int>();
        lock (_writeLock)
        {
            using (var tx = _writeConnection.BeginTransaction())
            {
                // Decision: a full scan of entries is fine here, removing a location is rare. No extra index
                // is added because the schema follows PLAN.md 6.1 exactly.
                using (var cmd = Command(_writeConnection, tx, "SELECT id FROM entries WHERE root_id = @r"))
                {
                    cmd.Parameters.AddWithValue("@r", (int)rootId);
                    using var r = cmd.ExecuteReader();
                    while (r.Read())
                    {
                        ids.Add(r.GetInt32(0));
                    }
                }

                using (var cmd = Command(_writeConnection, tx, "DELETE FROM entries WHERE root_id = @r"))
                {
                    cmd.Parameters.AddWithValue("@r", (int)rootId);
                    cmd.ExecuteNonQuery();
                }

                using (var cmd = Command(_writeConnection, tx, "DELETE FROM roots WHERE id = @r"))
                {
                    cmd.Parameters.AddWithValue("@r", (int)rootId);
                    cmd.ExecuteNonQuery();
                }

                tx.Commit();
            }

            ApplyDeletes(ids);
        }

        RaiseChanged(new IndexChange([], ids, Relocated: false, Reset: false));
        return ids.Count;
    }

    /// <summary>
    /// "Rebuild directory" (PLAN.md 8.5, stage 7): deletes every entry of every location in one transaction, gives each
    /// location a fresh root entry and sets its scan state back to "never scanned" (so the watcher leaves it to the
    /// coming scan, and shares count as due). The locations themselves, their attempt counters and the table
    /// <c>usage</c> stay. Entry numbers continue to grow (decision: never reused, also here, so a stale number held by a
    /// window can never hit a new entry; the renumbering at the next start compacts them). The memory is emptied and
    /// refilled with the root entries under one write lock. If the database write fails, nothing changes.
    /// The caller makes sure that no scan and no watcher works on the locations (see <see cref="LocationLocks"/>).
    /// </summary>
    /// <returns>Number of removed entries.</returns>
    public int ResetAllEntries()
    {
        int result = ResetAllEntriesCore();
        RaiseChanged(new IndexChange([], [], Relocated: false, Reset: true));
        return result;
    }

    private int ResetAllEntriesCore()
    {
        lock (_writeLock)
        {
            IReadOnlyList<RootInfo> roots;
            lock (_readLock)
            {
                roots = ReadRoots(null);
            }

            int removed;
            var fresh = new List<(int EntryId, RootInfo Root)>();
            using (var tx = _writeConnection.BeginTransaction())
            {
                using (var cmd = Command(_writeConnection, tx, "SELECT COUNT(*) FROM entries"))
                {
                    removed = Convert.ToInt32(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
                }

                using (var cmd = Command(_writeConnection, tx, "DELETE FROM entries"))
                {
                    cmd.ExecuteNonQuery();
                }

                using (var cmd = Command(
                    _writeConnection, tx,
                    "UPDATE roots SET last_scan_status = 0, last_scan_completed_utc = NULL, last_scan_failed_dirs = 0"))
                {
                    cmd.ExecuteNonQuery();
                }

                foreach (var root in roots)
                {
                    int entryId = AllocateId();
                    using var cmd = Command(
                        _writeConnection, tx,
                        "INSERT INTO entries(id, parent_id, root_id, name, is_dir, size, mtime, attrs) VALUES(@id, 0, @root, @name, 1, 0, 0, @attrs)");
                    cmd.Parameters.AddWithValue("@id", entryId);
                    cmd.Parameters.AddWithValue("@root", (int)root.Id);
                    cmd.Parameters.AddWithValue("@name", root.Path);
                    cmd.Parameters.AddWithValue("@attrs", (int)FileAttributes.Directory);
                    cmd.ExecuteNonQuery();
                    fresh.Add((entryId, root));
                }

                PersistMaxId(tx);
                tx.Commit();
            }

            using (var scope = _index.BeginUpdate())
            {
                scope.Clear();
                foreach (var (entryId, root) in fresh)
                {
                    scope.Set(entryId, 0, root.Id, root.Path, true, 0);
                }
            }

            return removed;
        }
    }

    /// <summary>
    /// Number of stored entries per location (the root entry counts), for the settings window. One pass over the table;
    /// call it from a background thread.
    /// </summary>
    public IReadOnlyDictionary<byte, int> CountEntriesByRoot()
    {
        lock (_readLock)
        {
            var result = new Dictionary<byte, int>();
            using var cmd = Command(_readConnection, null, "SELECT root_id, COUNT(*) FROM entries GROUP BY root_id");
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                result[(byte)r.GetInt32(0)] = r.GetInt32(1);
            }

            return result;
        }
    }

    // ---------------------------------------------------------------- reading for the scanner

    /// <summary>The children of a folder as stored in the database (for the comparison in PLAN.md 7.2).</summary>
    public IReadOnlyList<KnownChild> GetKnownChildren(int parentId)
    {
        lock (_readLock)
        {
            var result = new List<KnownChild>();
            using var cmd = Command(
                _readConnection, null, "SELECT id, name, is_dir, size, mtime, attrs FROM entries WHERE parent_id = @p");
            cmd.Parameters.AddWithValue("@p", parentId);
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                result.Add(new KnownChild(
                    r.GetInt32(0), r.GetString(1), r.GetInt32(2) != 0, r.GetInt64(3), r.GetInt64(4), (FileAttributes)r.GetInt32(5)));
            }

            return result;
        }
    }

    /// <summary>
    /// Reads every stored entry of one location in one pass and hands it to <paramref name="visit"/> (stage 9: the storage
    /// analysis of a share, PLAN.md 11.1). Read-only, on its own connection and without holding the store's locks, so
    /// scans and searches go on. Decision: a full pass over <c>entries</c> as in <see cref="RemoveRoot"/>; the schema
    /// has no index on <c>root_id</c> and the analysis runs rarely, on a background thread.
    /// </summary>
    public void ReadEntriesOfRoot(byte rootId, Action<StoredEntry> visit, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(visit);
        using var connection = _database.OpenConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT id, parent_id, root_id, name, is_dir, size, mtime, attrs FROM entries WHERE root_id = @r";
        cmd.Parameters.AddWithValue("@r", (int)rootId);
        using var r = cmd.ExecuteReader();
        int n = 0;
        while (r.Read())
        {
            if ((++n & 0x3FFF) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            visit(new StoredEntry(
                r.GetInt32(0), r.GetInt32(1), (byte)r.GetInt32(2), r.GetString(3), r.GetInt32(4) != 0,
                r.GetInt64(5), r.GetInt64(6), (FileAttributes)r.GetInt32(7)));
        }
    }

    /// <summary>
    /// Stage 10 (PLAN.md 11.2): every file of the given locations with at least <paramref name="minSize"/> bytes, smallest
    /// first, in one pass over the index on <c>entries(size)</c>. Read-only, on its own connection like
    /// <see cref="ReadEntriesOfRoot"/>.
    /// </summary>
    public void ReadFilesOfMinSize(
        long minSize, IReadOnlySet<byte> rootIds, Action<StoredEntry> visit, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(rootIds);
        ArgumentNullException.ThrowIfNull(visit);
        if (rootIds.Count == 0)
        {
            return;
        }

        using var connection = _database.OpenConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText =
            "SELECT id, parent_id, root_id, name, is_dir, size, mtime, attrs FROM entries WHERE size >= @min AND is_dir = 0 ORDER BY size";
        cmd.Parameters.AddWithValue("@min", Math.Max(1, minSize));
        using var r = cmd.ExecuteReader();
        int n = 0;
        while (r.Read())
        {
            if ((++n & 0x3FFF) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            byte root = (byte)r.GetInt32(2);
            if (!rootIds.Contains(root))
            {
                continue;
            }

            visit(new StoredEntry(
                r.GetInt32(0), r.GetInt32(1), root, r.GetString(3), false,
                r.GetInt64(5), r.GetInt64(6), (FileAttributes)r.GetInt32(7)));
        }
    }

    /// <summary>The stored hashes of the given entries (stage 10); entries without a row are missing from the result.</summary>
    public IReadOnlyDictionary<int, StoredHash> GetHashes(IReadOnlyCollection<int> entryIds)
    {
        ArgumentNullException.ThrowIfNull(entryIds);
        var result = new Dictionary<int, StoredHash>();
        lock (_readLock)
        {
            using var cmd = Command(_readConnection, null, "SELECT size, mtime, partial, full FROM hashes WHERE entry_id = @id");
            var p = cmd.Parameters.Add("@id", SqliteType.Integer);
            foreach (int id in entryIds)
            {
                p.Value = id;
                using var r = cmd.ExecuteReader();
                if (r.Read())
                {
                    result[id] = new StoredHash(
                        id, r.GetInt64(0), r.GetInt64(1),
                        r.IsDBNull(2) ? null : (byte[])r.GetValue(2),
                        r.IsDBNull(3) ? null : (byte[])r.GetValue(3));
                }
            }
        }

        return result;
    }

    /// <summary>
    /// Stores the hashes of an entry (stage 10), replacing an older row. Written only while the entry exists, under the
    /// same lock as every deletion, so no row of a removed entry can stay behind (the trigger removes the rest).
    /// </summary>
    /// <returns>False if the entry no longer exists.</returns>
    public bool SaveHash(StoredHash hash)
    {
        ArgumentNullException.ThrowIfNull(hash);
        lock (_writeLock)
        {
            using var cmd = Command(
                _writeConnection, null,
                """
                INSERT OR REPLACE INTO hashes(entry_id, size, mtime, partial, full)
                SELECT @id, @size, @mtime, @partial, @full WHERE EXISTS (SELECT 1 FROM entries WHERE id = @id)
                """);
            cmd.Parameters.AddWithValue("@id", hash.EntryId);
            cmd.Parameters.AddWithValue("@size", hash.Size);
            cmd.Parameters.AddWithValue("@mtime", hash.MTimeTicks);
            cmd.Parameters.Add("@partial", SqliteType.Blob).Value = (object?)hash.Partial ?? DBNull.Value;
            cmd.Parameters.Add("@full", SqliteType.Blob).Value = (object?)hash.Full ?? DBNull.Value;
            return cmd.ExecuteNonQuery() > 0;
        }
    }

    /// <summary>One entry from the database or null (stage 3 addition for subtree scans).</summary>
    public StoredEntry? GetEntry(int id)
    {
        lock (_readLock)
        {
            using var cmd = Command(
                _readConnection, null, "SELECT id, parent_id, root_id, name, is_dir, size, mtime, attrs FROM entries WHERE id = @id");
            cmd.Parameters.AddWithValue("@id", id);
            using var r = cmd.ExecuteReader();
            if (!r.Read())
            {
                return null;
            }

            return new StoredEntry(
                r.GetInt32(0), r.GetInt32(1), (byte)r.GetInt32(2), r.GetString(3), r.GetInt32(4) != 0,
                r.GetInt64(5), r.GetInt64(6), (FileAttributes)r.GetInt32(7));
        }
    }

    /// <summary>
    /// Finds the entry for a full path by walking down from the location whose path is the longest prefix.
    /// Names are compared case-insensitively. Returns null if the path is not in the directory.
    /// </summary>
    public int? FindEntryId(string fullPath)
    {
        ArgumentNullException.ThrowIfNull(fullPath);
        lock (_readLock)
        {
            RootInfo? best = null;
            foreach (var root in ReadRoots(null))
            {
                string rootPath = root.Path.TrimEnd('\\', '/');
                bool prefix = fullPath.StartsWith(rootPath, StringComparison.OrdinalIgnoreCase)
                    && (fullPath.Length == rootPath.Length || fullPath[rootPath.Length] is '\\' or '/');
                if (prefix && (best is null || root.Path.Length > best.Path.Length))
                {
                    best = root;
                }
            }

            if (best is null)
            {
                return null;
            }

            int current = best.RootEntryId;
            string rest = fullPath[best.Path.TrimEnd('\\', '/').Length..];
            foreach (var part in rest.Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries))
            {
                int? child = FindChild(current, part);
                if (child is null)
                {
                    return null;
                }

                current = child.Value;
            }

            return current;
        }
    }

    // ---------------------------------------------------------------- changes

    /// <summary>
    /// Writes a batch in one transaction (subtree deletions, insertions, updates) and then applies it to the memory.
    /// If the transaction fails, nothing changes in the database and in the memory.
    /// </summary>
    /// <param name="rootId">The location all inserted entries belong to.</param>
    public ApplyResult Apply(byte rootId, ChangeBatch batch)
    {
        ArgumentNullException.ThrowIfNull(batch);
        var result = ApplyCore(rootId, batch, out var deleted);
        if (Changed is not null)
        {
            var written = new List<WrittenFile>(batch.Inserts.Count + batch.Updates.Count);
            foreach (var e in batch.Inserts)
            {
                if (!e.IsDirectory)
                {
                    written.Add(new WrittenFile(e.Id, e.Name));
                }
            }

            foreach (var u in batch.Updates)
            {
                written.Add(new WrittenFile(u.Id, null));
            }

            RaiseChanged(new IndexChange(written, deleted, Relocated: false, Reset: false));
        }

        return result;
    }

    private ApplyResult ApplyCore(byte rootId, ChangeBatch batch, out List<int> deletedIds)
    {
        lock (_writeLock)
        {
            var deleted = new List<int>();
            using (var tx = _writeConnection.BeginTransaction())
            {
                foreach (int id in batch.DeleteSubtrees)
                {
                    CollectAndDeleteSubtree(tx, id, deleted);
                }

                if (batch.Inserts.Count > 0)
                {
                    int last = Volatile.Read(ref _lastId);
                    using var cmd = Command(
                        _writeConnection, tx,
                        "INSERT INTO entries(id, parent_id, root_id, name, is_dir, size, mtime, attrs) VALUES(@id, @parent, @root, @name, @dir, @size, @mtime, @attrs)");
                    var pId = cmd.Parameters.Add("@id", SqliteType.Integer);
                    var pParent = cmd.Parameters.Add("@parent", SqliteType.Integer);
                    var pName = cmd.Parameters.Add("@name", SqliteType.Text);
                    var pDir = cmd.Parameters.Add("@dir", SqliteType.Integer);
                    var pSize = cmd.Parameters.Add("@size", SqliteType.Integer);
                    var pMtime = cmd.Parameters.Add("@mtime", SqliteType.Integer);
                    var pAttrs = cmd.Parameters.Add("@attrs", SqliteType.Integer);
                    cmd.Parameters.AddWithValue("@root", (int)rootId);
                    foreach (var e in batch.Inserts)
                    {
                        if (e.Id < 1 || e.Id > last)
                        {
                            throw new ArgumentException($"Entry number {e.Id} was not handed out by AllocateId.", nameof(batch));
                        }

                        if (e.ParentId < 1)
                        {
                            throw new ArgumentException("Only AddRoot creates root entries.", nameof(batch));
                        }

                        pId.Value = e.Id;
                        pParent.Value = e.ParentId;
                        pName.Value = e.Name;
                        pDir.Value = e.IsDirectory ? 1 : 0;
                        pSize.Value = e.IsDirectory ? 0 : e.Size;
                        pMtime.Value = e.MTimeUnixSeconds;
                        pAttrs.Value = (int)e.Attributes;
                        cmd.ExecuteNonQuery();
                    }
                }

                if (batch.Updates.Count > 0)
                {
                    using var cmd = Command(
                        _writeConnection, tx, "UPDATE entries SET size = @size, mtime = @mtime, attrs = @attrs WHERE id = @id");
                    var pId = cmd.Parameters.Add("@id", SqliteType.Integer);
                    var pSize = cmd.Parameters.Add("@size", SqliteType.Integer);
                    var pMtime = cmd.Parameters.Add("@mtime", SqliteType.Integer);
                    var pAttrs = cmd.Parameters.Add("@attrs", SqliteType.Integer);
                    foreach (var u in batch.Updates)
                    {
                        pId.Value = u.Id;
                        pSize.Value = u.Size;
                        pMtime.Value = u.MTimeUnixSeconds;
                        pAttrs.Value = (int)u.Attributes;
                        cmd.ExecuteNonQuery();
                    }
                }

                PersistMaxId(tx);
                tx.Commit();
            }

            ApplyDeletes(deleted);
            using (var scope = _index.BeginUpdate())
            {
                foreach (var e in batch.Inserts)
                {
                    scope.Set(e.Id, e.ParentId, rootId, e.Name, e.IsDirectory, e.MTimeUnixSeconds);
                }

                foreach (var u in batch.Updates)
                {
                    scope.UpdateMTime(u.Id, u.MTimeUnixSeconds);
                }
            }

            deletedIds = deleted;
            return new ApplyResult(batch.Inserts.Count, batch.Updates.Count, deleted.Count);
        }
    }

    /// <summary>Removes an entry with all its descendants from the database and the memory.</summary>
    /// <returns>Number of removed entries.</returns>
    public int DeleteSubtree(int id)
    {
        var batch = new ChangeBatch();
        batch.DeleteSubtrees.Add(id);
        // The root is irrelevant without inserts.
        return Apply(0, batch).Deleted;
    }

    /// <summary>Renames an entry (one row). Returns false if it does not exist.</summary>
    public bool Rename(int id, string newName) => Relocate(id, null, newName);

    /// <summary>Moves an entry with its subtree below another folder of the same location (one row). Returns false if it does not exist.</summary>
    public bool Move(int id, int newParentId) => Relocate(id, newParentId, null);

    /// <summary>Moves and renames an entry in one step (one row), e.g. when a file is moved and renamed at once.</summary>
    public bool MoveAndRename(int id, int newParentId, string newName)
    {
        ArgumentNullException.ThrowIfNull(newName);
        return Relocate(id, newParentId, newName);
    }

    private bool Relocate(int id, int? newParentId, string? newName)
    {
        if (newName is not null)
        {
            ArgumentException.ThrowIfNullOrEmpty(newName);
        }

        if (!RelocateCore(id, newParentId, newName))
        {
            return false;
        }

        RaiseChanged(new IndexChange([new WrittenFile(id, newName)], [], Relocated: true, Reset: false));
        return true;
    }

    private bool RelocateCore(int id, int? newParentId, string? newName)
    {
        lock (_writeLock)
        {
            using (var tx = _writeConnection.BeginTransaction())
            {
                int parentId;
                int rootId;
                using (var cmd = Command(_writeConnection, tx, "SELECT parent_id, root_id FROM entries WHERE id = @id"))
                {
                    cmd.Parameters.AddWithValue("@id", id);
                    using var r = cmd.ExecuteReader();
                    if (!r.Read())
                    {
                        return false;
                    }

                    parentId = r.GetInt32(0);
                    rootId = r.GetInt32(1);
                }

                if (parentId == 0)
                {
                    throw new InvalidOperationException("The root entry of a location cannot be renamed or moved.");
                }

                if (newParentId is { } target)
                {
                    if (target < 1)
                    {
                        throw new ArgumentOutOfRangeException(nameof(newParentId));
                    }

                    // The new parent must exist in the same location and must not be the entry itself or a descendant.
                    int cur = target;
                    for (int depth = 0; cur != 0; depth++)
                    {
                        if (cur == id || depth > 100_000)
                        {
                            throw new ArgumentException("An entry cannot be moved below itself.", nameof(newParentId));
                        }

                        using var cmd = Command(_writeConnection, tx, "SELECT parent_id, root_id FROM entries WHERE id = @id");
                        cmd.Parameters.AddWithValue("@id", cur);
                        using var r = cmd.ExecuteReader();
                        if (!r.Read())
                        {
                            throw new ArgumentException("The new parent does not exist.", nameof(newParentId));
                        }

                        if (cur == target && r.GetInt32(1) != rootId)
                        {
                            throw new ArgumentException("An entry cannot be moved to another location.", nameof(newParentId));
                        }

                        cur = r.GetInt32(0);
                    }
                }

                using (var cmd = Command(
                    _writeConnection, tx,
                    "UPDATE entries SET parent_id = COALESCE(@parent, parent_id), name = COALESCE(@name, name) WHERE id = @id"))
                {
                    cmd.Parameters.AddWithValue("@id", id);
                    cmd.Parameters.AddWithValue("@parent", newParentId.HasValue ? newParentId.Value : DBNull.Value);
                    cmd.Parameters.AddWithValue("@name", (object?)newName ?? DBNull.Value);
                    cmd.ExecuteNonQuery();
                }

                tx.Commit();
            }

            using var scope = _index.BeginUpdate();
            if (newName is not null)
            {
                scope.Rename(id, newName);
            }

            if (newParentId is { } p)
            {
                scope.Move(id, p);
            }

            return true;
        }
    }

    // ---------------------------------------------------------------- image text (stage 11, PLAN.md 11.3)

    /// <summary>
    /// Raised after every change that reached the database and the memory, outside the store's locks, on the thread that
    /// made the change (scanner, watcher, UI action). Handlers must be quick and must not throw (an exception is swallowed).
    /// Used by the image text to learn about new, changed, moved and removed images.
    /// </summary>
    public event Action<IndexChange>? Changed;

    private void RaiseChanged(IndexChange change)
    {
        try
        {
            Changed?.Invoke(change);
        }
        catch (Exception)
        {
            // A listener must never break a scan or a watcher.
        }
    }

    /// <summary>
    /// Every file below the folder <paramref name="folderId"/> (all depths) as stored in the database, in one pass over the
    /// index on <c>parent_id</c>. Read-only, on its own connection.
    /// </summary>
    public void ReadFilesBelow(int folderId, Action<StoredEntry> visit, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(visit);
        using var connection = _database.OpenConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            WITH RECURSIVE sub(id) AS (
              SELECT @id
              UNION
              SELECT e.id FROM entries e JOIN sub ON e.parent_id = sub.id WHERE e.is_dir = 1
            )
            SELECT e.id, e.parent_id, e.root_id, e.name, e.is_dir, e.size, e.mtime, e.attrs
            FROM entries e JOIN sub ON e.parent_id = sub.id
            WHERE e.is_dir = 0
            """;
        cmd.Parameters.AddWithValue("@id", folderId);
        using var r = cmd.ExecuteReader();
        int n = 0;
        while (r.Read())
        {
            if ((++n & 0x3FF) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            visit(new StoredEntry(
                r.GetInt32(0), r.GetInt32(1), (byte)r.GetInt32(2), r.GetString(3), false,
                r.GetInt64(5), r.GetInt64(6), (FileAttributes)r.GetInt32(7)));
        }
    }

    /// <summary>Every stored image text (stage 11), read in one pass on its own connection.</summary>
    public IReadOnlyList<StoredImageText> ReadImageTexts(CancellationToken cancellationToken = default)
    {
        var result = new List<StoredImageText>();
        using var connection = _database.OpenConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT entry_id, size, mtime, text FROM ocr";
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            if ((result.Count & 0x3FF) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            result.Add(new StoredImageText(r.GetInt32(0), r.GetInt64(1), r.GetInt64(2), r.GetString(3)));
        }

        return result;
    }

    /// <summary>The stored image texts of the given entries; entries without a row are missing from the result.</summary>
    public IReadOnlyDictionary<int, StoredImageText> GetImageTexts(IReadOnlyCollection<int> entryIds)
    {
        ArgumentNullException.ThrowIfNull(entryIds);
        var result = new Dictionary<int, StoredImageText>();
        lock (_readLock)
        {
            using var cmd = Command(_readConnection, null, "SELECT size, mtime, text FROM ocr WHERE entry_id = @id");
            var p = cmd.Parameters.Add("@id", SqliteType.Integer);
            foreach (int id in entryIds)
            {
                p.Value = id;
                using var r = cmd.ExecuteReader();
                if (r.Read())
                {
                    result[id] = new StoredImageText(id, r.GetInt64(0), r.GetInt64(1), r.GetString(2));
                }
            }
        }

        return result;
    }

    /// <summary>
    /// Stores the text of an image, replacing an older row. Written only while the entry exists, under the same lock as
    /// every deletion, so no text of a removed entry can stay behind (the trigger removes the rest).
    /// </summary>
    /// <returns>False if the entry no longer exists.</returns>
    public bool SaveImageText(StoredImageText text)
    {
        ArgumentNullException.ThrowIfNull(text);
        lock (_writeLock)
        {
            using var cmd = Command(
                _writeConnection, null,
                """
                INSERT OR REPLACE INTO ocr(entry_id, size, mtime, text)
                SELECT @id, @size, @mtime, @text WHERE EXISTS (SELECT 1 FROM entries WHERE id = @id AND is_dir = 0)
                """);
            cmd.Parameters.AddWithValue("@id", text.EntryId);
            cmd.Parameters.AddWithValue("@size", text.Size);
            cmd.Parameters.AddWithValue("@mtime", text.MTimeTicks);
            cmd.Parameters.AddWithValue("@text", text.Text);
            return cmd.ExecuteNonQuery() > 0;
        }
    }

    /// <summary>Removes the stored texts of the given entries (the entries themselves stay).</summary>
    /// <returns>Number of removed rows.</returns>
    public int DeleteImageTexts(IReadOnlyCollection<int> entryIds)
    {
        ArgumentNullException.ThrowIfNull(entryIds);
        if (entryIds.Count == 0)
        {
            return 0;
        }

        lock (_writeLock)
        {
            int removed = 0;
            using var tx = _writeConnection.BeginTransaction();
            using (var cmd = Command(_writeConnection, tx, "DELETE FROM ocr WHERE entry_id = @id"))
            {
                var p = cmd.Parameters.Add("@id", SqliteType.Integer);
                foreach (int id in entryIds)
                {
                    p.Value = id;
                    removed += cmd.ExecuteNonQuery();
                }
            }

            tx.Commit();
            return removed;
        }
    }

    /// <summary>Removes every stored image text (switching the image text off, PLAN.md 11.3).</summary>
    /// <returns>Number of removed rows.</returns>
    public int DeleteAllImageTexts()
    {
        lock (_writeLock)
        {
            using var cmd = Command(_writeConnection, null, "DELETE FROM ocr");
            return cmd.ExecuteNonQuery();
        }
    }

    public void Dispose()
    {
        lock (_writeLock)
        {
            lock (_readLock)
            {
                _writeConnection.Dispose();
                _readConnection.Dispose();
            }
        }
    }

    // ---------------------------------------------------------------- helpers

    private static SqliteCommand Command(SqliteConnection connection, SqliteTransaction? tx, string sql)
    {
        var cmd = connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        return cmd;
    }

    private void PersistMaxId(SqliteTransaction tx)
    {
        using var cmd = Command(_writeConnection, tx, "INSERT OR REPLACE INTO meta(key, value) VALUES(@k, @v)");
        cmd.Parameters.AddWithValue("@k", MaxIdKey);
        cmd.Parameters.AddWithValue("@v", Volatile.Read(ref _lastId).ToString(CultureInfo.InvariantCulture));
        cmd.ExecuteNonQuery();
    }

    private void CollectAndDeleteSubtree(SqliteTransaction tx, int id, List<int> deleted)
    {
        using (var cmd = Command(_writeConnection, tx, "SELECT parent_id FROM entries WHERE id = @id"))
        {
            cmd.Parameters.AddWithValue("@id", id);
            if (cmd.ExecuteScalar() is long parent && parent == 0)
            {
                throw new InvalidOperationException("The root entry of a location is removed with RemoveRoot.");
            }
        }

        int start = deleted.Count;
        using (var cmd = Command(
            _writeConnection, tx,
            """
            WITH RECURSIVE sub(id) AS (
              SELECT @id
              UNION
              SELECT e.id FROM entries e JOIN sub ON e.parent_id = sub.id
            )
            SELECT sub.id FROM sub JOIN entries ON entries.id = sub.id
            """))
        {
            cmd.Parameters.AddWithValue("@id", id);
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                deleted.Add(r.GetInt32(0));
            }
        }

        using (var cmd = Command(_writeConnection, tx, "DELETE FROM entries WHERE id = @id"))
        {
            var p = cmd.Parameters.Add("@id", SqliteType.Integer);
            for (int i = start; i < deleted.Count; i++)
            {
                p.Value = deleted[i];
                cmd.ExecuteNonQuery();
            }
        }
    }

    private void ApplyDeletes(List<int> ids)
    {
        if (ids.Count == 0)
        {
            return;
        }

        using var scope = _index.BeginUpdate();
        foreach (int id in ids)
        {
            scope.Delete(id);
        }
    }

    private List<RootInfo> ReadRoots(byte? onlyId)
    {
        var result = new List<RootInfo>();
        using var cmd = Command(
            _readConnection, null,
            """
            SELECT r.id, r.path, r.unc_path, r.kind, r.enabled, r.last_scan_started_utc, r.last_scan_completed_utc,
                   r.last_scan_status, r.last_scan_failed_dirs, r.attempts_today,
                   COALESCE((SELECT e.id FROM entries e WHERE e.parent_id = 0 AND e.root_id = r.id LIMIT 1), 0)
            FROM roots r
            WHERE @only IS NULL OR r.id = @only
            ORDER BY r.id
            """);
        cmd.Parameters.AddWithValue("@only", onlyId.HasValue ? (int)onlyId.Value : DBNull.Value);
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            result.Add(new RootInfo(
                (byte)r.GetInt32(0),
                r.GetString(1),
                r.IsDBNull(2) ? null : r.GetString(2),
                (RootKind)r.GetInt32(3),
                r.GetInt32(4) != 0,
                r.IsDBNull(5) ? null : r.GetInt64(5),
                r.IsDBNull(6) ? null : r.GetInt64(6),
                (ScanStatus)r.GetInt32(7),
                r.GetInt32(8),
                r.GetInt32(9),
                r.GetInt32(10)));
        }

        return result;
    }

    private int? FindChild(int parentId, string name)
    {
        // First the fast path (index on parent_id, NOCASE folds ASCII only), then a case-insensitive scan of the
        // children for names with other letters, e.g. umlauts.
        using (var cmd = Command(
            _readConnection, null, "SELECT id FROM entries WHERE parent_id = @p AND name = @n COLLATE NOCASE LIMIT 1"))
        {
            cmd.Parameters.AddWithValue("@p", parentId);
            cmd.Parameters.AddWithValue("@n", name);
            if (cmd.ExecuteScalar() is long id)
            {
                return (int)id;
            }
        }

        using (var cmd = Command(_readConnection, null, "SELECT id, name FROM entries WHERE parent_id = @p"))
        {
            cmd.Parameters.AddWithValue("@p", parentId);
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                if (string.Equals(r.GetString(1), name, StringComparison.OrdinalIgnoreCase))
                {
                    return r.GetInt32(0);
                }
            }
        }

        return null;
    }
}
