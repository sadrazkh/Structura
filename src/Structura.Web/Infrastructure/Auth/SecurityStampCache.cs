using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Structura.Web.Persistence;

namespace Structura.Web.Infrastructure.Auth;

/// <summary>
/// Validates the token's security-stamp claim against the database so that deactivation,
/// password change, or reset kills existing sessions within the cache window (30 s).
/// </summary>
public sealed class SecurityStampCache(IMemoryCache cache)
{
    private static readonly TimeSpan Ttl = TimeSpan.FromSeconds(30);

    public async Task<bool> IsValidAsync(AppDbContext db, Guid userId, string stamp, CancellationToken ct)
    {
        var current = await cache.GetOrCreateAsync(CacheKey(userId), async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = Ttl;
            var user = await db.Users.AsNoTracking()
                .Where(u => u.Id == userId)
                .Select(u => new { u.SecurityStamp, u.IsActive })
                .FirstOrDefaultAsync(ct);
            return user is { IsActive: true } ? user.SecurityStamp : null;
        });
        return current is not null && current == stamp;
    }

    public void Invalidate(Guid userId) => cache.Remove(CacheKey(userId));

    private static string CacheKey(Guid userId) => $"sst:{userId}";
}
