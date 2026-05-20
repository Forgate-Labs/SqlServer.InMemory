using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Internal;

namespace SqlServer.InMemory.EFCore.Migrations;

#pragma warning disable EF1001
internal sealed class SqlServerInMemoryMigrationsAssembly : MigrationsAssembly
{
    private const string SqlServerProviderName = "Microsoft.EntityFrameworkCore.SqlServer";

    public SqlServerInMemoryMigrationsAssembly(
        ICurrentDbContext currentContext,
        IDbContextOptions options,
        IMigrationsIdGenerator idGenerator,
        IDiagnosticsLogger<DbLoggerCategory.Migrations> logger)
        : base(currentContext, options, idGenerator, logger)
    {
    }

    public override Migration CreateMigration(TypeInfo migrationClass, string activeProvider)
    {
        return base.CreateMigration(migrationClass, SqlServerProviderName);
    }
}
#pragma warning restore EF1001
