using SqlServer.InMemory.Sqlite;

namespace SqlServer.InMemory.EFCore;

/// <summary>
/// Manages named SQLite in-memory databases created by UseSqlServerInMemory.
/// </summary>
public static class SqlServerInMemoryDatabaseRegistry
{
    /// <summary>
    /// Deletes data from all user tables in the named in-memory database while keeping its schema alive.
    /// </summary>
    public static Task ResetAsync(string databaseName, CancellationToken cancellationToken = default)
    {
        return SqliteInMemoryConnectionFactory.ResetDatabaseAsync(databaseName, cancellationToken);
    }

    /// <summary>
    /// Releases the anchor connection for the named in-memory database.
    /// </summary>
    public static void Release(string databaseName)
    {
        SqliteInMemoryConnectionFactory.ReleaseDatabase(databaseName);
    }

    /// <summary>
    /// Releases anchor connections for all named in-memory databases.
    /// </summary>
    public static void ReleaseAll()
    {
        SqliteInMemoryConnectionFactory.ReleaseAllDatabases();
    }
}
