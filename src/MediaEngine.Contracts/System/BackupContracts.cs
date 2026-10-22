using System.Text.Json.Serialization;

namespace MediaEngine.Contracts.System;

public sealed record BackupArchiveDto(
    [property: JsonPropertyName("file_name")] string FileName,
    [property: JsonPropertyName("created_at")] DateTimeOffset CreatedAt,
    [property: JsonPropertyName("size_bytes")] long SizeBytes,
    [property: JsonPropertyName("includes_secrets")] bool IncludesSecrets = false);

public sealed record CreateBackupRequest;

public sealed record ScheduleRestoreRequest(
    [property: JsonPropertyName("file_name")] string FileName);

public sealed record RestoreValidationResultDto(
    [property: JsonPropertyName("valid")] bool Valid,
    [property: JsonPropertyName("file_name")] string FileName,
    [property: JsonPropertyName("validated_at")] DateTimeOffset ValidatedAt,
    [property: JsonPropertyName("message")] string Message,
    [property: JsonPropertyName("contents")] BackupContentsDto? Contents = null);

public sealed record ScheduleRestoreResultDto(
    [property: JsonPropertyName("scheduled")] bool Scheduled,
    [property: JsonPropertyName("restart_required")] bool RestartRequired,
    [property: JsonPropertyName("message")] string Message);

/// <summary>What a backup file holds, in counts read from its manifest. It names no title and no profile.</summary>
/// <param name="Profiles">Profiles in the backup.</param>
/// <param name="ConfigurationRows">Settings rows in the backup.</param>
/// <param name="PersonalItems">Rows of progress, bookmarks, favourites, choices and corrections.</param>
/// <param name="Titles">Titles the personal state is about.</param>
/// <param name="FileOnlyTitles">Titles that a restore can match by exact file only.</param>
public sealed record BackupContentsDto(
    [property: JsonPropertyName("profiles")] int Profiles,
    [property: JsonPropertyName("configuration_rows")] int ConfigurationRows,
    [property: JsonPropertyName("personal_items")] int PersonalItems,
    [property: JsonPropertyName("titles")] int Titles,
    [property: JsonPropertyName("file_only_titles")] int FileOnlyTitles);

/// <summary>The latest restore, in counts. Never names a title.</summary>
public sealed record LatestRestoreDto(
    [property: JsonPropertyName("restored_at")] DateTimeOffset RestoredAt,
    [property: JsonPropertyName("backup_file_name")] string BackupFileName,
    [property: JsonPropertyName("applied")] int Applied,
    [property: JsonPropertyName("attached")] int Attached,
    [property: JsonPropertyName("yielded")] int Yielded,
    [property: JsonPropertyName("purged")] int Purged);

/// <summary>What the Backup screen needs to know beyond the list of backups: counts only, never a title.</summary>
public sealed record BackupStateDto(
    [property: JsonPropertyName("file_only_titles")] int FileOnlyTitles,
    [property: JsonPropertyName("waiting_period_days")] int WaitingPeriodDays,
    [property: JsonPropertyName("still_waiting")] int StillWaiting,
    [property: JsonPropertyName("latest_restore")] LatestRestoreDto? LatestRestore);

public sealed record UpdateWaitingPeriodRequest(
    [property: JsonPropertyName("days")] int Days);
