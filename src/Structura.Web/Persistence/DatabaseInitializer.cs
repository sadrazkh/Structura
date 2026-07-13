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
        await ResetAdminPasswordAsync(scope.ServiceProvider, db, logger, ct);
        await DemoSeeder.SeedAsync(scope.ServiceProvider, db, logger, ct);
    }

    /// <summary>
    /// Break-glass admin password reset for when the admin is locked out (there is no email-based
    /// self-service reset). Set RESET_ADMIN_PASSWORD (and optionally RESET_ADMIN_EMAIL) and restart:
    /// the target administrator's password is reset, the account reactivated, lockout cleared, and a
    /// password change forced at next sign-in. Remove the variable afterwards.
    /// </summary>
    private static async Task ResetAdminPasswordAsync(
        IServiceProvider scoped, AppDbContext db, ILogger logger, CancellationToken ct)
    {
        var config = scoped.GetRequiredService<IConfiguration>();
        var newPassword = config["RESET_ADMIN_PASSWORD"];
        if (string.IsNullOrWhiteSpace(newPassword)) return;

        if (newPassword.Length < 10)
        {
            logger.LogWarning("RESET_ADMIN_PASSWORD ignored: password must be at least 10 characters.");
            return;
        }

        var targetEmail = config["RESET_ADMIN_EMAIL"];
        var query = db.Users.Where(u => u.Role == UserRole.Administrator);
        query = string.IsNullOrWhiteSpace(targetEmail)
            ? query.OrderBy(u => u.CreatedAt)                 // oldest administrator
            : query.Where(u => u.Email == targetEmail.Trim());

        var admin = await query.FirstOrDefaultAsync(ct);
        if (admin is null)
        {
            logger.LogWarning("RESET_ADMIN_PASSWORD: no matching administrator found{Target}.",
                string.IsNullOrWhiteSpace(targetEmail) ? "" : $" for '{targetEmail}'");
            return;
        }

        var hasher = scoped.GetRequiredService<IPasswordHasher<User>>();
        admin.PasswordHash = hasher.HashPassword(admin, newPassword);
        admin.IsActive = true;
        admin.MustChangePassword = true;
        admin.FailedLoginCount = 0;
        admin.LockoutEndAt = null;
        admin.BumpSecurityStamp();
        await db.SaveChangesAsync(ct);

        logger.LogWarning(
            "RESET_ADMIN_PASSWORD applied to administrator {Email}. Sign in, set a new password, " +
            "then REMOVE the RESET_ADMIN_PASSWORD variable and restart.", admin.Email);
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
