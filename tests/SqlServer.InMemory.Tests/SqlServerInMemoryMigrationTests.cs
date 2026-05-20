using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using SqlServer.InMemory.EFCore;
using SqlServer.InMemory.Exceptions;

namespace SqlServer.InMemory.Tests;

public sealed class SqlServerInMemoryMigrationTests
{
    [Fact]
    public async Task Can_apply_sql_server_style_migration_with_schema_constraints_and_defaults()
    {
        // Arrange
        await using var context = CreateContext();

        // Act
        await context.Database.MigrateAsync();

        context.MigrationUsers.Add(new MigrationUser { Email = "migration@test.com" });
        await context.SaveChangesAsync();
        var user = await context.MigrationUsers.SingleAsync(x => x.Email == "migration@test.com");

        context.MigrationOrders.Add(new MigrationOrder { MigrationUserId = user.Id, Total = 25.50m });
        await context.SaveChangesAsync();

        // Assert
        user.Id.Should().BeGreaterThan(0);
        user.CreatedAt.Should().NotBe(default);
        (await context.MigrationOrders.CountAsync()).Should().Be(1);
        (await TableExistsAsync(context, "auth_MigrationUsers")).Should().BeTrue();
        (await TableExistsAsync(context, "sales_MigrationOrders")).Should().BeTrue();
        (await TableExistsAsync(context, "auth_SqlServerProviderOnly")).Should().BeTrue();
        (await TableExistsAsync(context, "__EFMigrationsHistory")).Should().BeTrue();
    }

    [Fact]
    public async Task MigrateAsync_applies_unique_indexes_and_foreign_keys()
    {
        // Arrange
        await using var context = CreateContext();
        await context.Database.MigrateAsync();
        context.MigrationUsers.Add(new MigrationUser { Email = "duplicate@test.com" });
        await context.SaveChangesAsync();

        // Act
        context.MigrationUsers.Add(new MigrationUser { Email = "duplicate@test.com" });
        var duplicateEmail = () => context.SaveChangesAsync();

        // Assert
        var uniqueException = await duplicateEmail.Should().ThrowAsync<Exception>();
        ContainsException<SqlServerInMemoryUniqueConstraintException>(uniqueException.Which).Should().BeTrue();

        context.ChangeTracker.Clear();
        context.MigrationOrders.Add(new MigrationOrder { MigrationUserId = 999, Total = 10m });
        var invalidForeignKey = () => context.SaveChangesAsync();
        var foreignKeyException = await invalidForeignKey.Should().ThrowAsync<Exception>();
        ContainsException<SqlServerInMemoryForeignKeyException>(foreignKeyException.Which).Should().BeTrue();
    }

    [Fact]
    public async Task MigrateAsync_rebuilds_table_for_drop_foreign_key_operations()
    {
        // Arrange
        await using var context = CreateRebuildContext();

        // Act
        await context.Database.MigrateAsync();
        context.RebuildChildren.Add(new RebuildChild { ParentId = 999, Name = "Orphan" });
        await context.SaveChangesAsync();

        // Assert
        (await context.RebuildChildren.CountAsync()).Should().Be(1);
        (await TableExistsAsync(context, "RebuildChildren")).Should().BeTrue();
    }

    [Fact]
    public async Task MigrateAsync_fails_explicitly_for_unsupported_sql_server_migration_operations()
    {
        // Arrange
        await using var context = CreateUnsupportedContext();

        // Act
        var act = () => context.Database.MigrateAsync();

        // Assert
        var exception = await act.Should().ThrowAsync<SqlServerInMemoryMigrationException>();
        exception.Which.Message.Should().Contain("CreateSequenceOperation");
    }

