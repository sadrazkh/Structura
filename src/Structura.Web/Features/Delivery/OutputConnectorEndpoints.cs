using System.Text;
using System.Text.Json.Nodes;
using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Structura.Web.Domain;
using Structura.Web.Infrastructure.Auth;
using Structura.Web.Infrastructure.Delivery;
using Structura.Web.Infrastructure.Errors;
using Structura.Web.Infrastructure.Http;
using Structura.Web.Infrastructure.Secrets;
using Structura.Web.Infrastructure.Validation;
using Structura.Web.Persistence;

namespace Structura.Web.Features.Delivery;

public sealed record UpdateApiOutputRequest(
    string Url, string Method, Dictionary<string, string>? Headers,
    string AuthType, string? Token, string? ApiKeyHeaderName,
    string BodyTemplate, List<int>? SuccessStatusCodes, string? ResponseIdPath, bool Enabled);

public sealed class UpdateApiOutputRequestValidator : AbstractValidator<UpdateApiOutputRequest>
{
    private static readonly string[] ForbiddenHeaders =
        ["host", "content-length", "transfer-encoding", "connection", "authorization", "cookie"];

    public UpdateApiOutputRequestValidator()
    {
        RuleFor(x => x.Url).NotEmpty().Must(u => Uri.TryCreate(u, UriKind.Absolute, out _))
            .WithMessage("URL must be a valid absolute URL.");
        RuleFor(x => x.Method).Must(m => m is "POST" or "PUT" or "PATCH")
            .WithMessage("Method must be POST, PUT, or PATCH.");
        RuleFor(x => x.AuthType).Must(t => t is "none" or "bearer" or "apiKey")
            .WithMessage("Auth type must be none, bearer, or apiKey.");
        RuleFor(x => x.Headers)
            .Must(h => h is null || !h.Keys.Any(k => ForbiddenHeaders.Contains(k.ToLowerInvariant())))
            .WithMessage("Headers may not include Host, Content-Length, Transfer-Encoding, Connection, Authorization, or Cookie.");
    }
}

