using FluentValidation;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Structura.Web.Domain;
using Structura.Web.Infrastructure.Auth;
using Structura.Web.Infrastructure.Errors;
using Structura.Web.Infrastructure.Validation;
using Structura.Web.Persistence;

namespace Structura.Web.Features.Users;

public sealed record CreateUserRequest(string FullName, string Email, string Password, string Role);
public sealed record UpdateUserRequest(string FullName, string Role);
public sealed record ResetPasswordRequest(string NewPassword);

public sealed record UserDto(
    Guid Id, string FullName, string Email, string Role, bool IsActive,
    bool MustChangePassword, DateTimeOffset? LastLoginAt, DateTimeOffset CreatedAt);

public sealed class CreateUserRequestValidator : AbstractValidator<CreateUserRequest>
{
    public CreateUserRequestValidator()
    {
        RuleFor(x => x.FullName).NotEmpty().MaximumLength(200);
        RuleFor(x => x.Email).NotEmpty().EmailAddress().MaximumLength(320);
        RuleFor(x => x.Password).NotEmpty().MinimumLength(10)
            .WithMessage("Password must be at least 10 characters long.");
        RuleFor(x => x.Role).Must(r => UserRole.All.Contains(r))
            .WithMessage("Role must be one of: Administrator, ProjectManager, Reviewer.");
    }
}

public sealed class UpdateUserRequestValidator : AbstractValidator<UpdateUserRequest>
{
    public UpdateUserRequestValidator()
    {
        RuleFor(x => x.FullName).NotEmpty().MaximumLength(200);
        RuleFor(x => x.Role).Must(r => UserRole.All.Contains(r))
            .WithMessage("Role must be one of: Administrator, ProjectManager, Reviewer.");
    }
}

public sealed class ResetPasswordRequestValidator : AbstractValidator<ResetPasswordRequest>
{
    public ResetPasswordRequestValidator()
    {
        RuleFor(x => x.NewPassword).NotEmpty().MinimumLength(10)
            .WithMessage("Password must be at least 10 characters long.");
    }
}

public static class UserEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/users").RequireAuthorization("Administrator");

        group.MapGet("/", ListAsync);
        group.MapPost("/", CreateAsync).Validate<CreateUserRequest>();
        group.MapPut("/{id:guid}", UpdateAsync).Validate<UpdateUserRequest>();
        group.MapPost("/{id:guid}/reset-password", ResetPasswordAsync).Validate<ResetPasswordRequest>();
        group.MapPost("/{id:guid}/deactivate", DeactivateAsync);
        group.MapPost("/{id:guid}/reactivate", ReactivateAsync);
    }

    private static async Task<object> ListAsync(AppDbContext db, string? search, CancellationToken ct)
    {
        var query = db.Users.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(search))
        {
            var pattern = $"%{search.Trim()}%";
            query = query.Where(u =>
                EF.Functions.ILike(u.FullName, pattern) || EF.Functions.ILike(u.Email, pattern));
        }
        var items = await query.OrderBy(u => u.FullName).Select(u => ToDto(u)).ToListAsync(ct);
        return new { items };
    }

    private static async Task<IResult> CreateAsync(
        CreateUserRequest request, AppDbContext db, IPasswordHasher<User> hasher, CancellationToken ct)
    {
        var email = request.Email.Trim();
        if (await db.Users.AnyAsync(u => u.Email == email, ct))
            throw new ConflictException(ErrorCodes.Duplicate, "A user with this email already exists.");

        var user = new User
        {
            Email = email,
            FullName = request.FullName.Trim(),
            Role = request.Role,
            MustChangePassword = true,
        };
        user.PasswordHash = hasher.HashPassword(user, request.Password);
        db.Users.Add(user);
        await db.SaveChangesAsync(ct);
        return Results.Created($"/api/users/{user.Id}", ToDto(user));
    }

    private static async Task<UserDto> UpdateAsync(
        Guid id, UpdateUserRequest request, AppDbContext db, SecurityStampCache stampCache, CancellationToken ct)
    {
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == id, ct) ?? throw new NotFoundException("User");

        if (user.Role == UserRole.Administrator && request.Role != UserRole.Administrator)
            await EnsureAnotherActiveAdminAsync(db, user.Id, ct);

        user.FullName = request.FullName.Trim();
        if (user.Role != request.Role)
        {
            user.Role = request.Role;
            user.BumpSecurityStamp(); // force re-login so the role claim is refreshed
            await AuthEndpointsRevokeAsync(db, user.Id, ct);
            stampCache.Invalidate(user.Id);
        }
        await db.SaveChangesAsync(ct);
        return ToDto(user);
    }

    private static async Task<IResult> ResetPasswordAsync(
        Guid id, ResetPasswordRequest request, AppDbContext db, IPasswordHasher<User> hasher,
        SecurityStampCache stampCache, CancellationToken ct)
    {
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == id, ct) ?? throw new NotFoundException("User");
        user.PasswordHash = hasher.HashPassword(user, request.NewPassword);
        user.MustChangePassword = true;
        user.FailedLoginCount = 0;
        user.LockoutEndAt = null;
        user.BumpSecurityStamp();
        await AuthEndpointsRevokeAsync(db, user.Id, ct);
        await db.SaveChangesAsync(ct);
        stampCache.Invalidate(user.Id);
        return Results.NoContent();
    }

    private static async Task<IResult> DeactivateAsync(
        Guid id, AppDbContext db, ICurrentUser currentUser, SecurityStampCache stampCache, CancellationToken ct)
    {
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == id, ct) ?? throw new NotFoundException("User");
        if (user.Id == currentUser.Id)
            throw new ConflictException(ErrorCodes.InvalidState, "You cannot deactivate your own account.");
        if (user.Role == UserRole.Administrator)
            await EnsureAnotherActiveAdminAsync(db, user.Id, ct);
        if (!user.IsActive) return Results.NoContent();

        user.IsActive = false;
        user.BumpSecurityStamp();
        await AuthEndpointsRevokeAsync(db, user.Id, ct);
        await db.SaveChangesAsync(ct);
        stampCache.Invalidate(user.Id);
        return Results.NoContent();
    }

    private static async Task<IResult> ReactivateAsync(Guid id, AppDbContext db, CancellationToken ct)
    {
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == id, ct) ?? throw new NotFoundException("User");
        user.IsActive = true;
        user.FailedLoginCount = 0;
        user.LockoutEndAt = null;
        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }

    private static async Task EnsureAnotherActiveAdminAsync(AppDbContext db, Guid excludingUserId, CancellationToken ct)
    {
        var hasAnother = await db.Users.AnyAsync(
            u => u.Id != excludingUserId && u.Role == UserRole.Administrator && u.IsActive, ct);
        if (!hasAnother)
            throw new ConflictException(ErrorCodes.LastAdministrator,
                "This is the last active administrator account.");
    }

    private static Task AuthEndpointsRevokeAsync(AppDbContext db, Guid userId, CancellationToken ct) =>
        Features.Auth.AuthEndpoints.RevokeAllAsync(db, userId, DateTimeOffset.UtcNow, ct);

    private static UserDto ToDto(User u) => new(
        u.Id, u.FullName, u.Email, u.Role, u.IsActive, u.MustChangePassword, u.LastLoginAt, u.CreatedAt);
}
