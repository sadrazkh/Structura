using System.Text;
using Microsoft.EntityFrameworkCore;
using Structura.Web.Domain;
using Structura.Web.Infrastructure.Auth;
using Structura.Web.Infrastructure.Errors;
using Structura.Web.Persistence;

namespace Structura.Web.Features.Records;

public sealed record DeleteRecordsRequest(List<Guid> RecordIds);

public static class RecordEndpoints
{
    private const int PageSize = 50;

    public static void Map(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/projects/{projectId:guid}/records").RequireAuthorization();
        group.MapGet("/", ListAsync);
        group.MapGet("/{recordId:guid}", GetAsync);
        group.MapPost("/delete", DeleteAsync);
        group.MapGet("/counts", CountsAsync);
    }

    private static async Task<object> ListAsync(
        Guid projectId, ProjectAccessService access, AppDbContext db, ICurrentUser currentUser,
        string? q, string? processingStatus, string? reviewStatus, string? deliveryStatus,
        Guid? reviewerId, string? cursor, CancellationToken ct)
    {
        await access.EnsureCanViewAsync(projectId, ct);

        var query = db.Records.AsNoTracking().Where(r => r.ProjectId == projectId);

        // Reviewers only ever see their own assigned records (enforced in the query).
        if (currentUser.Role == UserRole.Reviewer)
            query = query.Where(r => r.AssignedReviewerId == currentUser.Id);

        if (!string.IsNullOrWhiteSpace(q))
        {
            var pattern = $"%{q.Trim()}%";
            query = query.Where(r =>
                EF.Functions.ILike(r.ExternalId, pattern) || EF.Functions.ILike(r.Text, pattern));
        }
        if (!string.IsNullOrWhiteSpace(processingStatus))
            query = query.Where(r => r.ProcessingStatusValue == processingStatus);
        if (!string.IsNullOrWhiteSpace(reviewStatus))
            query = query.Where(r => r.ReviewStatusValue == reviewStatus);
        if (!string.IsNullOrWhiteSpace(deliveryStatus))
            query = query.Where(r => r.DeliveryStatusValue == deliveryStatus);
        if (reviewerId is not null)
            query = query.Where(r => r.AssignedReviewerId == reviewerId);

        // Keyset pagination on (updated_at DESC, id DESC).
        if (TryDecodeCursor(cursor, out var afterUpdatedAt, out var afterId))
            query = query.Where(r =>
                r.UpdatedAt < afterUpdatedAt || (r.UpdatedAt == afterUpdatedAt && r.Id < afterId));

        var items = await query
            .OrderByDescending(r => r.UpdatedAt).ThenByDescending(r => r.Id)
            .Take(PageSize + 1)
            .Select(r => new
            {
                id = r.Id,
                externalId = r.ExternalId,
                textExcerpt = r.Text.Length > 160 ? r.Text.Substring(0, 160) + "…" : r.Text,
                processingStatus = r.ProcessingStatusValue,
                reviewStatus = r.ReviewStatusValue,
                deliveryStatus = r.DeliveryStatusValue,
                reviewerId = r.AssignedReviewerId,
                updatedAt = r.UpdatedAt,
            })
            .ToListAsync(ct);

        string? nextCursor = null;
        if (items.Count > PageSize)
        {
            var last = items[PageSize - 1];
            nextCursor = EncodeCursor(last.updatedAt, last.id);
            items = items.Take(PageSize).ToList();
        }
        return new { items, nextCursor };
    }

