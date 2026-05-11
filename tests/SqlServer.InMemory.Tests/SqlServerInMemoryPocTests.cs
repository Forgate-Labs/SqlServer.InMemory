using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using SqlServer.InMemory;
using SqlServer.InMemory.EFCore;
using SqlServer.InMemory.Exceptions;
using SqlServer.InMemory.TSql;

namespace SqlServer.InMemory.Tests;

public sealed class SqlServerInMemoryPocTests
{
    [Fact]
    public async Task Can_create_database_insert_and_query_entity()
    {
        // Arrange
        await using var context = CreateContext();
        await context.Database.EnsureCreatedAsync();

        // Act
        context.Users.Add(new User { Name = "Eduardo" });
        await context.SaveChangesAsync();
        var user = await context.Users.SingleAsync(x => x.Name == "Eduardo");

        // Assert
        user.Name.Should().Be("Eduardo");
    }

    [Fact]
    public async Task Can_generate_identity_key()
    {
        // Arrange
        await using var context = CreateContext();
        await context.Database.EnsureCreatedAsync();
        var user = new User { Name = "Identity" };

        // Act
        context.Users.Add(user);
        await context.SaveChangesAsync();

        // Assert
        user.Id.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task Throws_when_foreign_key_is_invalid()
    {
        // Arrange
        await using var context = CreateContext();
        await context.Database.EnsureCreatedAsync();
        context.Orders.Add(new Order { CustomerId = 999, Total = 10m });

        // Act
        var act = () => context.SaveChangesAsync();

        // Assert
        var exception = await act.Should().ThrowAsync<Exception>();
        ContainsException<SqlServerInMemoryForeignKeyException>(exception.Which).Should().BeTrue();
    }

    [Fact]
    public async Task Throws_when_unique_index_is_duplicated()
    {
        // Arrange
        await using var context = CreateContext();
        await context.Database.EnsureCreatedAsync();
        context.Accounts.AddRange(
            new Account { Email = "duplicate@test.com" },
            new Account { Email = "duplicate@test.com" });

        // Act
        var act = () => context.SaveChangesAsync();

        // Assert
        var exception = await act.Should().ThrowAsync<Exception>();
        ContainsException<SqlServerInMemoryUniqueConstraintException>(exception.Which).Should().BeTrue();
    }

    [Fact]
    public async Task Throws_when_required_property_is_null()
    {
        // Arrange
        await using var context = CreateContext();
        await context.Database.EnsureCreatedAsync();
        context.RequiredItems.Add(new RequiredItem { Name = null! });

        // Act
        var act = () => context.SaveChangesAsync();

        // Assert
        await act.Should().ThrowAsync<DbUpdateException>();
    }

    [Fact]
    public async Task Can_use_schema_normalization()
    {
        // Arrange
        await using var context = CreateContext(options => options.SchemaMode = SqlServerInMemorySchemaMode.PrefixSchemaName);
        await context.Database.EnsureCreatedAsync();

        // Act
        var connection = context.Database.GetDbConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table' ORDER BY name;";
        var names = new List<string>();
        await using (var reader = await command.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
            {
                names.Add(reader.GetString(0));
            }
        }

        // Assert
        names.Should().Contain("auth_SchemaUsers");
    }

    [Fact]
    public async Task Can_use_case_insensitive_collation_when_configured_on_property()
    {
        // Arrange
        await using var context = CreateContext();
        await context.Database.EnsureCreatedAsync();
        context.Accounts.Add(new Account { Email = "Eduardo@Test.com" });
        await context.SaveChangesAsync();

        // Act
        var account = await context.Accounts.SingleOrDefaultAsync(x => x.Email == "eduardo@test.com");

        // Assert
        account.Should().NotBeNull();
    }

    [Fact]
    public async Task Can_translate_raw_sql_automatically()
    {
        // Arrange
        await using var context = CreateContext();
        await context.Database.EnsureCreatedAsync();

        // Act
        await context.Database.ExecuteSqlRawAsync("INSERT INTO [auth].[SchemaUsers] ([Name]) VALUES ('Raw User')");
        var user = await context.SchemaUsers
            .FromSqlRaw("SELECT TOP(1) [Id], [Name] FROM [auth].[SchemaUsers]")
            .SingleAsync();

        // Assert
        user.Name.Should().Be("Raw User");
    }

    [Fact]
    public void Can_prepare_tsql_translator_abstraction()
    {
        // Arrange
        ITSqlTranslator translator = new NoOpTSqlTranslator();
        const string sql = "SELECT * FROM [Users]";

        // Act
        var translated = translator.TranslateToSqlite(sql);

        // Assert
        translated.Should().Be(sql);
    }

    [Fact]
    public void Can_translate_basic_sql_server_identifiers_to_sqlite_identifiers()
    {
        // Arrange
        ITSqlTranslator translator = new BasicTSqlTranslator();

        // Act
        var translated = translator.TranslateToSqlite("SELECT TOP(10) * FROM [dbo].[Users]");

        // Assert
        translated.Should().Be("SELECT * FROM \"dbo_Users\" LIMIT 10");
    }

    private static TestDbContext CreateContext(Action<SqlServerInMemoryOptions>? configure = null)
    {
        var options = new DbContextOptionsBuilder<TestDbContext>()
            .UseSqlServerInMemory(Guid.NewGuid().ToString("N"), configure)
            .Options;

        return new TestDbContext(options);
    }

    private static bool ContainsException<TException>(Exception exception)
        where TException : Exception
    {
        for (var current = exception; current is not null; current = current.InnerException!)
        {
            if (current is TException)
            {
                return true;
            }
        }

        return false;
    }

    private sealed class TestDbContext(DbContextOptions<TestDbContext> options) : DbContext(options)
    {
        public DbSet<User> Users => Set<User>();

        public DbSet<Customer> Customers => Set<Customer>();

        public DbSet<Order> Orders => Set<Order>();

        public DbSet<Account> Accounts => Set<Account>();

        public DbSet<RequiredItem> RequiredItems => Set<RequiredItem>();

        public DbSet<SchemaUser> SchemaUsers => Set<SchemaUser>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<Account>()
                .Property(x => x.Email)
                .UseCollation("SQLSERVER_CI_AS");

            modelBuilder.Entity<Account>()
                .HasIndex(x => x.Email)
                .IsUnique();

            modelBuilder.Entity<RequiredItem>()
                .Property(x => x.Name)
                .IsRequired();

            modelBuilder.Entity<SchemaUser>()
                .ToTable("SchemaUsers", "auth");
        }
    }

    private sealed class User
    {
        public int Id { get; set; }

        public string Name { get; set; } = string.Empty;
    }

    private sealed class Customer
    {
        public int Id { get; set; }

        public string Name { get; set; } = string.Empty;

        public ICollection<Order> Orders { get; set; } = [];
    }

    private sealed class Order
    {
        public int Id { get; set; }

        public int CustomerId { get; set; }

        public Customer Customer { get; set; } = null!;

        public decimal Total { get; set; }
    }

    private sealed class Account
    {
        public int Id { get; set; }

        public string Email { get; set; } = string.Empty;
    }

    private sealed class RequiredItem
    {
        public int Id { get; set; }

        public string Name { get; set; } = string.Empty;
    }

    private sealed class SchemaUser
    {
        public int Id { get; set; }

        public string Name { get; set; } = string.Empty;
    }
}
