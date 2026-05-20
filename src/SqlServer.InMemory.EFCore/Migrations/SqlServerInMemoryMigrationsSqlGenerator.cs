using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using SqlServer.InMemory.Exceptions;
using SqlServer.InMemory.TSql;

namespace SqlServer.InMemory.EFCore.Migrations;

internal sealed partial class SqlServerInMemoryMigrationsSqlGenerator : SqliteMigrationsSqlGenerator
{
    private readonly SqlServerInMemoryOptions options;
    private readonly ITSqlTranslator translator;

    public SqlServerInMemoryMigrationsSqlGenerator(
        MigrationsSqlGeneratorDependencies dependencies,
        IRelationalAnnotationProvider migrationsAnnotations,
        SqlServerInMemoryOptions options,
        ITSqlTranslator translator)
        : base(dependencies, migrationsAnnotations)
    {
        this.options = options;
        this.translator = translator;
    }

    public override IReadOnlyList<MigrationCommand> Generate(
        IReadOnlyList<MigrationOperation> operations,
        IModel? model = null,
        MigrationsSqlGenerationOptions options = MigrationsSqlGenerationOptions.Default)
    {
        ArgumentNullException.ThrowIfNull(operations);

        var normalizedOperations = new List<MigrationOperation>(operations.Count);
        foreach (var operation in operations)
        {
            normalizedOperations.AddRange(NormalizeOperation(operation, model));
        }

        return base.Generate(normalizedOperations, model, options);
    }

    private IReadOnlyList<MigrationOperation> NormalizeOperation(MigrationOperation operation, IModel? model)
    {
        switch (operation)
        {
            case EnsureSchemaOperation:
            case DropSchemaOperation:
                return [];

            case CreateSequenceOperation:
            case AlterSequenceOperation:
            case DropSequenceOperation:
            case RenameSequenceOperation:
            case RestartSequenceOperation:
                throw Unsupported(operation);

            case CreateTableOperation createTable:
                NormalizeCreateTable(createTable);
                break;

            case DropTableOperation dropTable:
                NormalizeTable(dropTable);
                break;

            case RenameTableOperation renameTable:
                NormalizeRenameTable(renameTable);
                break;

            case AddColumnOperation addColumn:
                NormalizeTable(addColumn);
                NormalizeColumn(addColumn);
                break;

            case AlterColumnOperation alterColumn:
                NormalizeTable(alterColumn);
                NormalizeColumn(alterColumn);
                NormalizeColumn(alterColumn.OldColumn);
                return CreateTableRebuildOperations(alterColumn.Table, alterColumn.Schema, model);

            case DropColumnOperation dropColumn:
                NormalizeTable(dropColumn);
                return CreateTableRebuildOperations(dropColumn.Table, dropColumn.Schema, model);

            case RenameColumnOperation renameColumn:
                NormalizeTable(renameColumn);
                break;

            case CreateIndexOperation createIndex:
                NormalizeTable(createIndex);
                createIndex.Filter = NormalizeSql(createIndex.Filter);
                break;

            case DropIndexOperation dropIndex:
                NormalizeTable(dropIndex);
                break;

            case RenameIndexOperation renameIndex:
                NormalizeTable(renameIndex);
                break;

            case AddForeignKeyOperation foreignKey:
                NormalizeForeignKey(foreignKey);
                return CreateTableRebuildOperations(foreignKey.Table, foreignKey.Schema, model);

            case DropForeignKeyOperation dropForeignKey:
                NormalizeTable(dropForeignKey);
                return CreateTableRebuildOperations(dropForeignKey.Table, dropForeignKey.Schema, model);

            case AddPrimaryKeyOperation primaryKey:
                NormalizeTable(primaryKey);
                return CreateTableRebuildOperations(primaryKey.Table, primaryKey.Schema, model);

            case DropPrimaryKeyOperation dropPrimaryKey:
                NormalizeTable(dropPrimaryKey);
                return CreateTableRebuildOperations(dropPrimaryKey.Table, dropPrimaryKey.Schema, model);

            case AddUniqueConstraintOperation uniqueConstraint:
                NormalizeTable(uniqueConstraint);
                return CreateTableRebuildOperations(uniqueConstraint.Table, uniqueConstraint.Schema, model);

            case DropUniqueConstraintOperation dropUniqueConstraint:
                NormalizeTable(dropUniqueConstraint);
                return CreateTableRebuildOperations(dropUniqueConstraint.Table, dropUniqueConstraint.Schema, model);

            case AddCheckConstraintOperation checkConstraint:
                NormalizeTable(checkConstraint);
                checkConstraint.Sql = NormalizeRequiredSql(checkConstraint.Sql);
                return CreateTableRebuildOperations(checkConstraint.Table, checkConstraint.Schema, model);

            case DropCheckConstraintOperation dropCheckConstraint:
                NormalizeTable(dropCheckConstraint);
                return CreateTableRebuildOperations(dropCheckConstraint.Table, dropCheckConstraint.Schema, model);

            case InsertDataOperation insertData:
                NormalizeTable(insertData);
                break;

            case UpdateDataOperation updateData:
                NormalizeTable(updateData);
                break;

            case DeleteDataOperation deleteData:
                NormalizeTable(deleteData);
                break;

            case SqlOperation:
                break;
        }

        return [operation];
    }

