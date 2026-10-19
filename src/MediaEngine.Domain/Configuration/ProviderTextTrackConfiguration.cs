using System.Text.Json;
using System.Text.Json.Serialization;

namespace MediaEngine.Domain.Configuration;

/// <summary>
/// How a subtitle or lyrics provider is searched and downloaded. Declared in <c>text_track</c> of
/// a provider file whose <c>adapter_type</c> is <c>text_track</c>; one built-in adapter runs it.
/// </summary>
public sealed class TextTrackConfiguration
{
    /// <summary><c>Subtitles</c> (lists of releases, downloaded as files) or <c>Lyrics</c> (one document per track).</summary>
    [JsonPropertyName("kind")]
    public string Kind { get; set; } = TextTrackKinds.Subtitles;

    /// <summary>What the provider supplies, used inside messages ("subtitles", "lyrics").</summary>
    [JsonPropertyName("noun")]
    public string Noun { get; set; } = "subtitles";

    [JsonPropertyName("api")]
    public TextTrackApiConfiguration Api { get; set; } = new();

    /// <summary>How the shared HTTP client for the provider is built.</summary>
    [JsonPropertyName("http")]
    public TextTrackHttpConfiguration Http { get; set; } = new();

    [JsonPropertyName("language")]
    public TextTrackLanguageConfiguration Language { get; set; } = new();

    [JsonPropertyName("search")]
    public TextTrackSearchConfiguration Search { get; set; } = new();

    [JsonPropertyName("download")]
    public TextTrackDownloadConfiguration Download { get; set; } = new();

    [JsonPropertyName("errors")]
    public TextTrackErrorConfiguration Errors { get; set; } = new();

    /// <summary>Message templates by name; <c>{display_name}</c> and <c>{name}</c> are filled in.</summary>
    [JsonPropertyName("messages")]
    public Dictionary<string, string> Messages { get; set; } = new(StringComparer.Ordinal);

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? UnmappedProperties { get; set; }
}

public static class TextTrackKinds
{
    public const string Subtitles = "Subtitles";
    public const string Lyrics = "Lyrics";
}

/// <summary>Names of the entries of <see cref="TextTrackConfiguration.Messages"/> the adapter reads.</summary>
public static class TextTrackMessages
{
    public const string SearchIncomplete = "search_incomplete";
    public const string SearchInvalid = "search_invalid";
    public const string IdentityMismatch = "identity_mismatch";
    public const string Ambiguous = "ambiguous";
    public const string Unreachable = "unreachable";
    public const string DownloadFailed = "download_failed";
    public const string ProviderDown = "provider_down";
    public const string AuthenticationRequired = "AuthenticationRequired";
    public const string QuotaExceeded = "QuotaExceeded";
    public const string RateLimited = "RateLimited";
    public const string ProviderUnavailable = "ProviderUnavailable";
    public const string ProviderError = "ProviderError";

    public static readonly IReadOnlyList<string> All =
    [
        SearchIncomplete, SearchInvalid, IdentityMismatch, Ambiguous, Unreachable, DownloadFailed, ProviderDown,
        AuthenticationRequired, QuotaExceeded, RateLimited, ProviderUnavailable, ProviderError,
    ];
}

public sealed class TextTrackApiConfiguration
{
    /// <summary>
    /// Hosts the API may be reached on (HTTPS, default port, no credentials in the address, no
    /// path). Requests are only sent to them and a redirect elsewhere is refused. Empty = unchecked.
    /// </summary>
    [JsonPropertyName("allowed_hosts")]
    public List<string> AllowedHosts { get; set; } = [];

    /// <summary>Value of the <c>Accept</c> header, when the provider wants one.</summary>
    [JsonPropertyName("accept")]
    public string? Accept { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? UnmappedProperties { get; set; }
}

public sealed class TextTrackHttpConfiguration
{
    /// <summary>False when the HTTP client must never follow redirects (so credentials cannot leak).</summary>
    [JsonPropertyName("allow_auto_redirect")]
    public bool AllowAutoRedirect { get; set; } = true;

    /// <summary>Largest response the HTTP client buffers; 0 keeps the platform default.</summary>
    [JsonPropertyName("max_response_buffer_bytes")]
    public long MaxResponseBufferBytes { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? UnmappedProperties { get; set; }
}

public sealed class TextTrackLanguageConfiguration
{
    /// <summary>Language names the provider spells out, mapped to two-letter codes (lower-case keys).</summary>
    [JsonPropertyName("aliases")]
    public Dictionary<string, string> Aliases { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Code used when a language is missing or not two or three letters.</summary>
    [JsonPropertyName("unknown")]
    public string Unknown { get; set; } = "und";

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? UnmappedProperties { get; set; }
}

public sealed class TextTrackSearchConfiguration
{
    /// <summary>Lookup values that must exist before a search is made (for example <c>title</c>).</summary>
    [JsonPropertyName("needs")]
    public List<string> Needs { get; set; } = [];

