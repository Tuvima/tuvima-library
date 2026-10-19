using System.Collections.ObjectModel;
using MediaEngine.Domain.Entities;
using MediaEngine.Domain.Enums;

namespace MediaEngine.Domain.Aggregates;

/// <summary>
/// The aggregate root for a group of related <see cref="Work"/> instances.
///
/// A Collection is a virtual, intelligence-driven discovery collection. It answers
/// "what story-world does this belong to?" — powered by Wikidata relationship
/// properties (franchise, series, fictional universe, narrative chain).
///
/// Collections may optionally belong to a <see cref="Entities.Universe"/> via
/// <see cref="UniverseId"/>; a Collection MUST belong to at most one Universe.
///
/// Works without franchise/series/universe data are standalone (CollectionId = null).
/// Collection assignment is driven by Wikidata QID-to-QID matching (firm links) or
/// text commonality (provisional links) when no QID is available.
///
/// Maps to <c>collections</c> in the Phase 4 schema.
/// </summary>
public sealed class Collection
{
    private readonly List<Work> _works = [];
    private readonly ReadOnlyCollection<Work> _worksView;
    private readonly List<CollectionRelationship> _relationships = [];
    private readonly ReadOnlyCollection<CollectionRelationship> _relationshipsView;
    private readonly List<Collection> _childCollections = [];
    private readonly ReadOnlyCollection<Collection> _childCollectionsView;

    public Collection()
    {
        _worksView = _works.AsReadOnly();
        _relationshipsView = _relationships.AsReadOnly();
        _childCollectionsView = _childCollections.AsReadOnly();
    }

    /// <summary>Stable identifier. PK in <c>collections</c>.</summary>
    public Guid Id { get; set; }

    /// <summary>
    /// Optional membership in a <see cref="Entities.Universe"/>.
    /// Null when this Collection does not belong to any Universe.
    /// Spec: "a Collection MUST belong to a maximum of one Universe."
    /// </summary>
    public Guid? UniverseId { get; set; }

    /// <summary>
    /// Human-readable name for display in the Dashboard and folder structure.
    /// Set from the title canonical value at organization time, or from the
    /// library.xml sidecar during Great Inhale.
    /// Null on collections created before Phase 7.
    /// </summary>
    public string? DisplayName { get; set; }

    /// <summary>
    /// Wikidata Q-identifier for the creative universe this Collection represents.
    /// Populated during Stage 1 (Reconciliation) of the hydration pipeline.
    /// Null until Wikidata identity has been confirmed.
    /// </summary>
    public string? WikidataQid { get; set; }

    /// <summary>
    /// The media type a Series shelf holds (a <c>MediaType</c> name such as Books or Audiobooks). A Series is identified
    /// by its Wikidata Series QID plus this media type, so a book Series and an audiobook Series with the same QID are
    /// two Series. Null on collections that are not Series shelves.
    /// </summary>
    public string? MediaType { get; set; }

    /// <summary>When this Collection was first registered in the system.</summary>
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>
    /// Wikidata coverage level for this Collection.
    /// Rich = QID found + 5+ properties filled.
    /// Limited = QID found but fewer than 5 properties.
    /// None = no QID found in Wikidata.
    /// Unknown = not yet checked (default for new/pre-existing collections).
    /// Tracked in DB and sidecar XML for filtering and scheduled refresh.
    /// </summary>
    public CollectionUniverseStatus UniverseStatus { get; private set; } = CollectionUniverseStatus.Unknown;

    /// <summary>
    /// Optional reference to a parent Collection that represents a franchise or creative universe.
    /// When set, this Collection is a "child" (e.g. "Dune Novels") of a broader Parent Collection (e.g. "Dune").
    /// Null for top-level Collections or Collections that don't belong to a larger franchise.
    /// </summary>
    public Guid? ParentCollectionId { get; set; }

    /// <summary>
    /// The type of collection container: Universe, Smart, System, Mix, Playlist, Genre, Author, Collection, or Custom.
    /// Defaults to "Universe" for broad story-world rollups.
    /// </summary>
    public CollectionType CollectionType { get; private set; } = CollectionType.Universe;

    /// <summary>Plain-text description or rule summary for display.</summary>
    public string? Description { get; set; }

