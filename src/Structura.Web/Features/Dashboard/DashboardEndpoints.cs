using Microsoft.EntityFrameworkCore;
using Structura.Web.Domain;
using Structura.Web.Infrastructure.Auth;
using Structura.Web.Persistence;

namespace Structura.Web.Features.Dashboard;

public static class DashboardEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        app.MapGet("/api/projects/{projectId:guid}/dashboard", GetAsync).RequireAuthorization();
    }

    private static async Task<object> GetAsync(
        Guid projectId, ProjectAccessService access, AppDbContext db, CancellationToken ct)
    {
        await access.EnsureCanViewAsync(projectId, ct);

        var processing = await CountBy(db, projectId, r => r.ProcessingStatusValue, ct);
        var review = await CountBy(db, projectId, r => r.ReviewStatusValue, ct);
        var delivery = await db.Records.AsNoTracking()
            .Where(r => r.ProjectId == projectId && r.ReviewStatusValue == ReviewStatus.Approved)
            .GroupBy(r => r.DeliveryStatusValue)
            .Select(g => new StatusCount(g.Key, g.Count()))
            .ToListAsync(ct);

        var total = await db.Records.AsNoTracking().CountAsync(r => r.ProjectId == projectId, ct);

        var runAggregate = await db.ProcessingRuns.AsNoTracking()
            .Where(r => r.ProjectId == projectId)
            .GroupBy(_ => 1)
            .Select(g => new
            {
                totalRuns = g.Count(),
                inputTokens = g.Sum(r => r.InputTokens),
                outputTokens = g.Sum(r => r.OutputTokens),
            })
            .FirstOrDefaultAsync(ct);

        var activeRun = await db.ProcessingRuns.AsNoTracking()
            .Where(r => r.ProjectId == projectId && r.Status == RunStatus.Running)
            .OrderByDescending(r => r.CreatedAt)
            .Select(r => new { id = r.Id, total = r.Total, succeeded = r.Succeeded, failed = r.Failed })
            .FirstOrDefaultAsync(ct);

        var recentRuns = await db.ProcessingRuns.AsNoTracking()
            .Where(r => r.ProjectId == projectId)
            .OrderByDescending(r => r.CreatedAt)
            .Take(5)
            .Select(r => new
            {
                id = r.Id, status = r.Status, total = r.Total,
                succeeded = r.Succeeded, failed = r.Failed, createdAt = r.CreatedAt,
            })
            .ToListAsync(ct);

        return new
        {
            total,
            processing,
            review,
            delivery,
            tokens = new
            {
                input = runAggregate?.inputTokens ?? 0,
                output = runAggregate?.outputTokens ?? 0,
                totalRuns = runAggregate?.totalRuns ?? 0,
            },
            activeRun,
            recentRuns,
        };
    }

    private static async Task<List<StatusCount>> CountBy(
        AppDbContext db, Guid projectId, System.Linq.Expressions.Expression<Func<Record, string>> selector,
        CancellationToken ct) =>
        await db.Records.AsNoTracking()
            .Where(r => r.ProjectId == projectId)
            .GroupBy(selector)
            .Select(g => new StatusCount(g.Key, g.Count()))
            .ToListAsync(ct);

    private sealed record StatusCount(string Status, int Count);
}
