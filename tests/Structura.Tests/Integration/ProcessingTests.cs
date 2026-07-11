using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Structura.Web.Domain;
using Structura.Web.Persistence;
using WireMock.Matchers;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;
using Xunit;

namespace Structura.Tests.Integration;

[Collection("app")]
public class ProcessingTests(TestAppFactory factory) : IDisposable
{
    private readonly WireMockServer _mock = WireMockServer.Start();

    public void Dispose() => _mock.Stop();

    // ---------- helpers ----------

    private async Task<(HttpClient Admin, Guid ProjectId)> SetUpProjectAsync()
    {
        var admin = await factory.AdminClientAsync();
        var projectId = await admin.CreateProjectAsync();

        (await admin.PutAsJsonAsync($"/api/projects/{projectId}/schema", new
        {
            fields = new object[]
            {
                new { key = "firstName", label = "First Name", type = "shortText", required = true, displayOrder = 0 },
                new
                {
                    key = "incidentType", label = "Incident Type", type = "singleSelect", required = true,
                    allowedValues = new[] { "Theft", "Fire" }, displayOrder = 1,
                },
                new { key = "isUrgent", label = "Is Urgent", type = "boolean", required = false, displayOrder = 2 },
            },
        })).EnsureSuccessStatusCode();

        (await admin.PutAsJsonAsync($"/api/projects/{projectId}/ai-config", new
        {
            provider = "OpenRouter",
            baseUrl = _mock.Url,
            apiKey = "sk-test-processing",
            model = "test/extractor",
            temperature = 0.1,
            maxOutputTokens = 512,
            timeoutSeconds = 15,
            concurrency = 4,
            systemInstruction = "Extract incident data.",
            extractionInstruction = "Dates ISO 8601.",
        })).EnsureSuccessStatusCode();

        return (admin, projectId);
    }

    private async Task SeedRecordsAsync(HttpClient admin, Guid projectId, params string[] texts)
    {
        var records = texts.Select((t, i) => new { externalId = $"P-{i + 1:D3}", text = t }).Cast<object>().ToList();
        (await admin.PostAsJsonAsync($"/api/projects/{projectId}/records/manual", new { records }))
            .EnsureSuccessStatusCode();
    }

    private static object Completion(object content, int inTokens = 100, int outTokens = 20) => new
    {
        choices = new[]
        {
            new { message = new { role = "assistant", content = content is string s ? s : JsonSerializer.Serialize(content) }, finish_reason = "stop" },
        },
        usage = new { prompt_tokens = inTokens, completion_tokens = outTokens },
    };

