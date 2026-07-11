namespace Structura.Web.Domain;

public static class ProjectStatus
{
    public const string Active = "Active";
    public const string Archived = "Archived";
}

public class Project : IHasTimestamps
{
    public const string EmptySchema = """{"version":0,"fields":[]}""";

    public Guid Id { get; set; } = Guid.CreateVersion7();
    public string Name { get; set; } = null!;
    public string Description { get; set; } = "";
    public string Status { get; set; } = ProjectStatus.Active;

    // Configuration documents (JSONB). Shapes are defined in docs/03-domain-and-database.md.
    public string SchemaFields { get; set; } = EmptySchema;
    public int SchemaVersion { get; set; }
    public string? PromptConfig { get; set; }
    public string? AiConfig { get; set; }
    public string? ApiInputConfig { get; set; }
    public string? ApiOutputConfig { get; set; }

    public Guid CreatedById { get; set; }
    public User CreatedBy { get; set; } = null!;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    public List<ProjectMember> Members { get; set; } = [];

    public bool IsArchived => Status == ProjectStatus.Archived;
}

public class ProjectMember : IHasTimestamps
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid ProjectId { get; set; }
    public Project Project { get; set; } = null!;
    public Guid UserId { get; set; }
    public User User { get; set; } = null!;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
