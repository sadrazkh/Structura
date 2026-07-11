using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace Structura.Tests.Integration;

/// <summary>HttpClient helpers for authenticated API calls in tests.</summary>
public static class ApiClient
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public sealed record AuthResult(string AccessToken, string RefreshToken, bool MustChangePassword);

    public static async Task<AuthResult> LoginAsync(this HttpClient client, string email, string password)
    {
        var response = await client.PostAsJsonAsync("/api/auth/login", new { email, password });
        response.EnsureSuccessStatusCode();
        return await ReadAuthAsync(response);
    }

    public static async Task<AuthResult> ReadAuthAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Json);
        return new AuthResult(
            body.GetProperty("accessToken").GetString()!,
            body.GetProperty("refreshToken").GetString()!,
            body.GetProperty("mustChangePassword").GetBoolean());
    }

    public static void SetBearer(this HttpClient client, string token) =>
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

    /// <summary>Client authenticated as the bootstrap administrator (password already rotated by the fixture).</summary>
    public static async Task<HttpClient> AdminClientAsync(this TestAppFactory factory)
    {
        var client = factory.CreateClient();
        var auth = await client.LoginAsync(TestAppFactory.AdminEmail, TestAppFactory.AdminFinalPassword);
        client.SetBearer(auth.AccessToken);
        return client;
    }

    /// <summary>Creates a user via the admin API and completes the forced password change.
    /// Returns a ready-to-use authenticated client plus the user's credentials.</summary>
    public static async Task<(HttpClient Client, Guid Id, string Email, string Password)> CreateReadyUserAsync(
        this TestAppFactory factory, HttpClient adminClient, string role)
    {
        var email = $"{role.ToLowerInvariant()}-{Guid.NewGuid():N}@test.local";
        const string initialPassword = "Initial!Passw0rd";
        var finalPassword = "Final!Passw0rd-" + Guid.NewGuid().ToString("N")[..8];

        var created = await adminClient.PostAsJsonAsync("/api/users",
            new { fullName = $"{role} User", email, password = initialPassword, role });
        created.EnsureSuccessStatusCode();
        var createdBody = await created.Content.ReadFromJsonAsync<JsonElement>(Json);
        var id = createdBody.GetProperty("id").GetGuid();

        var client = factory.CreateClient();
        var auth = await client.LoginAsync(email, initialPassword);
        client.SetBearer(auth.AccessToken);
        var changed = await client.PostAsJsonAsync("/api/auth/change-password",
            new { currentPassword = initialPassword, newPassword = finalPassword });
        changed.EnsureSuccessStatusCode();
        client.SetBearer((await ReadAuthAsync(changed)).AccessToken);

        return (client, id, email, finalPassword);
    }

    public static async Task<Guid> CreateProjectAsync(this HttpClient client, string? name = null)
    {
        var response = await client.PostAsJsonAsync("/api/projects",
            new { name = name ?? $"Project {Guid.NewGuid():N}", description = "test project" });
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Json);
        return body.GetProperty("id").GetGuid();
    }

    public static async Task<string?> ErrorCodeAsync(this HttpResponseMessage response)
    {
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Json);
        return body.TryGetProperty("code", out var code) ? code.GetString() : null;
    }
}
