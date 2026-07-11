using System.Text.Json.Nodes;

namespace Structura.Web.Infrastructure.Telegram;

public sealed record IncomingMessage(long UpdateId, long ChatId, long FromId, string? Username, string Text);

/// <summary>Extracts the fields we care about from a raw Telegram update (message with text).</summary>
public static class TelegramUpdateParser
{
    public static bool TryExtractMessage(JsonNode? update, out IncomingMessage message)
    {
        message = default!;
        if (update is null) return false;

        var updateId = update["update_id"]?.GetValue<long>() ?? 0;
        var msg = update["message"];
        var text = msg?["text"]?.GetValue<string>();
        var chatId = msg?["chat"]?["id"]?.GetValue<long>();
        var from = msg?["from"];
        var fromId = from?["id"]?.GetValue<long>();
        if (text is null || chatId is null || fromId is null) return false;

        message = new IncomingMessage(
            updateId, chatId.Value, fromId.Value,
            from?["username"]?.GetValue<string>(), text);
        return true;
    }
}
