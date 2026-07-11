using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;

namespace Structura.Web.Infrastructure.Auth;

public interface ICurrentUser
{
    Guid Id { get; }
    string Role { get; }
    bool IsAdministrator { get; }
}

public sealed class CurrentUser(IHttpContextAccessor accessor) : ICurrentUser
{
    private ClaimsPrincipal Principal =>
        accessor.HttpContext?.User ?? throw new InvalidOperationException("No HTTP context.");

    public Guid Id => Guid.Parse(
        Principal.FindFirstValue(JwtRegisteredClaimNames.Sub)
        ?? throw new InvalidOperationException("No authenticated user."));

    public string Role => Principal.FindFirstValue(AppClaims.Role) ?? "";

    public bool IsAdministrator => Role == Domain.UserRole.Administrator;
}
