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
}