    private void NormalizeCreateTable(CreateTableOperation operation)
    {
        var tableName = NormalizeTableName(operation.Name, operation.Schema);
        operation.Name = tableName;
        operation.Schema = NormalizeSchema(operation.Schema);

        foreach (var column in operation.Columns)
        {
            column.Table = operation.Name;
            column.Schema = operation.Schema;
            NormalizeColumn(column);
        }

        operation.PrimaryKey?.SetTable(operation.Name, operation.Schema);

        foreach (var uniqueConstraint in operation.UniqueConstraints)
        {
            uniqueConstraint.SetTable(operation.Name, operation.Schema);
        }

        foreach (var checkConstraint in operation.CheckConstraints)
        {
            checkConstraint.SetTable(operation.Name, operation.Schema);
            checkConstraint.Sql = NormalizeRequiredSql(checkConstraint.Sql);
        }

        foreach (var foreignKey in operation.ForeignKeys)
        {
            foreignKey.Table = operation.Name;
            foreignKey.Schema = operation.Schema;
            NormalizePrincipalTable(foreignKey);
        }
    }

    private void NormalizeForeignKey(AddForeignKeyOperation operation)
    {
        NormalizeTable(operation);
        NormalizePrincipalTable(operation);
    }

    private void NormalizePrincipalTable(AddForeignKeyOperation operation)
    {
        operation.PrincipalTable = NormalizeTableName(operation.PrincipalTable, operation.PrincipalSchema);
        operation.PrincipalSchema = NormalizeSchema(operation.PrincipalSchema);
    }

    private void NormalizeRenameTable(RenameTableOperation operation)
    {
        operation.Name = NormalizeTableName(operation.Name, operation.Schema);
        operation.Schema = NormalizeSchema(operation.Schema);

        if (!string.IsNullOrWhiteSpace(operation.NewName))
        {
            operation.NewName = NormalizeTableName(operation.NewName, operation.NewSchema);
        }

        operation.NewSchema = NormalizeSchema(operation.NewSchema);
    }

    private void NormalizeColumn(ColumnOperation operation)
    {
        operation.ColumnType = NormalizeColumnType(operation.ColumnType);
        operation.DefaultValueSql = NormalizeDefaultValueSql(operation.DefaultValueSql);
        operation.ComputedColumnSql = NormalizeSql(operation.ComputedColumnSql);
    }

    private string? NormalizeDefaultValueSql(string? sql)
    {
        if (string.IsNullOrWhiteSpace(sql))
        {
            return sql;
        }

        var normalized = RemoveOuterParentheses(sql.Trim());
        if (IsSqlFunction(normalized, "getdate") ||
            IsSqlFunction(normalized, "getutcdate") ||
            IsSqlFunction(normalized, "sysdatetime") ||
            IsSqlFunction(normalized, "sysutcdatetime"))
        {
            return "CURRENT_TIMESTAMP";
        }

        if (IsSqlFunction(normalized, "newid") || IsSqlFunction(normalized, "newsequentialid"))
        {
            throw new SqlServerInMemoryMigrationException(
                $"SqlServer.InMemory does not support SQL Server default value SQL '{sql}'. Use a client-generated value or add translator support before using MigrateAsync.");
        }

        return translator.TranslateToSqlite(sql);
    }

