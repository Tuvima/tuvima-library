using System.Text.Json;
using System.Text.Json.Serialization;

namespace MediaEngine.Domain.Configuration;

/// <summary>
/// Per-provider configuration loaded from <c>config/providers/{name}.json</c>.
///
/// Each metadata provider carries its own self-contained configuration including
/// scoring weight, endpoints, field weights, rate limits, and capability tags.
/// The provider configuration controls how the scoring engine and harvesting
/// pipeline treat this provider.
/// </summary>
public sealed class ProviderConfiguration
{
    /// <summary>Must match the provider's registered name in the provider libraryItem.</summary>
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    /// <summary>Semantic version of the provider adapter.</summary>
    [JsonPropertyName("version")]
    public string Version { get; set; } = "1.0.0";

    /// <summary>When <c>false</c>, the provider is skipped during harvesting.</summary>
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Default scoring weight for this provider across all metadata fields.
    /// Individual fields can be overridden via <see cref="FieldWeights"/>.
    /// </summary>
    [JsonPropertyName("weight")]
    public double Weight { get; set; } = 1.0;

    /// <summary>
    /// The media domain this provider specialises in.
    /// Used for Dashboard UI grouping and future domain-filtered scoring.
    /// </summary>
    [JsonPropertyName("domain")]
    public ProviderDomain Domain { get; set; } = ProviderDomain.Universal;

    /// <summary>
    /// Relative path to the provider's SVG icon (e.g. <c>"images/providers/apple_books.svg"</c>).
    /// Used by the Dashboard to display a visual identifier for each provider.
    /// The path is relative to <c>wwwroot/</c>.
    /// </summary>
    [JsonPropertyName("icon")]
    public string? Icon { get; set; }

    /// <summary>
    /// Declarative list of metadata fields this provider is considered an expert in
    /// (e.g. <c>["cover", "narrator", "series"]</c>). Shown in the Dashboard.
    /// </summary>
    [JsonPropertyName("capability_tags")]
    public List<string> CapabilityTags { get; set; } = [];

    /// <summary>
    /// Stable, provider-level contribution capabilities such as identity, metadata,
    /// artwork, lyrics, or relationships. Unlike <see cref="CapabilityTags"/>, these
    /// are coarse-grained provider behaviors rather than claim-field labels.
    /// </summary>
    [JsonPropertyName("provider_capabilities")]
    public List<string> ProviderCapabilities { get; set; } = [];

    /// <summary>
    /// Complete list of canonical claim keys this provider can supply.
    /// Used by the Dashboard to show all fields available from each provider
    /// and allow users to toggle which fields are active.
    /// Keys reference the canonical claim key constants in <c>MetadataFieldConstants</c>.
    /// </summary>
    [JsonPropertyName("available_fields")]
    public List<string> AvailableFields { get; set; } = [];

    /// <summary>
    /// Per-field weight overrides. Key = claim key (e.g. <c>"cover"</c>),
    /// value = weight in [0.0, 1.0]. Falls back to <see cref="Weight"/> if absent.
    /// </summary>
    [JsonPropertyName("field_weights")]
    public Dictionary<string, double> FieldWeights { get; set; } = [];

    /// <summary>
    /// Named endpoint URLs for this provider. Convention:
    /// <c>"api"</c> for the primary endpoint; additional keys for secondary
    /// endpoints (e.g. <c>"sparql"</c> for Wikidata).
    /// </summary>
    [JsonPropertyName("endpoints")]
    public Dictionary<string, string> Endpoints { get; set; } = [];

    /// <summary>
    /// Minimum milliseconds between consecutive calls to this provider.
    /// <c>0</c> = no throttle. Examples: Apple Books 300, Wikidata 1100, Audnexus 0.
    /// </summary>
    [JsonPropertyName("throttle_ms")]
    public int ThrottleMs { get; set; }

    /// <summary>
    /// Maximum concurrent requests to this provider.
    /// Defaults to <c>1</c> (serial). Increase for providers that allow parallel calls.
    /// </summary>
    [JsonPropertyName("max_concurrency")]
    public int MaxConcurrency { get; set; } = 1;

    /// <summary>
    /// Structured request pacing policy for this provider. This replaces the
    /// older millisecond-gap throttle with provider-level rate and concurrency
    /// limits that can be surfaced in operations telemetry.
    /// </summary>
    [JsonPropertyName("rate_limit")]
    public ProviderRateLimitConfiguration? RateLimit { get; set; }

