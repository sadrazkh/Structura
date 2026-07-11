using System.Text.Json;
using System.Text.Json.Nodes;
using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Structura.Web.Domain;
using Structura.Web.Infrastructure.Ai;
using Structura.Web.Infrastructure.Auth;
using Structura.Web.Infrastructure.Errors;
using Structura.Web.Infrastructure.Validation;
using Structura.Web.Persistence;

namespace Structura.Web.Features.Review;

public sealed record SaveDraftRequest(JsonObject FinalOutput, int Version);
public sealed record ApproveRequest(JsonObject FinalOutput, int Version);
public sealed record DecisionRequest(string Note, int Version);
public sealed record BulkApproveRequest(List<Guid> RecordIds);

public sealed class DecisionRequestValidator : AbstractValidator<DecisionRequest>
{
    public DecisionRequestValidator()
    {
        RuleFor(x => x.Note).NotEmpty().WithMessage("A note explaining the decision is required.")
            .MaximumLength(2000);
    }
}

public sealed class BulkApproveRequestValidator : AbstractValidator<BulkApproveRequest>
{
    public BulkApproveRequestValidator()
    {
        RuleFor(x => x.RecordIds).NotEmpty().Must(ids => ids.Count <= 200)
            .WithMessage("Select between 1 and 200 records.");
    }
}

