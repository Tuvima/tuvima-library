using MediaEngine.Domain.Constants;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Entities;
using MediaEngine.Domain.Enums;
using MediaEngine.Domain.Models;
using MediaEngine.Providers.Models;
using Microsoft.Extensions.Logging;

namespace MediaEngine.Providers.Services;

/// <summary>
/// Reads enriched claims from a fictional entity and creates graph edges in
/// the <see cref="IEntityRelationshipRepository"/>.
///
/// <para>
/// Called after a Character/Location/Organization/Event/Object is enriched by Wikidata.
/// Statement-level evidence is preferred; normalized <c>_qid</c> claims are used only
/// when a provider does not expose structured statements.
/// <list type="number">
/// <item>Find-or-create the target <see cref="FictionalEntity"/> by QID.</item>
/// <item>Insert a graph edge (idempotent via UNIQUE constraint).</item>
/// </list>
/// </para>
///
/// <para>
/// <b>Depth limit: 1</b> — target entities are created but NOT recursively enriched
/// for their own relationships. This prevents unbounded graph traversal.
/// </para>
/// </summary>
public interface IRelationshipPopulationService
{
    /// <summary>
    /// Populate relationship edges from the structured evidence of an enriched entity.
    /// </summary>
    /// <param name="entityQid">The Wikidata QID of the enriched entity.</param>
    /// <param name="canonicalValues">Canonical values keyed by claim key.</param>
    /// <param name="universeQid">The narrative root QID for this entity's universe.</param>
    /// <param name="universeLabel">Human-readable universe label.</param>
    /// <param name="contextWorkQid">Optional work QID providing context for performer links.</param>
    /// <param name="temporalQualifiers">Optional per-target-QID temporal qualifiers (start/end time).</param>
    /// <param name="statementEvidence">Optional statement-level provider evidence, including qualifiers.</param>
    /// <param name="currentDepth">Current hop depth (0 = first-level relationships from the source entity).</param>
    /// <param name="maxDepth">Maximum enrichment depth. Target entities at depth &lt; maxDepth are enqueued for enrichment.</param>
    /// <param name="ct">Cancellation token.</param>
    Task PopulateAsync(
        string entityQid,
        IReadOnlyDictionary<string, string> canonicalValues,
        string universeQid,
        string? universeLabel,
        string? contextWorkQid = null,
        IReadOnlyDictionary<string, (string? StartTime, string? EndTime)>? temporalQualifiers = null,
        IReadOnlyList<FictionalEntityRelationshipStatement>? statementEvidence = null,
        int currentDepth = 0,
        int maxDepth = 1,
        CancellationToken ct = default);
}

/// <summary>
/// Mapping between claim keys and their relationship type + expected target entity type.
/// </summary>
internal static class RelationshipClaimMap
{
    /// <summary>
    /// Maps canonical value claim keys (that end in <c>_qid</c>) to relationship types.
    /// Only claims listed here produce graph edges.
    /// </summary>
    internal static readonly IReadOnlyDictionary<string, (string RelType, string TargetEntityType)> Map =
        new Dictionary<string, (string, string)>(StringComparer.OrdinalIgnoreCase)
        {
            // Character → Character relationships
            ["father_qid"] = (RelationshipType.Father, FictionalEntityType.Character),
            ["mother_qid"] = (RelationshipType.Mother, FictionalEntityType.Character),
            ["spouse_qid"] = (RelationshipType.Spouse, FictionalEntityType.Character),
            ["sibling_qid"] = (RelationshipType.Sibling, FictionalEntityType.Character),
            ["child_qid"] = (RelationshipType.Child, FictionalEntityType.Character),
            ["opponent_qid"] = (RelationshipType.Opponent, FictionalEntityType.Character),
            ["student_of_qid"] = (RelationshipType.StudentOf, FictionalEntityType.Character),

            // Character → Character (romantic)
            ["partner_qid"] = (RelationshipType.Partner, FictionalEntityType.Character),

            // Character → Organization
            ["member_of_qid"] = (RelationshipType.MemberOf, FictionalEntityType.Organization),

            // Character → Organization (allegiance)
            ["allegiance_qid"] = (RelationshipType.Allegiance, FictionalEntityType.Organization),

            // Character → Location
            ["residence_qid"] = (RelationshipType.Residence, FictionalEntityType.Location),

            // Character → Organization/Location (education)
            ["educated_at_qid"] = (RelationshipType.EducatedAt, FictionalEntityType.Organization),

            // Character/Location/Org → Person (creator)
            // P170 points to a real creator. The relationship edge is retained,
            // but the target must never be materialized as a fictional Character.
            ["creator_qid"] = (RelationshipType.Creator, "Person"),

            // Location → Location
            ["located_in_qid"] = (RelationshipType.LocatedIn, FictionalEntityType.Location),
            ["part_of_qid"] = (RelationshipType.PartOf, FictionalEntityType.Location),

            // Organization → Character (P169 is specifically chief executive officer).
            ["chief_executive_officer_qid"] = (RelationshipType.ChiefExecutiveOfficer, FictionalEntityType.Character),

            // Organization → Organization
            ["parent_organization_qid"] = (RelationshipType.ParentOrganization, FictionalEntityType.Organization),
            ["has_parts_qid"] = (RelationshipType.HasParts, FictionalEntityType.Organization),

            // Character/Location/Org → Entity (position)
            ["position_held_qid"] = (RelationshipType.PositionHeld, FictionalEntityType.Character),

            // Character/Org → Event (conflict)
            ["conflict_qid"] = (RelationshipType.Conflict, FictionalEntityType.Event),

            // Character → Character (social web)
            ["significant_person_qid"] = (RelationshipType.SignificantPerson, FictionalEntityType.Character),

            // Character → Organization (affiliation)
            ["affiliation_qid"] = (RelationshipType.Affiliation, FictionalEntityType.Organization),

            // Event → location/participants/events.
            ["event_location_qid"] = (RelationshipType.OccursAt, FictionalEntityType.Location),
            ["participant_qid"] = (RelationshipType.Participant, FictionalEntityType.Character),
            ["cause_qid"] = (RelationshipType.CausedBy, FictionalEntityType.Event),
            ["effect_qid"] = (RelationshipType.Causes, FictionalEntityType.Event),
        };
}