    private string? NormalizeSql(string? sql)
    {
        return string.IsNullOrWhiteSpace(sql) ? sql : translator.TranslateToSqlite(sql);
    }

    private string NormalizeRequiredSql(string sql)
    {
        return string.IsNullOrWhiteSpace(sql) ? sql : translator.TranslateToSqlite(sql);
    }

    private string? NormalizeColumnType(string? columnType)
    {
        return string.IsNullOrWhiteSpace(columnType)
            ? columnType
            : SqlServerInMemoryTypeMapper.ToSqliteType(columnType);
    }

    private void NormalizeTable(TableOperation operation)
    {
        operation.Name = NormalizeTableName(operation.Name, operation.Schema);
        operation.Schema = NormalizeSchema(operation.Schema);
    }

    private void NormalizeTable(MigrationOperation operation)
    {
        switch (operation)
        {
            case AddColumnOperation addColumn:
                addColumn.Table = NormalizeTableName(addColumn.Table, addColumn.Schema);
                addColumn.Schema = NormalizeSchema(addColumn.Schema);
                break;
            case AlterColumnOperation alterColumn:
                alterColumn.Table = NormalizeTableName(alterColumn.Table, alterColumn.Schema);
                alterColumn.Schema = NormalizeSchema(alterColumn.Schema);
                break;
            case DropColumnOperation dropColumn:
                dropColumn.Table = NormalizeTableName(dropColumn.Table, dropColumn.Schema);
                dropColumn.Schema = NormalizeSchema(dropColumn.Schema);
                break;
            case RenameColumnOperation renameColumn:
                renameColumn.Table = NormalizeTableName(renameColumn.Table, renameColumn.Schema);
                renameColumn.Schema = NormalizeSchema(renameColumn.Schema);
                break;
            case CreateIndexOperation createIndex:
                createIndex.Table = NormalizeTableName(createIndex.Table, createIndex.Schema);
                createIndex.Schema = NormalizeSchema(createIndex.Schema);
                break;
            case DropIndexOperation dropIndex when !string.IsNullOrWhiteSpace(dropIndex.Table):
                dropIndex.Table = NormalizeTableName(dropIndex.Table, dropIndex.Schema);
                dropIndex.Schema = NormalizeSchema(dropIndex.Schema);
                break;
            case RenameIndexOperation renameIndex when !string.IsNullOrWhiteSpace(renameIndex.Table):
                renameIndex.Table = NormalizeTableName(renameIndex.Table, renameIndex.Schema);
                renameIndex.Schema = NormalizeSchema(renameIndex.Schema);
                break;
            case AddForeignKeyOperation foreignKey:
                foreignKey.Table = NormalizeTableName(foreignKey.Table, foreignKey.Schema);
                foreignKey.Schema = NormalizeSchema(foreignKey.Schema);
                break;
            case DropForeignKeyOperation dropForeignKey:
                dropForeignKey.Table = NormalizeTableName(dropForeignKey.Table, dropForeignKey.Schema);
                dropForeignKey.Schema = NormalizeSchema(dropForeignKey.Schema);
                break;
            case AddPrimaryKeyOperation primaryKey:
                primaryKey.Table = NormalizeTableName(primaryKey.Table, primaryKey.Schema);
                primaryKey.Schema = NormalizeSchema(primaryKey.Schema);
                break;
            case DropPrimaryKeyOperation dropPrimaryKey:
                dropPrimaryKey.Table = NormalizeTableName(dropPrimaryKey.Table, dropPrimaryKey.Schema);
                dropPrimaryKey.Schema = NormalizeSchema(dropPrimaryKey.Schema);
                break;
            case AddUniqueConstraintOperation uniqueConstraint:
                uniqueConstraint.Table = NormalizeTableName(uniqueConstraint.Table, uniqueConstraint.Schema);
                uniqueConstraint.Schema = NormalizeSchema(uniqueConstraint.Schema);
                break;
            case DropUniqueConstraintOperation dropUniqueConstraint:
                dropUniqueConstraint.Table = NormalizeTableName(dropUniqueConstraint.Table, dropUniqueConstraint.Schema);
                dropUniqueConstraint.Schema = NormalizeSchema(dropUniqueConstraint.Schema);
                break;
            case AddCheckConstraintOperation checkConstraint:
                checkConstraint.Table = NormalizeTableName(checkConstraint.Table, checkConstraint.Schema);
                checkConstraint.Schema = NormalizeSchema(checkConstraint.Schema);
                break;
            case DropCheckConstraintOperation dropCheckConstraint:
                dropCheckConstraint.Table = NormalizeTableName(dropCheckConstraint.Table, dropCheckConstraint.Schema);
                dropCheckConstraint.Schema = NormalizeSchema(dropCheckConstraint.Schema);
                break;
            case InsertDataOperation insertData:
                insertData.Table = NormalizeTableName(insertData.Table, insertData.Schema);
                insertData.Schema = NormalizeSchema(insertData.Schema);
                break;
            case UpdateDataOperation updateData:
                updateData.Table = NormalizeTableName(updateData.Table, updateData.Schema);
                updateData.Schema = NormalizeSchema(updateData.Schema);
                break;
            case DeleteDataOperation deleteData:
                deleteData.Table = NormalizeTableName(deleteData.Table, deleteData.Schema);
                deleteData.Schema = NormalizeSchema(deleteData.Schema);
                break;
        }
    }

