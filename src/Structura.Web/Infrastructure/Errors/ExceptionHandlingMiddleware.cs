using FluentValidation;

namespace Structura.Web.Infrastructure.Errors;

/// <summary>Maps exceptions to RFC 7807 problem+json responses with a stable `code`.</summary>
public sealed class ExceptionHandlingMiddleware(RequestDelegate next, ILogger<ExceptionHandlingMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await next(context);
        }
        catch (AppException ex)
        {
            await WriteProblem(context, ex.Status, ex.Code, ex.Message);
        }
        catch (ValidationException ex)
        {
            var errors = ex.Errors
                .GroupBy(e => JsonCamelCase(e.PropertyName))
                .ToDictionary(g => g.Key, g => g.Select(e => e.ErrorMessage).Distinct().ToArray());
            await WriteProblem(context, StatusCodes.Status400BadRequest,
                ErrorCodes.ValidationFailed, "One or more validation errors occurred.", errors);
        }
        catch (Exception ex) when (context.RequestAborted.IsCancellationRequested)
        {
            logger.LogDebug(ex, "Request was cancelled by the client.");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Unhandled exception for {Method} {Path}", context.Request.Method, context.Request.Path);
            await WriteProblem(context, StatusCodes.Status500InternalServerError,
                "internal_error", "An unexpected error occurred.");
        }
    }

    private static string JsonCamelCase(string propertyName) =>
        string.IsNullOrEmpty(propertyName) ? propertyName : char.ToLowerInvariant(propertyName[0]) + propertyName[1..];

    private static async Task WriteProblem(
        HttpContext context, int status, string code, string detail,
        Dictionary<string, string[]>? errors = null)
    {
        if (context.Response.HasStarted) return;
        context.Response.Clear();
        context.Response.StatusCode = status;
        context.Response.ContentType = "application/problem+json";
        await context.Response.WriteAsJsonAsync(new
        {
            type = "about:blank",
            title = code,
            status,
            code,
            detail,
            errors,
            traceId = context.TraceIdentifier,
        });
    }
}
