using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Structura.Web.Domain;
using Structura.Web.Infrastructure.Errors;
using Structura.Web.Infrastructure.Http;
using Structura.Web.Infrastructure.Realtime;
using Structura.Web.Infrastructure.Secrets;
using Structura.Web.Persistence;

namespace Structura.Web.Infrastructure.Delivery;

/// <summary>
/// Delivers approved records to each project's configured external API. The database is the
/// queue: approved records carry <c>delivery_status='Pending'</c>; this worker delivers them
/// for any project whose api_output_config is present and enabled. A crash simply leaves the
/// record Pending and it is retried — receivers dedupe on the Idempotency-Key header.
/// </summary>
public sealed class DeliveryWorker(
    IServiceScopeFactory scopeFactory,
    SafeHttpClientFactory safeHttp,
    ISecretProtector secrets,
    IHubContext<ProgressHub> hub,
    ILogger<DeliveryWorker> logger) : BackgroundService
{
    private const int Concurrency = 3;
    private const int BatchSize = 30;
    private const int MaxAttempts = 3;
    // Short in-worker backoff between automatic retries — long enough to ride out a blip,
    // short enough not to hold a delivery slot. Persistent failures fall to Failed and are
    // re-queued manually via "Retry Failed".
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var deliveredAny = await ProcessNextProjectAsync(stoppingToken);
                if (!deliveredAny) await Task.Delay(TimeSpan.FromSeconds(3), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                // shutting down — records stay Pending and resume next start
            }
            catch (Exception e)
            {
                logger.LogError(e, "DeliveryWorker loop failure");
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
        }
    }

    private async Task<bool> ProcessNextProjectAsync(CancellationToken ct)
    {
        Guid projectId;
        ApiOutputConfig config;
        List<Guid> recordIds;

        using (var scope = scopeFactory.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var now = DateTimeOffset.UtcNow;

            // Find a project that has deliverable records and an enabled connector.
            var candidate = await db.Records.AsNoTracking()
                .Where(r => r.ReviewStatusValue == ReviewStatus.Approved
                            && r.DeliveryStatusValue == DeliveryStatus.Pending
                            && (r.DeliveryNextRetryAt == null || r.DeliveryNextRetryAt <= now)
                            && r.Project.ApiOutputConfig != null)
                .Select(r => new { r.ProjectId, r.Project.ApiOutputConfig })
                .FirstOrDefaultAsync(ct);
            if (candidate is null) return false;

            var parsed = ApiOutputConfig.ParseOrNull(candidate.ApiOutputConfig);
            if (parsed is null || !parsed.Enabled || string.IsNullOrWhiteSpace(parsed.Url))
            {
                // Connector exists but is disabled/incomplete: park these records so we don't spin.
                await db.Records
                    .Where(r => r.ProjectId == candidate.ProjectId
                                && r.ReviewStatusValue == ReviewStatus.Approved
                                && r.DeliveryStatusValue == DeliveryStatus.Pending
                                && (r.DeliveryNextRetryAt == null || r.DeliveryNextRetryAt <= now))
                    .ExecuteUpdateAsync(s => s.SetProperty(r => r.DeliveryNextRetryAt, now.AddMinutes(5)), ct);
                return true;
            }

            projectId = candidate.ProjectId;
            config = parsed;
            recordIds = await db.Records.AsNoTracking()
                .Where(r => r.ProjectId == projectId
                            && r.ReviewStatusValue == ReviewStatus.Approved
                            && r.DeliveryStatusValue == DeliveryStatus.Pending
                            && (r.DeliveryNextRetryAt == null || r.DeliveryNextRetryAt <= now))
                .OrderBy(r => r.Id)
                .Take(BatchSize)
                .Select(r => r.Id)
                .ToListAsync(ct);
        }

        if (recordIds.Count == 0) return false;

        var token = config.TokenProtected is not null ? secrets.Unprotect(config.TokenProtected) : null;
        using var semaphore = new SemaphoreSlim(Concurrency);
        var tasks = recordIds.Select(async recordId =>
        {
            await semaphore.WaitAsync(ct);
            try { await DeliverRecordAsync(projectId, recordId, config, token, ct); }
            finally { semaphore.Release(); }
        });
        await Task.WhenAll(tasks);

        await BroadcastAsync(projectId, ct);
        return true;
    }

    private async Task DeliverRecordAsync(
        Guid projectId, Guid recordId, ApiOutputConfig config, string? token, CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var record = await db.Records
            .Include(r => r.ReviewedBy)
            .FirstOrDefaultAsync(r => r.Id == recordId, ct);
        // Guard against a state change between claim and delivery.
        if (record is null
            || record.ReviewStatusValue != ReviewStatus.Approved
            || record.DeliveryStatusValue != DeliveryStatus.Pending)
            return;

        string body;
        try
        {
            body = RenderBody(config, record);
        }
        catch (Exception e)
        {
            record.DeliveryAttempts++;
            record.DeliveryStatusValue = DeliveryStatus.Failed;
            record.DeliveryError = $"render_error: {e.Message}";
            record.DeliveryNextRetryAt = null;
            await db.SaveChangesAsync(ct);
            return;
        }

        // Automatic retry loop for transient errors; permanent errors stop immediately.
        for (var attempt = 1; ; attempt++)
        {
            var (ok, retryable, detail, externalId) = await SendAsync(config, token, record.Id, body, ct);
            record.DeliveryAttempts++;

            if (ok)
            {
                record.DeliveryStatusValue = DeliveryStatus.Delivered;
                record.DeliveredAt = DateTimeOffset.UtcNow;
                record.DeliveryError = null;
                record.DeliveryNextRetryAt = null;
                record.DeliveryExternalId = externalId;
                break;
            }
            if (!retryable || attempt >= MaxAttempts)
            {
                record.DeliveryStatusValue = DeliveryStatus.Failed;
                record.DeliveryError = detail;
                record.DeliveryNextRetryAt = null;
                break;
            }
            await Task.Delay(RetryDelay, ct);
        }
        await db.SaveChangesAsync(ct);
    }

    private static string RenderBody(ApiOutputConfig config, Record record)
    {
        var output = record.FinalOutput is not null
            ? JsonNode.Parse(record.FinalOutput) as JsonObject ?? new JsonObject()
            : new JsonObject();
        var context = new BodyTemplateRenderer.Context(
            record.Id, record.ExternalId, output,
            record.ReviewedBy?.FullName, record.ReviewedBy?.Email, record.ReviewedAt);

        return string.IsNullOrWhiteSpace(config.BodyTemplate)
            // Default body when no template is configured.
            ? new JsonObject
            {
                ["externalId"] = record.ExternalId,
                ["output"] = output.DeepClone(),
            }.ToJsonString()
            : BodyTemplateRenderer.Render(config.BodyTemplate, context);
    }

    private async Task<(bool Ok, bool Retryable, string? Detail, string? ExternalId)> SendAsync(
        ApiOutputConfig config, string? token, Guid recordId, string body, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(new HttpMethod(config.Method), config.Url)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        request.Headers.TryAddWithoutValidation("Idempotency-Key", recordId.ToString());
        foreach (var (key, value) in config.Headers)
            request.Headers.TryAddWithoutValidation(key, value);
        if (config.AuthType == "bearer" && token is not null)
            request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {token}");
        else if (config.AuthType == "apiKey" && token is not null)
            request.Headers.TryAddWithoutValidation(config.ApiKeyHeaderName, token);

        try
        {
            using var response = await safeHttp.SendAsync(request, SafeHttpProfile.Connector,
                TimeSpan.FromSeconds(30), ct);
            var responseBody = await SafeHttpClientFactory.ReadBodyCappedAsync(
                response, SafeHttpProfile.Connector, ct);
            var status = (int)response.StatusCode;

            if (config.SuccessStatusCodes.Contains(status))
                return (true, false, null, ExtractExternalId(config.ResponseIdPath, responseBody));

            var retryable = status is 408 or 429 || status >= 500;
            return (false, retryable, $"HTTP {status}: {Excerpt(responseBody)}", null);
        }
        catch (AppException e)
        {
            return (false, false, e.Message, null); // SSRF/validation → permanent
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            return (false, true, "Request timed out.", null);
        }
        catch (HttpRequestException e)
        {
            return (false, true, $"Unreachable: {e.Message}", null);
        }
    }

    private static string? ExtractExternalId(string? path, string responseBody)
    {
        if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(responseBody)) return null;
        try
        {
            JsonNode? node = JsonNode.Parse(responseBody);
            foreach (var segment in path.Split('.', StringSplitOptions.RemoveEmptyEntries))
                node = node?[segment];
            return node is JsonValue value ? value.ToString() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private Task BroadcastAsync(Guid projectId, CancellationToken ct) =>
        hub.Clients.Group(ProgressHub.GroupName(projectId)).SendAsync("DeliveryProgress", new { projectId }, ct);

    private static string Excerpt(string value) =>
        value.Length > 300 ? value[..300] + "…" : value;
}