    private IReadOnlyList<MigrationOperation> CreateTableRebuildOperations(string tableName, string? schema, IModel? model)
    {
        if (model is null)
        {
            throw new SqlServerInMemoryMigrationException(
                $"SqlServer.InMemory needs the target migration model to rebuild table '{tableName}'. Ensure migrations include their generated target model.");
        }

        var table = FindTable(model, tableName, schema);
        if (table is null)
        {
            throw new SqlServerInMemoryMigrationException(
                $"SqlServer.InMemory could not find table '{tableName}' in the target migration model while rebuilding a SQLite table.");
        }

        if (table.IsExcludedFromMigrations)
        {
            return [];
        }

        var tempTableName = $"__sqlserver_inmemory_rebuild_{tableName}";
        var columns = table.Columns
            .OrderBy(static column => column.Order ?? int.MaxValue)
            .ThenBy(static column => column.Name, StringComparer.Ordinal)
            .ToArray();
        var copyColumns = columns
            .Where(static column => string.IsNullOrWhiteSpace(column.ComputedColumnSql))
            .Select(static column => column.Name)
            .ToArray();

        var operations = new List<MigrationOperation>
        {
            CreateSqlOperation("PRAGMA foreign_keys = OFF"),
            CreateSqlOperation($"DROP TABLE IF EXISTS {QuoteIdentifier(tempTableName)}"),
            CreateSqlOperation(BuildCreateTableSql(table, tempTableName, columns)),
        };

        if (copyColumns.Length > 0)
        {
            var columnList = string.Join(", ", copyColumns.Select(QuoteIdentifier));
            operations.Add(CreateSqlOperation($"INSERT INTO {QuoteIdentifier(tempTableName)} ({columnList}) SELECT {columnList} FROM {QuoteIdentifier(tableName)}"));
        }

        operations.Add(CreateSqlOperation($"DROP TABLE {QuoteIdentifier(tableName)}"));
        operations.Add(CreateSqlOperation($"ALTER TABLE {QuoteIdentifier(tempTableName)} RENAME TO {QuoteIdentifier(tableName)}"));

        foreach (var index in table.Indexes.OrderBy(static index => index.Name, StringComparer.Ordinal))
        {
            operations.Add(CreateSqlOperation(BuildCreateIndexSql(index, tableName)));
        }

        operations.Add(CreateSqlOperation("PRAGMA foreign_keys = ON"));
        operations.Add(CreateSqlOperation("PRAGMA foreign_key_check"));

        return operations;
    }