    private static async Task<object> GetAsync(
        Guid projectId, Guid recordId, ProjectAccessService access, AppDbContext db,
        ICurrentUser currentUser, CancellationToken ct)
    {
        await access.EnsureCanViewAsync(projectId, ct);
        var record = await db.Records.AsNoTracking()
                         .Include(r => r.AssignedReviewer)
                         .FirstOrDefaultAsync(r => r.Id == recordId && r.ProjectId == projectId, ct)
                     ?? throw new NotFoundException("Record");
        if (currentUser.Role == UserRole.Reviewer && record.AssignedReviewerId != currentUser.Id)
            throw new ForbiddenException();

        var extractions = await db.ExtractionResults.AsNoTracking()
            .Where(e => e.RecordId == recordId)
            .OrderByDescending(e => e.CreatedAt)
            .Take(10)
            .Select(e => new
            {
                id = e.Id, runId = e.RunId, model = e.Model, status = e.Status,
                output = e.Output, error = e.Error,
                inputTokens = e.InputTokens, outputTokens = e.OutputTokens,
                durationMs = e.DurationMs, createdAt = e.CreatedAt,
            })
            .ToListAsync(ct);

        return new
        {
            id = record.Id,
            externalId = record.ExternalId,
            text = record.Text,
            processingStatus = record.ProcessingStatusValue,
            reviewStatus = record.ReviewStatusValue,
            deliveryStatus = record.DeliveryStatusValue,
            processingError = record.ProcessingError,
            reviewer = record.AssignedReviewer is null
                ? null
                : new { id = record.AssignedReviewer.Id, fullName = record.AssignedReviewer.FullName },
            finalOutput = record.FinalOutput,
            reviewNote = record.ReviewNote,
            version = record.Version,
            createdAt = record.CreatedAt,
            updatedAt = record.UpdatedAt,
            extractions,
        };
    }

    private static async Task<object> DeleteAsync(
        Guid projectId, DeleteRecordsRequest request, ProjectAccessService access,
        AppDbContext db, CancellationToken ct)
    {
        var project = await access.EnsureCanManageAsync(projectId, ct);
        ProjectAccessService.EnsureNotArchived(project);
        if (request.RecordIds.Count is 0 or > 1000)
            throw new AppException(400, ErrorCodes.ValidationFailed, "Select between 1 and 1,000 records.");

        // Guard (R22-lean): only untouched records are deletable.
        var deletable = await db.Records
            .Where(r => r.ProjectId == projectId
                        && request.RecordIds.Contains(r.Id)
                        && r.ProcessingStatusValue == ProcessingStatus.Pending
                        && r.ReviewStatusValue == ReviewStatus.Unassigned)
            .ToListAsync(ct);
        db.Records.RemoveRange(deletable);
        await db.SaveChangesAsync(ct);
        return new { deleted = deletable.Count, skipped = request.RecordIds.Count - deletable.Count };
    }

    private static async Task<object> CountsAsync(
        Guid projectId, ProjectAccessService access, AppDbContext db, CancellationToken ct)
    {
        await access.EnsureCanViewAsync(projectId, ct);
        var byProcessing = await db.Records.AsNoTracking()
            .Where(r => r.ProjectId == projectId)
            .GroupBy(r => r.ProcessingStatusValue)
            .Select(g => new { status = g.Key, count = g.Count() })
            .ToListAsync(ct);
        var byReview = await db.Records.AsNoTracking()
            .Where(r => r.ProjectId == projectId)
            .GroupBy(r => r.ReviewStatusValue)
            .Select(g => new { status = g.Key, count = g.Count() })
            .ToListAsync(ct);
        return new { processing = byProcessing, review = byReview };
    }

    private static string EncodeCursor(DateTimeOffset updatedAt, Guid id) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes($"{updatedAt.UtcTicks}:{id}"));

    private static bool TryDecodeCursor(string? cursor, out DateTimeOffset updatedAt, out Guid id)
    {
        updatedAt = default;
        id = default;
        if (string.IsNullOrWhiteSpace(cursor)) return false;
        try
        {
            var parts = Encoding.UTF8.GetString(Convert.FromBase64String(cursor)).Split(':');
            updatedAt = new DateTimeOffset(long.Parse(parts[0]), TimeSpan.Zero);
            id = Guid.Parse(parts[1]);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
