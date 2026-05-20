using System.Data.Common;
using System.Text.RegularExpressions;

namespace SqlServer.InMemory.EFCore.Interceptors;

internal static partial class SqlServerSchemaQualifierNormalizer
{
    public static string Normalize(DbConnection? connection, SqlServerInMemoryOptions options, string sql)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(sql);

        if (!options.NormalizeSchemas)
        {
            return sql;
        }

        sql = BracketedDboReferenceRegex().Replace(sql, match =>
        {
            if (IsInsideSqlString(sql, match.Index))
            {
                return match.Value;
            }

            var tableName = match.Groups[1].Value;
            return $"[{tableName}]";
        });

        return DboReferenceRegex().Replace(sql, match =>
        {
            if (IsInsideSqlString(sql, match.Index))
            {
                return match.Value;
            }

            return match.Groups[1].Value;
        });
    }

    private static bool IsInsideSqlString(string sql, int index)
    {
        var inString = false;
        for (var i = 0; i < index; i++)
        {
            if (sql[i] != '\'')
            {
                continue;
            }

            if (inString && i + 1 < index && sql[i + 1] == '\'')
            {
                i++;
                continue;
            }

            inString = !inString;
        }

        return inString;
    }

    [GeneratedRegex(@"\[dbo\]\.\[([^\]]+)\]", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex BracketedDboReferenceRegex();

    [GeneratedRegex(@"(?<!')\bdbo\.([A-Za-z_][A-Za-z0-9_]*)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DboReferenceRegex();
}