    /// <summary>
    /// Which stages of the three-stage hydration pipeline this provider participates in.
    /// Values: <c>1</c> (Retail Match), <c>2</c> (Universal Bridge), <c>3</c> (Human Collection).
    ///
    /// A provider can declare multiple stages (e.g. Audnexus declares <c>[1, 3]</c>
    /// for both retail matching and narrator enrichment).
    /// Defaults to <c>[1]</c> (Retail Match only).
    /// </summary>
    [JsonPropertyName("hydration_stages")]
    public List<int> HydrationStages { get; set; } = [1];

    /// <summary>
    /// Declares which bridge identifiers this provider works best with per media type.
    /// Key = media type display name (e.g. <c>"Books"</c>, <c>"Audiobooks"</c>),
    /// value = ordered list of canonical claim keys (e.g. <c>["isbn", "apple_books_id"]</c>).
    ///
    /// Used by bridge hint extraction and Stage 2 bridge ordering to determine
    /// which identifiers should be tried first for precise provider-to-Wikidata
    /// resolution.
    /// </summary>
    [JsonPropertyName("preferred_bridge_ids")]
    public Dictionary<string, List<string>>? PreferredBridgeIds { get; set; }

    /// <summary>
    /// Time-to-live in hours for cached HTTP responses from this provider.
    /// When set, the <c>ConfigDrivenAdapter</c> caches raw JSON responses and
    /// skips HTTP calls for repeated queries within the TTL window.
    /// Default: <c>168</c> (7 days). Set <c>null</c> or <c>0</c> to disable caching.
    /// Some community providers recommend a long TTL (168 hours / 7 days) to respect rate limits.
    /// </summary>
    [JsonPropertyName("cache_ttl_hours")]
    public int? CacheTtlHours { get; set; }

    // ── Config-driven adapter fields ─────────────────────────────────────────

    /// <summary>
    /// Adapter type: <c>"config_driven"</c> uses the universal adapter that reads
    /// behaviour from this config file. <c>"coded"</c> (default) uses a hard-coded
    /// adapter class (e.g. Wikidata's SPARQL adapter).
    /// </summary>
    [JsonPropertyName("adapter_type")]
    public string AdapterType { get; set; } = "coded";

    /// <summary>
    /// Stable GUID identifying this provider across all <c>metadata_claims.provider_id</c> rows.
    /// Required for <c>config_driven</c> adapters; ignored for <c>coded</c> adapters.
    /// </summary>
    [JsonPropertyName("provider_id")]
    public string? ProviderId { get; set; }

    /// <summary>Human-readable provider name for the Dashboard.</summary>
    [JsonPropertyName("display_name")]
    public string? DisplayName { get; set; }

    /// <summary>HTTP client configuration for config-driven adapters.</summary>
    [JsonPropertyName("http_client")]
    public HttpClientConfig? HttpClient { get; set; }

    /// <summary>Media type and entity type filters for config-driven adapters.</summary>
    [JsonPropertyName("can_handle")]
    public CanHandleConfig? CanHandle { get; set; }

    /// <summary>
    /// Ordered list of search strategies. Tried in <see cref="SearchStrategyConfig.Priority"/>
    /// order; first strategy that returns results wins.
    /// </summary>
    [JsonPropertyName("search_strategies")]
    public List<SearchStrategyConfig>? SearchStrategies { get; set; }

    /// <summary>Optional provider-neutral ordered member enumeration request.</summary>
    [JsonPropertyName("sequence_manifest")]
    public ProviderSequenceManifestRequestConfiguration? SequenceManifest { get; set; }

    /// <summary>
    /// Field extraction mappings. Each entry maps a JSON path in the API response
    /// to a claim key with a confidence value and optional transform.
    /// </summary>
    [JsonPropertyName("field_mappings")]
    public List<FieldMappingConfig>? FieldMappings { get; set; }

    /// <summary>
    /// When <c>true</c>, this provider requires an API key to function.
    /// The Dashboard shows this as a configuration requirement.
    /// Zero-key providers (the default) work without any authentication.
    /// </summary>
    [JsonPropertyName("requires_api_key")]
    public bool RequiresApiKey { get; set; }

    /// <summary>
    /// Optional Material icon name for the Dashboard UI (e.g. "MenuBook", "Headphones").
    /// When set, overrides the default accent icon from <c>ProviderAccentMap</c>.
    /// </summary>
    [JsonPropertyName("custom_icon_name")]
    public string? CustomIconName { get; set; }

    /// <summary>
    /// Controls how the adapter handles language when querying this provider's API.
    /// <c>"source"</c> — always query in English (default).
    /// <c>"localized"</c> — query in the user's metadata language.
    /// <c>"both"</c> — query in both languages and merge results.
    /// See <see cref="MediaEngine.Domain.Enums.LanguageStrategy"/>.
    /// </summary>
    [JsonPropertyName("language_strategy")]
    public string LanguageStrategyRaw { get; set; } = "source";

