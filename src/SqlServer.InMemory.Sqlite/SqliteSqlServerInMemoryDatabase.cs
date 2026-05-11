using System.Data.Common;
using SqlServer.InMemory.Abstractions;

namespace SqlServer.InMemory.Sqlite;

public sealed class SqliteSqlServerInMemoryDatabase : ISqlServerInMemoryDatabase
{
    public SqliteSqlServerInMemoryDatabase(DbConnection connection)
    {
        Connection = connection;
    }

    public DbConnection Connection { get; }

    public async Task ResetAsync(CancellationToken cancellationToken = default)
    {
        var tables = new List<string>();

        await using (var command = Connection.CreateCommand())
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
            await using var delete = Connection.CreateCommand();
            delete.CommandText = $"DELETE FROM \"{table.Replace("\"", "\"\"")}\";";
            await delete.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    public void Dispose() => Connection.Dispose();

    public ValueTask DisposeAsync() => Connection.DisposeAsync();
}
