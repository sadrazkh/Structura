namespace Structura.Web.Domain;

public class Record : IHasTimestamps
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid ProjectId { get; set; }
    public Project Project { get; set; } = null!;
    public string ExternalId { get; set; } = null!;
    public string Text { get; set; } = null!;

    public string ProcessingStatusValue { get; set; } = ProcessingStatus.Pending;
    public string ReviewStatusValue { get; set; } = ReviewStatus.Unassigned;
    public string DeliveryStatusValue { get; set; } = DeliveryStatus.Pending;
    public string? ProcessingError { get; set; }

    public Guid? LatestResultId { get; set; }
    public Guid? ProcessingRunId { get; set; }
    public Guid? ImportRunId { get; set; }

    public Guid? AssignedReviewerId { get; set; }
    public User? AssignedReviewer { get; set; }
    public DateTimeOffset? AssignedAt { get; set; }

    /// <summary>Human working/approved copy (JSONB) — never mixes with AI output rows.</summary>
    public string? FinalOutput { get; set; }
    public Guid? ReviewedById { get; set; }
    public DateTimeOffset? ReviewedAt { get; set; }
    public string? ReviewNote { get; set; }

    public int DeliveryAttempts { get; set; }
    public DateTimeOffset? DeliveredAt { get; set; }
    public string? DeliveryError { get; set; }

    public int Version { get; set; } = 1;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public class ImportRun : IHasTimestamps
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid ProjectId { get; set; }
    public Project Project { get; set; } = null!;
    public string Source { get; set; } = ImportSource.Excel;
    public string? FileName { get; set; }
    public string? FilePath { get; set; }
    /// <summary>{"idColumn": string|null, "textColumn": string, "generateIds": bool}</summary>
    public string? Mapping { get; set; }
    public string Status { get; set; } = ImportStatus.AwaitingMapping;
    public int? TotalRows { get; set; }
    public int Imported { get; set; }
    public int SkippedDuplicates { get; set; }
    public int Failed { get; set; }
    /// <summary>[{"row": int, "message": string}] capped at 1000 entries (JSONB).</summary>
    public string Errors { get; set; } = "[]";
    /// <summary>Resume checkpoint: 1-based data-row number of the last fully processed row.</summary>
    public int LastRowProcessed { get; set; }
    public Guid CreatedById { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public class ProcessingRun : IHasTimestamps
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid ProjectId { get; set; }
    public Project Project { get; set; } = null!;
    public string Status { get; set; } = RunStatus.Running;
    public string SchemaSnapshot { get; set; } = null!;
    public string PromptSnapshot { get; set; } = null!;
    public string Model { get; set; } = "";
    public int Total { get; set; }
    public int Succeeded { get; set; }
    public int Failed { get; set; }
    public bool CancelRequested { get; set; }
    public long InputTokens { get; set; }
    public long OutputTokens { get; set; }
    public Guid CreatedById { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public class ExtractionResult
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid RecordId { get; set; }
    public Record Record { get; set; } = null!;
    public Guid RunId { get; set; }
    public string Model { get; set; } = "";
    public string Status { get; set; } = ExtractionStatus.Succeeded;
    /// <summary>Raw model response, kept verbatim for traceability.</summary>
    public string? RawResponse { get; set; }
    /// <summary>Parsed AI values (JSONB) — never edited by humans.</summary>
    public string? Output { get; set; }
    public string? Error { get; set; }
    public int InputTokens { get; set; }
    public int OutputTokens { get; set; }
    public int DurationMs { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
