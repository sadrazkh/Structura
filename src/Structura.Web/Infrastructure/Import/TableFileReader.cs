using System.Globalization;
using System.Text;
using CsvHelper;
using CsvHelper.Configuration;
using MiniExcelLibs;

namespace Structura.Web.Infrastructure.Import;

public sealed record TableRow(int RowNumber, IReadOnlyDictionary<string, string?> Cells);

/// <summary>
/// Streaming reader for .xlsx (MiniExcel) and .csv (CsvHelper) — never loads whole files
/// into memory. Row numbers are 1-based data rows (header excluded).
/// </summary>
public static class TableFileReader
{
    public const int MaxRows = 100_000;
    public const int MaxCellChars = 256 * 1024;

    public static List<string> ReadColumns(string filePath)
    {
        foreach (var row in ReadRows(filePath))
            return row.Cells.Keys.ToList();
        // Empty file: still try to surface headers for CSV (CsvHelper needs a record; xlsx headers
        // come with the first row anyway). An empty column list is handled by callers.
        return [];
    }

    public static IEnumerable<TableRow> ReadRows(string filePath)
    {
        return Path.GetExtension(filePath).ToLowerInvariant() switch
        {
            ".xlsx" => ReadXlsx(filePath),
            ".csv" => ReadCsv(filePath),
            _ => throw new InvalidOperationException($"Unsupported file type '{Path.GetExtension(filePath)}'."),
        };
    }

    private static IEnumerable<TableRow> ReadXlsx(string filePath)
    {
        using var stream = File.OpenRead(filePath);
        var rowNumber = 0;
        foreach (var row in stream.Query(useHeaderRow: true))
        {
            rowNumber++;
            GuardRowCount(rowNumber);
            var dict = (IDictionary<string, object?>)row;
            var cells = new Dictionary<string, string?>(dict.Count, StringComparer.Ordinal);
            foreach (var (key, value) in dict)
                cells[key] = Normalize(value?.ToString());
            yield return new TableRow(rowNumber, cells);
        }
    }

    private static IEnumerable<TableRow> ReadCsv(string filePath)
    {
        using var reader = new StreamReader(filePath, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        using var csv = new CsvReader(reader, new CsvConfiguration(CultureInfo.InvariantCulture)
        {
            MissingFieldFound = null,
            BadDataFound = null,
            TrimOptions = TrimOptions.Trim,
        });

        if (!csv.Read() || !csv.ReadHeader()) yield break;
        var headers = csv.HeaderRecord ?? [];

        var rowNumber = 0;
        while (csv.Read())
        {
            rowNumber++;
            GuardRowCount(rowNumber);
            var cells = new Dictionary<string, string?>(headers.Length, StringComparer.Ordinal);
            for (var i = 0; i < headers.Length; i++)
                cells[headers[i]] = Normalize(csv.TryGetField<string>(i, out var value) ? value : null);
            yield return new TableRow(rowNumber, cells);
        }
    }

    private static string? Normalize(string? value)
    {
        var trimmed = value?.Trim();
        return string.IsNullOrEmpty(trimmed) ? null : trimmed;
    }

    private static void GuardRowCount(int rowNumber)
    {
        if (rowNumber > MaxRows)
            throw new InvalidOperationException($"The file has more than {MaxRows:N0} data rows.");
    }
}
