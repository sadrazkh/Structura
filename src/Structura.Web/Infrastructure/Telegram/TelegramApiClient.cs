using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Structura.Web.Infrastructure.Http;

namespace Structura.Web.Infrastructure.Telegram;

public sealed record TelegramSendResult(bool Ok, string? Error);

/// <summary>Overridable Telegram API base URL (tests point it at WireMock).</summary>
public sealed class TelegramApiOptions
{
    public string BaseUrl { get; set; } = "https://api.telegram.org";
}

/// <summary>
/// Thin Telegram Bot API client. Goes through SafeHttp so the outbound proxy (restricted
/// networks) applies. The base URL is configurable so tests can point at WireMock.
/// </summary>
public sealed class TelegramApiClient(SafeHttpClientFactory safeHttp, TelegramApiOptions options, ILogger<TelegramApiClient> logger)
{
    private string BaseUrl => options.BaseUrl.TrimEnd('/');

    public async Task<TelegramSendResult> SendMessageAsync(
        string botToken, long chatId, string text, JsonArray? inlineKeyboard, CancellationToken ct)
    {
        var payload = new JsonObject
        {
            ["chat_id"] = chatId,
            ["text"] = text,
            ["parse_mode"] = "MarkdownV2",
        };
        if (inlineKeyboard is not null)
            payload["reply_markup"] = new JsonObject { ["inline_keyboard"] = inlineKeyboard };

        return await CallAsync(botToken, "sendMessage", payload, ct);
    }

    public async Task<(bool Ok, string? Username, string? Error)> GetMeAsync(string botToken, CancellationToken ct)
    {
        var result = await CallRawAsync(botToken, "getMe", null, ct);
        if (!result.Ok) return (false, null, result.Error);
        var username = result.Body?["result"]?["username"]?.GetValue<string>();
        return (true, username, null);
    }

    public async Task<TelegramSendResult> SetWebhookAsync(
        string botToken, string url, string secretToken, CancellationToken ct)
    {
        var payload = new JsonObject
        {
            ["url"] = url,
            ["secret_token"] = secretToken,
            ["allowed_updates"] = new JsonArray("message", "callback_query"),
            ["drop_pending_updates"] = true,
        };
        return await CallAsync(botToken, "setWebhook", payload, ct);
    }

    public async Task<TelegramSendResult> DeleteWebhookAsync(string botToken, CancellationToken ct)
    {
        var result = await CallRawAsync(botToken, "deleteWebhook",
            new JsonObject { ["drop_pending_updates"] = false }, ct);
        return new TelegramSendResult(result.Ok, result.Error);
    }

    /// <summary>Long-poll for updates (polling mode). Returns the raw update array.</summary>
    public async Task<JsonArray?> GetUpdatesAsync(string botToken, long offset, int timeoutSeconds, CancellationToken ct)
    {
        var payload = new JsonObject
        {
            ["offset"] = offset,
            ["timeout"] = timeoutSeconds,
            ["allowed_updates"] = new JsonArray("message"),
        };
        // Add slack over the long-poll timeout for the HTTP read.
        var url = new Uri($"{BaseUrl}/bot{botToken}/getUpdates");
        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        try
        {
            using var response = await safeHttp.SendAsync(request, SafeHttpProfile.AiProvider,
                TimeSpan.FromSeconds(timeoutSeconds + 15), ct);
            var body = await SafeHttpClientFactory.ReadBodyCappedAsync(response, SafeHttpProfile.AiProvider, ct);
            var json = JsonNode.Parse(body) as JsonObject;
            return json?["ok"]?.GetValue<bool>() == true ? json["result"] as JsonArray : null;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            logger.LogWarning(e, "Telegram getUpdates failed");
            return null;
        }
    }

    private async Task<TelegramSendResult> CallAsync(
        string botToken, string method, JsonObject payload, CancellationToken ct)
    {
        var result = await CallRawAsync(botToken, method, payload, ct);
        return new TelegramSendResult(result.Ok, result.Error);
    }

    private async Task<(bool Ok, JsonObject? Body, string? Error)> CallRawAsync(
        string botToken, string method, JsonObject? payload, CancellationToken ct)
    {
        var url = new Uri($"{BaseUrl}/bot{botToken}/{method}");
        using var request = new HttpRequestMessage(HttpMethod.Post, url);
        if (payload is not null)
            request.Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json");

        try
        {
            // AiProvider profile: api.telegram.org is public; the profile still vets the host.
            using var response = await safeHttp.SendAsync(request, SafeHttpProfile.AiProvider,
                TimeSpan.FromSeconds(30), ct);
            var body = await SafeHttpClientFactory.ReadBodyCappedAsync(response, SafeHttpProfile.AiProvider, ct);
            var json = JsonNode.Parse(body) as JsonObject;
            if (json?["ok"]?.GetValue<bool>() == true)
                return (true, json, null);
            var description = json?["description"]?.GetValue<string>() ?? $"HTTP {(int)response.StatusCode}";
            return (false, null, description);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            logger.LogWarning(e, "Telegram API call {Method} failed", method);
            return (false, null, e.Message);
        }
    }
}
