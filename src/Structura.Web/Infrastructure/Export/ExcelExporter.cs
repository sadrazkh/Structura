using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Structura.Web.Domain;
using Structura.Web.Persistence;

namespace Structura.Web.Infrastructure.Export;

/// <summary>
/// Builds export rows for approved records, streaming from the database in keyset pages
/// so memory stays flat regardless of record count (docs/05).
/// </summary>
public static class ExcelExporter
{
    private const int PageSize = 1000;

    /// <summary>
    /// Field-column headers, guaranteed unique across the sheet (label collisions and reserved
    /// column names are disambiguated with the field key) so the writer never drops a column.
    /// </summary>
    public static List<(FieldSpec Field, string Header)> BuildFieldHeaders(SchemaDocument schema)
    {
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Record ID", "Review Status", "Reviewer", "Review Date",
        };
        var result = new List<(FieldSpec, string)>();
        foreach (var field in schema.Fields.OrderBy(f => f.DisplayOrder))
        {
            var header = Sanitize(HeaderLabel(field))!;
            if (!used.Add(header))
            {
                header = Sanitize($"{HeaderLabel(field)} ({field.Key})")!;
                var n = 2;
                while (!used.Add(header)) header = Sanitize($"{HeaderLabel(field)} ({field.Key} {n++})")!;
            }
            result.Add((field, header));
        }
        return result;
    }

    /// <summary>Column headers in order: Record ID, schema fields (by label), review metadata.</summary>
    public static List<string> BuildHeaders(SchemaDocument schema) =>
    [
        "Record ID",
        .. BuildFieldHeaders(schema).Select(x => x.Header),
        "Review Status",
        "Reviewer",
        "Review Date",
    ];

    /// <summary>
    /// Lazily yields one dictionary per approved record. Enumerated synchronously by the
    /// Excel/CSV writer while the request scope's DbContext is alive.
    /// </summary>
    public static IEnumerable<Dictionary<string, object?>> BuildRows(
        AppDbContext db, Guid projectId, SchemaDocument schema,
        DateTimeOffset? from, DateTimeOffset? to)
    {
        var fieldHeaders = BuildFieldHeaders(schema);
        var orderedFields = fieldHeaders.Select(x => x.Field).ToList();
        var fieldHeaderByKey = fieldHeaders.ToDictionary(x => x.Field.Key, x => x.Header);

        Guid? afterId = null;
        while (true)
        {
            var query = db.Records.AsNoTracking()
                .Where(r => r.ProjectId == projectId && r.ReviewStatusValue == ReviewStatus.Approved);
            if (from is not null) query = query.Where(r => r.ReviewedAt >= from);
            if (to is not null) query = query.Where(r => r.ReviewedAt <= to);
            if (afterId is not null) query = query.Where(r => r.Id.CompareTo(afterId.Value) > 0);

            var page = query
                .OrderBy(r => r.Id)
                .Take(PageSize)
                .Select(r => new RowData(
                    r.ExternalId, r.FinalOutput, r.ReviewStatusValue,
                    r.ReviewedBy != null ? r.ReviewedBy.FullName : null, r.ReviewedAt, r.Id))
                .ToList();

            if (page.Count == 0) yield break;

            foreach (var row in page)
                yield return ToRow(row, orderedFields, fieldHeaderByKey);

            if (page.Count < PageSize) yield break;
            afterId = page[^1].Id;
        }
    }

    private sealed record RowData(
        string ExternalId, string? FinalOutput, string ReviewStatus,
        string? Reviewer, DateTimeOffset? ReviewedAt, Guid Id);

    private static Dictionary<string, object?> ToRow(
        RowData row, List<FieldSpec> fields, Dictionary<string, string> fieldHeaderByKey)
    {
        var dict = new Dictionary<string, object?>
        {
            ["Record ID"] = Sanitize(row.ExternalId),
        };

        var output = ParseOutput(row.FinalOutput);
        foreach (var field in fields)
            dict[fieldHeaderByKey[field.Key]] = FormatValue(field, output);

        dict["Review Status"] = row.ReviewStatus;
        dict["Reviewer"] = Sanitize(row.Reviewer);
        dict["Review Date"] = row.ReviewedAt?.ToString("yyyy-MM-dd HH:mm:ss");
        return dict;
    }

    private static Dictionary<string, JsonElement> ParseOutput(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone());
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static object? FormatValue(FieldSpec field, Dictionary<string, JsonElement> output)
    {
        if (!output.TryGetValue(field.Key, out var value) || value.ValueKind is JsonValueKind.Null)
            return null;

        return field.Type switch
        {
            FieldTypes.Boolean => value.ValueKind == JsonValueKind.True ? "TRUE"
                : value.ValueKind == JsonValueKind.False ? "FALSE" : Sanitize(value.ToString()),
            FieldTypes.Integer or FieldTypes.Decimal =>
                value.ValueKind == JsonValueKind.Number ? value.GetRawText() : Sanitize(value.ToString()),
            FieldTypes.MultiSelect when value.ValueKind == JsonValueKind.Array =>
                Sanitize(string.Join("; ", value.EnumerateArray().Select(e => e.ToString()))),
            _ => Sanitize(value.ValueKind == JsonValueKind.String ? value.GetString() : value.ToString()),
        };
    }

    private static string HeaderLabel(FieldSpec field) =>
        string.IsNullOrWhiteSpace(field.Label) ? field.Key : field.Label;

    /// <summary>Neutralizes Excel/CSV formula injection by prefixing risky leading characters.</summary>
    public static string? Sanitize(string? value)
    {
        if (string.IsNullOrEmpty(value)) return value;
        return value[0] is '=' or '+' or '-' or '@' or '\t' or '\r'
            ? "'" + value
            : value;
    }
}
