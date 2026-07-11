using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Xunit;

namespace Structura.Tests.Integration;

[Collection("app")]
public class SchemaTests(TestAppFactory factory)
{
    private static object ValidFields => new
    {
        fields = new object[]
        {
            new { key = "firstName", label = "First Name", type = "shortText", required = true, displayOrder = 0 },
            new
            {
                key = "incidentType", label = "Incident Type", type = "singleSelect", required = true,
                allowedValues = new[] { "Theft", "Fire", "Flood" }, displayOrder = 1,
            },
            new { key = "isUrgent", label = "Is Urgent", type = "boolean", required = false, displayOrder = 2 },
        },
    };

    [Fact]
    public async Task Saving_a_schema_bumps_the_version_and_idempotent_saves_do_not()
    {
        var admin = await factory.AdminClientAsync();
        var projectId = await admin.CreateProjectAsync();

        var initial = await admin.GetFromJsonAsync<JsonElement>($"/api/projects/{projectId}/schema", ApiClient.Json);
        initial.GetProperty("version").GetInt32().Should().Be(0);

        var first = await admin.PutAsJsonAsync($"/api/projects/{projectId}/schema", ValidFields);
        first.EnsureSuccessStatusCode();
        var firstBody = await first.Content.ReadFromJsonAsync<JsonElement>(ApiClient.Json);
        firstBody.GetProperty("version").GetInt32().Should().Be(1);
        firstBody.GetProperty("changed").GetBoolean().Should().BeTrue();

        // Identical payload → no version bump.
        var second = await admin.PutAsJsonAsync($"/api/projects/{projectId}/schema", ValidFields);
        var secondBody = await second.Content.ReadFromJsonAsync<JsonElement>(ApiClient.Json);
        secondBody.GetProperty("version").GetInt32().Should().Be(1);
        secondBody.GetProperty("changed").GetBoolean().Should().BeFalse();

        var fetched = await admin.GetFromJsonAsync<JsonElement>($"/api/projects/{projectId}/schema", ApiClient.Json);
        fetched.GetProperty("fields").GetArrayLength().Should().Be(3);
    }

    [Fact]
    public async Task Invalid_schemas_are_rejected_with_field_errors()
    {
        var admin = await factory.AdminClientAsync();
        var projectId = await admin.CreateProjectAsync();

        // Duplicate keys + bad key + select without values, all at once.
        var response = await admin.PutAsJsonAsync($"/api/projects/{projectId}/schema", new
        {
            fields = new object[]
            {
                new { key = "Name", label = "Bad Key", type = "shortText" },
                new { key = "cat", label = "Cat", type = "singleSelect" },
                new { key = "dup", label = "A", type = "shortText" },
                new { key = "dup", label = "B", type = "shortText" },
            },
        });
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(ApiClient.Json);
        body.GetProperty("errors").EnumerateObject().Count().Should().BeGreaterThanOrEqualTo(3);
    }

    [Fact]
    public async Task Reviewer_cannot_edit_the_schema()
    {
        var admin = await factory.AdminClientAsync();
        var projectId = await admin.CreateProjectAsync();
        var (reviewer, reviewerId, _, _) = await factory.CreateReadyUserAsync(admin, "Reviewer");
        (await admin.PostAsJsonAsync($"/api/projects/{projectId}/members", new { userId = reviewerId }))
            .EnsureSuccessStatusCode();

        // Member reviewers may read the schema (the review form needs it) but never write it.
        (await reviewer.GetAsync($"/api/projects/{projectId}/schema")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await reviewer.PutAsJsonAsync($"/api/projects/{projectId}/schema", ValidFields))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Archived_project_schema_is_read_only()
    {
        var admin = await factory.AdminClientAsync();
        var projectId = await admin.CreateProjectAsync();
        (await admin.PostAsync($"/api/projects/{projectId}/archive", null)).EnsureSuccessStatusCode();

        var response = await admin.PutAsJsonAsync($"/api/projects/{projectId}/schema", ValidFields);
        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await response.ErrorCodeAsync()).Should().Be("invalid_state");
    }
}
