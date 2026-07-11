using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Structura.Web.Domain;
using Structura.Web.Infrastructure.Auth;
using Structura.Web.Infrastructure.Errors;
using Structura.Web.Infrastructure.Validation;
using Structura.Web.Persistence;

namespace Structura.Web.Features.Processing;

public sealed record CreateRunRequest(string Scope, List<Guid>? RecordIds);

public sealed class CreateRunRequestValidator : AbstractValidator<CreateRunRequest>
{
    public static readonly string[] Scopes = ["selected", "allPending", "failed", "reprocessRequested"];

    public CreateRunRequestValidator()
    {
        RuleFor(x => x.Scope).Must(s => Scopes.Contains(s))
            .WithMessage("Scope must be selected, allPending, failed, or reprocessRequested.");
        RuleFor(x => x.RecordIds).NotEmpty().When(x => x.Scope == "selected")
            .WithMessage("Select at least one record.");
        RuleFor(x => x.RecordIds).Must(ids => ids is null || ids.Count <= 10_000)
            .WithMessage("At most 10,000 records per run.");
    }
}

public static class RunEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/projects/{projectId:guid}/runs").RequireAuthorization();
        group.MapPost("/", CreateAsync).Validate<CreateRunRequest>();
        group.MapGet("/", ListAsync);
        group.MapGet("/{runId:guid}", GetAsync);
        group.MapPost("/{runId:guid}/cancel", CancelAsync);
        group.MapPost("/{runId:guid}/retry-failed", RetryFailedAsync);
    }

    private static async Task<IResult> CreateAsync(
        Guid projectId, CreateRunRequest request, ProjectAccessService access,
        AppDbContext db, ICurrentUser currentUser, CancellationToken ct)
    {
        var project = await access.EnsureCanManageAsync(projectId, ct);
        ProjectAccessService.EnsureNotArchived(project);
        EnsureProjectReady(project);

        var eligible = ResolveScope(db, projectId, request.Scope, request.RecordIds);
        return await CreateRunOverAsync(db, project, eligible, currentUser.Id, ct);
    }

    private static void EnsureProjectReady(Project project)
    {
        var config = AiConfigDocument.ParseOrNull(project.AiConfig);
        if (config?.ApiKeyProtected is null || string.IsNullOrWhiteSpace(config.Model))
            throw new ConflictException("configuration_incomplete",
                "Configure the AI provider (API key and model) before processing.");
        if (SchemaDocument.Parse(project.SchemaFields).Fields.Count == 0)
            throw new ConflictException("configuration_incomplete",
                "Define at least one schema field before processing.");
    }

    /// <summary>
    /// Eligibility rules: never a record that is mid-processing, attached to another
    /// running run, or already Approved (approved output must not be overwritten).
    /// </summary>
    private static IQueryable<Record> ResolveScope(
        AppDbContext db, Guid projectId, string scope, List<Guid>? recordIds)
    {
        var baseQuery = db.Records.Where(r =>
            r.ProjectId == projectId
            && r.ProcessingStatusValue != ProcessingStatus.Processing
            && r.ReviewStatusValue != ReviewStatus.Approved
            && !db.ProcessingRuns.Any(run => run.Id == r.ProcessingRunId && run.Status == RunStatus.Running));

        return scope switch
        {
            "selected" => baseQuery.Where(r =>
                recordIds!.Contains(r.Id)
                && (r.ProcessingStatusValue == ProcessingStatus.Pending
                    || r.ProcessingStatusValue == ProcessingStatus.Failed
                    || r.ReviewStatusValue == ReviewStatus.ReprocessRequested)),
            "allPending" => baseQuery.Where(r => r.ProcessingStatusValue == ProcessingStatus.Pending),
            "failed" => baseQuery.Where(r => r.ProcessingStatusValue == ProcessingStatus.Failed),
            "reprocessRequested" => baseQuery.Where(r => r.ReviewStatusValue == ReviewStatus.ReprocessRequested),
            _ => throw new AppException(400, ErrorCodes.ValidationFailed, "Unknown scope."),
        };
    }

    private static async Task<IResult> CreateRunOverAsync(
        AppDbContext db, Project project, IQueryable<Record> eligible, Guid createdById, CancellationToken ct)
    {
        var config = AiConfigDocument.ParseOrNull(project.AiConfig)!;
        var run = new ProcessingRun
        {
            ProjectId = project.Id,
            Status = RunStatus.Running,
            SchemaSnapshot = project.SchemaFields,
            PromptSnapshot = PromptConfigDocument.Parse(project.PromptConfig).ToJson(),
            Model = config.Model,
            CreatedById = createdById,
            StartedAt = DateTimeOffset.UtcNow,
        };

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        db.ProcessingRuns.Add(run);
        await db.SaveChangesAsync(ct);

        // Attach eligible records to this run and reset them to Pending in one statement.
        var total = await eligible.ExecuteUpdateAsync(s => s
            .SetProperty(r => r.ProcessingRunId, run.Id)
            .SetProperty(r => r.ProcessingStatusValue, ProcessingStatus.Pending)
            .SetProperty(r => r.ProcessingError, (string?)null)
            .SetProperty(r => r.Version, r => r.Version + 1), ct);

        if (total == 0)
            throw new AppException(400, ErrorCodes.ValidationFailed,
                "No eligible records for this scope (records that are approved, mid-processing, or already in a running run are excluded).");

        run.Total = total;
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);

        return Results.Created($"/api/projects/{project.Id}/runs/{run.Id}", ToDto(run));
    }

    private static async Task<object> ListAsync(
        Guid projectId, ProjectAccessService access, AppDbContext db, CancellationToken ct)
    {
        await access.EnsureCanViewAsync(projectId, ct);
        var items = await db.ProcessingRuns.AsNoTracking()
            .Where(r => r.ProjectId == projectId)
            .OrderByDescending(r => r.CreatedAt)
            .Take(50)
            .ToListAsync(ct);
        return new { items = items.Select(ToDto) };
    }

    private static async Task<object> GetAsync(
        Guid projectId, Guid runId, ProjectAccessService access, AppDbContext db, CancellationToken ct)
    {
        await access.EnsureCanViewAsync(projectId, ct);
        var run = await db.ProcessingRuns.AsNoTracking()
                      .FirstOrDefaultAsync(r => r.Id == runId && r.ProjectId == projectId, ct)
                  ?? throw new NotFoundException("Processing run");

        var failedRecords = await db.Records.AsNoTracking()
            .Where(r => r.ProcessingRunId == runId && r.ProcessingStatusValue == ProcessingStatus.Failed)
            .OrderBy(r => r.ExternalId)
            .Take(20)
            .Select(r => new { id = r.Id, externalId = r.ExternalId, error = r.ProcessingError })
            .ToListAsync(ct);

        return new
        {
            run = ToDto(run),
            failedRecords,
        };
    }

    private static async Task<IResult> CancelAsync(
        Guid projectId, Guid runId, ProjectAccessService access, AppDbContext db, CancellationToken ct)
    {
        await access.EnsureCanManageAsync(projectId, ct);
        var run = await db.ProcessingRuns.FirstOrDefaultAsync(
                      r => r.Id == runId && r.ProjectId == projectId, ct)
                  ?? throw new NotFoundException("Processing run");
        if (run.Status != RunStatus.Running)
            throw new ConflictException(ErrorCodes.InvalidState, $"Run is already {run.Status}.");
        run.CancelRequested = true;
        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }

    private static async Task<IResult> RetryFailedAsync(
        Guid projectId, Guid runId, ProjectAccessService access, AppDbContext db,
        ICurrentUser currentUser, CancellationToken ct)
    {
        var project = await access.EnsureCanManageAsync(projectId, ct);
        ProjectAccessService.EnsureNotArchived(project);
        EnsureProjectReady(project);

        var source = await db.ProcessingRuns.AsNoTracking()
                         .FirstOrDefaultAsync(r => r.Id == runId && r.ProjectId == projectId, ct)
                     ?? throw new NotFoundException("Processing run");
        if (source.Status is RunStatus.Running)
            throw new ConflictException(ErrorCodes.InvalidState, "Wait for the run to finish before retrying.");

        var failedOfRun = db.Records.Where(r =>
            r.ProjectId == projectId
            && r.ProcessingRunId == runId
            && r.ProcessingStatusValue == ProcessingStatus.Failed);
        return await CreateRunOverAsync(db, project, failedOfRun, currentUser.Id, ct);
    }

    private static object ToDto(ProcessingRun run) => new
    {
        id = run.Id,
        status = run.Status,
        model = run.Model,
        total = run.Total,
        succeeded = run.Succeeded,
        failed = run.Failed,
        inputTokens = run.InputTokens,
        outputTokens = run.OutputTokens,
        cancelRequested = run.CancelRequested,
        startedAt = run.StartedAt,
        finishedAt = run.FinishedAt,
        createdAt = run.CreatedAt,
    };
}
