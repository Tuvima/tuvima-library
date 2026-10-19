using System.Text.Json.Serialization;

namespace MediaEngine.Contracts.System;

/// <summary>The three level choices the log export offers.</summary>
public static class LogExportLevels
{
    public const string Errors = "errors";
    public const string WarningsAndAbove = "warnings_and_above";
    public const string Everything = "everything";

    public static bool IsKnown(string? level) =>
        level is Errors or WarningsAndAbove or Everything;
}

/// <summary>
/// What goes into a log export for a bug report. Sent as a body, never as a query string, so a search text or an id
/// is not left in browser history. Private details (keys, tokens, sign-in details, file and folder names) are hidden
/// unless <see cref="ShowFileNames"/> turns the file-name hiding off.
/// </summary>
public sealed class LogExportRequest
{
    /// <summary>Start of the time range. Required.</summary>
    [JsonPropertyName("from")]
    public DateTimeOffset? From { get; init; }

    /// <summary>End of the time range. Required.</summary>
    [JsonPropertyName("to")]
    public DateTimeOffset? To { get; init; }

    /// <summary><c>errors</c>, <c>warnings_and_above</c> or <c>everything</c>.</summary>
    [JsonPropertyName("level")]
    public string Level { get; init; } = LogExportLevels.WarningsAndAbove;

    /// <summary>Areas to keep (Ingestion, Playback, Network, Providers, Security, AI, Other). Empty or null means every area.</summary>
    [JsonPropertyName("areas")]
    public List<string>? Areas { get; init; } = [];

    /// <summary>A run id or item id; only lines and events that mention it are kept. Optional.</summary>
    [JsonPropertyName("item_or_run")]
    public string? ItemOrRun { get; init; }

    /// <summary>Keep the last part of each file name (never the folders). Off by default.</summary>
    [JsonPropertyName("show_file_names")]
    public bool ShowFileNames { get; init; }

    /// <summary>Add the system activity events for the same range.</summary>
    [JsonPropertyName("include_events")]
    public bool IncludeEvents { get; init; } = true;
}

/// <summary>What an export would contain, worked out exactly the way the export itself is.</summary>
public sealed class LogExportPreviewDto
{
    /// <summary>Entries in the file (a log entry with its detail lines counts once).</summary>
    [JsonPropertyName("line_count")]
    public int LineCount { get; init; }

    /// <summary>Size of the file in bytes.</summary>
    [JsonPropertyName("size_bytes")]
    public long SizeBytes { get; init; }

    /// <summary>True when the range held more than the limit and only the newest entries are kept.</summary>
    [JsonPropertyName("truncated")]
    public bool Truncated { get; init; }

    /// <summary>The name the downloaded file gets.</summary>
    [JsonPropertyName("file_name")]
    public string FileName { get; init; } = string.Empty;
}
