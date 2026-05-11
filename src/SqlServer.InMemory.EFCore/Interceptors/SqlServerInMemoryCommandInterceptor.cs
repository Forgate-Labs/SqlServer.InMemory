using System.Data.Common;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore.Diagnostics;
using SqlServer.InMemory.Exceptions;
using SqlServer.InMemory.TSql;

namespace SqlServer.InMemory.EFCore.Interceptors;

internal sealed class SqlServerInMemoryCommandInterceptor : DbCommandInterceptor
{
    private readonly ITSqlTranslator _translator;

    public SqlServerInMemoryCommandInterceptor(ITSqlTranslator translator)
    {
        _translator = translator;
    }

    public override InterceptionResult<DbDataReader> ReaderExecuting(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<DbDataReader> result)
    {
        TranslateCommand(command);
        return result;
    }

    public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<DbDataReader> result,
        CancellationToken cancellationToken = default)
    {
        TranslateCommand(command);
        return ValueTask.FromResult(result);
    }

    public override InterceptionResult<int> NonQueryExecuting(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<int> result)
    {
        TranslateCommand(command);
        return result;
    }

    public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        TranslateCommand(command);
        return ValueTask.FromResult(result);
    }

    public override InterceptionResult<object> ScalarExecuting(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<object> result)
    {
        TranslateCommand(command);
        return result;
    }

    public override ValueTask<InterceptionResult<object>> ScalarExecutingAsync(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<object> result,
        CancellationToken cancellationToken = default)
    {
        TranslateCommand(command);
        return ValueTask.FromResult(result);
    }

    public override void CommandFailed(DbCommand command, CommandErrorEventData eventData)
    {
        throw TranslateException(eventData.Exception);
    }

    public override Task CommandFailedAsync(DbCommand command, CommandErrorEventData eventData, CancellationToken cancellationToken = default)
    {
        throw TranslateException(eventData.Exception);
    }

    private void TranslateCommand(DbCommand command)
    {
        command.CommandText = _translator.TranslateToSqlite(command.CommandText);
        NormalizeSqliteParameters(command);
    }

    private static void NormalizeSqliteParameters(DbCommand command)
    {
        if (command is not SqliteCommand || command.Parameters.Count == 0)
        {
            return;
        }

        var replacementParameters = new List<SqliteParameter>();
        foreach (DbParameter parameter in command.Parameters)
        {
            if (parameter is SqliteParameter)
            {
                continue;
            }

            var replacement = new SqliteParameter
            {
                ParameterName = parameter.ParameterName,
                Value = parameter.Value ?? DBNull.Value,
                Direction = parameter.Direction,
                IsNullable = parameter.IsNullable,
                SourceColumn = parameter.SourceColumn,
                SourceVersion = parameter.SourceVersion,
                Size = parameter.Size
            };

            replacementParameters.Add(replacement);
        }

        if (replacementParameters.Count == 0)
        {
            return;
        }

        var originalParameters = command.Parameters.Cast<DbParameter>().ToArray();
        command.Parameters.Clear();

        foreach (var parameter in originalParameters)
        {
            if (parameter is SqliteParameter sqliteParameter)
            {
                command.Parameters.Add(sqliteParameter);
                continue;
            }

            var replacement = replacementParameters.First(x => x.ParameterName == parameter.ParameterName);
            command.Parameters.Add(replacement);
        }
    }

    private static Exception TranslateException(Exception exception)
    {
        var sqlite = FindSqliteException(exception);
        if (sqlite is null)
        {
            return exception;
        }

        if (sqlite.SqliteErrorCode == 19 && sqlite.SqliteExtendedErrorCode == 787)
        {
            return new SqlServerInMemoryForeignKeyException("A foreign key constraint failed.", exception);
        }

        if (sqlite.SqliteErrorCode == 19 && (sqlite.SqliteExtendedErrorCode == 2067 || sqlite.SqliteExtendedErrorCode == 1555))
        {
            return new SqlServerInMemoryUniqueConstraintException("A unique constraint failed.", exception);
        }

        return exception;
    }

    private static SqliteException? FindSqliteException(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException!)
        {
            if (current is SqliteException sqlite)
            {
                return sqlite;
            }
        }

        return null;
    }
}
