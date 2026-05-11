#pragma warning disable CS8764, CS8765

using System.Data;
using System.Data.Common;
using Microsoft.Data.Sqlite;

namespace SqlServer.InMemory.Sqlite;

internal sealed class SqlServerInMemorySqliteConnection : DbConnection
{
    private readonly SqliteConnection inner;

    public SqlServerInMemorySqliteConnection(SqliteConnection inner)
    {
        this.inner = inner;
    }

    public override string? ConnectionString
    {
        get => inner.ConnectionString;
        set => inner.ConnectionString = value;
    }

    public override string Database => inner.Database;

    public override string DataSource => inner.DataSource;

    public override string ServerVersion => inner.ServerVersion;

    public override ConnectionState State => inner.State;

    public override void ChangeDatabase(string databaseName) => inner.ChangeDatabase(databaseName);

    public override void Close() => inner.Close();

    public override void Open() => inner.Open();

    public override Task OpenAsync(CancellationToken cancellationToken) => inner.OpenAsync(cancellationToken);

    protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel) => new SqlServerInMemorySqliteTransaction(inner.BeginTransaction(isolationLevel), this);

    protected override DbCommand CreateDbCommand() => new SqlServerInMemorySqliteCommand(inner.CreateCommand());

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            inner.Dispose();
        }

        base.Dispose(disposing);
    }

    public override ValueTask DisposeAsync() => inner.DisposeAsync();
}

internal sealed class SqlServerInMemorySqliteTransaction : DbTransaction
{
    public SqlServerInMemorySqliteTransaction(SqliteTransaction inner, DbConnection connection)
    {
        Inner = inner;
        DbConnection = connection;
    }

    internal SqliteTransaction Inner { get; }

    public override IsolationLevel IsolationLevel => Inner.IsolationLevel;

    protected override DbConnection? DbConnection { get; }

    public override void Commit() => Inner.Commit();

    public override Task CommitAsync(CancellationToken cancellationToken = default) => Inner.CommitAsync(cancellationToken);

    public override void Rollback() => Inner.Rollback();

    public override Task RollbackAsync(CancellationToken cancellationToken = default) => Inner.RollbackAsync(cancellationToken);

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Inner.Dispose();
        }

        base.Dispose(disposing);
    }

    public override ValueTask DisposeAsync() => Inner.DisposeAsync();
}

internal sealed class SqlServerInMemorySqliteCommand : DbCommand
{
    private readonly SqliteCommand inner;
    private readonly SqlServerInMemorySqliteParameterCollection parameters;

    public SqlServerInMemorySqliteCommand(SqliteCommand inner)
    {
        this.inner = inner;
        parameters = new SqlServerInMemorySqliteParameterCollection(inner.Parameters);
    }

    public override string CommandText
    {
        get => inner.CommandText;
        set => inner.CommandText = value;
    }

    public override int CommandTimeout
    {
        get => inner.CommandTimeout;
        set => inner.CommandTimeout = value;
    }

    public override CommandType CommandType
    {
        get => inner.CommandType;
        set => inner.CommandType = value;
    }

    public override bool DesignTimeVisible
    {
        get => inner.DesignTimeVisible;
        set => inner.DesignTimeVisible = value;
    }

    public override UpdateRowSource UpdatedRowSource
    {
        get => inner.UpdatedRowSource;
        set => inner.UpdatedRowSource = value;
    }

    protected override DbConnection? DbConnection
    {
        get => inner.Connection;
        set => inner.Connection = (SqliteConnection?)value;
    }

    protected override DbParameterCollection DbParameterCollection => parameters;

    protected override DbTransaction? DbTransaction
    {
        get => inner.Transaction is null ? null : new SqlServerInMemorySqliteTransaction(inner.Transaction, DbConnection!);
        set => inner.Transaction = value switch
        {
            null => null,
            SqlServerInMemorySqliteTransaction wrapped => wrapped.Inner,
            SqliteTransaction sqlite => sqlite,
            _ => throw new InvalidOperationException("Unsupported transaction type.")
        };
    }

