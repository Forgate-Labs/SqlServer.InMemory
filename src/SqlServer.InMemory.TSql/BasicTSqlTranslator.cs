using System.Text;
using System.Text.RegularExpressions;

namespace SqlServer.InMemory.TSql;

public sealed partial class BasicTSqlTranslator : ITSqlTranslator
{
    public string TranslateToSqlite(string tsql)
    {
        ArgumentNullException.ThrowIfNull(tsql);

        var sql = tsql;
        sql = RemoveIdentityInsertRegex().Replace(sql, string.Empty);
        sql = RemoveBatchSeparatorRegex().Replace(sql, string.Empty);
        sql = RemoveNStringPrefixRegex().Replace(sql, "'");
        sql = SchemaIdentifierRegex().Replace(sql, static match => $"\"{match.Groups[1].Value}_{match.Groups[2].Value}\"");
        sql = DboPrefixRegex().Replace(sql, string.Empty);
        sql = IdentifierRegex().Replace(sql, static match => $"\"{match.Groups[1].Value}\"");
        sql = IfExistsInsertGuardRegex().Replace(sql, static match => $"INSERT OR IGNORE INTO {match.Groups[1].Value} ({match.Groups[2].Value}) VALUES ({match.Groups[3].Value});");
        sql = IfExistsUpdateElseInsertRegex().Replace(sql, static match => $"{match.Groups[2].Value.Trim()}\n{InsertOrIgnoreRegex().Replace(match.Groups[3].Value.Trim(), "INSERT OR IGNORE INTO")}");
        sql = GetDateRegex().Replace(sql, "CURRENT_TIMESTAMP");
        sql = DateAddRegex().Replace(sql, static match => TranslateDateAdd(match));
        sql = OutputInsertedRegex().Replace(sql, static match => $"INSERT INTO {match.Groups[1].Value} ({match.Groups[2].Value}) VALUES ({match.Groups[4].Value}) RETURNING {TranslateInsertedColumns(match.Groups[3].Value)}");
        sql = TopRegex().Replace(sql, static match => $"SELECT {match.Groups[2].Value} LIMIT {match.Groups[1].Value}");
        sql = OffsetFetchRegex().Replace(sql, static match => $"LIMIT {match.Groups[2].Value} OFFSET {match.Groups[1].Value}");
        sql = CountBigRegex().Replace(sql, "COUNT($1)");
        sql = IsNullRegex().Replace(sql, "COALESCE($1, $2)");
        sql = CastTypeRegex().Replace(sql, static match => $"CAST({match.Groups[1].Value} AS {TranslateType(match.Groups[2].Value)})");
        sql = ObjectIdGuardRegex().Replace(sql, string.Empty);
        sql = RemoveStandaloneBeginEndRegex().Replace(sql, string.Empty);
        sql = NormalizeBooleanComparisonRegex().Replace(sql, "$1 <> 1");
        return sql.Trim();
    }

    private static string TranslateInsertedColumns(string outputColumns)
    {
        var columns = InsertedColumnRegex().Matches(outputColumns)
            .Select(match => match.Groups[1].Value)
            .ToArray();

        return columns.Length == 0 ? outputColumns.Replace("INSERTED.", string.Empty, StringComparison.OrdinalIgnoreCase) : string.Join(", ", columns);
    }

    private static string TranslateDateAdd(Match match)
    {
        var part = match.Groups[1].Value.ToLowerInvariant();
        var amount = match.Groups[2].Value.Trim();
        var expression = match.Groups[3].Value.Trim();
        var modifierUnit = part switch
        {
            "year" or "yy" or "yyyy" => "years",
            "month" or "mm" => "months",
            "day" or "dd" => "days",
            "hour" or "hh" => "hours",
            "minute" or "mi" or "n" => "minutes",
            "second" or "ss" or "s" => "seconds",
            _ => part + "s"
        };

        return $"datetime({TranslateDateExpression(expression)}, {QuoteModifier(amount, modifierUnit)})";
    }

    private static string TranslateDateExpression(string expression)
    {
        return CastCurrentTimestampAsDateRegex().IsMatch(expression) ? "CURRENT_TIMESTAMP" : expression;
    }

    private static string QuoteModifier(string amount, string unit)
    {
        return amount.StartsWith('@') ? $"({amount} || ' {unit}')" : $"'{amount} {unit}'";
    }

    private static string TranslateType(string type)
    {
        var normalized = type.Trim().ToLowerInvariant();
        if (normalized.StartsWith("nvarchar", StringComparison.Ordinal) ||
            normalized.StartsWith("varchar", StringComparison.Ordinal) ||
            normalized.StartsWith("nchar", StringComparison.Ordinal) ||
            normalized.StartsWith("char", StringComparison.Ordinal) ||
            normalized == "uniqueidentifier")
        {
            return "TEXT";
        }

        if (normalized is "bit" or "tinyint" or "smallint" or "int" or "bigint")
        {
            return "INTEGER";
        }

        if (normalized.StartsWith("decimal", StringComparison.Ordinal) ||
            normalized.StartsWith("numeric", StringComparison.Ordinal) ||
            normalized is "money" or "smallmoney" or "float" or "real")
        {
            return "REAL";
        }

        if (normalized.StartsWith("datetime", StringComparison.Ordinal) || normalized == "date")
        {
            return "TEXT";
        }

        return type;
    }

