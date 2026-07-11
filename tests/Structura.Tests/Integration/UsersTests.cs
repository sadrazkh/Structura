using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Xunit;

namespace Structura.Tests.Integration;

[Collection("app")]
public class UsersTests(TestAppFactory factory)
{
    [Fact]
    public async Task Admin_can_create_and_list_users()
    {
        var admin = await factory.AdminClientAsync();
        var email = $"list-{Guid.NewGuid():N}@test.local";

        var created = await admin.PostAsJsonAsync("/api/users",
            new { fullName = "Listed User", email, password = "Valid!Passw0rd", role = "ProjectManager" });
        created.StatusCode.Should().Be(HttpStatusCode.Created);

        var list = await admin.GetFromJsonAsync<JsonElement>($"/api/users?search={email}", ApiClient.Json);
        list.GetProperty("items").EnumerateArray().Should()
            .Contain(u => u.GetProperty("email").GetString() == email);
    }

    [Fact]
    public async Task Creating_user_with_duplicate_email_returns_409()
    {
        var admin = await factory.AdminClientAsync();
        var email = $"dup-{Guid.NewGuid():N}@test.local";
        var payload = new { fullName = "Dup", email, password = "Valid!Passw0rd", role = "Reviewer" };

        (await admin.PostAsJsonAsync("/api/users", payload)).EnsureSuccessStatusCode();
        var second = await admin.PostAsJsonAsync("/api/users", payload);

        second.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await second.ErrorCodeAsync()).Should().Be("duplicate");
    }

    [Fact]
    public async Task Weak_password_is_rejected_with_field_errors()
    {
        var admin = await factory.AdminClientAsync();
        var response = await admin.PostAsJsonAsync("/api/users",
            new { fullName = "Weak", email = $"weak-{Guid.NewGuid():N}@test.local", password = "short", role = "Reviewer" });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(ApiClient.Json);
        body.GetProperty("errors").TryGetProperty("password", out _).Should().BeTrue();
    }

    [Theory]
    [InlineData("ProjectManager")]
    [InlineData("Reviewer")]
    public async Task Non_administrators_are_denied_user_management(string role)
    {
        var admin = await factory.AdminClientAsync();
        var (client, _, _, _) = await factory.CreateReadyUserAsync(admin, role);

        (await client.GetAsync("/api/users")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        var create = await client.PostAsJsonAsync("/api/users",
            new { fullName = "X", email = $"x-{Guid.NewGuid():N}@test.local", password = "Valid!Passw0rd", role = "Reviewer" });
        create.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Self_deactivation_is_blocked()
    {
        var admin = await factory.AdminClientAsync();
        var me = await admin.GetFromJsonAsync<JsonElement>("/api/me", ApiClient.Json);
        var myId = me.GetProperty("id").GetGuid();

        var response = await admin.PostAsync($"/api/users/{myId}/deactivate", null);
        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Password_reset_forces_change_on_next_login_and_kills_sessions()
    {
        var admin = await factory.AdminClientAsync();
        var (userClient, id, email, _) = await factory.CreateReadyUserAsync(admin, "Reviewer");

        var reset = await admin.PostAsJsonAsync($"/api/users/{id}/reset-password",
            new { newPassword = "Reset!Passw0rd1" });
        reset.EnsureSuccessStatusCode();

        // Old session is dead.
        (await userClient.GetAsync("/api/me")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        // Fresh login works with the reset password and is flagged for forced change.
        var fresh = factory.CreateClient();
        var auth = await fresh.LoginAsync(email, "Reset!Passw0rd1");
        auth.MustChangePassword.Should().BeTrue();
    }
}
