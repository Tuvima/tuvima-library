using System.Collections.ObjectModel;
using MediaEngine.Domain.Entities;
using MediaEngine.Domain.Enums;

namespace MediaEngine.Domain.Aggregates;

// M-083: OwnershipStatus surfaced here as a typed enum.

/// <summary>
/// The intellectual representation of a single title — the "what" of the media,
/// independent of any specific physical copy or encoding.
///
/// A Work may have many <see cref="Edition"/> children, each representing a
/// distinct physical form of the same content. Works also form a parent/child
/// hierarchy among themselves (introduced in M-081): an album is a parent
/// Work whose tracks are child Works, a TV show is a parent of seasons which
/// are parents of episodes, and so on.
///
/// Maps to <c>works</c> in the database.
/// </summary>
public sealed class Work
{
    private readonly Dictionary<string, string> _externalIdentifiers = new(StringComparer.OrdinalIgnoreCase);
    private readonly ReadOnlyDictionary<string, string> _externalIdentifiersView;
    private readonly List<Edition> _editions = [];
    private readonly ReadOnlyCollection<Edition> _editionsView;
    private readonly List<MetadataClaim> _metadataClaims = [];
    private readonly ReadOnlyCollection<MetadataClaim> _metadataClaimsView;
    private readonly List<CanonicalValue> _canonicalValues = [];
    private readonly ReadOnlyCollection<CanonicalValue> _canonicalValuesView;

    public Work()
    {
        _externalIdentifiersView = new ReadOnlyDictionary<string, string>(_externalIdentifiers);
        _editionsView = _editions.AsReadOnly();
        _metadataClaimsView = _metadataClaims.AsReadOnly();
        _canonicalValuesView = _canonicalValues.AsReadOnly();
    }

    /// <summary>Stable identifier. PK in <c>works</c>.</summary>
    public Guid Id { get; set; }

    /// <summary>
    /// Optional lane-level collection or broader rollup that this Work belongs to.
    /// Parent/child structure is represented separately by <see cref="ParentWorkId"/>.
    /// </summary>
    public Guid? CollectionId { get; set; }

    /// <summary>
    /// Wikidata Q-identifier for the specific work (book, film, etc.).
    /// Populated during Stage 1 (Reconciliation) of the hydration pipeline.
    /// Null until Wikidata identity has been confirmed for this Work.
    /// </summary>
    public string? WikidataQid { get; set; }

    /// <summary>
    /// The kind of intellectual content this Work contains.
    /// Stored as a string discriminator (<c>media_type</c> TEXT) in the database.
    /// </summary>
    public MediaType MediaType { get; set; }

    // -------------------------------------------------------------------------
    // M-081 — parent/child hierarchy
    // -------------------------------------------------------------------------

    /// <summary>
    /// This Work's role in the parent/child hierarchy.
    /// See <see cref="WorkKind"/> for the four roles. Default is
    /// <see cref="WorkKind.Standalone"/>.
    /// Stored as TEXT in <c>works.work_kind</c> with a CHECK constraint.
    /// </summary>
    public WorkKind WorkKind { get; set; } = WorkKind.Standalone;

    /// <summary>
    /// The parent Work in the hierarchy. NULL for
    /// <see cref="WorkKind.Standalone"/> and root <see cref="WorkKind.Parent"/>
    /// rows; set for every <see cref="WorkKind.Child"/> and for any nested
    /// parent (e.g. a Season whose parent is a Show).
    /// FK on <c>works.parent_work_id</c> with ON DELETE SET NULL.
    /// </summary>
    public Guid? ParentWorkId { get; set; }

    /// <summary>
    /// Position of this Work within its parent — track number, episode
    /// number, issue number, volume number. NULL for standalone Works
    /// and for parent containers themselves.
    /// Replaces the legacy <c>sequence_index</c> column.
    /// </summary>
    public int? Ordinal { get; set; }

    /// <summary>
    /// True when this Work is known to exist externally (Wikidata or a
    /// retail provider) but no file is in the library yet. Catalog Works
    /// power the "show me what I'm missing" view and are promoted to
    /// <see cref="WorkKind.Standalone"/> or <see cref="WorkKind.Child"/>
    /// when their files are ingested.
    /// </summary>
    public bool IsCatalogOnly { get; set; }

    /// <summary>
    /// Typed ownership status derived from <see cref="IsCatalogOnly"/>.
    /// <see cref="OwnershipStatus.Unowned"/> when no file is present;
    /// <see cref="OwnershipStatus.Owned"/> otherwise.
    ///
    /// Also backed by the <c>works.ownership</c> TEXT column (added in
    /// migration M-083) which stores 'Owned' or 'Unowned' and is kept in
    /// sync with <c>is_catalog_only</c> by the migration backfill.
    /// </summary>
    public OwnershipStatus Ownership
    {
        get => IsCatalogOnly ? OwnershipStatus.Unowned : OwnershipStatus.Owned;
        set => IsCatalogOnly = value == OwnershipStatus.Unowned;
    }

