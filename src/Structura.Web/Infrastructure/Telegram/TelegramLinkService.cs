using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Structura.Web.Domain;
using Structura.Web.Infrastructure.Errors;
using Structura.Web.Persistence;

namespace Structura.Web.Infrastructure.Telegram;

public sealed record LinkResult(bool Ok, string Message);

/// <summary>Account-linking logic (docs/06): hashed one-time codes, takeover prevention, revocation.</summary>
public sealed class TelegramLinkService(AppDbContext db, ILogger<TelegramLinkService> logger)
{
    // Crockford base32 without ambiguous characters (no I, L, O, U).
    private const string Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";
    private static readonly TimeSpan CodeTtl = TimeSpan.FromMinutes(10);
    private const int MaxCodesPerHour = 5;

    /// <summary>Generates a fresh linking code for the user; returns the plaintext code (shown once).</summary>
    public async Task<string> GenerateCodeAsync(Guid userId, CancellationToken ct)
    {
        var hourAgo = DateTimeOffset.UtcNow.AddHours(-1);
        var recent = await db.TelegramLinkCodes.CountAsync(c => c.UserId == userId && c.CreatedAt >= hourAgo, ct);
        if (recent >= MaxCodesPerHour)
            throw new AppException(429, "rate_limited", "Too many link codes requested. Try again later.");

        var code = GenerateCode();
        db.TelegramLinkCodes.Add(new TelegramLinkCode
        {
            UserId = userId,
            CodeHash = Hash(code),
            ExpiresAt = DateTimeOffset.UtcNow.Add(CodeTtl),
        });
        await db.SaveChangesAsync(ct);
        return code;
    }

    /// <summary>Consumes a code (from /start) and binds the Telegram account. Idempotent-safe messages.</summary>
    public async Task<LinkResult> LinkAsync(string code, long telegramUserId, string? telegramUsername, CancellationToken ct)
    {
        var hash = Hash(code.Trim().ToUpperInvariant());
        var now = DateTimeOffset.UtcNow;

        var codeRow = await db.TelegramLinkCodes.FirstOrDefaultAsync(c => c.CodeHash == hash, ct);
        if (codeRow is null || codeRow.UsedAt is not null || codeRow.ExpiresAt <= now)
            return new LinkResult(false, "This linking code is invalid or has expired. Generate a new one in the app.");

        // Takeover prevention: the Telegram account must not already be actively linked elsewhere.
        var existingForTelegram = await db.TelegramLinks
            .FirstOrDefaultAsync(l => l.TelegramUserId == telegramUserId && l.Status == TelegramLinkStatus.Active, ct);
        if (existingForTelegram is not null && existingForTelegram.UserId != codeRow.UserId)
            return new LinkResult(false, "This Telegram account is already linked to another user. Unlink it first.");

        var existingForUser = await db.TelegramLinks
            .FirstOrDefaultAsync(l => l.UserId == codeRow.UserId, ct);
        if (existingForUser is not null && existingForUser.Status == TelegramLinkStatus.Active
            && existingForUser.TelegramUserId != telegramUserId)
            return new LinkResult(false, "Your account is already linked to a different Telegram account. Unlink it first.");

        codeRow.UsedAt = now;

        if (existingForUser is not null)
        {
            // Re-activate / re-point the user's existing link row (unique on UserId).
            existingForUser.TelegramUserId = telegramUserId;
            existingForUser.TelegramUsername = telegramUsername;
            existingForUser.Status = TelegramLinkStatus.Active;
            existingForUser.LinkedAt = now;
            existingForUser.RevokedAt = null;
            existingForUser.RevokedById = null;
        }
        else
        {
            db.TelegramLinks.Add(new TelegramLink
            {
                UserId = codeRow.UserId,
                TelegramUserId = telegramUserId,
                TelegramUsername = telegramUsername,
                Status = TelegramLinkStatus.Active,
                LinkedAt = now,
            });
        }
        await db.SaveChangesAsync(ct);
        logger.LogInformation("Telegram account {TgId} linked to user {UserId}", telegramUserId, codeRow.UserId);
        return new LinkResult(true, "✅ Your account is linked. You will now receive review notifications here.");
    }

    public async Task UnlinkAsync(Guid userId, Guid? revokedById, CancellationToken ct)
    {
        var link = await db.TelegramLinks.FirstOrDefaultAsync(l => l.UserId == userId, ct);
        if (link is null || link.Status == TelegramLinkStatus.Revoked) return;
        link.Status = TelegramLinkStatus.Revoked;
        link.RevokedAt = DateTimeOffset.UtcNow;
        link.RevokedById = revokedById;
        await db.SaveChangesAsync(ct);
    }

    private static string GenerateCode()
    {
        var bytes = RandomNumberGenerator.GetBytes(8);
        var chars = new char[8];
        for (var i = 0; i < 8; i++) chars[i] = Alphabet[bytes[i] % Alphabet.Length];
        return new string(chars);
    }

    private static string Hash(string code) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(code)));
}