public static class OutputConnectorEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/projects/{projectId:guid}").RequireAuthorization();
        group.MapGet("/api-output", GetAsync);
        group.MapPut("/api-output", UpdateAsync).Validate<UpdateApiOutputRequest>();
        group.MapPost("/api-output/test", TestAsync);
        group.MapGet("/deliveries", ListAsync);
        group.MapPost("/deliveries/start", StartAsync);
        group.MapPost("/deliveries/retry-failed", RetryFailedAsync);
    }

    private static async Task<object> GetAsync(
        Guid projectId, ProjectAccessService access, CancellationToken ct)
    {
        var project = await access.EnsureCanManageAsync(projectId, ct);
        var config = ApiOutputConfig.ParseOrNull(project.ApiOutputConfig);
        return new
        {
            configured = config is not null,
            url = config?.Url ?? "",
            method = config?.Method ?? "POST",
            headers = config?.Headers ?? [],
            authType = config?.AuthType ?? "none",
            hasToken = config?.TokenProtected is not null,
            apiKeyHeaderName = config?.ApiKeyHeaderName ?? "X-Api-Key",
            bodyTemplate = config?.BodyTemplate ?? "",
            successStatusCodes = config?.SuccessStatusCodes ?? [200, 201, 202, 204],
            responseIdPath = config?.ResponseIdPath ?? "",
            enabled = config?.Enabled ?? true,
        };
    }

    private static async Task<IResult> UpdateAsync(
        Guid projectId, UpdateApiOutputRequest request, ProjectAccessService access,
        AppDbContext db, ISecretProtector secrets, SafeHttpClientFactory safeHttp, CancellationToken ct)
    {
        var project = await access.EnsureCanManageAsync(projectId, ct);
        ProjectAccessService.EnsureNotArchived(project);

        // Validate the body template against the project's schema keys.
        var schemaKeys = SchemaDocument.Parse(project.SchemaFields).Fields.Select(f => f.Key).ToHashSet();
        var templateErrors = BodyTemplateRenderer.Validate(request.BodyTemplate, schemaKeys);
        if (templateErrors.Count > 0)
            throw new AppException(400, ErrorCodes.ValidationFailed,
                "Body template problems: " + string.Join(" ", templateErrors));

        // Fail fast on unsafe destinations at save time (re-vetted on every send).
        await safeHttp.ValidateAsync(new Uri(request.Url), SafeHttpProfile.Connector, ct);

        var existing = ApiOutputConfig.ParseOrNull(project.ApiOutputConfig);
        string? tokenProtected = null;
        if (request.AuthType != "none")
        {
            if (!string.IsNullOrWhiteSpace(request.Token))
                tokenProtected = secrets.Protect(request.Token.Trim());
            else if (existing?.TokenProtected is not null)
                tokenProtected = existing.TokenProtected;
            else
                throw new ConflictException(ErrorCodes.ValidationFailed, "A token is required for this auth type.");
        }

        project.ApiOutputConfig = new ApiOutputConfig
        {
            Url = request.Url.Trim(),
            Method = request.Method,
            Headers = request.Headers ?? [],
            AuthType = request.AuthType,
            TokenProtected = tokenProtected,
            ApiKeyHeaderName = string.IsNullOrWhiteSpace(request.ApiKeyHeaderName) ? "X-Api-Key" : request.ApiKeyHeaderName.Trim(),
            BodyTemplate = request.BodyTemplate,
            SuccessStatusCodes = request.SuccessStatusCodes is { Count: > 0 } ? request.SuccessStatusCodes : [200, 201, 202, 204],
            ResponseIdPath = string.IsNullOrWhiteSpace(request.ResponseIdPath) ? null : request.ResponseIdPath.Trim(),
            Enabled = request.Enabled,
        }.ToJson();
        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }

    private static async Task<object> TestAsync(
        Guid projectId, bool? send, ProjectAccessService access, AppDbContext db,
        ISecretProtector secrets, SafeHttpClientFactory safeHttp, CancellationToken ct)
    {
        var project = await access.EnsureCanManageAsync(projectId, ct);
        var config = ApiOutputConfig.ParseOrNull(project.ApiOutputConfig)
                     ?? throw new ConflictException("configuration_incomplete", "Save the API output configuration first.");

        // Pick a sample approved record; fall back to a synthetic one so testing works pre-approval.
        var sample = await db.Records.AsNoTracking()
            .Include(r => r.ReviewedBy)
            .Where(r => r.ProjectId == projectId && r.ReviewStatusValue == ReviewStatus.Approved)
            .OrderByDescending(r => r.ReviewedAt)
            .FirstOrDefaultAsync(ct);

        JsonObject output;
        BodyTemplateRenderer.Context context;
        if (sample is not null)
        {
            output = sample.FinalOutput is not null
                ? JsonNode.Parse(sample.FinalOutput) as JsonObject ?? new JsonObject()
                : new JsonObject();
            context = new BodyTemplateRenderer.Context(sample.Id, sample.ExternalId, output,
                sample.ReviewedBy?.FullName, sample.ReviewedBy?.Email, sample.ReviewedAt);
        }
        else
        {
            output = new JsonObject();
            foreach (var field in SchemaDocument.Parse(project.SchemaFields).Fields)
                output[field.Key] = "sample";
            context = new BodyTemplateRenderer.Context(
                Guid.CreateVersion7(), "SAMPLE-001", output, "Sample Reviewer", "reviewer@example.com",
                DateTimeOffset.UtcNow);
        }

        string renderedBody;
        try
        {
            renderedBody = string.IsNullOrWhiteSpace(config.BodyTemplate)
                ? new JsonObject { ["externalId"] = context.ExternalId, ["output"] = output.DeepClone() }.ToJsonString()
                : BodyTemplateRenderer.Render(config.BodyTemplate, context);
        }
        catch (Exception e)
        {
            return new { ok = false, rendered = (string?)null, sent = false, error = $"Template render failed: {e.Message}" };
        }

        if (send != true)
            return new { ok = true, rendered = renderedBody, sent = false, statusCode = (int?)null, response = (string?)null, error = (string?)null };

        using var request = new HttpRequestMessage(new HttpMethod(config.Method), config.Url)
        {
            Content = new StringContent(renderedBody, Encoding.UTF8, "application/json"),
        };
        foreach (var (key, value) in config.Headers) request.Headers.TryAddWithoutValidation(key, value);
        if (config.TokenProtected is not null)
        {
            var token = secrets.Unprotect(config.TokenProtected);
            if (config.AuthType == "bearer") request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {token}");
            else if (config.AuthType == "apiKey") request.Headers.TryAddWithoutValidation(config.ApiKeyHeaderName, token);
        }

        try
        {
            using var response = await safeHttp.SendAsync(request, SafeHttpProfile.Connector, TimeSpan.FromSeconds(30), ct);
            var responseBody = await SafeHttpClientFactory.ReadBodyCappedAsync(response, SafeHttpProfile.Connector, ct);
            var status = (int)response.StatusCode;
            return new
            {
                ok = config.SuccessStatusCodes.Contains(status),
                rendered = renderedBody, sent = true, statusCode = status,
                response = responseBody.Length > 1000 ? responseBody[..1000] + "…" : responseBody,
                error = (string?)null,
            };
        }
        catch (AppException e)
        {
            return new { ok = false, rendered = renderedBody, sent = true, statusCode = (int?)null, response = (string?)null, error = e.Message };
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        {
            return new { ok = false, rendered = renderedBody, sent = true, statusCode = (int?)null, response = (string?)null, error = e.Message };
        }
    }

    private static async Task<object> ListAsync(
        Guid projectId, ProjectAccessService access, AppDbContext db,
        string? status, CancellationToken ct)
    {
        await access.EnsureCanViewAsync(projectId, ct);

        var counts = await db.Records.AsNoTracking()
            .Where(r => r.ProjectId == projectId && r.ReviewStatusValue == ReviewStatus.Approved)
            .GroupBy(r => r.DeliveryStatusValue)
            .Select(g => new { status = g.Key, count = g.Count() })
            .ToListAsync(ct);

        var query = db.Records.AsNoTracking()
            .Where(r => r.ProjectId == projectId && r.ReviewStatusValue == ReviewStatus.Approved);
        if (!string.IsNullOrWhiteSpace(status)) query = query.Where(r => r.DeliveryStatusValue == status);

        var items = await query
            .OrderByDescending(r => r.UpdatedAt)
            .Take(100)
            .Select(r => new
            {
                id = r.Id,
                externalId = r.ExternalId,
                deliveryStatus = r.DeliveryStatusValue,
                attempts = r.DeliveryAttempts,
                deliveredAt = r.DeliveredAt,
                error = r.DeliveryError,
                externalDeliveryId = r.DeliveryExternalId,
            })
            .ToListAsync(ct);

        return new { counts, items };
    }

    private static async Task<object> StartAsync(
        Guid projectId, ProjectAccessService access, AppDbContext db, CancellationToken ct)
    {
        var project = await access.EnsureCanManageAsync(projectId, ct);
        ProjectAccessService.EnsureNotArchived(project);
        if (ApiOutputConfig.ParseOrNull(project.ApiOutputConfig) is null)
            throw new ConflictException("configuration_incomplete", "Configure the API output connector first.");

        // Queue every approved record that isn't already delivered (re-arms Failed ones too).
        var queued = await db.Records
            .Where(r => r.ProjectId == projectId
                        && r.ReviewStatusValue == ReviewStatus.Approved
                        && r.DeliveryStatusValue != DeliveryStatus.Delivered)
            .ExecuteUpdateAsync(s => s
                .SetProperty(r => r.DeliveryStatusValue, DeliveryStatus.Pending)
                .SetProperty(r => r.DeliveryNextRetryAt, (DateTimeOffset?)null)
                .SetProperty(r => r.DeliveryError, (string?)null), ct);
        return new { queued };
    }

    private static async Task<object> RetryFailedAsync(
        Guid projectId, ProjectAccessService access, AppDbContext db, CancellationToken ct)
    {
        var project = await access.EnsureCanManageAsync(projectId, ct);
        ProjectAccessService.EnsureNotArchived(project);

        var requeued = await db.Records
            .Where(r => r.ProjectId == projectId
                        && r.ReviewStatusValue == ReviewStatus.Approved
                        && r.DeliveryStatusValue == DeliveryStatus.Failed)
            .ExecuteUpdateAsync(s => s
                .SetProperty(r => r.DeliveryStatusValue, DeliveryStatus.Pending)
                .SetProperty(r => r.DeliveryNextRetryAt, (DateTimeOffset?)null)
                .SetProperty(r => r.DeliveryAttempts, 0), ct);
        return new { requeued };
    }
}
