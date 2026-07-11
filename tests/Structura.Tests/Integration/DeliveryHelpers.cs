using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Structura.Web.Domain;
using Structura.Web.Persistence;

namespace Structura.Tests.Integration;

/// <summary>Shared setup for export + delivery tests: a project with approved records.</summary>
public static class DeliveryHelpers
{
    public static async Task<(HttpClient Admin, Guid ProjectId)> ProjectWithSchemaAsync(TestAppFactory factory)
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
                new { key = "tags", label = "Tags", type = "multiSelect", allowedValues = new[] { "a", "b", "c" }, displayOrder = 3 },
            },
        })).EnsureSuccessStatusCode();
        return (admin, projectId);
    }

    /// <summary>Seeds already-approved records directly (bypassing the full review flow for speed).</summary>
    public static async Task SeedApprovedAsync(
        TestAppFactory factory, Guid projectId, params (string ExternalId, string FinalOutput)[] records)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var reviewer = db.Users.First();
        foreach (var (externalId, finalOutput) in records)
        {
            db.Records.Add(new Structura.Web.Domain.Record
            {
                ProjectId = projectId,
                ExternalId = externalId,
                Text = $"source text for {externalId}",
                ProcessingStatusValue = ProcessingStatus.Completed,
                ReviewStatusValue = ReviewStatus.Approved,
                DeliveryStatusValue = DeliveryStatus.Pending,
                FinalOutput = finalOutput,
                ReviewedById = reviewer.Id,
                ReviewedAt = DateTimeOffset.UtcNow,
            });
        }
        await db.SaveChangesAsync();
    }
}