    [Fact]
    public async Task MigrateAsync_supports_raw_tsql_guards_and_sql_server_types()
    {
        // Arrange
        await using var context = CreateRawSqlContext();

        // Act
        await context.Database.MigrateAsync();
        context.RawSqlRows.Add(new RawSqlRow { Id = 1, Name = "raw" });
        await context.SaveChangesAsync();

        await context.Database.ExecuteSqlRawAsync(@"
INSERT INTO [dbo].[RawCreatedByObjectId]
    ([Id], [Name], [Code], [IsActive], [Amount], [CreatedAt], [ExternalId])
VALUES
    (1, N'Created', 'A', 1, 12.5, GETDATE(), '00000000-0000-0000-0000-000000000001');");

        var isActive = await context.Database.SqlQueryRaw<int>("SELECT IsActive AS [Value] FROM dbo.RawSqlRows WHERE Id = 1").SingleAsync();
        var objectId = await context.Database.SqlQueryRaw<int?>("SELECT OBJECT_ID(N'dbo.RawCreatedByObjectId', N'U') AS [Value]").SingleAsync();
        var existingColumnLength = await context.Database.SqlQueryRaw<int?>("SELECT COL_LENGTH(N'dbo.RawSqlRows', N'IsActive') AS [Value]").SingleAsync();
        var missingColumnLength = await context.Database.SqlQueryRaw<int?>("SELECT COL_LENGTH(N'dbo.RawSqlRows', N'Missing') AS [Value]").SingleAsync();

        // Assert
        (await TableExistsAsync(context, "RawSqlRows")).Should().BeTrue();
        (await TableExistsAsync(context, "RawCreatedByObjectId")).Should().BeTrue();
        (await ColumnExistsAsync(context, "RawSqlRows", "IsActive")).Should().BeTrue();
        isActive.Should().Be(0);
        objectId.Should().NotBeNull();
        existingColumnLength.Should().NotBeNull();
        missingColumnLength.Should().BeNull();
    }

    [Fact]
    public async Task ExecuteSqlRaw_skips_object_id_and_col_length_guard_bodies_when_metadata_is_missing()
    {
        // Arrange
        await using var context = CreateRawSqlContext();
        await context.Database.EnsureCreatedAsync();

        // Act
        var act = () => context.Database.ExecuteSqlRawAsync(@"
IF OBJECT_ID(N'dbo.DoesNotExist', N'U') IS NOT NULL
BEGIN
    DELETE FROM [dbo].[DoesNotExist] WHERE [Id] = 1;
END;

IF COL_LENGTH(N'dbo.RawSqlRows', N'MissingColumn') IS NOT NULL
BEGIN
    UPDATE [dbo].[RawSqlRows] SET [MissingColumn] = 1 WHERE [Id] = 1;
END;");

        // Assert
        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task ExecuteSqlRaw_maps_bracketed_dbo_references_to_existing_unqualified_table()
    {
        // Arrange
        await using var context = CreateUnqualifiedDboReferenceContext();
        await context.Database.EnsureCreatedAsync();

        // Act
        await context.Database.ExecuteSqlRawAsync(@"
IF COL_LENGTH(N'dbo.Families_Candidates', N'IsEligible') IS NULL
BEGIN
    ALTER TABLE [dbo].[Families_Candidates] ADD [IsEligible] bit NOT NULL DEFAULT CAST(0 AS bit);
END;");

        await context.Database.ExecuteSqlRawAsync(@"
IF COL_LENGTH(N'dbo.Families_Candidates', N'IsEligible') IS NULL
BEGIN
    ALTER TABLE [dbo].[Families_Candidates] ADD [IsEligible] bit NOT NULL DEFAULT CAST(0 AS bit);
END;");

        // Assert
        await context.Database.ExecuteSqlRawAsync("CREATE TABLE [Enderecos] ([Id] int NOT NULL);");
        await context.Database.ExecuteSqlRawAsync(@"
IF COL_LENGTH(N'dbo.Enderecos', N'CEP') IS NULL
BEGIN
    ALTER TABLE [dbo].[Enderecos] ADD [CEP] nvarchar(10) NULL;
END;");

        await context.Database.ExecuteSqlRawAsync(@"
IF COL_LENGTH(N'dbo.Enderecos', N'CEP') IS NULL
BEGIN
    ALTER TABLE [dbo].[Enderecos] ADD [CEP] nvarchar(10) NULL;
END;");

        (await TableExistsAsync(context, "Families_Candidates")).Should().BeTrue();
        (await TableExistsAsync(context, "dbo_Families_Candidates")).Should().BeFalse();
        (await ColumnExistsAsync(context, "Families_Candidates", "IsEligible")).Should().BeTrue();
        (await TableExistsAsync(context, "Enderecos")).Should().BeTrue();
        (await TableExistsAsync(context, "dbo_erecos")).Should().BeFalse();
        (await ColumnExistsAsync(context, "Enderecos", "CEP")).Should().BeTrue();
    }

    private static MigrationDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<MigrationDbContext>()
            .UseSqlServerInMemory(Guid.NewGuid().ToString("N"))
            .Options;

        return new MigrationDbContext(options);
    }

    private static RebuildMigrationDbContext CreateRebuildContext()
    {
        var options = new DbContextOptionsBuilder<RebuildMigrationDbContext>()
            .UseSqlServerInMemory(Guid.NewGuid().ToString("N"))
            .Options;

        return new RebuildMigrationDbContext(options);
    }

    private static UnsupportedMigrationDbContext CreateUnsupportedContext()
    {
        var options = new DbContextOptionsBuilder<UnsupportedMigrationDbContext>()
            .UseSqlServerInMemory(Guid.NewGuid().ToString("N"))
            .Options;

        return new UnsupportedMigrationDbContext(options);
    }

    private static RawSqlMigrationDbContext CreateRawSqlContext()
    {
        var options = new DbContextOptionsBuilder<RawSqlMigrationDbContext>()
            .UseSqlServerInMemory(Guid.NewGuid().ToString("N"))
            .Options;

        return new RawSqlMigrationDbContext(options);
    }

    private static UnqualifiedDboReferenceContext CreateUnqualifiedDboReferenceContext()
    {
        var options = new DbContextOptionsBuilder<UnqualifiedDboReferenceContext>()
            .UseSqlServerInMemory(Guid.NewGuid().ToString("N"))
            .Options;

        return new UnqualifiedDboReferenceContext(options);
    }

    private static async Task<bool> TableExistsAsync(DbContext context, string tableName)
    {
        var connection = context.Database.GetDbConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = $name;";
        var parameter = command.CreateParameter();
        parameter.ParameterName = "$name";
        parameter.Value = tableName;
        command.Parameters.Add(parameter);
        var result = await command.ExecuteScalarAsync();
        return Convert.ToInt32(result) == 1;
    }

    private static async Task<bool> ColumnExistsAsync(DbContext context, string tableName, string columnName)
    {
        var connection = context.Database.GetDbConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info(\"{tableName.Replace("\"", "\"\"")}\");";
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            if (reader.GetString(1).Equals(columnName, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static bool ContainsException<TException>(Exception exception)
        where TException : Exception
    {
        var current = exception;
        while (current is not null)
        {
            if (current is TException)
            {
                return true;
            }

            current = current.InnerException;
        }

        return false;
    }

    private sealed class UnqualifiedDboReferenceContext(DbContextOptions<UnqualifiedDboReferenceContext> options) : DbContext(options)
    {
        public DbSet<FamilyCandidate> FamilyCandidates => Set<FamilyCandidate>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<FamilyCandidate>(entity =>
            {
                entity.ToTable("Families_Candidates");
                entity.HasKey(x => x.Id);
                entity.Property(x => x.Name).HasColumnType("nvarchar(100)");
            });
        }
    }

    private sealed class FamilyCandidate
    {
        public int Id { get; set; }

        public string Name { get; set; } = string.Empty;
    }
}

public sealed class MigrationDbContext : DbContext
{
    public MigrationDbContext(DbContextOptions<MigrationDbContext> options)
        : base(options)
    {
    }

    public DbSet<MigrationUser> MigrationUsers => Set<MigrationUser>();

    public DbSet<MigrationOrder> MigrationOrders => Set<MigrationOrder>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<MigrationUser>(entity =>
        {
            entity.ToTable("MigrationUsers", "auth");
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => x.Email).IsUnique();
            entity.Property(x => x.Id).ValueGeneratedOnAdd();
            entity.Property(x => x.Email).HasColumnType("nvarchar(320)").HasMaxLength(320).IsRequired();
            entity.Property(x => x.CreatedAt).HasColumnType("datetime2").HasDefaultValueSql("GETDATE()");
        });

        modelBuilder.Entity<MigrationOrder>(entity =>
        {
            entity.ToTable("MigrationOrders", "sales");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).ValueGeneratedOnAdd();
            entity.Property(x => x.Total).HasColumnType("decimal(18,2)");
            entity.HasOne(x => x.MigrationUser)
                .WithMany(x => x.Orders)
                .HasForeignKey(x => x.MigrationUserId)
                .OnDelete(DeleteBehavior.Cascade);
        });
    }
}

public sealed class RebuildMigrationDbContext : DbContext
{
    public RebuildMigrationDbContext(DbContextOptions<RebuildMigrationDbContext> options)
        : base(options)
    {
    }

    public DbSet<RebuildParent> RebuildParents => Set<RebuildParent>();

    public DbSet<RebuildChild> RebuildChildren => Set<RebuildChild>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ConfigureRebuildTargetModel(modelBuilder);
    }

    internal static void ConfigureRebuildTargetModel(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<RebuildParent>(entity =>
        {
            entity.ToTable("RebuildParents", "dbo");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).ValueGeneratedOnAdd();
            entity.Property(x => x.Name).HasColumnType("nvarchar(100)").IsRequired();
        });

        modelBuilder.Entity<RebuildChild>(entity =>
        {
            entity.ToTable("RebuildChildren", "dbo");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).ValueGeneratedOnAdd();
            entity.Property(x => x.ParentId).HasColumnType("int");
            entity.Property(x => x.Name).HasColumnType("nvarchar(100)").IsRequired();
        });
    }
}

public sealed class UnsupportedMigrationDbContext : DbContext
{
    public UnsupportedMigrationDbContext(DbContextOptions<UnsupportedMigrationDbContext> options)
        : base(options)
    {
    }

