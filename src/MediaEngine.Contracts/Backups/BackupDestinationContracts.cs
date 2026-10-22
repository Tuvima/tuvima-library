using System.Text.Json.Serialization;

namespace MediaEngine.Contracts.Backups;

/// <summary>The built-in destination ids. A destination plugin uses its own id.</summary>
public static class BackupDestinationIds
{
    public const string Local = "local";
}

/// <summary>The kinds of destination an administrator can add (Developer tools spec WP26).</summary>
public static class BackupDestinationKinds
{
    /// <summary>Another folder on this computer, such as an external drive.</summary>
    public const string Folder = "folder";

    /// <summary>A folder on another computer on the network, reached by its path.</summary>
    public const string Share = "share";

    /// <summary>S3-compatible storage: Amazon S3, MinIO, Backblaze B2, Wasabi and similar.</summary>
    public const string S3 = "s3";

    public const string WebDav = "webdav";

    public static bool IsKnown(string? kind) => kind is Folder or Share or S3 or WebDav;

    /// <summary>True for the kinds that are reached over the web and so need an address and a login.</summary>
    public static bool IsWeb(string? kind) => kind is S3 or WebDav;
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

    /// <summary>True when the copy leaves this computer (it is still the same plain backup file).</summary>
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

    /// <summary><c>local</c>, or one of <see cref="BackupDestinationKinds"/>.</summary>
    [JsonPropertyName("kind")]
    public string Kind { get; set; } = BackupDestinationIds.Local;

    /// <summary>Where it sends to, in plain words: a folder path or a web address. Never holds a login.</summary>
    [JsonPropertyName("location")]
    public string Location { get; set; } = "";

    /// <summary>True for a destination an administrator added, which they can also change or remove.</summary>
    [JsonPropertyName("removable")]
    public bool Removable { get; set; }
}

/// <summary>Adds a backup destination, or changes one when <see cref="Id"/> is set. Logins are write-only: they are never sent back.</summary>
public sealed class SaveBackupDestinationRequest
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    /// <summary>One of <see cref="BackupDestinationKinds"/> other than the local folder.</summary>
    [JsonPropertyName("kind")]
    public string Kind { get; set; } = "";

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    /// <summary>A folder path (folder, share), or the web address (S3 endpoint, WebDAV folder).</summary>
    [JsonPropertyName("location")]
    public string Location { get; set; } = "";

    [JsonPropertyName("bucket")]
    public string? Bucket { get; set; }

    [JsonPropertyName("region")]
    public string? Region { get; set; }

    /// <summary>An optional folder inside the bucket.</summary>
    [JsonPropertyName("prefix")]
    public string? Prefix { get; set; }

    /// <summary>The S3 access key id, or the WebDAV user name.</summary>
    [JsonPropertyName("username")]
    public string? Username { get; set; }

    /// <summary>The S3 secret key, or the WebDAV password. Stored encrypted on this computer; left out means keep the saved one.</summary>
    [JsonPropertyName("secret")]
    public string? Secret { get; set; }

    /// <summary>
    /// For web destinations: the host name the person was shown ("Backups will be sent to nas.local") and agreed to. It must match the
    /// address, so a destination is never added without the address having been put in front of the administrator.
    /// </summary>
    [JsonPropertyName("confirmed_host")]
    public string? ConfirmedHost { get; set; }
}

public sealed class BackupSettingsResponse
{
    [JsonPropertyName("schedule")]
    public BackupScheduleDto Schedule { get; set; } = new();

    [JsonPropertyName("keep_last")]
    public int KeepLast { get; set; } = 10;

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
}
