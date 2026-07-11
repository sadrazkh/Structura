namespace Structura.Web.Infrastructure.Errors;

public static class ErrorCodes
{
    public const string InvalidCredentials = "invalid_credentials";
    public const string AccountLocked = "account_locked";
    public const string InvalidToken = "invalid_token";
    public const string PasswordChangeRequired = "password_change_required";
    public const string PermissionDenied = "permission_denied";
    public const string NotFound = "not_found";
    public const string Duplicate = "duplicate";
    public const string InvalidState = "invalid_state";
    public const string VersionConflict = "version_conflict";
    public const string ValidationFailed = "validation_failed";
    public const string SetupRequired = "setup_required";
    public const string LastAdministrator = "last_administrator";
}

public class AppException(int status, string code, string message) : Exception(message)
{
    public int Status { get; } = status;
    public string Code { get; } = code;
}

public sealed class NotFoundException(string what = "Resource")
    : AppException(StatusCodes.Status404NotFound, ErrorCodes.NotFound, $"{what} was not found.");

public sealed class ForbiddenException(string message = "You do not have permission to perform this action.")
    : AppException(StatusCodes.Status403Forbidden, ErrorCodes.PermissionDenied, message);

public sealed class ConflictException(string code, string message)
    : AppException(StatusCodes.Status409Conflict, code, message);

public sealed class UnauthorizedException(string code, string message)
    : AppException(StatusCodes.Status401Unauthorized, code, message);
