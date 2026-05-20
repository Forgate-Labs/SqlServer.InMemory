using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using SqlServer.InMemory.EFCore;
using SqlServer.InMemory.Exceptions;

namespace SqlServer.InMemory.Tests;

public sealed class SqlServerIdempotentConstraintScriptTests
{
    private const string UniqueConstraintName = "UK_CompanionDiagnosisCodes";
    private const string IndexOnlyConstraintName = "UK_CompanionDiagnosisCodes_IndexOnly";

    [Fact]
    public async Task If_not_exists_key_constraints_and_indexes_guard_adds_unique_index_and_is_idempotent()
    {
        await using var context = CreateContext();
        await context.Database.EnsureCreatedAsync();

        await context.Database.ExecuteSqlRawAsync(AddUniqueConstraintScript(UniqueConstraintName));
        await context.Database.ExecuteSqlRawAsync(AddUniqueConstraintScript(UniqueConstraintName));

        (await IndexExistsAsync(context, UniqueConstraintName)).Should().BeTrue();

        context.CompanionDiagnosisCodes.AddRange(
            new CompanionDiagnosisCode { CompanionId = 1, DiagnosisCodeId = 10 },
            new CompanionDiagnosisCode { CompanionId = 1, DiagnosisCodeId = 10 });

        var act = () => context.SaveChangesAsync();

        var exception = await act.Should().ThrowAsync<Exception>();
        ContainsException<SqlServerInMemoryUniqueConstraintException>(exception.Which).Should().BeTrue();
    }

    [Fact]
    public async Task If_exists_key_constraints_guard_drops_unique_index_and_allows_duplicates()
    {
        await using var context = CreateContext();
        await context.Database.EnsureCreatedAsync();
        await context.Database.ExecuteSqlRawAsync(AddUniqueConstraintScript(UniqueConstraintName));

        await context.Database.ExecuteSqlRawAsync(DropUniqueConstraintScript(UniqueConstraintName));

        (await IndexExistsAsync(context, UniqueConstraintName)).Should().BeFalse();

        context.CompanionDiagnosisCodes.AddRange(
            new CompanionDiagnosisCode { CompanionId = 2, DiagnosisCodeId = 20 },
            new CompanionDiagnosisCode { CompanionId = 2, DiagnosisCodeId = 20 });

        var act = () => context.SaveChangesAsync();

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task If_not_exists_sys_indexes_guard_adds_unique_index_once()
    {
        await using var context = CreateContext();
        await context.Database.EnsureCreatedAsync();

        await context.Database.ExecuteSqlRawAsync(AddUniqueConstraintWithIndexOnlyGuardScript(IndexOnlyConstraintName));
        await context.Database.ExecuteSqlRawAsync(AddUniqueConstraintWithIndexOnlyGuardScript(IndexOnlyConstraintName));

        (await IndexExistsAsync(context, IndexOnlyConstraintName)).Should().BeTrue();
    }

    private static ConstraintScriptDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<ConstraintScriptDbContext>()
            .UseSqlServerInMemory(Guid.NewGuid().ToString("N"))
            .Options;

        return new ConstraintScriptDbContext(options);
    }

    private static string AddUniqueConstraintScript(string constraintName)
    {
        return $@"
IF NOT EXISTS
(
    SELECT 1
    FROM sys.key_constraints
    WHERE [name] = N'{constraintName}'
      AND [parent_object_id] = OBJECT_ID(N'dbo.CompanionDiagnosisCodes')
)
AND NOT EXISTS
(
    SELECT 1
    FROM sys.indexes
    WHERE [name] = N'{constraintName}'
      AND [object_id] = OBJECT_ID(N'dbo.CompanionDiagnosisCodes')
)
BEGIN
    ALTER TABLE [dbo].[CompanionDiagnosisCodes] ADD CONSTRAINT [{constraintName}] UNIQUE CLUSTERED
    (
        [CompanionId] ASC,
        [DiagnosisCodeId] ASC
    ) WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, SORT_IN_TEMPDB = OFF, IGNORE_DUP_KEY = OFF, ONLINE = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON, OPTIMIZE_FOR_SEQUENTIAL_KEY = OFF) ON [PRIMARY];
END";
    }

    private static string AddUniqueConstraintWithIndexOnlyGuardScript(string constraintName)
    {
        return $@"
IF NOT EXISTS
(
    SELECT 1
    FROM sys.indexes
    WHERE [name] = N'{constraintName}'
      AND [object_id] = OBJECT_ID(N'dbo.CompanionDiagnosisCodes')
)
BEGIN
    ALTER TABLE [dbo].[CompanionDiagnosisCodes] ADD CONSTRAINT [{constraintName}] UNIQUE NONCLUSTERED
    (
        [CompanionId] ASC,
        [DiagnosisCodeId] DESC
    ) WITH (PAD_INDEX = OFF) ON [PRIMARY];
END";
    }

    private static string DropUniqueConstraintScript(string constraintName)
    {
        return $@"
IF EXISTS
(
    SELECT 1
    FROM sys.key_constraints
    WHERE [name] = N'{constraintName}'
      AND [parent_object_id] = OBJECT_ID(N'dbo.CompanionDiagnosisCodes')
)
BEGIN
    ALTER TABLE [dbo].[CompanionDiagnosisCodes] DROP CONSTRAINT [{constraintName}];
END";
    }

    private static async Task<bool> IndexExistsAsync(DbContext context, string indexName)
    {
        var connection = context.Database.GetDbConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'index' AND name = $name AND tbl_name = 'CompanionDiagnosisCodes';";
        var parameter = command.CreateParameter();
        parameter.ParameterName = "$name";
        parameter.Value = indexName;
        command.Parameters.Add(parameter);
        var result = await command.ExecuteScalarAsync();
        return Convert.ToInt32(result) == 1;
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

    private sealed class ConstraintScriptDbContext(DbContextOptions<ConstraintScriptDbContext> options) : DbContext(options)
    {
        public DbSet<CompanionDiagnosisCode> CompanionDiagnosisCodes => Set<CompanionDiagnosisCode>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<CompanionDiagnosisCode>(entity =>
            {
                entity.ToTable("CompanionDiagnosisCodes", "dbo");
                entity.HasKey(x => x.Id);
                entity.Property(x => x.Id).ValueGeneratedOnAdd();
                entity.Property(x => x.CompanionId).HasColumnType("int");
                entity.Property(x => x.DiagnosisCodeId).HasColumnType("int");
            });
        }
    }

    private sealed class CompanionDiagnosisCode
    {
        public int Id { get; set; }

        public int CompanionId { get; set; }

        public int DiagnosisCodeId { get; set; }
    }
}