public static class ReviewEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/review").RequireAuthorization();
        group.MapGet("/tasks", TasksAsync);
        group.MapGet("/progress", ProgressAsync);
        group.MapGet("/{projectId:guid}/records", QueueAsync);
        group.MapGet("/{projectId:guid}/records/{recordId:guid}", OpenAsync);
        group.MapGet("/{projectId:guid}/next", NextAsync);
        group.MapPut("/{projectId:guid}/records/{recordId:guid}", SaveDraftAsync);
        group.MapPost("/{projectId:guid}/records/{recordId:guid}/approve", ApproveAsync);
        group.MapPost("/{projectId:guid}/records/{recordId:guid}/reject", RejectAsync).Validate<DecisionRequest>();
        group.MapPost("/{projectId:guid}/records/{recordId:guid}/reprocess", ReprocessAsync).Validate<DecisionRequest>();
        group.MapPost("/{projectId:guid}/bulk-approve", BulkApproveAsync).Validate<BulkApproveRequest>();
    }

    // ---------- queue & navigation ----------

    private static async Task<object> TasksAsync(AppDbContext db, ICurrentUser user, CancellationToken ct)
    {
        var items = await db.Records.AsNoTracking()
            .Where(r => r.AssignedReviewerId == user.Id
                        && (r.ReviewStatusValue == ReviewStatus.Assigned
                            || r.ReviewStatusValue == ReviewStatus.InReview))
            .GroupBy(r => new { r.ProjectId, r.Project.Name })
            .Select(g => new
            {
                projectId = g.Key.ProjectId,
                projectName = g.Key.Name,
                pending = g.Count(),
                inReview = g.Count(r => r.ReviewStatusValue == ReviewStatus.InReview),
            })
            .OrderBy(g => g.projectName)
            .ToListAsync(ct);
        return new { items };
    }

    private static async Task<object> QueueAsync(
        Guid projectId, AppDbContext db, ICurrentUser user, ProjectAccessService access,
        string? status, CancellationToken ct)
    {
        await access.EnsureCanViewAsync(projectId, ct);
        var query = db.Records.AsNoTracking()
            .Where(r => r.ProjectId == projectId && r.AssignedReviewerId == user.Id);
        query = string.IsNullOrEmpty(status)
            ? query.Where(r => r.ReviewStatusValue == ReviewStatus.Assigned
                               || r.ReviewStatusValue == ReviewStatus.InReview)
            : query.Where(r => r.ReviewStatusValue == status);

        var items = await query
            .OrderBy(r => r.AssignedAt).ThenBy(r => r.Id)
            .Take(500)
            .Select(r => new
            {
                id = r.Id,
                externalId = r.ExternalId,
                textExcerpt = r.Text.Length > 140 ? r.Text.Substring(0, 140) + "…" : r.Text,
                reviewStatus = r.ReviewStatusValue,
                hasDraft = r.FinalOutput != null,
                assignedAt = r.AssignedAt,
            })
            .ToListAsync(ct);
        return new { items };
    }

    private static async Task<object> OpenAsync(
        Guid projectId, Guid recordId, AppDbContext db, ICurrentUser user, CancellationToken ct)
    {
        var record = await LoadOwnRecordAsync(db, projectId, recordId, user, ct);

        // Opening a fresh assignment moves it to InReview (Assigned → InReview).
        if (record.ReviewStatusValue == ReviewStatus.Assigned)
        {
            record.ReviewStatusValue = ReviewStatus.InReview;
            record.Version++;
            await db.SaveChangesAsync(ct);
        }

        var (schema, aiOutput) = await LoadReviewContextAsync(db, record, ct);
        return new
        {
            id = record.Id,
            externalId = record.ExternalId,
            text = record.Text,
            reviewStatus = record.ReviewStatusValue,
            version = record.Version,
            reviewNote = record.ReviewNote,
            fields = schema.Fields.OrderBy(f => f.DisplayOrder),
            aiOutput = aiOutput?.ToJsonString(),
            // The reviewer edits a working copy; it starts from the AI output.
            workingOutput = record.FinalOutput ?? aiOutput?.ToJsonString(),
        };
    }

    private static async Task<object> NextAsync(
        Guid projectId, AppDbContext db, ICurrentUser user, ProjectAccessService access,
        Guid? after, CancellationToken ct)
    {
        await access.EnsureCanViewAsync(projectId, ct);
        var queue = db.Records.AsNoTracking()
            .Where(r => r.ProjectId == projectId
                        && r.AssignedReviewerId == user.Id
                        && (r.ReviewStatusValue == ReviewStatus.Assigned
                            || r.ReviewStatusValue == ReviewStatus.InReview))
            .OrderBy(r => r.AssignedAt).ThenBy(r => r.Id);

        var remaining = await queue.CountAsync(ct);
        var next = await queue.Where(r => after == null || r.Id != after)
            .Select(r => (Guid?)r.Id).FirstOrDefaultAsync(ct);
        return new { recordId = next, remaining };
    }

    // ---------- writes ----------

    private static async Task<object> SaveDraftAsync(
        Guid projectId, Guid recordId, SaveDraftRequest request,
        AppDbContext db, ICurrentUser user, CancellationToken ct)
    {
        var record = await LoadOwnRecordAsync(db, projectId, recordId, user, ct);
        EnsureEditable(record);
        EnsureVersion(record, request.Version);

        record.FinalOutput = request.FinalOutput.ToJsonString();
        if (record.ReviewStatusValue == ReviewStatus.Assigned)
            record.ReviewStatusValue = ReviewStatus.InReview;
        record.Version++;
        await db.SaveChangesAsync(ct);
        return new { version = record.Version };
    }

    private static async Task<object> ApproveAsync(
        Guid projectId, Guid recordId, ApproveRequest request,
        AppDbContext db, ICurrentUser user, CancellationToken ct)
    {
        var record = await LoadOwnRecordAsync(db, projectId, recordId, user, ct);
        EnsureEditable(record);
        EnsureVersion(record, request.Version);

        var (schema, _) = await LoadReviewContextAsync(db, record, ct);
        var outcome = OutputValidator.Validate(schema, request.FinalOutput);
        if (!outcome.IsValid)
            throw new AppException(422, ErrorCodes.ValidationFailed,
                string.Join(" ", outcome.Errors));

        record.FinalOutput = outcome.NormalizedOutput.ToJsonString();
        record.ReviewStatusValue = ReviewStatus.Approved;
        record.ReviewedById = user.Id;
        record.ReviewedAt = DateTimeOffset.UtcNow;
        record.DeliveryStatusValue = DeliveryStatus.Pending;
        record.Version++;
        await db.SaveChangesAsync(ct);

        return await DecisionResponseAsync(db, projectId, record, user, ct);
    }

    private static async Task<object> RejectAsync(
        Guid projectId, Guid recordId, DecisionRequest request,
        AppDbContext db, ICurrentUser user, CancellationToken ct)
    {
        var record = await LoadOwnRecordAsync(db, projectId, recordId, user, ct);
        EnsureEditable(record);
        EnsureVersion(record, request.Version);

        record.ReviewStatusValue = ReviewStatus.Rejected;
        record.ReviewNote = request.Note.Trim();
        record.ReviewedById = user.Id;
        record.ReviewedAt = DateTimeOffset.UtcNow;
        record.Version++;
        await db.SaveChangesAsync(ct);

        return await DecisionResponseAsync(db, projectId, record, user, ct);
    }

    private static async Task<object> ReprocessAsync(
        Guid projectId, Guid recordId, DecisionRequest request,
        AppDbContext db, ICurrentUser user, CancellationToken ct)
    {
        var record = await LoadOwnRecordAsync(db, projectId, recordId, user, ct);
        EnsureEditable(record);
        EnsureVersion(record, request.Version);

        // The admin's "reprocessRequested" run scope picks these up; on success the record
        // comes back to this reviewer (ProcessingWorker, docs/02 F7).
        record.ReviewStatusValue = ReviewStatus.ReprocessRequested;
        record.ReviewNote = request.Note.Trim();
        record.ReviewedById = user.Id;
        record.ReviewedAt = DateTimeOffset.UtcNow;
        record.Version++;
        await db.SaveChangesAsync(ct);

        return await DecisionResponseAsync(db, projectId, record, user, ct);
    }

    private static async Task<object> BulkApproveAsync(
        Guid projectId, BulkApproveRequest request,
        AppDbContext db, ICurrentUser user, CancellationToken ct)
    {
        var records = await db.Records
            .Where(r => r.ProjectId == projectId && request.RecordIds.Contains(r.Id))
            .ToListAsync(ct);
        var byId = records.ToDictionary(r => r.Id);

        var results = new List<object>();
        var approved = 0;
        foreach (var recordId in request.RecordIds)
        {
            if (!byId.TryGetValue(recordId, out var record) || record.AssignedReviewerId != user.Id)
            {
                results.Add(new { recordId, ok = false, reason = "Record is not assigned to you." });
                continue;
            }
            if (record.ReviewStatusValue is not (ReviewStatus.Assigned or ReviewStatus.InReview))
            {
                results.Add(new { recordId, ok = false, reason = $"Record is {record.ReviewStatusValue}." });
                continue;
            }

            // Server-side re-validation of the current working copy (AI output when unedited).
            var (schema, aiOutput) = await LoadReviewContextAsync(db, record, ct);
            var working = record.FinalOutput is not null
                ? JsonNode.Parse(record.FinalOutput) as JsonObject
                : aiOutput;
            if (working is null)
            {
                results.Add(new { recordId, ok = false, reason = "Record has no output to approve." });
                continue;
            }
            var outcome = OutputValidator.Validate(schema, working);
            if (!outcome.IsValid)
            {
                results.Add(new { recordId, ok = false, reason = string.Join(" ", outcome.Errors) });
                continue;
            }

            record.FinalOutput = outcome.NormalizedOutput.ToJsonString();
            record.ReviewStatusValue = ReviewStatus.Approved;
            record.ReviewedById = user.Id;
            record.ReviewedAt = DateTimeOffset.UtcNow;
            record.DeliveryStatusValue = DeliveryStatus.Pending;
            record.Version++;
            approved++;
            results.Add(new { recordId, ok = true, reason = (string?)null });
        }
        await db.SaveChangesAsync(ct);
        return new { approved, skipped = results.Count - approved, results };
    }

    private static async Task<object> ProgressAsync(AppDbContext db, ICurrentUser user, CancellationToken ct)
    {
        var todayStart = DateTimeOffset.UtcNow.Date;
        var stats = await db.Records.AsNoTracking()
            .Where(r => r.AssignedReviewerId == user.Id || r.ReviewedById == user.Id)
            .GroupBy(_ => 1)
            .Select(g => new
            {
                pending = g.Count(r => r.AssignedReviewerId == user.Id
                                       && (r.ReviewStatusValue == ReviewStatus.Assigned
                                           || r.ReviewStatusValue == ReviewStatus.InReview)),
                approved = g.Count(r => r.ReviewedById == user.Id && r.ReviewStatusValue == ReviewStatus.Approved),
                rejected = g.Count(r => r.ReviewedById == user.Id && r.ReviewStatusValue == ReviewStatus.Rejected),
                reprocessRequested = g.Count(r => r.ReviewedById == user.Id
                                                  && r.ReviewStatusValue == ReviewStatus.ReprocessRequested),
                decidedToday = g.Count(r => r.ReviewedById == user.Id && r.ReviewedAt >= todayStart),
            })
            .FirstOrDefaultAsync(ct);
        return stats ?? new { pending = 0, approved = 0, rejected = 0, reprocessRequested = 0, decidedToday = 0 };
    }

    // ---------- shared helpers ----------

    private static async Task<Record> LoadOwnRecordAsync(
        AppDbContext db, Guid projectId, Guid recordId, ICurrentUser user, CancellationToken ct)
    {
        var record = await db.Records.FirstOrDefaultAsync(
                         r => r.Id == recordId && r.ProjectId == projectId, ct)
                     ?? throw new NotFoundException("Record");
        // Reviewer isolation is absolute: only the assignee may open or act on a record.
        if (record.AssignedReviewerId != user.Id) throw new ForbiddenException();
        return record;
    }

    private static void EnsureEditable(Record record)
    {
        if (record.ReviewStatusValue is not (ReviewStatus.Assigned or ReviewStatus.InReview))
            throw new ConflictException(ErrorCodes.InvalidState,
                $"Record is {record.ReviewStatusValue} and can no longer be edited.");
    }

    private static void EnsureVersion(Record record, int version)
    {
        if (record.Version != version)
            throw new ConflictException(ErrorCodes.VersionConflict,
                "The record changed since you loaded it. Reload to continue.");
    }

    /// <summary>
    /// The review form renders against the schema snapshot of the run that produced the
    /// AI output — never the (possibly newer) live project schema.
    /// </summary>
    private static async Task<(SchemaDocument Schema, JsonObject? AiOutput)> LoadReviewContextAsync(
        AppDbContext db, Record record, CancellationToken ct)
    {
        if (record.LatestResultId is null)
        {
            var projectSchema = await db.Projects.AsNoTracking()
                .Where(p => p.Id == record.ProjectId).Select(p => p.SchemaFields).FirstAsync(ct);
            return (SchemaDocument.Parse(projectSchema), null);
        }

        var context = await db.ExtractionResults.AsNoTracking()
            .Where(e => e.Id == record.LatestResultId)
            .Join(db.ProcessingRuns.AsNoTracking(), e => e.RunId, r => r.Id,
                (e, r) => new { e.Output, r.SchemaSnapshot })
            .FirstAsync(ct);

        var aiOutput = context.Output is null ? null : JsonNode.Parse(context.Output) as JsonObject;
        return (SchemaDocument.Parse(context.SchemaSnapshot), aiOutput);
    }

    private static async Task<object> DecisionResponseAsync(
        AppDbContext db, Guid projectId, Record decided, ICurrentUser user, CancellationToken ct)
    {
        var queue = db.Records.AsNoTracking()
            .Where(r => r.ProjectId == projectId
                        && r.AssignedReviewerId == user.Id
                        && r.Id != decided.Id
                        && (r.ReviewStatusValue == ReviewStatus.Assigned
                            || r.ReviewStatusValue == ReviewStatus.InReview))
            .OrderBy(r => r.AssignedAt).ThenBy(r => r.Id);
        var nextId = await queue.Select(r => (Guid?)r.Id).FirstOrDefaultAsync(ct);
        var remaining = await queue.CountAsync(ct);
        return new { version = decided.Version, nextRecordId = nextId, remaining };
    }
}