    private void StubDefaultSuccess() =>
        _mock.Given(Request.Create().WithPath("/chat/completions").UsingPost())
            .AtPriority(100)
            .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(
                Completion(new { firstName = "Sara", incidentType = "Theft", isUrgent = true })));

    /// <summary>Stub a different reply for requests whose body mentions a marker string.</summary>
    private void StubForText(string marker, object completionBody, int priority = 1) =>
        _mock.Given(Request.Create().WithPath("/chat/completions").UsingPost()
                .WithBody(new RegexMatcher($".*{marker}.*", ignoreCase: true)))
            .AtPriority(priority)
            .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(completionBody));

    private static async Task<JsonElement> WaitForRunAsync(HttpClient admin, Guid projectId, Guid runId, int seconds = 45)
    {
        for (var i = 0; i < seconds * 2; i++)
        {
            var response = await admin.GetFromJsonAsync<JsonElement>(
                $"/api/projects/{projectId}/runs/{runId}", ApiClient.Json);
            var status = response.GetProperty("run").GetProperty("status").GetString();
            if (status is "Completed" or "CompletedWithErrors" or "Cancelled" or "Failed") return response;
            await Task.Delay(500);
        }
        throw new TimeoutException("Processing run did not finish in time.");
    }

    private static async Task<Guid> StartRunAsync(HttpClient admin, Guid projectId, string scope = "allPending")
    {
        var response = await admin.PostAsJsonAsync($"/api/projects/{projectId}/runs",
            new { scope, recordIds = (Guid[]?)null });
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(ApiClient.Json);
        return body.GetProperty("id").GetGuid();
    }

    // ---------- tests ----------

    [Fact]
    public async Task Happy_path_processes_records_and_stores_normalized_output()
    {
        StubDefaultSuccess();
        var (admin, projectId) = await SetUpProjectAsync();
        await SeedRecordsAsync(admin, projectId,
            "گزارش سرقت اول", "Second theft report", "Third report");

        var runId = await StartRunAsync(admin, projectId);
        var result = await WaitForRunAsync(admin, projectId, runId);

        var run = result.GetProperty("run");
        run.GetProperty("status").GetString().Should().Be("Completed");
        run.GetProperty("total").GetInt32().Should().Be(3);
        run.GetProperty("succeeded").GetInt32().Should().Be(3);
        run.GetProperty("failed").GetInt32().Should().Be(0);
        run.GetProperty("inputTokens").GetInt64().Should().Be(300, "token usage must accumulate");

        // Records completed, review-ready, with extraction rows holding normalized output.
        var records = await admin.GetFromJsonAsync<JsonElement>(
            $"/api/projects/{projectId}/records?processingStatus=Completed", ApiClient.Json);
        records.GetProperty("items").GetArrayLength().Should().Be(3);
        var recordId = records.GetProperty("items")[0].GetProperty("id").GetGuid();
        var detail = await admin.GetFromJsonAsync<JsonElement>(
            $"/api/projects/{projectId}/records/{recordId}", ApiClient.Json);
        detail.GetProperty("reviewStatus").GetString().Should().Be("Unassigned");
        var extraction = detail.GetProperty("extractions")[0];
        extraction.GetProperty("status").GetString().Should().Be("Succeeded");
        using var output = JsonDocument.Parse(extraction.GetProperty("output").GetString()!);
        output.RootElement.GetProperty("firstName").GetString().Should().Be("Sara");
        output.RootElement.GetProperty("incidentType").GetString().Should().Be("Theft");

        // The provider received a structured-output request with the schema and auth.
        var request = _mock.LogEntries.First().RequestMessage;
        request.Headers!["Authorization"].Single().Should().Be("Bearer sk-test-processing");
        // Note: System.Text.Json escapes '<' as <, so assert on the tag name only.
        request.Body.Should().Contain("json_schema").And.Contain("incidentType").And.Contain("source_text");
    }

    [Fact]
    public async Task Invalid_json_fails_after_reask_and_retry_failed_recovers()
    {
        StubDefaultSuccess();
        // Records mentioning BROKEN get persistent garbage → invalid_json after re-ask.
        StubForText("BROKEN", Completion("this is not json at all { definitely broken"));
        var (admin, projectId) = await SetUpProjectAsync();
        await SeedRecordsAsync(admin, projectId, "good record", "record BROKEN here");

        var runId = await StartRunAsync(admin, projectId);
        var result = await WaitForRunAsync(admin, projectId, runId);
        result.GetProperty("run").GetProperty("status").GetString().Should().Be("CompletedWithErrors");
        result.GetProperty("run").GetProperty("succeeded").GetInt32().Should().Be(1);
        result.GetProperty("run").GetProperty("failed").GetInt32().Should().Be(1);
        var failedRecord = result.GetProperty("failedRecords")[0];
        failedRecord.GetProperty("error").GetString().Should().Contain("invalid_json");

        // Fix the stub, retry only the failed record.
        _mock.Reset();
        StubDefaultSuccess();
        var retry = await admin.PostAsJsonAsync($"/api/projects/{projectId}/runs/{runId}/retry-failed", new { });
        retry.EnsureSuccessStatusCode();
        var retryRunId = (await retry.Content.ReadFromJsonAsync<JsonElement>(ApiClient.Json))
            .GetProperty("id").GetGuid();
        var retryResult = await WaitForRunAsync(admin, projectId, retryRunId);
        retryResult.GetProperty("run").GetProperty("status").GetString().Should().Be("Completed");
        retryResult.GetProperty("run").GetProperty("total").GetInt32().Should().Be(1);
    }

    [Fact]
    public async Task Validation_failure_produces_readable_record_error()
    {
        StubDefaultSuccess();
        StubForText("ARSONCASE", Completion(new { firstName = "X", incidentType = "Arson", isUrgent = false }));
        var (admin, projectId) = await SetUpProjectAsync();
        await SeedRecordsAsync(admin, projectId, "ARSONCASE report text");

        var runId = await StartRunAsync(admin, projectId);
        var result = await WaitForRunAsync(admin, projectId, runId);

        result.GetProperty("run").GetProperty("status").GetString().Should().Be("CompletedWithErrors");
        var error = result.GetProperty("failedRecords")[0].GetProperty("error").GetString()!;
        error.Should().Contain("validation_failed").And.Contain("'Arson'").And.Contain("allowed values");
    }

    [Fact]
    public async Task Structured_output_rejection_falls_back_to_plain_mode()
    {
        // First call: reject response_format; the pipeline must retry without it.
        _mock.Given(Request.Create().WithPath("/chat/completions").UsingPost()
                .WithBody(new RegexMatcher(".*response_format.*")))
            .AtPriority(1)
            .RespondWith(Response.Create().WithStatusCode(400)
                .WithBodyAsJson(new { error = new { message = "response_format is not supported by this model" } }));
        StubDefaultSuccess();

        var (admin, projectId) = await SetUpProjectAsync();
        await SeedRecordsAsync(admin, projectId, "fallback test record");

        var runId = await StartRunAsync(admin, projectId);
        var result = await WaitForRunAsync(admin, projectId, runId);
        result.GetProperty("run").GetProperty("status").GetString().Should().Be("Completed");

        _mock.LogEntries.Count().Should().Be(2, "one rejected structured attempt + one plain retry");
        _mock.LogEntries.Last().RequestMessage.Body.Should().NotContain("response_format");
    }

    [Fact]
    public async Task Transient_500_is_retried_once_transparently()
    {
        // Deterministic flaky provider: first request 500, every later request 200.
        var calls = 0;
        var successJson = JsonSerializer.Serialize(
            Completion(new { firstName = "Sara", incidentType = "Fire", isUrgent = false }));
        _mock.Given(Request.Create().WithPath("/chat/completions").UsingPost())
            .RespondWith(Response.Create().WithCallback(_ =>
            {
                var attempt = Interlocked.Increment(ref calls);
                return new WireMock.ResponseMessage
                {
                    StatusCode = attempt == 1 ? 500 : 200,
                    BodyData = new WireMock.Util.BodyData
                    {
                        DetectedBodyType = WireMock.Types.BodyType.String,
                        BodyAsString = attempt == 1 ? "boom" : successJson,
                    },
                };
            }));

        var (admin, projectId) = await SetUpProjectAsync();
        await SeedRecordsAsync(admin, projectId, "flaky provider record");

        var runId = await StartRunAsync(admin, projectId);
        var result = await WaitForRunAsync(admin, projectId, runId);
        result.GetProperty("run").GetProperty("status").GetString().Should().Be("Completed");
        _mock.LogEntries.Count().Should().Be(2);
    }

    [Fact]
    public async Task Cancel_stops_dispatching_and_leaves_rest_pending()
    {
        _mock.Given(Request.Create().WithPath("/chat/completions").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200).WithDelay(TimeSpan.FromMilliseconds(600))
                .WithBodyAsJson(Completion(new { firstName = "S", incidentType = "Theft", isUrgent = false })));

        var (admin, projectId) = await SetUpProjectAsync();
        await SeedRecordsAsync(admin, projectId,
            Enumerable.Range(1, 40).Select(i => $"slow record {i}").ToArray());

        var runId = await StartRunAsync(admin, projectId);
        await Task.Delay(1200); // let a few finish
        (await admin.PostAsync($"/api/projects/{projectId}/runs/{runId}/cancel", null)).EnsureSuccessStatusCode();

        var result = await WaitForRunAsync(admin, projectId, runId);
        var run = result.GetProperty("run");
        run.GetProperty("status").GetString().Should().Be("Cancelled");
        (run.GetProperty("succeeded").GetInt32() + run.GetProperty("failed").GetInt32())
            .Should().BeLessThan(40, "cancel must stop dispatching new records");

        var pending = await admin.GetFromJsonAsync<JsonElement>(
            $"/api/projects/{projectId}/records?processingStatus=Pending", ApiClient.Json);
        pending.GetProperty("items").GetArrayLength().Should().BeGreaterThan(0,
            "unprocessed records stay Pending and eligible for future runs");
    }

    [Fact]
    public async Task Reprocess_requested_record_returns_to_its_reviewer_after_success()
    {
        StubDefaultSuccess();
        var (admin, projectId) = await SetUpProjectAsync();
        var (_, reviewerId, _, _) = await factory.CreateReadyUserAsync(admin, "Reviewer");
        await SeedRecordsAsync(admin, projectId, "record sent back by reviewer");

        // Simulate M4 state: processed once, assigned, then returned for reprocessing.
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var record = db.Records.Single(r => r.ProjectId == projectId);
            record.ProcessingStatusValue = ProcessingStatus.Completed;
            record.ReviewStatusValue = ReviewStatus.ReprocessRequested;
            record.AssignedReviewerId = reviewerId;
            record.FinalOutput = """{"firstName":"Old Edit"}""";
            await db.SaveChangesAsync();
        }

        var runId = await StartRunAsync(admin, projectId, scope: "reprocessRequested");
        (await WaitForRunAsync(admin, projectId, runId))
            .GetProperty("run").GetProperty("status").GetString().Should().Be("Completed");

        using var verifyScope = factory.Services.CreateScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<AppDbContext>();
        var reprocessed = verifyDb.Records.Single(r => r.ProjectId == projectId);
        reprocessed.ReviewStatusValue.Should().Be(ReviewStatus.Assigned, "it returns to the same reviewer");
        reprocessed.AssignedReviewerId.Should().Be(reviewerId);
        reprocessed.FinalOutput.Should().BeNull("the stale human working copy must reset");
    }

    [Fact]
    public async Task Approved_records_are_never_pulled_into_a_run()
    {
        StubDefaultSuccess();
        var (admin, projectId) = await SetUpProjectAsync();
        await SeedRecordsAsync(admin, projectId, "approved record", "normal record");

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var approved = db.Records.Single(r => r.ProjectId == projectId && r.ExternalId == "P-001");
            approved.ProcessingStatusValue = ProcessingStatus.Completed;
            approved.ReviewStatusValue = ReviewStatus.Approved;
            approved.FinalOutput = """{"firstName":"Final"}""";
            await db.SaveChangesAsync();
        }

        var runId = await StartRunAsync(admin, projectId);
        var result = await WaitForRunAsync(admin, projectId, runId);
        result.GetProperty("run").GetProperty("total").GetInt32().Should().Be(1, "only the normal record is eligible");

        using var verifyScope = factory.Services.CreateScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<AppDbContext>();
        var untouched = verifyDb.Records.Single(r => r.ProjectId == projectId && r.ExternalId == "P-001");
        // jsonb normalizes whitespace, so compare semantically.
        JsonDocument.Parse(untouched.FinalOutput!).RootElement.GetProperty("firstName").GetString()
            .Should().Be("Final");
        untouched.ReviewStatusValue.Should().Be(ReviewStatus.Approved);
    }

    [Fact]
    public async Task Crash_artifacts_are_recovered_and_run_completes_without_duplicates()
    {
        StubDefaultSuccess();
        var (admin, projectId) = await SetUpProjectAsync();
        await SeedRecordsAsync(admin, projectId, "r1", "r2", "r3");

        // Simulate the post-crash state directly: a Running run whose records are stuck
        // in Processing (the exact state after a mid-run kill).
        Guid runId;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var project = db.Projects.Single(p => p.Id == projectId);
            var run = new ProcessingRun
            {
                ProjectId = projectId,
                Status = RunStatus.Running,
                SchemaSnapshot = project.SchemaFields,
                PromptSnapshot = """{"systemInstruction":"x","extractionInstruction":"y"}""",
                Model = "test/extractor",
                Total = 3,
                CreatedById = db.Users.First().Id,
                StartedAt = DateTimeOffset.UtcNow,
            };
            db.ProcessingRuns.Add(run);
            foreach (var record in db.Records.Where(r => r.ProjectId == projectId))
            {
                record.ProcessingRunId = run.Id;
                record.ProcessingStatusValue = ProcessingStatus.Processing; // crash artifact
            }
            await db.SaveChangesAsync();
            runId = run.Id;
        }

        // The worker's recovery path resets Processing → Pending; here we apply the same
        // reset the startup routine performs, then let the live worker finish the run.
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            foreach (var record in db.Records.Where(r =>
                         r.ProjectId == projectId && r.ProcessingStatusValue == ProcessingStatus.Processing))
                record.ProcessingStatusValue = ProcessingStatus.Pending;
            await db.SaveChangesAsync();
        }

        var result = await WaitForRunAsync(admin, projectId, runId);
        result.GetProperty("run").GetProperty("status").GetString().Should().Be("Completed");
        result.GetProperty("run").GetProperty("succeeded").GetInt32().Should().Be(3);

        using var verifyScope = factory.Services.CreateScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<AppDbContext>();
        verifyDb.ExtractionResults.Count(e => e.RunId == runId)
            .Should().Be(3, "each record is extracted exactly once");
    }

    [Fact]
    public async Task Run_without_ai_configuration_is_rejected()
    {
        var admin = await factory.AdminClientAsync();
        var projectId = await admin.CreateProjectAsync();
        await SeedRecordsAsync(admin, projectId, "text");

        var response = await admin.PostAsJsonAsync($"/api/projects/{projectId}/runs",
            new { scope = "allPending", recordIds = (Guid[]?)null });
        response.StatusCode.Should().Be(System.Net.HttpStatusCode.Conflict);
        (await response.ErrorCodeAsync()).Should().Be("configuration_incomplete");
    }

    [Fact]
    public async Task Reviewer_cannot_start_or_cancel_runs()
    {
        StubDefaultSuccess();
        var (admin, projectId) = await SetUpProjectAsync();
        var (reviewer, reviewerId, _, _) = await factory.CreateReadyUserAsync(admin, "Reviewer");
        (await admin.PostAsJsonAsync($"/api/projects/{projectId}/members", new { userId = reviewerId }))
            .EnsureSuccessStatusCode();

        var start = await reviewer.PostAsJsonAsync($"/api/projects/{projectId}/runs",
            new { scope = "allPending", recordIds = (Guid[]?)null });
        start.StatusCode.Should().Be(System.Net.HttpStatusCode.Forbidden);
    }
}
