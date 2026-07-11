using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Xunit;

namespace Structura.Tests.Integration;

[Collection("app")]
public class ProjectsTests(TestAppFactory factory)
{
    [Fact]
    public async Task Reviewer_cannot_create_projects()
    {
        var admin = await factory.AdminClientAsync();
        var (reviewer, _, _, _) = await factory.CreateReadyUserAsync(admin, "Reviewer");

        var response = await reviewer.PostAsJsonAsync("/api/projects",
            new { name = $"Nope {Guid.NewGuid():N}", description = "" });
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Project_manager_creates_project_and_becomes_member()
    {
        var admin = await factory.AdminClientAsync();
        var (pm, pmId, _, _) = await factory.CreateReadyUserAsync(admin, "ProjectManager");

        var projectId = await pm.CreateProjectAsync();

        var list = await pm.GetFromJsonAsync<JsonElement>("/api/projects", ApiClient.Json);
        list.GetProperty("items").EnumerateArray().Should()
            .Contain(p => p.GetProperty("id").GetGuid() == projectId);

        var members = await pm.GetFromJsonAsync<JsonElement>($"/api/projects/{projectId}/members", ApiClient.Json);
        members.GetProperty("items").EnumerateArray().Should()
            .Contain(m => m.GetProperty("userId").GetGuid() == pmId);
    }

    [Fact]
    public async Task Non_member_project_manager_cannot_view_or_manage_a_project()
    {
        var admin = await factory.AdminClientAsync();
        var (owner, _, _, _) = await factory.CreateReadyUserAsync(admin, "ProjectManager");
        var (outsider, _, _, _) = await factory.CreateReadyUserAsync(admin, "ProjectManager");

        var projectId = await owner.CreateProjectAsync();

        (await outsider.GetAsync($"/api/projects/{projectId}")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        var update = await outsider.PutAsJsonAsync($"/api/projects/{projectId}",
            new { name = "Hijacked", description = "" });
        update.StatusCode.Should().Be(HttpStatusCode.Forbidden);

        // Membership list also filters it out.
        var list = await outsider.GetFromJsonAsync<JsonElement>("/api/projects", ApiClient.Json);
        list.GetProperty("items").EnumerateArray().Should()
            .NotContain(p => p.GetProperty("id").GetGuid() == projectId);
    }

    [Fact]
    public async Task Reviewer_member_can_view_but_not_manage()
    {
        var admin = await factory.AdminClientAsync();
        var (pm, _, _, _) = await factory.CreateReadyUserAsync(admin, "ProjectManager");
        var (reviewer, reviewerId, _, _) = await factory.CreateReadyUserAsync(admin, "Reviewer");

        var projectId = await pm.CreateProjectAsync();
        (await pm.PostAsJsonAsync($"/api/projects/{projectId}/members", new { userId = reviewerId }))
            .EnsureSuccessStatusCode();

        (await reviewer.GetAsync($"/api/projects/{projectId}")).StatusCode.Should().Be(HttpStatusCode.OK);
        var update = await reviewer.PutAsJsonAsync($"/api/projects/{projectId}",
            new { name = "Reviewer Edit", description = "" });
        update.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Administrator_sees_every_project()
    {
        var admin = await factory.AdminClientAsync();
        var (pm, _, _, _) = await factory.CreateReadyUserAsync(admin, "ProjectManager");
        var projectId = await pm.CreateProjectAsync();

        var list = await admin.GetFromJsonAsync<JsonElement>("/api/projects", ApiClient.Json);
        list.GetProperty("items").EnumerateArray().Should()
            .Contain(p => p.GetProperty("id").GetGuid() == projectId);
    }

    [Fact]
    public async Task Duplicate_member_and_duplicate_name_return_409()
    {
        var admin = await factory.AdminClientAsync();
        var (pm, _, _, _) = await factory.CreateReadyUserAsync(admin, "ProjectManager");
        var (_, reviewerId, _, _) = await factory.CreateReadyUserAsync(admin, "Reviewer");

        var name = $"Unique {Guid.NewGuid():N}";
        var projectId = await pm.CreateProjectAsync(name);

        var dupName = await pm.PostAsJsonAsync("/api/projects", new { name, description = "" });
        dupName.StatusCode.Should().Be(HttpStatusCode.Conflict);

        (await pm.PostAsJsonAsync($"/api/projects/{projectId}/members", new { userId = reviewerId }))
            .EnsureSuccessStatusCode();
        var dupMember = await pm.PostAsJsonAsync($"/api/projects/{projectId}/members", new { userId = reviewerId });
        dupMember.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Archived_project_rejects_modifications()
    {
        var admin = await factory.AdminClientAsync();
        var (pm, _, _, _) = await factory.CreateReadyUserAsync(admin, "ProjectManager");
        var projectId = await pm.CreateProjectAsync();

        (await pm.PostAsync($"/api/projects/{projectId}/archive", null)).EnsureSuccessStatusCode();

        var update = await pm.PutAsJsonAsync($"/api/projects/{projectId}",
            new { name = $"After Archive {Guid.NewGuid():N}", description = "" });
        update.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await update.ErrorCodeAsync()).Should().Be("invalid_state");

        // Still viewable.
        (await pm.GetAsync($"/api/projects/{projectId}")).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Unauthenticated_requests_are_rejected()
    {
        var anonymous = factory.CreateClient();
        (await anonymous.GetAsync("/api/projects")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await anonymous.GetAsync("/api/users")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }
}