    /// <summary>Icon name hint for UI rendering (e.g. "Label", "Waves", "Person").</summary>
    public string? IconName { get; set; }

    /// <summary>Local path to custom cover artwork uploaded for this collection.</summary>
    public string? CoverArtworkPath { get; set; }

    /// <summary>MIME type for <see cref="CoverArtworkPath"/>.</summary>
    public string? CoverArtworkMimeType { get; set; }

    public string? BackgroundArtworkPath { get; set; }
    public string? BackgroundArtworkMimeType { get; set; }
    public string? BannerArtworkPath { get; set; }
    public string? BannerArtworkMimeType { get; set; }
    public string? LogoArtworkPath { get; set; }
    public string? LogoArtworkMimeType { get; set; }

    /// <summary>"library" for library-scoped collections, "user" for per-profile collections.</summary>
    public CollectionScope Scope { get; private set; } = CollectionScope.Library;

    /// <summary>Owner profile. Null = library-scoped (shared).</summary>
    public Guid? ProfileId { get; set; }

    /// <summary>How membership is maintained. This is independent of Collection identity.</summary>
    public ContainerMembershipMode MembershipMode { get; set; } = ContainerMembershipMode.Smart;

    /// <summary>Primary Read, Watch, Listen, or Mixed projection for curated Collections.</summary>
    public CollectionPrimaryArea PrimaryArea { get; set; } = CollectionPrimaryArea.Mixed;

    /// <summary>Whether the Collection belongs to one profile or the whole Library.</summary>
    public ContainerOwnerKind OwnerKind { get; set; } = ContainerOwnerKind.Library;

    /// <summary>Private, selected-profile, or everyone visibility.</summary>
    public ContainerAudience Audience { get; set; } = ContainerAudience.Everyone;

    /// <summary>Profiles explicitly allowed to view this collection when its audience is selected profiles.</summary>
    public IReadOnlyList<Guid> AudienceProfileIds => _audienceProfileIds;

    private readonly List<Guid> _audienceProfileIds = [];

    public void ReplaceAudienceProfiles(IEnumerable<Guid> profileIds)
    {
        ArgumentNullException.ThrowIfNull(profileIds);
        _audienceProfileIds.Clear();
        _audienceProfileIds.AddRange(profileIds.Where(id => id != Guid.Empty).Distinct());
    }

    /// <summary>Whether this collection is visible in browsing. Default true.</summary>
    public bool IsEnabled { get; set; } = true;

    /// <summary>Whether this collection is pinned to media lane pages.</summary>
    public bool IsFeatured { get; set; }

    /// <summary>Minimum item count for smart collection generation threshold.</summary>
    public int MinItems { get; set; }

    /// <summary>JSON rule definition for smart collections (e.g. genre filter, vibe filter).</summary>
    public string? RuleJson { get; set; }

    /// <summary>How items are resolved: "query" (evaluate rules at display time) or "materialized" (pre-assigned).</summary>
    public CollectionResolution Resolution { get; private set; } = CollectionResolution.Query;

    /// <summary>SHA-256 hash of normalized RuleJson for deduplication.</summary>
    public string? RuleHash { get; set; }

    /// <summary>For content groups: field to group results by (e.g. "season", "album").</summary>
    public string? GroupByField { get; set; }

    /// <summary>Rule match mode: "all" (AND) or "any" (OR).</summary>
    public CollectionMatchMode MatchMode { get; private set; } = CollectionMatchMode.All;

    /// <summary>Default sort field for collection results.</summary>
    public string? SortField { get; set; }

    /// <summary>Sort direction: "asc" or "desc".</summary>
    public CollectionSortDirection SortDirection { get; private set; } = CollectionSortDirection.Desc;

    /// <summary>Optional tie-break sort field for collection results.</summary>
    public string? SecondarySortField { get; set; }

    /// <summary>Optional tie-break direction. Null when no secondary sort is configured.</summary>
    public CollectionSortDirection? SecondarySortDirection { get; set; }

    /// <summary>Cron expression or descriptive schedule for mix refresh.</summary>
    public string? RefreshSchedule { get; set; }

    /// <summary>When the collection's contents were last refreshed/regenerated.</summary>
    public DateTimeOffset? LastRefreshedAt { get; set; }

