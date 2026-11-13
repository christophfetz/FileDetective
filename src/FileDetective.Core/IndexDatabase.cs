using Microsoft.Data.Sqlite;

namespace FileDetective.Core;

/// <summary>
/// SQLite access (PLAN.md 6.1). Owns the file, creates the schema and hands out opened connections.
/// A file with a foreign schema version or a damaged file is renamed to <c>index.db.bak-&lt;timestamp&gt;</c>
/// and replaced by a fresh database instead of crashing the program.
/// Connections are pooled; <see cref="Dispose"/> clears the pools so that the files are released.
/// </summary>
public sealed class IndexDatabase : IDisposable
{
    /// <summary>Schema version stored in <c>meta.schema_version</c>.</summary>
    public const int SchemaVersion = 1;

    private const string SchemaSql = """
        CREATE TABLE meta(key TEXT PRIMARY KEY, value TEXT NOT NULL);

        CREATE TABLE roots(
          id INTEGER PRIMARY KEY,
          path TEXT NOT NULL,
          unc_path TEXT,
          kind INTEGER NOT NULL,
          enabled INTEGER NOT NULL,
          last_scan_started_utc INTEGER,
          last_scan_completed_utc INTEGER,
          last_scan_status INTEGER NOT NULL,
          last_scan_failed_dirs INTEGER NOT NULL DEFAULT 0,
          attempts_today INTEGER NOT NULL DEFAULT 0
        );

        CREATE TABLE entries(
          id INTEGER PRIMARY KEY,
          parent_id INTEGER NOT NULL,
          root_id INTEGER NOT NULL,
          name TEXT NOT NULL,
          is_dir INTEGER NOT NULL,
          size INTEGER NOT NULL,
          mtime INTEGER NOT NULL,
          attrs INTEGER NOT NULL
        );
        CREATE INDEX ix_entries_parent ON entries(parent_id);

        CREATE TABLE usage(
          path TEXT PRIMARY KEY COLLATE NOCASE,
          open_count INTEGER NOT NULL,
          last_opened_utc INTEGER NOT NULL
        );
        """;

    private const int SqliteBusy = 5;
    private const int SqliteLocked = 6;

    private readonly string _connectionString;

    private IndexDatabase(string filePath, string? backupPath)
    {
        FilePath = filePath;
        BackupPath = backupPath;
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = filePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = true,
        }.ToString();
    }

    /// <summary>Full path of the database file.</summary>
    public string FilePath { get; }

    /// <summary>Where the previous file was moved to, if it was unusable and a new database had to be created; else null.</summary>
    public string? BackupPath { get; }

    /// <summary>
    /// Opens the database at <paramref name="filePath"/>, creating directory, file and schema as needed.
    /// </summary>
    public static IndexDatabase Open(string filePath, TimeProvider? clock = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        filePath = Path.GetFullPath(filePath);
        var directory = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        string? backup = null;
        var state = File.Exists(filePath) ? Inspect(filePath) : FileState.Fresh;
        if (state == FileState.Unusable)
        {
            backup = MoveAside(filePath, clock ?? TimeProvider.System);
            state = FileState.Fresh;
        }

        var db = new IndexDatabase(filePath, backup);
        if (state == FileState.Fresh)
        {
            db.CreateSchema();
        }

        return db;
    }

    /// <summary>Opens a new connection with WAL and <c>synchronous=NORMAL</c>. The caller disposes it.</summary>
    public SqliteConnection OpenConnection()
    {
        var connection = new SqliteConnection(_connectionString);
        try
        {
            connection.Open();
            using var pragma = connection.CreateCommand();
            pragma.CommandText = "PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL;";
            pragma.ExecuteNonQuery();
            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    /// <summary>Releases pooled connections so that the files can be moved or deleted.</summary>
    public void Dispose() => SqliteConnection.ClearAllPools();

    private void CreateSchema()
    {
        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = SchemaSql;
        command.ExecuteNonQuery();
        command.CommandText = "INSERT INTO meta(key, value) VALUES('schema_version', @v)";
        command.Parameters.AddWithValue("@v", SchemaVersion.ToString(System.Globalization.CultureInfo.InvariantCulture));
        command.ExecuteNonQuery();
        transaction.Commit();
    }

    private enum FileState
    {
        Fresh,
        Usable,
        Unusable,
    }

    /// <summary>
    /// Decides whether an existing file can be used. Decision: an empty file or a database without any table counts as
    /// fresh (nothing to lose); anything SQLite cannot read, a missing table or another schema version is unusable.
    /// A busy or locked database is not renamed (it is probably in use by another instance): the exception is passed on.
    /// </summary>
    private static FileState Inspect(string filePath)
    {
        try
        {
            var connectionString = new SqliteConnectionStringBuilder
            {
                DataSource = filePath,
                Mode = SqliteOpenMode.ReadWrite,
                Pooling = false,
            }.ToString();
            using var connection = new SqliteConnection(connectionString);
            connection.Open();
            if (Scalar(connection, "SELECT count(*) FROM sqlite_master") == 0)
            {
                return FileState.Fresh;
            }

            using var command = connection.CreateCommand();
            command.CommandText = "SELECT value FROM meta WHERE key = 'schema_version'";
            var version = command.ExecuteScalar() as string;
            if (version != SchemaVersion.ToString(System.Globalization.CultureInfo.InvariantCulture))
            {
                return FileState.Unusable;
            }

            // All tables must be readable.
            Scalar(connection, "SELECT count(*) FROM (SELECT 1 FROM roots LIMIT 1)");
            Scalar(connection, "SELECT count(*) FROM (SELECT 1 FROM entries LIMIT 1)");
            Scalar(connection, "SELECT count(*) FROM (SELECT 1 FROM usage LIMIT 1)");
            return FileState.Usable;
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode is not (SqliteBusy or SqliteLocked))
        {
            return FileState.Unusable;
        }
    }

    private static long Scalar(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static string MoveAside(string filePath, TimeProvider clock)
    {
        SqliteConnection.ClearAllPools();
        string stamp = clock.GetUtcNow().UtcDateTime.ToString("yyyyMMddHHmmss", System.Globalization.CultureInfo.InvariantCulture);
        string backup = $"{filePath}.bak-{stamp}";
        for (int i = 1; File.Exists(backup) || File.Exists(backup + "-wal") || File.Exists(backup + "-shm"); i++)
        {
            backup = $"{filePath}.bak-{stamp}-{i}";
        }

        File.Move(filePath, backup);
        foreach (var suffix in new[] { "-wal", "-shm" })
        {
            if (File.Exists(filePath + suffix))
            {
                File.Move(filePath + suffix, backup + suffix);
            }
        }

        return backup;
    }
}
