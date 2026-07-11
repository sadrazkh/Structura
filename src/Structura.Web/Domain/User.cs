namespace Structura.Web.Domain;

public static class UserRole
{
    public const string Administrator = "Administrator";
    public const string ProjectManager = "ProjectManager";
    public const string Reviewer = "Reviewer";

    public static readonly string[] All = [Administrator, ProjectManager, Reviewer];
}

public class User : IHasTimestamps
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public string Email { get; set; } = null!;
    public string PasswordHash { get; set; } = null!;
    public string FullName { get; set; } = null!;
    public string Role { get; set; } = UserRole.Reviewer;
    public bool IsActive { get; set; } = true;
    public bool MustChangePassword { get; set; } = true;
    public string SecurityStamp { get; set; } = Guid.NewGuid().ToString("N");
    public int FailedLoginCount { get; set; }
    public DateTimeOffset? LockoutEndAt { get; set; }
    public DateTimeOffset? LastLoginAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    public List<ProjectMember> Memberships { get; set; } = [];

    public void BumpSecurityStamp() => SecurityStamp = Guid.NewGuid().ToString("N");
}
