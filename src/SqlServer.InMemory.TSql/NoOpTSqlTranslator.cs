namespace SqlServer.InMemory.TSql;

public sealed class NoOpTSqlTranslator : ITSqlTranslator
{
    public string TranslateToSqlite(string tsql) => tsql;
}
