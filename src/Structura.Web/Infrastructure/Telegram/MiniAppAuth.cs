using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Web;

namespace Structura.Web.Infrastructure.Telegram;

public sealed record MiniAppUser(long TelegramUserId, string? Username, string? FullName);

/// <summary>
/// Validates Telegram Mini App <c>initData</c> per the official algorithm:
/// secret = HMAC_SHA256("WebAppData", botToken); expected = HMAC_SHA256(secret, dataCheckString).
/// </summary>
public static class MiniAppAuth
{
    public static bool TryValidate(
        string initData, string botToken, TimeSpan maxAge, out MiniAppUser? user)
    {
        user = null;
        if (string.IsNullOrWhiteSpace(initData)) return false;

        // Parse the query-string-encoded initData into key/value pairs.
        var pairs = HttpUtility.ParseQueryString(initData);
        var providedHash = pairs["hash"];
        if (string.IsNullOrEmpty(providedHash)) return false;

        // Build the data-check-string: all keys except "hash", sorted, joined by \n.
        var dataCheckString = string.Join('\n', pairs.AllKeys
            .Where(k => k is not null && k != "hash")
            .OrderBy(k => k, StringComparer.Ordinal)
            .Select(k => $"{k}={pairs[k]}"));

        var secretKey = HMACSHA256.HashData(Encoding.UTF8.GetBytes("WebAppData"), Encoding.UTF8.GetBytes(botToken));
        var expectedHash = HMACSHA256.HashData(secretKey, Encoding.UTF8.GetBytes(dataCheckString));
        var expectedHex = Convert.ToHexString(expectedHash);

        if (!CryptographicOperations.FixedTimeEquals(
                Convert.FromHexString(expectedHex), HexOrEmpty(providedHash)))
            return false;

        // auth_date freshness.
        if (!long.TryParse(pairs["auth_date"], out var authUnix)) return false;
        var authTime = DateTimeOffset.FromUnixTimeSeconds(authUnix);
        if (DateTimeOffset.UtcNow - authTime > maxAge) return false;

        var userJson = pairs["user"];
        if (string.IsNullOrEmpty(userJson)) return false;
        try
        {
            using var doc = JsonDocument.Parse(userJson);
            var root = doc.RootElement;
            var id = root.GetProperty("id").GetInt64();
            var username = root.TryGetProperty("username", out var u) ? u.GetString() : null;
            var first = root.TryGetProperty("first_name", out var f) ? f.GetString() : null;
            var last = root.TryGetProperty("last_name", out var l) ? l.GetString() : null;
            var fullName = string.Join(' ', new[] { first, last }.Where(s => !string.IsNullOrWhiteSpace(s)));
            user = new MiniAppUser(id, username, string.IsNullOrWhiteSpace(fullName) ? null : fullName);
            return true;
        }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            return false;
        }
    }

    private static byte[] HexOrEmpty(string hex)
    {
        try { return Convert.FromHexString(hex); }
        catch (FormatException) { return []; }
    }
}