    /// <summary>
    /// Provider-specific identifiers for this Work (e.g.
    /// <c>{"isbn_13":"...", "tmdb_id":"...", "apple_music_id":"..."}</c>).
    /// Stored as a JSON blob in <c>works.external_identifiers</c>.
    /// Used by the HierarchyResolver to re-find catalog rows when their
    /// files are later ingested, and by the enrichment pipeline to skip
    /// search and call providers directly.
    /// </summary>
    public IReadOnlyDictionary<string, string> ExternalIdentifiers => _externalIdentifiersView;

    /// <summary>
    /// Normalized find-or-create cache key. Populated only on parent Works
    /// (<see cref="WorkKind.Parent"/>). Format is media-type-specific and
    /// computed by <c>HierarchyResolver</c> — e.g.
    /// <c>"pink floyd|the dark side of the moon"</c> for music albums or
    /// <c>"breaking bad"</c> for TV shows.
    /// Backed by <c>works.parent_key</c> with a partial index on
    /// <c>(media_type, parent_key)</c>. NULL on standalone/child/catalog rows.
    /// </summary>
    public string? ParentKey { get; set; }

    // -------------------------------------------------------------------------
    // Universe matching state
    // -------------------------------------------------------------------------

    /// <summary>
    /// Indicates the user explicitly skipped Universe (Wikidata) matching for
    /// this Work. When <c>true</c>, the pipeline will not attempt Stage 2
    /// universe linking and the Work is treated as content-matched only.
    /// Stored as INTEGER (0/1) in the <c>works</c> table.
    /// </summary>
    public bool UniverseMismatch { get; set; }

    /// <summary>
    /// Timestamp when <see cref="UniverseMismatch"/> was set to <c>true</c>.
    /// Null when universe matching has not been skipped.
    /// </summary>
    public DateTimeOffset? UniverseMismatchAt { get; set; }

    /// <summary>
    /// Wikidata lookup status: "confirmed" (QID found, firm link),
    /// "pending" (no QID yet, recheck periodically), "skipped" (user decision).
    /// </summary>
    public WikidataLinkStatus WikidataStatus { get; private set; } = WikidataLinkStatus.Pending;

    /// <summary>
    /// Timestamp of the last Wikidata lookup attempt.
    /// Used by the weekly sync to prioritize pending items for recheck.
    /// </summary>
    public DateTimeOffset? WikidataCheckedAt { get; set; }

    /// <summary>
    /// Match resolution level — drives the status chip on the Details tab:
    /// <list type="bullet">
    ///   <item><c>"edition"</c> — <b>Fully Linked</b> (green). Matched to a specific
    ///     Wikidata edition AND its parent work. Best case: edition-specific cover,
    ///     narrator, format metadata, plus full universe linkage.</item>
    ///   <item><c>"work"</c> — <b>Linked</b> (blue). Matched to the Wikidata work
    ///     but no edition entity found. Universe, franchise, characters all work.
    ///     For edition-aware media types, periodically re-checked.</item>
    ///   <item><c>"retail_only"</c> — <b>Identified</b> (amber). Retail provider matched
    ///     (cover + description + bridge IDs) but Wikidata has no entity for this item.
    ///     Will be regularly re-checked. Fully usable but no universe linkage.</item>
    ///   <item><c>"unlinked"</c> — <b>Unlinked</b> (grey). No external match found at all.
    ///     File metadata only. Very rare edge case.</item>
    /// </list>
    /// </summary>
    public WorkMatchLevel MatchLevel { get; private set; } = WorkMatchLevel.Work;

    // -------------------------------------------------------------------------
    // Children
    // -------------------------------------------------------------------------

    /// <summary>
    /// All known physical editions of this Work (e.g. theatrical vs. director's cut).
    /// </summary>
    public IReadOnlyList<Edition> Editions => _editionsView;

    // -------------------------------------------------------------------------
    // Metadata property bags
    // -------------------------------------------------------------------------

    /// <summary>
    /// All provider-asserted key-value claims about this Work.
    /// Multiple providers may assert values for the same key with differing
    /// <see cref="MetadataClaim.Confidence"/> levels.
    /// Append-only — historical claims are never removed.
    /// </summary>
    public IReadOnlyList<MetadataClaim> MetadataClaims => _metadataClaimsView;

    /// <summary>
    /// The winning metadata values for this Work after the scoring engine has
    /// resolved competing claims.
    /// Each entry represents one resolved field in the property bag.
    /// </summary>
    public IReadOnlyList<CanonicalValue> CanonicalValues => _canonicalValuesView;

