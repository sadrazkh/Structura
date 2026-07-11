using Microsoft.EntityFrameworkCore;
using Structura.Web.Domain;
using Structura.Web.Infrastructure.Settings;
using Structura.Web.Persistence;

namespace Structura.Web.Infrastructure.Telegram;

/// <summary>
/// Best-effort Telegram notifications (docs/06). Never throws into the caller's transaction —
/// notification failure must not fail the operation that triggered it. Fire-and-forget via a
/// fresh scope so it runs after the triggering request completes.
/// </summary>
public sealed class TelegramNotifier(
    IServiceScopeFactory scopeFactory,
    ILogger<TelegramNotifier> logger)
{
    public void NotifyAssignment(Guid reviewerId, string projectName, int count) =>
        FireAndForget(reviewerId, async (api, token, chatId, baseUrl, ct) =>
        {
            var text = $"📋 You have *{count}* new record\\(s\\) to review in _{TelegramMarkdown.Escape(projectName)}_\\.";
            await api.SendMessageAsync(token, chatId, text, OpenAppButton(baseUrl), ct);
        });

    public void NotifyReprocessReady(Guid reviewerId, string projectName) =>
        FireAndForget(reviewerId, async (api, token, chatId, baseUrl, ct) =>
        {
            var text = $"↺ A record you sent for reprocessing in _{TelegramMarkdown.Escape(projectName)}_ is ready again\\.";
            await api.SendMessageAsync(token, chatId, text, OpenAppButton(baseUrl), ct);
        });

    public void NotifyRunFinished(Guid userId, string projectName, int succeeded, int failed) =>
        FireAndForget(userId, async (api, token, chatId, baseUrl, ct) =>
        {
            var text = $"✅ Processing run finished in _{TelegramMarkdown.Escape(projectName)}_: *{succeeded}* succeeded, *{failed}* failed\\.";
            await api.SendMessageAsync(token, chatId, text, null, ct);
        });

    private void FireAndForget(
        Guid userId,
        Func<TelegramApiClient, string, long, string?, CancellationToken, Task> send)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                var settings = scope.ServiceProvider.GetRequiredService<AppSettingsService>();
                var api = scope.ServiceProvider.GetRequiredService<TelegramApiClient>();

                var token = await settings.GetAsync(AppSettingsService.TelegramBotToken);
                if (string.IsNullOrWhiteSpace(token)) return;

                var link = await db.TelegramLinks.AsNoTracking()
                    .FirstOrDefaultAsync(l => l.UserId == userId && l.Status == TelegramLinkStatus.Active);
                if (link is null) return;

                var baseUrl = await settings.GetAsync(AppSettingsService.PublicBaseUrl);
                await send(api, token, link.TelegramUserId, baseUrl, CancellationToken.None);
            }
            catch (Exception e)
            {
                logger.LogWarning(e, "Telegram notification to user {UserId} failed", userId);
            }
        });
    }

    private static System.Text.Json.Nodes.JsonArray? OpenAppButton(string? baseUrl)
    {
        if (string.IsNullOrWhiteSpace(baseUrl)) return null;
        return
        [
            new System.Text.Json.Nodes.JsonArray(new System.Text.Json.Nodes.JsonObject
            {
                ["text"] = "📋 Open review app",
                ["url"] = $"{baseUrl.TrimEnd('/')}/tg",
            }),
        ];
    }
}
