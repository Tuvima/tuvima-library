using MediaEngine.Contracts.System;

namespace MediaEngine.Api.Services.EngineLogs;

/// <summary>
/// What an export will contain, worked out without writing the file: its name, how many lines and bytes it holds, and
/// which limits (if any) dropped older lines. Hand it back to <see cref="ILogExportService.WriteAsync"/> to write it.
/// </summary>
public abstract class LogExportPlan
{
    public abstract string FileName { get; }

    /// <summary>Entries in the file (a log entry with its detail lines counts once).</summary>
    public abstract int LineCount { get; }

    /// <summary>Exact size of the file in bytes.</summary>
    public abstract long SizeBytes { get; }

    /// <summary>True when the line limit or the size limit dropped older lines.</summary>
    public abstract bool Truncated { get; }
}

/// <summary>Builds the one plain-text log file a person attaches to a bug report.</summary>
public interface ILogExportService
{
    /// <summary>
    /// Reads the Engine log files (and, when asked, the system activity events), applies the filters, then the line and size
    /// limits, and counts what the file will hold. The preview is this call alone; nothing is written.
    /// </summary>
    Task<LogExportPlan> PlanAsync(LogExportRequest request, CancellationToken ct = default);

    /// <summary>Writes the file for a plan to <paramref name="output"/>, one line at a time.</summary>
    Task WriteAsync(LogExportPlan plan, Stream output, CancellationToken ct = default);
}
