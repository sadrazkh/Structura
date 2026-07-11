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
    public DbSet<Record> Records => Set<Record>();
    public DbSet<ImportRun> ImportRuns => Set<ImportRun>();
    public DbSet<ProcessingRun> ProcessingRuns => Set<ProcessingRun>();
    public DbSet<ExtractionResult> ExtractionResults => Set<ExtractionResult>();

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

        b.Entity<Record>(e =>
        {
            e.Property(x => x.ExternalId).HasMaxLength(256).IsRequired();
            e.Property(x => x.Text).IsRequired();
            e.Property(x => x.ProcessingStatusValue).HasColumnName("processing_status").HasMaxLength(20);
            e.Property(x => x.ReviewStatusValue).HasColumnName("review_status").HasMaxLength(30);
            e.Property(x => x.DeliveryStatusValue).HasColumnName("delivery_status").HasMaxLength(20);
            e.Property(x => x.FinalOutput).HasColumnType("jsonb");
            e.Property(x => x.Version).IsConcurrencyToken();
            e.HasOne(x => x.Project).WithMany().HasForeignKey(x => x.ProjectId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.AssignedReviewer).WithMany().HasForeignKey(x => x.AssignedReviewerId)
                .OnDelete(DeleteBehavior.SetNull);
            e.HasOne(x => x.ReviewedBy).WithMany().HasForeignKey(x => x.ReviewedById)
                .OnDelete(DeleteBehavior.SetNull);
            e.HasOne<ImportRun>().WithMany().HasForeignKey(x => x.ImportRunId).OnDelete(DeleteBehavior.SetNull);
            e.HasOne<ProcessingRun>().WithMany().HasForeignKey(x => x.ProcessingRunId).OnDelete(DeleteBehavior.SetNull);
            e.HasIndex(x => new { x.ProjectId, x.ExternalId }).IsUnique();
            e.HasIndex(x => new { x.ProjectId, x.ProcessingStatusValue });
            e.HasIndex(x => new { x.ProjectId, x.ReviewStatusValue });
            e.HasIndex(x => new { x.ProjectId, x.DeliveryStatusValue });
            // Delivery worker claim predicate.
            e.HasIndex(x => new { x.ProjectId, x.ReviewStatusValue, x.DeliveryStatusValue });
            e.HasIndex(x => new { x.ProjectId, x.AssignedReviewerId, x.ReviewStatusValue });
            e.HasIndex(x => new { x.ProjectId, x.UpdatedAt });
            e.HasIndex(x => new { x.ProcessingRunId, x.ProcessingStatusValue });
            e.ToTable(t =>
            {
                t.HasCheckConstraint("ck_records_processing_status",
                    "processing_status IN ('Pending','Processing','Completed','Failed')");
                t.HasCheckConstraint("ck_records_review_status",
                    "review_status IN ('Unassigned','Assigned','InReview','Approved','Rejected','ReprocessRequested')");
                t.HasCheckConstraint("ck_records_delivery_status",
                    "delivery_status IN ('Pending','Delivered','Failed')");
            });
        });

        b.Entity<ImportRun>(e =>
        {
            e.Property(x => x.Source).HasMaxLength(10);
            e.Property(x => x.Status).HasMaxLength(30);
            e.Property(x => x.FileName).HasMaxLength(300);
            e.Property(x => x.FilePath).HasMaxLength(500);
            e.Property(x => x.Mapping).HasColumnType("jsonb");
            e.Property(x => x.Errors).HasColumnType("jsonb");
            e.HasOne(x => x.Project).WithMany().HasForeignKey(x => x.ProjectId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => new { x.ProjectId, x.CreatedAt });
            e.ToTable(t =>
            {
                t.HasCheckConstraint("ck_import_runs_source", "source IN ('Excel','Csv','Manual','Api')");
                t.HasCheckConstraint("ck_import_runs_status",
                    "status IN ('AwaitingMapping','Running','Completed','CompletedWithErrors','Failed','Cancelled')");
            });
        });

        b.Entity<ProcessingRun>(e =>
        {
            e.Property(x => x.Status).HasMaxLength(30);
            e.Property(x => x.SchemaSnapshot).HasColumnType("jsonb");
            e.Property(x => x.PromptSnapshot).HasColumnType("jsonb");
            e.Property(x => x.Model).HasMaxLength(200);
            e.HasOne(x => x.Project).WithMany().HasForeignKey(x => x.ProjectId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => new { x.ProjectId, x.CreatedAt });
            e.HasIndex(x => x.Status).HasFilter("status = 'Running'");
            e.ToTable(t => t.HasCheckConstraint("ck_processing_runs_status",
                "status IN ('Running','Completed','CompletedWithErrors','Cancelled','Failed')"));
        });

        b.Entity<ExtractionResult>(e =>
        {
            e.Property(x => x.Status).HasMaxLength(20);
            e.Property(x => x.Model).HasMaxLength(200);
            e.Property(x => x.Output).HasColumnType("jsonb");
            e.HasOne(x => x.Record).WithMany().HasForeignKey(x => x.RecordId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<ProcessingRun>().WithMany().HasForeignKey(x => x.RunId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => new { x.RecordId, x.CreatedAt });
            e.HasIndex(x => x.RunId);
            e.ToTable(t => t.HasCheckConstraint("ck_extraction_results_status",
                "status IN ('Succeeded','Failed')"));
        });
    }
}
