using FluentValidation;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Structura.Web.Domain;
using Structura.Web.Infrastructure.Auth;
using Structura.Web.Infrastructure.Errors;
using Structura.Web.Infrastructure.Validation;
using Structura.Web.Persistence;

namespace Structura.Web.Features.Auth;

public sealed record LoginRequest(string Email, string Password);
public sealed record RefreshRequest(string RefreshToken);
public sealed record ChangePasswordRequest(string CurrentPassword, string NewPassword);

public sealed record AuthResponse(
    string AccessToken, int ExpiresInSeconds, string RefreshToken,
    bool MustChangePassword, UserSummary User);

public sealed record UserSummary(Guid Id, string FullName, string Email, string Role);

public sealed class LoginRequestValidator : AbstractValidator<LoginRequest>
{
    public LoginRequestValidator()
    {
        RuleFor(x => x.Email).NotEmpty().EmailAddress();
        RuleFor(x => x.Password).NotEmpty();
    }
}

public sealed class ChangePasswordRequestValidator : AbstractValidator<ChangePasswordRequest>
{
    public ChangePasswordRequestValidator()
    {
        RuleFor(x => x.CurrentPassword).NotEmpty();
        RuleFor(x => x.NewPassword).NotEmpty()
            .MinimumLength(10).WithMessage("Password must be at least 10 characters long.")
            .Must((req, newPwd) => newPwd != req.CurrentPassword)
            .WithMessage("New password must be different from the current password.");
    }
}

public static class AuthEndpoints
{
    private const int MaxFailedLogins = 10;
    private static readonly TimeSpan LockoutDuration = TimeSpan.FromMinutes(15);

    public static void Map(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/auth");

        group.MapPost("/login", LoginAsync)
            .Validate<LoginRequest>()
            .RequireRateLimiting("login")
            .AllowAnonymous();

        group.MapPost("/refresh", RefreshAsync).AllowAnonymous();
        group.MapPost("/logout", LogoutAsync).RequireAuthorization();
        group.MapPost("/change-password", ChangePasswordAsync)
            .Validate<ChangePasswordRequest>()
            .RequireAuthorization();

        app.MapGet("/api/me", MeAsync).RequireAuthorization();
    }

    private static async Task<AuthResponse> LoginAsync(
        LoginRequest request, AppDbContext db, IPasswordHasher<User> hasher,
        JwtTokenService tokens, SecurityStampCache stampCache, CancellationToken ct)
    {
        var user = await db.Users.FirstOrDefaultAsync(u => u.Email == request.Email.Trim(), ct);
        if (user is null || !user.IsActive)
            throw new UnauthorizedException(ErrorCodes.InvalidCredentials, "Invalid email or password.");

        var now = DateTimeOffset.UtcNow;
        if (user.LockoutEndAt is { } lockedUntil && lockedUntil > now)
            throw new UnauthorizedException(ErrorCodes.AccountLocked,
                "Account is temporarily locked due to repeated failed sign-in attempts. Try again later.");

        var verdict = hasher.VerifyHashedPassword(user, user.PasswordHash, request.Password);
        if (verdict == PasswordVerificationResult.Failed)
        {
            user.FailedLoginCount++;
            if (user.FailedLoginCount >= MaxFailedLogins)
            {
                user.LockoutEndAt = now.Add(LockoutDuration);
                user.FailedLoginCount = 0;
            }
            await db.SaveChangesAsync(ct);
            throw new UnauthorizedException(ErrorCodes.InvalidCredentials, "Invalid email or password.");
        }

        if (verdict == PasswordVerificationResult.SuccessRehashNeeded)
            user.PasswordHash = hasher.HashPassword(user, request.Password);

        user.FailedLoginCount = 0;
        user.LockoutEndAt = null;
        user.LastLoginAt = now;
        var response = await IssueTokensAsync(db, tokens, user, ct);
        stampCache.Invalidate(user.Id);
        return response;
    }

