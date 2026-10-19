using MediaEngine.Domain.Constants;

namespace MediaEngine.Domain.Services;

/// <summary>Where a stored manifest row that names a Work came from.</summary>
public enum SeriesMembershipOrigin
{
    /// <summary>A hydrated Wikidata Series manifest lists the Work.</summary>
    WikidataSeries,

    /// <summary>
    /// A hydrated Wikidata parent-collection manifest (for example Memory's Legion) lists the Work. The parent
    /// collection is not a Series of its own; the Series whose manifest lists the parent is.
    /// </summary>
    WikidataParentCollection,

    /// <summary>A retail provider's own sequence manifest (for example a ComicVine volume) lists the Work.</summary>
    ProviderSequence,
}

/// <summary>A Series whose manifest lists a parent collection.</summary>
public sealed record SeriesParentListing(string SeriesQid, string MediaType, Guid CollectionId);

/// <summary>
/// One stored manifest row that names a Work: the shelf (collection) holding the manifest, the Series id, the media
/// type of that Series and the scope the manifest gives the Work.
/// </summary>
public sealed record SeriesManifestMembership(
    Guid CollectionId,
    string SeriesQid,
    string MediaType,
    string Scope,
    SeriesMembershipOrigin Origin,
    IReadOnlyList<SeriesParentListing>? ListedInSeries = null);

/// <summary>
/// The identity of a Series shelf. Series identity is the Wikidata Series QID plus the media type; only when no QID
/// exists does it fall back to a provider key (TMDB collection, TVDB show, ComicVine volume) or the local
/// <c>local:{type}:{label}</c> key.
/// </summary>
public sealed record SeriesShelfIdentity(string MediaType, string Label, string? Qid, string? ProviderKey)
{
    /// <summary>Stable key: the same Series always yields the same key, a Series of another media type never does.</summary>
    public string Key => !string.IsNullOrWhiteSpace(Qid)
        ? $"qid:{Qid.Trim().ToUpperInvariant()}|{MediaType.Trim().ToLowerInvariant()}"
        : $"key:{ProviderKey?.Trim().ToLowerInvariant()}";
}

public enum SeriesPlacementKind
{
    /// <summary>No manifest and no retail identity names a Series; the Work stays where it is (or standalone).</summary>
    Unplaced,

    /// <summary>The Work belongs on exactly one Series shelf.</summary>
    Placed,

    /// <summary>Two Series claim the Work equally and nothing separates them; the Work is left on no Series.</summary>
    Ambiguous,
}

public enum SeriesPlacementBasis
{
    None,
    WikidataManifest,
    ParentCollectionManifest,
    ProviderManifest,
    RetailIdentity,
}

/// <summary>The outcome of <see cref="SeriesPlacementRule.Decide"/>.</summary>
/// <param name="Kind">Placed, unplaced or ambiguous.</param>
/// <param name="Basis">Which evidence decided it.</param>
/// <param name="Shelf">The Series identity when placed through a QID or retail identity.</param>
/// <param name="ShelfCollectionId">The existing shelf when the evidence is a stored manifest; null for a retail identity.</param>
/// <param name="Scope">The membership scope on that shelf (a <see cref="SeriesMembershipScopeNames"/> value).</param>
/// <param name="Candidates">The competing Series when ambiguous.</param>
/// <param name="Reason">A short plain-English reason, for logs.</param>
public sealed record SeriesPlacementDecision(
    SeriesPlacementKind Kind,
    SeriesPlacementBasis Basis,
    SeriesShelfIdentity? Shelf,
    Guid? ShelfCollectionId,
    string Scope,
    IReadOnlyList<SeriesPlacementCandidate> Candidates,
    string Reason);

/// <summary>One Series that claims a Work.</summary>
public sealed record SeriesPlacementCandidate(Guid CollectionId, string SeriesQid, string MediaType, string Scope);

/// <summary>What the rule needs to know about a Work.</summary>
/// <param name="WorkMediaType">The Work's media type (a <c>MediaType</c> name such as Books or Audiobooks).</param>
/// <param name="Memberships">Every stored manifest row that names the Work.</param>
/// <param name="Retail">The retail or provider identity from the Work's claims, when there is one.</param>
public sealed record SeriesPlacementInput(
    string WorkMediaType,
    IReadOnlyList<SeriesManifestMembership> Memberships,
    SeriesShelfIdentity? Retail);

