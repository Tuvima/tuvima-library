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

/// <summary>How long Library keeps what it knows about a file that was deleted outside Library. <c>retention_days</c> 0 means never remove.</summary>
public sealed record ViewMissingFileSettingsDto(
    [property: JsonPropertyName("retention_days")] int RetentionDays,
    [property: JsonPropertyName("default_retention_days")] int DefaultRetentionDays);

public sealed record UpdateViewMissingFileSettingsRequest(
    [property: JsonPropertyName("retention_days")] int RetentionDays);

/// <summary>One line of a View library's Activity tab.</summary>
public sealed record ViewActivityEntryDto(
    [property: JsonPropertyName("occurred_at")] DateTimeOffset OccurredAt,
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("text")] string Text);

public sealed record ViewActivityDto(
    [property: JsonPropertyName("entries")] IReadOnlyList<ViewActivityEntryDto> Entries);

/// <summary>One copy inside a group of exact duplicates.</summary>
public sealed record ViewDuplicateCopyDto(
    [property: JsonPropertyName("item_id")] Guid ItemId,
    [property: JsonPropertyName("file_name")] string FileName,
    [property: JsonPropertyName("taken_at")] string? TakenAt,
    [property: JsonPropertyName("added_at")] string AddedAt,
    [property: JsonPropertyName("size_bytes")] long SizeBytes);

/// <summary>A group of exact copies in one place. <c>Scope</c> is "shared" or "personal".</summary>
public sealed record ViewDuplicateGroupDto(
    [property: JsonPropertyName("id")] Guid Id,
    [property: JsonPropertyName("scope")] string Scope,
    [property: JsonPropertyName("copies")] IReadOnlyList<ViewDuplicateCopyDto> Copies);

/// <summary>What the duplicates cleanup job last found, for the administrators who may act on it.</summary>
public sealed record ViewDuplicateReportDto(
    [property: JsonPropertyName("enabled")] bool Enabled,
    [property: JsonPropertyName("scan_hour")] int ScanHour,
    [property: JsonPropertyName("last_scan_at")] DateTimeOffset? LastScanAt,
    [property: JsonPropertyName("groups")] IReadOnlyList<ViewDuplicateGroupDto> Groups);

/// <summary>Keep one copy of a group; the rest go to Trash.</summary>
public sealed record ViewDuplicateKeepRequest(
    [property: JsonPropertyName("keep_item_id")] Guid KeepItemId);

/// <summary>How many copies one keep-one moved to Trash.</summary>
public sealed record ViewDuplicateKeepResultDto(
    [property: JsonPropertyName("moved_to_trash")] int MovedToTrash);

/// <summary>What Tidy this folder would do to one Managed folder, or is doing, or last did (Photo and video filing spec D17).</summary>
public sealed record ViewTidyStatusDto(
    [property: JsonPropertyName("source_id")] Guid SourceId,
    [property: JsonPropertyName("source_name")] string SourceName,
    [property: JsonPropertyName("will_move")] int WillMove,
    [property: JsonPropertyName("example_from")] string? ExampleFrom,
    [property: JsonPropertyName("example_to")] string? ExampleTo,
    [property: JsonPropertyName("running")] bool Running,
    [property: JsonPropertyName("scheduled")] bool Scheduled,
    [property: JsonPropertyName("schedule_hour")] int ScheduleHour,
    [property: JsonPropertyName("total")] int Total,
    [property: JsonPropertyName("moved")] int Moved,
    [property: JsonPropertyName("failed")] int Failed,
    [property: JsonPropertyName("message")] string? Message);

/// <summary>Turns the nightly Tidy of one folder on or off.</summary>
public sealed record ViewTidyScheduleRequest(
    [property: JsonPropertyName("scheduled")] bool Scheduled);
