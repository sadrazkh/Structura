using Structura.Web.Infrastructure.Errors;
using Structura.Web.Persistence;

namespace Structura.Web.Infrastructure.Auth;

/// <summary>
/// Two API gates:
/// 1) setup_required — no administrator exists and bootstrap env vars are missing (F1).
/// 2) password_change_required — authenticated user carries pwd=true; only the allow-listed
///    endpoints may be used until the password is changed.
/// </summary>
public sealed class GateMiddleware(RequestDelegate next)
{
    private static readonly string[] PasswordChangeAllowList =
    [
        "/api/auth/change-password",
        "/api/auth/logout",
        "/api/auth/refresh",
        "/api/auth/login",
        "/api/me",
    ];

    public async Task InvokeAsync(HttpContext context, SetupState setupState)
    {
        var path = context.Request.Path;
        if (!path.StartsWithSegments("/api") || path.StartsWithSegments("/api/health"))
        {
            await next(context);
            return;
        }

        if (setupState.IsSetupRequired)
            throw new AppException(StatusCodes.Status503ServiceUnavailable, ErrorCodes.SetupRequired,
                "No administrator account exists. Set BOOTSTRAP_ADMIN_EMAIL and BOOTSTRAP_ADMIN_PASSWORD and restart.");

        if (context.User.Identity?.IsAuthenticated == true
            && context.User.FindFirst(AppClaims.MustChangePassword)?.Value == "true"
            && !PasswordChangeAllowList.Any(allowed => path.StartsWithSegments(allowed)))
        {
            throw new AppException(StatusCodes.Status403Forbidden, ErrorCodes.PasswordChangeRequired,
                "You must change your password before continuing.");
        }

        await next(context);
    }
}
