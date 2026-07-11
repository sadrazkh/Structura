namespace Structura.Web.Domain;

public static class ProcessingStatus
{
    public const string Pending = "Pending";
    public const string Processing = "Processing";
    public const string Completed = "Completed";
    public const string Failed = "Failed";
}

public static class ReviewStatus
{
    public const string Unassigned = "Unassigned";
    public const string Assigned = "Assigned";
    public const string InReview = "InReview";
    public const string Approved = "Approved";
    public const string Rejected = "Rejected";
    public const string ReprocessRequested = "ReprocessRequested";
}

public static class DeliveryStatus
{
    public const string Pending = "Pending";
    public const string Delivered = "Delivered";
    public const string Failed = "Failed";
}

public static class ImportStatus
{
    public const string AwaitingMapping = "AwaitingMapping";
    public const string Running = "Running";
    public const string Completed = "Completed";
    public const string CompletedWithErrors = "CompletedWithErrors";
    public const string Failed = "Failed";
    public const string Cancelled = "Cancelled";
}

public static class ImportSource
{
    public const string Excel = "Excel";
    public const string Csv = "Csv";
    public const string Manual = "Manual";
    public const string Api = "Api";
}

public static class RunStatus
{
    public const string Running = "Running";
    public const string Completed = "Completed";
    public const string CompletedWithErrors = "CompletedWithErrors";
    public const string Cancelled = "Cancelled";
    public const string Failed = "Failed";
}

public static class ExtractionStatus
{
    public const string Succeeded = "Succeeded";
    public const string Failed = "Failed";
}
