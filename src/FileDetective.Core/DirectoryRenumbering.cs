using System.Globalization;
using Microsoft.Data.Sqlite;

namespace FileDetective.Core;

/// <summary>Result of <see cref="DirectoryRenumbering.RenumberIfNeeded"/>.</summary>
/// <param name="Performed">The entries were renumbered.</param>
/// <param name="MaxIdBefore">Highest entry number handed out before (from <c>meta.max_id</c> and the table).</param>
/// <param name="EntryCountBefore">Number of rows in <c>entries</c> before.</param>
/// <param name="MaxIdAfter">Highest entry number after; equals the number of kept entries if performed.</param>
/// <param name="DroppedUnreachable">Rows that were not reachable from a root entry through their parent chain and were not carried over.</param>
public readonly record struct RenumberResult(
    bool Performed, long MaxIdBefore, long EntryCountBefore, long MaxIdAfter, long DroppedUnreachable);

/// <summary>
/// One-time compaction of the entry numbers at program start (PLAN.md 6.3, stage 7). Entry numbers are never reused, so after
/// many scans in which files came and went the highest number can be far above the number of entries, and the in-memory
/// structure (one slot per number up to the highest) would waste memory. If <c>maxId &gt; 1.5 × count + 100,000</c>, the entries
/// are numbered 1..n in their old order, parent references are rewritten, and <c>meta.max_id</c> is set to n.
/// </summary>
/// <remarks>
/// Everything happens in one transaction, before the directory is loaded into memory and before an
/// <see cref="IndexStore"/> exists. Any failure rolls back and leaves the old numbers untouched; the program then simply
/// starts with them. <c>usage</c> is keyed by path and does not change. Rows whose parent chain does not lead to a root
/// entry (a damaged database; they never show up in searches because their path cannot be built, and no scan can reach
/// them) are not carried over, which also avoids dangling parent references.
/// </remarks>
public static class DirectoryRenumbering
{
    /// <summary>Slack in the trigger: numbers up to 1.5 × count + this are tolerated.</summary>
    public const long Slack = 100_000;

    /// <summary>True if the numbers are sparse enough to renumber (PLAN.md 6.3).</summary>
    public static bool IsNeeded(long maxId, long entryCount) => maxId > 1.5 * entryCount + Slack;

