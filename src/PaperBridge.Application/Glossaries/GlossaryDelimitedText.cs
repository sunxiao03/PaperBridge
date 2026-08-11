using System.Text;
using PaperBridge.Domain.Glossaries;

namespace PaperBridge.Application.Glossaries;

public static class GlossaryDelimitedText
{
    private static readonly string[] Header =
    [
        "english", "preferred_chinese", "english_aliases", "chinese_aliases", "category",
        "explanation", "notes", "source_reference", "priority", "review_status"
    ];

    public static IReadOnlyList<GlossaryImportRow> Parse(string content, char delimiter)
    {
        ArgumentNullException.ThrowIfNull(content);
        if (delimiter is not (',' or '\t'))
        {
            throw new ArgumentOutOfRangeException(nameof(delimiter));
        }

        var rows = ParseRows(content, delimiter);
        if (rows.Count == 0)
        {
            return [];
        }

        var indexes = rows[0]
            .Select((value, index) => (value: value.Trim().ToLowerInvariant(), index))
            .ToDictionary(item => item.value, item => item.index, StringComparer.OrdinalIgnoreCase);
        foreach (var required in Header.Take(2))
        {
            if (!indexes.ContainsKey(required))
            {
                throw new FormatException($"缺少必需列：{required}");
            }
        }

        var result = new List<GlossaryImportRow>();
        for (var rowIndex = 1; rowIndex < rows.Count; rowIndex++)
        {
            var row = rows[rowIndex];
            string Get(string name) => indexes.TryGetValue(name, out var index) && index < row.Count
                ? row[index].Trim()
                : string.Empty;
            var english = Get("english");
            var chinese = Get("preferred_chinese");
            if (english.Length == 0 && chinese.Length == 0)
            {
                continue;
            }

            if (english.Length == 0 || chinese.Length == 0)
            {
                throw new FormatException($"第 {rowIndex + 1} 行的英文术语或首选译名为空。");
            }

            if (!int.TryParse(Get("priority"), out var priority))
            {
                priority = 0;
            }

            var status = Enum.TryParse<GlossaryReviewStatus>(Get("review_status"), true, out var parsed)
                ? parsed
                : GlossaryReviewStatus.Pending;
            result.Add(new GlossaryImportRow(
                english,
                chinese,
                SplitAliases(Get("english_aliases")),
                SplitAliases(Get("chinese_aliases")),
                NullIfEmpty(Get("category")),
                NullIfEmpty(Get("explanation")),
                NullIfEmpty(Get("notes")),
                NullIfEmpty(Get("source_reference")),
                priority,
                status));
        }

        return result;
    }

    public static string Write(IEnumerable<GlossaryTerm> terms, char delimiter)
    {
        ArgumentNullException.ThrowIfNull(terms);
        var builder = new StringBuilder();
        builder.AppendLine(string.Join(delimiter, Header.Select(value => Escape(value, delimiter))));
        foreach (var term in terms.OrderBy(term => term.English, StringComparer.Ordinal))
        {
            var values = new[]
            {
                term.English, term.PreferredChinese, string.Join("; ", term.EnglishAliases),
                string.Join("; ", term.ChineseAliases), term.Category, term.Explanation, term.Notes,
                term.SourceReference, term.Priority.ToString(System.Globalization.CultureInfo.InvariantCulture),
                term.ReviewStatus.ToString()
            };
            builder.AppendLine(string.Join(delimiter, values.Select(value => Escape(value ?? string.Empty, delimiter))));
        }

        return builder.ToString();
    }

    private static IReadOnlyList<List<string>> ParseRows(string content, char delimiter)
    {
        var rows = new List<List<string>>();
        var row = new List<string>();
        var field = new StringBuilder();
        var quoted = false;
        for (var index = 0; index < content.Length; index++)
        {
            var character = content[index];
            if (quoted)
            {
                if (character == '"' && index + 1 < content.Length && content[index + 1] == '"')
                {
                    field.Append('"');
                    index++;
                }
                else if (character == '"')
                {
                    quoted = false;
                }
                else
                {
                    field.Append(character);
                }
            }
            else if (character == '"' && field.Length == 0)
            {
                quoted = true;
            }
            else if (character == delimiter)
            {
                row.Add(field.ToString());
                field.Clear();
            }
            else if (character is '\r' or '\n')
            {
                if (character == '\r' && index + 1 < content.Length && content[index + 1] == '\n')
                {
                    index++;
                }

                row.Add(field.ToString());
                field.Clear();
                rows.Add(row);
                row = [];
            }
            else
            {
                field.Append(character);
            }
        }

        if (quoted)
        {
            throw new FormatException("导入文件包含未闭合的引号。");
        }

        if (field.Length > 0 || row.Count > 0)
        {
            row.Add(field.ToString());
            rows.Add(row);
        }

        return rows;
    }

    private static string Escape(string value, char delimiter)
    {
        if (value.Length > 0 && value[0] is '=' or '+' or '-' or '@')
        {
            value = "'" + value;
        }

        return value.IndexOfAny([delimiter, '"', '\r', '\n']) >= 0
            ? $"\"{value.Replace("\"", "\"\"")}\""
            : value;
    }

    private static IReadOnlyList<string> SplitAliases(string value) => value
        .Split([';', '|'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToArray();

    private static string? NullIfEmpty(string value) => value.Length == 0 ? null : value;
}

public sealed record GlossaryImportRow(
    string English,
    string PreferredChinese,
    IReadOnlyList<string> EnglishAliases,
    IReadOnlyList<string> ChineseAliases,
    string? Category,
    string? Explanation,
    string? Notes,
    string? SourceReference,
    int Priority,
    GlossaryReviewStatus ReviewStatus);
