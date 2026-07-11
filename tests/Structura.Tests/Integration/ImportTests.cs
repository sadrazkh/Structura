using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using MiniExcelLibs;
using Structura.Web.Domain;
using Structura.Web.Persistence;
using Xunit;

namespace Structura.Tests.Integration;

[Collection("app")]
public class ImportTests(TestAppFactory factory)
{
    private static async Task<JsonElement> UploadAsync(HttpClient client, Guid projectId, string fileName, byte[] content)
    {
        using var form = new MultipartFormDataContent();
        var filePart = new ByteArrayContent(content);
        form.Add(filePart, "file", fileName);
        var response = await client.PostAsync($"/api/projects/{projectId}/imports/upload", form);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<JsonElement>(ApiClient.Json);
    }

    private static async Task<JsonElement> WaitForTerminalAsync(HttpClient client, Guid projectId, Guid runId)
    {
        for (var i = 0; i < 60; i++)
        {
            var run = await client.GetFromJsonAsync<JsonElement>(
                $"/api/projects/{projectId}/imports/{runId}", ApiClient.Json);
            var status = run.GetProperty("status").GetString();
            if (status is "Completed" or "CompletedWithErrors" or "Failed" or "Cancelled") return run;
            await Task.Delay(500);
        }
        throw new TimeoutException("Import run did not finish in time.");
    }

    private static byte[] BuildXlsx(IEnumerable<object> rows)
    {
        using var stream = new MemoryStream();
        stream.SaveAs(rows);
        return stream.ToArray();
    }

    [Fact]
    public async Task Xlsx_import_streams_rows_dedupes_and_reports_row_errors()
    {
        var admin = await factory.AdminClientAsync();
        var projectId = await admin.CreateProjectAsync();

        var rows = new List<object>();
        for (var i = 1; i <= 1200; i++)
            rows.Add(new { CaseNo = $"C-{i:D5}", ReportText = $"گزارش شماره {i} — incident report text {i}" });
        rows.Add(new { CaseNo = "C-00001", ReportText = "duplicate id row" });        // dup in file
        rows.Add(new { CaseNo = "C-99999", ReportText = "" });                        // empty text
        rows.Add(new { CaseNo = "", ReportText = "row without id" });                 // empty id

        var upload = await UploadAsync(admin, projectId, "reports.xlsx", BuildXlsx(rows));
        upload.GetProperty("columns").EnumerateArray().Select(c => c.GetString())
            .Should().Contain(["CaseNo", "ReportText"]);
        upload.GetProperty("previewRows").GetArrayLength().Should().Be(20);
        var runId = upload.GetProperty("id").GetGuid();

        (await admin.PostAsJsonAsync($"/api/projects/{projectId}/imports/{runId}/start",
            new { idColumn = "CaseNo", textColumn = "ReportText", generateIds = false }))
            .EnsureSuccessStatusCode();

        var run = await WaitForTerminalAsync(admin, projectId, runId);
        run.GetProperty("status").GetString().Should().Be("CompletedWithErrors");
        run.GetProperty("imported").GetInt32().Should().Be(1200);
        run.GetProperty("skippedDuplicates").GetInt32().Should().Be(1);
        run.GetProperty("failed").GetInt32().Should().Be(2);
        run.GetProperty("errors").GetArrayLength().Should().Be(2);

        // Re-importing the same file only skips (idempotent ingestion).
        var upload2 = await UploadAsync(admin, projectId, "reports.xlsx", BuildXlsx(rows));
        var runId2 = upload2.GetProperty("id").GetGuid();
        (await admin.PostAsJsonAsync($"/api/projects/{projectId}/imports/{runId2}/start",
            new { idColumn = "CaseNo", textColumn = "ReportText", generateIds = false }))
            .EnsureSuccessStatusCode();
        var run2 = await WaitForTerminalAsync(admin, projectId, runId2);
        run2.GetProperty("imported").GetInt32().Should().Be(0);
        run2.GetProperty("skippedDuplicates").GetInt32().Should().Be(1201);
    }

    [Fact]
    public async Task Csv_import_with_generated_ids_works()
    {
        var admin = await factory.AdminClientAsync();
        var projectId = await admin.CreateProjectAsync();

        var csv = new StringBuilder("Text\n");
        for (var i = 1; i <= 50; i++) csv.AppendLine($"\"Report body {i}, with a comma\"");
        var upload = await UploadAsync(admin, projectId, "rows.csv", Encoding.UTF8.GetBytes(csv.ToString()));
        var runId = upload.GetProperty("id").GetGuid();

        (await admin.PostAsJsonAsync($"/api/projects/{projectId}/imports/{runId}/start",
            new { idColumn = (string?)null, textColumn = "Text", generateIds = true }))
            .EnsureSuccessStatusCode();

        var run = await WaitForTerminalAsync(admin, projectId, runId);
        run.GetProperty("status").GetString().Should().Be("Completed");
        run.GetProperty("imported").GetInt32().Should().Be(50);

        var records = await admin.GetFromJsonAsync<JsonElement>(
            $"/api/projects/{projectId}/records", ApiClient.Json);
        records.GetProperty("items").EnumerateArray().First()
            .GetProperty("externalId").GetString().Should().StartWith("REC-");
    }