    /// <summary>Candidate requests; the first whose media type and needs fit is used.</summary>
    [JsonPropertyName("requests")]
    public List<TextTrackRequestConfiguration> Requests { get; set; } = [];

    /// <summary>For Subtitles: minutes an empty answer is remembered so the same search is not repeated.</summary>
    [JsonPropertyName("empty_search_minutes")]
    public int EmptySearchMinutes { get; set; }

    /// <summary>Remember answers in the shared response cache for the provider's <c>cache_ttl_hours</c>.</summary>
    [JsonPropertyName("response_cache")]
    public bool ResponseCache { get; set; }

    // -- Subtitles: a list of releases ------------------------------------------------------

    /// <summary>Path of the success flag; a value of <c>false</c> marks the answer as failed.</summary>
    [JsonPropertyName("success_path")]
    public string? SuccessPath { get; set; }

    /// <summary>Path of the list of titles the answer is about.</summary>
    [JsonPropertyName("titles_path")]
    public string? TitlesPath { get; set; }

    /// <summary>Path of the list of releases.</summary>
    [JsonPropertyName("items_path")]
    public string? ItemsPath { get; set; }

    [JsonPropertyName("max_items")]
    public int MaxItems { get; set; }

    [JsonPropertyName("item")]
    public TextTrackItemConfiguration Item { get; set; } = new();

    [JsonPropertyName("score")]
    public TextTrackScoreConfiguration Score { get; set; } = new();

    /// <summary>Best candidates closer than this are ambiguous and no choice is made.</summary>
    [JsonPropertyName("ambiguity_margin")]
    public double AmbiguityMargin { get; set; }

    [JsonPropertyName("max_candidates")]
    public int MaxCandidates { get; set; }

    /// <summary>Public page of a release, with <c>{id}</c> (escaped).</summary>
    [JsonPropertyName("source_url")]
    public string? SourceUrl { get; set; }

    // -- Lyrics: one document -----------------------------------------------------------------

    [JsonPropertyName("lyrics")]
    public TextTrackLyricsConfiguration Lyrics { get; set; } = new();

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? UnmappedProperties { get; set; }
}

public sealed class TextTrackRequestConfiguration
{
    /// <summary>Media type the request is for; empty = any.</summary>
    [JsonPropertyName("when_media_type")]
    public string? WhenMediaType { get; set; }

    /// <summary>Lookup values that must be present for this request.</summary>
    [JsonPropertyName("needs")]
    public List<string> Needs { get; set; } = [];

    [JsonPropertyName("path")]
    public string Path { get; set; } = string.Empty;

    /// <summary>
    /// Query pairs <c>[name, value]</c> in order; values are templates over the lookup values
    /// (<c>{title}</c>, <c>{tmdb_id}</c> …) and are URI-escaped. A pair whose template names a blank
    /// value is left out.
    /// </summary>
    [JsonPropertyName("query")]
    public List<List<string>> Query { get; set; } = [];

    /// <summary>Check that the answer is about the requested title.</summary>
    [JsonPropertyName("title_check")]
    public TextTrackTitleCheckConfiguration? TitleCheck { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? UnmappedProperties { get; set; }
}

public sealed class TextTrackTitleCheckConfiguration
{
    /// <summary>Value of the title's <c>type</c> field the answer must contain.</summary>
    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;

    /// <summary>Field of a title holding the identifier.</summary>
    [JsonPropertyName("id_path")]
    public string IdPath { get; set; } = string.Empty;

    /// <summary>Template for the identifier that must be found.</summary>
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("ignore_case")]
    public bool IgnoreCase { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? UnmappedProperties { get; set; }
}

public sealed class TextTrackItemConfiguration
{
    [JsonPropertyName("id_paths")]
    public List<string> IdPaths { get; set; } = [];

    [JsonPropertyName("release_paths")]
    public List<string> ReleasePaths { get; set; } = [];

    [JsonPropertyName("name_path")]
    public string NamePath { get; set; } = string.Empty;

    [JsonPropertyName("language_paths")]
    public List<string> LanguagePaths { get; set; } = [];

