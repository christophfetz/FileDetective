using System.Collections.Concurrent;

namespace FileDetective.Core;

/// <summary>
/// Remembers which paths were opened through the tool (PLAN.md 6.1 table <c>usage</c>, key = path, NOCASE) and
/// answers the search's question "when was entry number N last opened?" (<see cref="IUsageLookup"/>).
/// </summary>
/// <remarks>
/// The database is keyed by path so that the history survives a rebuild of the directory (new entry numbers).
/// The search asks by entry number, from several threads at once while it holds the read lock of the
/// <see cref="MemoryIndex"/>. Decision (simple solution): the tracker keeps a <see cref="ConcurrentDictionary{TKey,TValue}"/>
/// entry number to last opening time. It is built by <see cref="Reload"/> (path to entry number through
/// <see cref="IndexStore.FindEntryId"/>) and extended by <see cref="RecordOpen"/>. Reads never touch the database or
/// the index, so they are safe under the read lock. Paths that are not in the directory (yet) are not in the map;
/// call <see cref="Reload"/> after a scan or a rename/move to refresh the map. After a delete the old entry number
/// is never reused, so a stale map entry cannot match a wrong entry.
/// </remarks>
public sealed class UsageTracker : IRecentUsage
{
    private readonly IndexDatabase _database;
    private readonly IndexStore _store;
    private readonly TimeProvider _clock;
    private readonly object _writeLock = new();
    private volatile ConcurrentDictionary<int, long> _byEntry = new();

    public UsageTracker(IndexDatabase database, IndexStore store, TimeProvider? clock = null)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(store);
        _database = database;
        _store = store;
        _clock = clock ?? TimeProvider.System;
    }

    /// <summary>Number of entries in the lookup map.</summary>
    public int MappedCount => _byEntry.Count;

    /// <summary>Records that <paramref name="path"/> was opened now (counter + 1, time = now).</summary>
    public void RecordOpen(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        long now = _clock.GetUtcNow().ToUnixTimeSeconds();
        lock (_writeLock)
        {
            using (var connection = _database.OpenConnection())
            using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText =
                    """
                    INSERT INTO usage(path, open_count, last_opened_utc) VALUES(@p, 1, @t)
                    ON CONFLICT(path) DO UPDATE SET open_count = open_count + 1, last_opened_utc = @t
                    """;
                cmd.Parameters.AddWithValue("@p", path);
                cmd.Parameters.AddWithValue("@t", now);
                cmd.ExecuteNonQuery();
            }

            if (_store.FindEntryId(path) is { } id)
            {
                _byEntry[id] = now;
            }
        }
    }

    /// <summary>Number of times the path was opened through the tool (0 if never). Path comparison is case-insensitive.</summary>
    public int GetOpenCount(string path)
    {
        using var connection = _database.OpenConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT open_count FROM usage WHERE path = @p";
        cmd.Parameters.AddWithValue("@p", path);
        return cmd.ExecuteScalar() is long n ? (int)n : 0;
    }

    /// <summary>Rebuilds the entry-number map from the <c>usage</c> table (call after loading and after scans).</summary>
    public void Reload()
    {
        lock (_writeLock)
        {
            var rows = new List<(string Path, long Opened)>();
            using (var connection = _database.OpenConnection())
            using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = "SELECT path, last_opened_utc FROM usage";
                using var r = cmd.ExecuteReader();
                while (r.Read())
                {
                    rows.Add((r.GetString(0), r.GetInt64(1)));
                }
            }

            var map = new ConcurrentDictionary<int, long>();
            foreach (var (path, opened) in rows)
            {
                if (_store.FindEntryId(path) is { } id)
                {
                    map[id] = opened;
                }
            }

            _byEntry = map; // atomic swap; running searches keep the old, still consistent map
        }
    }

    /// <summary>
    /// The entries opened most recently, newest first (same time: higher entry number first, so the order is stable).
    /// Works on the map, so it never touches the database; entries that vanished since the last <see cref="Reload"/>
    /// are still in it, the caller drops what it cannot resolve.
    /// </summary>
    public IReadOnlyList<int> GetMostRecent(int count)
    {
        if (count <= 0)
        {
            return [];
        }

        return [.. _byEntry
            .OrderByDescending(kv => kv.Value)
            .ThenByDescending(kv => kv.Key)
            .Take(count)
            .Select(kv => kv.Key)];
    }

    public bool TryGetLastOpened(int entryId, out long lastOpenedUnixSeconds)
        => _byEntry.TryGetValue(entryId, out lastOpenedUnixSeconds);
}
