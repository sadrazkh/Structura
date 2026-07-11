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
public class ApiInputTests(TestAppFactory factory) : IDisposable
{
    private readonly WireMockServer _mock = WireMockServer.Start();

    public void Dispose() => _mock.Stop();

    private object Config(string dataPath = "data.items") => new
    {
        url = $"{_mock.Url}/v2/reports",
        method = "GET",
        headers = new Dictionary<string, string> { ["X-Custom"] = "structura" },
        authType = "bearer",
        token = "external-api-token-9876",
        apiKeyHeaderName = (string?)null,
        dataPath,
        idPath = "id",
        textPath = "body",
    };

    private void StubReports() =>
        _mock.Given(Request.Create().WithPath("/v2/reports").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new
            {
                data = new
                {
                    items = new object[]
                    {
                        new { id = "A-1", body = "گزارش اول از API" },
                        new { id = "A-2", body = "Second report body" },
                        new { id = "A-3", body = "" },              // missing text → mapping error
                        new { id = (string?)null, body = "no id" }, // missing id → mapping error
                    },
                },
            }));

    [Fact]
    public async Task Test_endpoint_previews_mapped_records_without_inserting()
    {
        StubReports();
        var admin = await factory.AdminClientAsync();
        var projectId = await admin.CreateProjectAsync();

        (await admin.PutAsJsonAsync($"/api/projects/{projectId}/api-input", Config())).EnsureSuccessStatusCode();

        var test = await admin.PostAsync($"/api/projects/{projectId}/api-input/test", null);
        test.EnsureSuccessStatusCode();
        var body = await test.Content.ReadFromJsonAsync<JsonElement>(ApiClient.Json);
        body.GetProperty("ok").GetBoolean().Should().BeTrue();
        body.GetProperty("totalItems").GetInt32().Should().Be(4);
        body.GetProperty("mappingErrors").GetInt32().Should().Be(2);
        body.GetProperty("items").GetArrayLength().Should().Be(2);

        // Nothing inserted by /test.
        var records = await admin.GetFromJsonAsync<JsonElement>($"/api/projects/{projectId}/records", ApiClient.Json);
        records.GetProperty("items").GetArrayLength().Should().Be(0);

        // Auth + custom headers really reached the external API.
        var received = _mock.LogEntries.Single();
        received.RequestMessage.Headers!["Authorization"].Single().Should().Be("Bearer external-api-token-9876");
        received.RequestMessage.Headers!["X-Custom"].Single().Should().Be("structura");
    }

    [Fact]
    public async Task Fetch_inserts_records_and_is_idempotent()
    {
        StubReports();
        var admin = await factory.AdminClientAsync();
        var projectId = await admin.CreateProjectAsync();
        (await admin.PutAsJsonAsync($"/api/projects/{projectId}/api-input", Config())).EnsureSuccessStatusCode();

        var fetch = await admin.PostAsync($"/api/projects/{projectId}/api-input/fetch", null);
        fetch.EnsureSuccessStatusCode();
        var body = await fetch.Content.ReadFromJsonAsync<JsonElement>(ApiClient.Json);
        body.GetProperty("imported").GetInt32().Should().Be(2);

        var again = await admin.PostAsync($"/api/projects/{projectId}/api-input/fetch", null);
        var againBody = await again.Content.ReadFromJsonAsync<JsonElement>(ApiClient.Json);
        againBody.GetProperty("imported").GetInt32().Should().Be(0);
        againBody.GetProperty("skippedDuplicates").GetInt32().Should().Be(2);
    }

    [Fact]
    public async Task Wrong_data_path_is_reported_not_thrown()
    {
        StubReports();
        var admin = await factory.AdminClientAsync();
        var projectId = await admin.CreateProjectAsync();
        (await admin.PutAsJsonAsync($"/api/projects/{projectId}/api-input", Config(dataPath: "data.wrong")))
            .EnsureSuccessStatusCode();

        var test = await admin.PostAsync($"/api/projects/{projectId}/api-input/test", null);
        var body = await test.Content.ReadFromJsonAsync<JsonElement>(ApiClient.Json);
        body.GetProperty("ok").GetBoolean().Should().BeFalse();
        body.GetProperty("error").GetString().Should().Contain("data.wrong");
    }

    [Fact]
    public async Task Forbidden_headers_are_rejected_at_save()
    {
        var admin = await factory.AdminClientAsync();
        var projectId = await admin.CreateProjectAsync();
        var response = await admin.PutAsJsonAsync($"/api/projects/{projectId}/api-input", new
        {
            url = $"{_mock.Url}/x",
            method = "GET",
            headers = new Dictionary<string, string> { ["Host"] = "internal.service" },
            authType = "none",
            dataPath = "",
            textPath = "body",
        });
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }
}