    private ITable? FindTable(IModel model, string tableName, string? schema)
    {
        return model.GetRelationalModel().Tables.FirstOrDefault(table =>
            string.Equals(NormalizeTableName(table.Name, table.Schema), tableName, StringComparison.Ordinal) &&
            string.Equals(NormalizeSchema(table.Schema), schema, StringComparison.Ordinal));
    }

    private string BuildCreateTableSql(ITable table, string tableName, IReadOnlyList<IColumn> columns)
    {
        var definitions = new List<string>();
        definitions.AddRange(columns.Select(BuildColumnDefinition));

        if (table.PrimaryKey is not null)
        {
            definitions.Add(BuildPrimaryKeyConstraint(table.PrimaryKey));
        }

        definitions.AddRange(table.UniqueConstraints
            .Where(static constraint => !constraint.GetIsPrimaryKey())
            .Select(BuildUniqueConstraint));
        definitions.AddRange(table.CheckConstraints.Select(BuildCheckConstraint));
        definitions.AddRange(table.ForeignKeyConstraints.Select(BuildForeignKeyConstraint));

        var builder = new StringBuilder();
        builder.Append("CREATE TABLE ").Append(QuoteIdentifier(tableName)).AppendLine(" (");
        builder.Append("    ").AppendJoin("," + Environment.NewLine + "    ", definitions);
        builder.AppendLine().Append(')');
        return builder.ToString();
    }

    private string BuildColumnDefinition(IColumn column)
    {
        var builder = new StringBuilder();
        builder.Append(QuoteIdentifier(column.Name)).Append(' ');

        if (!string.IsNullOrWhiteSpace(column.ComputedColumnSql))
        {
            builder.Append("AS (").Append(NormalizeRequiredSql(column.ComputedColumnSql)).Append(')');
            if (column.IsStored == true)
            {
                builder.Append(" STORED");
            }

            return builder.ToString();
        }

        builder.Append(NormalizeColumnType(column.StoreType) ?? column.StoreType);

        if (!column.IsNullable)
        {
            builder.Append(" NOT NULL");
        }

        if (!string.IsNullOrWhiteSpace(column.DefaultValueSql))
        {
            builder.Append(" DEFAULT (").Append(NormalizeDefaultValueSql(column.DefaultValueSql)).Append(')');
        }
        else if (column.DefaultValue is not null)
        {
            builder.Append(" DEFAULT ").Append(FormatSqlLiteral(column.DefaultValue));
        }

        return builder.ToString();
    }

    private string BuildPrimaryKeyConstraint(IUniqueConstraint constraint)
    {
        return $"CONSTRAINT {QuoteIdentifier(constraint.Name)} PRIMARY KEY ({JoinColumns(constraint.Columns)})";
    }

    private string BuildUniqueConstraint(IUniqueConstraint constraint)
    {
        return $"CONSTRAINT {QuoteIdentifier(constraint.Name)} UNIQUE ({JoinColumns(constraint.Columns)})";
    }

    private string BuildCheckConstraint(ICheckConstraint constraint)
    {
        return string.IsNullOrWhiteSpace(constraint.Name)
            ? $"CHECK ({NormalizeRequiredSql(constraint.Sql)})"
            : $"CONSTRAINT {QuoteIdentifier(constraint.Name)} CHECK ({NormalizeRequiredSql(constraint.Sql)})";
    }

    private string BuildForeignKeyConstraint(IForeignKeyConstraint constraint)
    {
        var principalTableName = NormalizeTableName(constraint.PrincipalTable.Name, constraint.PrincipalTable.Schema);
        var principalColumns = JoinColumns(constraint.PrincipalColumns);
        var builder = new StringBuilder();
        builder.Append("CONSTRAINT ").Append(QuoteIdentifier(constraint.Name))
            .Append(" FOREIGN KEY (").Append(JoinColumns(constraint.Columns)).Append(')')
            .Append(" REFERENCES ").Append(QuoteIdentifier(principalTableName))
            .Append(" (").Append(principalColumns).Append(')');

        var onDelete = GetReferentialActionSql(constraint.OnDeleteAction);
        if (onDelete is not null)
        {
            builder.Append(" ON DELETE ").Append(onDelete);
        }

        return builder.ToString();
    }