    [JsonPropertyName("hearing_impaired_paths")]
    public List<string> HearingImpairedPaths { get; set; } = [];

    /// <summary>When the lookup names a season and episode, items must match them.</summary>
    [JsonPropertyName("episode_match")]
    public TextTrackEpisodeMatchConfiguration? EpisodeMatch { get; set; }

    /// <summary>Optional files inside a release (a pack), each a candidate of its own.</summary>
    [JsonPropertyName("files")]
    public TextTrackFilesConfiguration? Files { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? UnmappedProperties { get; set; }
}

public sealed class TextTrackEpisodeMatchConfiguration
{
    [JsonPropertyName("season_path")]
    public string SeasonPath { get; set; } = string.Empty;

    [JsonPropertyName("episode_path")]
    public string EpisodePath { get; set; } = string.Empty;

    /// <summary>Paths that must not be <c>true</c> (a whole-season pack, for example).</summary>
    [JsonPropertyName("reject_true_paths")]
    public List<string> RejectTruePaths { get; set; } = [];

    /// <summary>Paths that, when present, must equal the episode (range start and end).</summary>
    [JsonPropertyName("range_paths")]
    public List<string> RangePaths { get; set; } = [];

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? UnmappedProperties { get; set; }
}

public sealed class TextTrackFilesConfiguration
{
    [JsonPropertyName("path")]
    public string Path { get; set; } = string.Empty;

    [JsonPropertyName("max")]
    public int Max { get; set; }

    [JsonPropertyName("id_paths")]
    public List<string> IdPaths { get; set; } = [];

    [JsonPropertyName("name_path")]
    public string NamePath { get; set; } = string.Empty;

    [JsonPropertyName("url_path")]
    public string UrlPath { get; set; } = string.Empty;

    /// <summary>Paths of the file's own language; the release's language paths are the fallback.</summary>
    [JsonPropertyName("language_paths")]
    public List<string> LanguagePaths { get; set; } = [];

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? UnmappedProperties { get; set; }
}

public sealed class TextTrackScoreConfiguration
{
    [JsonPropertyName("base")]
    public double Base { get; set; }

    /// <summary>Weight of the word overlap between the local file name and the release name (0-1).</summary>
    [JsonPropertyName("release_overlap_weight")]
    public double ReleaseOverlapWeight { get; set; }

