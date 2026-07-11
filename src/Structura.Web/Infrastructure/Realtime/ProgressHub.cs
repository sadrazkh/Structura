using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Structura.Web.Domain;
using Structura.Web.Persistence;

namespace Structura.Web.Infrastructure.Realtime;

/// <summary>
/// Live progress channel. Clients join per-project groups after a membership check;
/// workers broadcast ImportProgress / RunProgress messages to those groups.
/// </summary>
[Authorize]
public sealed class ProgressHub(AppDbContext db) : Hub
{
    public async Task JoinProject(Guid projectId)
    {
        var subject = Context.User?.FindFirst(System.IdentityModel.Tokens.Jwt.JwtRegisteredClaimNames.Sub)?.Value;
        if (subject is null || !Guid.TryParse(subject, out var userId))
            throw new HubException("Unauthorized.");

        var role = Context.User?.FindFirst(Auth.AppClaims.Role)?.Value;
        var allowed = role == UserRole.Administrator
                      || await db.ProjectMembers.AnyAsync(
                          m => m.ProjectId == projectId && m.UserId == userId, Context.ConnectionAborted);
        if (!allowed) throw new HubException("You do not have access to this project.");

        await Groups.AddToGroupAsync(Context.ConnectionId, GroupName(projectId), Context.ConnectionAborted);
    }

    public Task LeaveProject(Guid projectId) =>
        Groups.RemoveFromGroupAsync(Context.ConnectionId, GroupName(projectId), Context.ConnectionAborted);

    public static string GroupName(Guid projectId) => $"project:{projectId}";
}
