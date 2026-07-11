using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Structura.Web.Domain;
using Structura.Web.Infrastructure.Auth;
using Structura.Web.Infrastructure.Errors;
using Structura.Web.Infrastructure.Validation;
using Structura.Web.Persistence;

namespace Structura.Web.Features.Projects;

public sealed record CreateProjectRequest(string Name, string? Description);
public sealed record UpdateProjectRequest(string Name, string? Description);
/// <summary>Either userId or email must be provided (email lets project managers add
/// members without access to the admin-only user directory).</summary>
public sealed record AddMemberRequest(Guid? UserId, string? Email);

public sealed record ProjectListItem(
    Guid Id, string Name, string Description, string Status,
    int SchemaVersion, int MemberCount, DateTimeOffset CreatedAt);

public sealed class CreateProjectRequestValidator : AbstractValidator<CreateProjectRequest>
{
    public CreateProjectRequestValidator()
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(200);
        RuleFor(x => x.Description).MaximumLength(2000);
    }
}

public sealed class UpdateProjectRequestValidator : AbstractValidator<UpdateProjectRequest>
{
    public UpdateProjectRequestValidator()
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(200);
        RuleFor(x => x.Description).MaximumLength(2000);
    }
}

public sealed class AddMemberRequestValidator : AbstractValidator<AddMemberRequest>
{
    public AddMemberRequestValidator()
    {
        RuleFor(x => x)
            .Must(x => x.UserId is not null || !string.IsNullOrWhiteSpace(x.Email))
            .WithMessage("Provide a userId or an email.")
            .OverridePropertyName("email");
        RuleFor(x => x.Email).EmailAddress().When(x => !string.IsNullOrWhiteSpace(x.Email));
    }
}

public static class ProjectEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/projects").RequireAuthorization();

        group.MapGet("/", ListAsync);
        group.MapPost("/", CreateAsync).Validate<CreateProjectRequest>();
        group.MapGet("/{id:guid}", GetAsync);
        group.MapPut("/{id:guid}", UpdateAsync).Validate<UpdateProjectRequest>();
        group.MapPost("/{id:guid}/archive", ArchiveAsync);
        group.MapGet("/{id:guid}/members", ListMembersAsync);
        group.MapPost("/{id:guid}/members", AddMemberAsync).Validate<AddMemberRequest>();
        group.MapDelete("/{id:guid}/members/{userId:guid}", RemoveMemberAsync);
    }

    private static async Task<object> ListAsync(AppDbContext db, ICurrentUser currentUser, CancellationToken ct)
    {
        var query = db.Projects.AsNoTracking();
        if (!currentUser.IsAdministrator)
            query = query.Where(p => p.Members.Any(m => m.UserId == currentUser.Id));

        var items = await query
            .OrderByDescending(p => p.CreatedAt)
            .Select(p => new ProjectListItem(
                p.Id, p.Name, p.Description, p.Status, p.SchemaVersion, p.Members.Count, p.CreatedAt))
            .ToListAsync(ct);
        return new { items };
    }

    private static async Task<IResult> CreateAsync(
        CreateProjectRequest request, AppDbContext db, ICurrentUser currentUser, CancellationToken ct)
    {
        if (currentUser.Role is not (UserRole.Administrator or UserRole.ProjectManager))
            throw new ForbiddenException();

        var name = request.Name.Trim();
        if (await db.Projects.AnyAsync(p => p.Name == name, ct))
            throw new ConflictException(ErrorCodes.Duplicate, "A project with this name already exists.");

        var project = new Project
        {
            Name = name,
            Description = request.Description?.Trim() ?? "",
            CreatedById = currentUser.Id,
        };
        db.Projects.Add(project);

        // Project managers must be members of their own projects to keep access rules uniform.
        if (currentUser.Role == UserRole.ProjectManager)
            db.ProjectMembers.Add(new ProjectMember { ProjectId = project.Id, UserId = currentUser.Id });

        await db.SaveChangesAsync(ct);
        return Results.Created($"/api/projects/{project.Id}", new { id = project.Id });
    }

    private static async Task<object> GetAsync(
        Guid id, ProjectAccessService access, AppDbContext db, CancellationToken ct)
    {
        var project = await access.EnsureCanViewAsync(id, ct);
        var memberCount = await db.ProjectMembers.CountAsync(m => m.ProjectId == id, ct);
        return new
        {
            id = project.Id,
            name = project.Name,
            description = project.Description,
            status = project.Status,
            schemaVersion = project.SchemaVersion,
            memberCount,
            createdAt = project.CreatedAt,
        };
    }

    private static async Task<IResult> UpdateAsync(
        Guid id, UpdateProjectRequest request, ProjectAccessService access, AppDbContext db, CancellationToken ct)
    {
        var project = await access.EnsureCanManageAsync(id, ct);
        ProjectAccessService.EnsureNotArchived(project);

        var name = request.Name.Trim();
        if (await db.Projects.AnyAsync(p => p.Id != id && p.Name == name, ct))
            throw new ConflictException(ErrorCodes.Duplicate, "A project with this name already exists.");

        project.Name = name;
        project.Description = request.Description?.Trim() ?? "";
        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }

    private static async Task<IResult> ArchiveAsync(
        Guid id, ProjectAccessService access, AppDbContext db, CancellationToken ct)
    {
        var project = await access.EnsureCanManageAsync(id, ct);
        if (project.IsArchived)
            throw new ConflictException(ErrorCodes.InvalidState, "Project is already archived.");
        project.Status = ProjectStatus.Archived;
        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }

    private static async Task<object> ListMembersAsync(
        Guid id, ProjectAccessService access, AppDbContext db, CancellationToken ct)
    {
        await access.EnsureCanViewAsync(id, ct);
        var items = await db.ProjectMembers.AsNoTracking()
            .Where(m => m.ProjectId == id)
            .OrderBy(m => m.User.FullName)
            .Select(m => new
            {
                userId = m.UserId,
                fullName = m.User.FullName,
                email = m.User.Email,
                role = m.User.Role,
                isActive = m.User.IsActive,
                addedAt = m.CreatedAt,
            })
            .ToListAsync(ct);
        return new { items };
    }

    private static async Task<IResult> AddMemberAsync(
        Guid id, AddMemberRequest request, ProjectAccessService access, AppDbContext db, CancellationToken ct)
    {
        var project = await access.EnsureCanManageAsync(id, ct);
        ProjectAccessService.EnsureNotArchived(project);

        var email = request.Email?.Trim();
        var user = await db.Users.FirstOrDefaultAsync(
                       u => request.UserId != null ? u.Id == request.UserId : u.Email == email!, ct)
                   ?? throw new NotFoundException("User");
        if (!user.IsActive)
            throw new ConflictException(ErrorCodes.InvalidState, "Cannot add a deactivated user to a project.");
        if (await db.ProjectMembers.AnyAsync(m => m.ProjectId == id && m.UserId == user.Id, ct))
            throw new ConflictException(ErrorCodes.Duplicate, "User is already a member of this project.");

        db.ProjectMembers.Add(new ProjectMember { ProjectId = id, UserId = user.Id });
        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }

    private static async Task<IResult> RemoveMemberAsync(
        Guid id, Guid userId, ProjectAccessService access, AppDbContext db, CancellationToken ct)
    {
        var project = await access.EnsureCanManageAsync(id, ct);
        ProjectAccessService.EnsureNotArchived(project);

        var member = await db.ProjectMembers
            .FirstOrDefaultAsync(m => m.ProjectId == id && m.UserId == userId, ct)
            ?? throw new NotFoundException("Project member");
        db.ProjectMembers.Remove(member);
        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }
}
