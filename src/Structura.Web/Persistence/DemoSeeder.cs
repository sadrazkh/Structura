using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Structura.Web.Domain;
using Structura.Web.Infrastructure.Secrets;

namespace Structura.Web.Persistence;

/// <summary>
/// Seeds a complete, self-contained demo (SEED_DEMO=true): users, a project with schema/prompt/AI
/// config, 60 mixed Persian/English records already processed (pre-baked AI output so review works
/// without a live provider), assignments, and a few approvals. Idempotent — keyed on the demo
/// project name. Refuses to run in Production unless SEED_DEMO_FORCE=true.
/// </summary>
public static class DemoSeeder
{
    private const string ProjectName = "Incident Reports (Demo)";
    private const string DemoPassword = "Demo!Passw0rd";

    public static async Task SeedAsync(IServiceProvider services, AppDbContext db, ILogger logger, CancellationToken ct)
    {
        var config = services.GetRequiredService<IConfiguration>();
        if (!config.GetValue<bool>("SEED_DEMO")) return;

        var env = services.GetRequiredService<IHostEnvironment>();
        if (env.IsProduction() && !config.GetValue<bool>("SEED_DEMO_FORCE"))
        {
            logger.LogWarning("SEED_DEMO ignored in Production (set SEED_DEMO_FORCE=true to override).");
            return;
        }

        if (await db.Projects.AnyAsync(p => p.Name == ProjectName, ct))
        {
            logger.LogInformation("Demo data already present — skipping seed.");
            return;
        }

        logger.LogInformation("Seeding demo data...");
        var hasher = services.GetRequiredService<IPasswordHasher<User>>();
        var secrets = services.GetRequiredService<ISecretProtector>();

        // ---- Users ----
        var pm = CreateUser(hasher, "pm@demo.local", "Parisa (Project Manager)", UserRole.ProjectManager);
        var reviewers = Enumerable.Range(1, 5)
            .Select(i => CreateUser(hasher, $"reviewer{i}@demo.local", $"Reviewer {i}", UserRole.Reviewer))
            .ToList();
        db.Users.AddRange([pm, .. reviewers]);
        await db.SaveChangesAsync(ct);

        // ---- Project + config ----
        var schema = DemoSchema();
        var project = new Project
        {
            Name = ProjectName,
            Description = "Demo project: extract structured data from Persian and English incident reports.",
            Status = ProjectStatus.Active,
            SchemaFields = schema.ToJson(),
            SchemaVersion = 1,
            PromptConfig = new PromptConfigDocument
            {
                SystemInstruction = "You extract structured data from Persian and English incident reports.",
                ExtractionInstruction = "Dates must be ISO 8601. Return null for values not present in the text.",
            }.ToJson(),
            AiConfig = new AiConfigDocument
            {
                Provider = AiProviders.OpenRouter,
                BaseUrl = config["DEMO_PROVIDER_BASE_URL"] ?? AiProviders.DefaultBaseUrl(AiProviders.OpenRouter),
                ApiKeyProtected = secrets.Protect(config["DEMO_PROVIDER_API_KEY"] ?? "sk-demo-set-a-real-key"),
                Model = config["DEMO_PROVIDER_MODEL"] ?? "openai/gpt-4.1-mini",
                Temperature = 0.1,
                MaxOutputTokens = 1024,
                TimeoutSeconds = 60,
                Concurrency = 8,
            }.ToJson(),
            CreatedById = pm.Id,
        };
        db.Projects.Add(project);
        db.ProjectMembers.Add(new ProjectMember { ProjectId = project.Id, UserId = pm.Id });
        foreach (var reviewer in reviewers)
            db.ProjectMembers.Add(new ProjectMember { ProjectId = project.Id, UserId = reviewer.Id });
        await db.SaveChangesAsync(ct);

        // ---- A completed processing run (for realistic dashboards) ----
        var run = new ProcessingRun
        {
            ProjectId = project.Id,
            Status = RunStatus.Completed,
            SchemaSnapshot = project.SchemaFields,
            PromptSnapshot = project.PromptConfig!,
            Model = "demo/extractor",
            Total = 60,
            Succeeded = 60,
            CreatedById = pm.Id,
            StartedAt = DateTimeOffset.UtcNow.AddMinutes(-30),
            FinishedAt = DateTimeOffset.UtcNow.AddMinutes(-29),
            InputTokens = 60 * 220,
            OutputTokens = 60 * 45,
        };
        db.ProcessingRuns.Add(run);
        await db.SaveChangesAsync(ct);

        // ---- 60 records with pre-baked extraction results ----
        var records = new List<Record>(60);
        var extractions = new List<ExtractionResult>(60);
        for (var i = 1; i <= 60; i++)
        {
            var persian = i % 2 == 0;
            var text = persian
                ? $"در تاریخ ۱۲ مرداد خانم سارا احمدی گزارش سرقت شماره {i} را ثبت کرد."
                : $"On August 3rd Mr. John Smith reported a fire incident number {i}.";
            var output = persian
                ? $$"""{"firstName":"سارا","lastName":"احمدی","incidentType":"Theft","incidentDate":"2026-08-03","isUrgent":true,"description":"گزارش شماره {{i}}"}"""
                : $$"""{"firstName":"John","lastName":"Smith","incidentType":"Fire","incidentDate":"2026-08-04","isUrgent":false,"description":"Report {{i}}"}""";

            var record = new Record
            {
                ProjectId = project.Id,
                ExternalId = $"D-{i:D3}",
                Text = text,
                ProcessingStatusValue = ProcessingStatus.Completed,
                ReviewStatusValue = ReviewStatus.Unassigned,
                DeliveryStatusValue = DeliveryStatus.Pending,
                ProcessingRunId = run.Id,
            };
            var extraction = new ExtractionResult
            {
                RecordId = record.Id,
                RunId = run.Id,
                Model = "demo/extractor",
                Status = ExtractionStatus.Succeeded,
                RawResponse = output,
                Output = output,
                InputTokens = 220,
                OutputTokens = 45,
                DurationMs = 120,
            };
            record.LatestResultId = extraction.Id;
            records.Add(record);
            extractions.Add(extraction);
        }
        db.Records.AddRange(records);
        db.ExtractionResults.AddRange(extractions);
        await db.SaveChangesAsync(ct);

        // ---- Assign 50 records (10 each) to the 5 reviewers; leave 10 unassigned ----
        var now = DateTimeOffset.UtcNow;
        for (var i = 0; i < 50; i++)
        {
            var record = records[i];
            record.AssignedReviewerId = reviewers[i / 10].Id;
            record.AssignedAt = now;
            record.ReviewStatusValue = ReviewStatus.Assigned;
        }

        // ---- Reviewer 1 pre-approves 6 (one edited) ----
        for (var i = 0; i < 6; i++)
        {
            var record = records[i];
            record.ReviewStatusValue = ReviewStatus.Approved;
            record.ReviewedById = reviewers[0].Id;
            record.ReviewedAt = now;
            record.FinalOutput = i == 0
                ? extractions[i].Output!.Replace("سارا", "سارا (تأییدشده)")
                : extractions[i].Output;
        }
        await db.SaveChangesAsync(ct);

        logger.LogInformation(
            "Demo seeded: 1 PM + 5 reviewers, project '{Project}', 60 records (50 assigned, 6 approved). " +
            "Sign in with pm@demo.local / reviewer1@demo.local … password '{Password}'.",
            ProjectName, DemoPassword);
    }

