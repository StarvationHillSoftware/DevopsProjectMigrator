namespace DevOpsProjectMigrator.Models;

public enum MigrationItemStatus
{
    Completed,
    Skipped,
    Failed
}

public sealed record MigrationItemResult(string Category, string Item, MigrationItemStatus Status, string Message);

public sealed class MigrationReport
{
    public string SourceProjectName { get; set; } = string.Empty;
    public string DestinationProjectName { get; set; } = string.Empty;
    public DateTimeOffset StartedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? CompletedAt { get; set; }
    public List<MigrationItemResult> Items { get; } = [];

    public int CompletedCount => Items.Count(item => item.Status == MigrationItemStatus.Completed);
    public int SkippedCount => Items.Count(item => item.Status == MigrationItemStatus.Skipped);
    public int FailedCount => Items.Count(item => item.Status == MigrationItemStatus.Failed);
}
