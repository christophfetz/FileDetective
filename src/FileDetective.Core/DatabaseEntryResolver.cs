using Microsoft.Data.Sqlite;

namespace FileDetective.Core;

/// <summary>
/// <see cref="IEntryResolver"/> backed by the database (PLAN.md 5.2): entry number to full path (walking up the
/// parent chain; the root entry carries the whole path of the location) and display data.
/// Thread-safe: every call uses its own pooled connection and reads in one snapshot (read transaction).
/// </summary>
public sealed class DatabaseEntryResolver(IndexDatabase database) : IEntryResolver
{
    // Windows paths cannot be deeper than this; a longer chain means broken data (e.g. a cycle).
    private const int MaxDepth = 4096;

    private readonly IndexDatabase _database = database ?? throw new ArgumentNullException(nameof(database));

    public IReadOnlyList<EntryDetails?> Resolve(IReadOnlyList<int> entryIds, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entryIds);
        var result = new EntryDetails?[entryIds.Count];
        if (result.Length == 0)
        {
            return result;
        }

        using var connection = _database.OpenConnection();
        using var transaction = connection.BeginTransaction(deferred: true);
        using var cmd = connection.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = "SELECT parent_id, name, is_dir, size, mtime, attrs, root_id FROM entries WHERE id = @id";
        var idParam = cmd.Parameters.Add("@id", SqliteType.Integer);

        // Parent and name of folders already seen, shared by all hits of this call (hits often share folders).
        var folders = new Dictionary<int, (int Parent, string Name)?>();

        for (int i = 0; i < result.Length; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            idParam.Value = entryIds[i];
            int parent;
            string name;
            bool isDir;
            long size;
            long mtime;
            int attrs;
            byte rootId;
            using (var r = cmd.ExecuteReader())
            {
                if (!r.Read())
                {
                    continue;
                }

                parent = r.GetInt32(0);
                name = r.GetString(1);
                isDir = r.GetInt32(2) != 0;
                size = r.GetInt64(3);
                mtime = r.GetInt64(4);
                attrs = r.GetInt32(5);
                rootId = (byte)r.GetInt32(6);
            }

            string? path = BuildPath(cmd, idParam, folders, parent, name);
            if (path is null)
            {
                continue; // broken chain: treat as vanished
            }

            result[i] = new EntryDetails(
                path, name, isDir, size, DateTime.UnixEpoch.AddSeconds(mtime), (FileAttributes)attrs, rootId);
        }

        return result;
    }

    private static string? BuildPath(
        SqliteCommand cmd, SqliteParameter idParam, Dictionary<int, (int Parent, string Name)?> folders,
        int parent, string name)
    {
        var parts = new List<string> { name };
        int current = parent;
        while (current != 0)
        {
            if (parts.Count > MaxDepth)
            {
                return null;
            }

            if (!folders.TryGetValue(current, out var folder))
            {
                idParam.Value = current;
                using var r = cmd.ExecuteReader();
                folder = r.Read() ? (r.GetInt32(0), r.GetString(1)) : null;
                folders[current] = folder;
            }

            if (folder is not { } f)
            {
                return null;
            }

            parts.Add(f.Name);
            current = f.Parent;
        }

        // The last element is the root entry with the whole path of the location (may end with a backslash).
        var sb = new System.Text.StringBuilder(parts[^1]);
        for (int i = parts.Count - 2; i >= 0; i--)
        {
            if (sb.Length == 0 || sb[^1] != '\\')
            {
                sb.Append('\\');
            }

            sb.Append(parts[i]);
        }

        return sb.ToString();
    }
}
