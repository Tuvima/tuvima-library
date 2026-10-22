using MediaEngine.Domain;
using MediaEngine.Domain.Constants;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Entities;
using MediaEngine.Domain.Models;
using MediaEngine.Providers.Services;
using Microsoft.Extensions.Logging;

namespace MediaEngine.Providers.Workers;

/// <summary>
/// Resolves narrative root, extracts fictional-entity QIDs from canonicals,
/// and dispatches to <see cref="IRecursiveFictionalEntityService"/> for enrichment.
///
/// Extracted from <c>HydrationPipelineService.RunFictionalEntityEnrichmentAsync</c>.
/// </summary>
public sealed class FictionalEntityWorker
{
    private readonly INarrativeRootResolver _narrativeRootResolver;
    private readonly IRecursiveFictionalEntityService _fictionalEntityService;
    private readonly ICanonicalValueRepository _canonicalRepo;
    private readonly ICanonicalValueArrayRepository _canonicalArrayRepo;
    private readonly IWorkRepository _workRepo;
    private readonly ILogger<FictionalEntityWorker> _logger;
    private readonly IExternalLookupGuard? _lookupGuard;

    public FictionalEntityWorker(
        INarrativeRootResolver narrativeRootResolver,
        IRecursiveFictionalEntityService fictionalEntityService,
        ICanonicalValueRepository canonicalRepo,
        ICanonicalValueArrayRepository canonicalArrayRepo,
        IWorkRepository workRepo,
        ILogger<FictionalEntityWorker> logger,
        IExternalLookupGuard? lookupGuard = null)
    {
        _lookupGuard = lookupGuard;
        _narrativeRootResolver = narrativeRootResolver;
        _fictionalEntityService = fictionalEntityService;
        _canonicalRepo = canonicalRepo;
        _canonicalArrayRepo = canonicalArrayRepo;
        _workRepo = workRepo;
        _logger = logger;
    }

    /// <summary>
    /// Resolves the narrative root for the entity, extracts fictional entity
    /// references (characters, locations, events, and objects) from canonicals,
    /// and enriches them.
    /// </summary>
    public async Task EnrichAsync(Guid entityId, string workQid, CancellationToken ct)
    {
        if (await _lookupGuard.RefusesAsync(entityId, _logger, "Fictional entity lookup", ct).ConfigureAwait(false))
        {
            return;
        }

        var canonicalEntityId = await ResolveCanonicalEntityIdAsync(entityId, ct);
        var canonicals = await _canonicalRepo.GetByEntityAsync(canonicalEntityId, ct);
        var canonicalArrays = await _canonicalArrayRepo.GetAllByEntityAsync(canonicalEntityId, ct);

        // 1. Resolve narrative root
        var narrativeRoot = await _narrativeRootResolver.ResolveAsync(canonicalEntityId, ct);

        if (narrativeRoot is null)
        {
            _logger.LogDebug(
                "No narrative root resolved for entity {Id} (QID={Qid})",
                entityId, workQid);
            return;
        }

        // 2. Extract fictional entity references from canonicals
        var entityRefs = ExtractFictionalEntityReferences(canonicals, canonicalArrays);
        if (entityRefs.Count == 0)
        {
            _logger.LogDebug("No fictional entity references found for entity {Id}", entityId);
            return;
        }

        // 3. Enrich
        var workLabel = canonicals
            .FirstOrDefault(c => string.Equals(c.Key, MetadataFieldConstants.Title,
                StringComparison.OrdinalIgnoreCase))?.Value;

        _logger.LogInformation(
            "Fictional entity enrichment: {Count} entities for work '{Title}' (QID={Qid}) " +
            "in universe '{Universe}'",
            entityRefs.Count, workLabel ?? "(unknown)", workQid,
            narrativeRoot.Label ?? "(unknown)");

        await _fictionalEntityService.EnrichAsync(
            workQid, workLabel,
            narrativeRoot.Qid, narrativeRoot.Label,
            entityRefs, ct);
    }

    private async Task<Guid> ResolveCanonicalEntityIdAsync(Guid entityId, CancellationToken ct)
    {
        var lineage = await _workRepo.GetLineageByAssetAsync(entityId, ct);
        return lineage?.TargetForParentScope ?? entityId;
    }

    /// <summary>
    /// Extracts typed fictional-entity QIDs from canonical values.
    /// </summary>
    private static IReadOnlyList<FictionalEntityReference> ExtractFictionalEntityReferences(
        IReadOnlyList<CanonicalValue> canonicals,
        IReadOnlyDictionary<string, IReadOnlyList<CanonicalArrayEntry>> canonicalArrays)
    {
        var refs = new List<FictionalEntityReference>();
        AddArrayReferences(
            refs,
            canonicalArrays,
            MetadataFieldConstants.Characters,
            "Character");
        AddArrayReferences(
            refs,
            canonicalArrays,
            MetadataFieldConstants.NarrativeLocation,
            "Location");
        AddArrayReferences(refs, canonicalArrays, MetadataFieldConstants.NarrativeEvent, FictionalEntityType.Event);
        AddArrayReferences(refs, canonicalArrays, MetadataFieldConstants.NarrativeObject, FictionalEntityType.Object);

        var entityKeys = new Dictionary<string, (string LabelKey, string EntitySubType)>(
            StringComparer.OrdinalIgnoreCase)
        {
            ["characters_qid"] = ("characters", "Character"),
            ["narrative_location_qid"] = ("narrative_location", "Location"),
            [$"{MetadataFieldConstants.NarrativeEvent}_qid"] = (MetadataFieldConstants.NarrativeEvent, FictionalEntityType.Event),
            [$"{MetadataFieldConstants.NarrativeObject}_qid"] = (MetadataFieldConstants.NarrativeObject, FictionalEntityType.Object),
        };

        foreach (var (qidKey, (labelKey, entitySubType)) in entityKeys)
        {
            var qidValue = canonicals
                .FirstOrDefault(c => string.Equals(c.Key, qidKey, StringComparison.OrdinalIgnoreCase))
                ?.Value;

            if (string.IsNullOrWhiteSpace(qidValue))
            {
                continue;
            }

            var qidParts = qidValue.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var labelParts = canonicals
                .FirstOrDefault(c => string.Equals(c.Key, labelKey, StringComparison.OrdinalIgnoreCase))
                ?.Value
                ?.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) ?? [];

            for (var i = 0; i < qidParts.Length; i++)
            {
                var qidPart = qidParts[i].Trim();
                var segments = qidPart.Split("::", 2);
                var qid = segments[0];

                if (!qid.StartsWith('Q'))
                {
                    continue;
                }

                var label = segments.Length > 1 && !string.IsNullOrWhiteSpace(segments[1])
                    ? segments[1].Trim()
                    : (i < labelParts.Length ? labelParts[i].Trim() : null) ?? qid;

                refs.Add(new FictionalEntityReference(qid, label, entitySubType));
            }
        }

        return refs;
    }

    private static void AddArrayReferences(
        List<FictionalEntityReference> refs,
        IReadOnlyDictionary<string, IReadOnlyList<CanonicalArrayEntry>> canonicalArrays,
        string key,
        string entitySubType)
    {
        if (!canonicalArrays.TryGetValue(key, out var entries))
        {
            return;
        }

        foreach (var entry in entries)
        {
            var qid = entry.ValueQid;
            if (string.IsNullOrWhiteSpace(qid) || !qid.StartsWith('Q'))
            {
                continue;
            }

            refs.Add(new FictionalEntityReference(qid, entry.Value, entitySubType));
        }
    }
}