    public DbSet<UnsupportedSequenceEntity> UnsupportedSequenceEntities => Set<UnsupportedSequenceEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<UnsupportedSequenceEntity>(entity =>
        {
            entity.ToTable("UnsupportedSequenceEntities", "dbo");
            entity.HasKey(x => x.Id);
        });
    }
}

public sealed class MigrationUser
{
    public int Id { get; set; }

    public string Email { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; }

    public List<MigrationOrder> Orders { get; } = [];
}

public sealed class MigrationOrder
{
    public int Id { get; set; }

    public int MigrationUserId { get; set; }

    public decimal Total { get; set; }

    public MigrationUser MigrationUser { get; set; } = null!;
}

public sealed class RebuildParent
{
    public int Id { get; set; }

    public string Name { get; set; } = string.Empty;
}

public sealed class RebuildChild
{
    public int Id { get; set; }

    public int ParentId { get; set; }

    public string Name { get; set; } = string.Empty;
}

public sealed class UnsupportedSequenceEntity
{
    public int Id { get; set; }
}

public sealed class RawSqlMigrationDbContext : DbContext
{
    public RawSqlMigrationDbContext(DbContextOptions<RawSqlMigrationDbContext> options)
        : base(options)
    {
    }

    public DbSet<RawSqlRow> RawSqlRows => Set<RawSqlRow>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<RawSqlRow>(entity =>
        {
            entity.ToTable("RawSqlRows", "dbo");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Name).HasColumnType("nvarchar(100)");
            entity.Property(x => x.IsActive).HasColumnType("bit");
        });
    }
}

