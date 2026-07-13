using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;
using Xunit;

namespace Structura.Tests.Integration;

[Collection("app")]
public class SchemaGenerateTests(TestAppFactory factory) : IDisposable
{
    private readonly WireMockServer _mock = WireMockServer.Start();

    public void Dispose() => _mock.Stop();

    private async Task<(HttpClient Admin, Guid ProjectId)> ProjectWithAiAsync()
    {
        var admin = await factory.AdminClientAsync();
        var projectId = await admin.CreateProjectAsync();
        (await admin.PutAsJsonAsync($"/api/projects/{projectId}/ai-config", new
        {
            provider = "OpenRouter", baseUrl = _mock.Url, apiKey = "sk-gen", model = "test/model",
            temperature = 0.1, maxOutputTokens = 1024, timeoutSeconds = 15, concurrency = 2,
            systemInstruction = "", extractionInstruction = "",
        })).EnsureSuccessStatusCode();
        return (admin, projectId);
    }

    private void StubGeneration(object schemaObject) =>
        _mock.Given(Request.Create().WithPath("/chat/completions").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new
            {
                choices = new[]
                {
                    new { message = new { role = "assistant", content = JsonSerializer.Serialize(schemaObject) }, finish_reason = "stop" },
                },
                usage = new { prompt_tokens = 120, completion_tokens = 200 },
            }));

    [Fact]
    public async Task Generate_returns_normalized_fields_and_prompt()
    {
        StubGeneration(new
        {
            fields = new object[]
            {
                new { key = "First Name!", label = "First Name", type = "shortText", required = true, description = "Given name", extractionInstruction = "The person's first name.", allowedValues = (string[]?)null },
                new { key = "incidentType", label = "Incident Type", type = "singleSelect", required = true, description = (string?)null, extractionInstruction = "One of the categories.", allowedValues = new[] { "Theft", "Fire", "Theft" } },
                new { key = "isUrgent", label = "Urgent", type = "boolean", required = false, description = (string?)null, extractionInstruction = (string?)null, allowedValues = (string[]?)null },
                // a select with no options must degrade to shortText
                new { key = "location", label = "Location", type = "singleSelect", required = false, description = (string?)null, extractionInstruction = (string?)null, allowedValues = Array.Empty<string>() },
            },
            systemInstruction = "You extract incident data.",
            extractionInstruction = "Dates ISO 8601; null when missing.",
        });

        var (admin, projectId) = await ProjectWithAiAsync();
        var response = await admin.PostAsJsonAsync($"/api/projects/{projectId}/schema/generate",
            new { description = "Extract incident details", sampleText = (string?)null });
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(ApiClient.Json);

        var fields = body.GetProperty("fields").EnumerateArray().ToList();
        fields.Should().HaveCount(4);

        // Bad key coerced to a valid camelCase identifier.
        var first = fields[0];
        first.GetProperty("key").GetString().Should().MatchRegex("^[a-z][a-zA-Z0-9]*$");
        first.GetProperty("required").GetBoolean().Should().BeTrue();

        // Select keeps de-duplicated allowed values.
        var incidentType = fields[1];
        incidentType.GetProperty("type").GetString().Should().Be("singleSelect");
        incidentType.GetProperty("allowedValues").EnumerateArray().Select(v => v.GetString())
            .Should().BeEquivalentTo(["Theft", "Fire"]);

        // Empty-option select degraded to shortText with null allowedValues.
        var location = fields[3];
        location.GetProperty("type").GetString().Should().Be("shortText");
        location.GetProperty("allowedValues").ValueKind.Should().Be(JsonValueKind.Null);

        body.GetProperty("systemInstruction").GetString().Should().Be("You extract incident data.");
        body.GetProperty("extractionInstruction").GetString().Should().Contain("ISO 8601");

        // The generated schema is valid enough to save via the normal PUT.
        var save = await admin.PutAsJsonAsync($"/api/projects/{projectId}/schema",
            new { fields = body.GetProperty("fields") });
        save.EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task Generated_prompt_can_be_persisted_and_read_back()
    {
        StubGeneration(new
        {
            fields = new object[] { new { key = "name", label = "Name", type = "shortText", required = true, description = (string?)null, extractionInstruction = (string?)null, allowedValues = (string[]?)null } },
            systemInstruction = "Sys line.",
            extractionInstruction = "Extraction line.",
        });
        var (admin, projectId) = await ProjectWithAiAsync();
        var gen = await (await admin.PostAsJsonAsync($"/api/projects/{projectId}/schema/generate",
            new { description = "x", sampleText = (string?)null })).Content.ReadFromJsonAsync<JsonElement>(ApiClient.Json);

        (await admin.PutAsJsonAsync($"/api/projects/{projectId}/prompt", new
        {
            systemInstruction = gen.GetProperty("systemInstruction").GetString(),
            extractionInstruction = gen.GetProperty("extractionInstruction").GetString(),
        })).EnsureSuccessStatusCode();

        var config = await admin.GetFromJsonAsync<JsonElement>($"/api/projects/{projectId}/ai-config", ApiClient.Json);
        config.GetProperty("systemInstruction").GetString().Should().Be("Sys line.");
        config.GetProperty("extractionInstruction").GetString().Should().Be("Extraction line.");
    }

    [Fact]
    public async Task Generate_without_ai_configured_is_rejected()
    {
        var admin = await factory.AdminClientAsync();
        var projectId = await admin.CreateProjectAsync();
        var response = await admin.PostAsJsonAsync($"/api/projects/{projectId}/schema/generate",
            new { description = "anything", sampleText = (string?)null });
        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await response.ErrorCodeAsync()).Should().Be("configuration_incomplete");
    }

    [Fact]
    public async Task Blank_description_is_rejected()
    {
        var (admin, projectId) = await ProjectWithAiAsync();
        var response = await admin.PostAsJsonAsync($"/api/projects/{projectId}/schema/generate",
            new { description = "", sampleText = (string?)null });
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Reviewer_cannot_generate()
    {
        var (admin, projectId) = await ProjectWithAiAsync();
        var (reviewer, reviewerId, _, _) = await factory.CreateReadyUserAsync(admin, "Reviewer");
        (await admin.PostAsJsonAsync($"/api/projects/{projectId}/members", new { userId = reviewerId }))
            .EnsureSuccessStatusCode();
        var response = await reviewer.PostAsJsonAsync($"/api/projects/{projectId}/schema/generate",
            new { description = "x", sampleText = (string?)null });
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }
}
