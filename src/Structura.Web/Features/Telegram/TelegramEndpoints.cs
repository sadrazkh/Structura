using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Structura.Web.Domain;
using Structura.Web.Features.Auth;
using Structura.Web.Infrastructure.Auth;
using Structura.Web.Infrastructure.Errors;
using Structura.Web.Infrastructure.Settings;
using Structura.Web.Infrastructure.Telegram;
using Structura.Web.Persistence;

namespace Structura.Web.Features.Telegram;

public sealed record MiniAppAuthRequest(string InitData);

public static class TelegramEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        var self = app.MapGroup("/api/telegram").RequireAuthorization();
        self.MapPost("/link-code", GenerateCodeAsync);
        self.MapGet("/link", GetLinkAsync);
        self.MapDelete("/link", UnlinkAsync);

        // Webhook: anonymous but authenticated by the secret path + secret-token header.
        app.MapPost("/api/telegram/webhook/{secret}", WebhookAsync).AllowAnonymous();

        // Mini App bootstrap: exchange Telegram initData for a normal JWT pair.
        app.MapPost("/api/auth/telegram-miniapp", MiniAppAuthAsync).AllowAnonymous();
    }

    private static async Task<object> GenerateCodeAsync(
        TelegramLinkService links, ICurrentUser currentUser, AppSettingsService settings, CancellationToken ct)
    {
        var code = await links.GenerateCodeAsync(currentUser.Id, ct);
        var mode = await settings.GetAsync(AppSettingsService.TelegramMode, ct);
        return new
        {
            code,
            expiresInMinutes = 10,
            botConfigured = !string.IsNullOrWhiteSpace(await settings.GetAsync(AppSettingsService.TelegramBotToken, ct)),
            mode,
        };
    }

    private static async Task<object> GetLinkAsync(AppDbContext db, ICurrentUser currentUser, CancellationToken ct)
    {
        var link = await db.TelegramLinks.AsNoTracking()
            .FirstOrDefaultAsync(l => l.UserId == currentUser.Id && l.Status == TelegramLinkStatus.Active, ct);
        return new
        {
            linked = link is not null,
            telegramUsername = link?.TelegramUsername,
            linkedAt = link?.LinkedAt,
        };
    }

    private static async Task<IResult> UnlinkAsync(
        TelegramLinkService links, ICurrentUser currentUser, CancellationToken ct)
    {
        await links.UnlinkAsync(currentUser.Id, currentUser.Id, ct);
        return Results.NoContent();
    }

    private static async Task<IResult> WebhookAsync(
        string secret, HttpContext http, [FromBody] System.Text.Json.JsonElement update,
        AppSettingsService settings, IServiceScopeFactory scopeFactory, CancellationToken ct)
    {
        var expectedSecret = await settings.GetAsync(AppSettingsService.TelegramWebhookSecret, ct);
        var headerToken = http.Request.Headers["X-Telegram-Bot-Api-Secret-Token"].ToString();
        // Both the secret path segment and the secret-token header must match — else 404 (no leak).
        if (string.IsNullOrEmpty(expectedSecret) || secret != expectedSecret || headerToken != expectedSecret)
            return Results.NotFound();

        var node = System.Text.Json.Nodes.JsonNode.Parse(update.GetRawText());
        if (TelegramUpdateParser.TryExtractMessage(node, out var message))
        {
            // Process without blocking Telegram's retry timer; return 200 immediately.
            // A fresh scope is required — the request scope (and its DbContext) is disposed on return.
            _ = Task.Run(async () =>
            {
                using var scope = scopeFactory.CreateScope();
                var handler = scope.ServiceProvider.GetRequiredService<TelegramUpdateHandler>();
                await handler.HandleMessageAsync(
                    message.ChatId, message.FromId, message.Username, message.Text, CancellationToken.None);
            });
        }
        return Results.Ok();
    }

    private static async Task<IResult> MiniAppAuthAsync(
        MiniAppAuthRequest request, AppSettingsService settings, AppDbContext db,
        JwtTokenService tokens, CancellationToken ct)
    {
        var botToken = await settings.GetAsync(AppSettingsService.TelegramBotToken, ct);
        if (string.IsNullOrWhiteSpace(botToken))
            throw new AppException(StatusCodes.Status503ServiceUnavailable, "telegram_not_configured",
                "Telegram is not configured on this installation.");

        if (!MiniAppAuth.TryValidate(request.InitData, botToken, TimeSpan.FromHours(24), out var tgUser) || tgUser is null)
            throw new UnauthorizedException("invalid_init_data", "Invalid or expired Telegram sign-in.");

        var link = await db.TelegramLinks
            .FirstOrDefaultAsync(l => l.TelegramUserId == tgUser.TelegramUserId && l.Status == TelegramLinkStatus.Active, ct);
        if (link is null)
            throw new UnauthorizedException("not_linked",
                "This Telegram account is not linked to a Structura user. Link it from the app first.");

        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == link.UserId && u.IsActive, ct);
        if (user is null)
            throw new UnauthorizedException("not_linked", "The linked account is no longer active.");

        user.LastLoginAt = DateTimeOffset.UtcNow;
        var response = await AuthEndpoints.IssueTokensAsync(db, tokens, user, ct);
        return Results.Ok(response);
    }
}
