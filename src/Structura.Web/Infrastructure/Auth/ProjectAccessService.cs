using Microsoft.EntityFrameworkCore;
using Structura.Web.Domain;
using Structura.Web.Infrastructure.Errors;
using Structura.Web.Persistence;

namespace Structura.Web.Infrastructure.Auth;

/// <summary>
/// Simple access rules (docs/01 roles): Administrator → everything;
/// ProjectManager → manage projects they are a member of;
/// Reviewer → view projects they are a member of (review-only surface).
/// </summary>
public sealed class ProjectAccessService(AppDbContext db, ICurrentUser currentUser)
{
    public async Task<Project> EnsureCanViewAsync(Guid projectId, CancellationToken ct)
    {
        var project = await db.Projects.FirstOrDefaultAsync(p => p.Id == projectId, ct)
                      ?? throw new NotFoundException("Project");
        if (currentUser.IsAdministrator) return project;

        var isMember = await db.ProjectMembers
            .AnyAsync(m => m.ProjectId == projectId && m.UserId == currentUser.Id, ct);
        if (!isMember) throw new ForbiddenException();
        return project;
    }

    public async Task<Project> EnsureCanManageAsync(Guid projectId, CancellationToken ct)
    {
        var project = await EnsureCanViewAsync(projectId, ct);
        if (currentUser.IsAdministrator) return project;
        if (currentUser.Role != UserRole.ProjectManager) throw new ForbiddenException();
        return project;
    }

    public static void EnsureNotArchived(Project project)
    {
        if (project.IsArchived)
            throw new ConflictException(ErrorCodes.InvalidState, "This project is archived and read-only.");
    }
}
