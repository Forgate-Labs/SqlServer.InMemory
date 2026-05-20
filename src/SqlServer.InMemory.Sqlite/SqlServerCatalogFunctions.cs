using Microsoft.Data.Sqlite;

namespace SqlServer.InMemory.Sqlite;

internal static class SqlServerCatalogFunctions
{
    public static void Register(SqliteConnection connection, SqlServerInMemoryOptions options)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(options);

        connection.CreateFunction<string?, string?, long?>(
            "OBJECT_ID",
            (name, type) => ObjectId(connection, options, name, type));

        connection.CreateFunction<string?, long?>(
            "OBJECT_ID",
            name => ObjectId(connection, options, name, null));

        connection.CreateFunction<string?, string?, long?>(
            "COL_LENGTH",
            (tableName, columnName) => ColumnLength(connection, options, tableName, columnName));
    }

    private static long? ObjectId(SqliteConnection connection, SqlServerInMemoryOptions options, string? name, string? type)
    {
        if (string.IsNullOrWhiteSpace(name) || !IsUserTableType(type))
        {
            return null;
        }

        var tableName = FindExistingTable(connection, GetTableNameCandidates(name, options));
        return tableName is null ? null : Math.Abs(StringComparer.OrdinalIgnoreCase.GetHashCode(tableName)) + 1L;
    }

    private static long? ColumnLength(SqliteConnection connection, SqlServerInMemoryOptions options, string? tableName, string? columnName)
    {
        if (string.IsNullOrWhiteSpace(tableName) || string.IsNullOrWhiteSpace(columnName))
        {
            return null;
        }

        var sqliteTableName = FindExistingTable(connection, GetTableNameCandidates(tableName, options));
        if (sqliteTableName is null)
        {
            return null;
        }

        using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info(\"{sqliteTableName.Replace("\"", "\"\"")}\");";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var currentColumnName = reader.GetString(1);
            if (!currentColumnName.Equals(UnquoteName(columnName), StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var storeType = reader.IsDBNull(2) ? string.Empty : reader.GetString(2);
            return SqlServerInMemoryTypeMapper.TryGetSqlServerColumnLength(storeType) ?? 1;
        }

        return null;
    }

    private static bool IsUserTableType(string? type)
    {
        if (string.IsNullOrWhiteSpace(type))
        {
            return true;
        }

        return UnquoteSqlString(type).Equals("U", StringComparison.OrdinalIgnoreCase);
    }

    private static string? FindExistingTable(SqliteConnection connection, IEnumerable<string> candidates)
    {
        foreach (var candidate in candidates)
        {
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT name FROM sqlite_master WHERE type IN ('table', 'view') AND lower(name) = lower($name) LIMIT 1;";
            command.Parameters.AddWithValue("$name", candidate);
            var result = command.ExecuteScalar();
            if (result is string name)
            {
                return name;
            }
        }

        return null;
    }

    private static IEnumerable<string> GetTableNameCandidates(string sqlServerName, SqlServerInMemoryOptions options)
    {
        var normalized = UnquoteSqlString(sqlServerName).Trim();
        normalized = normalized.Replace("[", string.Empty, StringComparison.Ordinal).Replace("]", string.Empty, StringComparison.Ordinal);
        normalized = normalized.Replace("\"", string.Empty, StringComparison.Ordinal);

        var parts = normalized.Split('.', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 2)
        {
            var schema = parts[0];
            var table = parts[1];
            if (options.NormalizeSchemas)
            {
                if (schema.Equals("dbo", StringComparison.OrdinalIgnoreCase))
                {
                    yield return table;
                }

                if (options.SchemaMode == SqlServerInMemorySchemaMode.PrefixSchemaName)
                {
                    yield return $"{schema}_{table}";
                }

                if (!schema.Equals("dbo", StringComparison.OrdinalIgnoreCase))
                {
                    yield return table;
                }
            }
            else
            {
                yield return normalized;
            }

            yield return normalized;
            yield break;
        }

        yield return normalized;
    }

    private static string UnquoteName(string value)
    {
        return UnquoteSqlString(value).Trim().Trim('[', ']', '"');
    }

    private static string UnquoteSqlString(string value)
    {
        var text = value.Trim();
        if (text.StartsWith("N'", StringComparison.OrdinalIgnoreCase))
        {
            text = text[1..];
        }

        if (text.Length >= 2 && text[0] == '\'' && text[^1] == '\'')
        {
            text = text[1..^1].Replace("''", "'", StringComparison.Ordinal);
        }

        return text;
    }
}
