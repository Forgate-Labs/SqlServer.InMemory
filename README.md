# SqlServer.InMemory

[![NuGet](https://img.shields.io/nuget/v/SqlServer.InMemory.svg?label=NuGet)](https://www.nuget.org/packages/SqlServer.InMemory/0.1.1)

SqlServer.InMemory is not a full SQL Server emulator. It is a testing-oriented EF Core provider/adapter that uses SQLite in-memory as the execution engine and adds SQL Server-inspired compatibility behavior.

The project is a proof of concept for automated tests that want a relational in-memory database with an API close to SQL Server-oriented EF Core setup:

```csharp
var options = new DbContextOptionsBuilder<AppDbContext>()
    .UseSqlServerInMemory("TestDb")
    .Options;

await using var context = new AppDbContext(options);
await context.Database.EnsureCreatedAsync();
```

## What it is

- A small EF Core adapter for tests.
- A SQLite in-memory backend hidden behind `UseSqlServerInMemory`.
- A place for SQL Server-inspired compatibility features.
- A foundation for a future partial T-SQL translator.

## What it is not

- It is not SQL Server.
- It does not implement TDS, `SqlConnection`, stored procedures, views, triggers, a query planner, or a full T-SQL parser.
- It does not use Docker, Testcontainers, or a real SQL Server instance.

## Projects

```text
src/SqlServer.InMemory          Core options, abstractions, and exceptions.
src/SqlServer.InMemory.Sqlite   SQLite in-memory connection lifecycle and helpers.
src/SqlServer.InMemory.EFCore   EF Core extension, schema normalization, and exception translation.
src/SqlServer.InMemory.TSql     Future T-SQL translator contracts and basic translator POC.
tests/SqlServer.InMemory.Tests  xUnit test coverage for the POC behavior.
```

## Usage

```csharp
using Microsoft.EntityFrameworkCore;
using SqlServer.InMemory.EFCore;

var options = new DbContextOptionsBuilder<AppDbContext>()
    .UseSqlServerInMemory("TestDb", sqlOptions =>
    {
        sqlOptions.EnforceForeignKeys = true;
        sqlOptions.UseCaseInsensitiveCollation = true;
        sqlOptions.SchemaMode = SqlServerInMemorySchemaMode.PrefixSchemaName;
    })
    .Options;

await using var context = new AppDbContext(options);
await context.Database.EnsureCreatedAsync();
```

## Implemented in this POC

- `UseSqlServerInMemory` for typed and untyped `DbContextOptionsBuilder`.
- SQLite in-memory connection opened immediately and kept alive by EF Core options.
- `PRAGMA foreign_keys = ON` by default.
- `SQLSERVER_CI_AS` SQLite collation registration.
- Schema normalization:
  - `PrefixSchemaName`: `auth.Users` becomes `auth_Users`.
  - `IgnoreSchema`: `auth.Users` becomes `Users`.
- Basic exception translation for SQLite foreign key and unique constraint errors.
- Automatic raw SQL translation through an EF Core command interceptor.
- Compatibility wrapper that accepts SQL Server `DbParameter` instances, including `Microsoft.Data.SqlClient.SqlParameter`, when the SQLite command is created.
- Translation for common test-oriented T-SQL constructs: `dbo.`, `N'...'`, `GETDATE()`, `DATEADD`, `ISNULL`, `COUNT_BIG`, `TOP`, `OFFSET/FETCH`, `SET IDENTITY_INSERT`, guarded seed inserts, `IF OBJECT_ID` guards, and `OUTPUT INSERTED` for simple inserts.
- Core custom exception types.
- Reset helper abstraction and SQLite implementation.
- T-SQL translator abstraction, no-op translator, and basic identifier/TOP translator.

## Known limitations

- SQLite type behavior is not SQL Server type behavior.
- Case-insensitive collation is registered, but columns are not automatically rewritten to use it in every model scenario.
- Exception translation is intentionally narrow and based on SQLite error codes.
- Raw SQL translation is automatic, but it uses a regex-based translator intended for tests and known patterns.
- The basic translator is not a full T-SQL parser.
- Complex T-SQL such as `MERGE`, `OUTER APPLY`, stored procedures, temporary tables, table variables, dynamic SQL, and arbitrary procedural `IF/ELSE` blocks are still unsupported unless rewritten into supported patterns.

## Roadmap

### v0.1 — SQLite backend

- EF Core extension.
- SQLite in-memory.
- FK.
- Unique index.
- Not null.
- Schema normalization.
- Basic exception translation.

### v0.2 — SQL Server-like behavior

- Better type mapping.
- Decimal handling.
- Datetime handling.
- Rowversion simulation.
- More SQL Server-like error messages.
- Better collation support.

### v0.3 — T-SQL subset translator

- Identifiers.
- Schemas.
- `TOP`.
- Simple `SELECT`.
- Simple `INSERT`.
- Simple `UPDATE`.
- Simple `DELETE`.
- Simple `CREATE TABLE`.
- Simple `CREATE INDEX`.

### v0.4 — Raw SQL integration

- Translator diagnostics.
- Warnings for unsupported T-SQL.
- Configurable translator replacement.
- Optional explicit raw SQL helper APIs if needed.

## Running tests

```bash
dotnet test SqlServer.InMemory.sln
```
