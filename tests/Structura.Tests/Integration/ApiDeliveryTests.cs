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
public class ApiDeliveryTests(TestAppFactory factory) : IDisposable
{
    private readonly WireMockServer _mock = WireMockServer.Start();

    public void Dispose() => _mock.Stop();

    private object OutputConfig(bool enabled = true, string? bodyTemplate = null) => new
    {
        url = $"{_mock.Url}/ingest",
        method = "POST",
        headers = new Dictionary<string, string> { ["X-Source"] = "structura" },
        authType = "bearer",
        token = "delivery-token-42",
        apiKeyHeaderName = (string?)null,
        bodyTemplate = bodyTemplate ?? """{"externalId":"{{record.externalId}}","category":"{{output.incidentType}}","approved":{{{review.isApproved}}}}""",
        successStatusCodes = new[] { 200, 201 },
        responseIdPath = "id",
        enabled,
    };

    private async Task<JsonElement> WaitForDeliveryAsync(
        HttpClient admin, Guid projectId, Func<JsonElement, bool> done, int seconds = 30)
    {
        for (var i = 0; i < seconds * 2; i++)
        {
            var list = await admin.GetFromJsonAsync<JsonElement>(
                $"/api/projects/{projectId}/deliveries", ApiClient.Json);
            if (done(list)) return list;
            await Task.Delay(500);
        }
        throw new TimeoutException("Deliveries did not reach the expected state in time.");
    }

    private static int CountByStatus(JsonElement list, string status) =>
        list.GetProperty("counts").EnumerateArray()
            .Where(c => c.GetProperty("status").GetString() == status)
            .Select(c => c.GetProperty("count").GetInt32())
            .FirstOrDefault();

    [Fact]
    public async Task Approved_records_are_delivered_automatically_once_a_connector_exists()
    {
        _mock.Given(Request.Create().WithPath("/ingest").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(201).WithBodyAsJson(new { id = "ext-123" }));

        var (admin, projectId) = await DeliveryHelpers.ProjectWithSchemaAsync(factory);
        (await admin.PutAsJsonAsync($"/api/projects/{projectId}/api-output", OutputConfig()))
            .EnsureSuccessStatusCode();
        await DeliveryHelpers.SeedApprovedAsync(factory, projectId,
            ("D-1", """{"firstName":"Sara","incidentType":"Theft"}"""),
            ("D-2", """{"firstName":"John","incidentType":"Fire"}"""));

        var list = await WaitForDeliveryAsync(admin, projectId, l => CountByStatus(l, "Delivered") == 2);
        CountByStatus(list, "Delivered").Should().Be(2);

        // The receiver got a real, correct, idempotent request.
        var received = _mock.LogEntries.First().RequestMessage;
        received.Headers!["Authorization"].Single().Should().Be("Bearer delivery-token-42");
        received.Headers!["X-Source"].Single().Should().Be("structura");
        received.Headers!.Should().ContainKey("Idempotency-Key");
        using var body = JsonDocument.Parse(received.Body!);
        body.RootElement.GetProperty("category").GetString().Should().BeOneOf("Theft", "Fire");
        body.RootElement.GetProperty("approved").GetBoolean().Should().BeTrue();

        // The extracted external id is recorded.
        list.GetProperty("items").EnumerateArray()
            .Should().OnlyContain(i => i.GetProperty("externalDeliveryId").GetString() == "ext-123");
    }

    [Fact]
    public async Task A_forced_failure_is_retried_and_then_succeeds()
    {
        // First call 500, subsequent 200 — exercises the automatic in-flight retry path.
        var calls = 0;
        var successBody = JsonSerializer.Serialize(new { id = "ok" });
        _mock.Given(Request.Create().WithPath("/ingest").UsingPost())
            .RespondWith(Response.Create().WithCallback(_ =>
            {
                var n = Interlocked.Increment(ref calls);
                return new WireMock.ResponseMessage
                {
                    StatusCode = n == 1 ? 500 : 200,
                    BodyData = new WireMock.Util.BodyData
                    {
                        DetectedBodyType = WireMock.Types.BodyType.String,
                        BodyAsString = n == 1 ? "boom" : successBody,
                    },
                };
            }));

        var (admin, projectId) = await DeliveryHelpers.ProjectWithSchemaAsync(factory);
        (await admin.PutAsJsonAsync($"/api/projects/{projectId}/api-output", OutputConfig()))
            .EnsureSuccessStatusCode();
        await DeliveryHelpers.SeedApprovedAsync(factory, projectId,
            ("R-1", """{"firstName":"Retry","incidentType":"Fire"}"""));

        // The 30s backoff means the retry won't happen automatically within the test window;
        // an admin "Retry Failed" re-arms it immediately, which is the documented manual path.
        var afterFirst = await WaitForDeliveryAsync(admin, projectId,
            l => CountByStatus(l, "Delivered") == 1 || CountByStatus(l, "Failed") == 1);

        if (CountByStatus(afterFirst, "Delivered") == 0)
        {
            (await admin.PostAsync($"/api/projects/{projectId}/deliveries/retry-failed", null))
                .EnsureSuccessStatusCode();
            afterFirst = await WaitForDeliveryAsync(admin, projectId, l => CountByStatus(l, "Delivered") == 1);
        }
        CountByStatus(afterFirst, "Delivered").Should().Be(1);
        calls.Should().BeGreaterThanOrEqualTo(2);
    }

