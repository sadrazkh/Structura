using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Structura.Web.Domain;
using Structura.Web.Infrastructure.Ai;
using Structura.Web.Infrastructure.Realtime;
using Structura.Web.Infrastructure.Secrets;
using Structura.Web.Persistence;

namespace Structura.Web.Infrastructure.Processing;

/// <summary>
/// Executes AI processing runs. The database is the queue: records are claimed with
/// FOR UPDATE SKIP LOCKED, processed concurrently (per-project concurrency), and every
/// state change is committed per record — so a crash/restart resumes automatically
/// (startup resets crash artifacts from Processing back to Pending).
/// </summary>
public sealed class ProcessingWorker(
    IServiceScopeFactory scopeFactory,
    ExtractionPipeline pipeline,
    IHubContext<ProgressHub> hub,
    ILogger<ProcessingWorker> logger) : BackgroundService
{
    private DateTimeOffset _lastBroadcast = DateTimeOffset.MinValue;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await RecoverCrashArtifactsAsync(stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var workedOnRun = await ProcessNextRunAsync(stoppingToken);
                if (!workedOnRun) await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                // shutting down — Processing rows are reset to Pending on next start
            }
            catch (Exception e)
            {
                logger.LogError(e, "ProcessingWorker loop failure");
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
        }
    }

    /// <summary>Records stuck in Processing can only be crash artifacts (single worker).</summary>
    private async Task RecoverCrashArtifactsAsync(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var recovered = await db.Records
            .Where(r => r.ProcessingStatusValue == ProcessingStatus.Processing)
            .ExecuteUpdateAsync(s => s
                .SetProperty(r => r.ProcessingStatusValue, ProcessingStatus.Pending)
                .SetProperty(r => r.Version, r => r.Version + 1), ct);
        if (recovered > 0)
            logger.LogWarning("Recovered {Count} record(s) stuck in Processing after a restart.", recovered);
    }

    private async Task<bool> ProcessNextRunAsync(CancellationToken ct)
    {
        Guid runId;
        Guid projectId;
        using (var scope = scopeFactory.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var run = await db.ProcessingRuns.AsNoTracking()
                .Where(r => r.Status == RunStatus.Running)
                .OrderBy(r => r.CreatedAt)
                .FirstOrDefaultAsync(ct);
            if (run is null) return false;
            runId = run.Id;
            projectId = run.ProjectId;
        }

        logger.LogInformation("Processing run {RunId}: started", runId);
        await ExecuteRunAsync(runId, projectId, ct);
        return true;
    }

    private async Task ExecuteRunAsync(Guid runId, Guid projectId, CancellationToken ct)
    {
        // Load execution config once per run (API key decrypted here, never persisted).
        AiConfigDocument config;
        string apiKey;
        SchemaDocument schema;
        PromptConfigDocument prompt;
        using (var scope = scopeFactory.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var secrets = scope.ServiceProvider.GetRequiredService<ISecretProtector>();
            var run = await db.ProcessingRuns.AsNoTracking().FirstAsync(r => r.Id == runId, ct);
            var project = await db.Projects.AsNoTracking().FirstAsync(p => p.Id == projectId, ct);
            var liveConfig = AiConfigDocument.ParseOrNull(project.AiConfig);
            if (liveConfig?.ApiKeyProtected is null)
            {
                await FinalizeRunAsync(runId, projectId, RunStatus.Failed, ct);
                logger.LogError("Run {RunId} failed: AI configuration is missing.", runId);
                return;
            }
            config = liveConfig;
            apiKey = secrets.Unprotect(liveConfig.ApiKeyProtected);
            schema = SchemaDocument.Parse(run.SchemaSnapshot);
            prompt = System.Text.Json.JsonSerializer.Deserialize<PromptConfigDocument>(
                run.PromptSnapshot, AiConfigDocument.JsonOptions) ?? new PromptConfigDocument();
        }

        var concurrency = Math.Clamp(config.Concurrency, 1, 16);
        using var semaphore = new SemaphoreSlim(concurrency);
        var inFlight = new List<Task>();

        while (!ct.IsCancellationRequested)
        {
            // Cooperative cancel: stop dispatching, let in-flight requests finish.
            bool cancelRequested;
            using (var scope = scopeFactory.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                cancelRequested = await db.ProcessingRuns.AsNoTracking()
                    .Where(r => r.Id == runId).Select(r => r.CancelRequested).FirstAsync(ct);
            }
            if (cancelRequested)
            {
                await Task.WhenAll(inFlight);
                await FinalizeRunAsync(runId, projectId, RunStatus.Cancelled, ct);
                logger.LogInformation("Processing run {RunId}: cancelled", runId);
                return;
            }

            var claimed = await ClaimRecordsAsync(runId, concurrency * 2, ct);
            if (claimed.Count == 0)
            {
                inFlight.RemoveAll(t => t.IsCompleted);
                if (inFlight.Count == 0)
                {
                    var finalStatus = await DetermineFinalStatusAsync(runId, ct);
                    await FinalizeRunAsync(runId, projectId, finalStatus, ct);
                    logger.LogInformation("Processing run {RunId}: {Status}", runId, finalStatus);
                    return;
                }
                await Task.WhenAny(Task.WhenAll(inFlight), Task.Delay(500, ct));
                continue;
            }

            foreach (var recordId in claimed)
            {
                await semaphore.WaitAsync(ct);
                inFlight.Add(Task.Run(async () =>
                {
                    try
                    {
                        await ProcessRecordAsync(runId, projectId, recordId, config, apiKey, schema, prompt, ct);
                    }
                    catch (Exception e) when (e is not OperationCanceledException)
                    {
                        logger.LogError(e, "Unexpected failure processing record {RecordId}", recordId);
                        await MarkRecordFailedAsync(runId, recordId, $"internal_error: {e.Message}");
                    }
                    finally
                    {
                        semaphore.Release();
                    }
                }, CancellationToken.None));
            }
            inFlight.RemoveAll(t => t.IsCompleted);
        }
    }

    /// <summary>Atomically claims a batch of Pending records for this run (SKIP LOCKED).</summary>
    private async Task<List<Guid>> ClaimRecordsAsync(Guid runId, int batchSize, CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.Database.SqlQuery<Guid>($"""
            UPDATE records SET processing_status = 'Processing', version = version + 1
            WHERE id IN (
                SELECT id FROM records
                WHERE processing_run_id = {runId} AND processing_status = 'Pending'
                LIMIT {batchSize}
                FOR UPDATE SKIP LOCKED)
            RETURNING id AS "Value"
            """).ToListAsync(ct);
    }

    private async Task ProcessRecordAsync(
        Guid runId, Guid projectId, Guid recordId,
        AiConfigDocument config, string apiKey, SchemaDocument schema, PromptConfigDocument prompt,
        CancellationToken ct)
    {
        string text;
        using (var scope = scopeFactory.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            text = await db.Records.AsNoTracking()
                .Where(r => r.Id == recordId).Select(r => r.Text).FirstAsync(ct);
        }

        var attempt = await pipeline.ExtractAsync(projectId, config, apiKey, schema, prompt, text, ct);

        using (var scope = scopeFactory.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await using var transaction = await db.Database.BeginTransactionAsync(ct);

            var extraction = new ExtractionResult
            {
                RecordId = recordId,
                RunId = runId,
                Model = config.Model,
                Status = attempt.Succeeded ? ExtractionStatus.Succeeded : ExtractionStatus.Failed,
                RawResponse = attempt.RawResponse,
                Output = attempt.Output,
                Error = attempt.Error,
                InputTokens = attempt.InputTokens,
                OutputTokens = attempt.OutputTokens,
                DurationMs = attempt.DurationMs,
            };
            db.ExtractionResults.Add(extraction);

            var record = await db.Records.FirstAsync(r => r.Id == recordId, ct);
            record.Version++;
            if (attempt.Succeeded)
            {
                record.ProcessingStatusValue = ProcessingStatus.Completed;
                record.ProcessingError = null;
                record.LatestResultId = extraction.Id;
                // Fresh AI output = fresh review baseline; the reviewer's working copy resets.
                record.FinalOutput = null;
                record.ReviewedById = null;
                record.ReviewedAt = null;
                if (record.ReviewStatusValue == ReviewStatus.ReprocessRequested)
                {
                    // Returned records go back to the same reviewer when still assigned (docs/02 F7).
                    record.ReviewStatusValue = record.AssignedReviewerId is not null
                        ? ReviewStatus.Assigned
                        : ReviewStatus.Unassigned;
                }
            }
            else
            {
                record.ProcessingStatusValue = ProcessingStatus.Failed;
                record.ProcessingError = attempt.Error;
                record.LatestResultId = extraction.Id;
            }

            await db.SaveChangesAsync(ct);

            var succeededDelta = attempt.Succeeded ? 1 : 0;
            var failedDelta = attempt.Succeeded ? 0 : 1;
            await db.ProcessingRuns
                .Where(r => r.Id == runId)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(r => r.Succeeded, r => r.Succeeded + succeededDelta)
                    .SetProperty(r => r.Failed, r => r.Failed + failedDelta)
                    .SetProperty(r => r.InputTokens, r => r.InputTokens + attempt.InputTokens)
                    .SetProperty(r => r.OutputTokens, r => r.OutputTokens + attempt.OutputTokens), ct);

            await transaction.CommitAsync(ct);
        }

        await BroadcastProgressAsync(runId, projectId, force: false, CancellationToken.None);
    }

    private async Task MarkRecordFailedAsync(Guid runId, Guid recordId, string error)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await db.Records.Where(r => r.Id == recordId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(r => r.ProcessingStatusValue, ProcessingStatus.Failed)
                .SetProperty(r => r.ProcessingError, error)
                .SetProperty(r => r.Version, r => r.Version + 1));
        await db.ProcessingRuns.Where(r => r.Id == runId)
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.Failed, r => r.Failed + 1));
    }

    private async Task<string> DetermineFinalStatusAsync(Guid runId, CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var failed = await db.ProcessingRuns.AsNoTracking()
            .Where(r => r.Id == runId).Select(r => r.Failed).FirstAsync(ct);
        return failed > 0 ? RunStatus.CompletedWithErrors : RunStatus.Completed;
    }

    private async Task FinalizeRunAsync(Guid runId, Guid projectId, string status, CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await db.ProcessingRuns.Where(r => r.Id == runId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(r => r.Status, status)
                .SetProperty(r => r.FinishedAt, DateTimeOffset.UtcNow), ct);
        await BroadcastProgressAsync(runId, projectId, force: true, CancellationToken.None);
    }

    private async Task BroadcastProgressAsync(Guid runId, Guid projectId, bool force, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        if (!force && now - _lastBroadcast < TimeSpan.FromMilliseconds(800)) return;
        _lastBroadcast = now;

        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var run = await db.ProcessingRuns.AsNoTracking().FirstOrDefaultAsync(r => r.Id == runId, ct);
        if (run is null) return;
        await hub.Clients.Group(ProgressHub.GroupName(projectId)).SendAsync("RunProgress", new
        {
            runId = run.Id,
            status = run.Status,
            total = run.Total,
            succeeded = run.Succeeded,
            failed = run.Failed,
            inputTokens = run.InputTokens,
            outputTokens = run.OutputTokens,
        }, ct);
    }
}
