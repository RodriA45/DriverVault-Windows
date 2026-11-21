namespace DriverVault.Models;

public enum DriverExportOutcome
{
    Succeeded,
    Skipped,
    Failed
}

public sealed class DriverExportItemResult
{
    public required DriverEntry Driver { get; init; }
    public required DriverExportOutcome Outcome { get; init; }
    public string TargetRelativeFolder { get; init; } = string.Empty;
    public string Message { get; init; } = string.Empty;
    public long BytesWritten { get; init; }
}

public sealed class ExtractionSummary
{
    public required string OutputFolderPath { get; init; }
    public string? ZipFilePath { get; init; }
    public string? ReportHtmlPath { get; init; }
    public string? ReportTxtPath { get; init; }
    public string? InstallerBatPath { get; init; }
    public bool WasCancelled { get; init; }
    public TimeSpan Elapsed { get; init; }
    public List<DriverExportItemResult> Items { get; } = [];

    public int SucceededCount => Items.Count(i => i.Outcome == DriverExportOutcome.Succeeded);
    public int SkippedCount => Items.Count(i => i.Outcome == DriverExportOutcome.Skipped);
    public int FailedCount => Items.Count(i => i.Outcome == DriverExportOutcome.Failed);
    public long TotalBytesWritten => Items.Where(i => i.Outcome == DriverExportOutcome.Succeeded).Sum(i => i.BytesWritten);
}
