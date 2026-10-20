using MediaEngine.Domain;
using MediaEngine.Domain.Aggregates;
using MediaEngine.Domain.Constants;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Enums;
using MediaEngine.Domain.Models;
using MediaEngine.Domain.Services;
using Microsoft.Extensions.Logging;

namespace MediaEngine.Providers.Services;

/// <summary>
/// Resolves the narrative root (the Universe record) for a work.
///
/// <para>
/// A work that sits on a Series shelf takes that shelf's Universe, so the graph and the Library never disagree. A work
/// with no Universe on its shelf is decided by the same <see cref="UniverseIdentityRule"/> the shelves use, from its own
/// claims:
/// <list type="number">
/// <item>P1434 (<c>fictional_universe</c>) — broadest universe</item>
/// <item>P8345 (<c>franchise</c>) — franchise within a universe</item>
/// <item>a Series other than the work's own — for example a broader film series</item>
/// <item>P144 (<c>based_on</c>) — the work it adapts</item>
/// <item>P179 (<c>series</c>) — the work's own Series</item>
/// </list>
/// If none of these exists the work is marked standalone.
/// </para>
///
/// <para>
/// Cross-media convergence: both "Dune (novel)" and "Dune (2021 film)" share
/// P1434 = Q3041974. That shared QID becomes the <c>.universe/</c> folder and the
/// merge point for characters, locations, and organizations across all media.
/// </para>
/// </summary>
public sealed class NarrativeRootResolver : INarrativeRootResolver
{
    private static readonly string[] FictionalMemberKeys =
    [
        MetadataFieldConstants.Characters,
        MetadataFieldConstants.NarrativeLocation,
        MetadataFieldConstants.NarrativeEvent,
        MetadataFieldConstants.NarrativeObject,
    ];

    private readonly ICanonicalValueRepository _canonicalValues;
    private readonly ICanonicalValueArrayRepository _canonicalArrays;
    private readonly INarrativeRootRepository _narrativeRoots;
    private readonly ICollectionRepository _collections;
    private readonly IQidLabelRepository _qidLabelRepo;
    private readonly ISystemActivityRepository _activity;
    private readonly ILogger<NarrativeRootResolver> _logger;

