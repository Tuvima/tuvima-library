using System.Text.Json.Serialization;

namespace MediaEngine.Domain.Models;

/// <summary>
/// Centralised colour palette loaded from <c>config/ui/palette.json</c>.
/// All UI colour constants read from this configuration instead of
/// hardcoding hex values. Supports seasonal themes by swapping the file.
/// </summary>
public sealed class PaletteConfiguration
{
    [JsonPropertyName("theme")]
    public ThemePalette Theme { get; set; } = new();

    [JsonPropertyName("status")]
    public StatusPalette Status { get; set; } = new();

    [JsonPropertyName("pipeline")]
    public PipelinePalette Pipeline { get; set; } = new();

    [JsonPropertyName("media_type")]
    public MediaTypePalette MediaType { get; set; } = new();

    [JsonPropertyName("confidence")]
    public ConfidencePalette Confidence { get; set; } = new();

    [JsonPropertyName("review_trigger")]
    public ReviewTriggerPalette ReviewTrigger { get; set; } = new();
}

public sealed class ThemePalette
{
    [JsonPropertyName("primary")] public string Primary { get; set; } = "#8852FC";
    [JsonPropertyName("secondary")] public string Secondary { get; set; } = "#9CA3AF";
    [JsonPropertyName("background")] public string Background { get; set; } = "#080B14";
    [JsonPropertyName("surface")] public string Surface { get; set; } = "#0C1020";
    [JsonPropertyName("text_primary")] public string TextPrimary { get; set; } = "#F3F4F6";
    [JsonPropertyName("text_secondary")] public string TextSecondary { get; set; } = "#9CA3AF";
    [JsonPropertyName("text_disabled")] public string TextDisabled { get; set; } = "#4B5563";
    [JsonPropertyName("error")] public string Error { get; set; } = "#CF6679";
    [JsonPropertyName("warning")] public string Warning { get; set; } = "#FFB74D";
    [JsonPropertyName("info")] public string Info { get; set; } = "#4FC3F7";
    [JsonPropertyName("success")] public string Success { get; set; } = "#81C784";
}

public sealed class StatusPalette
{
    [JsonPropertyName("verified")] public string Verified { get; set; } = "#5DCAA5";
    [JsonPropertyName("provisional")] public string Provisional { get; set; } = "#3B82F6";
    [JsonPropertyName("needs_review")] public string NeedsReview { get; set; } = "#EF9F27";
    [JsonPropertyName("quarantined")] public string Quarantined { get; set; } = "#E24B4A";
    [JsonPropertyName("identified")] public string Identified { get; set; } = "#5DCAA5";
    [JsonPropertyName("confirmed")] public string Confirmed { get; set; } = "#5C9FCA";
    [JsonPropertyName("in_review")] public string InReview { get; set; } = "#EF9F27";
    [JsonPropertyName("known")] public string Known { get; set; } = "#B39DDB";
    [JsonPropertyName("awaiting_stage2")] public string AwaitingStage2 { get; set; } = "#7F77DD";
    [JsonPropertyName("rejected")] public string Rejected { get; set; } = "#E24B4A";
    [JsonPropertyName("default")] public string Default { get; set; } = "rgba(255,255,255,0.3)";
}

public sealed class PipelinePalette
{
    [JsonPropertyName("completed")] public string Completed { get; set; } = "#5DCAA5";
    [JsonPropertyName("warning")] public string Warning { get; set; } = "#EF9F27";
    [JsonPropertyName("failed")] public string Failed { get; set; } = "#A05050";
    [JsonPropertyName("pending")] public string Pending { get; set; } = "#3B3B3B";
}

public sealed class MediaTypePalette
{
    [JsonPropertyName("movie")] public string Movie { get; set; } = "#60A5FA";
    [JsonPropertyName("book")] public string Book { get; set; } = "#5DCAA5";
    [JsonPropertyName("audiobook")] public string Audiobook { get; set; } = "#A78BFA";
    [JsonPropertyName("tv")] public string TV { get; set; } = "#FBBF24";
    [JsonPropertyName("music")] public string Music { get; set; } = "#22D3EE";
    [JsonPropertyName("comic")] public string Comic { get; set; } = "#7C4DFF";
    [JsonPropertyName("unknown")] public string Unknown { get; set; } = "rgba(255,255,255,0.4)";
}

public sealed class ConfidencePalette
{
    [JsonPropertyName("high")] public string High { get; set; } = "#5DCAA5";
    [JsonPropertyName("medium")] public string Medium { get; set; } = "#EF9F27";
    [JsonPropertyName("low")] public string Low { get; set; } = "#A05050";
}

public sealed class ReviewTriggerPalette
{
    [JsonPropertyName("low_confidence")] public string LowConfidence { get; set; } = "#B08940";
    [JsonPropertyName("multiple_matches")] public string MultipleMatches { get; set; } = "#5C7A99";
    [JsonPropertyName("match_failed")] public string MatchFailed { get; set; } = "#A05050";
    [JsonPropertyName("ambiguous")] public string Ambiguous { get; set; } = "#B08940";
    [JsonPropertyName("movie_matched_as_tv")] public string MovieMatchedAsTv { get; set; } = "#5C7A99";
    [JsonPropertyName("user_report")] public string UserReport { get; set; } = "#C9922E";
    [JsonPropertyName("default")] public string Default { get; set; } = "#6B6B6B";
}
