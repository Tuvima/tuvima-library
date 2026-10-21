using System.Collections.Concurrent;
using MediaEngine.Domain.Aggregates;
using MediaEngine.Domain.Constants;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Entities;
using MediaEngine.Domain.Enums;
using MediaEngine.Domain.Models;
using MediaEngine.Domain.Services;
using MediaEngine.Intelligence.Contracts;
using Microsoft.Extensions.Logging;

namespace MediaEngine.Intelligence;

/// <summary>
/// Decides which Universe a Series shelf belongs to and keeps the Library's Universe shelves in step with it.
///
/// <para>
/// The Universe record is the <c>narrative_roots</c> row; a Universe shelf is how the Library presents it. A Series shelf
/// always names its Universe (<see cref="Collection.UniverseQid"/>), even when it is the only Series of that Universe.
/// A Universe shelf exists once at least two Series shelves name the same Universe (see
/// <see cref="UniverseIdentityRule.ShouldPresentUniverseShelf"/>), and then every one of those Series shelves is its
/// child. The decision is the one <see cref="UniverseIdentityRule"/>, which the Work-level narrative root uses too.
/// </para>
///
/// <para>
/// The decision is recomputed on every call, so the result does not depend on which shelf was ingested first: when a
/// shelf's Universe changes, the Universe it left is re-checked, and the shelves that point at it through a rollup
/// relationship (a TV show based on this book Series) are decided again.
/// </para>
/// </summary>
public sealed class ParentCollectionResolver : IParentCollectionResolver
{
    // One lock per Universe record so two workers never create two Universe shelves for it (the unique index on
    // collections.universe_qid is the backstop). The resolver is a singleton, so entries live for the process.
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> UniverseLocks = new(StringComparer.OrdinalIgnoreCase);

    private readonly ICollectionRepository _collectionRepo;
    private readonly INarrativeRootRepository _narrativeRoots;
    private readonly ICanonicalValueRepository _canonicalValues;
    private readonly IQidLabelRepository? _qidLabels;
    private readonly IConfigurationLoader? _configLoader;
    private readonly ILogger<ParentCollectionResolver> _logger;

