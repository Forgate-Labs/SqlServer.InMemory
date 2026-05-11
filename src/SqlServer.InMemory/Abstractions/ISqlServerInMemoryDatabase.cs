using System.Data.Common;

namespace SqlServer.InMemory.Abstractions;

public interface ISqlServerInMemoryDatabase : IAsyncDisposable, IDisposable
{
    DbConnection Connection { get; }

    Task ResetAsync(CancellationToken cancellationToken = default);
}