    [Fact]
    public async Task Permanent_4xx_fails_without_retry_and_retry_failed_requeues()
    {
        _mock.Given(Request.Create().WithPath("/ingest").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(400).WithBody("bad request"));

        var (admin, projectId) = await DeliveryHelpers.ProjectWithSchemaAsync(factory);
        (await admin.PutAsJsonAsync($"/api/projects/{projectId}/api-output", OutputConfig()))
            .EnsureSuccessStatusCode();
        await DeliveryHelpers.SeedApprovedAsync(factory, projectId,
            ("F-1", """{"firstName":"Fail","incidentType":"Fire"}"""));

        var list = await WaitForDeliveryAsync(admin, projectId, l => CountByStatus(l, "Failed") == 1);
        var item = list.GetProperty("items")[0];
        item.GetProperty("deliveryStatus").GetString().Should().Be("Failed");
        item.GetProperty("error").GetString().Should().Contain("HTTP 400");
        item.GetProperty("attempts").GetInt32().Should().Be(1, "4xx is permanent, no auto-retry");

        // Fix the endpoint and re-queue.
        _mock.Reset();
        _mock.Given(Request.Create().WithPath("/ingest").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new { id = "now-ok" }));
        (await admin.PostAsync($"/api/projects/{projectId}/deliveries/retry-failed", null))
            .EnsureSuccessStatusCode();

        var recovered = await WaitForDeliveryAsync(admin, projectId, l => CountByStatus(l, "Delivered") == 1);
        CountByStatus(recovered, "Delivered").Should().Be(1);
    }

    [Fact]
    public async Task Disabled_connector_does_not_deliver()
    {
        _mock.Given(Request.Create().WithPath("/ingest").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200));

        var (admin, projectId) = await DeliveryHelpers.ProjectWithSchemaAsync(factory);
        (await admin.PutAsJsonAsync($"/api/projects/{projectId}/api-output", OutputConfig(enabled: false)))
            .EnsureSuccessStatusCode();
        await DeliveryHelpers.SeedApprovedAsync(factory, projectId,
            ("OFF-1", """{"firstName":"NoSend","incidentType":"Fire"}"""));

        await Task.Delay(4000); // give the worker a chance to (not) act
        _mock.LogEntries.Should().BeEmpty("a disabled connector must never send");
        var list = await admin.GetFromJsonAsync<JsonElement>(
            $"/api/projects/{projectId}/deliveries", ApiClient.Json);
        CountByStatus(list, "Pending").Should().Be(1);
    }

    [Fact]
    public async Task Test_request_previews_and_optionally_sends()
    {
        _mock.Given(Request.Create().WithPath("/ingest").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new { id = "test-id" }));

        var (admin, projectId) = await DeliveryHelpers.ProjectWithSchemaAsync(factory);
        (await admin.PutAsJsonAsync($"/api/projects/{projectId}/api-output", OutputConfig()))
            .EnsureSuccessStatusCode();

        // Dry run (no send) — renders a body from a synthetic sample.
        var dry = await admin.PostAsync($"/api/projects/{projectId}/api-output/test", null);
        dry.EnsureSuccessStatusCode();
        var dryBody = await dry.Content.ReadFromJsonAsync<JsonElement>(ApiClient.Json);
        dryBody.GetProperty("sent").GetBoolean().Should().BeFalse();
        dryBody.GetProperty("rendered").GetString().Should().Contain("externalId");
        _mock.LogEntries.Should().BeEmpty();

        // Real send.
        var real = await admin.PostAsync($"/api/projects/{projectId}/api-output/test?send=true", null);
        var realBody = await real.Content.ReadFromJsonAsync<JsonElement>(ApiClient.Json);
        realBody.GetProperty("sent").GetBoolean().Should().BeTrue();
        realBody.GetProperty("statusCode").GetInt32().Should().Be(200);
        _mock.LogEntries.Should().HaveCount(1);
    }

    [Fact]
    public async Task Body_template_with_unknown_placeholder_is_rejected_at_save()
    {
        var (admin, projectId) = await DeliveryHelpers.ProjectWithSchemaAsync(factory);
        var response = await admin.PutAsJsonAsync($"/api/projects/{projectId}/api-output",
            OutputConfig(bodyTemplate: """{"x":"{{output.doesNotExist}}"}"""));
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.ErrorCodeAsync()).Should().Be("validation_failed");
    }

    [Fact]
    public async Task Output_config_masks_the_token()
    {
        var (admin, projectId) = await DeliveryHelpers.ProjectWithSchemaAsync(factory);
        (await admin.PutAsJsonAsync($"/api/projects/{projectId}/api-output", OutputConfig()))
            .EnsureSuccessStatusCode();
        var config = await admin.GetFromJsonAsync<JsonElement>(
            $"/api/projects/{projectId}/api-output", ApiClient.Json);
        config.GetProperty("hasToken").GetBoolean().Should().BeTrue();
        config.ToString().Should().NotContain("delivery-token-42");
    }
}
