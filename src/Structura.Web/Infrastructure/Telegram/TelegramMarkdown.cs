namespace Structura.Web.Infrastructure.Telegram;

public static class TelegramMarkdown
{
    // All characters Telegram requires escaping in MarkdownV2 message text.
    private static readonly char[] Reserved =
        ['_', '*', '[', ']', '(', ')', '~', '`', '>', '#', '+', '-', '=', '|', '{', '}', '.', '!'];

    /// <summary>Escapes user/content text for safe MarkdownV2 rendering (prevents formatting injection).</summary>
    public static string Escape(string? text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var sb = new System.Text.StringBuilder(text.Length + 16);
        foreach (var c in text)
        {
            if (Array.IndexOf(Reserved, c) >= 0) sb.Append('\\');
            sb.Append(c);
        }
        return sb.ToString();
    }
}
