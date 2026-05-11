using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata.Conventions.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using SqlServer.InMemory.EFCore.Conventions;
using SqlServer.InMemory.EFCore.Interceptors;
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

        var connection = new SqliteInMemoryConnectionFactory().Create(databaseName, options);
        var serviceProvider = new ServiceCollection()
            .AddEntityFrameworkSqlite()
            .AddSingleton(options)
            .AddSingleton<IConventionSetPlugin, SqlServerInMemoryConventionSetPlugin>()
            .AddSingleton<IModelCustomizer, SqlServerInMemoryModelCustomizer>()
            .BuildServiceProvider();

        return optionsBuilder
            .UseInternalServiceProvider(serviceProvider)
            .UseSqlite(connection)
            .AddInterceptors(new SqlServerInMemoryCommandInterceptor(new BasicTSqlTranslator()));
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
