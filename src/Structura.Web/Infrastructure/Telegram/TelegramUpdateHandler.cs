using System.Text;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Structura.Web.Domain;
using Structura.Web.Infrastructure.Settings;
using Structura.Web.Persistence;

namespace Structura.Web.Infrastructure.Telegram;

/// <summary>Processes one incoming bot update (webhook or polling). Notification + entry point only.</summary>
public sealed class TelegramUpdateHandler(
    AppDbContext db,
    TelegramApiClient api,
    TelegramLinkService linkService,
    AppSettingsService settings)
{
    public async Task HandleMessageAsync(long chatId, long fromId, string? username, string text, CancellationToken ct)
    {
        var botToken = await settings.GetAsync(AppSettingsService.TelegramBotToken, ct);
        if (string.IsNullOrWhiteSpace(botToken)) return;

        var command = text.Trim();
        var (verb, payload) = SplitCommand(command);

        var reply = verb switch
        {
            "/start" when !string.IsNullOrWhiteSpace(payload) => await HandleStartWithCodeAsync(payload!, fromId, username, ct),
            "/start" => Greeting(),
            "/tasks" => await HandleTasksAsync(fromId, ct),
            "/next" => await HandleNextAsync(fromId, ct),
            "/help" => HelpText(),
            _ => "Unknown command. Send /help to see what I can do.",
        };

        var keyboard = await BuildKeyboardAsync(verb, fromId, ct);
        await api.SendMessageAsync(botToken, chatId, reply, keyboard, ct);
    }

    private async Task<string> HandleStartWithCodeAsync(string code, long fromId, string? username, CancellationToken ct)
    {
        var result = await linkService.LinkAsync(code, fromId, username, ct);
        return TelegramMarkdown.Escape(result.Message);
    }

    private async Task<string> HandleTasksAsync(long fromId, CancellationToken ct)
    {
        var user = await ResolveUserAsync(fromId, ct);
        if (user is null) return LinkInstructions();

        var perProject = await db.Records.AsNoTracking()
            .Where(r => r.AssignedReviewerId == user.Value
                        && (r.ReviewStatusValue == ReviewStatus.Assigned || r.ReviewStatusValue == ReviewStatus.InReview))
            .GroupBy(r => r.Project.Name)
            .Select(g => new { Project = g.Key, Count = g.Count() })
            .ToListAsync(ct);

        if (perProject.Count == 0) return "You're all caught up 🎉 No records waiting for review\\.";

        var sb = new StringBuilder("*Your pending reviews:*\n");
        foreach (var p in perProject)
            sb.AppendLine($"• {TelegramMarkdown.Escape(p.Project)}: *{p.Count}*");
        return sb.ToString().TrimEnd();
    }

    private async Task<string> HandleNextAsync(long fromId, CancellationToken ct)
    {
        var user = await ResolveUserAsync(fromId, ct);
        if (user is null) return LinkInstructions();

        var pending = await db.Records.AsNoTracking()
            .CountAsync(r => r.AssignedReviewerId == user.Value
                             && (r.ReviewStatusValue == ReviewStatus.Assigned || r.ReviewStatusValue == ReviewStatus.InReview), ct);
        return pending == 0
            ? "Nothing to review right now\\."
            : $"You have *{pending}* record\\(s\\) waiting\\. Tap the button below to open the review app\\.";
    }

    private async Task<JsonArray?> BuildKeyboardAsync(string verb, long fromId, CancellationToken ct)
    {
        if (verb is not ("/tasks" or "/next" or "/start")) return null;
        var user = await ResolveUserAsync(fromId, ct);
        if (user is null) return null;
        var baseUrl = await settings.GetAsync(AppSettingsService.PublicBaseUrl, ct);
        if (string.IsNullOrWhiteSpace(baseUrl)) return null;

        // A URL button opens the reviewer PWA; inside Telegram, /tg boots the Mini App adapter.
        return
        [
            new JsonArray(new JsonObject { ["text"] = "📋 Open review app", ["url"] = $"{baseUrl.TrimEnd('/')}/tg" }),
        ];
    }

    private async Task<Guid?> ResolveUserAsync(long telegramUserId, CancellationToken ct) =>
        await db.TelegramLinks.AsNoTracking()
            .Where(l => l.TelegramUserId == telegramUserId && l.Status == TelegramLinkStatus.Active)
            .Select(l => (Guid?)l.UserId)
            .FirstOrDefaultAsync(ct);

    private static (string Verb, string? Payload) SplitCommand(string text)
    {
        var space = text.IndexOf(' ');
        if (space < 0) return (StripBotMention(text), null);
        return (StripBotMention(text[..space]), text[(space + 1)..].Trim());
    }

    // "/start@MyBot" → "/start"
    private static string StripBotMention(string verb)
    {
        var at = verb.IndexOf('@');
        return at < 0 ? verb : verb[..at];
    }

    private static string Greeting() =>
        "👋 Welcome to *Structura*\\.\nTo receive review notifications, open the app, go to Settings → Telegram, generate a code, and send it here as `/start YOURCODE`\\.";

    private static string LinkInstructions() =>
        "Your Telegram account isn't linked yet\\. Open Structura → Settings → Telegram, generate a code, and send `/start YOURCODE` here\\.";

    private static string HelpText() =>
        "*Structura bot*\n" +
        "/tasks — how many records are waiting for you\n" +
        "/next — open the next record to review\n" +
        "/help — this message\n\n" +
        "Reviewing happens in the app; this bot just notifies you and opens it\\.";
}