    private static async Task<AuthResponse> RefreshAsync(
        RefreshRequest request, AppDbContext db, JwtTokenService tokens, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.RefreshToken))
            throw new UnauthorizedException(ErrorCodes.InvalidToken, "Invalid refresh token.");

        var hash = JwtTokenService.HashRefreshToken(request.RefreshToken);
        var stored = await db.RefreshTokens.Include(t => t.User)
            .FirstOrDefaultAsync(t => t.TokenHash == hash, ct);
        if (stored is null)
            throw new UnauthorizedException(ErrorCodes.InvalidToken, "Invalid refresh token.");

        var now = DateTimeOffset.UtcNow;
        if (stored.RevokedAt is not null)
        {
            // Reuse of a rotated token: assume theft, revoke every active token for the user.
            await RevokeAllAsync(db, stored.UserId, now, ct);
            await db.SaveChangesAsync(ct);
            throw new UnauthorizedException(ErrorCodes.InvalidToken, "Refresh token is no longer valid.");
        }

        if (stored.ExpiresAt <= now || !stored.User.IsActive)
            throw new UnauthorizedException(ErrorCodes.InvalidToken, "Refresh token is no longer valid.");

        var refreshToken = JwtTokenService.GenerateRefreshToken();
        var replacement = new RefreshToken
        {
            UserId = stored.UserId,
            TokenHash = JwtTokenService.HashRefreshToken(refreshToken),
            ExpiresAt = now.AddDays(14),
        };
        db.RefreshTokens.Add(replacement);
        stored.RevokedAt = now;
        stored.ReplacedById = replacement.Id;
        await db.SaveChangesAsync(ct);

        var (accessToken, expiresIn) = tokens.CreateAccessToken(stored.User);
        var user = stored.User;
        return new AuthResponse(accessToken, expiresIn, refreshToken, user.MustChangePassword,
            new UserSummary(user.Id, user.FullName, user.Email, user.Role));
    }

    private static async Task<IResult> LogoutAsync(
        AppDbContext db, ICurrentUser currentUser, CancellationToken ct)
    {
        await RevokeAllAsync(db, currentUser.Id, DateTimeOffset.UtcNow, ct);
        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }

    private static async Task<AuthResponse> ChangePasswordAsync(
        ChangePasswordRequest request, AppDbContext db, ICurrentUser currentUser,
        IPasswordHasher<User> hasher, JwtTokenService tokens, SecurityStampCache stampCache,
        CancellationToken ct)
    {
        var user = await db.Users.FirstAsync(u => u.Id == currentUser.Id, ct);
        var verdict = hasher.VerifyHashedPassword(user, user.PasswordHash, request.CurrentPassword);
        if (verdict == PasswordVerificationResult.Failed)
            throw new UnauthorizedException(ErrorCodes.InvalidCredentials, "Current password is incorrect.");

        user.PasswordHash = hasher.HashPassword(user, request.NewPassword);
        user.MustChangePassword = false;
        user.BumpSecurityStamp();
        await RevokeAllAsync(db, user.Id, DateTimeOffset.UtcNow, ct);
        var response = await IssueTokensAsync(db, tokens, user, ct);
        stampCache.Invalidate(user.Id);
        return response;
    }

    private static async Task<object> MeAsync(AppDbContext db, ICurrentUser currentUser, CancellationToken ct)
    {
        var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == currentUser.Id, ct)
                   ?? throw new NotFoundException("User");
        var memberships = await db.ProjectMembers.AsNoTracking()
            .Where(m => m.UserId == user.Id)
            .Select(m => new { projectId = m.ProjectId, projectName = m.Project.Name })
            .ToListAsync(ct);
        return new
        {
            id = user.Id,
            fullName = user.FullName,
            email = user.Email,
            role = user.Role,
            mustChangePassword = user.MustChangePassword,
            memberships,
        };
    }

    internal static async Task<AuthResponse> IssueTokensAsync(
        AppDbContext db, JwtTokenService tokens, User user, CancellationToken ct)
    {
        var refreshToken = JwtTokenService.GenerateRefreshToken();
        db.RefreshTokens.Add(new RefreshToken
        {
            UserId = user.Id,
            TokenHash = JwtTokenService.HashRefreshToken(refreshToken),
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(14),
        });
        await db.SaveChangesAsync(ct);

        var (accessToken, expiresIn) = tokens.CreateAccessToken(user);
        return new AuthResponse(accessToken, expiresIn, refreshToken, user.MustChangePassword,
            new UserSummary(user.Id, user.FullName, user.Email, user.Role));
    }

    internal static async Task RevokeAllAsync(AppDbContext db, Guid userId, DateTimeOffset now, CancellationToken ct)
    {
        var active = await db.RefreshTokens
            .Where(t => t.UserId == userId && t.RevokedAt == null && t.ExpiresAt > now)
            .ToListAsync(ct);
        foreach (var token in active) token.RevokedAt = now;
    }
}
