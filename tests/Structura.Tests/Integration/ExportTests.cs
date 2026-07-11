using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using MiniExcelLibs;
using Xunit;

namespace Structura.Tests.Integration;

[Collection("app")]
public class ExportTests(TestAppFactory factory)
{
    [Fact]
    public async Task Excel_export_contains_headers_field_values_and_review_metadata()
    {
        var (admin, projectId) = await DeliveryHelpers.ProjectWithSchemaAsync(factory);
        await DeliveryHelpers.SeedApprovedAsync(factory, projectId,
            ("EX-1", """{"firstName":"سارا","incidentType":"Theft","isUrgent":true,"tags":["a","b"]}"""),
            ("EX-2", """{"firstName":"John","incidentType":"Fire","isUrgent":false,"tags":[]}"""));

        var response = await admin.GetAsync($"/api/projects/{projectId}/export/excel");
        response.EnsureSuccessStatusCode();
        response.Content.Headers.ContentType!.MediaType
            .Should().Be("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet");
        response.Content.Headers.ContentDisposition!.FileName.Should().Contain(".xlsx");

        var bytes = await response.Content.ReadAsByteArrayAsync();
        using var stream = new MemoryStream(bytes);
        var rows = stream.Query(useHeaderRow: true).Cast<IDictionary<string, object?>>().ToList();

        rows.Should().HaveCount(2);
        var headers = rows[0].Keys.ToList();
        headers.Should().ContainInOrder("Record ID", "First Name", "Incident Type", "Is Urgent", "Tags");
        headers.Should().Contain(["Review Status", "Reviewer", "Review Date"]);

        var sara = rows.Single(r => (string)r["Record ID"]! == "EX-1");
        ((object)sara["First Name"]!).ToString().Should().Be("سارا");
        ((object)sara["Incident Type"]!).ToString().Should().Be("Theft");
        ((object)sara["Is Urgent"]!).ToString().Should().Be("TRUE");
        ((object)sara["Tags"]!).ToString().Should().Be("a; b");
        ((object)sara["Review Status"]!).ToString().Should().Be("Approved");

        var john = rows.Single(r => (string)r["Record ID"]! == "EX-2");
        ((object)john["Is Urgent"]!).ToString().Should().Be("FALSE");
    }

    [Fact]
    public async Task Export_neutralizes_formula_injection()
    {
        var (admin, projectId) = await DeliveryHelpers.ProjectWithSchemaAsync(factory);
        await DeliveryHelpers.SeedApprovedAsync(factory, projectId,
            ("EX-EVIL", """{"firstName":"=cmd|'/c calc'!A1","incidentType":"Fire"}"""));

        var response = await admin.GetAsync($"/api/projects/{projectId}/export/excel");
        response.EnsureSuccessStatusCode();
        var bytes = await response.Content.ReadAsByteArrayAsync();
        using var stream = new MemoryStream(bytes);
        var rows = stream.Query(useHeaderRow: true).Cast<IDictionary<string, object?>>().ToList();

        ((object)rows.Single()["First Name"]!).ToString()
            .Should().StartWith("'=", "a leading = must be prefixed to defuse the formula");
    }

    [Fact]
    public async Task Csv_export_works()
    {
        var (admin, projectId) = await DeliveryHelpers.ProjectWithSchemaAsync(factory);
        await DeliveryHelpers.SeedApprovedAsync(factory, projectId,
            ("CSV-1", """{"firstName":"Ada","incidentType":"Fire"}"""));

        var response = await admin.GetAsync($"/api/projects/{projectId}/export/excel?format=csv");
        response.EnsureSuccessStatusCode();
        response.Content.Headers.ContentType!.MediaType.Should().Be("text/csv");
        var text = await response.Content.ReadAsStringAsync();
        text.Should().Contain("Record ID").And.Contain("CSV-1").And.Contain("Ada");
    }

    [Fact]
    public async Task Export_only_includes_approved_records()
    {
        var (admin, projectId) = await DeliveryHelpers.ProjectWithSchemaAsync(factory);
        await DeliveryHelpers.SeedApprovedAsync(factory, projectId,
            ("A-1", """{"firstName":"Approved","incidentType":"Fire"}"""));
        // A non-approved record via manual import (Pending/Unassigned).
        (await admin.PostAsJsonAsync($"/api/projects/{projectId}/records/manual",
            new { records = new object[] { new { externalId = "N-1", text = "not approved" } } }))
            .EnsureSuccessStatusCode();

        var response = await admin.GetAsync($"/api/projects/{projectId}/export/excel");
        var bytes = await response.Content.ReadAsByteArrayAsync();
        using var stream = new MemoryStream(bytes);
        var rows = stream.Query(useHeaderRow: true).Cast<IDictionary<string, object?>>().ToList();
        rows.Should().HaveCount(1);
        ((string)rows[0]["Record ID"]!).Should().Be("A-1");
    }

    [Fact]
    public async Task Reviewer_cannot_export()
    {
        var (admin, projectId) = await DeliveryHelpers.ProjectWithSchemaAsync(factory);
        var (reviewer, reviewerId, _, _) = await factory.CreateReadyUserAsync(admin, "Reviewer");
        (await admin.PostAsJsonAsync($"/api/projects/{projectId}/members", new { userId = reviewerId }))
            .EnsureSuccessStatusCode();

        (await reviewer.GetAsync($"/api/projects/{projectId}/export/excel"))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }
}
