using System.Text.Json.Serialization;

namespace MediaEngine.Contracts.Settings;

public sealed class ProviderCatalogueDto
{
    [JsonPropertyName("providerId")]
    public string ProviderId { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("displayName")]
    public string DisplayName { get; set; } = string.Empty;

    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; }

    [JsonPropertyName("domain")]
    public string Domain { get; set; } = string.Empty;

    [JsonPropertyName("mediaTypes")]
    public List<string> MediaTypes { get; set; } = [];

    [JsonPropertyName("accentColor")]
    public string AccentColor { get; set; } = "#90A4AE";

    [JsonPropertyName("materialIcon")]
    public string MaterialIcon { get; set; } = "Cloud";

    [JsonPropertyName("externalUrlTemplate")]
    public string? ExternalUrlTemplate { get; set; }

    [JsonPropertyName("externalLinks")]
    public Dictionary<string, ProviderExternalLinkDto> ExternalLinks { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);

    [JsonPropertyName("category")]
    public string Category { get; set; } = "Open";

    [JsonPropertyName("requiresKey")]
    public bool RequiresKey { get; set; }

    [JsonPropertyName("authType")]
    public string AuthType { get; set; } = "none";

    [JsonPropertyName("searchChips")]
    public Dictionary<string, List<string>> SearchChips { get; set; } = [];

    [JsonPropertyName("rankingChips")]
    public Dictionary<string, List<string>> RankingChips { get; set; } = [];

    [JsonPropertyName("iconPath")]
    public string? IconPath { get; set; }

    [JsonPropertyName("hydrationStages")]
    public List<int> HydrationStages { get; set; } = [];

    [JsonPropertyName("capabilities")]
    public List<string> Capabilities { get; set; } = [];

    [JsonPropertyName("systemRole")]
    public string? SystemRole { get; set; }

    [JsonPropertyName("requiredSystemProvider")]
    public bool RequiredSystemProvider { get; set; }

    [JsonPropertyName("languageStrategy")]
    public string LanguageStrategy { get; set; } = "source";

    [JsonPropertyName("onboarding")]
    public ProviderOnboardingDto? Onboarding { get; set; }
}

public sealed class ProviderOnboardingDto
{
    [JsonPropertyName("classification")]
    public string Classification { get; set; } = "optional";

    [JsonPropertyName("signupUrl")]
    public string? SignupUrl { get; set; }

    [JsonPropertyName("helpUrl")]
    public string? HelpUrl { get; set; }

    [JsonPropertyName("termsUrl")]
    public string? TermsUrl { get; set; }

    [JsonPropertyName("privacyUrl")]
    public string? PrivacyUrl { get; set; }

    [JsonPropertyName("supportedLanes")]
    public List<string> SupportedLanes { get; set; } = [];

    [JsonPropertyName("requiredScopes")]
    public List<string> RequiredScopes { get; set; } = [];

    [JsonPropertyName("skipConsequences")]
    public List<ProviderSkipConsequenceDto> SkipConsequences { get; set; } = [];

    [JsonPropertyName("credentials")]
    public List<ProviderCredentialFieldDto> Credentials { get; set; } = [];

    [JsonPropertyName("intro")]
    public ProviderOnboardingIntroDto? Intro { get; set; }

    [JsonPropertyName("steps")]
    public List<ProviderOnboardingStepDto> Steps { get; set; } = [];

    [JsonPropertyName("troubleshooting")]
    public List<ProviderOnboardingTroubleshootingDto> Troubleshooting { get; set; } = [];
}

public sealed class ProviderOnboardingIntroDto
{
    [JsonPropertyName("title")]
    public string Title { get; set; } = string.Empty;

    [JsonPropertyName("summary")]
    public string Summary { get; set; } = string.Empty;
}

public sealed class ProviderOnboardingStepDto
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
    public ProviderOnboardingActionDto? Action { get; set; }

    [JsonPropertyName("credentialKeys")]
    public List<string> CredentialKeys { get; set; } = [];
}

public sealed class ProviderOnboardingActionDto
{
    [JsonPropertyName("kind")]
    public string Kind { get; set; } = "information";

    [JsonPropertyName("label")]
    public string? Label { get; set; }

    [JsonPropertyName("url")]
    public string? Url { get; set; }

    [JsonPropertyName("urlRole")]
    public string? UrlRole { get; set; }
}

public sealed class ProviderOnboardingTroubleshootingDto
{
    [JsonPropertyName("status")]
    public string Status { get; set; } = string.Empty;

    [JsonPropertyName("title")]
    public string Title { get; set; } = string.Empty;

    [JsonPropertyName("message")]
    public string Message { get; set; } = string.Empty;