    /// <summary>Parsed <see cref="LanguageStrategy"/> from <see cref="LanguageStrategyRaw"/>.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public MediaEngine.Domain.Enums.LanguageStrategy LanguageStrategy =>
        Enum.TryParse<MediaEngine.Domain.Enums.LanguageStrategy>(LanguageStrategyRaw, ignoreCase: true, out var v)
            ? v
            : MediaEngine.Domain.Enums.LanguageStrategy.Source;

    /// <summary>
    /// Optional language-to-storefront fallbacks for providers whose catalog varies by country.
    /// The configured request country is always tried first; these markets are queried only when
    /// it returns no usable result.
    /// </summary>
    [JsonPropertyName("market_fallbacks")]
    public Dictionary<string, List<string>> MarketFallbacks { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Optional UI metadata block read from provider config JSON files.
    /// Contains accent colour, material icon, external URL template, category,
    /// auth type, and per-media-type search/ranking chips.
    /// When absent, the Dashboard falls back to defaults in <c>ProviderAccentMap</c>.
    /// </summary>
    [JsonPropertyName("ui_metadata")]
    public ProviderUiMetadata? UiMetadata { get; set; }

    /// <summary>
    /// Provider-neutral setup metadata consumed by first-run and settings UI.
    /// Provider names, signup instructions, credential fields, and skip impact
    /// belong here rather than in Dashboard components.
    /// </summary>
    [JsonPropertyName("onboarding")]
    public ProviderOnboardingConfiguration? Onboarding { get; set; }
}

/// <summary>Declarative provider setup contract.</summary>
public sealed class ProviderOnboardingConfiguration
{
    /// <summary><c>built_in</c>, <c>recommended</c>, or <c>optional</c>.</summary>
    [JsonPropertyName("classification")]
    public string Classification { get; set; } = "optional";

    [JsonPropertyName("signup_url")]
    public string? SignupUrl { get; set; }

    [JsonPropertyName("help_url")]
    public string? HelpUrl { get; set; }

    [JsonPropertyName("terms_url")]
    public string? TermsUrl { get; set; }

    [JsonPropertyName("privacy_url")]
    public string? PrivacyUrl { get; set; }

    /// <summary>Stable lane identifiers: <c>read</c>, <c>watch</c>, and <c>listen</c>.</summary>
    [JsonPropertyName("supported_lanes")]
    public List<string> SupportedLanes { get; set; } = [];

    [JsonPropertyName("required_scopes")]
    public List<string> RequiredScopes { get; set; } = [];

    [JsonPropertyName("skip_consequences")]
    public List<ProviderSkipConsequenceConfiguration> SkipConsequences { get; set; } = [];

    [JsonPropertyName("credentials")]
    public List<ProviderCredentialFieldConfiguration> Credentials { get; set; } = [];

    [JsonPropertyName("authentication_probe")]
    public ProviderAuthenticationProbeConfiguration? AuthenticationProbe { get; set; }

    [JsonPropertyName("intro")]
    public ProviderOnboardingIntroConfiguration? Intro { get; set; }

    [JsonPropertyName("steps")]
    public List<ProviderOnboardingStepConfiguration> Steps { get; set; } = [];

    [JsonPropertyName("troubleshooting")]
    public List<ProviderOnboardingTroubleshootingConfiguration> Troubleshooting { get; set; } = [];
}

public sealed class ProviderOnboardingIntroConfiguration
{
    [JsonPropertyName("title")]
    public string Title { get; set; } = string.Empty;

    [JsonPropertyName("summary")]
    public string Summary { get; set; } = string.Empty;
}

public sealed class ProviderOnboardingStepConfiguration
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("title")]
    public string Title { get; set; } = string.Empty;

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("instructions")]
    public List<string> Instructions { get; set; } = [];

    [JsonPropertyName("action")]
    public ProviderOnboardingActionConfiguration? Action { get; set; }

    [JsonPropertyName("credential_keys")]
    public List<string> CredentialKeys { get; set; } = [];
}

public sealed class ProviderOnboardingActionConfiguration
{
    [JsonPropertyName("kind")]
    public string Kind { get; set; } = "information";

    [JsonPropertyName("label")]
    public string? Label { get; set; }

    [JsonPropertyName("url")]
    public string? Url { get; set; }

    [JsonPropertyName("url_role")]
    public string? UrlRole { get; set; }
}

public sealed class ProviderOnboardingTroubleshootingConfiguration
{
    [JsonPropertyName("status")]
    public string Status { get; set; } = string.Empty;

    [JsonPropertyName("title")]
    public string Title { get; set; } = string.Empty;

