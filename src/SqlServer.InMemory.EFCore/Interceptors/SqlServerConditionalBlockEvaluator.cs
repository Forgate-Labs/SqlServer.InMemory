using System.Data.Common;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace SqlServer.InMemory.EFCore.Interceptors;

internal static partial class SqlServerConditionalBlockEvaluator
{
    public static string Evaluate(DbConnection? connection, SqlServerInMemoryOptions options, string sql)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(sql);

        if (connection is not SqliteConnection sqliteConnection)
        {
            return sql;
        }

        var builder = new StringBuilder(sql.Length);
        var index = 0;
        while (index < sql.Length)
        {
            var ifIndex = FindKeywordOutsideString(sql, "IF", index);
            if (ifIndex < 0)
            {
                builder.Append(sql, index, sql.Length - index);
                break;
            }

            builder.Append(sql, index, ifIndex - index);
            if (!TryReadIfBlock(sql, ifIndex, out var condition, out var body, out var endIndex))
            {
                builder.Append(sql[ifIndex]);
                index = ifIndex + 1;
                continue;
            }

            var shouldExecuteBody = TryEvaluateCondition(sqliteConnection, options, condition);
            if (shouldExecuteBody is null)
            {
                builder.Append(sql, ifIndex, endIndex - ifIndex);
            }
            else
            {
                builder.Append(shouldExecuteBody.Value ? body.Trim() : "SELECT 1 WHERE 0;");
            }

            index = endIndex;
        }

