using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Structura.Web.Domain;
using Structura.Web.Infrastructure.Realtime;
using Structura.Web.Persistence;

namespace Structura.Web.Infrastructure.Import;

public sealed record ImportMapping(string? IdColumn, string TextColumn, bool GenerateIds);

/// <summary>
/// Executes file import runs. The database is the queue: any run in `Running` state is
/// picked up, and `last_row_processed` is the resume checkpoint — so an application
/// restart continues exactly where the previous instance stopped.
/// </summary>
public sealed class ImportWorker(
    IServiceScopeFactory scopeFactory,
    IHubContext<ProgressHub> hub,
    ILogger<ImportWorker> logger) : BackgroundService
{
    public const int ChunkSize = 500;
    public const int MaxStoredErrors = 1000;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var processedAny = await ProcessNextRunAsync(stoppingToken);
                if (!processedAny) await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                // shutting down — the checkpoint makes the run resumable
            }
            catch (Exception e)
            {
                logger.LogError(e, "ImportWorker loop failure");
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
        }
    }

    private async Task<bool> ProcessNextRunAsync(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var run = await db.ImportRuns
            .Where(r => r.Status == ImportStatus.Running && r.FilePath != null)
            .OrderBy(r => r.CreatedAt)
            .FirstOrDefaultAsync(ct);
        if (run is null) return false;

        logger.LogInformation("Import run {RunId}: starting at checkpoint row {Checkpoint}",
            run.Id, run.LastRowProcessed);
        try
        {
            await ProcessRunAsync(db, run, ct);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            logger.LogError(e, "Import run {RunId} failed", run.Id);
            run.Status = ImportStatus.Failed;
            AppendError(run, 0, $"Import failed: {e.Message}");
            run.FinishedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(CancellationToken.None);
            await BroadcastAsync(run, CancellationToken.None);
        }
        return true;
    }

    private async Task ProcessRunAsync(AppDbContext db, ImportRun run, CancellationToken ct)
    {
        var mapping = JsonSerializer.Deserialize<ImportMapping>(run.Mapping!, SchemaDocument.JsonOptions)
                      ?? throw new InvalidOperationException("Import run has no column mapping.");
        if (!File.Exists(run.FilePath))
            throw new InvalidOperationException("The uploaded file no longer exists on disk.");

        var chunk = new List<TableRow>(ChunkSize);
        var rowCount = 0;
        var headerChecked = false;

        foreach (var row in TableFileReader.ReadRows(run.FilePath!))
        {
            ct.ThrowIfCancellationRequested();
            rowCount = row.RowNumber;

            if (!headerChecked)
            {
                headerChecked = true;
                if (!row.Cells.ContainsKey(mapping.TextColumn))
                    throw new InvalidOperationException($"Text column '{mapping.TextColumn}' was not found in the file.");
                if (mapping.IdColumn is not null && !row.Cells.ContainsKey(mapping.IdColumn))
                    throw new InvalidOperationException($"ID column '{mapping.IdColumn}' was not found in the file.");
            }

            if (row.RowNumber <= run.LastRowProcessed) continue; // resume checkpoint

            chunk.Add(row);
            if (chunk.Count >= ChunkSize)
            {
                if (!await CommitChunkAsync(db, run, mapping, chunk, ct)) return; // cancelled
                chunk.Clear();
            }
        }

        if (chunk.Count > 0 && !await CommitChunkAsync(db, run, mapping, chunk, ct)) return;

        run.TotalRows = rowCount;
        run.Status = run.Failed > 0 ? ImportStatus.CompletedWithErrors : ImportStatus.Completed;
        run.FinishedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        await BroadcastAsync(run, ct);
        TryDeleteFile(run);
        logger.LogInformation(
            "Import run {RunId}: {Status} — {Imported} imported, {Skipped} duplicates, {Failed} failed",
            run.Id, run.Status, run.Imported, run.SkippedDuplicates, run.Failed);
    }

    /// <summary>Returns false when the run was cancelled by the admin.</summary>
    private async Task<bool> CommitChunkAsync(
        AppDbContext db, ImportRun run, ImportMapping mapping, List<TableRow> chunk, CancellationToken ct)
    {
        // Cooperative cancel: the endpoint flips the status; we notice at chunk boundaries.
        var currentStatus = await db.ImportRuns.AsNoTracking()
            .Where(r => r.Id == run.Id).Select(r => r.Status).FirstAsync(ct);
        if (currentStatus == ImportStatus.Cancelled)
        {
            TryDeleteFile(run);
            return false;
        }

        var candidates = new List<Record>(chunk.Count);
        var chunkIds = new HashSet<string>(StringComparer.Ordinal);

        foreach (var row in chunk)
        {
            var text = row.Cells.GetValueOrDefault(mapping.TextColumn);
            if (text is null)
            {
                run.Failed++;
                AppendError(run, row.RowNumber, "Text is empty.");
                continue;
            }
            if (text.Length > TableFileReader.MaxCellChars)
            {
                run.Failed++;
                AppendError(run, row.RowNumber, $"Text exceeds {TableFileReader.MaxCellChars / 1024} KB.");
                continue;
            }

            string externalId;
            if (mapping.GenerateIds || mapping.IdColumn is null)
            {
                externalId = GenerateId(run.Id, row.RowNumber);
            }
            else
            {
                var rawId = row.Cells.GetValueOrDefault(mapping.IdColumn);
                if (rawId is null)
                {
                    run.Failed++;
                    AppendError(run, row.RowNumber, "Record ID is empty.");
                    continue;
                }
                if (rawId.Length > 256)
                {
                    run.Failed++;
                    AppendError(run, row.RowNumber, "Record ID exceeds 256 characters.");
                    continue;
                }
                externalId = rawId;
            }

            if (!chunkIds.Add(externalId))
            {
                run.SkippedDuplicates++;
                continue;
            }

            candidates.Add(new Record
            {
                ProjectId = run.ProjectId,
                ExternalId = externalId,
                Text = text,
                ImportRunId = run.Id,
            });
        }

        if (candidates.Count > 0)
        {
            var candidateIds = candidates.Select(c => c.ExternalId).ToList();
            var existing = await db.Records
                .Where(r => r.ProjectId == run.ProjectId && candidateIds.Contains(r.ExternalId))
                .Select(r => r.ExternalId)
                .ToListAsync(ct);
            var existingSet = existing.ToHashSet(StringComparer.Ordinal);

            var fresh = candidates.Where(c => !existingSet.Contains(c.ExternalId)).ToList();
            run.SkippedDuplicates += candidates.Count - fresh.Count;
            run.Imported += fresh.Count;
            db.Records.AddRange(fresh);
        }

        run.LastRowProcessed = chunk[^1].RowNumber;
        await db.SaveChangesAsync(ct); // chunk records + counters + checkpoint in one transaction
        db.ChangeTracker.Clear();
        db.Attach(run);

        await BroadcastAsync(run, ct);
        return true;
    }

    private static string GenerateId(Guid runId, int rowNumber)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"{runId}:{rowNumber}"));
        return $"REC-{Convert.ToHexString(hash)[..10]}";
    }

    public static void AppendError(ImportRun run, int rowNumber, string message)
    {
        var errors = JsonSerializer.Deserialize<List<ImportRowError>>(run.Errors, SchemaDocument.JsonOptions) ?? [];
        if (errors.Count < MaxStoredErrors)
        {
            errors.Add(new ImportRowError(rowNumber, message));
            run.Errors = JsonSerializer.Serialize(errors, SchemaDocument.JsonOptions);
        }
    }

    private Task BroadcastAsync(ImportRun run, CancellationToken ct) =>
        hub.Clients.Group(ProgressHub.GroupName(run.ProjectId)).SendAsync("ImportProgress", new
        {
            runId = run.Id,
            status = run.Status,
            imported = run.Imported,
            skippedDuplicates = run.SkippedDuplicates,
            failed = run.Failed,
            lastRowProcessed = run.LastRowProcessed,
            totalRows = run.TotalRows,
        }, ct);

    private void TryDeleteFile(ImportRun run)
    {
        try
        {
            if (run.FilePath is not null && File.Exists(run.FilePath)) File.Delete(run.FilePath);
        }
        catch (IOException e)
        {
            logger.LogWarning(e, "Could not delete import file {Path}", run.FilePath);
        }
    }
}

public sealed record ImportRowError(int Row, string Message);
