namespace SqlServer.InMemory.TSql;

public interface ITSqlTranslator
{
    string TranslateToSqlite(string tsql);
}
