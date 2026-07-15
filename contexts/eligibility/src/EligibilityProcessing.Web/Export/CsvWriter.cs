using System.Text;

namespace EligibilityProcessing.Web.Export;

/// <summary>
/// Minimal RFC 4180 CSV builder, reusable across export endpoints. Fields are
/// quoted only when they contain a comma, quote, or newline; embedded quotes are
/// doubled. Rows are CRLF-terminated.
/// </summary>
public static class CsvWriter
{
    public static string Build(IReadOnlyList<string> headers, IEnumerable<IReadOnlyList<string>> rows)
    {
        ArgumentNullException.ThrowIfNull(headers);
        ArgumentNullException.ThrowIfNull(rows);

        var sb = new StringBuilder();
        AppendRow(sb, headers);
        foreach (var row in rows)
        {
            AppendRow(sb, row);
        }
        return sb.ToString();
    }

    private static void AppendRow(StringBuilder sb, IReadOnlyList<string> fields)
    {
        for (var i = 0; i < fields.Count; i++)
        {
            if (i > 0) sb.Append(',');
            sb.Append(Escape(fields[i]));
        }
        sb.Append("\r\n");
    }

    private static string Escape(string? field)
    {
        field ??= "";
        if (field.IndexOfAny(QuoteTriggers) < 0)
        {
            return field;
        }
        return "\"" + field.Replace("\"", "\"\"") + "\"";
    }

    private static readonly char[] QuoteTriggers = { '"', ',', '\n', '\r' };
}
