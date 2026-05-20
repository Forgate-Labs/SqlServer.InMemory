using System.Collections.Concurrent;
using System.Data.Common;
using System.Globalization;
using Microsoft.Data.Sqlite;

namespace SqlServer.InMemory.Sqlite;

public sealed class SqliteInMemoryConnectionFactory : ISqliteInMemoryConnectionFactory
{
    private static readonly ConcurrentDictionary<string, Lazy<SharedDatabase>> Databases = new(StringComparer.Ordinal);

    public DbConnection Create(string databaseName, SqlServerInMemoryOptions options)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databaseName);
        ArgumentNullException.ThrowIfNull(options);

        var database = Databases.GetOrAdd(
            databaseName,
            static name => new Lazy<SharedDatabase>(() => new SharedDatabase(name), isThreadSafe: true)).Value;
        var connection = new SqliteConnection(database.ConnectionString);
        connection.Open();
        ConfigureConnection(connection, options);

        return new SqlServerInMemorySqliteConnection(connection);
    }

    /// <summary>
    /// Deletes data from all user tables in a named in-memory database while keeping its schema alive.
    /// </summary>
    public static async Task ResetDatabaseAsync(string databaseName, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databaseName);

        if (!Databases.TryGetValue(databaseName, out var databaseSlot))
        {
            return;
        }

        var database = databaseSlot.Value;
        await using var connection = new SqliteConnection(database.ConnectionString);
        await connection.OpenAsync(cancellationToken);

        var tables = new List<string>();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%';";
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                tables.Add(reader.GetString(0));
            }
        }

        foreach (var table in tables)
        {
            await using var delete = connection.CreateCommand();
            delete.CommandText = $"DELETE FROM \"{table.Replace("\"", "\"\"")}\";";
            await delete.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    /// <summary>
    /// Releases the anchor connection for a named in-memory database.
    /// </summary>
    public static void ReleaseDatabase(string databaseName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databaseName);

        if (Databases.TryRemove(databaseName, out var databaseSlot) && databaseSlot.IsValueCreated)
        {
            databaseSlot.Value.Dispose();
        }
    }

    /// <summary>
    /// Releases anchor connections for all named in-memory databases.
    /// </summary>
    public static void ReleaseAllDatabases()
    {
        foreach (var databaseName in Databases.Keys)
        {
            ReleaseDatabase(databaseName);
        }
    }

    private static void ConfigureConnection(SqliteConnection connection, SqlServerInMemoryOptions options)
    {
        SqlServerCatalogFunctions.Register(connection, options);

        if (options.UseCaseInsensitiveCollation)
        {
            connection.CreateCollation(
                "SQLSERVER_CI_AS",
                static (x, y) => string.Compare(x, y, CultureInfo.InvariantCulture, CompareOptions.IgnoreCase));
        }

        using var command = connection.CreateCommand();
        command.CommandText = options.EnforceForeignKeys ? "PRAGMA foreign_keys = ON;" : "PRAGMA foreign_keys = OFF;";
        command.ExecuteNonQuery();
    }

    private sealed class SharedDatabase : IDisposable
    {
        private readonly SqliteConnection anchorConnection;

        public SharedDatabase(string databaseName)
        {
            ConnectionString = new SqliteConnectionStringBuilder
            {
                DataSource = databaseName,
                Mode = SqliteOpenMode.Memory,
                Cache = SqliteCacheMode.Shared
            }.ToString();

            anchorConnection = new SqliteConnection(ConnectionString);
            anchorConnection.Open();
        }

        public string ConnectionString { get; }

        public void Dispose() => anchorConnection.Dispose();
    }
}
