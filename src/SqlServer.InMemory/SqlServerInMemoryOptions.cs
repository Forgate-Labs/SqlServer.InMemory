namespace SqlServer.InMemory;

public sealed class SqlServerInMemoryOptions
{
    public bool EnforceForeignKeys { get; set; } = true;

    public bool UseCaseInsensitiveCollation { get; set; } = true;

    public bool NormalizeSchemas { get; set; } = true;

    public SqlServerInMemorySchemaMode SchemaMode { get; set; } = SqlServerInMemorySchemaMode.PrefixSchemaName;
}
