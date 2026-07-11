using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Structura.Web.Domain;

namespace Structura.Web.Persistence;

/// <summary>Signals that no administrator exists and bootstrap env vars were not provided (F1).</summary>
public sealed class SetupState
{
    public volatile bool IsSetupRequired;
}

public static class DatabaseInitializer
{
    // Arbitrary constant; the same lock id must be used by every app instance.
    private const long MigrationLockId = 0x5354525543545552; // "STRUCTUR"

    public static async Task InitializeAsync(IServiceProvider services, CancellationToken ct = default)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var logger = scope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("DatabaseInitializer");

        await MigrateWithAdvisoryLockAsync(db, logger, ct);
        await SeedBootstrapAdminAsync(scope.ServiceProvider, db, logger, ct);
        await DemoSeeder.SeedAsync(scope.ServiceProvider, db, logger, ct);
    }

    private static async Task MigrateWithAdvisoryLockAsync(AppDbContext db, ILogger logger, CancellationToken ct)
    {
        var connectionString = db.Database.GetConnectionString()
                               ?? throw new InvalidOperationException("No connection string configured.");

        NpgsqlConnection? lockConnection = null;
        try
        {
            lockConnection = new NpgsqlConnection(connectionString);
            await lockConnection.OpenAsync(ct);
            await using var cmd = new NpgsqlCommand($"SELECT pg_advisory_lock({MigrationLockId})", lockConnection);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.InvalidCatalogName)
        {
            // Database does not exist yet: Migrate() below creates it; no concurrent instance can
            // have gotten further than this point either, so skipping the lock is safe here.
            if (lockConnection is not null) await lockConnection.DisposeAsync();
            lockConnection = null;
        }

        try
        {
            logger.LogInformation("Applying database migrations...");
            await db.Database.MigrateAsync(ct);
            logger.LogInformation("Database is up to date.");
        }
        finally
        {
            if (lockConnection is not null)
            {
                await using var unlock = new NpgsqlCommand($"SELECT pg_advisory_unlock({MigrationLockId})", lockConnection);
                await unlock.ExecuteNonQueryAsync(ct);
                await lockConnection.DisposeAsync();
            }
        }
    }

    private static async Task SeedBootstrapAdminAsync(
        IServiceProvider scoped, AppDbContext db, ILogger logger, CancellationToken ct)
    {
        var setupState = scoped.GetRequiredService<SetupState>();

        var hasAdmin = await db.Users.AnyAsync(u => u.Role == UserRole.Administrator && u.IsActive, ct);
        if (hasAdmin)
        {
            setupState.IsSetupRequired = false;
            return;
        }

        var config = scoped.GetRequiredService<IConfiguration>();
        var email = config["BOOTSTRAP_ADMIN_EMAIL"];
        var password = config["BOOTSTRAP_ADMIN_PASSWORD"];

        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(password))
        {
            setupState.IsSetupRequired = true;
            logger.LogWarning(
                "No active administrator exists and BOOTSTRAP_ADMIN_EMAIL / BOOTSTRAP_ADMIN_PASSWORD are not set. " +
                "API responds with 503 setup_required until they are provided.");
            return;
        }

        var hasher = scoped.GetRequiredService<IPasswordHasher<User>>();
        var admin = new User
        {
            Email = email.Trim(),
            FullName = "Administrator",
            Role = UserRole.Administrator,
            MustChangePassword = true,
        };
        admin.PasswordHash = hasher.HashPassword(admin, password);
        db.Users.Add(admin);
        await db.SaveChangesAsync(ct);
        setupState.IsSetupRequired = false;
        logger.LogInformation("Bootstrap administrator {Email} created (password change forced on first login).", admin.Email);
    }
}
