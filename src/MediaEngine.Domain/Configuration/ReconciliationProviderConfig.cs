using System.Text.Json.Serialization;
using MediaEngine.Domain;

namespace MediaEngine.Domain.Configuration;

/// <summary>Configuration for the Wikidata Reconciliation + Data Extension adapter.</summary>
public sealed class ReconciliationProviderConfig
{
    [JsonPropertyName("name")] public string Name { get; set; } = "wikidata_reconciliation";
    [JsonPropertyName("provider_id")] public string ProviderId { get; set; } = WellKnownProviders.Wikidata.ToString();
    [JsonPropertyName("adapter_type")] public string AdapterType { get; set; } = "reconciliation";
    [JsonPropertyName("enabled")] public bool Enabled { get; set; } = true;
    [JsonPropertyName("hydration_stages")] public List<int> HydrationStages { get; set; } = [1];
    [JsonPropertyName("endpoints")] public ReconciliationEndpoints Endpoints { get; set; } = new();
    [JsonPropertyName("throttle_ms")] public int ThrottleMs { get; set; } = 200;
    [JsonPropertyName("max_concurrency")] public int MaxConcurrency { get; set; } = 3;
    [JsonPropertyName("cache_ttl_hours")] public int CacheTtlHours { get; set; } = 168;
    [JsonPropertyName("batch_size")] public int BatchSize { get; set; } = 50;
    [JsonPropertyName("reconciliation")] public ReconciliationSettings Reconciliation { get; set; } = new();
    [JsonPropertyName("instance_of_classes")] public Dictionary<string, List<string>> InstanceOfClasses { get; set; } = new();
    [JsonPropertyName("exclude_classes")] public Dictionary<string, List<string>> ExcludeClasses { get; set; } = new();
    [JsonPropertyName("edition_pivot")] public Dictionary<string, EditionPivotRuleEntry> EditionPivot { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    [JsonPropertyName("bridge_resolution")] public BridgeResolutionConfiguration BridgeResolution { get; set; } = new();
    [JsonPropertyName("data_extension")] public DataExtensionSettings DataExtension { get; set; } = new();

    /// <summary>
    /// Returns the edition pivot rules as an <see cref="EditionPivotConfiguration"/>.
    /// </summary>
    public EditionPivotConfiguration GetEditionPivotConfiguration() => new()
    {
        Rules = new Dictionary<string, EditionPivotRuleEntry>(EditionPivot, StringComparer.OrdinalIgnoreCase),
    };
}

public sealed class BridgeResolutionConfiguration
{
    [JsonPropertyName("scopes")]
    public Dictionary<string, BridgeResolutionScopeConfiguration> Scopes { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public BridgeResolutionScopeConfiguration? GetScope(string scope) =>
        Scopes.TryGetValue(scope, out var configuration) ? configuration : null;
}

public sealed class BridgeResolutionScopeConfiguration
{
    [JsonPropertyName("target_ids")]
    public List<string> TargetIds { get; set; } = [];

    [JsonPropertyName("context_ids")]
    public List<string> ContextIds { get; set; } = [];

    [JsonPropertyName("allow_constrained_text_fallback")]
    public bool AllowConstrainedTextFallback { get; set; }
}

public sealed class ReconciliationEndpoints
{
    [JsonPropertyName("reconciliation")] public string Reconciliation { get; set; } = "https://wikidata.reconci.link/en/api";
    [JsonPropertyName("commons_file_path")] public string CommonsFilePath { get; set; } = "https://commons.wikimedia.org/wiki/Special:FilePath/";
}

public sealed class ReconciliationSettings
{
    [JsonPropertyName("auto_accept_threshold")] public int AutoAcceptThreshold { get; set; } = 95;
    [JsonPropertyName("review_threshold")] public int ReviewThreshold { get; set; } = 70;
    [JsonPropertyName("max_candidates")] public int MaxCandidates { get; set; } = 5;
    [JsonPropertyName("person_property_constraints")] public Dictionary<string, string> PersonPropertyConstraints { get; set; } = new();
    [JsonPropertyName("cast_member_limit")] public int CastMemberLimit { get; set; } = 20;
}

public sealed class DataExtensionSettings
{
    [JsonPropertyName("work_properties")] public DataExtensionPropertyGroup WorkProperties { get; set; } = new();
    [JsonPropertyName("person_properties")] public DataExtensionPropertyGroup PersonProperties { get; set; } = new();
    [JsonPropertyName("audiobook_edition_properties")] public List<string> AudiobookEditionProperties { get; set; } = [];
    [JsonPropertyName("character_properties")] public DataExtensionPropertyGroup CharacterProperties { get; set; } = new();
    [JsonPropertyName("location_properties")] public DataExtensionPropertyGroup LocationProperties { get; set; } = new();
    [JsonPropertyName("organization_properties")] public DataExtensionPropertyGroup OrganizationProperties { get; set; } = new();
    [JsonPropertyName("event_properties")] public DataExtensionPropertyGroup EventProperties { get; set; } = new();
    [JsonPropertyName("object_properties")] public DataExtensionPropertyGroup ObjectProperties { get; set; } = new();
    [JsonPropertyName("species_properties")] public DataExtensionPropertyGroup SpeciesProperties { get; set; } = new();
    [JsonPropertyName("property_labels")] public Dictionary<string, string> PropertyLabels { get; set; } = new();
}

public sealed class DataExtensionPropertyGroup
{
    [JsonPropertyName("core")] public List<string> Core { get; set; } = [];
    [JsonPropertyName("bridges")] public List<string> Bridges { get; set; } = [];
    [JsonPropertyName("editions")] public List<string> Editions { get; set; } = [];
    [JsonPropertyName("social")] public List<string> Social { get; set; } = [];
    [JsonPropertyName("pen_names")] public List<string> PenNames { get; set; } = [];
    [JsonPropertyName("group")] public List<string> Group { get; set; } = [];
}
