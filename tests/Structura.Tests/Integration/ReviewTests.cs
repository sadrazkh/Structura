using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Structura.Web.Domain;
using Structura.Web.Persistence;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;
using Xunit;

namespace Structura.Tests.Integration;

[Collection("app")]
public class ReviewTests(TestAppFactory factory) : IDisposable
{
    private readonly WireMockServer _mock = WireMockServer.Start();

    public void Dispose() => _mock.Stop();

    /// <summary>Project with schema + AI config, N processed records, ready for assignment.</summary>
    private async Task<(HttpClient Admin, Guid ProjectId)> SetUpProcessedProjectAsync(int recordCount)
    {
        _mock.Given(Request.Create().WithPath("/chat/completions").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new
            {
                choices = new[]
                {
                    new
                    {
                        message = new
                        {
                            role = "assistant",
                            content = """{"firstName":"Sara","incidentType":"Theft","isUrgent":true}""",
                        },
                        finish_reason = "stop",
                    },
                },
                usage = new { prompt_tokens = 100, completion_tokens = 20 },
            }));

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
            provider = "OpenRouter", baseUrl = _mock.Url, apiKey = "sk-review-tests",
            model = "test/extractor", temperature = 0.1, maxOutputTokens = 512,
            timeoutSeconds = 15, concurrency = 4,
            systemInstruction = "x", extractionInstruction = "y",
        })).EnsureSuccessStatusCode();

        var records = Enumerable.Range(1, recordCount)
            .Select(i => new { externalId = $"RV-{i:D3}", text = $"گزارش شماره {i}" })
            .Cast<object>().ToList();
        (await admin.PostAsJsonAsync($"/api/projects/{projectId}/records/manual", new { records }))
            .EnsureSuccessStatusCode();

        var runResponse = await admin.PostAsJsonAsync($"/api/projects/{projectId}/runs",
            new { scope = "allPending", recordIds = (Guid[]?)null });
        runResponse.EnsureSuccessStatusCode();
        var runId = (await runResponse.Content.ReadFromJsonAsync<JsonElement>(ApiClient.Json))
            .GetProperty("id").GetGuid();
        for (var i = 0; i < 60; i++)
        {
            var run = await admin.GetFromJsonAsync<JsonElement>(
                $"/api/projects/{projectId}/runs/{runId}", ApiClient.Json);
            if (run.GetProperty("run").GetProperty("status").GetString() == "Completed") break;
            await Task.Delay(500);
        }
        return (admin, projectId);
    }

    private static async Task<List<Guid>> RecordIdsAsync(HttpClient admin, Guid projectId)
    {
        var list = await admin.GetFromJsonAsync<JsonElement>(
            $"/api/projects/{projectId}/records", ApiClient.Json);
        return list.GetProperty("items").EnumerateArray()
            .Select(i => i.GetProperty("id").GetGuid()).ToList();
    }

    private async Task<(HttpClient Client, Guid Id)> AddReviewerAsync(HttpClient admin, Guid projectId)
    {
        var (client, id, _, _) = await factory.CreateReadyUserAsync(admin, "Reviewer");
        (await admin.PostAsJsonAsync($"/api/projects/{projectId}/members", new { userId = id }))
            .EnsureSuccessStatusCode();
        return (client, id);
    }

    // ---------- assignment ----------

    [Fact]
    public async Task Distribute_assigns_records_evenly_between_reviewers()
    {
        var (admin, projectId) = await SetUpProcessedProjectAsync(10);
        var (_, reviewerA) = await AddReviewerAsync(admin, projectId);
        var (_, reviewerB) = await AddReviewerAsync(admin, projectId);
        var recordIds = await RecordIdsAsync(admin, projectId);

        var response = await admin.PostAsJsonAsync($"/api/projects/{projectId}/assignments", new
        {
            recordIds, mode = "distribute", reviewerId = (Guid?)null, reviewerIds = new[] { reviewerA, reviewerB },
        });
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(ApiClient.Json);
        body.GetProperty("assigned").GetInt32().Should().Be(10);

        var status = await admin.GetFromJsonAsync<JsonElement>(
            $"/api/projects/{projectId}/review-status", ApiClient.Json);
        var perReviewer = status.GetProperty("perReviewer").EnumerateArray().ToList();
        perReviewer.Should().HaveCount(2);
        perReviewer.Select(r => r.GetProperty("pending").GetInt32()).Should().AllBeEquivalentTo(5);
    }

    [Fact]
    public async Task Unprocessed_and_already_assigned_records_are_skipped_with_reasons()
    {
        var (admin, projectId) = await SetUpProcessedProjectAsync(2);
        var (_, reviewer) = await AddReviewerAsync(admin, projectId);
        // One extra record that was never processed.
        (await admin.PostAsJsonAsync($"/api/projects/{projectId}/records/manual",
            new { records = new object[] { new { externalId = "RV-RAW", text = "raw" } } }))
            .EnsureSuccessStatusCode();
        var recordIds = await RecordIdsAsync(admin, projectId);

        // First assignment: 2 processed assigned, 1 raw skipped.
        var first = await admin.PostAsJsonAsync($"/api/projects/{projectId}/assignments",
            new { recordIds, mode = "single", reviewerId = reviewer, reviewerIds = (Guid[]?)null });
        var firstBody = await first.Content.ReadFromJsonAsync<JsonElement>(ApiClient.Json);
        firstBody.GetProperty("assigned").GetInt32().Should().Be(2);
        firstBody.GetProperty("results").EnumerateArray()
            .Should().Contain(r => r.GetProperty("reason").GetString()!.Contains("Not processed"));

        // Second attempt: everything skipped (already assigned / unprocessed).
        var second = await admin.PostAsJsonAsync($"/api/projects/{projectId}/assignments",
            new { recordIds, mode = "single", reviewerId = reviewer, reviewerIds = (Guid[]?)null });
        (await second.Content.ReadFromJsonAsync<JsonElement>(ApiClient.Json))
            .GetProperty("assigned").GetInt32().Should().Be(0);
    }

    [Fact]
    public async Task Assigning_to_a_non_member_is_rejected()
    {
        var (admin, projectId) = await SetUpProcessedProjectAsync(1);
        var (_, outsiderId, _, _) = await factory.CreateReadyUserAsync(admin, "Reviewer"); // not a member
        var recordIds = await RecordIdsAsync(admin, projectId);

        var response = await admin.PostAsJsonAsync($"/api/projects/{projectId}/assignments",
            new { recordIds, mode = "single", reviewerId = outsiderId, reviewerIds = (Guid[]?)null });
        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    // ---------- the review flow ----------

    [Fact]
    public async Task Full_review_flow_open_edit_save_approve_keeps_ai_output_untouched()
    {
        var (admin, projectId) = await SetUpProcessedProjectAsync(2);
        var (reviewer, reviewerId) = await AddReviewerAsync(admin, projectId);
        var recordIds = await RecordIdsAsync(admin, projectId);
        (await admin.PostAsJsonAsync($"/api/projects/{projectId}/assignments",
            new { recordIds, mode = "single", reviewerId, reviewerIds = (Guid[]?)null }))
            .EnsureSuccessStatusCode();

        // Tasks summary shows the pending queue.
        var tasks = await reviewer.GetFromJsonAsync<JsonElement>("/api/review/tasks", ApiClient.Json);
        tasks.GetProperty("items")[0].GetProperty("pending").GetInt32().Should().Be(2);

        // Open: Assigned → InReview, form fields + AI output + working copy returned.
        var queue = await reviewer.GetFromJsonAsync<JsonElement>(
            $"/api/review/{projectId}/records", ApiClient.Json);
        var recordId = queue.GetProperty("items")[0].GetProperty("id").GetGuid();
        var opened = await reviewer.GetFromJsonAsync<JsonElement>(
            $"/api/review/{projectId}/records/{recordId}", ApiClient.Json);
        opened.GetProperty("reviewStatus").GetString().Should().Be("InReview");
        opened.GetProperty("fields").GetArrayLength().Should().Be(3);
        var version = opened.GetProperty("version").GetInt32();
        using var working = JsonDocument.Parse(opened.GetProperty("workingOutput").GetString()!);
        working.RootElement.GetProperty("firstName").GetString().Should().Be("Sara");

        // Save a human edit, then approve with another edit.
        var draft = await reviewer.PutAsJsonAsync($"/api/review/{projectId}/records/{recordId}", new
        {
            finalOutput = new { firstName = "سارا (edited)", incidentType = "Theft", isUrgent = false },
            version,
        });
        draft.EnsureSuccessStatusCode();
        version = (await draft.Content.ReadFromJsonAsync<JsonElement>(ApiClient.Json))
            .GetProperty("version").GetInt32();

        var approve = await reviewer.PostAsJsonAsync($"/api/review/{projectId}/records/{recordId}/approve", new
        {
            finalOutput = new { firstName = "سارا نهایی", incidentType = "Fire", isUrgent = true },
            version,
        });
        approve.EnsureSuccessStatusCode();
        var approveBody = await approve.Content.ReadFromJsonAsync<JsonElement>(ApiClient.Json);
        approveBody.GetProperty("nextRecordId").GetGuid().Should().NotBeEmpty("auto-advance needs the next record");
        approveBody.GetProperty("remaining").GetInt32().Should().Be(1);

        // Human output and AI output live separately: the extraction row is untouched.
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var record = db.Records.Single(r => r.Id == recordId);
        record.ReviewStatusValue.Should().Be(ReviewStatus.Approved);
        record.ReviewedById.Should().Be(reviewerId);
        JsonDocument.Parse(record.FinalOutput!).RootElement.GetProperty("firstName").GetString()
            .Should().Be("سارا نهایی");
        var extraction = db.ExtractionResults.Single(e => e.RecordId == recordId);
        JsonDocument.Parse(extraction.Output!).RootElement.GetProperty("firstName").GetString()
            .Should().Be("Sara", "the AI output must never be overwritten by human edits");
    }

    [Fact]
    public async Task Stale_version_save_returns_409_version_conflict()
    {
        var (admin, projectId) = await SetUpProcessedProjectAsync(1);
        var (reviewer, reviewerId) = await AddReviewerAsync(admin, projectId);
        var recordIds = await RecordIdsAsync(admin, projectId);
        (await admin.PostAsJsonAsync($"/api/projects/{projectId}/assignments",
            new { recordIds, mode = "single", reviewerId, reviewerIds = (Guid[]?)null }))
            .EnsureSuccessStatusCode();

        var opened = await reviewer.GetFromJsonAsync<JsonElement>(
            $"/api/review/{projectId}/records/{recordIds[0]}", ApiClient.Json);
        var version = opened.GetProperty("version").GetInt32();

        // First save wins…
        (await reviewer.PutAsJsonAsync($"/api/review/{projectId}/records/{recordIds[0]}",
            new { finalOutput = new { firstName = "A", incidentType = "Theft" }, version }))
            .EnsureSuccessStatusCode();

        // …a second save with the stale version is rejected, nothing overwritten.
        var stale = await reviewer.PutAsJsonAsync($"/api/review/{projectId}/records/{recordIds[0]}",
            new { finalOutput = new { firstName = "B", incidentType = "Fire" }, version });
        stale.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await stale.ErrorCodeAsync()).Should().Be("version_conflict");

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        JsonDocument.Parse(db.Records.Single(r => r.Id == recordIds[0]).FinalOutput!)
            .RootElement.GetProperty("firstName").GetString().Should().Be("A");
    }

    [Fact]
    public async Task Approve_with_validation_errors_is_blocked()
    {
        var (admin, projectId) = await SetUpProcessedProjectAsync(1);
        var (reviewer, reviewerId) = await AddReviewerAsync(admin, projectId);
        var recordIds = await RecordIdsAsync(admin, projectId);
        (await admin.PostAsJsonAsync($"/api/projects/{projectId}/assignments",
            new { recordIds, mode = "single", reviewerId, reviewerIds = (Guid[]?)null }))
            .EnsureSuccessStatusCode();
        var opened = await reviewer.GetFromJsonAsync<JsonElement>(
            $"/api/review/{projectId}/records/{recordIds[0]}", ApiClient.Json);

        var response = await reviewer.PostAsJsonAsync($"/api/review/{projectId}/records/{recordIds[0]}/approve", new
        {
            finalOutput = new { firstName = (string?)null, incidentType = "Arson" },
            version = opened.GetProperty("version").GetInt32(),
        });
        response.StatusCode.Should().Be((HttpStatusCode)422);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>(ApiClient.Json);
        problem.GetProperty("detail").GetString().Should().Contain("firstName").And.Contain("'Arson'");
    }

    [Fact]
    public async Task Reject_and_reprocess_require_a_note_and_set_statuses()
    {
        var (admin, projectId) = await SetUpProcessedProjectAsync(2);
        var (reviewer, reviewerId) = await AddReviewerAsync(admin, projectId);
        var recordIds = await RecordIdsAsync(admin, projectId);
        (await admin.PostAsJsonAsync($"/api/projects/{projectId}/assignments",
            new { recordIds, mode = "single", reviewerId, reviewerIds = (Guid[]?)null }))
            .EnsureSuccessStatusCode();

        Task<JsonElement> OpenAsync(Guid id) => reviewer.GetFromJsonAsync<JsonElement>(
            $"/api/review/{projectId}/records/{id}", ApiClient.Json);

        // Note is mandatory.
        var opened = await OpenAsync(recordIds[0]);
        var noNote = await reviewer.PostAsJsonAsync($"/api/review/{projectId}/records/{recordIds[0]}/reject",
            new { note = "", version = opened.GetProperty("version").GetInt32() });
        noNote.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        (await reviewer.PostAsJsonAsync($"/api/review/{projectId}/records/{recordIds[0]}/reject",
            new { note = "متن ناقص است", version = opened.GetProperty("version").GetInt32() }))
            .EnsureSuccessStatusCode();

        var opened2 = await OpenAsync(recordIds[1]);
        (await reviewer.PostAsJsonAsync($"/api/review/{projectId}/records/{recordIds[1]}/reprocess",
            new { note = "استخراج اشتباه است", version = opened2.GetProperty("version").GetInt32() }))
            .EnsureSuccessStatusCode();

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.Records.Single(r => r.Id == recordIds[0]).ReviewStatusValue.Should().Be(ReviewStatus.Rejected);
        var reprocess = db.Records.Single(r => r.Id == recordIds[1]);
        reprocess.ReviewStatusValue.Should().Be(ReviewStatus.ReprocessRequested);
        reprocess.ReviewNote.Should().Be("استخراج اشتباه است");
    }

    [Fact]
    public async Task Bulk_approve_revalidates_each_record_and_reports_per_record_results()
    {
        var (admin, projectId) = await SetUpProcessedProjectAsync(3);
        var (reviewer, reviewerId) = await AddReviewerAsync(admin, projectId);
        var recordIds = await RecordIdsAsync(admin, projectId);
        (await admin.PostAsJsonAsync($"/api/projects/{projectId}/assignments",
            new { recordIds, mode = "single", reviewerId, reviewerIds = (Guid[]?)null }))
            .EnsureSuccessStatusCode();

        // Sabotage one record's working copy with an invalid value (direct DB, simulating a bad draft).
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var bad = db.Records.Single(r => r.Id == recordIds[1]);
            bad.FinalOutput = """{"firstName":"X","incidentType":"Arson","isUrgent":false}""";
            await db.SaveChangesAsync();
        }

        var response = await reviewer.PostAsJsonAsync($"/api/review/{projectId}/bulk-approve",
            new { recordIds });
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(ApiClient.Json);
        body.GetProperty("approved").GetInt32().Should().Be(2);
        body.GetProperty("skipped").GetInt32().Should().Be(1);
        body.GetProperty("results").EnumerateArray()
            .Single(r => !r.GetProperty("ok").GetBoolean())
            .GetProperty("reason").GetString().Should().Contain("'Arson'");
    }

    [Fact]
    public async Task Reviewer_cannot_touch_another_reviewers_record()
    {
        var (admin, projectId) = await SetUpProcessedProjectAsync(1);
        var (_, ownerId) = await AddReviewerAsync(admin, projectId);
        var (intruder, _) = await AddReviewerAsync(admin, projectId);
        var recordIds = await RecordIdsAsync(admin, projectId);
        (await admin.PostAsJsonAsync($"/api/projects/{projectId}/assignments",
            new { recordIds, mode = "single", reviewerId = ownerId, reviewerIds = (Guid[]?)null }))
            .EnsureSuccessStatusCode();

        (await intruder.GetAsync($"/api/review/{projectId}/records/{recordIds[0]}"))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await intruder.PostAsJsonAsync($"/api/review/{projectId}/records/{recordIds[0]}/approve",
            new { finalOutput = new { firstName = "Hack", incidentType = "Theft" }, version = 1 }))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Unassign_returns_records_to_the_pool_but_approved_stays()
    {
        var (admin, projectId) = await SetUpProcessedProjectAsync(2);
        var (reviewer, reviewerId) = await AddReviewerAsync(admin, projectId);
        var recordIds = await RecordIdsAsync(admin, projectId);
        (await admin.PostAsJsonAsync($"/api/projects/{projectId}/assignments",
            new { recordIds, mode = "single", reviewerId, reviewerIds = (Guid[]?)null }))
            .EnsureSuccessStatusCode();

        // Approve the first record, then try to unassign both.
        var opened = await reviewer.GetFromJsonAsync<JsonElement>(
            $"/api/review/{projectId}/records/{recordIds[0]}", ApiClient.Json);
        (await reviewer.PostAsJsonAsync($"/api/review/{projectId}/records/{recordIds[0]}/approve", new
        {
            finalOutput = JsonDocument.Parse(opened.GetProperty("workingOutput").GetString()!).RootElement,
            version = opened.GetProperty("version").GetInt32(),
        })).EnsureSuccessStatusCode();

        var response = await admin.PostAsJsonAsync($"/api/projects/{projectId}/assignments/unassign",
            new { recordIds });
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(ApiClient.Json);
        body.GetProperty("unassigned").GetInt32().Should().Be(1, "approved records are final");
        body.GetProperty("skipped").GetInt32().Should().Be(1);
    }
}
