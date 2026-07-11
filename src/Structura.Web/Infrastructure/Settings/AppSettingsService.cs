using Microsoft.EntityFrameworkCore;
using Structura.Web.Domain;
using Structura.Web.Infrastructure.Secrets;
using Structura.Web.Persistence;

namespace Structura.Web.Infrastructure.Settings;

/// <summary>Typed accessor over the app_settings key/value table, with encryption for secrets.</summary>
public sealed class AppSettingsService(AppDbContext db, ISecretProtector secrets)
{
    public const string TelegramBotToken = "telegram.botToken";
    public const string TelegramMode = "telegram.mode";                 // webhook | polling
    public const string TelegramWebhookSecret = "telegram.webhookSecret";
    public const string PublicBaseUrl = "general.publicBaseUrl";

    public async Task<string?> GetAsync(string key, CancellationToken ct = default)
    {
        var setting = await db.AppSettings.AsNoTracking().FirstOrDefaultAsync(s => s.Key == key, ct);
        if (setting is null) return null;
        return setting.IsProtected ? secrets.Unprotect(setting.Value) : setting.Value;
    }

    public async Task SetAsync(string key, string value, bool isProtected, CancellationToken ct = default)
    {
        var setting = await db.AppSettings.FirstOrDefaultAsync(s => s.Key == key, ct);
        var stored = isProtected ? secrets.Protect(value) : value;
        if (setting is null)
        {
            db.AppSettings.Add(new AppSetting { Key = key, Value = stored, IsProtected = isProtected, UpdatedAt = DateTimeOffset.UtcNow });
        }
        else
        {
            setting.Value = stored;
            setting.IsProtected = isProtected;
            setting.UpdatedAt = DateTimeOffset.UtcNow;
        }
        await db.SaveChangesAsync(ct);
    }
}
