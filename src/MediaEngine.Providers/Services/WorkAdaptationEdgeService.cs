using MediaEngine.Domain;
using MediaEngine.Domain.Constants;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Entities;
using Microsoft.Extensions.Logging;

namespace MediaEngine.Providers.Services;

/// <summary>
/// Turns a Work's Wikidata adaptation claims into graph edges between Works, so a Universe's Adaptations fill from
/// evidence instead of being guessed.
/// </summary>
public interface IWorkAdaptationEdgeService
{
    /// <summary>
    /// Writes one <c>entity_relationships</c> row for each "based on" (P144) and "derivative work" (P4969) claim the Work
    /// carries. Safe to run again: a statement that is already stored is updated in place, never duplicated.
    /// </summary>
    /// <param name="canonicalEntityId">The Work (parent-scope entity) whose claims are read.</param>
    /// <param name="fallbackWorkQid">The Work's QID when its own claims do not name it.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>How many adaptation statements were written.</returns>
    Task<int> WriteAsync(Guid canonicalEntityId, string? fallbackWorkQid, CancellationToken ct = default);
}

/// <inheritdoc cref="IWorkAdaptationEdgeService"/>
/// <remarks>
/// Direction: every edge reads "subject is an adaptation of object". A "based on" claim (this Work is based on X) is
/// stored as this Work to X. A "derivative work" claim (X is derived from this Work) is the same fact seen from the
/// other end, so it is stored reversed, as X to this Work with the <c>derivative_work</c> type; the Universe read side
/// then shows it as "X is a derivative of this Work". Ownership is not checked here: the Universe reads only show an
/// edge whose both ends the viewer can see.
/// </remarks>
public sealed class WorkAdaptationEdgeService : IWorkAdaptationEdgeService
{
    private const string WikidataProvider = "wikidata";
    private const string WikidataProvenance = "Wikidata";

    private readonly ICanonicalValueRepository _canonicalValues;
    private readonly ICanonicalValueArrayRepository _canonicalArrays;
    private readonly IEntityRelationshipRepository _relationships;
    private readonly ILogger<WorkAdaptationEdgeService> _logger;

    public WorkAdaptationEdgeService(
        ICanonicalValueRepository canonicalValues,
        ICanonicalValueArrayRepository canonicalArrays,
        IEntityRelationshipRepository relationships,
        ILogger<WorkAdaptationEdgeService> logger)
    {
        ArgumentNullException.ThrowIfNull(canonicalValues);
        ArgumentNullException.ThrowIfNull(canonicalArrays);
        ArgumentNullException.ThrowIfNull(relationships);
        ArgumentNullException.ThrowIfNull(logger);
        _canonicalValues = canonicalValues;
        _canonicalArrays = canonicalArrays;
        _relationships = relationships;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<int> WriteAsync(Guid canonicalEntityId, string? fallbackWorkQid, CancellationToken ct = default)
    {
        var scalars = (await _canonicalValues.GetByEntityAsync(canonicalEntityId, ct).ConfigureAwait(false))
            .GroupBy(value => value.Key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First().Value, StringComparer.OrdinalIgnoreCase);
        var arrays = await _canonicalArrays.GetAllByEntityAsync(canonicalEntityId, ct).ConfigureAwait(false);

        var basedOn = CollectTargetQids(scalars, arrays, MetadataFieldConstants.BasedOn);
        var derivatives = CollectTargetQids(scalars, arrays, MetadataFieldConstants.DerivativeWork);
        if (basedOn.Count == 0 && derivatives.Count == 0)
        {
            return 0;
        }

        var workQid = CleanQid(scalars.GetValueOrDefault(BridgeIdKeys.WikidataQid)) ?? CleanQid(fallbackWorkQid);
        if (workQid is null)
        {
            _logger.LogDebug(
                "Work {EntityId} carries adaptation claims but no Wikidata QID of its own; no adaptation edges written.",
                canonicalEntityId);
            return 0;
        }

        var written = 0;
        foreach (var sourceQid in basedOn)
        {
            written += await WriteEdgeAsync(workQid, RelationshipType.BasedOn, sourceQid, ct).ConfigureAwait(false);
        }

        foreach (var derivativeQid in derivatives)
        {
            // P4969 on this Work names a Work derived from it: the edge points from the derivative to this Work.
            written += await WriteEdgeAsync(derivativeQid, RelationshipType.DerivativeWork, workQid, ct).ConfigureAwait(false);
        }

        if (written > 0)
        {
            _logger.LogInformation(
                "Wrote {Count} adaptation edge(s) for Work {WorkQid} ({EntityId})", written, workQid, canonicalEntityId);
        }

        return written;
    }

    private async Task<int> WriteEdgeAsync(string subjectQid, string relationshipType, string objectQid, CancellationToken ct)
    {
        if (string.Equals(subjectQid, objectQid, StringComparison.OrdinalIgnoreCase))
        {
            return 0;
        }

        // The data store keys a statement by subject, type, object and source, so a re-run updates the row it wrote before.
        await _relationships.CreateAsync(new EntityRelationship
        {
            SubjectQid = subjectQid,
            RelationshipTypeValue = relationshipType,
            ObjectQid = objectQid,
            Confidence = 0.9,
            SourceProvider = WikidataProvider,
            Provenance = WikidataProvenance,
            DiscoveredAt = DateTimeOffset.UtcNow,
        }, ct).ConfigureAwait(false);
        return 1;
    }

    /// <summary>
    /// The QIDs a claim key names: the companion <c>{key}_qid</c> canonical value and each array entry, without repeats.
    /// </summary>
    private static List<string> CollectTargetQids(
        IReadOnlyDictionary<string, string> scalars,
        IReadOnlyDictionary<string, IReadOnlyList<CanonicalArrayEntry>> arrays,
        string claimKey)
    {
        var qids = new List<string>();
        void Add(string? raw)
        {
            var qid = CleanQid(raw);
            if (qid is not null && !qids.Contains(qid, StringComparer.OrdinalIgnoreCase))
            {
                qids.Add(qid);
            }
        }

        if (scalars.TryGetValue(claimKey + MetadataFieldConstants.CompanionQidSuffix, out var scalarQid))
        {
            foreach (var part in (scalarQid ?? string.Empty).Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                Add(part);
            }
        }

        if (arrays.TryGetValue(claimKey, out var entries))
        {
            foreach (var entry in entries.OrderBy(entry => entry.Ordinal))
            {
                Add(entry.ValueQid);
            }
        }

        return qids;
    }

    /// <summary>The bare QID from a value that may carry an entity URI prefix or a "::Label" suffix; null when it is not a QID.</summary>
    private static string? CleanQid(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var qid = raw.Contains('/') ? raw.Split('/')[^1] : raw;
        qid = qid.Split("::", 2)[0].Trim();
        return qid.Length > 1 && qid[0] == 'Q' ? qid : null;
    }
}
