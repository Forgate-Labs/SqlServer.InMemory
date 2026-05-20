namespace SqlServer.InMemory;

public static class SqlServerInMemoryTypeMapper
{
    public static string NormalizeSqlServerTypeName(string columnType)
    {
        ArgumentNullException.ThrowIfNull(columnType);

        var buffer = new char[columnType.Length];
        var length = 0;

        foreach (var character in columnType)
        {
            if (char.IsWhiteSpace(character) || character is '[' or ']')
            {
                continue;
            }

            buffer[length++] = char.ToLowerInvariant(character);
        }

        return new string(buffer, 0, length);
    }

    public static string ToSqliteType(string columnType)
    {
        ArgumentNullException.ThrowIfNull(columnType);

        var normalized = NormalizeSqlServerTypeName(columnType);
        if (normalized.StartsWith("nvarchar", StringComparison.Ordinal) ||
            normalized.StartsWith("varchar", StringComparison.Ordinal) ||
            normalized.StartsWith("nchar", StringComparison.Ordinal) ||
            normalized.StartsWith("char", StringComparison.Ordinal) ||
            normalized is "text" or "ntext" or "xml" or "uniqueidentifier")
        {
            return "TEXT";
        }

        if (normalized.StartsWith("varbinary", StringComparison.Ordinal) ||
            normalized.StartsWith("binary", StringComparison.Ordinal) ||
            normalized is "image" or "rowversion" or "timestamp")
        {
            return "BLOB";
        }

        if (normalized is "bit" or "tinyint" or "smallint" or "int" or "bigint")
        {
            return "INTEGER";
        }

        if (normalized.StartsWith("decimal", StringComparison.Ordinal) ||
            normalized.StartsWith("numeric", StringComparison.Ordinal) ||
            normalized is "money" or "smallmoney" or "float" or "real")
        {
            return "REAL";
        }

        if (normalized.StartsWith("datetime", StringComparison.Ordinal) ||
            normalized is "smalldatetime" or "datetimeoffset" or "date" or "time")
        {
            return "TEXT";
        }

        return columnType;
    }

    public static int? TryGetSqlServerColumnLength(string columnType)
    {
        ArgumentNullException.ThrowIfNull(columnType);

        var normalized = NormalizeSqlServerTypeName(columnType);
        var openParen = normalized.IndexOf('(', StringComparison.Ordinal);
        var closeParen = normalized.LastIndexOf(')');
        var baseType = openParen < 0 ? normalized : normalized[..openParen];

        if (openParen < 0 || closeParen <= openParen)
        {
            return null;
        }

        var lengthText = normalized[(openParen + 1)..closeParen];
        if (lengthText.Equals("max", StringComparison.Ordinal))
        {
            return -1;
        }

        var comma = lengthText.IndexOf(',', StringComparison.Ordinal);
        if (comma >= 0)
        {
            lengthText = lengthText[..comma];
        }

        if (!int.TryParse(lengthText, out var length))
        {
            return null;
        }

        return baseType switch
        {
            "nvarchar" or "nchar" => length * 2,
            "varchar" or "char" or "varbinary" or "binary" => length,
            _ => null
        };
    }
}
