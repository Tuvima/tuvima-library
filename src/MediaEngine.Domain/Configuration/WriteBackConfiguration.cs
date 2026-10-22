using System.Text.Json.Serialization;

namespace MediaEngine.Domain.Configuration;

/// <summary>
/// Configuration for file write-back behaviour.
/// Loaded from <c>config/writeback.json</c>.
/// </summary>
public sealed class WriteBackConfiguration
{
    /// <summary>Master toggle for write-back. When false, no file modifications occur.</summary>
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Explicit opt-in for embedding preferred artwork into eligible media files.
    /// Existing metadata write-back settings must not silently enable image writes.
    /// </summary>
    [JsonPropertyName("artwork_enabled")]
    public bool ArtworkEnabled { get; set; }

    /// <summary>Write metadata into files after automatic provider matching (Stage 1).</summary>
    [JsonPropertyName("write_on_auto_match")]
    public bool WriteOnAutoMatch { get; set; } = true;

    /// <summary>Write metadata into files after manual user overrides.</summary>
    [JsonPropertyName("write_on_manual_override")]
    public bool WriteOnManualOverride { get; set; } = true;

    /// <summary>Write metadata into files after Universe (Wikidata) enrichment (Stage 2).</summary>
    [JsonPropertyName("write_on_universe_enrichment")]
    public bool WriteOnUniverseEnrichment { get; set; } = true;

    /// <summary>
    /// Create a .bak backup of the file before modifying it. Disabled by
    /// default during the pre-beta destructive-cutover phase.
    /// </summary>
    [JsonPropertyName("backup_before_write")]
    public bool BackupBeforeWrite { get; set; }

    /// <summary>
    /// Which fields to write: <c>"all"</c> for all canonical values,
    /// or a comma-separated list of claim keys.
    /// </summary>
    [JsonPropertyName("fields_to_write")]
    public string FieldsToWrite { get; set; } = "all";

    /// <summary>Claim keys to exclude from write-back even when <see cref="FieldsToWrite"/> is "all".</summary>
    [JsonPropertyName("exclude_fields")]
    public List<string> ExcludeFields { get; set; } = [];

    /// <summary>Smallest and largest number of minutes between scheduled file-write runs.</summary>
    public const int MinRunIntervalMinutes = 1;

    /// <summary>The longest wait between runs: one day.</summary>
    public const int MaxRunIntervalMinutes = 1440;

    /// <summary>
    /// How often the waiting file writes are worked through, in minutes (batched write-back D2). Every change is saved in Library at
    /// once and its file catches up on this schedule. Read through <see cref="RunInterval"/>, which keeps it in range.
    /// </summary>
    [JsonPropertyName("run_interval_minutes")]
    public int RunIntervalMinutes { get; set; } = 60;

    /// <summary>True when <paramref name="minutes"/> is an interval a person may save.</summary>
    public static bool IsValidRunInterval(int minutes) =>
        minutes is >= MinRunIntervalMinutes and <= MaxRunIntervalMinutes;

    /// <summary>The interval between runs, kept between <see cref="MinRunIntervalMinutes"/> and <see cref="MaxRunIntervalMinutes"/>.</summary>
    [JsonIgnore]
    public TimeSpan RunInterval =>
        TimeSpan.FromMinutes(Math.Clamp(RunIntervalMinutes, MinRunIntervalMinutes, MaxRunIntervalMinutes));

    /// <summary>
    /// Whether a change made for <paramref name="trigger"/> may be written into a file. The one place that maps a trigger to its
    /// switch: queueing a write and writing it both ask here. The <c>config_change</c> trigger is the automatic re-tag sweep and
    /// follows <see cref="WriteOnAutoMatch"/> because the person has just applied new field settings.
    /// </summary>
    public bool AllowsTrigger(string trigger) => Enabled && trigger switch
    {
        "auto_match" => WriteOnAutoMatch,
        "manual_override" => WriteOnManualOverride,
        "universe_enrichment" => WriteOnUniverseEnrichment,
        "config_change" => WriteOnAutoMatch,
        _ => true,
    };
}