public sealed class RawSqlRow
{
    public int Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public bool IsActive { get; set; }
}

[DbContext(typeof(MigrationDbContext))]
[Migration("20260101000000_CreateSqlServerStyleSchema")]
public sealed class CreateSqlServerStyleSchema : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.EnsureSchema("auth");
        migrationBuilder.EnsureSchema("sales");

        if (migrationBuilder.ActiveProvider == "Microsoft.EntityFrameworkCore.SqlServer")
        {
            migrationBuilder.CreateTable(
                name: "SqlServerProviderOnly",
                schema: "auth",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SqlServerProviderOnly", x => x.Id);
                });
        }

        migrationBuilder.CreateTable(
            name: "MigrationUsers",
            schema: "auth",
            columns: table => new
            {
                Id = table.Column<int>(type: "int", nullable: false)
                    .Annotation("SqlServer:Identity", "1, 1"),
                Email = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: false),
                CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false, defaultValueSql: "GETDATE()")
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_MigrationUsers", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "MigrationOrders",
            schema: "sales",
            columns: table => new
            {
                Id = table.Column<int>(type: "int", nullable: false)
                    .Annotation("SqlServer:Identity", "1, 1"),
                MigrationUserId = table.Column<int>(type: "int", nullable: false),
                Total = table.Column<decimal>(type: "decimal(18,2)", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_MigrationOrders", x => x.Id);
                table.ForeignKey(
                    name: "FK_MigrationOrders_MigrationUsers_MigrationUserId",
                    column: x => x.MigrationUserId,
                    principalSchema: "auth",
                    principalTable: "MigrationUsers",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateIndex(
            name: "IX_MigrationUsers_Email",
            schema: "auth",
            table: "MigrationUsers",
            column: "Email",
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_MigrationOrders_MigrationUserId",
            schema: "sales",
            table: "MigrationOrders",
            column: "MigrationUserId");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "MigrationOrders", schema: "sales");
        migrationBuilder.DropTable(name: "MigrationUsers", schema: "auth");
        migrationBuilder.DropTable(name: "SqlServerProviderOnly", schema: "auth");
    }
}

