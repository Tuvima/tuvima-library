using System.Text.Json.Serialization;

namespace MediaEngine.Contracts.Backups;

/// <summary>The built-in destination ids. A destination plugin uses its own id.</summary>
public static class BackupDestinationIds
{
    public const string Local = "local";
}

public static class BackupFrequencies
{
    public const string Daily = "daily";
    public const string Weekly = "weekly";
}

public sealed class BackupScheduleDto
{
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; }

    /// <summary><c>daily</c> or <c>weekly</c>.</summary>
    [JsonPropertyName("frequency")]
    public string Frequency { get; set; } = BackupFrequencies.Daily;

    /// <summary>Local time as HH:mm.</summary>
    [JsonPropertyName("time")]
    public string Time { get; set; } = "02:00";

    /// <summary>0 (Sunday) to 6 (Saturday); used when the frequency is weekly.</summary>
    [JsonPropertyName("weekday")]
    public int Weekday { get; set; }
}

/// <summary>A destination as the Backup destinations section shows it.</summary>
public sealed class BackupDestinationDto
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    /// <summary>True when the copy leaves this computer, so it is encrypted first.</summary>
    [JsonPropertyName("leaves_machine")]
    public bool LeavesMachine { get; set; }

    [JsonPropertyName("is_configured")]
    public bool IsConfigured { get; set; }

    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; }

    /// <summary><c>ok</c>, <c>needs_attention</c> or <c>off</c>.</summary>
    [JsonPropertyName("state")]
    public string State { get; set; } = "ok";

    [JsonPropertyName("state_label")]
    public string StateLabel { get; set; } = "";

    [JsonPropertyName("last_copy_at")]
    public DateTimeOffset? LastCopyAt { get; set; }

    /// <summary>True when the newest copy was read back and matched.</summary>
    [JsonPropertyName("last_copy_verified")]
    public bool LastCopyVerified { get; set; }

    [JsonPropertyName("free_space_bytes")]
    public long? FreeSpaceBytes { get; set; }
}

public sealed class BackupSettingsResponse
{
    [JsonPropertyName("schedule")]
    public BackupScheduleDto Schedule { get; set; } = new();

    [JsonPropertyName("keep_last")]
    public int KeepLast { get; set; } = 10;

    /// <summary>True once a backup passphrase has been set. The passphrase itself is never stored.</summary>
    [JsonPropertyName("passphrase_set")]
    public bool PassphraseSet { get; set; }

    [JsonPropertyName("destinations")]
    public List<BackupDestinationDto> Destinations { get; set; } = [];

    [JsonPropertyName("next_run_at")]
    public DateTimeOffset? NextRunAt { get; set; }

    [JsonPropertyName("last_restore_check_at")]
    public DateTimeOffset? LastRestoreCheckAt { get; set; }

    /// <summary>Plain words about the last restore check ("Passed", "Failed: ..."), or null when none has run.</summary>
    [JsonPropertyName("last_restore_check_result")]
    public string? LastRestoreCheckResult { get; set; }
}

public sealed class UpdateBackupSettingsRequest
{
    [JsonPropertyName("schedule")]
    public BackupScheduleDto? Schedule { get; set; }

    [JsonPropertyName("keep_last")]
    public int? KeepLast { get; set; }

    /// <summary>The destination ids that should receive each backup. The local folder always does.</summary>
    [JsonPropertyName("enabled_destinations")]
    public List<string>? EnabledDestinations { get; set; }
}

public sealed class SetBackupPassphraseRequest
{
    [JsonPropertyName("passphrase")]
    public string Passphrase { get; set; } = "";

    /// <summary>Required when a passphrase is already set, so a signed-in session cannot swap it silently.</summary>
    [JsonPropertyName("current_passphrase")]
    public string? CurrentPassphrase { get; set; }
}

public sealed class BackupDestinationOutcomeDto
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    /// <summary><c>copied</c>, <c>skipped</c> or <c>failed</c>.</summary>
    [JsonPropertyName("outcome")]
    public string Outcome { get; set; } = "";

    [JsonPropertyName("message")]
    public string Message { get; set; } = "";
}

public sealed class BackupRunResponse
{
    [JsonPropertyName("archive_name")]
    public string ArchiveName { get; set; } = "";

    [JsonPropertyName("destinations")]
    public List<BackupDestinationOutcomeDto> Destinations { get; set; } = [];

    [JsonPropertyName("all_ok")]
    public bool AllOk { get; set; }
}

public sealed class BackupDestinationTestResponse
{
    [JsonPropertyName("success")]
    public bool Success { get; set; }

    [JsonPropertyName("message")]
    public string Message { get; set; } = "";
}

public sealed class DestinationBackupDto
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("size_bytes")]
    public long SizeBytes { get; set; }

    [JsonPropertyName("modified_at")]
    public DateTimeOffset ModifiedAt { get; set; }

    /// <summary>True when the file is encrypted and needs the passphrase to restore.</summary>
    [JsonPropertyName("encrypted")]
    public bool Encrypted { get; set; }
}

public sealed class DestinationBackupsResponse
{
    [JsonPropertyName("items")]
    public List<DestinationBackupDto> Items { get; set; } = [];
}

public sealed class RestoreFromDestinationRequest
{
    [JsonPropertyName("destination_id")]
    public string DestinationId { get; set; } = "";

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    /// <summary>Needed for an encrypted backup. Used once to open the file and never stored.</summary>
    [JsonPropertyName("passphrase")]
    public string? Passphrase { get; set; }
}
