using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Structura.Web.Domain;
using Structura.Web.Infrastructure.Auth;
using Structura.Web.Infrastructure.Errors;
using Structura.Web.Infrastructure.Http;
using Structura.Web.Infrastructure.Secrets;
using Structura.Web.Infrastructure.Validation;
using Structura.Web.Persistence;

namespace Structura.Web.Features.Imports;

/// <summary>Stored in projects.api_input_config (JSONB). Credentials are encrypted.</summary>
public sealed class ApiInputConfig
{
    [JsonPropertyName("url")] public string Url { get; set; } = "";
    [JsonPropertyName("method")] public string Method { get; set; } = "GET";
    [JsonPropertyName("headers")] public Dictionary<string, string> Headers { get; set; } = [];
    [JsonPropertyName("authType")] public string AuthType { get; set; } = "none"; // none | bearer | apiKey
    [JsonPropertyName("tokenProtected")] public string? TokenProtected { get; set; }
    [JsonPropertyName("apiKeyHeaderName")] public string ApiKeyHeaderName { get; set; } = "X-Api-Key";
    [JsonPropertyName("dataPath")] public string DataPath { get; set; } = "";
    [JsonPropertyName("idPath")] public string? IdPath { get; set; }
    [JsonPropertyName("textPath")] public string TextPath { get; set; } = "";
}

public sealed record UpdateApiInputRequest(
    string Url, string Method, Dictionary<string, string>? Headers,
    string AuthType, string? Token, string? ApiKeyHeaderName,
    string DataPath, string? IdPath, string TextPath);

public sealed class UpdateApiInputRequestValidator : AbstractValidator<UpdateApiInputRequest>
{
    // Hop-by-hop and identity-defining headers are never user-settable (docs/07 §7).
    private static readonly string[] ForbiddenHeaders =
        ["host", "content-length", "transfer-encoding", "connection", "authorization", "cookie"];

    public UpdateApiInputRequestValidator()
    {
        RuleFor(x => x.Url).NotEmpty().Must(u => Uri.TryCreate(u, UriKind.Absolute, out _))
            .WithMessage("URL must be a valid absolute URL.");
        RuleFor(x => x.Method).Must(m => m is "GET" or "POST").WithMessage("Method must be GET or POST.");
        RuleFor(x => x.AuthType).Must(t => t is "none" or "bearer" or "apiKey")
            .WithMessage("Auth type must be none, bearer, or apiKey.");
        RuleFor(x => x.TextPath).NotEmpty();
        RuleFor(x => x.Headers).Must(h => h is null || !h.Keys.Any(k => ForbiddenHeaders.Contains(k.ToLowerInvariant())))
            .WithMessage("Headers may not include Host, Content-Length, Transfer-Encoding, Connection, Authorization, or Cookie.");
    }
}