/// <inheritdoc cref="IRelationshipPopulationService"/>
public sealed class RelationshipPopulationService : IRelationshipPopulationService
{
    private readonly IEntityRelationshipRepository _relRepo;
    private readonly IFictionalEntityRepository _entityRepo;
    private readonly IMetadataHarvestQueueAdmission _harvestQueue;
    private readonly IUniverseGraphQueryService _graphQuery;
    private readonly ILogger<RelationshipPopulationService> _logger;

    public RelationshipPopulationService(
        IEntityRelationshipRepository relRepo,
        IFictionalEntityRepository entityRepo,
        IMetadataHarvestQueueAdmission harvestQueue,
        IUniverseGraphQueryService graphQuery,
        ILogger<RelationshipPopulationService> logger)
    {
        ArgumentNullException.ThrowIfNull(relRepo);
        ArgumentNullException.ThrowIfNull(entityRepo);
        ArgumentNullException.ThrowIfNull(harvestQueue);
        ArgumentNullException.ThrowIfNull(graphQuery);
        ArgumentNullException.ThrowIfNull(logger);
        _relRepo = relRepo;
        _entityRepo = entityRepo;
        _harvestQueue = harvestQueue;
        _graphQuery = graphQuery;
        _logger = logger;
    }

    /// <inheritdoc/>
    public async Task PopulateAsync(
        string entityQid,
        IReadOnlyDictionary<string, string> canonicalValues,
        string universeQid,
        string? universeLabel,
        string? contextWorkQid = null,
        IReadOnlyDictionary<string, (string? StartTime, string? EndTime)>? temporalQualifiers = null,
        IReadOnlyList<FictionalEntityRelationshipStatement>? statementEvidence = null,
        int currentDepth = 0,
        int maxDepth = 1,
        CancellationToken ct = default)
    {
        var edgesCreated = 0;
        var sourceEntity = await _entityRepo.FindByQidAsync(entityQid, ct).ConfigureAwait(false);
        var hasStructuredEvidence = statementEvidence is not null;
        var statementsByClaimKey = (statementEvidence ?? [])
            .Where(statement => !string.IsNullOrWhiteSpace(statement.ClaimKey)
                                && !string.IsNullOrWhiteSpace(statement.TargetQid))
            .GroupBy(statement => statement.ClaimKey, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => (IReadOnlyList<FictionalEntityRelationshipStatement>)group.ToList(), StringComparer.OrdinalIgnoreCase);

        foreach (var (claimKey, (relType, targetEntityType)) in RelationshipClaimMap.Map)
        {
            ct.ThrowIfCancellationRequested();

            var statements = hasStructuredEvidence
                ? statementsByClaimKey.GetValueOrDefault(claimKey) ?? []
                : GetCanonicalFallbackStatements(canonicalValues, claimKey);
            foreach (var statement in statements)
            {
                var targetQid = NormalizeQid(statement.TargetQid);

                if (string.IsNullOrWhiteSpace(targetQid) || !targetQid.StartsWith('Q'))
                {
                    continue;
                }

                try
                {
                    // Find-or-create the target entity. Enqueues enrichment if within depth limit.
                    if (!string.Equals(targetEntityType, "Person", StringComparison.OrdinalIgnoreCase))
                    {
                        await EnsureTargetEntityExists(
                                targetQid,
                                ResolveTargetEntityType(claimKey, targetEntityType, sourceEntity?.EntitySubType),
                                universeQid,
                                universeLabel,
                                currentDepth,
                                maxDepth,
                                ct)
                            .ConfigureAwait(false);
                    }

                    var qualifiers = BuildQualifiers(statement);
                    var startTime = GetQualifierValue(statement, "P580");
                    var endTime = GetQualifierValue(statement, "P582");
                    if (temporalQualifiers?.TryGetValue(targetQid, out var temporal) == true)
                    {
                        startTime ??= temporal.StartTime;
                        endTime ??= temporal.EndTime;
                    }

                    // A statement's qualifiers participate in its key, so adaptation-, time-,
                    // and spoiler-scoped facts do not overwrite one another.
                    await _relRepo.CreateAsync(new EntityRelationship
                    {
                        SubjectQid = entityQid,
                        RelationshipTypeValue = relType,
                        ObjectQid = targetQid,
                        Confidence = statement.Confidence,
                        ContextWorkQid = GetQualifierValue(statement, "P10663") ?? contextWorkQid,
                        SourceProvider = statement.SourceProvider,
                        Provenance = statement.Provenance,
                        DiscoveredAt = DateTimeOffset.UtcNow,
                        StartTime = startTime,
                        EndTime = endTime,
                        Qualifiers = qualifiers,
                    }, ct).ConfigureAwait(false);

                    edgesCreated++;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex,
                        "Failed to create {RelType} edge from {Subject} to {Object}",
                        relType, entityQid, targetQid);
                }
            }
        }

