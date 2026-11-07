using System.Text.Json.Serialization;

namespace MediaEngine.Domain.Configuration;

/// <summary>
/// Threshold and decay parameters for metadata scoring.
/// </summary>
public sealed class ScoringSettings
{
    /// <summary>Minimum confidence score required for automatic collection linking.</summary>
    [JsonPropertyName("auto_link_threshold")]
    public double AutoLinkThreshold { get; set; } = 0.85;

    /// <summary>Scores at or above this value but below auto-link require review.</summary>
    [JsonPropertyName("conflict_threshold")]
    public double ConflictThreshold { get; set; } = 0.60;

    /// <summary>Margin used to decide whether competing claim values are conflicted.</summary>
    [JsonPropertyName("conflict_epsilon")]
    public double ConflictEpsilon { get; set; } = 0.05;

    /// <summary>Claims older than this many days receive a time-decay multiplier.</summary>
    [JsonPropertyName("stale_claim_decay_days")]
    public int StaleClaimDecayDays { get; set; } = 90;

    /// <summary>Weight multiplier applied to stale claims.</summary>
    [JsonPropertyName("stale_claim_decay_factor")]
    public double StaleClaimDecayFactor { get; set; } = 0.8;

    /// <summary>The Match tab's verdict bands, length tolerances and compared facts (universal editor v5).</summary>
    [JsonPropertyName("match_verdict")]
    public MatchVerdictSettings MatchVerdict { get; set; } = new();

    /// <summary>The Match tab's music flow: how many releases are listed and how the release and track to suggest are chosen (universal editor v5).</summary>
    [JsonPropertyName("music_match")]
    public MusicMatchSettings MusicMatch { get; set; } = new();
}