    /// <summary>Extra score by file extension.</summary>
    [JsonPropertyName("format_bonus")]
    public Dictionary<string, double> FormatBonus { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    [JsonPropertyName("hearing_impaired")]
    public double HearingImpaired { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? UnmappedProperties { get; set; }
}

public sealed class TextTrackLyricsConfiguration
{
    /// <summary>Sources of the text, best first; the first non-blank one is used.</summary>
    [JsonPropertyName("sources")]
    public List<TextTrackLyricsSourceConfiguration> Sources { get; set; } = [];

    /// <summary>Format of the text when no source has any.</summary>
    [JsonPropertyName("fallback_format")]
    public string FallbackFormat { get; set; } = string.Empty;

    [JsonPropertyName("instrumental_path")]
    public string? InstrumentalPath { get; set; }

    [JsonPropertyName("id_path")]
    public string? IdPath { get; set; }

    [JsonPropertyName("duration")]
    public TextTrackDurationConfiguration Duration { get; set; } = new();

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? UnmappedProperties { get; set; }
}

public sealed class TextTrackLyricsSourceConfiguration
{
    [JsonPropertyName("path")]
    public string Path { get; set; } = string.Empty;

    /// <summary>Format of the text from this source (for example <c>lrc</c> for timed lines).</summary>
    [JsonPropertyName("format")]
    public string Format { get; set; } = string.Empty;

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? UnmappedProperties { get; set; }
}

public sealed class TextTrackDurationConfiguration
{
    /// <summary>Path of the track length (seconds) in the answer.</summary>
    [JsonPropertyName("path")]
    public string Path { get; set; } = string.Empty;

    /// <summary>A difference up to this many seconds is a perfect match.</summary>
    [JsonPropertyName("full_within_seconds")]
    public double FullWithinSeconds { get; set; }

    /// <summary>A difference beyond this many seconds is a mismatch (score -1).</summary>
    [JsonPropertyName("reject_beyond_seconds")]
    public double RejectBeyondSeconds { get; set; }

    /// <summary>Format whose mismatching answers are dropped (timed lyrics); other formats are kept.</summary>
    [JsonPropertyName("strict_format")]
    public string StrictFormat { get; set; } = string.Empty;

    /// <summary>Lowest confidence of a kept answer in a format other than <c>strict_format</c>.</summary>
    [JsonPropertyName("lenient_floor")]
    public double LenientFloor { get; set; }

    /// <summary>Confidence when the duration is unknown on either side.</summary>
    [JsonPropertyName("default_confidence")]
    public double DefaultConfidence { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? UnmappedProperties { get; set; }
}

public sealed class TextTrackDownloadConfiguration
{
    /// <summary><c>payload</c>: the text came with the search; <c>fetch</c>: it is downloaded from a link.</summary>
    [JsonPropertyName("mode")]
    public string Mode { get; set; } = TextTrackDownloadModes.Payload;

    /// <summary>Asks the API for the file link when the search did not give one.</summary>
    [JsonPropertyName("resolve")]
    public TextTrackResolveConfiguration? Resolve { get; set; }

    [JsonPropertyName("allowed_hosts")]
    public List<string> AllowedHosts { get; set; } = [];

    /// <summary>Scheme and host put in front of links that start with <c>path_prefix</c>.</summary>
    [JsonPropertyName("relative_base")]
    public string? RelativeBase { get; set; }

    /// <summary>Links must start with this path; they may not carry a query, a fragment or <c>..</c>.</summary>
    [JsonPropertyName("path_prefix")]
    public string? PathPrefix { get; set; }

    [JsonPropertyName("max_bytes")]
    public long MaxBytes { get; set; }

    /// <summary>Accepted file extensions (without a dot).</summary>
    [JsonPropertyName("formats")]
    public List<string> Formats { get; set; } = [];

    [JsonPropertyName("archive")]
    public TextTrackArchiveConfiguration? Archive { get; set; }

    /// <summary>The output format; the text must contain cues (<c> --&gt; </c>) after conversion.</summary>
    [JsonPropertyName("output_format")]
    public string? OutputFormat { get; set; }

    /// <summary>A download that yields nothing marks the provider's files as incompatible with playback.</summary>
    [JsonPropertyName("null_means_incompatible")]
    public bool NullMeansIncompatible { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? UnmappedProperties { get; set; }
}

public static class TextTrackDownloadModes
{
    public const string Payload = "payload";
    public const string Fetch = "fetch";
}

public sealed class TextTrackResolveConfiguration
{
    /// <summary>API path with <c>{id}</c> (escaped).</summary>
    [JsonPropertyName("path")]
    public string Path { get; set; } = string.Empty;

    [JsonPropertyName("url_paths")]
    public List<string> UrlPaths { get; set; } = [];

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? UnmappedProperties { get; set; }
}

public sealed class TextTrackArchiveConfiguration
{
    [JsonPropertyName("max_entries")]
    public int MaxEntries { get; set; }

    /// <summary>An entry may not expand to more than this many times its compressed size.</summary>
    [JsonPropertyName("max_expansion_ratio")]
    public int MaxExpansionRatio { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? UnmappedProperties { get; set; }
}

public sealed class TextTrackErrorConfiguration
{
    /// <summary>Failure statuses in order; the first entry that fits the answer decides.</summary>
    [JsonPropertyName("statuses")]
    public List<TextTrackErrorStatusConfiguration> Statuses { get; set; } = [];

    [JsonPropertyName("default_status")]
    public string DefaultStatus { get; set; } = "ProviderError";

    /// <summary>Path, in a failed answer, of the provider's own error code.</summary>
    [JsonPropertyName("error_code_path")]
    public string? ErrorCodePath { get; set; }

    /// <summary>Response header (epoch seconds) that says when a limit resets, besides Retry-After.</summary>
    [JsonPropertyName("reset_header")]
    public string? ResetHeader { get; set; }

    /// <summary>Statuses that are the caller's problem, not an outage, so provider health is not touched.</summary>
    [JsonPropertyName("not_outage_codes")]
    public List<int> NotOutageCodes { get; set; } = [];

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? UnmappedProperties { get; set; }
}

public sealed class TextTrackErrorStatusConfiguration
{
    [JsonPropertyName("codes")]
    public List<int> Codes { get; set; } = [];

    /// <summary>Matches every code from here up; alternative to <c>codes</c>.</summary>
    [JsonPropertyName("min_code")]
    public int? MinCode { get; set; }

    /// <summary>Only when the provider's own error code equals this.</summary>
    [JsonPropertyName("error_code")]
    public string? ErrorCode { get; set; }

    [JsonPropertyName("status")]
    public string Status { get; set; } = string.Empty;

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? UnmappedProperties { get; set; }
}
