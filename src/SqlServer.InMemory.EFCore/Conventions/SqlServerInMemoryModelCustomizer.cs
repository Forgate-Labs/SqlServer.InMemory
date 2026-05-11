using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;

namespace SqlServer.InMemory.EFCore.Conventions;

internal sealed class SqlServerInMemoryModelCustomizer : ModelCustomizer
{
    private readonly SqlServerInMemoryOptions _options;

    public SqlServerInMemoryModelCustomizer(ModelCustomizerDependencies dependencies, SqlServerInMemoryOptions options)
        : base(dependencies)
    {
        _options = options;
    }

    public override void Customize(ModelBuilder modelBuilder, DbContext context)
    {
        base.Customize(modelBuilder, context);

        if (!_options.NormalizeSchemas)
        {
            return;
        }

        foreach (var entityType in modelBuilder.Model.GetEntityTypes())
        {
            var tableName = entityType.GetTableName();
            var schema = entityType.GetSchema();
            if (string.IsNullOrWhiteSpace(tableName) || string.IsNullOrWhiteSpace(schema))
            {
                continue;
            }

            var normalizedName = _options.SchemaMode == SqlServerInMemorySchemaMode.PrefixSchemaName
                ? $"{schema}_{tableName}"
                : tableName;

            entityType.SetTableName(normalizedName);
            entityType.SetSchema(null);
        }
    }
}
