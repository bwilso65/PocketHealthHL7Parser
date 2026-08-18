using Dapper;
using Microsoft.Data.Sqlite;

namespace Hl7Receiver.Storage;

/// <summary>
/// Owns the SQLite connection string and schema bootstrap.
/// One instance per process; hand out short-lived connections via <see cref="Open"/>.
/// </summary>
public sealed class Database
{
    private readonly string _connectionString;

    public string Path { get; }

    static Database()
    {
        // Columns are snake_case (received_at); read-model properties are PascalCase (ReceivedAt).
        DefaultTypeMap.MatchNamesWithUnderscores = true;
    }

    public Database(string path)
    {
        Path = path;

        var directory = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
        }.ToString();
    }

    /// <summary>Opens a new connection. Caller disposes.</summary>
    public SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        return connection;
    }

    /// <summary>
    /// Creates the schema if it does not exist. Idempotent; safe to run on every startup.
    /// Rollback journal, not WAL: WAL's -shm file is memory-mapped and shared across processes, which
    /// isn't reliable over a Docker Desktop bind mount from a Windows host — a second process (e.g. the
    /// sqlite3 CLI via `docker compose exec`) can fail to open the DB, or see a stale/empty snapshot,
    /// while the server keeps running. DELETE mode has no cross-process shared memory, so external reads
    /// are consistent at any point, not just right after a restart. We're single-writer already (one
    /// background worker), so WAL's concurrent-writer benefit was never in play.
    /// </summary>
    public void Initialize()
    {
        using var connection = Open();
        connection.Execute("PRAGMA journal_mode=DELETE;");
        connection.Execute("PRAGMA foreign_keys=ON;");
        connection.Execute(Schema.Sql);
    }
}