    /// <summary>
    /// Renumbers if <see cref="IsNeeded"/> says so (or <paramref name="force"/>).
    /// </summary>
    /// <param name="beforeCommit">For tests: called inside the transaction right before the commit; an exception rolls back.</param>
    public static RenumberResult RenumberIfNeeded(IndexDatabase database, bool force = false, Action? beforeCommit = null)
    {
        ArgumentNullException.ThrowIfNull(database);
        using var connection = database.OpenConnection();

        long fromMeta = 0;
        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = "SELECT value FROM meta WHERE key = 'max_id'";
            if (cmd.ExecuteScalar() is string s && long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out long parsed))
            {
                fromMeta = parsed;
            }
        }

        long fromTable;
        long count;
        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = "SELECT COALESCE(MAX(id), 0), COUNT(*) FROM entries";
            using var r = cmd.ExecuteReader();
            r.Read();
            fromTable = r.GetInt64(0);
            count = r.GetInt64(1);
        }

        long maxId = Math.Max(fromMeta, fromTable);
        if (!force && !IsNeeded(maxId, count))
        {
            return new RenumberResult(false, maxId, count, maxId, 0);
        }

        long kept;
        using (var tx = connection.BeginTransaction())
        {
            Execute(connection, tx, "DROP TABLE IF EXISTS renumber_map");
            Execute(connection, tx, "CREATE TABLE renumber_map(old_id INTEGER PRIMARY KEY, new_id INTEGER NOT NULL)");

            // Only entries reachable from a root entry are kept. UNION (not UNION ALL) also ends a cycle in damaged data.
            Execute(
                connection, tx,
                """
                WITH RECURSIVE reach(id) AS (
                  SELECT id FROM entries WHERE parent_id = 0
                  UNION
                  SELECT e.id FROM entries e JOIN reach r ON e.parent_id = r.id
                )
                INSERT INTO renumber_map(old_id, new_id)
                SELECT id, ROW_NUMBER() OVER (ORDER BY id) FROM entries WHERE id IN (SELECT id FROM reach) ORDER BY id
                """);

            Execute(
                connection, tx,
                """
                CREATE TABLE entries_new(
                  id INTEGER PRIMARY KEY,
                  parent_id INTEGER NOT NULL,
                  root_id INTEGER NOT NULL,
                  name TEXT NOT NULL,
                  is_dir INTEGER NOT NULL,
                  size INTEGER NOT NULL,
                  mtime INTEGER NOT NULL,
                  attrs INTEGER NOT NULL
                )
                """);
            Execute(
                connection, tx,
                """
                INSERT INTO entries_new(id, parent_id, root_id, name, is_dir, size, mtime, attrs)
                SELECT m.new_id, COALESCE(p.new_id, 0), e.root_id, e.name, e.is_dir, e.size, e.mtime, e.attrs
                FROM entries e
                JOIN renumber_map m ON m.old_id = e.id
                LEFT JOIN renumber_map p ON p.old_id = e.parent_id
                ORDER BY m.new_id
                """);

            using (var cmd = connection.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = "SELECT COUNT(*) FROM entries_new";
                kept = Convert.ToInt64(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
            }

            // Safety net: the copy must hold exactly the mapped entries, and every non-root entry needs its new parent.
            long mapped;
            using (var cmd = connection.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = "SELECT COUNT(*) FROM renumber_map";
                mapped = Convert.ToInt64(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
            }

            long dangling;
            using (var cmd = connection.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText =
                    "SELECT COUNT(*) FROM entries_new c WHERE c.parent_id <> 0 AND NOT EXISTS (SELECT 1 FROM entries_new p WHERE p.id = c.parent_id)";
                dangling = Convert.ToInt64(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
            }

            if (kept != mapped || dangling != 0)
            {
                throw new InvalidOperationException("The renumbered directory is inconsistent; the old numbers are kept.");
            }

            // Stage 10: the stored hashes follow their entries to the new numbers (negative in between, so no number
            // collides with one not yet moved); hashes of entries that are not carried over go.
            IndexDatabase.EnsureExtensions(connection, tx);
            Execute(connection, tx, "DELETE FROM hashes WHERE entry_id NOT IN (SELECT old_id FROM renumber_map)");
            Execute(
                connection, tx,
                "UPDATE hashes SET entry_id = -(SELECT new_id FROM renumber_map WHERE old_id = hashes.entry_id)");
            Execute(connection, tx, "UPDATE hashes SET entry_id = -entry_id");

            // Stage 11: the image texts follow the same way.
            Execute(connection, tx, "DELETE FROM ocr WHERE entry_id NOT IN (SELECT old_id FROM renumber_map)");
            Execute(
                connection, tx,
                "UPDATE ocr SET entry_id = -(SELECT new_id FROM renumber_map WHERE old_id = ocr.entry_id)");
            Execute(connection, tx, "UPDATE ocr SET entry_id = -entry_id");

            // Dropping the table also drops its indexes and triggers; EnsureExtensions creates them again.
            Execute(connection, tx, "DROP TABLE entries");
            Execute(connection, tx, "ALTER TABLE entries_new RENAME TO entries");
            Execute(connection, tx, "CREATE INDEX ix_entries_parent ON entries(parent_id)");
            IndexDatabase.EnsureExtensions(connection, tx, hashesAreMapped: true);
            Execute(connection, tx, "DROP TABLE renumber_map");

            using (var cmd = connection.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = "INSERT OR REPLACE INTO meta(key, value) VALUES('max_id', @v)";
                cmd.Parameters.AddWithValue("@v", kept.ToString(CultureInfo.InvariantCulture));
                cmd.ExecuteNonQuery();
            }

            beforeCommit?.Invoke();
            tx.Commit();
        }

        return new RenumberResult(true, maxId, count, kept, count - kept);
    }

    private static void Execute(SqliteConnection connection, SqliteTransaction tx, string sql)
    {
        using var cmd = connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }
}