    [Fact]
    public async Task Invalid_files_are_rejected_at_upload()
    {
        var admin = await factory.AdminClientAsync();
        var projectId = await admin.CreateProjectAsync();

        // Wrong extension
        using var form1 = new MultipartFormDataContent
        {
            { new ByteArrayContent("hello"u8.ToArray()), "file", "notes.txt" },
        };
        var bad1 = await admin.PostAsync($"/api/projects/{projectId}/imports/upload", form1);
        bad1.StatusCode.Should().Be(System.Net.HttpStatusCode.BadRequest);
        (await bad1.ErrorCodeAsync()).Should().Be("import_invalid_file");

        // .xlsx that is not a zip container
        using var form2 = new MultipartFormDataContent
        {
            { new ByteArrayContent("this is not a workbook"u8.ToArray()), "file", "fake.xlsx" },
        };
        var bad2 = await admin.PostAsync($"/api/projects/{projectId}/imports/upload", form2);
        bad2.StatusCode.Should().Be(System.Net.HttpStatusCode.BadRequest);

        // CSV with null bytes (binary masquerading as text)
        using var form3 = new MultipartFormDataContent
        {
            { new ByteArrayContent([0x41, 0x00, 0x42]), "file", "binary.csv" },
        };
        var bad3 = await admin.PostAsync($"/api/projects/{projectId}/imports/upload", form3);
        bad3.StatusCode.Should().Be(System.Net.HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Import_resumes_from_the_checkpoint_after_a_restart()
    {
        var admin = await factory.AdminClientAsync();
        var projectId = await admin.CreateProjectAsync();

        // Simulate the post-crash state: a Running run whose checkpoint says 600 rows were
        // already handled (the first 600 records exist in the DB). The worker must continue
        // from row 601 without duplicating anything.
        var rows = new List<object>();
        for (var i = 1; i <= 1000; i++) rows.Add(new { Id = $"R-{i:D4}", Text = $"text {i}" });
        var xlsx = BuildXlsx(rows);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var dataDir = Path.Combine(Path.GetTempPath(), $"structura-resume-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dataDir);
        var run = new ImportRun
        {
            ProjectId = projectId,
            Source = ImportSource.Excel,
            FileName = "resume.xlsx",
            Status = ImportStatus.Running,
            Mapping = """{"idColumn":"Id","textColumn":"Text","generateIds":false}""",
            LastRowProcessed = 600,
            Imported = 600,
            CreatedById = db.Users.First().Id,
        };
        run.FilePath = Path.Combine(dataDir, $"{run.Id}.xlsx");
        await File.WriteAllBytesAsync(run.FilePath, xlsx);
        for (var i = 1; i <= 600; i++)
        {
            db.Records.Add(new Web.Domain.Record
            {
                ProjectId = projectId, ExternalId = $"R-{i:D4}", Text = $"text {i}", ImportRunId = run.Id,
            });
        }
        db.ImportRuns.Add(run);
        await db.SaveChangesAsync();

        var finished = await WaitForTerminalAsync(admin, projectId, run.Id);
        finished.GetProperty("status").GetString().Should().Be("Completed");
        finished.GetProperty("imported").GetInt32().Should().Be(1000);
        finished.GetProperty("skippedDuplicates").GetInt32().Should().Be(0, "the checkpoint must skip already-processed rows");

        var counts = await admin.GetFromJsonAsync<JsonElement>(
            $"/api/projects/{projectId}/records/counts", ApiClient.Json);
        counts.GetProperty("processing").EnumerateArray()
            .Single(g => g.GetProperty("status").GetString() == "Pending")
            .GetProperty("count").GetInt32().Should().Be(1000);
    }

    [Fact]
    public async Task Cancel_stops_the_import_at_a_chunk_boundary()
    {
        var admin = await factory.AdminClientAsync();
        var projectId = await admin.CreateProjectAsync();

        var rows = new List<object>();
        for (var i = 1; i <= 20_000; i++) rows.Add(new { Id = $"K-{i:D5}", Text = $"cancel test {i}" });
        var upload = await UploadAsync(admin, projectId, "big.xlsx", BuildXlsx(rows));
        var runId = upload.GetProperty("id").GetGuid();
        (await admin.PostAsJsonAsync($"/api/projects/{projectId}/imports/{runId}/start",
            new { idColumn = "Id", textColumn = "Text", generateIds = false })).EnsureSuccessStatusCode();

        // Cancel immediately — the worker must stop at the next chunk boundary.
        (await admin.PostAsync($"/api/projects/{projectId}/imports/{runId}/cancel", null))
            .EnsureSuccessStatusCode();

        var run = await WaitForTerminalAsync(admin, projectId, runId);
        run.GetProperty("status").GetString().Should().Be("Cancelled");
        run.GetProperty("imported").GetInt32().Should().BeLessThan(20_000);
    }

    [Fact]
    public async Task Manual_bulk_input_dedupes_and_generates_ids()
    {
        var admin = await factory.AdminClientAsync();
        var projectId = await admin.CreateProjectAsync();

        var response = await admin.PostAsJsonAsync($"/api/projects/{projectId}/records/manual", new
        {
            records = new object[]
            {
                new { externalId = "M-1", text = "متن اول" },
                new { externalId = "M-1", text = "duplicate of first" },
                new { externalId = (string?)null, text = "auto id row" },
            },
        });
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(ApiClient.Json);
        body.GetProperty("imported").GetInt32().Should().Be(2);
        body.GetProperty("skippedDuplicates").GetInt32().Should().Be(1);

        // Same call again → everything already exists.
        var again = await admin.PostAsJsonAsync($"/api/projects/{projectId}/records/manual", new
        {
            records = new object[] { new { externalId = "M-1", text = "متن اول" } },
        });
        var againBody = await again.Content.ReadFromJsonAsync<JsonElement>(ApiClient.Json);
        againBody.GetProperty("imported").GetInt32().Should().Be(0);
        againBody.GetProperty("skippedDuplicates").GetInt32().Should().Be(1);
    }
}
