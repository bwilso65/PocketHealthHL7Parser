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
    /// WAL mode lets the DB be read (e.g. via the sqlite3 CLI) while the server is writing.
    /// </summary>
    public void Initialize()
    {
        using var connection = Open();
        connection.Execute("PRAGMA journal_mode=WAL;");
        connection.Execute("PRAGMA foreign_keys=ON;");
        connection.Execute(Schema.Sql);
    }
}