    /// <summary>Last modification timestamp.</summary>
    public DateTimeOffset? ModifiedAt { get; set; }

    // -------------------------------------------------------------------------
    // Children
    // -------------------------------------------------------------------------

    /// <summary>
    /// All Works that belong to this Collection.
    /// This is the aggregate boundary: changes to a Collection and its Works
    /// MUST occur within a single transaction.
    /// </summary>
    public IReadOnlyList<Work> Works => _worksView;

    /// <summary>
    /// Multi-dimensional grouping signals from Wikidata that define this Collection.
    /// Each relationship links to a Wikidata QID (franchise, series, universe, etc.).
    /// </summary>
    public IReadOnlyList<CollectionRelationship> Relationships => _relationshipsView;

    /// <summary>
    /// Child Collections that belong to this Collection as a franchise/universe parent.
    /// Only populated when this Collection acts as a Parent Collection.
    /// Not loaded by default — requires explicit query.
    /// </summary>
    public IReadOnlyList<Collection> ChildCollections => _childCollectionsView;

    public void RestoreDefinition(
        CollectionType collectionType,
        CollectionScope scope,
        CollectionResolution resolution,
        CollectionMatchMode matchMode,
        CollectionSortDirection sortDirection,
        CollectionUniverseStatus universeStatus)
    {
        CollectionType = collectionType;
        Scope = scope;
        Resolution = resolution;
        MatchMode = matchMode;
        SortDirection = sortDirection;
        UniverseStatus = universeStatus;
    }

    public void ClassifyAs(CollectionType collectionType) =>
        CollectionType = collectionType;

    public void SetVisibility(CollectionScope scope, Guid? profileId)
    {
        if (scope == CollectionScope.User && profileId is null)
        {
            throw new ArgumentException("A user-scoped collection requires an owner profile.", nameof(profileId));
        }

        Scope = scope;
        ProfileId = scope == CollectionScope.Library ? null : profileId;
    }

    public void ChangeResolution(CollectionResolution resolution) =>
        Resolution = resolution;

    public void ChangeRuleOrdering(
        CollectionMatchMode matchMode,
        CollectionSortDirection sortDirection)
    {
        MatchMode = matchMode;
        SortDirection = sortDirection;
    }

    public void RecordUniverseCoverage(CollectionUniverseStatus status) =>
        UniverseStatus = status;

    public void AddWork(Work work)
    {
        ArgumentNullException.ThrowIfNull(work);

        if (work.Id != Guid.Empty && _works.Any(existing => existing.Id == work.Id))
        {
            return;
        }

        _works.Add(work);
    }

    public void AddWorks(IEnumerable<Work> works)
    {
        ArgumentNullException.ThrowIfNull(works);

        foreach (var work in works)
        {
            AddWork(work);
        }
    }

    public void AddRelationship(CollectionRelationship relationship)
    {
        ArgumentNullException.ThrowIfNull(relationship);

        if (HasRelationship(relationship))
        {
            return;
        }

        _relationships.Add(relationship);
    }

    public void AddRelationships(IEnumerable<CollectionRelationship> relationships)
    {
        ArgumentNullException.ThrowIfNull(relationships);

        foreach (var relationship in relationships)
        {
            AddRelationship(relationship);
        }
    }

    public void AddChildCollection(Collection child)
    {
        ArgumentNullException.ThrowIfNull(child);

        if (child.Id != Guid.Empty && _childCollections.Any(existing => existing.Id == child.Id))
        {
            return;
        }

        _childCollections.Add(child);
    }

    public void AddChildCollections(IEnumerable<Collection> children)
    {
        ArgumentNullException.ThrowIfNull(children);

        foreach (var child in children)
        {
            AddChildCollection(child);
        }
    }

    private bool HasRelationship(CollectionRelationship relationship)
    {
        if (relationship.Id != Guid.Empty)
        {
            return _relationships.Any(existing => existing.Id == relationship.Id);
        }

        return _relationships.Any(existing =>
            existing.CollectionId == relationship.CollectionId &&
            string.Equals(existing.RelType, relationship.RelType, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(existing.RelQid, relationship.RelQid, StringComparison.OrdinalIgnoreCase));
    }
}