    private static User CreateUser(IPasswordHasher<User> hasher, string email, string name, string role)
    {
        var user = new User { Email = email, FullName = name, Role = role, MustChangePassword = false };
        user.PasswordHash = hasher.HashPassword(user, DemoPassword);
        return user;
    }

    private static SchemaDocument DemoSchema() => new()
    {
        Version = 1,
        Fields =
        [
            new FieldSpec { Key = "firstName", Label = "First Name", Type = FieldTypes.ShortText, Required = true, DisplayOrder = 0 },
            new FieldSpec { Key = "lastName", Label = "Last Name", Type = FieldTypes.ShortText, Required = true, DisplayOrder = 1 },
            new FieldSpec
            {
                Key = "incidentType", Label = "Incident Type", Type = FieldTypes.SingleSelect, Required = true,
                AllowedValues = ["Theft", "Fire", "Flood", "Assault", "Other"], DisplayOrder = 2,
            },
            new FieldSpec { Key = "incidentDate", Label = "Incident Date", Type = FieldTypes.Date, DisplayOrder = 3 },
            new FieldSpec { Key = "isUrgent", Label = "Is Urgent", Type = FieldTypes.Boolean, DisplayOrder = 4 },
            new FieldSpec { Key = "description", Label = "Description", Type = FieldTypes.LongText, DisplayOrder = 5 },
        ],
    };
}
