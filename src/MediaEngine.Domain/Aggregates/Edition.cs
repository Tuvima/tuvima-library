using System.Collections.ObjectModel;
using MediaEngine.Domain.Entities;

namespace MediaEngine.Domain.Aggregates;

/// <summary>
/// A specific physical manifestation of a <see cref="Work"/>
/// (e.g. "4K Blu-ray", "First Edition Hardcover", "Director's Cut").
///
/// An Edition is the bridge between the intellectual content (<see cref="Work"/>)
/// and the files on disk (<see cref="MediaAsset"/>).
/// One Work may have many Editions; one Edition may have many MediaAssets.
///
/// Metadata claims and canonical values can be scoped to an Edition as well as
/// to its parent Work, allowing edition-specific enrichment (e.g. runtime of a
/// specific cut differs from the theatrical release).
///
/// Maps to <c>editions</c> in the Phase 4 schema.
/// </summary>
public sealed class Edition
{
    private readonly List<MediaAsset> _mediaAssets = [];
    private readonly ReadOnlyCollection<MediaAsset> _mediaAssetsView;
    private readonly List<MetadataClaim> _metadataClaims = [];
    private readonly ReadOnlyCollection<MetadataClaim> _metadataClaimsView;
    private readonly List<CanonicalValue> _canonicalValues = [];
    private readonly ReadOnlyCollection<CanonicalValue> _canonicalValuesView;

    public Edition()
    {
        _mediaAssetsView = _mediaAssets.AsReadOnly();
        _metadataClaimsView = _metadataClaims.AsReadOnly();
        _canonicalValuesView = _canonicalValues.AsReadOnly();
    }

    /// <summary>Stable identifier. PK in <c>editions</c>.</summary>
    public Guid Id { get; set; }

    /// <summary>FK → <c>works.id</c>.</summary>
    public Guid WorkId { get; set; }

    /// <summary>
    /// Human-readable label describing this physical form.
    /// Examples: <c>"4K Bluray"</c>, <c>"First Edition"</c>, <c>"Director's Cut"</c>.
    /// Nullable — may be absent before metadata is resolved.
    /// </summary>
    public string? FormatLabel { get; set; }

    /// <summary>
    /// Wikidata QID for this specific edition (e.g. Q113799157 for "Blade Runner: The Final Cut").
    /// Populated during Stage 2 (Wikidata Bridge Resolution) when the bridge ID resolves
    /// to an edition entity (P629 present).
    /// </summary>
    public string? WikidataQid { get; set; }

    // -------------------------------------------------------------------------
    // Children
    // -------------------------------------------------------------------------

    /// <summary>
    /// Physical files associated with this Edition.
    /// Each asset carries a verified <see cref="MediaAsset.ContentHash"/>.
    /// </summary>
    public IReadOnlyList<MediaAsset> MediaAssets => _mediaAssetsView;

    // -------------------------------------------------------------------------
    // Metadata property bags
    // -------------------------------------------------------------------------

    /// <summary>
    /// All provider claims scoped to this Edition.
    /// Keyed by (<see cref="MetadataClaim.ClaimKey"/>, <see cref="MetadataClaim.ProviderId"/>).
    /// Append-only — NEVER remove historical claims.
    /// </summary>
    public IReadOnlyList<MetadataClaim> MetadataClaims => _metadataClaimsView;

    /// <summary>
    /// Scored, authoritative values for this Edition derived from
    /// <see cref="MetadataClaims"/>.
    /// Keyed by <see cref="CanonicalValue.Key"/>.
    /// </summary>
    public IReadOnlyList<CanonicalValue> CanonicalValues => _canonicalValuesView;

    /// <summary>
    /// Re-parents this Edition (and so the files it holds) under another Work. A logical change
    /// only: the files on disk are never read, moved or written.
    /// </summary>
    /// <returns><c>true</c> when the Edition moved; <c>false</c> when it already belongs to the Work.</returns>
    public bool MoveToWork(Guid targetWorkId)
    {
        if (targetWorkId == Guid.Empty)
        {
            throw new ArgumentException("A target Work is required.", nameof(targetWorkId));
        }

        if (WorkId == targetWorkId)
        {
            return false;
        }

        WorkId = targetWorkId;
        return true;
    }

    public void AddMediaAsset(MediaAsset asset)
    {
        ArgumentNullException.ThrowIfNull(asset);

        if (asset.Id != Guid.Empty && _mediaAssets.Any(existing => existing.Id == asset.Id))
        {
            return;
        }

        _mediaAssets.Add(asset);
    }

    public void AddMediaAssets(IEnumerable<MediaAsset> assets)
    {
        ArgumentNullException.ThrowIfNull(assets);

        foreach (var asset in assets)
        {
            AddMediaAsset(asset);
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
