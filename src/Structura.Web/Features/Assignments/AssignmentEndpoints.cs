using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Structura.Web.Domain;
using Structura.Web.Infrastructure.Auth;
using Structura.Web.Infrastructure.Errors;
using Structura.Web.Infrastructure.Validation;
using Structura.Web.Persistence;

namespace Structura.Web.Features.Assignments;

public sealed record AssignRequest(List<Guid> RecordIds, string Mode, Guid? ReviewerId, List<Guid>? ReviewerIds);
public sealed record UnassignRequest(List<Guid> RecordIds);
public sealed record ReassignRequest(List<Guid> RecordIds, Guid ReviewerId);

public sealed class AssignRequestValidator : AbstractValidator<AssignRequest>
{
    public AssignRequestValidator()
    {
        RuleFor(x => x.RecordIds).NotEmpty().Must(ids => ids.Count <= 5000)
            .WithMessage("Select between 1 and 5,000 records.");
        RuleFor(x => x.Mode).Must(m => m is "single" or "distribute")
            .WithMessage("Mode must be single or distribute.");
        RuleFor(x => x.ReviewerId).NotNull().When(x => x.Mode == "single")
            .WithMessage("Choose a reviewer.");
        RuleFor(x => x.ReviewerIds).NotEmpty().When(x => x.Mode == "distribute")
            .WithMessage("Choose at least one reviewer.");
    }
}