/// <summary>
/// The one rule that says which Series a Work belongs to. Collection assignment and manifest linking both call it, so
/// running either any number of times, in any order, leaves the Work on the same shelf.
///
/// Precedence:
/// (a) a hydrated Wikidata Series manifest that lists the Work, with the scope the manifest gives it;
/// (b) otherwise a parent-collection manifest that lists the Work, placing it in the Series whose manifest lists that
///     parent, as <see cref="SeriesMembershipScopeNames.CollectedContent"/> (the parent never becomes a shelf for it);
/// (c) a Series of another media type is never a candidate. If several Series of the Work's own media type remain, the
///     strongest scope wins (main sequence over supplementary over collected over broader over unpositioned); if two
///     Series are still tied, the Work is ambiguous and is not guessed into either;
/// (d) with no Wikidata manifest, a retail provider's own manifest, then the retail identity (provider key, then
///     label plus media type), as main sequence.
/// </summary>
public static class SeriesPlacementRule
{
    public static SeriesPlacementDecision Decide(SeriesPlacementInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        var eligible = input.Memberships
            .Where(m => MediaMatches(m.MediaType, input.WorkMediaType))
            .ToList();

        var targets = eligible
            .Where(m => m.Origin == SeriesMembershipOrigin.WikidataSeries)
            .Select(m => new SeriesPlacementCandidate(m.CollectionId, m.SeriesQid, m.MediaType, NormalizeScope(m.Scope)))
            .ToList();
        var basis = SeriesPlacementBasis.WikidataManifest;

        if (targets.Count == 0)
        {
            targets = eligible
                .Where(m => m.Origin == SeriesMembershipOrigin.WikidataParentCollection)
                .SelectMany(m => m.ListedInSeries ?? [])
                .Where(listing => MediaMatches(listing.MediaType, input.WorkMediaType))
                .Select(listing => new SeriesPlacementCandidate(
                    listing.CollectionId,
                    listing.SeriesQid,
                    listing.MediaType,
                    SeriesMembershipScopeNames.CollectedContent))
                .ToList();
            basis = SeriesPlacementBasis.ParentCollectionManifest;
        }

        if (targets.Count > 0)
        {
            return DecideAmong(targets, basis);
        }

        var providerShelves = eligible
            .Where(m => m.Origin == SeriesMembershipOrigin.ProviderSequence)
            .GroupBy(m => m.CollectionId)
            .ToList();
        if (providerShelves.Count == 1)
        {
            var shelf = providerShelves[0].OrderBy(m => ScopeRank(m.Scope)).First();
            return new SeriesPlacementDecision(
                SeriesPlacementKind.Placed,
                SeriesPlacementBasis.ProviderManifest,
                input.Retail,
                shelf.CollectionId,
                NormalizeScope(shelf.Scope),
                [],
                $"the provider's own manifest lists the work in '{shelf.SeriesQid}'");
        }

        if (input.Retail is not null)
        {
            return new SeriesPlacementDecision(
                SeriesPlacementKind.Placed,
                SeriesPlacementBasis.RetailIdentity,
                input.Retail,
                null,
                SeriesMembershipScopeNames.MainSequence,
                [],
                "no Wikidata manifest names the work, so the retail identity decides");
        }

        return new SeriesPlacementDecision(
            SeriesPlacementKind.Unplaced,
            SeriesPlacementBasis.None,
            null,
            null,
            SeriesMembershipScopeNames.MainSequence,
            [],
            "no manifest and no retail identity names a Series");
    }

    /// <summary>Whether a Series of <paramref name="seriesMediaType"/> may hold a Work of <paramref name="workMediaType"/>.</summary>
    public static bool MediaMatches(string? seriesMediaType, string? workMediaType)
        => !string.IsNullOrWhiteSpace(seriesMediaType)
           && string.Equals(seriesMediaType.Trim(), workMediaType?.Trim(), StringComparison.OrdinalIgnoreCase);

    /// <summary>Lower is stronger. Unknown scope names rank last.</summary>
    public static int ScopeRank(string? scope) => NormalizeScope(scope) switch
    {
        SeriesMembershipScopeNames.MainSequence => 0,
        SeriesMembershipScopeNames.Supplementary => 1,
        SeriesMembershipScopeNames.CollectedContent => 2,
        SeriesMembershipScopeNames.BroaderContext => 3,
        SeriesMembershipScopeNames.Unpositioned => 4,
        _ => 5,
    };

    private static SeriesPlacementDecision DecideAmong(
        IReadOnlyList<SeriesPlacementCandidate> targets,
        SeriesPlacementBasis basis)
    {
        // The same Series reached twice (two manifest rows, or a parent listed in two rows) counts once, at its
        // strongest scope.
        var collapsed = targets
            .GroupBy(t => $"{t.SeriesQid.Trim().ToUpperInvariant()}|{t.MediaType.Trim().ToLowerInvariant()}")
            .Select(group => group.OrderBy(t => ScopeRank(t.Scope)).ThenBy(t => t.CollectionId).First())
            .ToList();

        var best = collapsed.Min(t => ScopeRank(t.Scope));
        var top = collapsed.Where(t => ScopeRank(t.Scope) == best).ToList();
        if (top.Count == 1)
        {
            var winner = top[0];
            return new SeriesPlacementDecision(
                SeriesPlacementKind.Placed,
                basis,
                new SeriesShelfIdentity(winner.MediaType, winner.SeriesQid, winner.SeriesQid, null),
                winner.CollectionId,
                winner.Scope,
                [],
                basis == SeriesPlacementBasis.ParentCollectionManifest
                    ? $"a parent-collection manifest lists the work and '{winner.SeriesQid}' lists that parent collection"
                    : $"the Wikidata manifest of '{winner.SeriesQid}' lists the work as {winner.Scope}");
        }

        return new SeriesPlacementDecision(
            SeriesPlacementKind.Ambiguous,
            basis,
            null,
            null,
            SeriesMembershipScopeNames.MainSequence,
            top.OrderBy(t => t.SeriesQid, StringComparer.OrdinalIgnoreCase).ToList(),
            $"{top.Count} Series of the work's media type list it as {top[0].Scope} and nothing separates them");
    }

    private static string NormalizeScope(string? scope)
        => string.IsNullOrWhiteSpace(scope) ? SeriesMembershipScopeNames.MainSequence : scope.Trim();
}
