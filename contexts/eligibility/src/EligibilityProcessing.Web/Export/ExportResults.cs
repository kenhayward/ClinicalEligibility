using System.Text;
using Microsoft.AspNetCore.Mvc;

namespace EligibilityProcessing.Web.Export;

/// <summary>
/// Reusable file-download results for export endpoints. Returns the body as an
/// attachment (Content-Disposition) so the browser downloads rather than renders.
/// </summary>
public static class ExportResults
{
    private static readonly byte[] Utf8Bom = { 0xEF, 0xBB, 0xBF };

    /// <summary>
    /// A downloadable CSV file. Prepends a UTF-8 BOM so spreadsheet apps detect
    /// the encoding and render non-ASCII text correctly.
    /// </summary>
    public static FileContentResult CsvFile(string csv, string downloadName)
    {
        var body = Encoding.UTF8.GetBytes(csv ?? "");
        var bytes = new byte[Utf8Bom.Length + body.Length];
        Buffer.BlockCopy(Utf8Bom, 0, bytes, 0, Utf8Bom.Length);
        Buffer.BlockCopy(body, 0, bytes, Utf8Bom.Length, body.Length);
        return new FileContentResult(bytes, "text/csv; charset=utf-8")
        {
            FileDownloadName = string.IsNullOrWhiteSpace(downloadName) ? "export.csv" : downloadName
        };
    }
}
