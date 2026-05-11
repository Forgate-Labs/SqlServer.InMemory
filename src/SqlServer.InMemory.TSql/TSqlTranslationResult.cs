namespace SqlServer.InMemory.TSql;

public sealed class TSqlTranslationResult
{
    public required string OriginalSql { get; init; }

    public required string TranslatedSql { get; init; }

    public IReadOnlyList<string> Warnings { get; init; } = [];
}
