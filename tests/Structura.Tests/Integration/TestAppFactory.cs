using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Npgsql;
using Testcontainers.PostgreSql;
using Xunit;

namespace Structura.Tests.Integration;

/// <summary>
/// Boots the real application against PostgreSQL.
/// Default: a disposable Testcontainers instance (requires Docker — the CI path).
/// Override: set STRUCTURA_TEST_DB to a connection string (e.g. a local PostgreSQL)
/// to run the suite on machines without Docker; the target database is dropped
/// and recreated on start so every run begins from a clean state.
/// </summary>
public sealed class TestAppFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    private static readonly string? ExternalDb =
        Environment.GetEnvironmentVariable("STRUCTURA_TEST_DB");

    private readonly PostgreSqlContainer? _postgres = ExternalDb is null
        ? new PostgreSqlBuilder("postgres:16-alpine")
            .WithDatabase("structura_test")
            .WithUsername("structura")
            .WithPassword("structura")
            .Build()
        : null;

    private string _connectionString = "";

    public const string AdminEmail = "admin@test.local";
    public const string AdminBootstrapPassword = "Admin!Passw0rd";
    public const string AdminFinalPassword = "Admin!Passw0rd-final";

    public async Task InitializeAsync()
    {
        if (_postgres is not null)
        {
            await _postgres.StartAsync();
            _connectionString = _postgres.GetConnectionString();
        }
        else
        {
            _connectionString = ExternalDb!;
            await RecreateExternalDatabaseAsync(_connectionString);
        }

        // Host creation runs migrations + bootstrap seeding; then complete the forced
        // password change once so every test can log in with AdminFinalPassword.
        var client = CreateClient();
        var auth = await client.LoginAsync(AdminEmail, AdminBootstrapPassword);
        client.SetBearer(auth.AccessToken);
        var changed = await client.PostAsJsonAsync("/api/auth/change-password",
            new { currentPassword = AdminBootstrapPassword, newPassword = AdminFinalPassword });
        changed.EnsureSuccessStatusCode();
    }

    private static async Task RecreateExternalDatabaseAsync(string connectionString)
    {
        var builder = new NpgsqlConnectionStringBuilder(connectionString);
        var database = builder.Database
                       ?? throw new InvalidOperationException("STRUCTURA_TEST_DB must include a database name.");
        builder.Database = "postgres";
        await using var connection = new NpgsqlConnection(builder.ConnectionString);
        await connection.OpenAsync();
        await using var drop = new NpgsqlCommand(
            $"DROP DATABASE IF EXISTS \"{database}\" WITH (FORCE)", connection);
        await drop.ExecuteNonQueryAsync();
        await using var create = new NpgsqlCommand($"CREATE DATABASE \"{database}\"", connection);
        await create.ExecuteNonQueryAsync();
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        // UseSetting feeds host configuration, which IS visible to top-level statements
        // in Program.cs (ConfigureAppConfiguration would apply too late for those reads).
        builder.UseSetting("ConnectionStrings:Default", _connectionString);
        builder.UseSetting("Jwt:SigningKey", "integration-test-signing-key-0123456789abcdef");
        builder.UseSetting("BOOTSTRAP_ADMIN_EMAIL", AdminEmail);
        builder.UseSetting("BOOTSTRAP_ADMIN_PASSWORD", AdminBootstrapPassword);
        builder.UseSetting("RateLimiting:LoginPermitLimit", "10000");
        builder.UseSetting("Serilog:MinimumLevel:Default", "Warning");
    }

    public new async Task DisposeAsync()
    {
        await base.DisposeAsync();
        if (_postgres is not null) await _postgres.DisposeAsync();
    }
}

[CollectionDefinition("app")]
public sealed class AppCollection : ICollectionFixture<TestAppFactory>;
