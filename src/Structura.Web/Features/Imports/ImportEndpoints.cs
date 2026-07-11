using System.Text.Json;
using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Structura.Web.Domain;
using Structura.Web.Infrastructure.Auth;
using Structura.Web.Infrastructure.Errors;
using Structura.Web.Infrastructure.Import;
using Structura.Web.Infrastructure.Validation;
using Structura.Web.Persistence;

namespace Structura.Web.Features.Imports;

public sealed record StartImportRequest(string? IdColumn, string TextColumn, bool GenerateIds);
public sealed record ManualRecordsRequest(List<ManualRecord> Records);
public sealed record ManualRecord(string? ExternalId, string Text);

public sealed class StartImportRequestValidator : AbstractValidator<StartImportRequest>
{
    public StartImportRequestValidator()
    {
        RuleFor(x => x.TextColumn).NotEmpty();
        RuleFor(x => x).Must(x => x.GenerateIds || !string.IsNullOrWhiteSpace(x.IdColumn))
            .WithMessage("Choose an ID column or enable generated IDs.")
            .OverridePropertyName("idColumn");
    }
}

public sealed class ManualRecordsRequestValidator : AbstractValidator<ManualRecordsRequest>
{
    public ManualRecordsRequestValidator()
    {
        RuleFor(x => x.Records).NotEmpty().Must(r => r.Count <= 1000)
            .WithMessage("At most 1,000 records per request.");
        RuleForEach(x => x.Records).ChildRules(record =>
        {
            record.RuleFor(r => r.Text).NotEmpty().MaximumLength(TableFileReader.MaxCellChars);
            record.RuleFor(r => r.ExternalId).MaximumLength(256);
        });
    }
}

public static class ImportEndpoints
{
    private const long MaxUploadBytes = 50 * 1024 * 1024;
    private static readonly string[] AllowedExtensions = [".xlsx", ".csv"];

    public static void Map(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/projects/{projectId:guid}/imports").RequireAuthorization();
        group.MapPost("/upload", UploadAsync).DisableAntiforgery();
        group.MapGet("/", ListAsync);
        group.MapGet("/{runId:guid}", GetAsync);
        group.MapPost("/{runId:guid}/start", StartAsync).Validate<StartImportRequest>();
        group.MapPost("/{runId:guid}/cancel", CancelAsync);

        app.MapPost("/api/projects/{projectId:guid}/records/manual", ManualAsync)
            .Validate<ManualRecordsRequest>()
            .RequireAuthorization();
    }

    private static async Task<IResult> UploadAsync(
        Guid projectId, IFormFile file, ProjectAccessService access, AppDbContext db,
        ICurrentUser currentUser, IConfiguration config, CancellationToken ct)
    {
        var project = await access.EnsureCanManageAsync(projectId, ct);
        ProjectAccessService.EnsureNotArchived(project);

        var extension = Path.GetExtension(file.FileName).ToLowerInvariant();
        if (!AllowedExtensions.Contains(extension))
            throw new AppException(400, "import_invalid_file", "Only .xlsx and .csv files are supported.");
        if (file.Length == 0 || file.Length > MaxUploadBytes)
            throw new AppException(400, "import_invalid_file", "File must be between 1 byte and 50 MB.");

        var run = new ImportRun
        {
            ProjectId = projectId,
            Source = extension == ".xlsx" ? ImportSource.Excel : ImportSource.Csv,
            FileName = Path.GetFileName(file.FileName),
            CreatedById = currentUser.Id,
        };

        var uploadsDir = Path.Combine(DataDir(config), "uploads");
        Directory.CreateDirectory(uploadsDir);
        var storedPath = Path.Combine(uploadsDir, $"{run.Id}{extension}");
        await using (var target = File.Create(storedPath))
        {
            await file.CopyToAsync(target, ct);
        }
        run.FilePath = storedPath;

        ValidateMagicBytes(storedPath, extension);

        List<string> columns;
        var preview = new List<IReadOnlyDictionary<string, string?>>();
        try
        {
            columns = TableFileReader.ReadColumns(storedPath);
            foreach (var row in TableFileReader.ReadRows(storedPath).Take(20))
                preview.Add(row.Cells);
        }
        catch (Exception e) when (e is not AppException)
        {
            File.Delete(storedPath);
            throw new AppException(400, "import_invalid_file", $"The file could not be parsed: {e.Message}");
        }
        if (columns.Count == 0)
        {
            File.Delete(storedPath);
            throw new AppException(400, "import_invalid_file", "The file has no header row or no columns.");
        }

        db.ImportRuns.Add(run);
        await db.SaveChangesAsync(ct);
        return Results.Ok(new { id = run.Id, fileName = run.FileName, columns, previewRows = preview });
    }

    private static void ValidateMagicBytes(string path, string extension)
    {
        using var stream = File.OpenRead(path);
        var header = new byte[8192];
        var read = stream.Read(header, 0, header.Length);
        if (extension == ".xlsx")
        {
            // XLSX is a ZIP container: PK\x03\x04
            if (read < 4 || header[0] != 0x50 || header[1] != 0x4B || header[2] != 0x03 || header[3] != 0x04)
                throw new AppException(400, "import_invalid_file", "The file is not a valid .xlsx workbook.");
        }
        else
        {
            for (var i = 0; i < read; i++)
                if (header[i] == 0)
                    throw new AppException(400, "import_invalid_file", "The file is not a valid text CSV.");
        }
    }