public static class ApiInputEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/projects/{projectId:guid}/api-input").RequireAuthorization();
        group.MapGet("/", GetAsync);
        group.MapPut("/", UpdateAsync).Validate<UpdateApiInputRequest>();
        group.MapPost("/test", (Guid projectId, ProjectAccessService access, AppDbContext db,
                ISecretProtector secrets, SafeHttpClientFactory safeHttp, ICurrentUser user, CancellationToken ct) =>
            FetchAsync(projectId, access, db, secrets, safeHttp, user, insert: false, ct));
        group.MapPost("/fetch", (Guid projectId, ProjectAccessService access, AppDbContext db,
                ISecretProtector secrets, SafeHttpClientFactory safeHttp, ICurrentUser user, CancellationToken ct) =>
            FetchAsync(projectId, access, db, secrets, safeHttp, user, insert: true, ct));
    }

    private static async Task<object> GetAsync(
        Guid projectId, ProjectAccessService access, CancellationToken ct)
    {
        var project = await access.EnsureCanManageAsync(projectId, ct);
        var config = Parse(project.ApiInputConfig);
        return new
        {
            configured = config is not null,
            url = config?.Url ?? "",
            method = config?.Method ?? "GET",
            headers = config?.Headers ?? [],
            authType = config?.AuthType ?? "none",
            hasToken = config?.TokenProtected is not null,
            apiKeyHeaderName = config?.ApiKeyHeaderName ?? "X-Api-Key",
            dataPath = config?.DataPath ?? "",
            idPath = config?.IdPath ?? "",
            textPath = config?.TextPath ?? "",
        };
    }

    private static async Task<IResult> UpdateAsync(
        Guid projectId, UpdateApiInputRequest request, ProjectAccessService access,
        AppDbContext db, ISecretProtector secrets, SafeHttpClientFactory safeHttp, CancellationToken ct)
    {
        var project = await access.EnsureCanManageAsync(projectId, ct);
        ProjectAccessService.EnsureNotArchived(project);

        // Fail fast on unsafe destinations at save time (vetted again on every fetch).
        await safeHttp.ValidateAsync(new Uri(request.Url), SafeHttpProfile.Connector, ct);

        var existing = Parse(project.ApiInputConfig);
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

        project.ApiInputConfig = JsonSerializer.Serialize(new ApiInputConfig
        {
            Url = request.Url.Trim(),
            Method = request.Method,
            Headers = request.Headers ?? [],
            AuthType = request.AuthType,
            TokenProtected = tokenProtected,
            ApiKeyHeaderName = string.IsNullOrWhiteSpace(request.ApiKeyHeaderName) ? "X-Api-Key" : request.ApiKeyHeaderName.Trim(),
            DataPath = request.DataPath.Trim(),
            IdPath = string.IsNullOrWhiteSpace(request.IdPath) ? null : request.IdPath.Trim(),
            TextPath = request.TextPath.Trim(),
        }, SchemaDocument.JsonOptions);
        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }

    private static async Task<object> FetchAsync(
        Guid projectId, ProjectAccessService access, AppDbContext db, ISecretProtector secrets,
        SafeHttpClientFactory safeHttp, ICurrentUser currentUser, bool insert, CancellationToken ct)
    {
        var project = await access.EnsureCanManageAsync(projectId, ct);
        if (insert) ProjectAccessService.EnsureNotArchived(project);
        var config = Parse(project.ApiInputConfig)
                     ?? throw new ConflictException("configuration_incomplete", "Save the API input configuration first.");

        using var request = new HttpRequestMessage(new HttpMethod(config.Method), config.Url);
        foreach (var (key, value) in config.Headers)
            request.Headers.TryAddWithoutValidation(key, value);
        if (config.AuthType == "bearer" && config.TokenProtected is not null)
            request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {secrets.Unprotect(config.TokenProtected)}");
        else if (config.AuthType == "apiKey" && config.TokenProtected is not null)
            request.Headers.TryAddWithoutValidation(config.ApiKeyHeaderName, secrets.Unprotect(config.TokenProtected));

        string body;
        int statusCode;
        try
        {
            using var response = await safeHttp.SendAsync(request, SafeHttpProfile.Connector,
                TimeSpan.FromSeconds(30), ct);
            statusCode = (int)response.StatusCode;
            body = await SafeHttpClientFactory.ReadBodyCappedAsync(response, SafeHttpProfile.Connector, ct);
            if (!response.IsSuccessStatusCode)
                return new { ok = false, statusCode, error = $"The API returned HTTP {statusCode}.", items = Array.Empty<object>() };
        }
        catch (HttpRequestException e)
        {
            return new { ok = false, statusCode = 0, error = $"The API is unreachable: {e.Message}", items = Array.Empty<object>() };
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            return new { ok = false, statusCode = 0, error = "The API request timed out.", items = Array.Empty<object>() };
        }

        JsonNode? root;
        try { root = JsonNode.Parse(body); }
        catch (JsonException)
        {
            return new { ok = false, statusCode, error = "The API response is not valid JSON.", items = Array.Empty<object>() };
        }

        var dataNode = WalkPath(root, config.DataPath);
        if (dataNode is not JsonArray array)
            return new { ok = false, statusCode, error = $"Data path '{config.DataPath}' does not point to a JSON array.", items = Array.Empty<object>() };

        var mapped = new List<(string ExternalId, string Text)>();
        var mappingErrors = 0;
        var index = 0;
        foreach (var item in array)
        {
            index++;
            var text = AsScalarString(WalkPath(item, config.TextPath));
            if (string.IsNullOrEmpty(text)) { mappingErrors++; continue; }
            var id = config.IdPath is null
                ? $"API-{Guid.CreateVersion7().ToString("N")[..10].ToUpperInvariant()}"
                : AsScalarString(WalkPath(item, config.IdPath));
            if (string.IsNullOrEmpty(id)) { mappingErrors++; continue; }
            mapped.Add((id, text));
        }

        var mappedIds = mapped.Select(m => m.ExternalId).Distinct().ToList();
        var existingIds = (await db.Records
                .Where(r => r.ProjectId == projectId && mappedIds.Contains(r.ExternalId))
                .Select(r => r.ExternalId).ToListAsync(ct))
            .ToHashSet(StringComparer.Ordinal);

        if (!insert)
        {
            return new
            {
                ok = true, statusCode, error = (string?)null,
                totalItems = array.Count, mappingErrors,
                alreadyExisting = mapped.Count(m => existingIds.Contains(m.ExternalId)),
                items = mapped.Take(10).Select(m => new
                {
                    externalId = m.ExternalId,
                    textExcerpt = m.Text.Length > 200 ? m.Text[..200] + "…" : m.Text,
                    duplicate = existingIds.Contains(m.ExternalId),
                }),
            };
        }

        var run = new ImportRun
        {
            ProjectId = projectId, Source = ImportSource.Api, Status = ImportStatus.Completed,
            CreatedById = currentUser.Id, TotalRows = array.Count, Failed = mappingErrors,
            FinishedAt = DateTimeOffset.UtcNow,
        };
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (externalId, text) in mapped)
        {
            if (!seen.Add(externalId) || existingIds.Contains(externalId))
            {
                run.SkippedDuplicates++;
                continue;
            }
            db.Records.Add(new Record
            {
                ProjectId = projectId, ExternalId = externalId, Text = text, ImportRunId = run.Id,
            });
            run.Imported++;
        }
        if (run.Failed > 0)
        {
            run.Status = ImportStatus.CompletedWithErrors;
            Infrastructure.Import.ImportWorker.AppendError(run, 0, $"{mappingErrors} items had missing ID or text.");
        }
        db.ImportRuns.Add(run);
        await db.SaveChangesAsync(ct);

        return new
        {
            ok = true, statusCode, error = (string?)null,
            totalItems = array.Count, mappingErrors,
            imported = run.Imported, skippedDuplicates = run.SkippedDuplicates, runId = run.Id,
        };
    }

    private static string? AsScalarString(JsonNode? node) =>
        node is JsonValue value ? value.ToString().Trim() : null;

    /// <summary>Walks a dot-separated path ("data.items" / "" = root) through a JsonNode.</summary>
    internal static JsonNode? WalkPath(JsonNode? node, string path)
    {
        if (string.IsNullOrEmpty(path)) return node;
        foreach (var segment in path.Split('.', StringSplitOptions.RemoveEmptyEntries))
        {
            node = node?[segment];
            if (node is null) return null;
        }
        return node;
    }

    private static ApiInputConfig? Parse(string? json) =>
        string.IsNullOrWhiteSpace(json)
            ? null
            : JsonSerializer.Deserialize<ApiInputConfig>(json, SchemaDocument.JsonOptions);
}
