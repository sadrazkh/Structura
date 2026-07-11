using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Xunit;

namespace Structura.Tests.Integration;

[Collection("app")]
public class AuthTests(TestAppFactory factory)
{
    [Fact]
    public async Task Login_with_wrong_password_returns_401_invalid_credentials()
    {
        var client = factory.CreateClient();
        var response = await client.PostAsJsonAsync("/api/auth/login",
            new { email = TestAppFactory.AdminEmail, password = "definitely-wrong-password" });

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await response.ErrorCodeAsync()).Should().Be("invalid_credentials");
    }

    [Fact]
    public async Task Login_with_unknown_email_returns_401_without_user_enumeration()
    {
        var client = factory.CreateClient();
        var response = await client.PostAsJsonAsync("/api/auth/login",
            new { email = "nobody@test.local", password = "whatever-password" });

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await response.ErrorCodeAsync()).Should().Be("invalid_credentials");
    }

    [Fact]
    public async Task New_user_is_gated_until_forced_password_change_is_completed()
    {
        var admin = await factory.AdminClientAsync();
        var email = $"gated-{Guid.NewGuid():N}@test.local";
        var created = await admin.PostAsJsonAsync("/api/users",
            new { fullName = "Gated User", email, password = "Initial!Passw0rd", role = "Reviewer" });
        created.EnsureSuccessStatusCode();

        var client = factory.CreateClient();
        var auth = await client.LoginAsync(email, "Initial!Passw0rd");
        auth.MustChangePassword.Should().BeTrue();
        client.SetBearer(auth.AccessToken);

        // Gated: normal endpoints refuse until the password is changed.
        var blocked = await client.GetAsync("/api/projects");
        blocked.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await blocked.ErrorCodeAsync()).Should().Be("password_change_required");

        // /api/me stays reachable so the client can render the change-password screen.
        (await client.GetAsync("/api/me")).StatusCode.Should().Be(HttpStatusCode.OK);

        var changed = await client.PostAsJsonAsync("/api/auth/change-password",
            new { currentPassword = "Initial!Passw0rd", newPassword = "Fresh!Passw0rd1" });
        changed.EnsureSuccessStatusCode();
        client.SetBearer((await ApiClient.ReadAuthAsync(changed)).AccessToken);

        (await client.GetAsync("/api/projects")).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Refresh_rotates_tokens_and_reuse_revokes_the_family()
    {
        var admin = await factory.AdminClientAsync();
        var (_, _, email, password) = await factory.CreateReadyUserAsync(admin, "Reviewer");

        var client = factory.CreateClient();
        var auth = await client.LoginAsync(email, password);

        // First refresh: succeeds and returns a new pair.
        var refresh1 = await client.PostAsJsonAsync("/api/auth/refresh", new { refreshToken = auth.RefreshToken });
        refresh1.StatusCode.Should().Be(HttpStatusCode.OK);
        var rotated = await ApiClient.ReadAuthAsync(refresh1);
        rotated.RefreshToken.Should().NotBe(auth.RefreshToken);

        // Reusing the rotated (old) token is treated as theft…
        var reuse = await client.PostAsJsonAsync("/api/auth/refresh", new { refreshToken = auth.RefreshToken });
        reuse.StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        // …and revokes the whole family: the new token no longer works either.
        var afterReuse = await client.PostAsJsonAsync("/api/auth/refresh", new { refreshToken = rotated.RefreshToken });
        afterReuse.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Ten_failed_logins_lock_the_account()
    {
        var admin = await factory.AdminClientAsync();
        var (_, _, email, password) = await factory.CreateReadyUserAsync(admin, "Reviewer");
        var client = factory.CreateClient();

        for (var i = 0; i < 10; i++)
        {
            var failed = await client.PostAsJsonAsync("/api/auth/login",
                new { email, password = "wrong-password-attempt" });
            failed.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        }

        // Even the correct password is rejected while locked.
        var locked = await client.PostAsJsonAsync("/api/auth/login", new { email, password });
        locked.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await locked.ErrorCodeAsync()).Should().Be("account_locked");
    }

    [Fact]
    public async Task Deactivated_user_session_is_killed()
    {
        var admin = await factory.AdminClientAsync();
        var (userClient, id, _, _) = await factory.CreateReadyUserAsync(admin, "Reviewer");

        (await userClient.GetAsync("/api/me")).StatusCode.Should().Be(HttpStatusCode.OK);

        (await admin.PostAsync($"/api/users/{id}/deactivate", null)).EnsureSuccessStatusCode();

        var after = await userClient.GetAsync("/api/me");
        after.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }
}
