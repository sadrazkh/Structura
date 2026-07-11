using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Xunit;

namespace Structura.Tests.Integration;

[Collection("app")]
public class RecordsTests(TestAppFactory factory)
{
    private static async Task SeedRecordsAsync(HttpClient admin, Guid projectId, int count, string prefix = "S")
    {
        var records = Enumerable.Range(1, count)
            .Select(i => new { externalId = $"{prefix}-{i:D4}", text = $"searchable text {prefix} {i}" })
            .Cast<object>().ToList();
        (await admin.PostAsJsonAsync($"/api/projects/{projectId}/records/manual", new { records }))
            .EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task Records_list_supports_search_filters_and_keyset_pagination()
    {
        var admin = await factory.AdminClientAsync();
        var projectId = await admin.CreateProjectAsync();
        await SeedRecordsAsync(admin, projectId, 120);

        // Page 1
        var page1 = await admin.GetFromJsonAsync<JsonElement>(
            $"/api/projects/{projectId}/records", ApiClient.Json);
        page1.GetProperty("items").GetArrayLength().Should().Be(50);
        var cursor = page1.GetProperty("nextCursor").GetString();
        cursor.Should().NotBeNull();

        // Page 2 and 3 — no overlaps, all 120 covered.
        var seen = page1.GetProperty("items").EnumerateArray()
            .Select(i => i.GetProperty("id").GetGuid()).ToHashSet();
        var page2 = await admin.GetFromJsonAsync<JsonElement>(
            $"/api/projects/{projectId}/records?cursor={Uri.EscapeDataString(cursor!)}", ApiClient.Json);
        foreach (var item in page2.GetProperty("items").EnumerateArray())
            seen.Add(item.GetProperty("id").GetGuid()).Should().BeTrue("keyset pages must not overlap");
        var cursor2 = page2.GetProperty("nextCursor").GetString();
        var page3 = await admin.GetFromJsonAsync<JsonElement>(
            $"/api/projects/{projectId}/records?cursor={Uri.EscapeDataString(cursor2!)}", ApiClient.Json);
        page3.GetProperty("items").GetArrayLength().Should().Be(20);
        page3.GetProperty("nextCursor").ValueKind.Should().Be(JsonValueKind.Null);

        // Search
        var search = await admin.GetFromJsonAsync<JsonElement>(
            $"/api/projects/{projectId}/records?q=S-0007", ApiClient.Json);
        search.GetProperty("items").GetArrayLength().Should().Be(1);

        // Status filter
        var pending = await admin.GetFromJsonAsync<JsonElement>(
            $"/api/projects/{projectId}/records?processingStatus=Pending", ApiClient.Json);
        pending.GetProperty("items").GetArrayLength().Should().Be(50);
        var completed = await admin.GetFromJsonAsync<JsonElement>(
            $"/api/projects/{projectId}/records?processingStatus=Completed", ApiClient.Json);
        completed.GetProperty("items").GetArrayLength().Should().Be(0);
    }

    [Fact]
    public async Task Record_detail_returns_full_text_and_statuses()
    {
        var admin = await factory.AdminClientAsync();
        var projectId = await admin.CreateProjectAsync();
        await SeedRecordsAsync(admin, projectId, 1, "D");

        var list = await admin.GetFromJsonAsync<JsonElement>($"/api/projects/{projectId}/records", ApiClient.Json);
        var recordId = list.GetProperty("items")[0].GetProperty("id").GetGuid();

        var detail = await admin.GetFromJsonAsync<JsonElement>(
            $"/api/projects/{projectId}/records/{recordId}", ApiClient.Json);
        detail.GetProperty("externalId").GetString().Should().Be("D-0001");
        detail.GetProperty("processingStatus").GetString().Should().Be("Pending");
        detail.GetProperty("reviewStatus").GetString().Should().Be("Unassigned");
        detail.GetProperty("extractions").GetArrayLength().Should().Be(0);
        detail.GetProperty("version").GetInt32().Should().Be(1);
    }

    [Fact]
    public async Task Delete_only_removes_untouched_records()
    {
        var admin = await factory.AdminClientAsync();
        var projectId = await admin.CreateProjectAsync();
        await SeedRecordsAsync(admin, projectId, 3, "X");

        var list = await admin.GetFromJsonAsync<JsonElement>($"/api/projects/{projectId}/records", ApiClient.Json);
        var ids = list.GetProperty("items").EnumerateArray()
            .Select(i => i.GetProperty("id").GetGuid()).ToList();

        var response = await admin.PostAsJsonAsync($"/api/projects/{projectId}/records/delete",
            new { recordIds = ids });
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(ApiClient.Json);
        body.GetProperty("deleted").GetInt32().Should().Be(3);

        var after = await admin.GetFromJsonAsync<JsonElement>($"/api/projects/{projectId}/records", ApiClient.Json);
        after.GetProperty("items").GetArrayLength().Should().Be(0);
    }

    [Fact]
    public async Task Reviewer_only_sees_records_assigned_to_them()
    {
        var admin = await factory.AdminClientAsync();
        var projectId = await admin.CreateProjectAsync();
        var (reviewer, reviewerId, _, _) = await factory.CreateReadyUserAsync(admin, "Reviewer");
        (await admin.PostAsJsonAsync($"/api/projects/{projectId}/members", new { userId = reviewerId }))
            .EnsureSuccessStatusCode();
        await SeedRecordsAsync(admin, projectId, 5, "RV");

        // Nothing assigned yet → the reviewer's list is empty even though records exist.
        var list = await reviewer.GetFromJsonAsync<JsonElement>(
            $"/api/projects/{projectId}/records", ApiClient.Json);
        list.GetProperty("items").GetArrayLength().Should().Be(0);

        // Direct record access is also denied.
        var adminList = await admin.GetFromJsonAsync<JsonElement>(
            $"/api/projects/{projectId}/records", ApiClient.Json);
        var recordId = adminList.GetProperty("items")[0].GetProperty("id").GetGuid();
        (await reviewer.GetAsync($"/api/projects/{projectId}/records/{recordId}"))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }
}
