using MediaEngine.Contracts.System;

namespace MediaEngine.Api.Services.EngineLogs;

/// <summary>A finished export: the file name, its bytes and what it holds.</summary>
public sealed record LogExportResult(string FileName, byte[] Content, int LineCount, bool Truncated);

/// <summary>Builds the one plain-text log file a person attaches to a bug report.</summary>
public interface ILogExportService
{
    /// <summary>
    /// Reads the Engine log files (and, when asked, the system activity events) for the range, hides private details,
    /// and returns the finished file. The preview is this same call, so its count and size are exact.
    /// </summary>
    Task<LogExportResult> BuildAsync(LogExportRequest request, CancellationToken ct = default);
}