[DbContext(typeof(RebuildMigrationDbContext))]
[Migration("20260101000001_CreateRebuildForeignKeySchema")]
public sealed class CreateRebuildForeignKeySchema : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.EnsureSchema("dbo");

        migrationBuilder.CreateTable(
            name: "RebuildParents",
            schema: "dbo",
            columns: table => new
            {
                Id = table.Column<int>(type: "int", nullable: false)
                    .Annotation("SqlServer:Identity", "1, 1"),
                Name = table.Column<string>(type: "nvarchar(100)", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_RebuildParents", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "RebuildChildren",
            schema: "dbo",
            columns: table => new
            {
                Id = table.Column<int>(type: "int", nullable: false)
                    .Annotation("SqlServer:Identity", "1, 1"),
                ParentId = table.Column<int>(type: "int", nullable: false),
                Name = table.Column<string>(type: "nvarchar(100)", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_RebuildChildren", x => x.Id);
                table.ForeignKey(
                    name: "FK_RebuildChildren_RebuildParents_ParentId",
                    column: x => x.ParentId,
                    principalSchema: "dbo",
                    principalTable: "RebuildParents",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "RebuildChildren", schema: "dbo");
        migrationBuilder.DropTable(name: "RebuildParents", schema: "dbo");
    }
}

[DbContext(typeof(RebuildMigrationDbContext))]
[Migration("20260101000002_DropRebuildForeignKey")]
public sealed class DropRebuildForeignKey : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropForeignKey(
            name: "FK_RebuildChildren_RebuildParents_ParentId",
            schema: "dbo",
            table: "RebuildChildren");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddForeignKey(
            name: "FK_RebuildChildren_RebuildParents_ParentId",
            schema: "dbo",
            table: "RebuildChildren",
            column: "ParentId",
            principalSchema: "dbo",
            principalTable: "RebuildParents",
            principalColumn: "Id",
            onDelete: ReferentialAction.Cascade);
    }

    protected override void BuildTargetModel(ModelBuilder modelBuilder)
    {
        RebuildMigrationDbContext.ConfigureRebuildTargetModel(modelBuilder);
    }
}

[DbContext(typeof(RawSqlMigrationDbContext))]
[Migration("20260101000001_CreateRawSqlGuardedSchema")]
public sealed class CreateRawSqlGuardedSchema : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.EnsureSchema("dbo");

        migrationBuilder.CreateTable(
            name: "RawSqlRows",
            schema: "dbo",
            columns: table => new
            {
                Id = table.Column<int>(type: "int", nullable: false),
                Name = table.Column<string>(type: "nvarchar(100)", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_RawSqlRows", x => x.Id);
            });

        migrationBuilder.Sql(@"
IF COL_LENGTH(N'dbo.RawSqlRows', N'IsActive') IS NULL
BEGIN
    ALTER TABLE dbo.RawSqlRows ADD IsActive bit NOT NULL DEFAULT CAST(0 AS bit);
END");

        migrationBuilder.Sql(@"
IF COL_LENGTH(N'dbo.RawSqlRows', N'IsActive') IS NULL
BEGIN
    ALTER TABLE [dbo].[RawSqlRows] ADD [IsActive] bit NOT NULL DEFAULT CAST(0 AS bit);
END");

        migrationBuilder.Sql(@"
IF OBJECT_ID(N'dbo.RawCreatedByObjectId', N'U') IS NULL
BEGIN
    CREATE TABLE [dbo].[RawCreatedByObjectId] (
        [Id] int NOT NULL,
        [SmallId] smallint NULL,
        [BigId] bigint NULL,
        [Name] nvarchar(100) NULL,
        [Code] varchar(20) NULL,
        [IsActive] bit NOT NULL DEFAULT CAST(0 AS bit),
        [Amount] smallmoney NULL,
        [CreatedAt] datetime2 NULL,
        [Payload] varbinary(max) NULL,
        [ExternalId] uniqueidentifier NULL
    );
END");

        migrationBuilder.Sql(@"
IF OBJECT_ID(N'dbo.RawCreatedByObjectId', N'U') IS NULL
BEGIN
    CREATE TABLE [dbo].[RawCreatedByObjectId] (
        [Id] int NOT NULL,
        [Name] nvarchar(100) NULL
    );
END");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "RawSqlRows", schema: "dbo");
        migrationBuilder.Sql("DROP TABLE IF EXISTS [dbo].[RawCreatedByObjectId]");
    }
}

[DbContext(typeof(UnsupportedMigrationDbContext))]
[Migration("20260101000001_CreateUnsupportedSequence")]
public sealed class CreateUnsupportedSequence : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateSequence<int>(
            name: "OrderNumbers",
            schema: "dbo");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropSequence(
            name: "OrderNumbers",
            schema: "dbo");
    }
}