public static class AssignmentEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/projects/{projectId:guid}/assignments").RequireAuthorization();
        group.MapPost("/", AssignAsync).Validate<AssignRequest>();
        group.MapPost("/unassign", UnassignAsync);
        group.MapPost("/reassign", ReassignAsync);
        app.MapGet("/api/projects/{projectId:guid}/review-status", ReviewStatusAsync).RequireAuthorization();
    }

    private static async Task<object> AssignAsync(
        Guid projectId, AssignRequest request, ProjectAccessService access,
        AppDbContext db, CancellationToken ct)
    {
        var project = await access.EnsureCanManageAsync(projectId, ct);
        ProjectAccessService.EnsureNotArchived(project);

        var reviewerIds = request.Mode == "single" ? [request.ReviewerId!.Value] : request.ReviewerIds!;
        await EnsureReviewersAreMembersAsync(db, projectId, reviewerIds, ct);

        var records = await db.Records
            .Where(r => r.ProjectId == projectId && request.RecordIds.Contains(r.Id))
            .ToListAsync(ct);
        var foundIds = records.Select(r => r.Id).ToHashSet();

        var results = new List<object>();
        foreach (var missing in request.RecordIds.Where(id => !foundIds.Contains(id)))
            results.Add(new { recordId = missing, ok = false, reason = "Record not found." });

        var assigned = 0;
        var reviewerIndex = 0;
        var now = DateTimeOffset.UtcNow;
        foreach (var record in records)
        {
            string? reason = record switch
            {
                { ProcessingStatusValue: not ProcessingStatus.Completed } =>
                    "Not processed yet — only completed records can be reviewed.",
                { ReviewStatusValue: ReviewStatus.Approved } => "Already approved.",
                { ReviewStatusValue: ReviewStatus.Assigned or ReviewStatus.InReview } =>
                    "Already assigned — use reassign instead.",
                _ => null,
            };
            if (reason is not null)
            {
                results.Add(new { recordId = record.Id, ok = false, reason });
                continue;
            }

            // Round-robin keeps the distribution even in "distribute" mode.
            record.AssignedReviewerId = reviewerIds[reviewerIndex % reviewerIds.Count];
            reviewerIndex++;
            record.AssignedAt = now;
            record.ReviewStatusValue = ReviewStatus.Assigned;
            record.Version++;
            assigned++;
        }
        await db.SaveChangesAsync(ct);

        return new { assigned, skipped = results.Count, results };
    }

    private static async Task EnsureReviewersAreMembersAsync(
        AppDbContext db, Guid projectId, List<Guid> reviewerIds, CancellationToken ct)
    {
        var memberIds = await db.ProjectMembers
            .Where(m => m.ProjectId == projectId && reviewerIds.Contains(m.UserId) && m.User.IsActive)
            .Select(m => m.UserId)
            .ToListAsync(ct);
        var nonMembers = reviewerIds.Except(memberIds).ToList();
        if (nonMembers.Count > 0)
            throw new ConflictException(ErrorCodes.ValidationFailed,
                "Every reviewer must be an active member of this project.");
    }

    private static async Task<object> UnassignAsync(
        Guid projectId, UnassignRequest request, ProjectAccessService access,
        AppDbContext db, CancellationToken ct)
    {
        var project = await access.EnsureCanManageAsync(projectId, ct);
        ProjectAccessService.EnsureNotArchived(project);

        var updated = await db.Records
            .Where(r => r.ProjectId == projectId
                        && request.RecordIds.Contains(r.Id)
                        && (r.ReviewStatusValue == ReviewStatus.Assigned
                            || r.ReviewStatusValue == ReviewStatus.InReview))
            .ExecuteUpdateAsync(s => s
                .SetProperty(r => r.AssignedReviewerId, (Guid?)null)
                .SetProperty(r => r.AssignedAt, (DateTimeOffset?)null)
                .SetProperty(r => r.ReviewStatusValue, ReviewStatus.Unassigned)
                .SetProperty(r => r.Version, r => r.Version + 1), ct);
        return new { unassigned = updated, skipped = request.RecordIds.Count - updated };
    }

    private static async Task<object> ReassignAsync(
        Guid projectId, ReassignRequest request, ProjectAccessService access,
        AppDbContext db, CancellationToken ct)
    {
        var project = await access.EnsureCanManageAsync(projectId, ct);
        ProjectAccessService.EnsureNotArchived(project);
        await EnsureReviewersAreMembersAsync(db, projectId, [request.ReviewerId], ct);

        var updated = await db.Records
            .Where(r => r.ProjectId == projectId
                        && request.RecordIds.Contains(r.Id)
                        && (r.ReviewStatusValue == ReviewStatus.Assigned
                            || r.ReviewStatusValue == ReviewStatus.InReview
                            || r.ReviewStatusValue == ReviewStatus.ReprocessRequested))
            .ExecuteUpdateAsync(s => s
                .SetProperty(r => r.AssignedReviewerId, request.ReviewerId)
                .SetProperty(r => r.AssignedAt, DateTimeOffset.UtcNow)
                .SetProperty(r => r.Version, r => r.Version + 1), ct);
        return new { reassigned = updated, skipped = request.RecordIds.Count - updated };
    }

    private static async Task<object> ReviewStatusAsync(
        Guid projectId, ProjectAccessService access, AppDbContext db, CancellationToken ct)
    {
        await access.EnsureCanManageAsync(projectId, ct);

        var statusCounts = await db.Records.AsNoTracking()
            .Where(r => r.ProjectId == projectId)
            .GroupBy(r => r.ReviewStatusValue)
            .Select(g => new { status = g.Key, count = g.Count() })
            .ToListAsync(ct);

        var perReviewer = await db.Records.AsNoTracking()
            .Where(r => r.ProjectId == projectId && r.AssignedReviewerId != null)
            .GroupBy(r => new { r.AssignedReviewerId, r.AssignedReviewer!.FullName, r.AssignedReviewer.Email })
            .Select(g => new
            {
                reviewerId = g.Key.AssignedReviewerId,
                fullName = g.Key.FullName,
                email = g.Key.Email,
                pending = g.Count(r => r.ReviewStatusValue == ReviewStatus.Assigned
                                       || r.ReviewStatusValue == ReviewStatus.InReview),
                approved = g.Count(r => r.ReviewStatusValue == ReviewStatus.Approved),
                rejected = g.Count(r => r.ReviewStatusValue == ReviewStatus.Rejected),
                reprocessRequested = g.Count(r => r.ReviewStatusValue == ReviewStatus.ReprocessRequested),
            })
            .OrderBy(g => g.fullName)
            .ToListAsync(ct);

        return new { statusCounts, perReviewer };
    }
}
