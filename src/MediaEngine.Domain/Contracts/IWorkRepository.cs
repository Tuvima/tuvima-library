using MediaEngine.Domain.Enums;

namespace MediaEngine.Domain.Contracts;

/// <summary>
/// Repository for querying and persisting Work entities.
///
/// As of Phase 3 (M-082), the interface centres on parent/child resolution
/// rather than ad-hoc title+author dedup. The legacy
/// <c>FindByTitleAuthorAsync</c> path is gone — hierarchical media types
/// (music, TV, comics, series-bound books) flow through the
/// <c>HierarchyResolver</c>, which calls <see cref="FindParentByKeyAsync"/>
/// and <see cref="FindChildByOrdinalAsync"/>.
/// </summary>
public interface IWorkRepository
{
    Task<Guid> GetOrCreateAudiobookRecordingAsync(string recordingKey, Guid? seriesId, double? ordinalSort, CancellationToken ct = default)
        => throw new NotSupportedException("Recording identity is not implemented by this repository.");

    /// <summary>
    /// Finds a parent Work for the given media type and normalized
    /// <paramref name="parentKey"/>. Used as the indexed find-or-create
    /// lookup at the start of every hierarchical ingestion.
    /// Returns null when no parent exists yet.
    /// </summary>
    Task<Guid?> FindParentByKeyAsync(
        MediaType mediaType,
        string parentKey,
        CancellationToken ct = default);

    /// <summary>
    /// Atomically finds or creates a parent Work. Implementations should use
    /// insert-or-select semantics so concurrent ingestion cannot create
    /// duplicate local shelves.
    /// </summary>
    Task<Guid> GetOrCreateParentAsync(
        MediaType mediaType,
        string parentKey,
        Guid? grandparentWorkId,
        int? ordinal,
        double? ordinalSort = null,
        CancellationToken ct = default);

    /// <summary>
    /// Finds a child Work of the given parent at the given ordinal
    /// (track number, episode number, issue number, volume number).
    /// Returns null when no child exists at that position.
    /// </summary>
    Task<Guid?> FindChildByOrdinalAsync(
        Guid parentWorkId,
        int ordinal,
        CancellationToken ct = default);

    /// <summary>
    /// Finds a child by decimal/disc-aware sort value when the integer ordinal
    /// is not precise enough.
    /// </summary>
    Task<Guid?> FindChildByOrdinalSortAsync(
        Guid parentWorkId,
        double ordinalSort,
        CancellationToken ct = default);

    /// <summary>
    /// Finds a child Work of the given parent by case-insensitive title
    /// match. Used as a fallback when the source file lacks an ordinal
    /// (track number missing, episode title only, etc.).
    /// </summary>
    Task<Guid?> FindChildByTitleAsync(
        Guid parentWorkId,
        string title,
        CancellationToken ct = default);

    /// <summary>
    /// Finds any Work whose <c>external_identifiers</c> JSON blob contains
    /// the given <paramref name="scheme"/> → <paramref name="value"/> pair.
    /// Used by the resolver to promote catalog rows when their files arrive.
    /// </summary>
    Task<Guid?> FindByExternalIdentifierAsync(
        string scheme,
        string value,
        CancellationToken ct = default);

    /// <summary>
    /// Inserts a new parent Work. Sets <c>work_kind = 'parent'</c> and
    /// records the <paramref name="parentKey"/> for future find-or-create
    /// hits. Returns the new Work's id.
    /// </summary>
    Task<Guid> InsertParentAsync(
        MediaType mediaType,
        string parentKey,
        Guid? grandparentWorkId,
        int? ordinal,
        CancellationToken ct = default);

    /// <summary>
    /// Inserts a new child Work. Sets <c>work_kind = 'child'</c> and
    /// links to <paramref name="parentWorkId"/>. Returns the new Work's id.
    /// </summary>
    Task<Guid> InsertChildAsync(
        MediaType mediaType,
        Guid parentWorkId,
        int? ordinal,
        CancellationToken ct = default);

    /// <summary>
    /// Atomically finds or creates a child Work by decimal/disc-aware sort
    /// value when present, otherwise by integer ordinal.
    /// </summary>
    Task<Guid> GetOrCreateChildAsync(
        MediaType mediaType,
        Guid parentWorkId,
        int? ordinal,
        double? ordinalSort = null,
        CancellationToken ct = default);

    /// <summary>
    /// Persists the decimal/disc-aware sequence sort value for a Work.
    /// </summary>
    Task UpdateOrdinalSortAsync(
        Guid workId,
        double? ordinalSort,
        CancellationToken ct = default);

    /// <summary>
    /// Inserts a new standalone Work (movies, single-title books, etc.).
    /// Returns the new Work's id.
    /// </summary>
    Task<Guid> InsertStandaloneAsync(
        MediaType mediaType,
        CancellationToken ct = default);

