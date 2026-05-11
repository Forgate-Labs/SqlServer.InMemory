using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Metadata.Conventions;

namespace SqlServer.InMemory.EFCore.Conventions;

internal sealed class SqlServerInMemoryModelFinalizingConvention : IModelFinalizingConvention
{
    private readonly SqlServerInMemoryOptions _options;

    public SqlServerInMemoryModelFinalizingConvention(SqlServerInMemoryOptions options)
    {
        _options = options;
    }

    public void ProcessModelFinalizing(IConventionModelBuilder modelBuilder, IConventionContext<IConventionModelBuilder> context)
    {
        if (!_options.NormalizeSchemas)
        {
            return;
        }

        foreach (var entityType in modelBuilder.Metadata.GetEntityTypes())
        {
            var tableName = entityType.GetTableName() ?? entityType.ShortName();
            var schema = entityType.GetSchema();
            if (string.IsNullOrWhiteSpace(tableName) || string.IsNullOrWhiteSpace(schema))
            {
                continue;
            }

            var normalizedName = _options.SchemaMode == SqlServerInMemorySchemaMode.PrefixSchemaName
                ? $"{schema}_{tableName}"
                : tableName;

            entityType.Builder.HasAnnotation(RelationalAnnotationNames.TableName, normalizedName);
            entityType.Builder.HasAnnotation(RelationalAnnotationNames.Schema, null);
        }
    }
}
