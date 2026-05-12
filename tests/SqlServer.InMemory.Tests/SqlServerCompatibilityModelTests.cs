using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using SqlServer.InMemory.EFCore;

namespace SqlServer.InMemory.Tests;

public sealed class SqlServerCompatibilityModelTests
{
    [Fact]
    public async Task Can_create_bigint_identity_primary_key_and_insert_entity()
    {
        // Arrange
        await using var context = CreateCompatibilityContext();
        await context.Database.EnsureCreatedAsync();
        var entity = new BigIntIdentityEntity { Name = "Big identity" };

        // Act
        context.BigIntIdentityEntities.Add(entity);
        await context.SaveChangesAsync();

        // Assert
        entity.Id.Should().BeGreaterThan(0);
        (await context.BigIntIdentityEntities.SingleAsync()).Name.Should().Be("Big identity");
    }

    [Fact]
    public async Task Can_create_nvarchar_max_and_varchar_max_columns_and_persist_values()
    {
        // Arrange
        await using var context = CreateCompatibilityContext();
        await context.Database.EnsureCreatedAsync();
        var entity = new TextMaxEntity
        {
            NVarCharValue = "Unicode value",
            VarCharValue = "Ansi value"
        };

        // Act
        context.TextMaxEntities.Add(entity);
        await context.SaveChangesAsync();

        // Assert
        var saved = await context.TextMaxEntities.SingleAsync();
        saved.NVarCharValue.Should().Be("Unicode value");
        saved.VarCharValue.Should().Be("Ansi value");
    }

    [Fact]
    public async Task Can_create_varbinary_max_column_and_persist_value()
    {
        // Arrange
        await using var context = CreateCompatibilityContext();
        await context.Database.EnsureCreatedAsync();
        var bytes = new byte[] { 1, 2, 3, 4 };

        // Act
        context.BinaryMaxEntities.Add(new BinaryMaxEntity { Data = bytes });
        await context.SaveChangesAsync();

        // Assert
        var saved = await context.BinaryMaxEntities.SingleAsync();
        saved.Data.Should().Equal(bytes);
    }

    [Fact]
    public async Task Contexts_with_same_database_name_share_schema_and_data()
    {
        // Arrange
        var databaseName = Guid.NewGuid().ToString("N");
        await using (var setup = CreateSharedContext(databaseName))
        {
            await setup.Database.EnsureCreatedAsync();
            setup.SharedItems.Add(new SharedItem { Name = "Shared" });
            await setup.SaveChangesAsync();
        }

        // Act
        await using var context = CreateSharedContext(databaseName);
        var names = await context.SharedItems.Select(x => x.Name).ToListAsync();

        // Assert
        names.Should().Equal("Shared");
    }

    [Fact]
    public async Task Contexts_with_different_database_names_are_isolated()
    {
        // Arrange
        var firstDatabaseName = Guid.NewGuid().ToString("N");
        var secondDatabaseName = Guid.NewGuid().ToString("N");

        await using (var first = CreateSharedContext(firstDatabaseName))
        {
            await first.Database.EnsureCreatedAsync();
            first.SharedItems.Add(new SharedItem { Name = "Only first" });
            await first.SaveChangesAsync();
        }

        // Act
        await using var second = CreateSharedContext(secondDatabaseName);
        await second.Database.EnsureCreatedAsync();
        var count = await second.SharedItems.CountAsync();

        // Assert
        count.Should().Be(0);
    }

    [Fact]
    public async Task Can_reset_named_database_without_discarding_schema()
    {
        // Arrange
        var databaseName = Guid.NewGuid().ToString("N");
        await using (var setup = CreateSharedContext(databaseName))
        {
            await setup.Database.EnsureCreatedAsync();
            setup.SharedItems.Add(new SharedItem { Name = "Before reset" });
            await setup.SaveChangesAsync();
        }

        // Act
        await SqlServerInMemoryDatabaseRegistry.ResetAsync(databaseName);

        // Assert
        await using var context = CreateSharedContext(databaseName);
        var count = await context.SharedItems.CountAsync();
        count.Should().Be(0);
    }

    private static CompatibilityModelDbContext CreateCompatibilityContext()
    {
        var options = new DbContextOptionsBuilder<CompatibilityModelDbContext>()
            .UseSqlServerInMemory(Guid.NewGuid().ToString("N"))
            .Options;

        return new CompatibilityModelDbContext(options);
    }

    private static SharedDatabaseDbContext CreateSharedContext(string databaseName)
    {
        var options = new DbContextOptionsBuilder<SharedDatabaseDbContext>()
            .UseSqlServerInMemory(databaseName)
            .Options;

        return new SharedDatabaseDbContext(options);
    }

    private sealed class CompatibilityModelDbContext(DbContextOptions<CompatibilityModelDbContext> options) : DbContext(options)
    {
        public DbSet<BigIntIdentityEntity> BigIntIdentityEntities => Set<BigIntIdentityEntity>();

        public DbSet<TextMaxEntity> TextMaxEntities => Set<TextMaxEntity>();

        public DbSet<BinaryMaxEntity> BinaryMaxEntities => Set<BinaryMaxEntity>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<BigIntIdentityEntity>()
                .Property(x => x.Id)
                .HasColumnType("bigint")
                .ValueGeneratedOnAdd();

            modelBuilder.Entity<TextMaxEntity>()
                .Property(x => x.NVarCharValue)
                .HasColumnType("NVARCHAR(MAX)");

            modelBuilder.Entity<TextMaxEntity>()
                .Property(x => x.VarCharValue)
                .HasColumnType("varchar(max)");

            modelBuilder.Entity<BinaryMaxEntity>()
                .Property(x => x.Data)
                .HasColumnType("varbinary(max)");
        }
    }

    private sealed class SharedDatabaseDbContext(DbContextOptions<SharedDatabaseDbContext> options) : DbContext(options)
    {
        public DbSet<SharedItem> SharedItems => Set<SharedItem>();
    }

    private sealed class BigIntIdentityEntity
    {
        public long Id { get; set; }

        public string Name { get; set; } = string.Empty;
    }

    private sealed class TextMaxEntity
    {
        public int Id { get; set; }

        public string NVarCharValue { get; set; } = string.Empty;

        public string VarCharValue { get; set; } = string.Empty;
    }

    private sealed class BinaryMaxEntity
    {
        public int Id { get; set; }

        public byte[] Data { get; set; } = [];
    }

    private sealed class SharedItem
    {
        public int Id { get; set; }

        public string Name { get; set; } = string.Empty;
    }
}
