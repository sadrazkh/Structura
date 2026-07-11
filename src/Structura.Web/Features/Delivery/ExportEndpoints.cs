using MiniExcelLibs;
using Structura.Web.Domain;
using Structura.Web.Infrastructure.Auth;
using Structura.Web.Infrastructure.Export;
using Structura.Web.Persistence;

namespace Structura.Web.Features.Delivery;

public static class ExportEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        app.MapGet("/api/projects/{projectId:guid}/export/excel", ExportAsync).RequireAuthorization();
    }

    private static async Task<IResult> ExportAsync(
        Guid projectId, HttpContext http, ProjectAccessService access, AppDbContext db,
        string? from, string? to, string? format, CancellationToken ct)
    {
        var project = await access.EnsureCanManageAsync(projectId, ct);
        var schema = SchemaDocument.Parse(project.SchemaFields);

        DateTimeOffset? fromDate = DateTimeOffset.TryParse(from, out var f) ? f : null;
        DateTimeOffset? toDate = DateTimeOffset.TryParse(to, out var t) ? t : null;
        var isCsv = string.Equals(format, "csv", StringComparison.OrdinalIgnoreCase);

        var stamp = DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss");
        var safeName = new string(project.Name.Where(c => char.IsLetterOrDigit(c) || c is '-' or '_').ToArray());
        if (string.IsNullOrEmpty(safeName)) safeName = "export";
        var fileName = $"{safeName}-approved-{stamp}.{(isCsv ? "csv" : "xlsx")}";

        // Stream rows into a temp file (flat memory), then stream the file to the client.
        var tempPath = Path.Combine(Path.GetTempPath(), $"structura-export-{Guid.CreateVersion7():N}.{(isCsv ? "csv" : "xlsx")}");
        try
        {
            var rows = ExcelExporter.BuildRows(db, projectId, schema, fromDate, toDate);
            await MiniExcel.SaveAsAsync(tempPath, rows, printHeader: true,
                excelType: isCsv ? ExcelType.CSV : ExcelType.XLSX,
                overwriteFile: true, cancellationToken: ct);

            var contentType = isCsv
                ? "text/csv"
                : "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";
            http.Response.Headers.ContentDisposition = $"attachment; filename=\"{fileName}\"";
            await using var file = File.OpenRead(tempPath);
            http.Response.ContentType = contentType;
            http.Response.ContentLength = file.Length;
            await file.CopyToAsync(http.Response.Body, ct);
            return Results.Empty;
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                try { File.Delete(tempPath); } catch (IOException) { /* best effort */ }
            }
        }
    }
}
