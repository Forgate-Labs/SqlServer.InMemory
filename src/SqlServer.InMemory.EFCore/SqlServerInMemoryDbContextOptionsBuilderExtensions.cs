using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata.Conventions.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using SqlServer.InMemory.EFCore.Conventions;
using SqlServer.InMemory.EFCore.Interceptors;
using SqlServer.InMemory.EFCore.Migrations;
using SqlServer.InMemory.Sqlite;
using SqlServer.InMemory.TSql;

namespace SqlServer.InMemory.EFCore;

public static class SqlServerInMemoryDbContextOptionsBuilderExtensions
{
    public static DbContextOptionsBuilder UseSqlServerInMemory(
        this DbContextOptionsBuilder optionsBuilder,
        string databaseName,
        Action<SqlServerInMemoryOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(optionsBuilder);
        ArgumentException.ThrowIfNullOrWhiteSpace(databaseName);

        var options = new SqlServerInMemoryOptions();
        configure?.Invoke(options);

        var translator = new BasicTSqlTranslator();
        var connection = new SqliteInMemoryConnectionFactory().Create(databaseName, options);
        var serviceProvider = new ServiceCollection()
            .AddEntityFrameworkSqlite()
            .AddSingleton(options)
            .AddSingleton<ITSqlTranslator>(translator)
            .AddSingleton<IConventionSetPlugin, SqlServerInMemoryConventionSetPlugin>()
            .AddSingleton<IModelCustomizer, SqlServerInMemoryModelCustomizer>()
            .AddScoped<IMigrationsAssembly, SqlServerInMemoryMigrationsAssembly>()
            .AddScoped<IMigrationsSqlGenerator, SqlServerInMemoryMigrationsSqlGenerator>()
            .BuildServiceProvider();

        return optionsBuilder
            .UseInternalServiceProvider(serviceProvider)
            .UseSqlite(connection)
            .AddInterceptors(new SqlServerInMemoryCommandInterceptor(translator, options));
    }

    public static DbContextOptionsBuilder<TContext> UseSqlServerInMemory<TContext>(
        this DbContextOptionsBuilder<TContext> optionsBuilder,
        string databaseName,
        Action<SqlServerInMemoryOptions>? configure = null)
        where TContext : DbContext
    {
        UseSqlServerInMemory((DbContextOptionsBuilder)optionsBuilder, databaseName, configure);
        return optionsBuilder;
    }
}
