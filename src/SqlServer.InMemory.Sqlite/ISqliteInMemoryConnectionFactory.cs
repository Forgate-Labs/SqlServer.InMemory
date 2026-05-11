using System.Data.Common;

namespace SqlServer.InMemory.Sqlite;

public interface ISqliteInMemoryConnectionFactory
{
    DbConnection Create(string databaseName, SqlServerInMemoryOptions options);
}