        var appearancesPersisted = sourceEntity is not null
            && await PersistAppearancesAsync(sourceEntity, statementsByClaimKey, ct).ConfigureAwait(false);

        if (edgesCreated > 0 || appearancesPersisted)
        {
            _logger.LogInformation(
                "Created {Count} relationship edges for entity {Qid}",
                edgesCreated, entityQid);

            if (!string.IsNullOrWhiteSpace(universeQid))
            {
                try
                {
                    _graphQuery.InvalidateCache(universeQid);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Failed to invalidate graph cache for universe {Qid}", universeQid);
                }
            }
        }
    }

    private async Task<bool> PersistAppearancesAsync(
        FictionalEntity sourceEntity,
        IReadOnlyDictionary<string, IReadOnlyList<FictionalEntityRelationshipStatement>> statementsByClaimKey,
        CancellationToken ct)
    {
        var persisted = false;
        foreach (var claimKey in new[] { "present_in_work_qid", "first_appearance_qid" })
        {
            if (!statementsByClaimKey.TryGetValue(claimKey, out var statements))
            {
                continue;
            }

            foreach (var statement in statements)
            {
                var workQid = NormalizeQid(statement.TargetQid);
                if (string.IsNullOrWhiteSpace(workQid) || !workQid.StartsWith('Q'))
                {
                    continue;
                }

                await _entityRepo.LinkToWorkAsync(new FictionalEntityWorkLink(
                    FictionalEntityId: sourceEntity.Id,
                    WorkQid: workQid,
                    WorkLabel: statement.TargetLabel,
                    LinkType: string.Equals(claimKey, "first_appearance_qid", StringComparison.OrdinalIgnoreCase)
                        ? "first_appearance"
                        : "appears_in",
                    AppearanceRole: GetQualifierValue(statement, "P5800"),
                    WorkContext: GetQualifierValue(statement, "P10663"),
                    AnchorKind: GetQualifierValue(statement, "P1545") is null ? null : "ordinal",
                    AnchorValue: GetQualifierValue(statement, "P1545"),
                    NarrativeTimeIndex: GetQualifierValue(statement, "P4895"),
                    StartTime: GetQualifierValue(statement, "P580"),
                    EndTime: GetQualifierValue(statement, "P582"),
                    SpoilerForWorkQid: GetQualifierValue(statement, "P7528"),
                    SourceProvider: statement.SourceProvider,
                    Provenance: statement.Provenance,
                    Confidence: statement.Confidence), ct).ConfigureAwait(false);
                persisted = true;
            }
        }

        return persisted;
    }

    private static IReadOnlyList<FictionalEntityRelationshipStatement> GetCanonicalFallbackStatements(
        IReadOnlyDictionary<string, string> canonicalValues,
        string claimKey)
    {
        if (!canonicalValues.TryGetValue(claimKey, out var rawValue)
            || string.IsNullOrWhiteSpace(rawValue))
        {
            return [];
        }

        return rawValue
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(value => value.Split("::", 2)[0])
            .Select(value => new FictionalEntityRelationshipStatement(
                claimKey,
                value,
                TargetLabel: null,
                Confidence: 0.9,
                Qualifiers: []))
            .ToList();
    }

    private static List<EntityRelationshipQualifier> BuildQualifiers(
        FictionalEntityRelationshipStatement statement) => statement.Qualifiers
        .Where(qualifier => !string.IsNullOrWhiteSpace(qualifier.Value))
        .Select(qualifier => new EntityRelationshipQualifier
        {
            QualifierType = qualifier.PropertyId switch
            {
                "P10663" => GraphQualifierType.AppliesToWork,
                "P580" => GraphQualifierType.StartTime,
                "P582" => GraphQualifierType.EndTime,
                "P585" => GraphQualifierType.PointInTime,
                "P4895" => GraphQualifierType.TimeIndex,
                "P7528" => GraphQualifierType.SpoilerForWork,
                _ => $"wikidata:{qualifier.PropertyId}",
            },
            Value = qualifier.Value,
            ValueKind = qualifier.ValueKind,
            SourceProvider = statement.SourceProvider,
            Provenance = statement.Provenance,
            Confidence = statement.Confidence,
        })
        .ToList();

    private static string? GetQualifierValue(FictionalEntityRelationshipStatement statement, string propertyId) =>
        statement.Qualifiers.FirstOrDefault(qualifier =>
            string.Equals(qualifier.PropertyId, propertyId, StringComparison.OrdinalIgnoreCase))?.Value;

    private static string NormalizeQid(string rawQid)
    {
        var qid = rawQid.Contains('/') ? rawQid.Split('/')[^1] : rawQid;
        return qid.Split("::", 2)[0].Trim();
    }

    private static string ResolveTargetEntityType(
        string claimKey,
        string configuredTargetType,
        string? sourceEntityType) => claimKey switch
        {
            "part_of_qid" when sourceEntityType is FictionalEntityType.Event or FictionalEntityType.Object => sourceEntityType,
            "has_parts_qid" when sourceEntityType == FictionalEntityType.Object => FictionalEntityType.Object,
            _ => configuredTargetType,
        };

    /// <summary>
    /// Ensures a fictional entity record exists for the given QID.
    /// If not found, creates a stub record. When <paramref name="currentDepth"/> is
    /// less than <paramref name="maxDepth"/>, the new entity is also enqueued for
    /// Wikidata enrichment so its own relationships are discovered (2-hop lineage).
    /// </summary>
    private async Task EnsureTargetEntityExists(
        string qid, string entitySubType, string universeQid, string? universeLabel,
        int currentDepth, int maxDepth,
        CancellationToken ct)
    {
        var existing = await _entityRepo.FindByQidAsync(qid, ct).ConfigureAwait(false);
        if (existing is not null)
        {
            return;
        }

        var entity = new FictionalEntity
        {
            Id = Guid.NewGuid(),
            WikidataQid = qid,
            Label = qid, // Will be updated when/if enriched
            EntitySubType = entitySubType,
            FictionalUniverseQid = universeQid,
            FictionalUniverseLabel = universeLabel,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        await _entityRepo.CreateAsync(entity, ct).ConfigureAwait(false);

        _logger.LogDebug(
            "Created stub entity for relationship target {Qid} ({Type})",
            qid, entitySubType);

        // Depth-aware enrichment: enqueue if within configured depth limit.
        if (currentDepth < maxDepth)
        {
            var entityType = entitySubType switch
            {
                FictionalEntityType.Character => EntityType.Character,
                FictionalEntityType.Location => EntityType.Location,
                FictionalEntityType.Organization => EntityType.Organization,
                FictionalEntityType.Event => EntityType.Event,
                FictionalEntityType.Object => EntityType.Object,
                _ => EntityType.Character,
            };

            await _harvestQueue.EnqueueAsync(new HarvestRequest
            {
                EntityId = entity.Id,
                EntityType = entityType,
                MediaType = MediaType.Unknown,
                Hints = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["wikidata_qid"] = qid,
                    ["label"] = qid,
                    ["entity_sub_type"] = entitySubType,
                    ["universe_qid"] = universeQid,
                    ["enrichment_depth"] = (currentDepth + 1).ToString(),
                },
            }, ct).ConfigureAwait(false);

            _logger.LogDebug(
                "Enqueued depth-{Depth} enrichment for relationship target {Qid} ({Type})",
                currentDepth + 1, qid, entitySubType);
        }
    }
}
