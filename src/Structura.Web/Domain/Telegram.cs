namespace Structura.Web.Domain;

public static class TelegramLinkStatus
{
    public const string Active = "Active";
    public const string Revoked = "Revoked";
}

/// <summary>A confirmed binding between a Structura user and a Telegram account.</summary>
public class TelegramLink : IHasTimestamps
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid UserId { get; set; }
    public User User { get; set; } = null!;
    public long TelegramUserId { get; set; }
    public string? TelegramUsername { get; set; }
    public string Status { get; set; } = TelegramLinkStatus.Active;
    public DateTimeOffset LinkedAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
    public Guid? RevokedById { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>A one-time, hashed, expiring code used to link a Telegram account (docs/06).</summary>
public class TelegramLinkCode : IHasTimestamps
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid UserId { get; set; }
    public User User { get; set; } = null!;
    public string CodeHash { get; set; } = null!;
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? UsedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