    [JsonPropertyName("message")]
    public string Message { get; set; } = string.Empty;

    [JsonPropertyName("instructions")]
    public List<string> Instructions { get; set; } = [];

    [JsonPropertyName("help_url")]
    public string? HelpUrl { get; set; }
}

public sealed class ProviderSkipConsequenceConfiguration
{
    [JsonPropertyName("lane")]
    public string Lane { get; set; } = string.Empty;

    [JsonPropertyName("summary")]
    public string Summary { get; set; } = string.Empty;

    [JsonPropertyName("unavailable_capabilities")]
    public List<string> UnavailableCapabilities { get; set; } = [];
}

public sealed class ProviderCredentialFieldConfiguration
{
    /// <summary>Stable storage key such as <c>api_key</c>.</summary>
    [JsonPropertyName("key")]
    public string Key { get; set; } = string.Empty;

    [JsonPropertyName("label")]
    public string Label { get; set; } = string.Empty;

    [JsonPropertyName("input_type")]
    public string InputType { get; set; } = "password";

    [JsonPropertyName("required")]
    public bool Required { get; set; } = true;

    /// <summary>Who supplies this credential: <c>user_supplied</c> or <c>application_managed</c>.</summary>
    [JsonPropertyName("ownership")]
    public string Ownership { get; set; } = "user_supplied";

    /// <summary>Credential purpose used by generic provider authentication UI and services.</summary>
    [JsonPropertyName("purpose")]
    public string? Purpose { get; set; }

    [JsonPropertyName("format_hint")]
    public string? FormatHint { get; set; }

    [JsonPropertyName("minimum_length")]
    public int? MinimumLength { get; set; }

    [JsonPropertyName("maximum_length")]
    public int? MaximumLength { get; set; }

    /// <summary>Server-side local validation pattern. It is not returned to clients.</summary>
    [JsonPropertyName("validation_pattern")]
    public string? ValidationPattern { get; set; }
}

public sealed class ProviderAuthenticationProbeConfiguration
{
    /// <summary>Relative path beneath the provider's configured API endpoint.</summary>
    [JsonPropertyName("path")]
    public string Path { get; set; } = string.Empty;

    [JsonPropertyName("method")]
    public string Method { get; set; } = "GET";

    [JsonPropertyName("success_status_codes")]
    public List<int> SuccessStatusCodes { get; set; } = [200];
}

public sealed class ProviderSequenceManifestRequestConfiguration
{
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; }

    [JsonPropertyName("url_template")]
    public required string UrlTemplate { get; set; }

    [JsonPropertyName("fields")]
    public List<string> Fields { get; set; } = [];

    [JsonPropertyName("page_size")]
    public int PageSize { get; set; }

    [JsonPropertyName("max_pages")]
    public int MaxPages { get; set; }

    [JsonPropertyName("container_kind")]
    public required string ContainerKind { get; set; }

    [JsonPropertyName("expected_total_kind")]
    public required string ExpectedTotalKind { get; set; }
}

public sealed class ProviderRateLimitConfiguration
{
    [JsonPropertyName("requests_per_second")]
    public double? RequestsPerSecond { get; set; }

    [JsonPropertyName("requests_per_minute")]
    public int? RequestsPerMinute { get; set; }

    [JsonPropertyName("burst")]
    public int Burst { get; set; } = 1;

    [JsonPropertyName("max_concurrency")]
    public int MaxConcurrency { get; set; } = 1;
}

/// <summary>
/// UI metadata for a provider, stored under the <c>ui_metadata</c> key in each
/// provider config JSON file. Consumed by <c>GET /providers/catalogue</c>.
/// </summary>
public sealed class ProviderUiMetadata
{
    /// <summary>Hex accent colour for the provider card (e.g. <c>"#FF2D55"</c>).</summary>
    [JsonPropertyName("accent_color")]
    public string AccentColor { get; set; } = "#90A4AE";

    /// <summary>Material icon name (e.g. <c>"MenuBook"</c>).</summary>
    [JsonPropertyName("material_icon")]
    public string MaterialIcon { get; set; } = "Cloud";

    /// <summary>
    /// URL template for linking to the item on the provider's site.
    /// Placeholders are field names (e.g. <c>{apple_books_id}</c>).
    /// Empty string means no external link available.
    /// </summary>
    [JsonPropertyName("external_url_template")]
    public string ExternalUrlTemplate { get; set; } = "";