    private string BuildCreateIndexSql(ITableIndex index, string tableName)
    {
        var builder = new StringBuilder();
        builder.Append("CREATE ");
        if (index.IsUnique)
        {
            builder.Append("UNIQUE ");
        }

        builder.Append("INDEX ").Append(QuoteIdentifier(index.Name))
            .Append(" ON ").Append(QuoteIdentifier(tableName))
            .Append(" (").Append(JoinColumns(index.Columns)).Append(')');

        if (!string.IsNullOrWhiteSpace(index.Filter))
        {
            builder.Append(" WHERE ").Append(NormalizeRequiredSql(index.Filter));
        }

        return builder.ToString();
    }

    private static SqlOperation CreateSqlOperation(string sql)
    {
        return new SqlOperation
        {
            Sql = sql,
            SuppressTransaction = true
        };
    }

    private static string JoinColumns(IEnumerable<IColumn> columns)
    {
        return string.Join(", ", columns.Select(static column => QuoteIdentifier(column.Name)));
    }

    private static string? GetReferentialActionSql(ReferentialAction action)
    {
        return action switch
        {
            ReferentialAction.Cascade => "CASCADE",
            ReferentialAction.SetNull => "SET NULL",
            ReferentialAction.SetDefault => "SET DEFAULT",
            ReferentialAction.Restrict => "RESTRICT",
            _ => null
        };
    }

    private static string FormatSqlLiteral(object value)
    {
        return value switch
        {
            bool boolValue => boolValue ? "1" : "0",
            string stringValue => $"'{stringValue.Replace("'", "''", StringComparison.Ordinal)}'",
            char charValue => $"'{charValue.ToString().Replace("'", "''", StringComparison.Ordinal)}'",
            DateTime dateTimeValue => $"'{dateTimeValue:O}'",
            DateTimeOffset dateTimeOffsetValue => $"'{dateTimeOffsetValue:O}'",
            Guid guidValue => $"'{guidValue}'",
            byte[] bytes => "X'" + Convert.ToHexString(bytes) + "'",
            IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
            _ => $"'{value.ToString()?.Replace("'", "''", StringComparison.Ordinal)}'"
        };
    }

    private static string QuoteIdentifier(string identifier)
    {
        return $"\"{identifier.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";
    }

    private string NormalizeTableName(string tableName, string? schema)
    {
        if (!options.NormalizeSchemas || string.IsNullOrWhiteSpace(schema))
        {
            return tableName;
        }

        if (schema.Equals("dbo", StringComparison.OrdinalIgnoreCase))
        {
            return tableName;
        }

        return options.SchemaMode == SqlServerInMemorySchemaMode.PrefixSchemaName
            ? $"{schema}_{tableName}"
            : tableName;
    }

    private string? NormalizeSchema(string? schema)
    {
        return options.NormalizeSchemas ? null : schema;
    }

    private static SqlServerInMemoryMigrationException Unsupported(MigrationOperation operation)
    {
        return new SqlServerInMemoryMigrationException(
            $"SqlServer.InMemory does not support SQL Server migration operation '{operation.GetType().Name}'. Add translator support before using MigrateAsync with this operation.");
    }

    private static string RemoveOuterParentheses(string sql)
    {
        while (sql.Length > 1 && sql[0] == '(' && sql[^1] == ')')
        {
            sql = sql[1..^1].Trim();
        }

        return sql;
    }

    private static bool IsSqlFunction(string sql, string functionName)
    {
        return Regex.IsMatch(sql, "^" + Regex.Escape(functionName) + @"\s*\(\s*\)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }
}

internal static class SqlServerInMemoryMigrationOperationExtensions
{
    public static void SetTable(this AddPrimaryKeyOperation operation, string table, string? schema)
    {
        operation.Table = table;
        operation.Schema = schema;
    }

    public static void SetTable(this AddUniqueConstraintOperation operation, string table, string? schema)
    {
        operation.Table = table;
        operation.Schema = schema;
    }

    public static void SetTable(this AddCheckConstraintOperation operation, string table, string? schema)
    {
        operation.Table = table;
        operation.Schema = schema;
    }
}