    public NarrativeRootResolver(
        ICanonicalValueRepository canonicalValues,
        ICanonicalValueArrayRepository canonicalArrays,
        INarrativeRootRepository narrativeRoots,
        ICollectionRepository collections,
        IQidLabelRepository qidLabelRepo,
        ISystemActivityRepository activity,
        ILogger<NarrativeRootResolver> logger)
    {
        _canonicalValues = canonicalValues;
        _canonicalArrays = canonicalArrays;
        _narrativeRoots = narrativeRoots;
        _collections = collections;
        _qidLabelRepo = qidLabelRepo;
        _activity = activity;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<NarrativeRoot?> ResolveAsync(Guid entityId, CancellationToken ct = default)
    {
        var values = await _canonicalValues.GetByEntityAsync(entityId, ct);
        var lookup = values.ToDictionary(v => v.Key, v => v.Value, StringComparer.OrdinalIgnoreCase);
        var arrays = await _canonicalArrays.GetAllByEntityAsync(entityId, ct);
        foreach (var (key, entries) in arrays)
        {
            var first = entries.OrderBy(entry => entry.Ordinal).FirstOrDefault();
            if (first is null)
            {
                continue;
            }

            lookup.TryAdd(key, first.Value);
            if (!string.IsNullOrWhiteSpace(first.ValueQid))
            {
                lookup.TryAdd($"{key}{MetadataFieldConstants.CompanionQidSuffix}", first.ValueQid);
            }
        }

        var input = BuildInput(lookup, arrays);

        // A work on a Series shelf takes the shelf's Universe: the shelf is how the Library presents the record, and the
        // graph must root where the Library does.
        var shelf = await _collections.FindSeriesShelfForWorkTreeAsync(entityId, ct).ConfigureAwait(false);
        NarrativeRoot? root = null;
        var shelfUniverseQid = UniverseIdentityRule.NormaliseQid(shelf?.UniverseQid);
        if (shelfUniverseQid is not null)
        {
            root = await EnsureRootFromShelfAsync(shelf!, shelfUniverseQid, ct).ConfigureAwait(false);
        }
        else
        {
            var choice = UniverseIdentityRule.Choose(input);
            if (choice is not null)
            {
                var identity = await UniverseIdentityRule.CanonicaliseAsync(
                    choice,
                    new LibraryUniverseLookup(_collections, _narrativeRoots, shelf?.Id),
                    ct).ConfigureAwait(false);
                root = await EnsureRootAsync(identity.Qid, identity.Label, identity.Level, ct).ConfigureAwait(false);
            }
        }

        if (root is not null)
        {
            await StoreNarrativeRootOnWork(entityId, root, ct);
            await RecordChildNodesAsync(root, input, ct);
            return root;
        }

        // Standalone — no Wikidata universe/franchise/series link found.
        // If the work has a wikidata_qid, mark it as standalone rather than leaving
        // it orphaned. This prevents single-work "universes" from being created.
        if (lookup.TryGetValue("wikidata_qid", out var workQid) &&
            !string.IsNullOrWhiteSpace(workQid))
        {
            await _canonicalValues.UpsertBatchAsync(new[]
            {
                new Domain.Entities.CanonicalValue
                {
                    EntityId     = entityId,
                    Key          = "narrative_scope",
                    Value        = "standalone",
                    LastScoredAt = DateTimeOffset.UtcNow,
                },
            }, ct);

            _logger.LogDebug(
                "Entity {EntityId} marked as standalone (QID {Qid}, no universe/franchise/series)",
                entityId, workQid);
        }
        else
        {
            _logger.LogDebug(
                "No narrative root found for entity {EntityId} — standalone work (no QID)",
                entityId);
        }

        return null;
    }

    // ── Private Helpers ──────────────────────────────────────────────────

    /// <summary>
    /// Collects what the work's claims say for the Universe rule: every value of each relationship type (the scalar
    /// canonical value and each array entry), the work's own Series, and the QIDs of its fictional members.
    /// </summary>
    private static UniverseIdentityInput BuildInput(
        Dictionary<string, string> lookup,
        IReadOnlyDictionary<string, IReadOnlyList<CanonicalArrayEntry>> arrays)
    {
        var series = CollectCandidates(lookup, arrays, MetadataFieldConstants.Series);

        // The work's own Series is its first series_qid: the scalar canonical value, otherwise the first array entry.
        string? ownSeriesQid = null;
        string? ownSeriesLabel = null;
        if (TryExtractQidAndLabel(lookup, MetadataFieldConstants.Series, out var scalarQid, out var scalarLabel))
        {
            ownSeriesQid = scalarQid;
            ownSeriesLabel = scalarLabel;
        }
        else if (series.Count > 0)
        {
            ownSeriesQid = series[0].Qid;
            ownSeriesLabel = series[0].Label;
        }

        return new UniverseIdentityInput
        {
            OwnSeriesQid = ownSeriesQid,
            OwnSeriesLabel = ownSeriesLabel,
            FictionalUniverses = CollectCandidates(lookup, arrays, MetadataFieldConstants.FictionalUniverse),
            Franchises = CollectCandidates(lookup, arrays, MetadataFieldConstants.Franchise),
            Series = series,
            BasedOn = CollectCandidates(lookup, arrays, MetadataFieldConstants.BasedOn),
            FictionalMemberQids = CollectMemberQids(lookup, arrays),
        };
    }

    private static List<UniverseClaim> CollectCandidates(
        Dictionary<string, string> lookup,
        IReadOnlyDictionary<string, IReadOnlyList<CanonicalArrayEntry>> arrays,
        string claimKey)
    {
        var candidates = new List<UniverseClaim>();

        // The scalar canonical value is read from the canonical values alone (the merged lookup also carries the first
        // array entry, which the array pass below adds with every other entry).
        if (TryExtractQidAndLabel(lookup, claimKey, out var qid, out var label))
        {
            candidates.Add(new UniverseClaim(qid, label));
        }

        if (arrays.TryGetValue(claimKey, out var entries))
        {
            foreach (var entry in entries.OrderBy(entry => entry.Ordinal))
            {
                var entryQid = CleanQid(entry.ValueQid);
                if (!string.IsNullOrWhiteSpace(entryQid))
                {
                    candidates.Add(new UniverseClaim(entryQid, entry.Value));
                }
            }
        }

        return candidates;
    }

    private static List<string> CollectMemberQids(
        Dictionary<string, string> lookup,
        IReadOnlyDictionary<string, IReadOnlyList<CanonicalArrayEntry>> arrays)
    {
        var qids = new List<string>();
        foreach (var key in FictionalMemberKeys)
        {
            if (lookup.TryGetValue($"{key}{MetadataFieldConstants.CompanionQidSuffix}", out var value))
            {
                qids.AddRange(value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Select(CleanQid)
                    .Where(qid => !string.IsNullOrWhiteSpace(qid))!);
            }

            if (arrays.TryGetValue(key, out var entries))
            {
                qids.AddRange(entries
                    .Select(entry => CleanQid(entry.ValueQid))
                    .Where(qid => !string.IsNullOrWhiteSpace(qid))!);
            }
        }

        return qids;
    }

    /// <summary>The bare QID from a value that may carry an entity URI prefix or a "::Label" suffix.</summary>
    private static string? CleanQid(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var qid = raw.Contains('/') ? raw.Split('/')[^1] : raw;
        return qid.Split("::", 2)[0].Trim();
    }

    /// <summary>
    /// Try to extract a QID and label from the canonical values for a given claim key.
    /// QID is stored as <c>{key}_qid</c> (e.g. <c>fictional_universe_qid</c>).
    /// Label is stored as <c>{key}</c> (e.g. <c>fictional_universe</c>).
    /// </summary>
    private static bool TryExtractQidAndLabel(
        Dictionary<string, string> lookup,
        string claimKey,
        out string qid,
        out string label)
    {
        qid = string.Empty;
        label = string.Empty;

        if (!lookup.TryGetValue($"{claimKey}_qid", out var rawQid) ||
            string.IsNullOrWhiteSpace(rawQid))
        {
            // Some properties emit entity URIs — try the raw value and strip the URI prefix.
            if (lookup.TryGetValue(claimKey, out var rawValue) &&
                !string.IsNullOrWhiteSpace(rawValue) &&
                rawValue.StartsWith("http://www.wikidata.org/entity/Q", StringComparison.Ordinal))
            {
                var uriQid = rawValue.Split('/')[^1];
                // Strip "::Label" suffix if present
                qid = uriQid.Contains("::") ? uriQid.Split("::", 2)[0] : uriQid;
                label = rawValue; // Will be overwritten if a label exists
            }
            else
            {
                return false;
            }
        }
        else
        {
            // Strip entity URI prefix if present
            qid = rawQid.Contains('/') ? rawQid.Split('/')[^1] : rawQid;
        }

        // Strip "::Label" suffix produced by WikidataAdapter.StripEntityUri().
        // Canonical _qid values may store "Q18417290::Cormoran Strike" — we need
        // the bare QID for folder names and database keys.
        if (qid.Contains("::"))
        {
            var colonParts = qid.Split("::", 2);
            qid = colonParts[0];
        }

        if (string.IsNullOrWhiteSpace(qid))
        {
            return false;
        }

        // Grab the human-readable label
        if (lookup.TryGetValue(claimKey, out var labelValue) && !string.IsNullOrWhiteSpace(labelValue))
        {
            var raw = labelValue;
            // Strip any "QID::Label" prefix if the label claim carried the compound format
            label = raw.Contains("::") ? raw.Split("::", 2)[^1].Trim() : raw;
            // If the label is a bare QID (e.g. "Q3041974"), prefer the ::suffix we stripped earlier
            if (label.Length > 1 && label[0] is 'Q' && char.IsDigit(label[1]))
            {
                label = qid; // Fallback — will be resolved from qid_label cache
            }
        }
        else
        {
            label = qid; // Fallback to QID as label
        }

        return true;
    }

    /// <summary>
    /// Returns the label unchanged if it is pure ASCII; otherwise falls back to
    /// the QID label cache (populated from reconciliation results) or the bare QID.
    /// This guards against reconci.link returning non-Latin labels (e.g. Amharic)
    /// for some entities even when <c>uselang=en</c> is set.
    /// </summary>
    private async Task<string> SanitizeLabelAsync(string qid, string label, CancellationToken ct)
    {
        if (label.Any(c => c > 127))
        {
            var cached = await _qidLabelRepo.GetLabelAsync(qid, ct).ConfigureAwait(false);
            return !string.IsNullOrWhiteSpace(cached) ? cached : qid;
        }
        return label;
    }

    /// <summary>
    /// The shelf's Universe record. It exists whenever the shelf names it (the shelf's foreign key needs it); when it is
    /// missing it is created from the shelf's own name.
    /// </summary>
    private async Task<NarrativeRoot> EnsureRootFromShelfAsync(Collection shelf, string universeQid, CancellationToken ct)
    {
        var existing = await _narrativeRoots.FindByQidAsync(universeQid, ct).ConfigureAwait(false);
        if (existing is not null)
        {
            return existing;
        }

        return await EnsureRootAsync(
            universeQid,
            string.IsNullOrWhiteSpace(shelf.DisplayName) ? universeQid : shelf.DisplayName,
            NarrativeLevel.Series,
            ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Returns the Universe record for the QID, creating it when it is new. A record that already exists is never
    /// changed here: the Universe record is one stable thing, whichever work or shelf reaches it first.
    /// </summary>
    private async Task<NarrativeRoot> EnsureRootAsync(string qid, string label, string level, CancellationToken ct)
    {
        label = await SanitizeLabelAsync(qid, label, ct).ConfigureAwait(false);

        var root = new NarrativeRoot
        {
            Qid = qid,
            Label = label,
            Level = level,
            ParentQid = null,
            CreatedAt = DateTimeOffset.UtcNow,
        };

        await _narrativeRoots.EnsureAsync(root, ct);
        await CacheLabelAsync(qid, label, level, ct);

        var stored = await _narrativeRoots.FindByQidAsync(qid, ct).ConfigureAwait(false) ?? root;
        _logger.LogInformation(
            "Narrative root resolved: {Label} ({Qid}) at level {Level}",
            stored.Label, stored.Qid, stored.Level);

        return stored;
    }

    private async Task UpsertRootAsync(
        string qid, string label, string level, string? parentQid, CancellationToken ct)
    {
        label = await SanitizeLabelAsync(qid, label, ct).ConfigureAwait(false);

        var root = new NarrativeRoot
        {
            Qid = qid,
            Label = label,
            Level = level,
            ParentQid = parentQid,
            CreatedAt = DateTimeOffset.UtcNow,
        };

        await _narrativeRoots.UpsertAsync(root, ct);
        await CacheLabelAsync(qid, label, level, ct);

        _logger.LogInformation(
            "Narrative root resolved: {Label} ({Qid}) at level {Level}",
            label, qid, level);
    }

    /// <summary>Cache narrative root QID → label for offline resolution.</summary>
    private async Task CacheLabelAsync(string qid, string label, string level, CancellationToken ct)
    {
        try
        {
            await _qidLabelRepo.UpsertAsync(qid, label, null, level, ct)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Failed to cache narrative root QID label for {Qid}", qid);
        }
    }

    /// <summary>
    /// Records the franchise and Series the work names as children of its root, so the graph keeps the hierarchy
    /// (Universe → franchise → Series). A node never becomes its own child.
    /// </summary>
    private async Task RecordChildNodesAsync(
        NarrativeRoot root,
        UniverseIdentityInput input,
        CancellationToken ct)
    {
        var members = input.FictionalMemberQids
            .Select(UniverseIdentityRule.NormaliseQid)
            .OfType<string>()
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var franchise = input.Franchises
            .Select(candidate => (Qid: UniverseIdentityRule.NormaliseQid(candidate.Qid), candidate.Label))
            .Where(candidate => candidate.Qid is not null && !members.Contains(candidate.Qid))
            .OrderBy(candidate => candidate.Qid, Comparer<string?>.Create((left, right) => UniverseIdentityRule.CompareQids(left!, right!)))
            .FirstOrDefault();
        var ownSeriesQid = UniverseIdentityRule.NormaliseQid(input.OwnSeriesQid);

        string? franchiseQid = null;
        if (root.Level == NarrativeLevel.Universe
            && franchise.Qid is not null
            && !string.Equals(franchise.Qid, root.Qid, StringComparison.OrdinalIgnoreCase))
        {
            franchiseQid = franchise.Qid;
            await UpsertRootAsync(
                franchise.Qid,
                string.IsNullOrWhiteSpace(franchise.Label) ? franchise.Qid : franchise.Label,
                NarrativeLevel.Franchise,
                root.Qid,
                ct);
        }

        if (ownSeriesQid is null
            || root.Level == NarrativeLevel.Series
            || string.Equals(ownSeriesQid, root.Qid, StringComparison.OrdinalIgnoreCase)
            || string.Equals(ownSeriesQid, franchiseQid, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        await UpsertRootAsync(
            ownSeriesQid,
            string.IsNullOrWhiteSpace(input.OwnSeriesLabel) ? ownSeriesQid : input.OwnSeriesLabel,
            NarrativeLevel.Series,
            franchiseQid ?? root.Qid,
            ct);
    }

    private async Task StoreNarrativeRootOnWork(
        Guid entityId, NarrativeRoot root, CancellationToken ct)
    {
        // Store narrative_root_qid and narrative_root_label as canonical values on the work
        var values = new[]
        {
            new Domain.Entities.CanonicalValue
            {
                EntityId = entityId,
                Key = "narrative_root_qid",
                Value = root.Qid,
                LastScoredAt = DateTimeOffset.UtcNow,
            },
            new Domain.Entities.CanonicalValue
            {
                EntityId = entityId,
                Key = "narrative_root_label",
                Value = root.Label,
                LastScoredAt = DateTimeOffset.UtcNow,
            },
        };

        await _canonicalValues.UpsertBatchAsync(values, ct);

        // Log activity.
        await _activity.LogAsync(new Domain.Entities.SystemActivityEntry
        {
            OccurredAt = DateTimeOffset.UtcNow,
            ActionType = SystemActionType.NarrativeRootResolved,
            CollectionName = root.Label,
            EntityId = entityId,
            EntityType = "NarrativeRoot",
            Detail = $"Resolved narrative root: {root.Label} ({root.Qid}) at level {root.Level}",
        }, ct);
    }
}