    public override void Cancel() => inner.Cancel();

    public override int ExecuteNonQuery() => inner.ExecuteNonQuery();

    public override object? ExecuteScalar() => inner.ExecuteScalar();

    public override void Prepare() => inner.Prepare();

    protected override DbParameter CreateDbParameter() => inner.CreateParameter();

    protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior) => inner.ExecuteReader(behavior);

    public override Task<int> ExecuteNonQueryAsync(CancellationToken cancellationToken) => inner.ExecuteNonQueryAsync(cancellationToken);

    public override Task<object?> ExecuteScalarAsync(CancellationToken cancellationToken) => inner.ExecuteScalarAsync(cancellationToken);

    protected override Task<DbDataReader> ExecuteDbDataReaderAsync(CommandBehavior behavior, CancellationToken cancellationToken) => inner.ExecuteReaderAsync(behavior, cancellationToken).ContinueWith<DbDataReader>(x => x.Result, cancellationToken);

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            inner.Dispose();
        }

        base.Dispose(disposing);
    }
}

internal sealed class SqlServerInMemorySqliteParameterCollection : DbParameterCollection
{
    private readonly SqliteParameterCollection inner;

    public SqlServerInMemorySqliteParameterCollection(SqliteParameterCollection inner)
    {
        this.inner = inner;
    }

    public override int Count => inner.Count;

    public override object SyncRoot => ((System.Collections.ICollection)inner).SyncRoot;

    public override int Add(object value)
    {
        inner.Add(ToSqliteParameter(value));
        return inner.Count - 1;
    }

    public override void AddRange(Array values)
    {
        foreach (var value in values)
        {
            Add(value!);
        }
    }

    public override void Clear() => inner.Clear();

    public override bool Contains(object value) => value is DbParameter parameter && inner.Contains(parameter.ParameterName);

    public override bool Contains(string value) => inner.Contains(value);

    public override void CopyTo(Array array, int index) => ((System.Collections.ICollection)inner).CopyTo(array, index);

    public override System.Collections.IEnumerator GetEnumerator() => inner.GetEnumerator();

    public override int IndexOf(object value) => value is DbParameter parameter ? inner.IndexOf(parameter.ParameterName) : -1;

    public override int IndexOf(string parameterName) => inner.IndexOf(parameterName);

    public override void Insert(int index, object value) => inner.Insert(index, ToSqliteParameter(value));

    public override void Remove(object value)
    {
        if (value is DbParameter parameter)
        {
            inner.RemoveAt(parameter.ParameterName);
        }
    }

    public override void RemoveAt(int index) => inner.RemoveAt(index);

    public override void RemoveAt(string parameterName) => inner.RemoveAt(parameterName);

    protected override DbParameter GetParameter(int index) => inner[index];

    protected override DbParameter GetParameter(string parameterName) => inner[parameterName];

    protected override void SetParameter(int index, DbParameter value) => inner[index] = ToSqliteParameter(value);

    protected override void SetParameter(string parameterName, DbParameter value) => inner[parameterName] = ToSqliteParameter(value);

    private static SqliteParameter ToSqliteParameter(object value)
    {
        if (value is SqliteParameter sqliteParameter)
        {
            return sqliteParameter;
        }

        if (value is not DbParameter parameter)
        {
            throw new ArgumentException("Only DbParameter values are supported.", nameof(value));
        }

        return new SqliteParameter
        {
            ParameterName = parameter.ParameterName,
            Value = parameter.Value ?? DBNull.Value,
            Direction = parameter.Direction,
            IsNullable = parameter.IsNullable,
            SourceColumn = parameter.SourceColumn,
            SourceVersion = parameter.SourceVersion,
            Size = parameter.Size
        };
    }
}
