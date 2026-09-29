using MediaEngine.Contracts.Settings;
using MediaEngine.Domain.Configuration;
using MediaEngine.Web.Components.Settings;
using MediaEngine.Web.Components.Shared;
using MudBlazor;
using PipelineConfiguration = MediaEngine.Contracts.Settings.PipelineConfiguration;
using PipelineProviderEntry = MediaEngine.Contracts.Settings.PipelineProviderEntry;

namespace MediaEngine.Web.Services.Integration;

/// <summary>
/// Composes the provider inventory and explanatory ingestion flow from the
/// Engine's live provider catalogue, status, pipeline, and hydration settings.
/// </summary>
public sealed class MetadataSettingsStateService
{
    public static readonly string[] MediaTypeOrder = ["Books", "Audiobooks", "Comics", "Movies", "TV", "Music"];

    private readonly IEngineApiClient _api;
    private readonly ProviderCatalogueService _catalogueService;

    public MetadataSettingsStateService(IEngineApiClient api, ProviderCatalogueService catalogueService)
    {
        _api = api;
        _catalogueService = catalogueService;
    }

    public async Task<MetadataSettingsSnapshot> LoadAsync(CancellationToken ct = default)
    {
        var catalogueTask = _catalogueService.GetCatalogueAsync(ct);
        var statusesTask = _api.GetProviderStatusAsync(ct);
        var pipelinesTask = _api.GetPipelinesAsync(ct);
        var hydrationTask = _api.GetHydrationSettingsAsync(ct);
        await Task.WhenAll(catalogueTask, statusesTask, pipelinesTask, hydrationTask);

        var catalogue = await catalogueTask;
        var statuses = (await statusesTask).ToDictionary(status => status.Name, StringComparer.OrdinalIgnoreCase);
        var pipelines = await pipelinesTask ?? new PipelineConfiguration();
        var hydration = await hydrationTask;
        var providers = catalogue
            .Where(IsUserVisibleProvider)
            .Select(provider => BuildProvider(provider, statuses.GetValueOrDefault(provider.Name), pipelines, hydration))
            .OrderBy(provider => provider.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var flows = MediaTypeOrder.Select(mediaType => BuildFlow(mediaType, pipelines, providers)).ToList();

        return new MetadataSettingsSnapshot(providers, flows, statuses, hydration);
    }

    public static string NormalizeMediaType(string? value)
    {
        var normalized = new string((value ?? string.Empty).Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
        return normalized switch
        {
            "book" or "books" => "Books",
            "audiobook" or "audiobooks" => "Audiobooks",
            "comic" or "comics" => "Comics",
            "movie" or "movies" => "Movies",
            "tv" or "tvshow" or "tvshows" => "TV",
            "music" or "song" or "songs" => "Music",
            _ => string.Empty,
        };
    }

    public static string MediaTypeSlug(string mediaType) => NormalizeMediaType(mediaType) switch
    {
        "Books" => "books",
        "Audiobooks" => "audiobooks",
        "Comics" => "comics",
        "Movies" => "movies",
        "TV" => "tv",
        "Music" => "music",
        _ => string.Empty,
    };

    public static string MediaTypeLabel(string mediaType) => NormalizeMediaType(mediaType) == "TV" ? "TV Shows" : NormalizeMediaType(mediaType);

    public static string MediaTypeIcon(string mediaType) =>
        AppMediaPresentation.IconFor(NormalizeMediaType(mediaType));

    public static string LaneFor(string mediaType) => NormalizeMediaType(mediaType) switch
    {
        "Books" or "Comics" => "Read",
        "Movies" or "TV" => "Watch",
        "Audiobooks" or "Music" => "Listen",
        _ => "Other",
    };

    private MetadataProviderInventoryItem BuildProvider(
        ProviderCatalogueDto catalogue,
        ProviderStatusDto? status,
        PipelineConfiguration pipelines,
        HydrationSettingsDto? hydration)
    {
        var mediaTypes = catalogue.MediaTypes
            .Select(NormalizeMediaType)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(MediaOrder)
            .ToList();
        var participations = BuildParticipations(catalogue, status, pipelines, hydration, mediaTypes);
        var enabled = status?.Enabled ?? catalogue.Enabled;
        var requiresKey = status?.RequiresApiKey ?? catalogue.RequiresKey;
        var hasKey = status?.HasApiKey ?? !requiresKey;
        var logo = catalogue.IconPath;
        if (!string.IsNullOrWhiteSpace(logo) && !logo.StartsWith('/'))
        {
            logo = "/" + logo.TrimStart('/');
        }

        return new MetadataProviderInventoryItem(
            catalogue.Name,
            catalogue.DisplayName,
            DescriptionFor(catalogue, participations),
            catalogue.Category,
            catalogue.Domain,
            logo,
            _catalogueService.GetAccent(catalogue.Name, catalogue.MaterialIcon).Icon,
            catalogue.AccentColor,
            mediaTypes,
            mediaTypes.Select(LaneFor).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            catalogue.Capabilities,
            catalogue.HydrationStages,
            catalogue.SystemRole,
            catalogue.RequiredSystemProvider,
            enabled,
            requiresKey,
            hasKey,
            requiresKey && !hasKey,
            HealthLabel(status),
            HealthTone(status),
            participations,
            catalogue,
            status);
    }

    private static IReadOnlyList<MetadataProviderParticipation> BuildParticipations(
        ProviderCatalogueDto catalogue,
        ProviderStatusDto? status,
        PipelineConfiguration pipelines,
        HydrationSettingsDto? hydration,
        IReadOnlyList<string> configuredMediaTypes)
    {
        var result = new List<MetadataProviderParticipation>();
        foreach (var mediaType in MediaTypeOrder)
        {
            var ordered = pipelines.GetPipelineForMediaType(mediaType).Providers.OrderBy(entry => entry.Rank).ToList();
            for (var index = 0; index < ordered.Count; index++)
            {
                var entry = ordered[index];
                if (!string.Equals(entry.Name, catalogue.Name, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                result.Add(new MetadataProviderParticipation(
                    mediaType,
                    LaneFor(mediaType),
                    1,
                    "Identify & initial metadata",
                    RoleLabel(entry, index),
                    entry.Purpose,
                    ContributionLabels(mediaType, catalogue.Capabilities, status, entry),
                    status?.Enabled ?? catalogue.Enabled));
            }
        }

        var appliesTo = configuredMediaTypes.Count > 0 ? configuredMediaTypes : MediaTypeOrder;
        if (catalogue.HydrationStages.Contains(2))
        {
            foreach (var mediaType in appliesTo)
            {
                result.Add(new MetadataProviderParticipation(
                    mediaType,
                    LaneFor(mediaType),
                    2,
                    "Canonical identity",
                    catalogue.RequiredSystemProvider ? "Required" : "Optional",
                    catalogue.SystemRole,
                    CanonicalOutputs(catalogue.Capabilities),
                    status?.Enabled ?? catalogue.Enabled));
            }
        }

        var suppliesLaterEnrichment = catalogue.HydrationStages.Contains(3)
            || string.Equals(catalogue.Category, "Enrichment", StringComparison.OrdinalIgnoreCase)
            || (string.Equals(catalogue.SystemRole, "canonical_source", StringComparison.OrdinalIgnoreCase)
                && catalogue.Capabilities.Any(capability => capability is ProviderCapabilityId.People or ProviderCapabilityId.Relationships));
        if (suppliesLaterEnrichment)
        {
            foreach (var mediaType in appliesTo)
            {
                result.Add(new MetadataProviderParticipation(
                    mediaType,
                    LaneFor(mediaType),
                    3,
                    "Enrichment after match",
                    catalogue.RequiredSystemProvider ? "Required" : "Optional",
                    "enrichment",
                    EnrichmentOutputs(catalogue),
                    (status?.Enabled ?? catalogue.Enabled) && (hydration?.Stage3Enabled ?? true)));
            }
        }

        return result
            .GroupBy(item => (item.MediaType, item.Stage), new MediaStageComparer())
            .Select(group => group.First())
            .OrderBy(item => MediaOrder(item.MediaType))
            .ThenBy(item => item.Stage)
            .ToList();
    }

    private static MetadataMediaPipeline BuildFlow(
        string mediaType,
        PipelineConfiguration configuration,
        IReadOnlyList<MetadataProviderInventoryItem> providers)
    {
        var providerLookup = providers.ToDictionary(provider => provider.Key, StringComparer.OrdinalIgnoreCase);
        var participants = providers
            .SelectMany(provider => provider.Participations
                .Where(item => string.Equals(item.MediaType, mediaType, StringComparison.OrdinalIgnoreCase))
                .Select(item => new MetadataFlowParticipant(
                    provider.Key,
                    provider.DisplayName,
                    item.RoleLabel,
                    item.Stage,
                    item.Outputs,
                    item.Enabled,
                    provider.NeedsSetup,
                    provider.LogoUrl,
                    provider.Icon,
                    provider.AccentColor,
                    provider.HealthLabel,
                    provider.HealthTone)))
            .ToList();

        var stageOneOrder = configuration.GetPipelineForMediaType(mediaType).Providers
            .OrderBy(entry => entry.Rank)
            .Select(entry => entry.Name)
            .ToList();
        var stageOne = stageOneOrder
            .Where(key => providerLookup.ContainsKey(key))
            .Select(key => participants.First(item => item.Stage == 1 && string.Equals(item.ProviderKey, key, StringComparison.OrdinalIgnoreCase)))
            .ToList();
        var canonical = participants.Where(item => item.Stage == 2).OrderBy(item => item.DisplayName).ToList();
        var enrichment = participants.Where(item => item.Stage == 3).OrderBy(item => item.DisplayName).ToList();
        var results = participants
            .Where(item => item.Enabled && !item.NeedsSetup)
            .SelectMany(item => item.Outputs)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new MetadataMediaPipeline(
            mediaType,
            MediaTypeLabel(mediaType),
            LaneFor(mediaType),
            MediaTypeIcon(mediaType),
            configuration.GetPipelineForMediaType(mediaType).Strategy.ToString(),
            stageOne,
            canonical,
            enrichment,
            results);
    }

    private static bool IsUserVisibleProvider(ProviderCatalogueDto provider) =>
        provider.Capabilities.Count > 0
        || provider.HydrationStages.Count > 0
        || provider.RequiredSystemProvider
        || !string.IsNullOrWhiteSpace(provider.SystemRole);

    private static IReadOnlyList<string> ContributionLabels(
        string mediaType,
        IReadOnlyList<string> capabilities,
        ProviderStatusDto? status,
        PipelineProviderEntry entry)
    {
        var contributions = new List<string>();
        if (string.Equals(entry.Purpose, "identity", StringComparison.OrdinalIgnoreCase) || entry.UseAsIdentityFallback)
        {
            contributions.Add("Identity");
        }

        if (capabilities.Contains(ProviderCapabilityId.Metadata, StringComparer.OrdinalIgnoreCase))
        {
            contributions.Add(NormalizeMediaType(mediaType) switch { "Comics" => "Issue details", "Music" => "Album details", "TV" => "Episode details", _ => "Metadata" });
        }

        if (capabilities.Contains(ProviderCapabilityId.Artwork, StringComparer.OrdinalIgnoreCase))
        {
            contributions.Add(NormalizeMediaType(mediaType) is "Movies" or "TV" ? "Poster" : "Cover");
            if (NormalizeMediaType(mediaType) is "Movies" or "TV")
            {
                contributions.Add("Background");
            }
        }
        if (capabilities.Contains(ProviderCapabilityId.Ratings, StringComparer.OrdinalIgnoreCase))
        {
            contributions.Add("Ratings");
        }

        if (capabilities.Contains(ProviderCapabilityId.People, StringComparer.OrdinalIgnoreCase))
        {
            contributions.Add("People seeds");
        }

        if (contributions.Count == 0 && status?.AvailableFields is { Count: > 0 })
        {
            contributions.Add("Metadata");
        }

        return contributions.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static IReadOnlyList<string> CanonicalOutputs(IReadOnlyList<string> capabilities)
    {
        var outputs = new List<string> { "Canonical identity" };
        if (capabilities.Contains(ProviderCapabilityId.Relationships, StringComparer.OrdinalIgnoreCase))
        {
            outputs.Add("Relationships");
        }

        return outputs;
    }

    private static IReadOnlyList<string> EnrichmentOutputs(ProviderCatalogueDto provider)
    {
        var outputs = new List<string>();
        foreach (var capability in provider.Capabilities)
        {
            switch (capability.ToLowerInvariant())
            {
                case ProviderCapabilityId.Artwork:
                    if (string.Equals(provider.Category, "Image", StringComparison.OrdinalIgnoreCase))
                    {
                        outputs.AddRange(["Backgrounds", "Logos"]);
                    }
                    else
                    {
                        outputs.Add("Artwork");
                    }

                    break;
                case ProviderCapabilityId.Lyrics:
                    outputs.AddRange(["Lyrics", "Synchronized lyrics"]);
                    break;
                case ProviderCapabilityId.Subtitles:
                    outputs.AddRange(["Subtitles", "Text tracks"]);
                    break;
                case ProviderCapabilityId.People:
                    outputs.AddRange(["People details", "Portraits"]);
                    break;
                case ProviderCapabilityId.Relationships:
                    outputs.Add("Relationships");
                    break;
            }
        }
        return outputs.Count == 0 ? ["Additional metadata"] : outputs.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static string RoleLabel(PipelineProviderEntry entry, int index)
    {
        if (entry.UseAsIdentityFallback)
        {
            return "Fallback";
        }

        if (string.Equals(entry.Purpose, "enrichment", StringComparison.OrdinalIgnoreCase))
        {
            return "Optional";
        }

        if (index == 0 && string.Equals(entry.Purpose, "identity", StringComparison.OrdinalIgnoreCase))
        {
            return "Primary";
        }

        if (string.Equals(entry.Purpose, "identity", StringComparison.OrdinalIgnoreCase))
        {
            return "Secondary";
        }

        return "Participant";
    }

    private static string DescriptionFor(ProviderCatalogueDto provider, IReadOnlyList<MetadataProviderParticipation> participations)
    {
        if (provider.RequiredSystemProvider)
        {
            return "Required canonical identity and relationship provider.";
        }

        var stages = participations.Select(item => item.Stage).Distinct().Order().ToList();
        if (stages.SequenceEqual([1]))
        {
            return "Identifies media and supplies initial metadata during ingest.";
        }

        if (stages.Contains(3) && !stages.Contains(1))
        {
            return "Adds optional information after media has been identified.";
        }

        return "Contributes metadata and enrichment across the ingestion flow.";
    }

    public static string HealthLabel(ProviderStatusDto? status)
    {
        if (status is null)
        {
            return "Not checked";
        }

        if (!status.Enabled)
        {
            return "Disabled";
        }

        if (status.RequiresApiKey && !status.HasApiKey)
        {
            return "Needs setup";
        }

        if (string.Equals(status.HealthStatus, "Down", StringComparison.OrdinalIgnoreCase))
        {
            return "Unavailable";
        }

        if (string.Equals(status.HealthStatus, "Degraded", StringComparison.OrdinalIgnoreCase))
        {
            return "Degraded";
        }

        if (DateTimeOffset.TryParse(status.LastSuccessAt, out var lastSuccess)
            && DateTimeOffset.TryParse(status.ConnectionCheckedAt, out var lastConnectionCheck)
            && lastSuccess > lastConnectionCheck)
        {
            return "Connected";
        }

        if (status.ConnectionStatus is "valid")
        {
            return "Connected";
        }

        if (status.ConnectionStatus is "local_ready")
        {
            return "Ready locally";
        }

        if (!string.IsNullOrWhiteSpace(status.ConnectionStatus)
            && status.ConnectionStatus is not "valid" and not "local_ready")
        {
            return "Needs attention";
        }

        return status.LastSuccessAt is not null ? "Connected" : "Not checked";
    }

    public static AppUiTone HealthTone(ProviderStatusDto? status) => HealthLabel(status) switch
    {
        "Connected" or "Ready locally" => AppUiTone.Success,
        "Degraded" or "Needs setup" or "Needs attention" => AppUiTone.Warning,
        "Unavailable" => AppUiTone.Error,
        _ => AppUiTone.Neutral,
    };

    private static int MediaOrder(string mediaType)
    {
        var index = Array.FindIndex(MediaTypeOrder, value => string.Equals(value, mediaType, StringComparison.OrdinalIgnoreCase));
        return index >= 0 ? index : int.MaxValue;
    }

    private sealed class MediaStageComparer : IEqualityComparer<(string MediaType, int Stage)>
    {
        public bool Equals((string MediaType, int Stage) x, (string MediaType, int Stage) y) =>
            x.Stage == y.Stage && string.Equals(x.MediaType, y.MediaType, StringComparison.OrdinalIgnoreCase);

        public int GetHashCode((string MediaType, int Stage) obj) =>
            HashCode.Combine(StringComparer.OrdinalIgnoreCase.GetHashCode(obj.MediaType), obj.Stage);
    }
}

public sealed record MetadataSettingsSnapshot(
    IReadOnlyList<MetadataProviderInventoryItem> Providers,
    IReadOnlyList<MetadataMediaPipeline> MediaPipelines,
    IReadOnlyDictionary<string, ProviderStatusDto> Statuses,
    HydrationSettingsDto? Hydration)
{
    public MetadataProviderInventoryItem? ProviderFor(string? key) => Providers.FirstOrDefault(provider =>
        string.Equals(provider.Key, key, StringComparison.OrdinalIgnoreCase));

    public MetadataMediaPipeline? PipelineFor(string? mediaType) => MediaPipelines.FirstOrDefault(pipeline =>
        string.Equals(pipeline.MediaType, MetadataSettingsStateService.NormalizeMediaType(mediaType), StringComparison.OrdinalIgnoreCase));
}

public sealed record MetadataProviderInventoryItem(
    string Key,
    string DisplayName,
    string Description,
    string Category,
    string Domain,
    string? LogoUrl,
    string Icon,
    string AccentColor,
    IReadOnlyList<string> MediaTypes,
    IReadOnlyList<string> Lanes,
    IReadOnlyList<string> Capabilities,
    IReadOnlyList<int> HydrationStages,
    string? SystemRole,
    bool RequiredSystemProvider,
    bool Enabled,
    bool RequiresKey,
    bool HasKey,
    bool NeedsSetup,
    string HealthLabel,
    AppUiTone HealthTone,
    IReadOnlyList<MetadataProviderParticipation> Participations,
    ProviderCatalogueDto Catalogue,
    ProviderStatusDto? Status);

public sealed record MetadataProviderParticipation(
    string MediaType,
    string Lane,
    int Stage,
    string StageLabel,
    string RoleLabel,
    string? Purpose,
    IReadOnlyList<string> Outputs,
    bool Enabled);

public sealed record MetadataMediaPipeline(
    string MediaType,
    string Label,
    string Lane,
    string Icon,
    string Strategy,
    IReadOnlyList<MetadataFlowParticipant> Identification,
    IReadOnlyList<MetadataFlowParticipant> CanonicalIdentity,
    IReadOnlyList<MetadataFlowParticipant> Enrichment,
    IReadOnlyList<string> Results);

public sealed record MetadataFlowParticipant(
    string ProviderKey,
    string DisplayName,
    string RoleLabel,
    int Stage,
    IReadOnlyList<string> Outputs,
    bool Enabled,
    bool NeedsSetup,
    string? LogoUrl,
    string Icon,
    string AccentColor,
    string HealthLabel,
    AppUiTone HealthTone);