    public ParentCollectionResolver(
        ICollectionRepository collectionRepo,
        INarrativeRootRepository narrativeRoots,
        ICanonicalValueRepository canonicalValues,
        ILogger<ParentCollectionResolver> logger,
        IQidLabelRepository? qidLabels = null,
        IConfigurationLoader? configLoader = null)
    {
        ArgumentNullException.ThrowIfNull(collectionRepo);
        ArgumentNullException.ThrowIfNull(narrativeRoots);
        ArgumentNullException.ThrowIfNull(canonicalValues);
        ArgumentNullException.ThrowIfNull(logger);
        _collectionRepo = collectionRepo;
        _narrativeRoots = narrativeRoots;
        _canonicalValues = canonicalValues;
        _qidLabels = qidLabels;
        _configLoader = configLoader;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task ResolveParentCollectionAsync(Guid collectionId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        await ResolveShelfAsync(collectionId, resolveDependents: true, new HashSet<Guid>(), ct).ConfigureAwait(false);
    }

    private async Task ResolveShelfAsync(
        Guid collectionId,
        bool resolveDependents,
        HashSet<Guid> visited,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!visited.Add(collectionId))
        {
            return;
        }

        var shelf = await _collectionRepo.GetByIdAsync(collectionId, ct).ConfigureAwait(false);
        if (shelf is null)
        {
            _logger.LogWarning("ParentCollectionResolver: Collection {CollectionId} not found; skipping.", collectionId);
            return;
        }

        if (shelf.CollectionType is not (CollectionType.ContentGroup or CollectionType.Series))
        {
            _logger.LogDebug(
                "ParentCollectionResolver: Collection {CollectionId} is a {CollectionType}, not a Series shelf; no-op.",
                collectionId, shelf.CollectionType);
            return;
        }

        var relationships = await _collectionRepo.GetRelationshipsAsync(collectionId, ct).ConfigureAwait(false);
        var choice = UniverseIdentityRule.Choose(BuildInput(shelf, relationships, GetRollupRelationshipTypes()));
        UniverseIdentity? identity = null;
        if (choice is not null)
        {
            identity = await UniverseIdentityRule.CanonicaliseAsync(
                choice,
                new LibraryUniverseLookup(_collectionRepo, _narrativeRoots, shelf.Id),
                ct).ConfigureAwait(false);
        }

        var previousUniverseQid = UniverseIdentityRule.NormaliseQid(shelf.UniverseQid);
        var universeQid = identity?.Qid;
        var changed = !string.Equals(previousUniverseQid, universeQid, StringComparison.OrdinalIgnoreCase);

        string? universeLabel = null;
        if (identity is not null)
        {
            // The shelf's foreign key needs the record to exist before the shelf can name it.
            universeLabel = await EnsureRecordAsync(identity, ct).ConfigureAwait(false);
        }

        if (changed)
        {
            _logger.LogInformation(
                "ParentCollectionResolver: Series shelf {CollectionId} '{Name}' moves from Universe {Previous} to {Universe} ({Step}, {Source}).",
                collectionId, shelf.DisplayName, previousUniverseQid ?? "none", universeQid ?? "none",
                identity?.Step.ToString() ?? "no rollup", identity?.CanonicalSource.ToString() ?? "none");

            // The parent is set by the reconcile below, once the Universe shelf is known to exist.
            await _collectionRepo.SetUniverseAsync(collectionId, universeQid, parentCollectionId: null, ct).ConfigureAwait(false);

            if (universeQid is not null)
            {
                await FollowUniverseWithWorksAsync(shelf, universeQid, universeLabel ?? universeQid, ct).ConfigureAwait(false);
            }
        }

        if (identity is not null)
        {
            await ReconcileUniverseAsync(identity, universeLabel ?? identity.Label, ct).ConfigureAwait(false);
        }

        if (!changed)
        {
            return;
        }

        if (previousUniverseQid is not null)
        {
            await ReconcileUniverseAsync(previousUniverseQid, ct).ConfigureAwait(false);
        }

        if (resolveDependents && !string.IsNullOrWhiteSpace(shelf.WikidataQid))
        {
            await ResolveDependentsAsync(shelf, visited, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Decides again the shelves that point at this one through a rollup relationship (a TV show based on this book
    /// Series): their Universe can follow this shelf's. One level deep only; the visited set stops loops.
    /// </summary>
    private async Task ResolveDependentsAsync(Collection shelf, HashSet<Guid> visited, CancellationToken ct)
    {
        var dependents = await _collectionRepo
            .ListShelvesWithRollupRelationshipQidAsync(shelf.WikidataQid!, ct)
            .ConfigureAwait(false);

        foreach (var dependent in dependents)
        {
            if (dependent.Id == shelf.Id || visited.Contains(dependent.Id))
            {
                continue;
            }

            await ResolveShelfAsync(dependent.Id, resolveDependents: false, visited, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Makes the Universe shelf for <paramref name="identity"/> match the Series shelves that name it: creates it when
    /// the rule is met, and parents every Series shelf of that Universe to it.
    /// </summary>
    private Task ReconcileUniverseAsync(UniverseIdentity identity, string label, CancellationToken ct) =>
        ReconcileUniverseAsync(identity.Qid, label, identity.Step, ct);

    private Task ReconcileUniverseAsync(string universeQid, CancellationToken ct) =>
        ReconcileUniverseAsync(universeQid, label: null, step: null, ct);

    private async Task ReconcileUniverseAsync(
        string universeQid,
        string? label,
        UniverseDecisionStep? step,
        CancellationToken ct)
    {
        var gate = UniverseLocks.GetOrAdd(universeQid, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var seriesShelves = await _collectionRepo.ListSeriesShelvesByUniverseQidAsync(universeQid, ct).ConfigureAwait(false);
            var universeShelf = await _collectionRepo.FindUniverseShelfByUniverseQidAsync(universeQid, ct).ConfigureAwait(false);
            var present = UniverseIdentityRule.ShouldPresentUniverseShelf(
                universeQid,
                seriesShelves.Select(candidate => new UniverseSeriesShelf(candidate.WikidataQid, candidate.MediaType)).ToList());

            if (present)
            {
                universeShelf ??= await CreateUniverseShelfAsync(universeQid, label, step, seriesShelves.Count, ct).ConfigureAwait(false);
                foreach (var seriesShelf in seriesShelves.Where(candidate => candidate.ParentCollectionId != universeShelf.Id))
                {
                    await _collectionRepo.SetUniverseAsync(seriesShelf.Id, universeQid, universeShelf.Id, ct).ConfigureAwait(false);
                    _logger.LogInformation(
                        "ParentCollectionResolver: Series shelf {CollectionId} assigned to Universe shelf {UniverseShelfId} (Universe {Universe}).",
                        seriesShelf.Id, universeShelf.Id, universeQid);
                }
            }

            if (universeShelf is null)
            {
                return;
            }

            // A child that no longer names this Universe, or every child when the rule is no longer met, must not keep
            // pointing at the shelf.
            var namesThisUniverse = present ? seriesShelves.Select(candidate => candidate.Id).ToHashSet() : [];
            var children = await _collectionRepo.GetChildCollectionsAsync(universeShelf.Id, ct).ConfigureAwait(false);
            foreach (var child in children.Where(candidate => !namesThisUniverse.Contains(candidate.Id)))
            {
                await _collectionRepo.SetParentCollectionAsync(child.Id, null, ct).ConfigureAwait(false);
            }

            if (!present)
            {
                var removed = await _collectionRepo.DeleteUnusedUniverseShelfAsync(universeShelf.Id, ct).ConfigureAwait(false);
                _logger.LogInformation(
                    removed
                        ? "ParentCollectionResolver: Universe {Universe} no longer has two Series shelves; removed its unused Universe shelf {UniverseShelfId}."
                        : "ParentCollectionResolver: Universe {Universe} no longer has two Series shelves; kept Universe shelf {UniverseShelfId} because it has content or edits.",
                    universeQid, universeShelf.Id);
            }
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<Collection> CreateUniverseShelfAsync(
        string universeQid,
        string? label,
        UniverseDecisionStep? step,
        int seriesShelfCount,
        CancellationToken ct)
    {
        var record = await _narrativeRoots.FindByQidAsync(universeQid, ct).ConfigureAwait(false);
        var displayName = !string.IsNullOrWhiteSpace(record?.Label)
            ? record!.Label
            : string.IsNullOrWhiteSpace(label) ? universeQid : label;

        var universeShelf = new Collection
        {
            Id = Guid.NewGuid(),
            DisplayName = displayName,
            CreatedAt = DateTimeOffset.UtcNow,
            WikidataQid = universeQid,
            UniverseQid = universeQid,
        };
        universeShelf.RestoreDefinition(
            CollectionType.Universe,
            CollectionScope.Library,
            CollectionResolution.Materialized,
            CollectionMatchMode.All,
            CollectionSortDirection.Desc,
            CollectionUniverseStatus.Unknown);

        await _collectionRepo.UpsertAsync(universeShelf, ct).ConfigureAwait(false);

        await _collectionRepo.InsertRelationshipsAsync(
        [
            new CollectionRelationship
            {
                Id = Guid.NewGuid(),
                CollectionId = universeShelf.Id,
                RelType = RelationshipTypeFor(step),
                RelQid = universeQid,
                RelLabel = displayName,
                Confidence = 1.0,
                DiscoveredAt = DateTimeOffset.UtcNow,
            },
        ], ct).ConfigureAwait(false);

        _logger.LogInformation(
            "ParentCollectionResolver: Created Universe shelf {UniverseShelfId} '{Label}' for Universe {Universe} ({SeriesShelfCount} Series shelf(s)).",
            universeShelf.Id, displayName, universeQid, seriesShelfCount);

        return universeShelf;
    }

    private static string RelationshipTypeFor(UniverseDecisionStep? step) => step switch
    {
        UniverseDecisionStep.FictionalUniverse => "fictional_universe",
        UniverseDecisionStep.Franchise => "franchise",
        UniverseDecisionStep.BasedOn => "based_on",
        _ => "series",
    };

    /// <summary>
    /// Creates the Universe record when the Library has none yet and returns its label. An existing record is never
    /// changed here.
    /// </summary>
    private async Task<string> EnsureRecordAsync(UniverseIdentity identity, CancellationToken ct)
    {
        var label = await SanitizeLabelAsync(identity.Qid, identity.Label, ct).ConfigureAwait(false);
        await _narrativeRoots.EnsureAsync(new NarrativeRoot
        {
            Qid = identity.Qid,
            Label = label,
            Level = identity.Level,
            ParentQid = null,
            CreatedAt = DateTimeOffset.UtcNow,
        }, ct).ConfigureAwait(false);

        var stored = await _narrativeRoots.FindByQidAsync(identity.Qid, ct).ConfigureAwait(false);
        return string.IsNullOrWhiteSpace(stored?.Label) ? label : stored!.Label;
    }

    /// <summary>
    /// The graph root of a Work follows its shelf: when the shelf's Universe changes, the narrative root stored on the
    /// Works of that shelf is rewritten. Fictional entities already discovered under an earlier root keep their old
    /// <c>fictional_universe_qid</c> until the next universe enrichment pass reads the new root.
    /// </summary>
    private async Task FollowUniverseWithWorksAsync(
        Collection shelf,
        string universeQid,
        string universeLabel,
        CancellationToken ct)
    {
        var rootWorkIds = await _collectionRepo.GetRootWorkIdsOnShelfAsync(shelf.Id, ct).ConfigureAwait(false);
        if (rootWorkIds.Count == 0)
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        var values = rootWorkIds
            .SelectMany(workId => new[]
            {
                new CanonicalValue { EntityId = workId, Key = "narrative_root_qid", Value = universeQid, LastScoredAt = now },
                new CanonicalValue { EntityId = workId, Key = "narrative_root_label", Value = universeLabel, LastScoredAt = now },
            })
            .ToList();
        await _canonicalValues.UpsertBatchAsync(values, ct).ConfigureAwait(false);
    }

    private async Task<string> SanitizeLabelAsync(string qid, string label, CancellationToken ct)
    {
        if (!label.Any(c => c > 127))
        {
            return label;
        }

        // A label in another script (reconciliation sometimes returns one) falls back to the cached English label.
        var cached = _qidLabels is null ? null : await _qidLabels.GetLabelAsync(qid, ct).ConfigureAwait(false);
        return !string.IsNullOrWhiteSpace(cached) ? cached : qid;
    }

    private static UniverseIdentityInput BuildInput(
        Collection shelf,
        IReadOnlyList<CollectionRelationship> relationships,
        HashSet<string> rollupTypes)
    {
        IReadOnlyList<UniverseClaim> Of(string relType) => relationships
            .Where(rel => rollupTypes.Contains(rel.RelType)
                && string.Equals(rel.RelType, relType, StringComparison.OrdinalIgnoreCase))
            .Select(rel => new UniverseClaim(rel.RelQid, rel.RelLabel))
            .ToList();

        return new UniverseIdentityInput
        {
            OwnSeriesQid = shelf.WikidataQid,
            OwnSeriesLabel = shelf.DisplayName,
            FictionalUniverses = Of("fictional_universe"),
            Franchises = Of("franchise"),
            Series = Of("series"),
            BasedOn = Of("based_on"),
        };
    }

    private HashSet<string> GetRollupRelationshipTypes()
    {
        try
        {
            var configured = _configLoader?.LoadHydration().CollectionRollupRelationshipTypes
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .ToList();
            if (configured is { Count: > 0 })
            {
                return new HashSet<string>(configured, StringComparer.OrdinalIgnoreCase);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Tests and first-run config fall back to the model defaults.
            _logger.LogDebug(ex, "ParentCollectionResolver: could not read the rollup relationship types; using the defaults.");
        }

        return new HashSet<string>(
            new MediaEngine.Domain.Configuration.HydrationSettings().CollectionRollupRelationshipTypes,
            StringComparer.OrdinalIgnoreCase);
    }
}
