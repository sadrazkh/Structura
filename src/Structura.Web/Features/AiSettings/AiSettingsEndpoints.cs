using FluentValidation;
using Structura.Web.Domain;
using Structura.Web.Infrastructure.Ai;
using Structura.Web.Infrastructure.Auth;
using Structura.Web.Infrastructure.Errors;
using Structura.Web.Infrastructure.Secrets;
using Structura.Web.Infrastructure.Validation;
using Structura.Web.Persistence;

namespace Structura.Web.Features.AiSettings;

public sealed record UpdateAiSettingsRequest(
    string Provider, string BaseUrl, string? ApiKey, string Model,
    double Temperature, int MaxOutputTokens, int TimeoutSeconds, int Concurrency,
    string SystemInstruction, string ExtractionInstruction);

public sealed record UpdatePromptRequest(string? SystemInstruction, string? ExtractionInstruction);

public sealed class UpdateAiSettingsRequestValidator : AbstractValidator<UpdateAiSettingsRequest>
{
    public UpdateAiSettingsRequestValidator()
    {
        RuleFor(x => x.Provider).Must(p => AiProviders.All.Contains(p))
            .WithMessage("Provider must be OpenRouter or Nvidia.");
        RuleFor(x => x.BaseUrl).NotEmpty().Must(url => Uri.TryCreate(url, UriKind.Absolute, out _))
            .WithMessage("Base URL must be a valid absolute URL.");
        RuleFor(x => x.Model).NotEmpty().MaximumLength(200);
        RuleFor(x => x.Temperature).InclusiveBetween(0, 2);
        RuleFor(x => x.MaxOutputTokens).InclusiveBetween(1, 32_000);
        RuleFor(x => x.TimeoutSeconds).InclusiveBetween(5, 300);
        RuleFor(x => x.Concurrency).InclusiveBetween(1, 16);
        RuleFor(x => x.SystemInstruction).MaximumLength(20_000);
        RuleFor(x => x.ExtractionInstruction).MaximumLength(20_000);
    }
}

public static class AiSettingsEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/projects/{projectId:guid}/ai-config").RequireAuthorization();
        group.MapGet("/", GetAsync);
        group.MapPut("/", UpdateAsync).Validate<UpdateAiSettingsRequest>();
        group.MapPost("/test", TestAsync);

        // Prompt-only update (used by the AI schema generator to persist suggested instructions
        // without touching the provider/key settings).
        app.MapPut("/api/projects/{projectId:guid}/prompt", UpdatePromptAsync).RequireAuthorization();
    }

    private static async Task<IResult> UpdatePromptAsync(
        Guid projectId, UpdatePromptRequest request, ProjectAccessService access,
        AppDbContext db, CancellationToken ct)
    {
        var project = await access.EnsureCanManageAsync(projectId, ct);
        ProjectAccessService.EnsureNotArchived(project);
        project.PromptConfig = new PromptConfigDocument
        {
            SystemInstruction = (request.SystemInstruction ?? "").Trim(),
            ExtractionInstruction = (request.ExtractionInstruction ?? "").Trim(),
        }.ToJson();
        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }

    private static async Task<object> GetAsync(
        Guid projectId, ProjectAccessService access, ISecretProtector secrets, CancellationToken ct)
    {
        var project = await access.EnsureCanManageAsync(projectId, ct);
        var config = AiConfigDocument.ParseOrNull(project.AiConfig);
        var prompt = PromptConfigDocument.Parse(project.PromptConfig);
        return new
        {
            configured = config is not null,
            provider = config?.Provider ?? AiProviders.OpenRouter,
            baseUrl = config?.BaseUrl ?? AiProviders.DefaultBaseUrl(AiProviders.OpenRouter),
            apiKeyMasked = config?.ApiKeyProtected is null
                ? ""
                : SecretProtector.Mask(secrets.Unprotect(config.ApiKeyProtected)),
            hasApiKey = config?.ApiKeyProtected is not null,
            model = config?.Model ?? "",
            temperature = config?.Temperature ?? 0.1,
            maxOutputTokens = config?.MaxOutputTokens ?? 2048,
            timeoutSeconds = config?.TimeoutSeconds ?? 60,
            concurrency = config?.Concurrency ?? 5,
            systemInstruction = prompt.SystemInstruction,
            extractionInstruction = prompt.ExtractionInstruction,
        };
    }

    private static async Task<IResult> UpdateAsync(
        Guid projectId, UpdateAiSettingsRequest request, ProjectAccessService access,
        AppDbContext db, ISecretProtector secrets, CancellationToken ct)
    {
        var project = await access.EnsureCanManageAsync(projectId, ct);
        ProjectAccessService.EnsureNotArchived(project);

        var existing = AiConfigDocument.ParseOrNull(project.AiConfig);

        // Replace-only secret semantics: null/empty ApiKey keeps the stored key.
        string? apiKeyProtected;
        if (!string.IsNullOrWhiteSpace(request.ApiKey))
            apiKeyProtected = secrets.Protect(request.ApiKey.Trim());
        else if (existing?.ApiKeyProtected is not null)
            apiKeyProtected = existing.ApiKeyProtected;
        else
            throw new ConflictException(ErrorCodes.ValidationFailed, "An API key is required.");

        project.AiConfig = new AiConfigDocument
        {
            Provider = request.Provider,
            BaseUrl = request.BaseUrl.Trim().TrimEnd('/'),
            ApiKeyProtected = apiKeyProtected,
            Model = request.Model.Trim(),
            Temperature = request.Temperature,
            MaxOutputTokens = request.MaxOutputTokens,
            TimeoutSeconds = request.TimeoutSeconds,
            Concurrency = request.Concurrency,
        }.ToJson();

        project.PromptConfig = new PromptConfigDocument
        {
            SystemInstruction = request.SystemInstruction.Trim(),
            ExtractionInstruction = request.ExtractionInstruction.Trim(),
        }.ToJson();

        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }

    private static async Task<object> TestAsync(
        Guid projectId, ProjectAccessService access, ISecretProtector secrets,
        OpenAiCompatibleClient aiClient, CancellationToken ct)
    {
        var project = await access.EnsureCanManageAsync(projectId, ct);
        var config = AiConfigDocument.ParseOrNull(project.AiConfig)
                     ?? throw new ConflictException("configuration_incomplete",
                         "Save the AI configuration before testing the connection.");
        if (config.ApiKeyProtected is null || string.IsNullOrWhiteSpace(config.Model))
            throw new ConflictException("configuration_incomplete",
                "An API key and a model are required before testing.");

        try
        {
            var result = await aiClient.CompleteAsync(
                config,
                secrets.Unprotect(config.ApiKeyProtected),
                [
                    new AiMessage("system", "You are a connection test. Reply with exactly: OK"),
                    new AiMessage("user", "ping"),
                ],
                responseFormat: null,
                maxTokensOverride: 20,
                ct);
            return new
            {
                ok = true,
                durationMs = result.DurationMs,
                inputTokens = result.InputTokens,
                outputTokens = result.OutputTokens,
                sample = result.Content?.Trim(),
            };
        }
        catch (AiProviderException e)
        {
            return new { ok = false, durationMs = 0, inputTokens = 0, outputTokens = 0, sample = (string?)null, error = e.Message };
        }
    }
}
