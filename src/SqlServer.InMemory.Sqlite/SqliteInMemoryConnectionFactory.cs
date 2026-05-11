using System.Data.Common;
using System.Globalization;
using Microsoft.Data.Sqlite;

namespace SqlServer.InMemory.Sqlite;

public sealed class SqliteInMemoryConnectionFactory : ISqliteInMemoryConnectionFactory
{
    public DbConnection Create(string databaseName, SqlServerInMemoryOptions options)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databaseName);
        ArgumentNullException.ThrowIfNull(options);

        var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();

        if (options.UseCaseInsensitiveCollation)
        {
            connection.CreateCollation(
                "SQLSERVER_CI_AS",
                static (x, y) => string.Compare(x, y, CultureInfo.InvariantCulture, CompareOptions.IgnoreCase));
        }

        using var command = connection.CreateCommand();
        command.CommandText = options.EnforceForeignKeys ? "PRAGMA foreign_keys = ON;" : "PRAGMA foreign_keys = OFF;";
        command.ExecuteNonQuery();

        return new SqlServerInMemorySqliteConnection(connection);
    }
}
