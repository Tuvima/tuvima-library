using MediaEngine.Contracts.Settings;
using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Contracts;
using MediaTypePipeline = MediaEngine.Domain.Configuration.MediaTypePipeline;
using PipelineProviderEntry = MediaEngine.Domain.Configuration.PipelineProviderEntry;

namespace MediaEngine.Api.Services.Settings;

/// <summary>
/// Applies an edited ingestion flow (order, role and Off per media type) to <c>config/pipelines.json</c>. Only the provider
/// list changes: strategy, scoring and per-field priorities are left exactly as they are. A change affects new ingestion
/// only; nothing here looks at items that already hold a match.
/// </summary>
public sealed class PipelineFlowEditor(IConfigurationLoader loader)
{
    /// <summary>Saves the edit, or returns the plain-English problems and saves nothing.</summary>
    public PipelineFlowSaveResponse Save(PipelineFlowSaveRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var problems = new List<PipelineFlowProblemDto>();
        var current = loader.LoadPipelines();
        var providers = loader.LoadAllProviders();

        foreach (var (mediaType, rows) in request.MediaTypes)
        {
            if (!current.Pipelines.TryGetValue(mediaType, out var pipeline))
            {
                problems.Add(Problem(mediaType, $"{mediaType} is not a media type with an ingestion flow."));
                continue;
            }

            var problem = Apply(mediaType, pipeline, rows ?? [], providers);
            if (problem is not null)
            {
                problems.Add(problem);
            }
        }

        if (problems.Count > 0)
        {
            return new PipelineFlowSaveResponse { Saved = false, Problems = problems };
        }

        loader.SavePipelines(current);
        return new PipelineFlowSaveResponse { Saved = true };
    }

    /// <summary>The installed providers that can be added to a flow, with where each is used now.</summary>
    public PipelineFlowOptionsResponse Options()
    {
        var pipelines = loader.LoadPipelines().Pipelines;
        var response = new PipelineFlowOptionsResponse();
        foreach (var provider in loader.LoadAllProviders().Where(IsFlowProvider).OrderBy(p => p.DisplayName ?? p.Name, StringComparer.OrdinalIgnoreCase))
        {
            var handles = pipelines.Keys.Where(media => Handles(provider, media)).OrderBy(media => media, StringComparer.OrdinalIgnoreCase).ToList();
            response.Providers.Add(new PipelineFlowOptionDto
            {
                Name = provider.Name,
                DisplayName = provider.DisplayName ?? provider.Name,
                PluginId = provider.PluginId,
                Enabled = provider.Enabled,
                MediaTypes = handles,
                UsedIn = pipelines
                    .Where(pair => pair.Value.Providers.Any(entry => Same(entry.Name, provider.Name)))
                    .Select(pair => pair.Key)
                    .OrderBy(media => media, StringComparer.OrdinalIgnoreCase)
                    .ToList(),
            });
        }

        return response;
    }

    private static PipelineFlowProblemDto? Apply(string mediaType, MediaTypePipeline pipeline, List<PipelineFlowProviderDto> rows, IReadOnlyList<ProviderConfiguration> providers)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var entries = new List<PipelineProviderEntry>();
        foreach (var row in rows)
        {
            var provider = providers.FirstOrDefault(p => Same(p.Name, row.Name));
            if (provider is null || !IsFlowProvider(provider))
            {
                return Problem(mediaType, $"{row.Name} is not an installed provider that can identify media.");
            }

            if (!Handles(provider, mediaType))
            {
                return Problem(mediaType, $"{provider.DisplayName ?? provider.Name} does not handle {mediaType}.");
            }

            if (!seen.Add(provider.Name))
            {
                return Problem(mediaType, $"{provider.DisplayName ?? provider.Name} is listed twice in {mediaType}.");
            }

            var role = (row.Role ?? string.Empty).Trim().ToLowerInvariant();
            if (role is not (PipelineFlowRoles.Primary or PipelineFlowRoles.Fallback or PipelineFlowRoles.Enrichment))
            {
                return Problem(mediaType, $"{provider.DisplayName ?? provider.Name} has an unknown role.");
            }

            var existing = pipeline.Providers.FirstOrDefault(entry => Same(entry.Name, provider.Name));
            entries.Add(Build(provider.Name, role, existing, entries.Count + 1));
        }

        if (!entries.Any(IsIdentity))
        {
            return Problem(mediaType, $"{mediaType} needs at least one provider that identifies it. Keep one Primary or Fallback provider.");
        }

        pipeline.Providers = entries;
        return null;
    }

    private static PipelineProviderEntry Build(string name, string role, PipelineProviderEntry? existing, int rank)
    {
        if (existing is not null && RoleOf(existing) == role)
        {
            existing.Rank = rank;
            return existing;
        }

        // A role change starts from a clean entry so no leftover follow-up or condition from the old role survives.
        return new PipelineProviderEntry
        {
            Rank = rank,
            Name = name,
            // The data rules allow the identity-fallback flag on enrichment providers only, as the shipped Music flow does.
            Purpose = role == PipelineFlowRoles.Primary ? "identity" : "enrichment",
            UseAsIdentityFallback = role == PipelineFlowRoles.Fallback,
            RequiresIdentity = role != PipelineFlowRoles.Primary,
        };
    }

    /// <summary>The role an existing flow entry plays, as the flow editor names it.</summary>
    public static string RoleOf(PipelineProviderEntry entry)
    {
        if (entry.UseAsIdentityFallback)
        {
            return PipelineFlowRoles.Fallback;
        }

        return string.Equals(entry.Purpose, "enrichment", StringComparison.OrdinalIgnoreCase)
            ? PipelineFlowRoles.Enrichment
            : PipelineFlowRoles.Primary;
    }

    private static bool IsIdentity(PipelineProviderEntry entry) =>
        RoleOf(entry) is PipelineFlowRoles.Primary or PipelineFlowRoles.Fallback;

    private static bool IsFlowProvider(ProviderConfiguration provider) =>
        string.Equals(provider.AdapterType, "config_driven", StringComparison.OrdinalIgnoreCase);

    private static bool Handles(ProviderConfiguration provider, string mediaType) =>
        provider.CanHandle?.MediaTypes is { Count: > 0 } media
        && media.Any(m => Same(m, mediaType));

    private static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private static PipelineFlowProblemDto Problem(string mediaType, string message) => new() { MediaType = mediaType, Message = message };
}