    /// <summary>
    /// External item links keyed by the canonical or bridge identifier that
    /// supplies the template value. Providers with more than one public entity
    /// type (for example MusicBrainz releases, release groups, and recordings)
    /// declare each link independently here instead of relying on UI code.
    /// </summary>
    [JsonPropertyName("external_links")]
    public Dictionary<string, ProviderExternalLinkConfiguration> ExternalLinks { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Provider category: <c>"Retail"</c>, <c>"Open"</c>, <c>"Image"</c>, or <c>"Local"</c>.</summary>
    [JsonPropertyName("category")]
    public string Category { get; set; } = "Open";

    /// <summary>When <c>true</c>, the provider requires a user-supplied API key.</summary>
    [JsonPropertyName("requires_key")]
    public bool RequiresKey { get; set; }

    /// <summary>Authentication mechanism: <c>"none"</c>, <c>"api_key"</c>, <c>"bearer"</c>, or <c>"basic"</c>.</summary>
    [JsonPropertyName("auth_type")]
    public string AuthType { get; set; } = "none";

    /// <summary>Optional stable role identifier such as <c>canonical_source</c>.</summary>
    [JsonPropertyName("system_role")]
    public string? SystemRole { get; set; }

    /// <summary>Whether the provider is required by a system-defined pipeline stage.</summary>
    [JsonPropertyName("required_system_provider")]
    public bool RequiredSystemProvider { get; set; }

    /// <summary>
    /// Per-media-type labels shown as search capability chips in the Pipeline panel.
    /// Key = media type display name (e.g. <c>"Books"</c>), value = chip labels.
    /// </summary>
    [JsonPropertyName("search_chips")]
    public Dictionary<string, List<string>> SearchChips { get; set; } = [];

    /// <summary>
    /// Per-media-type labels shown as ranking capability chips in the Pipeline panel.
    /// Key = media type display name, value = chip labels.
    /// </summary>
    [JsonPropertyName("ranking_chips")]
    public Dictionary<string, List<string>> RankingChips { get; set; } = [];
}

public sealed class ProviderExternalLinkConfiguration
{
    [JsonPropertyName("label")]
    public string Label { get; set; } = "View source";

    [JsonPropertyName("url_template")]
    public string UrlTemplate { get; set; } = "";

    [JsonPropertyName("tooltip")]
    public string? Tooltip { get; set; }
}

/// <summary>HTTP client configuration for config-driven provider adapters.</summary>
public sealed class HttpClientConfig
{
    [JsonPropertyName("timeout_seconds")]
    public int TimeoutSeconds { get; set; } = 10;

    [JsonPropertyName("user_agent")]
    public string? UserAgent { get; set; }

    /// <summary>
    /// API key or token for authenticated providers (e.g. TMDB or Comic Vine API keys).
    /// Stored in the provider config file. Empty or <c>null</c> = no key configured.
    /// </summary>
    [JsonPropertyName("api_key")]
    public string? ApiKey { get; set; }

    /// <summary>Optional administrator supplied replacement for an application managed API key.</summary>
    [JsonPropertyName("api_key_override")]
    public string? ApiKeyOverride { get; set; }

    /// <summary>Optional user/client key used alongside an application API key.</summary>
    [JsonPropertyName("client_key")]
    public string? ClientKey { get; set; }

    /// <summary>Optional access token for providers whose declared credential purpose is access_token.</summary>
    [JsonPropertyName("access_token")]
    public string? AccessToken { get; set; }

    /// <summary>Subscriber PIN for a user-supported TVDB project key, when required.</summary>
    [JsonPropertyName("pin")]
    public string? Pin { get; set; }

    /// <summary>
    /// How the API key is delivered to the provider. Values:
    /// <c>"bearer"</c> — sent as <c>Authorization: Bearer {key}</c> header (e.g. TMDB).
    /// <c>"query"</c> — appended as a query parameter using <see cref="ApiKeyParamName"/>.
    /// <c>"header"</c> — sent as a custom header using <see cref="ApiKeyParamName"/> as the header name.
    /// <c>"basic"</c> — sent as <c>Authorization: Basic {base64(username:password)}</c> header.
    /// Default: <c>null</c> (no API key delivery).
    /// </summary>
    [JsonPropertyName("api_key_delivery")]
    public string? ApiKeyDelivery { get; set; }

    /// <summary>
    /// The query parameter name (for <c>"query"</c> delivery) or header name (for <c>"header"</c> delivery).
    /// Example: <c>"api_key"</c> for an API key query parameter.
    /// </summary>
    [JsonPropertyName("api_key_param_name")]
    public string? ApiKeyParamName { get; set; }

    /// <summary>
    /// Username for HTTP Basic Authentication.
    /// Used when <see cref="ApiKeyDelivery"/> is <c>"basic"</c>.
    /// </summary>
    [JsonPropertyName("username")]
    public string? Username { get; set; }

