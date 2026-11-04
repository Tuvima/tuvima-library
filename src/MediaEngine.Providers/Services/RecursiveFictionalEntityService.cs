using MediaEngine.Domain.Constants;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Entities;
using MediaEngine.Domain.Enums;
using MediaEngine.Domain.Models;
using Microsoft.Extensions.Logging;

namespace MediaEngine.Providers.Services;

/// <summary>
/// Discovers and enriches fictional entities (characters, locations, organizations)
/// extracted from a work's Wikidata claims. Mirrors the pattern of
/// <see cref="RecursiveIdentityService"/> for persons.
///
/// <para>
/// For each <see cref="FictionalEntityReference"/>:
/// <list type="number">
/// <item>Find-or-create a <see cref="FictionalEntity"/> by QID.</item>
/// <item>Link the entity to the source work.</item>
/// <item>Set the <c>FictionalUniverseQid</c> from the narrative root.</item>
/// <item>If not yet enriched, enqueue a <see cref="HarvestRequest"/>
///       with the appropriate <see cref="EntityType"/>.</item>
/// </list>
/// </para>
///
/// <para>
/// <b>Query efficiency:</b> Entities that were already enriched by a previous work's
/// hydration are linked instantly (zero SPARQL calls). Only new entities trigger
/// enrichment requests — this is Layer 1 of the three-layer deduplication strategy.
/// </para>
/// </summary>
public sealed class RecursiveFictionalEntityService : IRecursiveFictionalEntityService
{
    private readonly IFictionalEntityRepository _entityRepo;
    private readonly IMetadataHarvestingService _harvesting;
    private readonly ILogger<RecursiveFictionalEntityService> _logger;

    public RecursiveFictionalEntityService(
        IFictionalEntityRepository entityRepo,
        IMetadataHarvestingService harvesting,
        ILogger<RecursiveFictionalEntityService> logger)
    {
        ArgumentNullException.ThrowIfNull(entityRepo);
        ArgumentNullException.ThrowIfNull(harvesting);
        ArgumentNullException.ThrowIfNull(logger);
        _entityRepo = entityRepo;
        _harvesting = harvesting;
        _logger = logger;
    }

    /// <inheritdoc/>
    public async Task EnrichAsync(
        string workQid,
        string? workLabel,
        string narrativeRootQid,
        string? narrativeRootLabel,
        IReadOnlyList<FictionalEntityReference> references,
        CancellationToken ct = default)
    {
        if (references.Count == 0)
        {
            return;
        }

        _logger.LogInformation(
            "Processing {Count} fictional entity references for work {WorkQid} in universe {UniverseQid}",
            references.Count, workQid, narrativeRootQid);

        var failures = new List<Exception>();
        foreach (var reference in references
                     .DistinctBy(reference => reference.WikidataQid, StringComparer.OrdinalIgnoreCase)
                     .Take(100))
        {
            ct.ThrowIfCancellationRequested();

            if (string.IsNullOrWhiteSpace(reference.WikidataQid))
            {
                continue;
            }

            try
            {
                await ProcessEntityAsync(
                    workQid, workLabel, narrativeRootQid, narrativeRootLabel,
                    reference, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Failed to process fictional entity '{Label}' ({Qid}) for work {WorkQid}",
                    reference.Label, reference.WikidataQid, workQid);
                failures.Add(ex);
            }
        }

        if (failures.Count > 0)
        {
            throw new AggregateException(
                $"{failures.Count} fictional-entity enrichment operation(s) did not settle.",
                failures);
        }
    }

    // ── Private Helpers ──────────────────────────────────────────────────────

    private async Task ProcessEntityAsync(
        string workQid,
        string? workLabel,
        string narrativeRootQid,
        string? narrativeRootLabel,
        FictionalEntityReference reference,
        CancellationToken ct)
    {
        // 1. Find-or-create by QID.
        var entity = await _entityRepo.FindByQidAsync(reference.WikidataQid, ct)
            .ConfigureAwait(false);

        if (entity is null)
        {
            entity = new FictionalEntity
            {
                Id = Guid.NewGuid(),
                WikidataQid = reference.WikidataQid,
                Label = reference.Label ?? reference.WikidataQid,
                EntitySubType = reference.EntitySubType,
                FictionalUniverseQid = narrativeRootQid,
                FictionalUniverseLabel = narrativeRootLabel,
                CreatedAt = DateTimeOffset.UtcNow,
            };

            await _entityRepo.CreateAsync(entity, ct).ConfigureAwait(false);

            _logger.LogDebug(
                "Created fictional entity '{Label}' ({Qid}, {Type}), id={Id}",
                entity.Label, entity.WikidataQid, entity.EntitySubType, entity.Id);
        }

        // 2. Link entity to the source work (idempotent — INSERT OR IGNORE).
        await _entityRepo.LinkToWorkAsync(new FictionalEntityWorkLink(
            entity.Id,
            workQid,
            workLabel,
            "appears_in"), ct)
            .ConfigureAwait(false);

        // 3. If not yet enriched, enqueue a Wikidata harvest request.
        //    This is the skip-if-enriched gate (Layer 1 query efficiency).
        if (entity.EnrichedAt is null)
        {
            var entityType = reference.EntitySubType switch
            {
                FictionalEntityType.Character => EntityType.Character,
                FictionalEntityType.Location => EntityType.Location,
                FictionalEntityType.Organization => EntityType.Organization,
                FictionalEntityType.Event => EntityType.Event,
                FictionalEntityType.Object => EntityType.Object,
                FictionalEntityType.Species => EntityType.Species,
                _ => EntityType.Character,
            };

            // First-level owned-work entities are core ingestion evidence. Complete
            // them in the caller's durable identity operation so a process restart
            // cannot leave an in-memory queue item masquerading as settled work.
            await _harvesting.ProcessSynchronousAsync(new HarvestRequest
            {
                EntityId = entity.Id,
                EntityType = entityType,
                MediaType = MediaType.Unknown,
                Hints = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["wikidata_qid"] = reference.WikidataQid,
                    ["label"] = reference.Label ?? reference.WikidataQid,
                    ["entity_sub_type"] = reference.EntitySubType,
                    ["universe_qid"] = narrativeRootQid,
                },
            }, ct).ConfigureAwait(false);

            _logger.LogDebug(
                "Enqueued Wikidata enrichment for fictional entity '{Label}' ({Qid})",
                entity.Label, entity.WikidataQid);
        }
        else
        {
            _logger.LogDebug(
                "Fictional entity '{Label}' ({Qid}) already enriched — linked to work only",
                entity.Label, entity.WikidataQid);
        }
    }
}
