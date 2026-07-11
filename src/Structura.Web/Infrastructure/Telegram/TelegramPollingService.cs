using Structura.Web.Infrastructure.Settings;

namespace Structura.Web.Infrastructure.Telegram;

/// <summary>
/// Long-polls Telegram for updates when the bot is configured in polling mode (docs/06).
/// Polling suits dev / restricted networks that can't receive webhooks. No-op when the bot
/// is unconfigured or in webhook mode.
/// </summary>
public sealed class TelegramPollingService(
    IServiceScopeFactory scopeFactory,
    TelegramApiClient api,
    ILogger<TelegramPollingService> logger) : BackgroundService
{
    private long _offset;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                string? token;
                string? mode;
                using (var scope = scopeFactory.CreateScope())
                {
                    var settings = scope.ServiceProvider.GetRequiredService<AppSettingsService>();
                    token = await settings.GetAsync(AppSettingsService.TelegramBotToken, stoppingToken);
                    mode = await settings.GetAsync(AppSettingsService.TelegramMode, stoppingToken);
                }

                if (string.IsNullOrWhiteSpace(token) || mode != "polling")
                {
                    await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
                    continue;
                }

                var updates = await api.GetUpdatesAsync(token, _offset, 25, stoppingToken);
                if (updates is null)
                {
                    await Task.Delay(TimeSpan.FromSeconds(3), stoppingToken);
                    continue;
                }

                foreach (var update in updates)
                {
                    if (!TelegramUpdateParser.TryExtractMessage(update, out var message))
                    {
                        var id = update?["update_id"]?.GetValue<long>();
                        if (id is not null) _offset = Math.Max(_offset, id.Value + 1);
                        continue;
                    }
                    _offset = Math.Max(_offset, message.UpdateId + 1);
                    await DispatchAsync(message, stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                // shutting down
            }
            catch (Exception e)
            {
                logger.LogError(e, "Telegram polling loop failure");
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
        }
    }

    private async Task DispatchAsync(IncomingMessage message, CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var handler = scope.ServiceProvider.GetRequiredService<TelegramUpdateHandler>();
        await handler.HandleMessageAsync(message.ChatId, message.FromId, message.Username, message.Text, ct);
    }
}