    /// <summary>
    /// Password for HTTP Basic Authentication.
    /// Used when <see cref="ApiKeyDelivery"/> is <c>"basic"</c>.
    /// </summary>
    [JsonPropertyName("password")]
    public string? Password { get; set; }
}

/// <summary>Media type and entity type capability filters for config-driven adapters.</summary>
public sealed class CanHandleConfig
{
    [JsonPropertyName("media_types")]
    public List<string> MediaTypes { get; set; } = [];

    [JsonPropertyName("entity_types")]
    public List<string> EntityTypes { get; set; } = [];
}

/// <summary>
/// A single search strategy for a config-driven adapter.
/// Strategies are tried in <see cref="Priority"/> order; first success wins.
/// </summary>
public sealed class SearchStrategyConfig
{
    /// <summary>Human-readable strategy name for logging.</summary>
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    /// <summary>Execution priority — lower numbers tried first.</summary>
    [JsonPropertyName("priority")]
    public int Priority { get; set; }

    /// <summary>
    /// Request fields that must be non-empty for this strategy to attempt.
    /// Values: <c>"title"</c>, <c>"author"</c>, <c>"isbn"</c>, <c>"asin"</c>, etc.
    /// </summary>
    [JsonPropertyName("required_fields")]
    public List<string> RequiredFields { get; set; } = [];

    /// <summary>
    /// URL template with placeholders: <c>{base_url}</c>, <c>{title}</c>, <c>{author}</c>,
    /// <c>{isbn}</c>, <c>{asin}</c>, <c>{query}</c>. All values are URI-escaped.
    /// </summary>
    [JsonPropertyName("url_template")]
    public string UrlTemplate { get; set; } = string.Empty;

    /// <summary>
    /// Template for building the <c>{query}</c> placeholder.
    /// Example: <c>"{title} {author}"</c>. Missing fields are omitted.
    /// </summary>
    [JsonPropertyName("query_template")]
    public string? QueryTemplate { get; set; }

    /// <summary>
    /// Structured, provider-configured query composition. When present this is
    /// used instead of <see cref="QueryTemplate"/>.
    /// </summary>
    [JsonPropertyName("query")]
    public QueryCompositionConfig? Query { get; set; }

    /// <summary>
    /// JSON path to the results array in the response (e.g. <c>"docs"</c>, <c>"items"</c>).
    /// When absent, the response is treated as a direct result object.
    /// </summary>
    [JsonPropertyName("results_path")]
    public string? ResultsPath { get; set; }

    /// <summary>Index within the results array to use. Default: 0 (first result).</summary>
    [JsonPropertyName("result_index")]
    public int ResultIndex { get; set; }

    /// <summary>
    /// When <c>true</c>, HTTP 404 is treated as "no results" rather than an error.
    /// Useful for direct-lookup APIs like Audnexus.
    /// </summary>
    [JsonPropertyName("tolerate_404")]
    public bool Tolerate404 { get; set; }

    /// <summary>
    /// Maximum number of results to return in multi-result search mode.
    /// Overrides the <c>limit</c> parameter passed to <c>SearchAsync</c> when set
    /// to a positive value. Default 0 means "use the caller's limit".
    /// </summary>
    [JsonPropertyName("max_results")]
    public int MaxResults { get; set; }

    /// <summary>
    /// Number of results to request from the API during automatic single-result matching
    /// (<c>FetchAsync</c>). Because only <c>result_index</c> + 1 results are used,
    /// there is no need to request the full <c>max_results</c> count.
    /// Replaces the <c>{limit}</c> placeholder in the URL template when called via
    /// <c>FetchAsync</c>. Default: 5.
    /// </summary>
    [JsonPropertyName("fetch_limit")]
    public int FetchLimit { get; set; } = 5;

    /// <summary>
    /// Optional media type filter. When non-empty, this strategy is only used
    /// when the request's media type matches one of these values.
    /// When empty/null, the strategy is used for all media types.
    /// Values: <c>"Books"</c>, <c>"Audiobooks"</c>, <c>"Movies"</c>, <c>"Comics"</c>, <c>"TV"</c>.
    /// </summary>
    [JsonPropertyName("media_types")]
    public List<string>? MediaTypes { get; set; }

    /// <summary>
    /// Optional nested selection config for choosing a sub-result from within the
    /// matched result (e.g. picking the best release from a MusicBrainz recording).
    /// When set, the adapter navigates into the nested array at <see cref="ReleaseSelectionConfig.Path"/>
    /// and applies filters, sort, and soft preferences to pick the best sub-result.
    /// </summary>
    [JsonPropertyName("release_selection")]
    public ReleaseSelectionConfig? ReleaseSelection { get; set; }