    [GeneratedRegex(@"\[([A-Za-z_][A-Za-z0-9_]*)\]\.\[([A-Za-z_][A-Za-z0-9_]*)\]", RegexOptions.CultureInvariant)]
    private static partial Regex SchemaIdentifierRegex();

    [GeneratedRegex(@"\[([A-Za-z_][A-Za-z0-9_]*)\]", RegexOptions.CultureInvariant)]
    private static partial Regex IdentifierRegex();

    [GeneratedRegex(@"\bdbo\.", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DboPrefixRegex();

    [GeneratedRegex(@"SELECT\s+TOP\s*\(\s*(\d+)\s*\)\s+(.+?)(?=(?:\r?\n\s*\)\s+AS\b)|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Singleline)]
    private static partial Regex TopRegex();

    [GeneratedRegex(@"\bOFFSET\s+(.+?)\s+ROWS\s+FETCH\s+NEXT\s+(.+?)\s+ROWS\s+ONLY", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Singleline)]
    private static partial Regex OffsetFetchRegex();

    [GeneratedRegex(@"\bCOUNT_BIG\s*\(\s*(.*?)\s*\)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CountBigRegex();

    [GeneratedRegex(@"\bISNULL\s*\(\s*([^,()]+(?:\([^)]*\))?)\s*,\s*([^)]+?)\s*\)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex IsNullRegex();

    [GeneratedRegex(@"\bGETDATE\s*\(\s*\)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex GetDateRegex();

    [GeneratedRegex(@"\bDATEADD\s*\(\s*(\w+)\s*,\s*([^,]+)\s*,\s*((?:CAST\([^)]*\))|[^)]+)\)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DateAddRegex();

    [GeneratedRegex(@"CAST\s*\(\s*(.+?)\s+AS\s+([A-Za-z]+(?:\([^)]*\))?)\s*\)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Singleline)]
    private static partial Regex CastTypeRegex();

    [GeneratedRegex(@"CAST\s*\(\s*CURRENT_TIMESTAMP\s+AS\s+date\s*\)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CastCurrentTimestampAsDateRegex();

    [GeneratedRegex(@"^\s*SET\s+IDENTITY_INSERT\s+[^\r\n]+\r?\n?", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Multiline)]
    private static partial Regex RemoveIdentityInsertRegex();

    [GeneratedRegex(@"^\s*GO\s*;?\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Multiline)]
    private static partial Regex RemoveBatchSeparatorRegex();

    [GeneratedRegex(@"(?<![A-Za-z0-9_])N'", RegexOptions.CultureInvariant)]
    private static partial Regex RemoveNStringPrefixRegex();

    [GeneratedRegex(@"IF\s+NOT\s+EXISTS\s*\([^)]*\)\s*BEGIN\s*INSERT\s+INTO\s+([^\s(]+)\s*\((.*?)\)\s*VALUES\s*\((.*?)\)\s*;?\s*END\s*;?", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Singleline)]
    private static partial Regex IfExistsInsertGuardRegex();

    [GeneratedRegex(@"IF\s+EXISTS\s*\([^)]*\)\s*BEGIN\s*(UPDATE\s+.+?)\s*END\s*ELSE\s*BEGIN\s*(INSERT\s+INTO\s+.+?)\s*END\s*;?", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Singleline)]
    private static partial Regex IfExistsUpdateElseInsertRegex();

    [GeneratedRegex(@"\bINSERT\s+INTO\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex InsertOrIgnoreRegex();

    [GeneratedRegex(@"^\s*IF\s+OBJECT_ID\s*\([^\r\n]+\)\s+IS\s+NOT\s+NULL\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Multiline)]
    private static partial Regex ObjectIdGuardRegex();

    [GeneratedRegex(@"^\s*(BEGIN|END)\s*;?\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Multiline)]
    private static partial Regex RemoveStandaloneBeginEndRegex();

    [GeneratedRegex(@"INSERT\s+INTO\s+(.+?)\s*\((.*?)\)\s*OUTPUT\s+(.+?)\s+VALUES\s*\((.*?)\)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Singleline)]
    private static partial Regex OutputInsertedRegex();

    [GeneratedRegex(@"INSERTED\.([A-Za-z_][A-Za-z0-9_]*)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex InsertedColumnRegex();

    [GeneratedRegex(@"([^\s]+)\s*<>\s*1", RegexOptions.CultureInvariant)]
    private static partial Regex NormalizeBooleanComparisonRegex();
}