        return builder.ToString();
    }

    private static bool? TryEvaluateCondition(SqliteConnection connection, SqlServerInMemoryOptions options, string condition)
    {
        var metadataCondition = MetadataNullConditionRegex().Match(condition);
        if (metadataCondition.Success)
        {
            var functionName = metadataCondition.Groups["function"].Value;
            var arguments = SplitArguments(metadataCondition.Groups["arguments"].Value);
            var valueExists = functionName.Equals("OBJECT_ID", StringComparison.OrdinalIgnoreCase)
                ? EvaluateObjectId(connection, options, arguments)
                : EvaluateColLength(connection, options, arguments);
            var isNotNullCondition = metadataCondition.Groups["not"].Success;
            return isNotNullCondition ? valueExists : !valueExists;
        }

        var terms = SplitTopLevelAnd(condition);
        if (terms.Count == 0)
        {
            return null;
        }

        var result = true;
        foreach (var term in terms)
        {
            var termValue = TryEvaluateExistsTerm(connection, options, term);
            if (termValue is null)
            {
                return null;
            }

            result &= termValue.Value;
        }

        return result;
    }

    private static bool? TryEvaluateExistsTerm(SqliteConnection connection, SqlServerInMemoryOptions options, string term)
    {
        var text = term.Trim();
        var isNegated = false;
        var index = 0;

        if (StartsWithKeyword(text, "NOT", index))
        {
            isNegated = true;
            index += 3;
            SkipWhitespace(text, ref index);
        }

        if (!StartsWithKeyword(text, "EXISTS", index))
        {
            return null;
        }

        index += 6;
        SkipWhitespace(text, ref index);
        if (index >= text.Length || text[index] != '(')
        {
            return null;
        }

        var closeIndex = FindMatchingParenthesis(text, index);
        if (closeIndex < 0 || !string.IsNullOrWhiteSpace(text[(closeIndex + 1)..]))
        {
            return null;
        }

        var exists = TryEvaluateCatalogQuery(connection, options, text[(index + 1)..closeIndex]);
        return exists is null ? null : isNegated ? !exists.Value : exists.Value;
    }

    private static bool? TryEvaluateCatalogQuery(SqliteConnection connection, SqlServerInMemoryOptions options, string query)
    {
        var catalogMatch = CatalogFromRegex().Match(query);
        if (!catalogMatch.Success)
        {
            return null;
        }

        var nameMatch = CatalogNamePredicateRegex().Match(query);
        if (!nameMatch.Success)
        {
            return null;
        }

        var catalog = catalogMatch.Groups["catalog"].Value;
        var objectIdMatch = catalog.Equals("indexes", StringComparison.OrdinalIgnoreCase)
            ? IndexObjectIdPredicateRegex().Match(query)
            : KeyConstraintObjectIdPredicateRegex().Match(query);

        string? tableName = null;
        if (objectIdMatch.Success)
        {
            var arguments = SplitArguments(objectIdMatch.Groups["arguments"].Value);
            if (arguments.Count == 0 || (arguments.Count > 1 && !IsUserTableType(arguments[1])))
            {
                return false;
            }

            tableName = FindExistingTable(connection, GetTableNameCandidates(arguments[0], options));
            if (tableName is null)
            {
                return false;
            }
        }

        var indexName = UnquoteSqlString(nameMatch.Groups["value"].Value);
        return FindExistingIndex(connection, indexName, tableName);
    }

    private static bool EvaluateObjectId(SqliteConnection connection, SqlServerInMemoryOptions options, IReadOnlyList<string> arguments)
    {
        if (arguments.Count == 0 || (arguments.Count > 1 && !IsUserTableType(arguments[1])))
        {
            return false;
        }

        return FindExistingTable(connection, GetTableNameCandidates(arguments[0], options)) is not null;
    }

    private static bool EvaluateColLength(SqliteConnection connection, SqlServerInMemoryOptions options, IReadOnlyList<string> arguments)
    {
        if (arguments.Count < 2)
        {
            return false;
        }

        var tableName = FindExistingTable(connection, GetTableNameCandidates(arguments[0], options));
        if (tableName is null)
        {
            return false;
        }

        using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info(\"{tableName.Replace("\"", "\"\"")}\");";
        using var reader = command.ExecuteReader();
        var columnName = UnquoteName(arguments[1]);
        while (reader.Read())
        {
            if (reader.GetString(1).Equals(columnName, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static string? FindExistingTable(SqliteConnection connection, IEnumerable<string> candidates)
    {
        foreach (var candidate in candidates)
        {
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT name FROM sqlite_master WHERE type IN ('table', 'view') AND lower(name) = lower($name) LIMIT 1;";
            command.Parameters.AddWithValue("$name", candidate);
            var result = command.ExecuteScalar();
            if (result is string name)
            {
                return name;
            }
        }

        return null;
    }

    private static bool FindExistingIndex(SqliteConnection connection, string indexName, string? tableName)
    {
        using var command = connection.CreateCommand();
        command.CommandText = tableName is null
            ? "SELECT 1 FROM sqlite_master WHERE type = 'index' AND lower(name) = lower($name) LIMIT 1;"
            : "SELECT 1 FROM sqlite_master WHERE type = 'index' AND lower(name) = lower($name) AND lower(tbl_name) = lower($table) LIMIT 1;";
        command.Parameters.AddWithValue("$name", indexName);
        if (tableName is not null)
        {
            command.Parameters.AddWithValue("$table", tableName);
        }

        return command.ExecuteScalar() is not null;
    }

    private static IEnumerable<string> GetTableNameCandidates(string sqlServerName, SqlServerInMemoryOptions options)
    {
        var normalized = UnquoteSqlString(sqlServerName).Trim();
        normalized = normalized.Replace("[", string.Empty, StringComparison.Ordinal).Replace("]", string.Empty, StringComparison.Ordinal);
        normalized = normalized.Replace("\"", string.Empty, StringComparison.Ordinal);

        var parts = normalized.Split('.', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 2)
        {
            var schema = parts[0];
            var table = parts[1];
            if (options.NormalizeSchemas)
            {
                if (schema.Equals("dbo", StringComparison.OrdinalIgnoreCase))
                {
                    yield return table;
                }

                if (options.SchemaMode == SqlServerInMemorySchemaMode.PrefixSchemaName)
                {
                    yield return $"{schema}_{table}";
                }

                if (!schema.Equals("dbo", StringComparison.OrdinalIgnoreCase))
                {
                    yield return table;
                }
            }
            else
            {
                yield return normalized;
            }

            yield return normalized;
            yield break;
        }

        yield return normalized;
    }

    private static List<string> SplitArguments(string arguments)
    {
        var result = new List<string>();
        var start = 0;
        var depth = 0;
        var inString = false;

        for (var index = 0; index < arguments.Length; index++)
        {
            var character = arguments[index];
            if (character == '\'')
            {
                if (inString && index + 1 < arguments.Length && arguments[index + 1] == '\'')
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
                result.Add(arguments[start..index].Trim());
                start = index + 1;
            }
        }

        result.Add(arguments[start..].Trim());
        return result;
    }

    private static List<string> SplitTopLevelAnd(string condition)
    {
        var result = new List<string>();
        var start = 0;
        var depth = 0;
        var inString = false;

        for (var index = 0; index < condition.Length; index++)
        {
            var character = condition[index];
            if (character == '\'')
            {
                if (inString && index + 1 < condition.Length && condition[index + 1] == '\'')
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

            if (depth == 0 && IsKeywordAt(condition, index, "AND"))
            {
                result.Add(condition[start..index].Trim());
                index += 2;
                start = index + 1;
            }
        }

        result.Add(condition[start..].Trim());
        result.RemoveAll(string.IsNullOrWhiteSpace);
        return result;
    }

    private static bool TryReadIfBlock(string sql, int ifIndex, out string condition, out string body, out int endIndex)
    {
        condition = string.Empty;
        body = string.Empty;
        endIndex = ifIndex;

        var beginIndex = FindTopLevelKeywordOutsideString(sql, "BEGIN", ifIndex + 2);
        if (beginIndex < 0 || !TryFindMatchingEnd(sql, beginIndex, out var endKeywordIndex, out endIndex))
        {
            return false;
        }

        condition = sql[(ifIndex + 2)..beginIndex].Trim();
        body = sql[(beginIndex + 5)..endKeywordIndex];
        return true;
    }

    private static int FindKeywordOutsideString(string text, string keyword, int startIndex)
    {
        var inString = false;
        for (var index = startIndex; index < text.Length; index++)
        {
            if (text[index] == '\'')
            {
                if (inString && index + 1 < text.Length && text[index + 1] == '\'')
                {
                    index++;
                    continue;
                }

                inString = !inString;
                continue;
            }

            if (!inString && IsKeywordAt(text, index, keyword))
            {
                return index;
            }
        }

        return -1;
    }

    private static int FindTopLevelKeywordOutsideString(string text, string keyword, int startIndex)
    {
        var depth = 0;
        var inString = false;
        for (var index = startIndex; index < text.Length; index++)
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

            if (depth == 0 && IsKeywordAt(text, index, keyword))
            {
                return index;
            }
        }

        return -1;
    }

    private static bool TryFindMatchingEnd(string text, int beginIndex, out int endKeywordIndex, out int endIndex)
    {
        var depth = 1;
        var inString = false;
        endKeywordIndex = -1;
        endIndex = -1;

        for (var index = beginIndex + 5; index < text.Length; index++)
        {
            if (text[index] == '\'')
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

            if (IsKeywordAt(text, index, "BEGIN"))
            {
                depth++;
                index += 4;
                continue;
            }

            if (!IsKeywordAt(text, index, "END"))
            {
                continue;
            }

            depth--;
            if (depth != 0)
            {
                index += 2;
                continue;
            }

            endKeywordIndex = index;
            endIndex = index + 3;
            SkipWhitespace(text, ref endIndex);
            if (endIndex < text.Length && text[endIndex] == ';')
            {
                endIndex++;
            }

            return true;
        }

        return false;
    }

    private static int FindMatchingParenthesis(string text, int openIndex)
    {
        var depth = 0;
        var inString = false;
        for (var index = openIndex; index < text.Length; index++)
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

            if (character != ')')
            {
                continue;
            }

            depth--;
            if (depth == 0)
            {
                return index;
            }
        }

        return -1;
    }

    private static bool StartsWithKeyword(string text, string keyword, int index)
    {
        return IsKeywordAt(text, index, keyword);
    }

    private static bool IsKeywordAt(string text, int index, string keyword)
    {
        if (index < 0 || index + keyword.Length > text.Length)
        {
            return false;
        }

        if (!text.AsSpan(index, keyword.Length).Equals(keyword, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var beforeIsIdentifier = index > 0 && IsIdentifierCharacter(text[index - 1]);
        var afterIndex = index + keyword.Length;
        var afterIsIdentifier = afterIndex < text.Length && IsIdentifierCharacter(text[afterIndex]);
        return !beforeIsIdentifier && !afterIsIdentifier;
    }

    private static bool IsIdentifierCharacter(char character)
    {
        return char.IsLetterOrDigit(character) || character == '_' || character == '@';
    }

    private static void SkipWhitespace(string text, ref int index)
    {
        while (index < text.Length && char.IsWhiteSpace(text[index]))
        {
            index++;
        }
    }

    private static bool IsUserTableType(string type)
    {
        return UnquoteSqlString(type).Equals("U", StringComparison.OrdinalIgnoreCase);
    }

    private static string UnquoteName(string value)
    {
        return UnquoteSqlString(value).Trim().Trim('[', ']', '"');
    }

    private static string UnquoteSqlString(string value)
    {
        var text = value.Trim();
        if (text.StartsWith("N'", StringComparison.OrdinalIgnoreCase))
        {
            text = text[1..];
        }

        if (text.Length >= 2 && text[0] == '\'' && text[^1] == '\'')
        {
            text = text[1..^1].Replace("''", "'", StringComparison.Ordinal);
        }

        return text;
    }

    [GeneratedRegex(@"^(?<function>OBJECT_ID|COL_LENGTH)\s*\((?<arguments>.*)\)\s+IS\s+(?<not>NOT\s+)?NULL$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Singleline)]
    private static partial Regex MetadataNullConditionRegex();

    [GeneratedRegex(@"\bFROM\s+sys\.(?<catalog>indexes|key_constraints)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Singleline)]
    private static partial Regex CatalogFromRegex();

    [GeneratedRegex(@"(?:\[name\]|name)\s*=\s*(?<value>N?'(?:''|[^'])*')", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Singleline)]
    private static partial Regex CatalogNamePredicateRegex();

    [GeneratedRegex(@"(?:\[object_id\]|object_id)\s*=\s*OBJECT_ID\s*\((?<arguments>.*?)\)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Singleline)]
    private static partial Regex IndexObjectIdPredicateRegex();

    [GeneratedRegex(@"(?:\[parent_object_id\]|parent_object_id)\s*=\s*OBJECT_ID\s*\((?<arguments>.*?)\)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Singleline)]
    private static partial Regex KeyConstraintObjectIdPredicateRegex();
}