    /// <summary>Optional generic scoring and filtering for result arrays.</summary>
    [JsonPropertyName("candidate_selection")]
    public CandidateSelectionConfig? CandidateSelection { get; set; }

    /// <summary>
    /// Claims that must not be emitted by this strategy. This is useful when a
    /// provider returns work-level aggregate arrays that cannot be attributed to
    /// the exact edition represented by the lookup request.
    /// </summary>
    [JsonPropertyName("suppress_claims")]
    public List<string> SuppressClaims { get; set; } = [];

    /// <summary>
    /// Maps a claim key to a lookup-request field whose exact value replaces the
    /// provider response for this strategy (for example, <c>isbn</c> to
    /// <c>isbn</c> on an exact ISBN lookup).
    /// </summary>
    [JsonPropertyName("request_claim_overrides")]
    public Dictionary<string, string> RequestClaimOverrides { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);
}

public sealed class QueryCompositionConfig
{
    /// <summary>Query escaping syntax: <c>plain</c> or <c>lucene</c>.</summary>
    [JsonPropertyName("syntax")]
    public string Syntax { get; set; } = "plain";

    /// <summary>Operator placed between emitted clauses.</summary>
    [JsonPropertyName("operator")]
    public string Operator { get; set; } = "AND";

    /// <summary>Ordered configured query clauses.</summary>
    [JsonPropertyName("clauses")]
    public List<QueryClauseConfig> Clauses { get; set; } = [];
}

public sealed class QueryClauseConfig
{
    /// <summary>Optional provider query field name.</summary>
    [JsonPropertyName("field")]
    public string? Field { get; set; }

    /// <summary>Lookup-request field supplying the clause value.</summary>
    [JsonPropertyName("value")]
    public string Value { get; set; } = string.Empty;

    /// <summary>Match formatting: <c>term</c> or <c>phrase</c>.</summary>
    [JsonPropertyName("match")]
    public string Match { get; set; } = "term";

    /// <summary>Whether absence of this value suppresses the entire strategy.</summary>
    [JsonPropertyName("required")]
    public bool Required { get; set; }
}

public sealed class CandidateSelectionConfig
{
    [JsonPropertyName("title_paths")]
    public List<string> TitlePaths { get; set; } = [];

    [JsonPropertyName("creator_paths")]
    public List<string> CreatorPaths { get; set; } = [];

    [JsonPropertyName("creator_request_fields")]
    public List<string> CreatorRequestFields { get; set; } = ["artist", "author", "composer"];

    [JsonPropertyName("minimum_title_score")]
    public double MinimumTitleScore { get; set; }

    [JsonPropertyName("minimum_creator_score")]
    public double MinimumCreatorScore { get; set; }

    [JsonPropertyName("require_nested_selection")]
    public bool RequireNestedSelection { get; set; }

    [JsonPropertyName("request_filters")]
    public List<RequestCandidateFilterConfig> RequestFilters { get; set; } = [];
}

public sealed class RequestCandidateFilterConfig
{
    [JsonPropertyName("request_field")]
    public string RequestField { get; set; } = string.Empty;

    [JsonPropertyName("candidate_paths")]
    public List<string> CandidatePaths { get; set; } = [];

    /// <summary>
    /// Comparison operator: <c>exact</c>, <c>normalized_similarity</c>, or
    /// <c>album_identity</c>.
    /// </summary>
    [JsonPropertyName("operator")]
    public string Operator { get; set; } = "exact";

    [JsonPropertyName("minimum_score")]
    public double MinimumScore { get; set; } = 1.0;

    [JsonPropertyName("required")]
    public bool Required { get; set; }
}

/// <summary>
/// Maps a JSON path in the API response to a metadata claim.
/// </summary>
public sealed class FieldMappingConfig
{
    /// <summary>The metadata claim key (e.g. <c>"title"</c>, <c>"cover"</c>).</summary>
    [JsonPropertyName("claim_key")]
    public string ClaimKey { get; set; } = string.Empty;

    /// <summary>
    /// JSON path expression to extract the value. Supports dot-notation,
    /// array indexing (<c>[0]</c>), and wildcard iteration (<c>[*].name</c>).
    /// </summary>
    [JsonPropertyName("json_path")]
    public string JsonPath { get; set; } = string.Empty;

    /// <summary>Confidence value (0.0–1.0) assigned to claims produced by this mapping.</summary>
    [JsonPropertyName("confidence")]
    public double Confidence { get; set; } = 0.5;

    /// <summary>
    /// Optional named transform to apply to the extracted value.
    /// See <c>ValueTransformCatalog</c> for available transforms.
    /// </summary>
    [JsonPropertyName("transform")]
    public string? Transform { get; set; }