    [JsonPropertyName("instructions")]
    public List<string> Instructions { get; set; } = [];

    [JsonPropertyName("helpUrl")]
    public string? HelpUrl { get; set; }
}

public sealed class ProviderSkipConsequenceDto
{
    [JsonPropertyName("lane")]
    public string Lane { get; set; } = string.Empty;

    [JsonPropertyName("summary")]
    public string Summary { get; set; } = string.Empty;

    [JsonPropertyName("unavailableCapabilities")]
    public List<string> UnavailableCapabilities { get; set; } = [];
}

public sealed class ProviderCredentialFieldDto
{
    [JsonPropertyName("key")]
    public string Key { get; set; } = string.Empty;

    [JsonPropertyName("label")]
    public string Label { get; set; } = string.Empty;

    [JsonPropertyName("inputType")]
    public string InputType { get; set; } = "password";

    [JsonPropertyName("required")]
    public bool Required { get; set; }

    [JsonPropertyName("ownership")]
    public string Ownership { get; set; } = "user_supplied";

    [JsonPropertyName("purpose")]
    public string Purpose { get; set; } = "other";

    [JsonPropertyName("formatHint")]
    public string? FormatHint { get; set; }

    [JsonPropertyName("minimumLength")]
    public int? MinimumLength { get; set; }

    [JsonPropertyName("maximumLength")]
    public int? MaximumLength { get; set; }

    [JsonPropertyName("configured")]
    public bool Configured { get; set; }
}

