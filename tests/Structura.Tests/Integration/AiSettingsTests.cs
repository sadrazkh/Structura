using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;
using Xunit;

namespace Structura.Tests.Integration;

[Collection("app")]
public class AiSettingsTests(TestAppFactory factory) : IDisposable
{
    private readonly WireMockServer _mock = WireMockServer.Start();

    public void Dispose() => _mock.Stop();

    private object SettingsPayload(string? apiKey = "sk-test-key-123456") => new
    {
        provider = "OpenRouter",
        baseUrl = _mock.Url,
        apiKey,
        model = "test/model-small",
        temperature = 0.1,
        maxOutputTokens = 512,
        timeoutSeconds = 30,
        concurrency = 3,
        systemInstruction = "You extract incident data.",
        extractionInstruction = "Dates must be ISO 8601.",
    };

    [Fact]
    public async Task Api_key_is_stored_encrypted_and_only_a_mask_is_ever_returned()
    {
        var admin = await factory.AdminClientAsync();
        var projectId = await admin.CreateProjectAsync();

        (await admin.PutAsJsonAsync($"/api/projects/{projectId}/ai-config", SettingsPayload()))
            .EnsureSuccessStatusCode();

        var fetched = await admin.GetFromJsonAsync<JsonElement>($"/api/projects/{projectId}/ai-config", ApiClient.Json);
        fetched.GetProperty("hasApiKey").GetBoolean().Should().BeTrue();
        var masked = fetched.GetProperty("apiKeyMasked").GetString()!;
        masked.Should().StartWith("••••").And.EndWith("3456");
        fetched.ToString().Should().NotContain("sk-test-key-123456", "the raw key must never be in a response");

        // Saving again without a key keeps the stored key (replace-only semantics).
        (await admin.PutAsJsonAsync($"/api/projects/{projectId}/ai-config", SettingsPayload(apiKey: null)))
            .EnsureSuccessStatusCode();
        var refetched = await admin.GetFromJsonAsync<JsonElement>($"/api/projects/{projectId}/ai-config", ApiClient.Json);
        refetched.GetProperty("hasApiKey").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task First_save_without_an_api_key_is_rejected()
    {
        var admin = await factory.AdminClientAsync();
        var projectId = await admin.CreateProjectAsync();
        var response = await admin.PutAsJsonAsync($"/api/projects/{projectId}/ai-config", SettingsPayload(apiKey: null));
        response.StatusCode.Should().Be(System.Net.HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Test_connection_succeeds_against_an_openai_compatible_endpoint()
    {
        _mock.Given(Request.Create().WithPath("/chat/completions").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new
            {
                choices = new[] { new { message = new { role = "assistant", content = "OK" }, finish_reason = "stop" } },
                usage = new { prompt_tokens = 12, completion_tokens = 2 },
            }));

        var admin = await factory.AdminClientAsync();
        var projectId = await admin.CreateProjectAsync();
        (await admin.PutAsJsonAsync($"/api/projects/{projectId}/ai-config", SettingsPayload()))
            .EnsureSuccessStatusCode();

        var test = await admin.PostAsync($"/api/projects/{projectId}/ai-config/test", null);
        test.EnsureSuccessStatusCode();
        var body = await test.Content.ReadFromJsonAsync<JsonElement>(ApiClient.Json);
        body.GetProperty("ok").GetBoolean().Should().BeTrue();
        body.GetProperty("sample").GetString().Should().Be("OK");
        body.GetProperty("inputTokens").GetInt32().Should().Be(12);

        // The mock must have received a real bearer-authenticated OpenAI-style request.
        var received = _mock.LogEntries.Single();
        received.RequestMessage.Headers!["Authorization"].Single().Should().Be("Bearer sk-test-key-123456");
        received.RequestMessage.Body.Should().Contain("test/model-small");
    }

    [Fact]
    public async Task Test_connection_reports_an_invalid_api_key_clearly()
    {
        _mock.Given(Request.Create().WithPath("/chat/completions").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(401)
                .WithBodyAsJson(new { error = new { message = "Invalid API key" } }));

        var admin = await factory.AdminClientAsync();
        var projectId = await admin.CreateProjectAsync();
        (await admin.PutAsJsonAsync($"/api/projects/{projectId}/ai-config", SettingsPayload()))
            .EnsureSuccessStatusCode();

        var test = await admin.PostAsync($"/api/projects/{projectId}/ai-config/test", null);
        var body = await test.Content.ReadFromJsonAsync<JsonElement>(ApiClient.Json);
        body.GetProperty("ok").GetBoolean().Should().BeFalse();
        body.GetProperty("error").GetString().Should().Contain("Invalid API key");
    }

    [Fact]
    public async Task Testing_before_configuring_returns_configuration_incomplete()
    {
        var admin = await factory.AdminClientAsync();
        var projectId = await admin.CreateProjectAsync();
        var response = await admin.PostAsync($"/api/projects/{projectId}/ai-config/test", null);
        response.StatusCode.Should().Be(System.Net.HttpStatusCode.Conflict);
        (await response.ErrorCodeAsync()).Should().Be("configuration_incomplete");
    }
}