    /// <summary>
    /// Optional arguments passed to the transform function.
    /// Interpretation is transform-specific.
    /// </summary>
    [JsonPropertyName("transform_args")]
    public string? TransformArgs { get; set; }

    /// <summary>
    /// Optional media type filter. When non-empty, this mapping is only used
    /// when the request's media type matches one of these values.
    /// When empty/null, the mapping is used for all media types.
    /// Values: <c>"Books"</c>, <c>"Audiobooks"</c>, <c>"Movies"</c>, <c>"Comics"</c>, <c>"TV"</c>.
    /// </summary>
    [JsonPropertyName("media_types")]
    public List<string>? MediaTypes { get; set; }

    /// <summary>
    /// When a strategy uses <see cref="SearchStrategyConfig.ReleaseSelection"/>, specifies
    /// which result node to extract from: <c>"recording"</c> (top-level match) or
    /// <c>"release"</c> (nested selected sub-result). When null, extracts from the
    /// top-level result (default behaviour for all other providers).
    /// </summary>
    [JsonPropertyName("source")]
    public string? Source { get; set; }

    /// <summary>
    /// Optional condition that must be met on the source node for this mapping to emit
    /// a claim. Used to prevent speculative claims (e.g. only emit a cover URL when
    /// the API confirms artwork exists).
    /// </summary>
    [JsonPropertyName("condition")]
    public SelectionFilter? Condition { get; set; }
}

/// <summary>
/// Configures nested sub-result selection within a matched result.
/// Used when an API returns hierarchical data (e.g. MusicBrainz recordings
/// contain nested release arrays) and the adapter needs to pick the best
/// sub-result based on type, date, and artwork availability.
/// </summary>
public sealed class ReleaseSelectionConfig
{
    /// <summary>JSON path to the nested array within the matched result (e.g. <c>"releases"</c>).</summary>
    [JsonPropertyName("path")]
    public string Path { get; set; } = string.Empty;

    /// <summary>
    /// Hard filters — sub-results must match ALL filters to be considered.
    /// Example: <c>status == "Official"</c>, <c>release-group.primary-type == "Album"</c>.
    /// </summary>
    [JsonPropertyName("filters")]
    public List<SelectionFilter>? Filters { get; set; }

    /// <summary>
    /// Sort order applied to filtered candidates. First sort field is primary.
    /// Example: sort by <c>date</c> ascending to prefer the earliest release.
    /// </summary>
    [JsonPropertyName("sort")]
    public List<SelectionSort>? Sort { get; set; }

    /// <summary>
    /// Soft preferences — among similarly-ranked candidates, prefer those matching
    /// these conditions. Example: prefer releases where <c>cover-art-archive.artwork == true</c>.
    /// </summary>
    [JsonPropertyName("prefer")]
    public List<SelectionFilter>? Prefer { get; set; }

    /// <summary>
    /// Fallback release types to try (in order) when no candidates pass the primary filters.
    /// Example: <c>["Single", "EP"]</c> — if no Album found, try Singles, then EPs.
    /// Fallback candidates must still have <c>status == "Official"</c>.
    /// </summary>
    [JsonPropertyName("fallback_types")]
    public List<string>? FallbackTypes { get; set; }

    /// <summary>
    /// Configured request-to-release constraints applied before sorting.
    /// </summary>
    [JsonPropertyName("request_filters")]
    public List<RequestCandidateFilterConfig> RequestFilters { get; set; } = [];
}

/// <summary>
/// A filter condition that checks a JSON path against an expected value.
/// Supports string and boolean comparisons.
/// </summary>
public sealed class SelectionFilter
{
    /// <summary>JSON path to evaluate on the candidate node (e.g. <c>"release-group.primary-type"</c>).</summary>
    [JsonPropertyName("path")]
    public string Path { get; set; } = string.Empty;

    /// <summary>
    /// Expected value. Supports strings (<c>"Album"</c>) and booleans (<c>true</c>).
    /// Comparison is case-insensitive for strings.
    /// </summary>
    [JsonPropertyName("equals")]
    public JsonElement? EqualsValue { get; set; }
}

/// <summary>
/// A sort directive for ordering candidates by a JSON path value.
/// </summary>
public sealed class SelectionSort
{
    /// <summary>JSON path to the value used for sorting (e.g. <c>"date"</c>).</summary>
    [JsonPropertyName("path")]
    public string Path { get; set; } = string.Empty;

    /// <summary>Sort direction: <c>"asc"</c> (default) or <c>"desc"</c>.</summary>
    [JsonPropertyName("direction")]
    public string Direction { get; set; } = "asc";
}