    /// <summary>
    /// Inserts a new catalog Work — known externally but no file in the
    /// library yet. <paramref name="ordinal"/> is the child's position
    /// inside its parent. The CatalogUpsertService uses this when Wikidata
    /// reveals tracks/episodes/issues you don't yet own.
    /// </summary>
    Task<Guid> InsertCatalogChildAsync(
        MediaType mediaType,
        Guid parentWorkId,
        int? ordinal,
        IReadOnlyDictionary<string, string>? externalIdentifiers,
        CancellationToken ct = default);

    /// <summary>
    /// Promotes a catalog row (<c>work_kind = 'catalog'</c>,
    /// <c>is_catalog_only = 1</c>) to a real owned child Work
    /// (<c>work_kind = 'child'</c>, <c>is_catalog_only = 0</c>) once a
    /// matching file is ingested.
    /// </summary>
    Task PromoteCatalogToOwnedAsync(Guid workId, CancellationToken ct = default);

    /// <summary>
    /// Merges the given identifiers into <c>works.external_identifiers</c>.
    /// Existing keys are NOT overwritten — only missing keys are added.
    /// Each pipeline layer (file processor → retail → Wikidata) calls this
    /// with the IDs it knows about.
    /// </summary>
    Task WriteExternalIdentifiersAsync(
        Guid workId,
        IReadOnlyDictionary<string, string> identifiers,
        CancellationToken ct = default);

    /// <summary>
    /// Resolves the parent/child lineage for a given media asset.
    /// Walks <c>media_assets → editions → works</c> and returns the asset's
    /// owning Work along with its parent Work id (if any) and work kind.
    /// Returns <c>null</c> when the asset id is unknown.
    ///
    /// Workers (RetailMatchWorker, WikidataBridgeWorker) use this to decide
    /// whether a provider claim or bridge ID describes the file's own Work
    /// (a track, episode, issue) or its container (an album, show, series).
    /// </summary>
    Task<WorkLineage?> GetLineageByAssetAsync(
        Guid assetId,
        CancellationToken ct = default);

    /// <summary>
    /// Lists the direct child works of a container. TV artwork enrichment uses
    /// this to discover the locally represented season works beneath a show.
    /// </summary>
    Task<IReadOnlyList<ChildWorkReference>> GetDirectChildrenAsync(
        Guid parentWorkId,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult<IReadOnlyList<ChildWorkReference>>([]);
    }

    /// <summary>
    /// Finds a trusted QID already assigned to an owned sibling variant in
    /// another media type. Used by Stage 2 when an audiobook/book variant has
    /// retail identity but the provider bridge does not resolve directly.
    /// Implementations must require a normalized title match and should use
    /// creator matching when a creator hint is available.
    /// </summary>
    Task<ConfirmedSiblingWorkQid?> FindConfirmedSiblingQidAsync(
        MediaType sourceMediaType,
        IReadOnlyList<MediaType> candidateMediaTypes,
        string title,
        string? creator,
        Guid? excludeWorkId = null,
        CancellationToken ct = default);
}

/// <summary>
/// Walked lineage of a single media asset: the asset's owning Work,
/// the parent Work that contains it (if hierarchical), and the work kind.
/// Returned by <see cref="IWorkRepository.GetLineageByAssetAsync"/>.
/// </summary>
public sealed record WorkLineage(
    Guid AssetId,
    Guid EditionId,
    Guid WorkId,
    Guid? ParentWorkId,
    Guid RootParentWorkId,
    WorkKind WorkKind,
    MediaType MediaType)
{
    /// <summary>
    /// Returns the Work id that should receive a parent-scoped claim.
    /// For TV (Show → Season → Episode) this walks up to the SHOW, not the
    /// season — show_name, network, cast, and genre are show-level facts.
    /// For music/comics/series-bound books this is the immediate parent.
    /// For standalone media (movies, single books) this falls back to the
    /// asset's own Work — parent-scoped claims are still recorded, but on
    /// the same row as self-scoped claims, which is the desired behaviour.
    /// </summary>
    public Guid TargetForParentScope => RootParentWorkId;

    /// <summary>
    /// Returns the Work id that should receive a self-scoped claim — the
    /// asset's own Work (track, episode, issue, movie, single book).
    /// </summary>
    public Guid TargetForSelfScope => WorkId;
}

public sealed record ConfirmedSiblingWorkQid(
    Guid WorkId,
    MediaType MediaType,
    string WikidataQid,
    string Title,
    string? Creator);

public sealed record ChildWorkReference(
    Guid WorkId,
    int? Ordinal,
    WorkKind WorkKind,
    bool IsCatalogOnly);