    public void SetExternalIdentifier(string key, string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(value);

        _externalIdentifiers[key] = value;
    }

    public void LinkToWikidata(
        string qid,
        WorkMatchLevel matchLevel,
        WikidataLinkStatus status = WikidataLinkStatus.Confirmed,
        DateTimeOffset? checkedAt = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(qid);
        if (qid.Length < 2 || (qid[0] is not ('Q' or 'q')) || !qid[1..].All(char.IsDigit))
        {
            throw new ArgumentException("A Wikidata identifier must be Q followed by digits.", nameof(qid));
        }

        if (status is WikidataLinkStatus.Pending
            or WikidataLinkStatus.Missing
            or WikidataLinkStatus.Skipped
            or WikidataLinkStatus.Manual
            or WikidataLinkStatus.ProviderOnly
            or WikidataLinkStatus.UserRejected)
        {
            throw new ArgumentOutOfRangeException(nameof(status), status, "A linked Work requires a linked status.");
        }

        WikidataQid = $"Q{qid[1..]}";
        MatchLevel = matchLevel;
        WikidataStatus = status;
        WikidataCheckedAt = checkedAt ?? DateTimeOffset.UtcNow;
        UniverseMismatch = false;
        UniverseMismatchAt = null;
    }

    public void RecordWikidataState(
        string? qid,
        WikidataLinkStatus status,
        WorkMatchLevel matchLevel,
        DateTimeOffset? checkedAt,
        bool universeMismatch = false,
        DateTimeOffset? universeMismatchAt = null)
    {
        if (!string.IsNullOrWhiteSpace(qid))
        {
            LinkToWikidata(qid, matchLevel, status, checkedAt);
            return;
        }

        if (status is WikidataLinkStatus.Confirmed
            or WikidataLinkStatus.AutoAligned
            or WikidataLinkStatus.UserConfirmed
            or WikidataLinkStatus.UserReplaced)
        {
            throw new ArgumentOutOfRangeException(nameof(status), status, "A linked status requires a Wikidata identifier.");
        }

        WikidataQid = null;
        WikidataStatus = status;
        MatchLevel = matchLevel;
        WikidataCheckedAt = checkedAt;
        UniverseMismatch = universeMismatch;
        UniverseMismatchAt = universeMismatch ? universeMismatchAt ?? checkedAt ?? DateTimeOffset.UtcNow : null;
    }

    /// <summary>
    /// Restores a previously validated identity state from persistence.
    /// New identity decisions should use <see cref="LinkToWikidata"/>.
    /// </summary>
    public void RestoreWikidataState(
        string? qid,
        WikidataLinkStatus status,
        WorkMatchLevel matchLevel,
        DateTimeOffset? checkedAt,
        bool universeMismatch,
        DateTimeOffset? universeMismatchAt)
    {
        WikidataQid = qid;
        WikidataStatus = status;
        MatchLevel = matchLevel;
        WikidataCheckedAt = checkedAt;
        UniverseMismatch = universeMismatch;
        UniverseMismatchAt = universeMismatchAt;
    }

    /// <summary>
    /// Records that this Work (or the container above it) now holds an owned file. A catalog row
    /// becomes a real child Work; every other kind keeps its role.
    /// </summary>
    public void MarkOwned()
    {
        Ownership = OwnershipStatus.Owned;
        if (WorkKind == WorkKind.Catalog)
        {
            WorkKind = WorkKind.Child;
        }
    }

    public void AddEdition(Edition edition)
    {
        ArgumentNullException.ThrowIfNull(edition);

        if (edition.Id != Guid.Empty && _editions.Any(existing => existing.Id == edition.Id))
        {
            return;
        }

        _editions.Add(edition);
    }

    public void AddEditions(IEnumerable<Edition> editions)
    {
        ArgumentNullException.ThrowIfNull(editions);

        foreach (var edition in editions)
        {
            AddEdition(edition);
        }
    }

    public void AddMetadataClaim(MetadataClaim claim)
    {
        ArgumentNullException.ThrowIfNull(claim);

        _metadataClaims.Add(claim);
    }

    public void AddMetadataClaims(IEnumerable<MetadataClaim> claims)
    {
        ArgumentNullException.ThrowIfNull(claims);

        foreach (var claim in claims)
        {
            AddMetadataClaim(claim);
        }
    }

    public void AddCanonicalValue(CanonicalValue value)
    {
        ArgumentNullException.ThrowIfNull(value);

        _canonicalValues.Add(value);
    }

    public void AddCanonicalValues(IEnumerable<CanonicalValue> values)
    {
        ArgumentNullException.ThrowIfNull(values);

        foreach (var value in values)
        {
            AddCanonicalValue(value);
        }
    }
}
