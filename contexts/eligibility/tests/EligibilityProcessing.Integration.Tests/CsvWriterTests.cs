using EligibilityProcessing.Web.Export;
using Xunit;

namespace EligibilityProcessing.Integration.Tests;

public class CsvWriterTests
{
    private static IReadOnlyList<string> Row(params string[] fields) => fields;

    [Fact]
    public void Header_and_rows_are_crlf_terminated()
    {
        var csv = CsvWriter.Build(Row("a", "b"), new[] { Row("1", "2") });
        Assert.Equal("a,b\r\n1,2\r\n", csv);
    }

    [Fact]
    public void Plain_fields_are_not_quoted()
    {
        var csv = CsvWriter.Build(Row("h"), new[] { Row("plain") });
        Assert.Equal("h\r\nplain\r\n", csv);
    }

    [Fact]
    public void Fields_with_comma_quote_or_newline_are_quoted_and_escaped()
    {
        var csv = CsvWriter.Build(Row("h"), new[]
        {
            Row("a,b"),
            Row("he said \"hi\""),
            Row("line1\nline2")
        });
        Assert.Contains("\"a,b\"", csv);
        Assert.Contains("\"he said \"\"hi\"\"\"", csv);   // embedded quotes doubled
        Assert.Contains("\"line1\nline2\"", csv);
    }

    [Fact]
    public void Header_only_when_no_rows()
    {
        var csv = CsvWriter.Build(Row("a", "b"), Array.Empty<IReadOnlyList<string>>());
        Assert.Equal("a,b\r\n", csv);
    }
}
