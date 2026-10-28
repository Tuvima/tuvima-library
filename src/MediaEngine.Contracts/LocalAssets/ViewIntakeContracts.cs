using System.Text.Json.Serialization;

namespace MediaEngine.Contracts.LocalAssets;

/// <summary>What is waiting in, and refused from, the Import area for one kind (Photo and video filing spec D1, D6, D9).</summary>
public sealed record ViewIntakeSummaryDto(
    [property: JsonPropertyName("content_kind")] string ContentKind,
    [property: JsonPropertyName("import_path")] string? ImportPath,
    [property: JsonPropertyName("rejected_path")] string? RejectedPath,
    [property: JsonPropertyName("available")] bool Available,
    [property: JsonPropertyName("waiting")] int Waiting,
    [property: JsonPropertyName("waiting_note")] string? WaitingNote,
    [property: JsonPropertyName("stray_count")] int StrayCount,
    [property: JsonPropertyName("retention_days")] int RetentionDays,
    [property: JsonPropertyName("layout_pattern")] string LayoutPattern,
    [property: JsonPropertyName("rejected")] IReadOnlyList<ViewIntakeRejectedDto> Rejected);

/// <summary>One refused file: its own name, the plain reason, and when it will be removed.</summary>
public sealed record ViewIntakeRejectedDto(
    [property: JsonPropertyName("id")] Guid Id,
    [property: JsonPropertyName("file_name")] string FileName,
    [property: JsonPropertyName("reason")] string Reason,
    [property: JsonPropertyName("rejected_at")] DateTimeOffset RejectedAt,
    [property: JsonPropertyName("expires_at")] DateTimeOffset? ExpiresAt);

/// <summary>What one Scan now did, in counts.</summary>
public sealed record ViewIntakeScanResultDto(
    [property: JsonPropertyName("filed")] int Filed,
    [property: JsonPropertyName("duplicates")] int Duplicates,
    [property: JsonPropertyName("rejected")] int Rejected,
    [property: JsonPropertyName("waiting")] int Waiting,
    [property: JsonPropertyName("message")] string Message);

public sealed record ViewFilingLayoutDto(
    [property: JsonPropertyName("pattern")] string Pattern,
    [property: JsonPropertyName("default_pattern")] string DefaultPattern,
    [property: JsonPropertyName("example")] string Example);

public sealed record UpdateViewFilingLayoutRequest(
    [property: JsonPropertyName("pattern")] string? Pattern);

/// <summary>One line of a View library's Activity tab.</summary>
public sealed record ViewActivityEntryDto(
    [property: JsonPropertyName("occurred_at")] DateTimeOffset OccurredAt,
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("text")] string Text);

public sealed record ViewActivityDto(
    [property: JsonPropertyName("entries")] IReadOnlyList<ViewActivityEntryDto> Entries);