public sealed class ProviderCredentialWriteRequest
{
    [JsonPropertyName("credentials")]
    public Dictionary<string, string> Credentials { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class ProviderCredentialOperationResultDto
{
    [JsonPropertyName("success")]
    public bool Success { get; set; }

    [JsonPropertyName("status")]
    public string Status { get; set; } = "connectivity_failure";

    [JsonPropertyName("message")]
    public string Message { get; set; } = string.Empty;

    [JsonPropertyName("responseTimeMs")]
    public int ResponseTimeMs { get; set; }

    [JsonPropertyName("retryAfterSeconds")]
    public int? RetryAfterSeconds { get; set; }

    [JsonPropertyName("fieldErrors")]
    public Dictionary<string, string> FieldErrors { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class ProviderExternalLinkDto
{
    [JsonPropertyName("label")]
    public string Label { get; set; } = "View source";

    [JsonPropertyName("urlTemplate")]
    public string UrlTemplate { get; set; } = string.Empty;

    [JsonPropertyName("tooltip")]
    public string? Tooltip { get; set; }
}

public sealed record ProviderStatusDto(
    [property: JsonPropertyName("name")] string Name = "",
    [property: JsonPropertyName("display_name")] string DisplayName = "",
    [property: JsonPropertyName("enabled")] bool Enabled = false,
    [property: JsonPropertyName("is_zero_key")] bool IsZeroKey = false,
    [property: JsonPropertyName("is_reachable")] bool IsReachable = false,
    [property: JsonPropertyName("domain")] string Domain = "",
    [property: JsonPropertyName("capability_tags")] List<string>? CapabilityTags = null,
    [property: JsonPropertyName("default_weight")] double DefaultWeight = 1.0,
    [property: JsonPropertyName("field_weights")] Dictionary<string, double>? FieldWeights = null,
    [property: JsonPropertyName("hydration_stages")] List<int>? HydrationStages = null,
    [property: JsonPropertyName("endpoints")] Dictionary<string, string>? Endpoints = null,
    [property: JsonPropertyName("field_mappings")] List<FieldMappingDto>? FieldMappings = null,
    [property: JsonPropertyName("throttle_ms")] int ThrottleMs = 0,
    [property: JsonPropertyName("max_concurrency")] int MaxConcurrency = 1,
    [property: JsonPropertyName("language_strategy")] string? LanguageStrategy = null,
    [property: JsonPropertyName("available_fields")] List<string>? AvailableFields = null,
    [property: JsonPropertyName("media_types")] List<string>? MediaTypes = null,
    [property: JsonPropertyName("requires_api_key")] bool RequiresApiKey = false,
    [property: JsonPropertyName("has_api_key")] bool HasApiKey = false,
    [property: JsonPropertyName("api_key_delivery")] string? ApiKeyDelivery = null,
    [property: JsonPropertyName("api_key_param_name")] string? ApiKeyParamName = null,
    [property: JsonPropertyName("timeout_seconds")] int TimeoutSeconds = 10,
    [property: JsonPropertyName("custom_icon_name")] string? CustomIconName = null,
    [property: JsonPropertyName("health_status")] string? HealthStatus = null,
    [property: JsonPropertyName("consecutive_failures")] int ConsecutiveFailures = 0,
    [property: JsonPropertyName("last_success_at")] string? LastSuccessAt = null,
    [property: JsonPropertyName("last_failure_at")] string? LastFailureAt = null,
    [property: JsonPropertyName("last_failure_reason")] string? LastFailureReason = null,
    [property: JsonPropertyName("down_since")] string? DownSince = null,
    [property: JsonPropertyName("connection_status")] string? ConnectionStatus = null,
    [property: JsonPropertyName("connection_checked_at")] string? ConnectionCheckedAt = null,
    [property: JsonPropertyName("connection_message")] string? ConnectionMessage = null);

public sealed class FieldMappingDto
{
    [JsonPropertyName("claim_key")]
    public string ClaimKey { get; set; } = string.Empty;

    [JsonPropertyName("json_path")]
    public string JsonPath { get; set; } = string.Empty;

    [JsonPropertyName("confidence")]
    public double Confidence { get; set; } = 0.5;

    [JsonPropertyName("transform")]
    public string? Transform { get; set; }

    [JsonPropertyName("transform_args")]
    public string? TransformArgs { get; set; }
}

public sealed class ProviderTestResultDto
{
    public ProviderTestResultDto()
    {
    }

    public ProviderTestResultDto(bool success, int responseTimeMs, List<string> sampleFields, string message)
    {
        Success = success;
        ResponseTimeMs = responseTimeMs;
        SampleFields = sampleFields;
        Message = message;
    }

    [JsonPropertyName("success")]
    public bool Success { get; init; }

    [JsonPropertyName("response_time_ms")]
    public int ResponseTimeMs { get; init; }

    [JsonPropertyName("sample_fields")]
    public List<string> SampleFields { get; init; } = [];

    [JsonPropertyName("message")]
    public string Message { get; init; } = string.Empty;

    [JsonPropertyName("status")]
    public string Status { get; init; } = string.Empty;
}

public sealed class ProviderSampleResultDto
{
    public ProviderSampleResultDto()
    {
    }

    public ProviderSampleResultDto(
        string providerName,
        List<ProviderSampleClaimDto> claims,
        string? message = null)
    {
        ProviderName = providerName;
        Claims = claims;
        Message = message;
    }

    [JsonPropertyName("provider_name")]
    public string ProviderName { get; init; } = string.Empty;

    [JsonPropertyName("claims")]
    public List<ProviderSampleClaimDto> Claims { get; init; } = [];

    [JsonPropertyName("message")]
    public string? Message { get; init; }
}

public sealed class ProviderSampleClaimDto
{
    public ProviderSampleClaimDto()
    {
    }

    public ProviderSampleClaimDto(string key, string value, double confidence)
    {
        Key = key;
        Value = value;
        Confidence = confidence;
    }

    [JsonPropertyName("key")]
    public string Key { get; init; } = string.Empty;

    [JsonPropertyName("value")]
    public string Value { get; init; } = string.Empty;

    [JsonPropertyName("confidence")]
    public double Confidence { get; init; }
}

public sealed class UpdateProviderRequest
{
    [JsonPropertyName("enabled")]
    public bool Enabled { get; init; }
}

public sealed class ProviderSampleRequest
{
    [JsonPropertyName("title")]
    public string? Title { get; init; }

    [JsonPropertyName("author")]
    public string? Author { get; init; }

    [JsonPropertyName("isbn")]
    public string? Isbn { get; init; }

    [JsonPropertyName("asin")]
    public string? Asin { get; init; }

    [JsonPropertyName("media_type")]
    public string? MediaType { get; init; }
}

public sealed class ProviderConfigUpdateDto
{
    [JsonPropertyName("enabled")]
    public bool? Enabled { get; set; } = true;

    [JsonPropertyName("weight")]
    public double? Weight { get; set; } = 1.0;

    [JsonPropertyName("field_weights")]
    public Dictionary<string, double>? FieldWeights { get; set; } = new();

    [JsonPropertyName("capability_tags")]
    public List<string>? CapabilityTags { get; set; } = [];

    [JsonPropertyName("endpoints")]
    public Dictionary<string, string>? Endpoints { get; set; } = new();

    [JsonPropertyName("throttle_ms")]
    public int? ThrottleMs { get; set; } = 500;

    [JsonPropertyName("max_concurrency")]
    public int? MaxConcurrency { get; set; } = 1;

    [JsonPropertyName("language_strategy")]
    public string? LanguageStrategy { get; set; }

    [JsonPropertyName("field_mappings")]
    public List<FieldMappingDto>? FieldMappings { get; set; }

    [JsonPropertyName("timeout_seconds")]
    public int? TimeoutSeconds { get; set; }

    [JsonPropertyName("custom_icon_name")]
    public string? CustomIconName { get; set; }
}
