using System.Text.Json.Serialization;

namespace MediaEngine.Domain.Configuration;

/// <summary>
/// How photos and personal videos that arrive in Library's Import area are filed, loaded from <c>config/photo_filing.json</c>
/// (Photo and video filing spec D2, D6, D19). Everything here has a safe default, so a missing file means the values below.
/// </summary>
public sealed class PhotoFilingSettings
{
    /// <summary>The default layout: year, then a date folder named like <c>2026-10-28</c>.</summary>
    public const string DefaultLayoutPattern = "{year}/{year}-{month}-{day}";

    /// <summary>
    /// The folders a filed photo lands in, inside the destination folder. The tokens are <c>{year}</c>, <c>{month}</c> and <c>{day}</c>
    /// (the date the photo was taken). The file keeps its own name. An unusable pattern falls back to <see cref="DefaultLayoutPattern"/>.
    /// </summary>
    [JsonPropertyName("layout_pattern")]
    public string LayoutPattern { get; set; } = DefaultLayoutPattern;

    /// <summary>How many days a refused file stays in the Rejected area before it is removed automatically.</summary>
    [JsonPropertyName("rejected_retention_days")]
    public int RejectedRetentionDays { get; set; } = 30;

    /// <summary>
    /// How long a file in the Import area must stay unchanged before Library files it, so a copy that is still being written
    /// (a card reader, a phone sync) is never filed half done.
    /// </summary>
    [JsonPropertyName("settle_seconds")]
    public int SettleSeconds { get; set; } = 30;

    /// <summary>How often the Import and Rejected areas are looked at for files copied in by hand.</summary>
    [JsonPropertyName("scan_interval_seconds")]
    public int ScanIntervalSeconds { get; set; } = 60;

    /// <summary>The most files taken in one pass, so a very large card never holds up the rest of the Engine.</summary>
    [JsonPropertyName("max_files_per_pass")]
    public int MaxFilesPerPass { get; set; } = 500;

    /// <summary>Whether the duplicates cleanup job runs on its own every night. An administrator can always run it now.</summary>
    [JsonPropertyName("duplicate_scan_enabled")]
    public bool DuplicateScanEnabled { get; set; } = true;

    /// <summary>The hour of the day (0-23, server time) the nightly duplicates job runs.</summary>
    [JsonPropertyName("duplicate_scan_hour")]
    public int DuplicateScanHour { get; set; } = 3;

    /// <summary>The hour of the day (0-23, server time) a scheduled Tidy this folder run starts. A run is scheduled per folder.</summary>
    [JsonPropertyName("tidy_hour")]
    public int TidyHour { get; set; } = 2;

    /// <summary>The default for <see cref="MissingFileRetentionDays"/>.</summary>
    public const int DefaultMissingFileRetentionDays = 90;

    /// <summary>
    /// How many days Library keeps what it knows about a photo, video or document whose file was deleted outside Library (notes, tags,
    /// Gallery spots) before removing it. 0 means never remove. Nothing here ever deletes a file.
    /// </summary>
    [JsonPropertyName("missing_file_retention_days")]
    public int MissingFileRetentionDays { get; set; } = DefaultMissingFileRetentionDays;
}
