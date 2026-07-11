using Microsoft.EntityFrameworkCore;
using Structura.Web.Domain;

namespace Structura.Web.Persistence;

public class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<User> Users => Set<User>();
    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();
    public DbSet<Project> Projects => Set<Project>();
    public DbSet<ProjectMember> ProjectMembers => Set<ProjectMember>();
    public DbSet<AppSetting> AppSettings => Set<AppSetting>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.HasPostgresExtension("citext");

        b.Entity<User>(e =>
        {
            e.Property(x => x.Email).HasColumnType("citext").IsRequired();
            e.HasIndex(x => x.Email).IsUnique();
            e.Property(x => x.FullName).HasMaxLength(200).IsRequired();
            e.Property(x => x.Role).HasMaxLength(30).IsRequired();
            e.Property(x => x.SecurityStamp).HasMaxLength(64).IsRequired();
            e.ToTable(t => t.HasCheckConstraint("ck_users_role",
                "role IN ('Administrator','ProjectManager','Reviewer')"));
        });

        b.Entity<RefreshToken>(e =>
        {
            e.Property(x => x.TokenHash).HasMaxLength(128).IsRequired();
            e.HasIndex(x => x.TokenHash).IsUnique();
            e.HasIndex(x => x.UserId);
            e.HasOne(x => x.User).WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<Project>(e =>
        {
            e.Property(x => x.Name).HasMaxLength(200).IsRequired();
            e.HasIndex(x => x.Name).IsUnique();
            e.Property(x => x.Description).HasMaxLength(2000);
            e.Property(x => x.Status).HasMaxLength(20).IsRequired();
            e.Property(x => x.SchemaFields).HasColumnType("jsonb").IsRequired();
            e.Property(x => x.PromptConfig).HasColumnType("jsonb");
            e.Property(x => x.AiConfig).HasColumnType("jsonb");
            e.Property(x => x.ApiInputConfig).HasColumnType("jsonb");
            e.Property(x => x.ApiOutputConfig).HasColumnType("jsonb");
            e.HasOne(x => x.CreatedBy).WithMany().HasForeignKey(x => x.CreatedById).OnDelete(DeleteBehavior.Restrict);
            e.ToTable(t => t.HasCheckConstraint("ck_projects_status", "status IN ('Active','Archived')"));
        });

        b.Entity<ProjectMember>(e =>
        {
            e.HasIndex(x => new { x.ProjectId, x.UserId }).IsUnique();
            e.HasIndex(x => x.UserId);
            e.HasOne(x => x.Project).WithMany(p => p.Members).HasForeignKey(x => x.ProjectId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.User).WithMany(u => u.Memberships).HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<AppSetting>(e =>
        {
            e.HasKey(x => x.Key);
            e.Property(x => x.Key).HasMaxLength(100);
        });
    }
}