    private static async Task<object> ListAsync(
        Guid projectId, ProjectAccessService access, AppDbContext db, CancellationToken ct)
    {
        await access.EnsureCanViewAsync(projectId, ct);
        var items = await db.ImportRuns.AsNoTracking()
            .Where(r => r.ProjectId == projectId)
            .OrderByDescending(r => r.CreatedAt)
            .Take(50)
            .Select(r => new
            {
                id = r.Id, source = r.Source, fileName = r.FileName, status = r.Status,
                totalRows = r.TotalRows, imported = r.Imported,
                skippedDuplicates = r.SkippedDuplicates, failed = r.Failed,
                createdAt = r.CreatedAt, finishedAt = r.FinishedAt,
            })
            .ToListAsync(ct);
        return new { items };
    }

    private static async Task<object> GetAsync(
        Guid projectId, Guid runId, ProjectAccessService access, AppDbContext db, CancellationToken ct)
    {
        await access.EnsureCanViewAsync(projectId, ct);
        var run = await db.ImportRuns.AsNoTracking()
                      .FirstOrDefaultAsync(r => r.Id == runId && r.ProjectId == projectId, ct)
                  ?? throw new NotFoundException("Import run");
        return new
        {
            id = run.Id, source = run.Source, fileName = run.FileName, status = run.Status,
            totalRows = run.TotalRows, imported = run.Imported,
            skippedDuplicates = run.SkippedDuplicates, failed = run.Failed,
            lastRowProcessed = run.LastRowProcessed,
            errors = JsonSerializer.Deserialize<List<ImportRowError>>(run.Errors, SchemaDocument.JsonOptions),
            createdAt = run.CreatedAt, finishedAt = run.FinishedAt,
        };
    }

    private static async Task<IResult> StartAsync(
        Guid projectId, Guid runId, StartImportRequest request,
        ProjectAccessService access, AppDbContext db, CancellationToken ct)
    {
        var project = await access.EnsureCanManageAsync(projectId, ct);
        ProjectAccessService.EnsureNotArchived(project);

        var run = await db.ImportRuns.FirstOrDefaultAsync(r => r.Id == runId && r.ProjectId == projectId, ct)
                  ?? throw new NotFoundException("Import run");
        if (run.Status != ImportStatus.AwaitingMapping)
            throw new ConflictException(ErrorCodes.InvalidState, $"Import run is {run.Status}, not awaiting mapping.");

        run.Mapping = JsonSerializer.Serialize(
            new ImportMapping(request.GenerateIds ? null : request.IdColumn, request.TextColumn, request.GenerateIds),
            SchemaDocument.JsonOptions);
        run.Status = ImportStatus.Running;
        await db.SaveChangesAsync(ct);
        return Results.Accepted($"/api/projects/{projectId}/imports/{runId}");
    }

    private static async Task<IResult> CancelAsync(
        Guid projectId, Guid runId, ProjectAccessService access, AppDbContext db, CancellationToken ct)
    {
        await access.EnsureCanManageAsync(projectId, ct);
        var run = await db.ImportRuns.FirstOrDefaultAsync(r => r.Id == runId && r.ProjectId == projectId, ct)
                  ?? throw new NotFoundException("Import run");
        if (run.Status is not (ImportStatus.Running or ImportStatus.AwaitingMapping))
            throw new ConflictException(ErrorCodes.InvalidState, $"Import run is already {run.Status}.");
        run.Status = ImportStatus.Cancelled;
        run.FinishedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }

    private static async Task<object> ManualAsync(
        Guid projectId, ManualRecordsRequest request, ProjectAccessService access,
        AppDbContext db, ICurrentUser currentUser, CancellationToken ct)
    {
        var project = await access.EnsureCanManageAsync(projectId, ct);
        ProjectAccessService.EnsureNotArchived(project);

        var run = new ImportRun
        {
            ProjectId = projectId,
            Source = ImportSource.Manual,
            Status = ImportStatus.Running,
            CreatedById = currentUser.Id,
        };
        db.ImportRuns.Add(run);

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var candidates = new List<Record>();
        var rowNumber = 0;
        foreach (var item in request.Records)
        {
            rowNumber++;
            var externalId = string.IsNullOrWhiteSpace(item.ExternalId)
                ? $"REC-{Guid.CreateVersion7().ToString("N")[..10].ToUpperInvariant()}"
                : item.ExternalId.Trim();
            if (!seen.Add(externalId))
            {
                run.SkippedDuplicates++;
                continue;
            }
            candidates.Add(new Record
            {
                ProjectId = projectId, ExternalId = externalId, Text = item.Text.Trim(), ImportRunId = run.Id,
            });
        }

        var candidateIds = candidates.Select(c => c.ExternalId).ToList();
        var existing = (await db.Records
                .Where(r => r.ProjectId == projectId && candidateIds.Contains(r.ExternalId))
                .Select(r => r.ExternalId).ToListAsync(ct))
            .ToHashSet(StringComparer.Ordinal);
        var fresh = candidates.Where(c => !existing.Contains(c.ExternalId)).ToList();
        run.SkippedDuplicates += candidates.Count - fresh.Count;
        run.Imported = fresh.Count;
        run.TotalRows = request.Records.Count;
        run.Status = ImportStatus.Completed;
        run.FinishedAt = DateTimeOffset.UtcNow;
        db.Records.AddRange(fresh);
        await db.SaveChangesAsync(ct);

        return new { imported = run.Imported, skippedDuplicates = run.SkippedDuplicates, runId = run.Id };
    }

    internal static string DataDir(IConfiguration config) =>
        config["DATA_DIR"] ?? Path.Combine(AppContext.BaseDirectory, "data");
}
