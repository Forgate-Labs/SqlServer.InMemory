using System.Text;
using System.Text.RegularExpressions;
using SqlServer.InMemory;

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
        sql = NormalizeDdl(sql);
        sql = SchemaIdentifierRegex().Replace(sql, static match =>
            match.Groups[1].Value.Equals("dbo", StringComparison.OrdinalIgnoreCase)
                ? $"\"{match.Groups[2].Value}\""
                : $"\"{match.Groups[1].Value}_{match.Groups[2].Value}\"");
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
        sql = DefaultCastRegex().Replace(sql, static match => $"DEFAULT {match.Groups[1].Value.Trim()}");
        sql = CastTypeRegex().Replace(sql, static match => $"CAST({match.Groups[1].Value} AS {TranslateType(match.Groups[2].Value)})");
        sql = RemoveStandaloneBeginEndRegex().Replace(sql, string.Empty);
        sql = NormalizeBooleanComparisonRegex().Replace(sql, "$1 <> 1");
        return sql.Trim();
    }

    private static string NormalizeDdl(string sql)
    {
        sql = AlterTableAddUniqueConstraintRegex().Replace(sql, static match => TranslateAddUniqueConstraint(match));
        sql = AlterTableDropConstraintRegex().Replace(sql, static match => $"DROP INDEX IF EXISTS {QuoteIdentifier(UnquoteIdentifier(match.Groups["name"].Value))};");

        if (sql.Contains('"', StringComparison.Ordinal))
        {
            return sql;
        }

        sql = RemoveIdentityDefinitionRegex().Replace(sql, string.Empty);
        sql = AlterTableAddColumnRegex().Replace(sql, static match =>
        {
            var type = match.Groups["type"].Value;
            return $"{match.Groups["prefix"].Value}{SqlServerInMemoryTypeMapper.ToSqliteType(type)}{match.Groups["rest"].Value}";
        });

        sql = CreateTableRegex().Replace(sql, static match =>
            $"{match.Groups["prefix"].Value}{NormalizeCreateTableBody(match.Groups["body"].Value)}){match.Groups["suffix"].Value}");

        return sql;
    }

    private static string TranslateAddUniqueConstraint(Match match)
    {
        var tableName = NormalizeTableIdentifier(match.Groups["table"].Value);
        var indexName = UnquoteIdentifier(match.Groups["name"].Value);
        var columns = SplitTopLevelCommaSeparated(match.Groups["columns"].Value)
            .Select(NormalizeIndexColumn)
            .Where(column => !string.IsNullOrWhiteSpace(column))
            .Select(QuoteIdentifier);

        return $"CREATE UNIQUE INDEX IF NOT EXISTS {QuoteIdentifier(indexName)} ON {QuoteIdentifier(tableName)} ({string.Join(", ", columns)});";
    }

    private static string NormalizeTableIdentifier(string identifier)
    {
        var text = UnquoteIdentifier(identifier);
        var parts = text.Split('.', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length != 2)
        {
            return text;
        }

        return parts[0].Equals("dbo", StringComparison.OrdinalIgnoreCase) ? parts[1] : $"{parts[0]}_{parts[1]}";
    }

    private static string NormalizeIndexColumn(string column)
    {
        var match = IndexColumnIdentifierRegex().Match(column);
        if (match.Success)
        {
            return match.Groups["bracket"].Success
                ? match.Groups["bracket"].Value
                : match.Groups["quote"].Success
                    ? match.Groups["quote"].Value
                    : match.Groups["plain"].Value;
        }

        return IndexColumnSortDirectionRegex().Replace(column.Trim(), string.Empty).Trim();
    }

    private static string UnquoteIdentifier(string identifier)
    {
        return identifier.Trim()
            .Replace("[", string.Empty, StringComparison.Ordinal)
            .Replace("]", string.Empty, StringComparison.Ordinal)
            .Replace("\"", string.Empty, StringComparison.Ordinal);
    }

    private static string QuoteIdentifier(string identifier)
    {
        return $"\"{identifier.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";
    }

    private static string NormalizeCreateTableBody(string body)
    {
        var definitions = SplitTopLevelCommaSeparated(body);
        for (var index = 0; index < definitions.Count; index++)
        {
            definitions[index] = NormalizeColumnDefinition(definitions[index]);
        }

        return string.Join(",", definitions);
    }

    private static string NormalizeColumnDefinition(string definition)
    {
        var trimmed = definition.TrimStart();
        if (ConstraintDefinitionRegex().IsMatch(trimmed))
        {
            return definition;
        }

        var match = ColumnDefinitionTypeRegex().Match(definition);
        if (!match.Success)
        {
            return definition;
        }

        var type = match.Groups["type"].Value;
        var sqliteType = SqlServerInMemoryTypeMapper.ToSqliteType(type);
        if (sqliteType.Equals(type, StringComparison.Ordinal))
        {
            return definition;
        }

        return $"{match.Groups["prefix"].Value}{sqliteType}{match.Groups["rest"].Value}";
    }

    private static List<string> SplitTopLevelCommaSeparated(string text)
    {
        var parts = new List<string>();
        var start = 0;
        var depth = 0;
        var inString = false;

        for (var index = 0; index < text.Length; index++)
        {
            var character = text[index];
            if (character == '\'')
            {
                if (inString && index + 1 < text.Length && text[index + 1] == '\'')
                {
                    index++;
                    continue;
                }

                inString = !inString;
                continue;
            }

            if (inString)
            {
                continue;
            }

            if (character == '(')
            {
                depth++;
                continue;
            }

            if (character == ')' && depth > 0)
            {
                depth--;
                continue;
            }

            if (character == ',' && depth == 0)
            {
                parts.Add(text[start..index]);
                start = index + 1;
            }
        }

        parts.Add(text[start..]);
        return parts;
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
        return SqlServerInMemoryTypeMapper.ToSqliteType(type);
    }

    [GeneratedRegex(@"(?<prefix>\bCREATE\s+TABLE\s+.+?\()(?<body>.*)\)(?<suffix>\s*;?)\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Singleline)]
    private static partial Regex CreateTableRegex();

    [GeneratedRegex(@"\bALTER\s+TABLE\s+(?<table>(?:\[[^\]]+\]|""[^""]+""|[A-Za-z_][A-Za-z0-9_]*)(?:\s*\.\s*(?:\[[^\]]+\]|""[^""]+""|[A-Za-z_][A-Za-z0-9_]*))?)\s+ADD\s+CONSTRAINT\s+(?<name>\[[^\]]+\]|""[^""]+""|[A-Za-z_][A-Za-z0-9_]*)\s+UNIQUE\s+(?:(?:CLUSTERED|NONCLUSTERED)\s+)?\((?<columns>.*?)\)\s*(?:WITH\s*\(.*?\)\s*)?(?:ON\s+(?:\[[^\]]+\]|""[^""]+""|[A-Za-z_][A-Za-z0-9_]*)\s*)?;?", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Singleline)]
    private static partial Regex AlterTableAddUniqueConstraintRegex();

    [GeneratedRegex(@"\bALTER\s+TABLE\s+(?<table>(?:\[[^\]]+\]|""[^""]+""|[A-Za-z_][A-Za-z0-9_]*)(?:\s*\.\s*(?:\[[^\]]+\]|""[^""]+""|[A-Za-z_][A-Za-z0-9_]*))?)\s+DROP\s+CONSTRAINT\s+(?<name>\[[^\]]+\]|""[^""]+""|[A-Za-z_][A-Za-z0-9_]*)\s*;?", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Singleline)]
    private static partial Regex AlterTableDropConstraintRegex();

    [GeneratedRegex(@"^\s*(?:\[(?<bracket>[^\]]+)\]|""(?<quote>[^""]+)""|(?<plain>[A-Za-z_][A-Za-z0-9_]*))", RegexOptions.CultureInvariant)]
    private static partial Regex IndexColumnIdentifierRegex();

    [GeneratedRegex(@"\s+(?:ASC|DESC)\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex IndexColumnSortDirectionRegex();

    [GeneratedRegex(@"(?<prefix>\bALTER\s+TABLE\s+.+?\s+ADD\s+(?:COLUMN\s+)?(?:\[[^\]]+\]|[A-Za-z_][A-Za-z0-9_]*)\s+)(?<type>\[?[A-Za-z][A-Za-z0-9_]*\]?(?:\s*\([^)]*\))?)(?<rest>[^;]*;?)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Singleline)]
    private static partial Regex AlterTableAddColumnRegex();

    [GeneratedRegex(@"(?<prefix>^\s*(?:\[[^\]]+\]|[A-Za-z_][A-Za-z0-9_]*)\s+)(?<type>\[?[A-Za-z][A-Za-z0-9_]*\]?(?:\s*\([^)]*\))?)(?<rest>.*)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Singleline)]
    private static partial Regex ColumnDefinitionTypeRegex();

    [GeneratedRegex(@"^(CONSTRAINT|PRIMARY|FOREIGN|UNIQUE|CHECK|KEY)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ConstraintDefinitionRegex();

    [GeneratedRegex(@"\s+IDENTITY\s*\([^)]*\)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex RemoveIdentityDefinitionRegex();

    [GeneratedRegex(@"\[([A-Za-z_][A-Za-z0-9_]*)\]\.\[([A-Za-z_][A-Za-z0-9_]*)\]", RegexOptions.CultureInvariant)]
    private static partial Regex SchemaIdentifierRegex();

    [GeneratedRegex(@"\[([A-Za-z_][A-Za-z0-9_]*)\]", RegexOptions.CultureInvariant)]
    private static partial Regex IdentifierRegex();

    [GeneratedRegex(@"(?<!')\bdbo\.", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
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

    [GeneratedRegex(@"\bDEFAULT\s+CAST\s*\(\s*(.+?)\s+AS\s+\[?[A-Za-z][A-Za-z0-9_]*\]?(?:\s*\([^)]*\))?\s*\)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Singleline)]
    private static partial Regex DefaultCastRegex();

    [GeneratedRegex(@"CAST\s*\(\s*(.+?)\s+AS\s+(\[?[A-Za-z][A-Za-z0-9_]*\]?(?:\s*\([^)]*\))?)\s*\)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Singleline)]
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

    [GeneratedRegex(@"^\s*(BEGIN|END)\s*;?\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Multiline)]
    private static partial Regex RemoveStandaloneBeginEndRegex();

    [GeneratedRegex(@"INSERT\s+INTO\s+(.+?)\s*\((.*?)\)\s*OUTPUT\s+(.+?)\s+VALUES\s*\((.*?)\)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Singleline)]
    private static partial Regex OutputInsertedRegex();

    [GeneratedRegex(@"INSERTED\.([A-Za-z_][A-Za-z0-9_]*)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex InsertedColumnRegex();

    [GeneratedRegex(@"([^\s]+)\s*<>\s*1", RegexOptions.CultureInvariant)]
    private static partial Regex NormalizeBooleanComparisonRegex();
}
