using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using SqlServer.InMemory.EFCore;
using SqlParameter = Microsoft.Data.SqlClient.SqlParameter;

namespace SqlServer.InMemory.Tests;

public sealed class CasaCustoZeroCompatibilityTests
{
    [Fact]
    public async Task Can_execute_sql_server_seed_script_with_identity_insert_and_sql_parameters()
    {
        // Arrange
        await using var context = CreateContext();
        await context.Database.EnsureCreatedAsync();

        const string sql = @"
IF NOT EXISTS (SELECT 1 FROM dbo.Deficiencias WHERE Id = @id)
BEGIN
    SET IDENTITY_INSERT dbo.Deficiencias ON;
    INSERT INTO dbo.Deficiencias (Id, Deficiencia, IsActive) VALUES (@id, N'Deficiência Phase9', 1);
    SET IDENTITY_INSERT dbo.Deficiencias OFF;
END;";

        // Act
        await context.Database.ExecuteSqlRawAsync(sql, new SqlParameter("@id", 8001001L));
        await context.Database.ExecuteSqlRawAsync(sql, new SqlParameter("@id", 8001001L));
        var count = await context.Database.SqlQueryRaw<long>(
            "SELECT COUNT_BIG(1) AS [Value] FROM dbo.Deficiencias WHERE Id = @id",
            new SqlParameter("@id", 8001001L))
            .SingleAsync();

        // Assert
        count.Should().Be(1);
    }

    [Fact]
    public async Task Can_translate_common_casa_custo_zero_query_constructs()
    {
        // Arrange
        await using var context = CreateContext();
        await context.Database.EnsureCreatedAsync();
        await context.Database.ExecuteSqlRawAsync(@"
INSERT INTO Familias (Id, DataCriacao, DataAtualizacao, IsActive) VALUES (1, GETDATE(), DATEADD(day, 30, GETDATE()), 1);
INSERT INTO Familias (Id, DataCriacao, DataAtualizacao, IsActive) VALUES (2, DATEADD(minute, 5, GETDATE()), GETDATE(), 1);");

        // Act
        var ids = await context.Database.SqlQueryRaw<long>(@"
SELECT Id AS [Value]
FROM Familias
WHERE ISNULL(IsActive, 0) = 1
ORDER BY Id
OFFSET @skip ROWS FETCH NEXT @take ROWS ONLY",
            new SqlParameter("@skip", 1),
            new SqlParameter("@take", 1))
            .ToListAsync();

        // Assert
        ids.Should().Equal(2);
    }

    [Fact]
    public async Task Can_translate_output_inserted_for_sql_query_raw()
    {
        // Arrange
        await using var context = CreateContext();
        await context.Database.EnsureCreatedAsync();

        // Act
        var inserted = await context.Database.SqlQueryRaw<InsertedConclusion>(@"
INSERT INTO ConclusaoAnalisesPropriedadesDocumentos (FamiliaId, UserId, DataHora, Atendido, Nota, IsActive)
OUTPUT INSERTED.Id, INSERTED.DataHora
VALUES (@familiaId, @userId, GETDATE(), @atendido, @nota, 1)",
            new SqlParameter("@familiaId", 10L),
            new SqlParameter("@userId", Guid.Parse("00000000-0000-0000-0000-000000008001")),
            new SqlParameter("@atendido", true),
            new SqlParameter("@nota", "ok"))
            .ToListAsync();

        // Assert
        inserted.Should().ContainSingle();
        inserted[0].Id.Should().BeGreaterThan(0);
        inserted[0].DataHora.Should().BeAfter(DateTime.MinValue);
    }

    [Fact]
    public async Task Can_translate_object_id_guard_blocks()
    {
        // Arrange
        await using var context = CreateContext();
        await context.Database.EnsureCreatedAsync();
        context.Analises.Add(new AnalisePropriedadeDocumento { Id = 1, FamiliaId = 10, PropriedadeAnaliseId = 20, IsActive = true });
        await context.SaveChangesAsync();

        // Act
        await context.Database.ExecuteSqlRawAsync(@"
IF OBJECT_ID(N'AnalisesPropriedadesDocumentos', N'U') IS NOT NULL
BEGIN
    DELETE FROM AnalisesPropriedadesDocumentos WHERE FamiliaId = @familiaId AND PropriedadeAnaliseId = @campoAnaliseId;
END;",
            new SqlParameter("@familiaId", 10L),
            new SqlParameter("@campoAnaliseId", 20L));

        var exists = await context.Analises.AnyAsync();

        // Assert
        exists.Should().BeFalse();
    }

    private static CompatibilityDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<CompatibilityDbContext>()
            .UseSqlServerInMemory(Guid.NewGuid().ToString("N"))
            .Options;

        return new CompatibilityDbContext(options);
    }

    private sealed class CompatibilityDbContext(DbContextOptions<CompatibilityDbContext> options) : DbContext(options)
    {
        public DbSet<Deficiencia> Deficiencias => Set<Deficiencia>();

        public DbSet<Familia> Familias => Set<Familia>();

        public DbSet<ConclusaoAnalise> Conclusoes => Set<ConclusaoAnalise>();

        public DbSet<AnalisePropriedadeDocumento> Analises => Set<AnalisePropriedadeDocumento>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<Deficiencia>().ToTable("Deficiencias");
            modelBuilder.Entity<Deficiencia>().Property(x => x.Nome).HasColumnName("Deficiencia");
            modelBuilder.Entity<ConclusaoAnalise>().ToTable("ConclusaoAnalisesPropriedadesDocumentos");
            modelBuilder.Entity<AnalisePropriedadeDocumento>().ToTable("AnalisesPropriedadesDocumentos");
        }
    }

    private sealed class Deficiencia
    {
        public long Id { get; set; }

        public string Nome { get; set; } = string.Empty;

        public bool IsActive { get; set; }
    }

    private sealed class Familia
    {
        public long Id { get; set; }

        public DateTime DataCriacao { get; set; }

        public DateTime DataAtualizacao { get; set; }

        public bool IsActive { get; set; }
    }

    private sealed class ConclusaoAnalise
    {
        public long Id { get; set; }

        public long FamiliaId { get; set; }

        public Guid UserId { get; set; }

        public DateTime DataHora { get; set; }

        public bool Atendido { get; set; }

        public string? Nota { get; set; }

        public bool IsActive { get; set; }
    }

    private sealed class AnalisePropriedadeDocumento
    {
        public long Id { get; set; }

        public long FamiliaId { get; set; }

        public long PropriedadeAnaliseId { get; set; }

        public bool IsActive { get; set; }
    }

    private sealed record InsertedConclusion(long Id, DateTime DataHora);
}
