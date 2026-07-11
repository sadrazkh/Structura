using System.Security.Cryptography;
using FluentValidation;
using Structura.Web.Infrastructure.Errors;
using Structura.Web.Infrastructure.Secrets;
using Structura.Web.Infrastructure.Settings;
using Structura.Web.Infrastructure.Telegram;
using Structura.Web.Infrastructure.Validation;

namespace Structura.Web.Features.Settings;

public sealed record UpdateSettingsRequest(
    string? PublicBaseUrl, string TelegramMode, string? TelegramBotToken);

public sealed class UpdateSettingsRequestValidator : AbstractValidator<UpdateSettingsRequest>
{
    public UpdateSettingsRequestValidator()
    {
        RuleFor(x => x.TelegramMode).Must(m => m is "webhook" or "polling")
            .WithMessage("Telegram mode must be webhook or polling.");
        RuleFor(x => x.PublicBaseUrl)
            .Must(u => string.IsNullOrWhiteSpace(u) || Uri.TryCreate(u, UriKind.Absolute, out _))
            .WithMessage("Public base URL must be a valid absolute URL.");
    }
}

public static class SettingsEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/settings").RequireAuthorization("Administrator");
        group.MapGet("/", GetAsync);
        group.MapPut("/", UpdateAsync).Validate<UpdateSettingsRequest>();
        group.MapPost("/telegram/set-webhook", SetWebhookAsync);
        group.MapPost("/telegram/test", TestAsync);
    }

    private static async Task<object> GetAsync(AppSettingsService settings, ISecretProtector secrets, CancellationToken ct)
    {
        var token = await settings.GetAsync(AppSettingsService.TelegramBotToken, ct);
        return new
        {
            publicBaseUrl = await settings.GetAsync(AppSettingsService.PublicBaseUrl, ct) ?? "",
            telegramMode = await settings.GetAsync(AppSettingsService.TelegramMode, ct) ?? "polling",
            telegramBotTokenMasked = string.IsNullOrEmpty(token) ? "" : SecretProtector.Mask(token),
            telegramConfigured = !string.IsNullOrWhiteSpace(token),
        };
    }

    private static async Task<IResult> UpdateAsync(
        UpdateSettingsRequest request, AppSettingsService settings, CancellationToken ct)
    {
        await settings.SetAsync(AppSettingsService.PublicBaseUrl, request.PublicBaseUrl?.Trim().TrimEnd('/') ?? "", isProtected: false, ct);
        await settings.SetAsync(AppSettingsService.TelegramMode, request.TelegramMode, isProtected: false, ct);

        // Replace-only: a blank token keeps the stored one.
        if (!string.IsNullOrWhiteSpace(request.TelegramBotToken))
        {
            await settings.SetAsync(AppSettingsService.TelegramBotToken, request.TelegramBotToken.Trim(), isProtected: true, ct);
            // Ensure a webhook secret exists for later Set Webhook.
            if (string.IsNullOrEmpty(await settings.GetAsync(AppSettingsService.TelegramWebhookSecret, ct)))
                await settings.SetAsync(AppSettingsService.TelegramWebhookSecret,
                    Convert.ToHexString(RandomNumberGenerator.GetBytes(16)), isProtected: false, ct);
        }
        return Results.NoContent();
    }

    private static async Task<object> TestAsync(AppSettingsService settings, TelegramApiClient api, CancellationToken ct)
    {
        var token = await settings.GetAsync(AppSettingsService.TelegramBotToken, ct);
        if (string.IsNullOrWhiteSpace(token))
            throw new ConflictException("configuration_incomplete", "Save a bot token first.");
        var (ok, username, error) = await api.GetMeAsync(token, ct);
        return new { ok, username, error };
    }

    private static async Task<object> SetWebhookAsync(AppSettingsService settings, TelegramApiClient api, CancellationToken ct)
    {
        var token = await settings.GetAsync(AppSettingsService.TelegramBotToken, ct);
        var baseUrl = await settings.GetAsync(AppSettingsService.PublicBaseUrl, ct);
        var secret = await settings.GetAsync(AppSettingsService.TelegramWebhookSecret, ct);
        if (string.IsNullOrWhiteSpace(token)) throw new ConflictException("configuration_incomplete", "Save a bot token first.");
        if (string.IsNullOrWhiteSpace(baseUrl)) throw new ConflictException("configuration_incomplete", "Set the public base URL first.");
        if (string.IsNullOrWhiteSpace(secret))
        {
            secret = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
            await settings.SetAsync(AppSettingsService.TelegramWebhookSecret, secret, isProtected: false, ct);
        }

        var webhookUrl = $"{baseUrl.TrimEnd('/')}/api/telegram/webhook/{secret}";
        var result = await api.SetWebhookAsync(token, webhookUrl, secret, ct);
        return new { ok = result.Ok, webhookUrl, error = result.Error };
    }
}
