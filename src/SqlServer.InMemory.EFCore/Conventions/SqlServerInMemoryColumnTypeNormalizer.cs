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

        var canonicalType = SqlServerInMemoryTypeMapper.NormalizeSqlServerTypeName(columnType);

        if (IsSqlServerBigIntIdentityPrimaryKey(entityType, property, canonicalType))
        {
            return "INTEGER";
        }

        var sqliteType = SqlServerInMemoryTypeMapper.ToSqliteType(columnType);
        return sqliteType.Equals(columnType, StringComparison.Ordinal) ? null : sqliteType;
    }

    private static bool IsSqlServerBigIntIdentityPrimaryKey(IReadOnlyEntityType entityType, IReadOnlyProperty property, string canonicalType)
    {
        return canonicalType == "bigint"
            && property.ClrType == typeof(long)
            && property.ValueGenerated == ValueGenerated.OnAdd
            && entityType.FindPrimaryKey()?.Properties.Contains(property) == true;
    }
}
