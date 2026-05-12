using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace SqlServer.InMemory.EFCore.Conventions;

internal static class SqlServerInMemoryColumnTypeNormalizer
{
    public static void Normalize(IMutableEntityType entityType)
    {
        foreach (var property in entityType.GetProperties())
        {
            var normalizedType = GetNormalizedColumnType(entityType, property);
            if (normalizedType is null)
            {
                continue;
            }

            property.SetColumnType(normalizedType);
        }
    }

    public static void Normalize(IConventionEntityType entityType)
    {
        foreach (var property in entityType.GetProperties())
        {
            var normalizedType = GetNormalizedColumnType(entityType, property);
            if (normalizedType is null)
            {
                continue;
            }

            property.Builder.HasAnnotation(RelationalAnnotationNames.ColumnType, normalizedType);
        }
    }

    private static string? GetNormalizedColumnType(IReadOnlyEntityType entityType, IReadOnlyProperty property)
    {
        var columnType = property.GetColumnType();
        if (string.IsNullOrWhiteSpace(columnType))
        {
            return null;
        }

        var canonicalType = GetCanonicalColumnType(columnType);

        if (IsSqlServerBigIntIdentityPrimaryKey(entityType, property, canonicalType))
        {
            return "INTEGER";
        }

        return canonicalType switch
        {
            "nvarchar(max)" or "varchar(max)" or "ntext" or "text" => "TEXT",
            "varbinary(max)" or "image" => "BLOB",
            _ => null
        };
    }

    private static bool IsSqlServerBigIntIdentityPrimaryKey(IReadOnlyEntityType entityType, IReadOnlyProperty property, string canonicalType)
    {
        return canonicalType == "bigint"
            && property.ClrType == typeof(long)
            && property.ValueGenerated == ValueGenerated.OnAdd
            && entityType.FindPrimaryKey()?.Properties.Contains(property) == true;
    }

    private static string GetCanonicalColumnType(string columnType)
    {
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
}
