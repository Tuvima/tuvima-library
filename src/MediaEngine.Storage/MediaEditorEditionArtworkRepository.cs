using System.Data;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dapper;
using MediaEngine.Contracts.Artwork;
using MediaEngine.Domain;
using MediaEngine.Storage.Contracts;

namespace MediaEngine.Storage;

internal sealed record VerifiedEditionCoverAssignment(
    string OperationToken,
    Guid EditionId,
    Guid ExpectedWorkId,
    Guid ArtworkAssetId,
    string ExpectedVariantContentHash,
    string ExpectedEditionRevision,
    IReadOnlyList<VerifiedArtworkAssetLibrary> ExpectedAffectedAssetLibraries,
    string? ExpectedMusicBrainzReleaseId = null);

public enum EditionCoverCommitOutcome { Committed, Replayed, Conflict }

public sealed record EditionCoverCommitResult(
    EditionCoverCommitOutcome Outcome,
    string? ConflictReason = null);

public sealed record EditionCoverReviewFacts(
    Guid AssetId,
    Guid EditionId,
    Guid WorkId,
    string MediaType,
    Guid ArtworkAssetId,
    string VariantContentHash,
    string VariantOriginalPath,
    string EditionRevision,
    IReadOnlyList<VerifiedArtworkAssetLibrary> AffectedAssetLibraries,
    string? MusicBrainzReleaseId);

/// <summary>
/// Edition-specific cover preference. The verified Edition ID is the scope:
/// new assets assigned to that Edition inherit it automatically; sibling
/// Editions keep their own choice or fall back to Work artwork.
/// </summary>
public sealed class MediaEditorEditionArtworkRepository(IDatabaseConnection database)
{
    public Task<EditionCoverReviewFacts?> ReviewAssetCoverAsync(
        Guid assetId, Guid artworkAssetId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var connection = database.CreateConnection();
        var identity = connection.QuerySingleOrDefault<AssetReviewRow>(new CommandDefinition("""
            SELECT a.id AS AssetId, e.id AS EditionId, e.work_id AS WorkId,
                   w.media_type AS MediaType
            FROM media_assets a JOIN editions e ON e.id=a.edition_id
            JOIN works w ON w.id=e.work_id
            WHERE a.id=@assetId AND a.status='Normal' AND a.is_orphaned=0;
            """, new { assetId }, cancellationToken: ct));
        if (identity is null || !IsAllowedMedia(identity.MediaType))
        {
            return Task.FromResult<EditionCoverReviewFacts?>(null);
        }
        var edition = ReadEdition(connection, null, identity.EditionId);
        var variant = connection.QuerySingleOrDefault<VariantRow>(new CommandDefinition("""
            SELECT content_hash AS ContentHash, original_path AS OriginalPath
            FROM artwork_assets WHERE id=@artworkAssetId;
            """, new { artworkAssetId }, cancellationToken: ct));
        var affected = ReadAffectedAssets(connection, null, identity.EditionId);
        if (edition is null || variant is null || string.IsNullOrWhiteSpace(variant.ContentHash)
            || string.IsNullOrWhiteSpace(variant.OriginalPath) || affected is null)
        {
            return Task.FromResult<EditionCoverReviewFacts?>(null);
        }
        var releaseId = ReadMusicReleaseId(connection, null, identity.EditionId);
        if (identity.MediaType == "Music" && !Guid.TryParse(releaseId, out _))
        {
            return Task.FromResult<EditionCoverReviewFacts?>(null);
        }
        return Task.FromResult<EditionCoverReviewFacts?>(new(identity.AssetId,
            identity.EditionId, identity.WorkId, identity.MediaType, artworkAssetId,
            variant.ContentHash, variant.OriginalPath, ReadRevision(connection, null, edition),
            affected, releaseId));
    }

    internal Task<string?> GetEditionRevisionAsync(Guid editionId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var connection = database.CreateConnection();
        var edition = ReadEdition(connection, null, editionId);
        return Task.FromResult(IsAllowedMedia(edition?.MediaType)
            ? ReadRevision(connection, null, edition!) : null);
    }

    internal Task<PreferredArtworkCommitResult> CommitVerifiedCoverAsync(
        VerifiedEditionCoverAssignment assignment, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(assignment);
        return database.ExecuteWriteAsync((connection, transaction, token) =>
            ApplyVerifiedInTransaction(connection, transaction, assignment, token), ct);
    }

    public async Task<EditionCoverCommitResult> CommitReviewedCoverAsync(
        string operationToken, EditionCoverReviewFacts review,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(review);
        var result = await CommitVerifiedCoverAsync(new(operationToken,
            review.EditionId, review.WorkId, review.ArtworkAssetId,
            review.VariantContentHash, review.EditionRevision,
            review.AffectedAssetLibraries, review.MusicBrainzReleaseId), ct);
        return new((EditionCoverCommitOutcome)result.Outcome, result.ConflictReason);
    }

    internal static PreferredArtworkCommitResult ApplyVerifiedInTransaction(
        IDbConnection connection, IDbTransaction transaction,
        VerifiedEditionCoverAssignment assignment, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var affected = assignment.ExpectedAffectedAssetLibraries;
        if (string.IsNullOrWhiteSpace(assignment.OperationToken) || assignment.OperationToken.Length > 128
            || assignment.EditionId == Guid.Empty || assignment.ExpectedWorkId == Guid.Empty
            || assignment.ArtworkAssetId == Guid.Empty
            || string.IsNullOrWhiteSpace(assignment.ExpectedVariantContentHash)
            || string.IsNullOrWhiteSpace(assignment.ExpectedEditionRevision)
            || affected is null || affected.Count is < 1 or > 1000
            || affected.Any(item => item.AssetId == Guid.Empty || item.LibraryId == Guid.Empty)
            || affected.Select(item => item.AssetId).Distinct().Count() != affected.Count)
        {
            return new(PreferredArtworkCommitOutcome.Conflict, "The reviewed Edition cover is incomplete.");
        }
        var normalized = assignment with
        {
            ExpectedAffectedAssetLibraries = affected.OrderBy(item => item.AssetId).ToArray()
        };
        var requestHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
            JsonSerializer.Serialize(normalized))));
        var receipt = connection.QuerySingleOrDefault<ReceiptRow>("""
            SELECT request_hash AS RequestHash, edition_id AS EditionId
            FROM media_editor_edition_artwork_commits WHERE operation_token=@OperationToken;
            """, assignment, transaction);
        if (receipt is not null)
        {
            return receipt.RequestHash == requestHash && receipt.EditionId == assignment.EditionId
                    ? new(PreferredArtworkCommitOutcome.Replayed)
                    : new(PreferredArtworkCommitOutcome.Conflict,
                        "This operation token was already used for another Edition cover.");
        }

        var edition = ReadEdition(connection, transaction, assignment.EditionId);
        if (edition is null || edition.WorkId != assignment.ExpectedWorkId
            || !IsAllowedMedia(edition.MediaType))
        {
            return new(PreferredArtworkCommitOutcome.Conflict, "The Edition or its Work changed.");
        }
        var releaseId = ReadMusicReleaseId(connection, transaction, edition.Id);
        if (edition.MediaType == "Music")
        {
            if (string.IsNullOrWhiteSpace(assignment.ExpectedMusicBrainzReleaseId)
                || !Guid.TryParse(assignment.ExpectedMusicBrainzReleaseId, out _)
                || releaseId is null
                || !string.Equals(releaseId, assignment.ExpectedMusicBrainzReleaseId,
                    StringComparison.OrdinalIgnoreCase))
            {
                return new(PreferredArtworkCommitOutcome.Conflict,
                        "This Music Edition has no matching exact release identity.");
            }
        }
        else if (assignment.ExpectedMusicBrainzReleaseId is not null)
        {
            return new(PreferredArtworkCommitOutcome.Conflict,
                    "A Music release identity was supplied for a different media type.");
        }

        var actual = ReadAffectedAssets(connection, transaction, edition.Id);
        if (actual is null || !actual.SequenceEqual(normalized.ExpectedAffectedAssetLibraries))
        {
            return new(PreferredArtworkCommitOutcome.Conflict,
                    "The Edition's complete file set or a file's library changed after review.");
        }
        var variant = connection.QuerySingleOrDefault<VariantRow>("""
            SELECT content_hash AS ContentHash, original_path AS OriginalPath
            FROM artwork_assets WHERE id=@ArtworkAssetId;
            """, assignment, transaction);
        if (variant is null || variant.ContentHash != assignment.ExpectedVariantContentHash
            || string.IsNullOrWhiteSpace(variant.OriginalPath))
        {
            return new(PreferredArtworkCommitOutcome.Conflict,
                    "The managed cover variant changed after review.");
        }
        if (ReadRevision(connection, transaction, edition) != assignment.ExpectedEditionRevision)
        {
            return new(PreferredArtworkCommitOutcome.Conflict,
                    "The Edition identity or cover preference changed after review.");
        }

        var priorCanonical = connection.Query<Guid>("""
            SELECT artwork_asset_id FROM entity_artwork_links
            WHERE entity_id=@EditionId AND entity_type='Edition' AND role='Primary'
              AND COALESCE(context,'')='' AND is_preferred=1 ORDER BY artwork_asset_id;
            """, assignment, transaction).ToArray();
        var priorLegacy = connection.Query<Guid>("""
            SELECT id FROM entity_assets
            WHERE entity_id=@EditionId AND entity_type='Edition' AND asset_type='CoverArt'
              AND is_preferred=1 ORDER BY id;
            """, assignment, transaction).ToArray();
        var newLinkId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow.ToString("O");
        connection.Execute("""
            UPDATE entity_artwork_links SET is_preferred=0, updated_at=@now
            WHERE entity_id=@EditionId AND entity_type='Edition' AND role='Primary'
              AND COALESCE(context,'')='' AND is_preferred=1;
            INSERT INTO entity_artwork_links
                (id, entity_id, entity_type, artwork_asset_id, role, context,
                 source_asset_type, is_preferred, is_user_override, created_at)
            VALUES (@newLinkId, @EditionId, 'Edition', @ArtworkAssetId,
                    'Primary', '', 'CoverArt', 1, 1, @now)
            ON CONFLICT(entity_id, entity_type, artwork_asset_id, role, context)
            DO UPDATE SET source_asset_type='CoverArt', is_preferred=1,
                          is_user_override=1, updated_at=@now;
            """, new { assignment.EditionId, assignment.ArtworkAssetId, newLinkId, now }, transaction);
        var durableLinkId = connection.ExecuteScalar<Guid>("""
            SELECT id FROM entity_artwork_links
            WHERE entity_id=@EditionId AND entity_type='Edition'
              AND artwork_asset_id=@ArtworkAssetId AND role='Primary' AND context='';
            """, assignment, transaction);
        connection.Execute("""
            UPDATE entity_assets SET is_preferred=0, updated_at=@now
            WHERE entity_id=@EditionId AND entity_type='Edition'
              AND asset_type='CoverArt' AND is_preferred=1;
            INSERT INTO entity_assets
                (id, entity_id, entity_type, asset_type, image_url,
                 local_image_path, local_image_path_s, local_image_path_m, local_image_path_l,
                 source_provider, width_px, height_px, aspect_class,
                 primary_hex, secondary_hex, accent_hex, asset_class,
                 storage_location, owner_scope, is_preferred, is_user_override, created_at)
            SELECT @durableLinkId, @EditionId, 'Edition', 'CoverArt',
                   '/api/v1/display/artwork/assets/' || lower(hex(art.id)) || '/content',
                   art.original_path, art.small_path, art.medium_path, art.large_path,
                   art.source_provider, art.width_px, art.height_px, art.aspect_class,
                   art.primary_hex, art.secondary_hex, art.accent_hex,
                   'Artwork', 'Central', 'Edition', 1, 1, @now
            FROM artwork_assets art WHERE art.id=@ArtworkAssetId
            ON CONFLICT(id) DO UPDATE SET
                local_image_path=excluded.local_image_path,
                local_image_path_s=excluded.local_image_path_s,
                local_image_path_m=excluded.local_image_path_m,
                local_image_path_l=excluded.local_image_path_l,
                is_preferred=1, is_user_override=1, updated_at=@now;
            """, new { assignment.EditionId, assignment.ArtworkAssetId, durableLinkId, now }, transaction);
        connection.Execute("""
            INSERT INTO media_editor_edition_artwork_commits
                (operation_token, request_hash, edition_id, work_id, artwork_asset_id,
                 expected_revision, previous_preferred_ids_json, affected_assets_json, committed_at)
            VALUES (@OperationToken, @requestHash, @EditionId, @ExpectedWorkId, @ArtworkAssetId,
                    @ExpectedEditionRevision, @previousJson, @affectedJson, @now);
            """, new { assignment.OperationToken, requestHash, assignment.EditionId,
                assignment.ExpectedWorkId, assignment.ArtworkAssetId,
                assignment.ExpectedEditionRevision,
                previousJson = JsonSerializer.Serialize(new { Canonical = priorCanonical, Legacy = priorLegacy }),
                affectedJson = JsonSerializer.Serialize(normalized.ExpectedAffectedAssetLibraries), now }, transaction);
        return new(PreferredArtworkCommitOutcome.Committed);
    }

    /// <summary>
    /// Reads the asset's actual Edition first, then its Work ancestry. This is
    /// an internal read path; the caller must authorize the media asset.
    /// </summary>
    public Task<EffectiveArtworkSelection?> GetEffectiveAssetCoverAsync(
        Guid assetId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var connection = database.CreateConnection();
        var identity = connection.QuerySingleOrDefault<AssetIdentityRow>("""
            SELECT e.id AS EditionId, e.work_id AS WorkId,
                   w.media_type AS MediaType
            FROM media_assets a JOIN editions e ON e.id=a.edition_id
            JOIN works w ON w.id=e.work_id
            WHERE a.id=@assetId AND a.status='Normal' AND a.is_orphaned=0;
            """, new { assetId });
        if (identity is null || !IsAllowedMedia(identity.MediaType))
        {
            return Task.FromResult<EffectiveArtworkSelection?>(null);
        }
        var workIds = connection.Query<Guid>("""
            WITH RECURSIVE ancestors(id, parent_work_id, depth) AS (
                SELECT id, parent_work_id, 0 FROM works WHERE id=@WorkId
                UNION ALL
                SELECT parent.id, parent.parent_work_id, ancestors.depth+1
                FROM ancestors JOIN works parent ON parent.id=ancestors.parent_work_id
                WHERE ancestors.depth < 8
            ) SELECT id FROM ancestors ORDER BY depth;
            """, identity).ToArray();
        if (workIds.Length == 0)
        {
            return Task.FromResult<EffectiveArtworkSelection?>(null);
        }
        // A Music Edition loses its release-specific precedence if its exact
        // Edition-level source identity disappears or becomes contradictory.
        var edition = identity.MediaType == "Music"
            && !Guid.TryParse(ReadMusicReleaseId(connection, null, identity.EditionId), out _)
            ? new ArtworkEntityWorkspaceDto(identity.EditionId, "Edition", [])
            : ReadWorkspace(connection, identity.EditionId, "Edition");
        var work = ReadWorkspace(connection, workIds[0], "Work");
        var parents = workIds.Skip(1).Select(id => ReadWorkspace(connection, id, "Work")).ToArray();
        return Task.FromResult<EffectiveArtworkSelection?>(
            EffectiveArtworkResolver.ResolveEditionCover(edition, work, parents));
    }

    private static ArtworkEntityWorkspaceDto ReadWorkspace(IDbConnection connection, Guid id, string type)
    {
        var variants = connection.Query<WorkspaceRow>("""
            SELECT link.id AS LinkId, link.artwork_asset_id AS ArtworkAssetId,
                   link.role AS Role, link.context AS Context,
                   link.source_asset_type AS SourceAssetType,
                   link.is_preferred AS IsPreferred,
                   link.is_user_override AS IsUserOverride,
                   art.width_px AS Width, art.height_px AS Height,
                   art.aspect_class AS Aspect, art.source_provider AS SourceProvider,
                   art.source_url AS SourceUrl
            FROM entity_artwork_links link
            JOIN artwork_assets art ON art.id=link.artwork_asset_id
            WHERE link.entity_id=@id AND link.entity_type=@type
            ORDER BY link.role, link.is_preferred DESC, link.sort_order, link.created_at;
            """, new { id, type }).Select(row => new ArtworkEntityVariantDto(
                row.LinkId, row.ArtworkAssetId, row.Role, row.Context, row.SourceAssetType,
                row.IsPreferred, row.IsUserOverride,
                $"/api/v1/display/artwork/assets/{row.ArtworkAssetId:D}/content?size=l",
                $"/api/v1/display/artwork/assets/{row.ArtworkAssetId:D}/content?size=s",
                row.Width, row.Height, row.Aspect ?? "UnsupportedRect",
                row.SourceProvider, row.SourceUrl)).ToArray();
        return new(id, type, variants);
    }

    private static EditionRow? ReadEdition(IDbConnection connection, IDbTransaction? transaction, Guid id) =>
        connection.QuerySingleOrDefault<EditionRow>("""
            SELECT e.id AS Id, e.work_id AS WorkId, e.format_label AS FormatLabel,
                   w.media_type AS MediaType
            FROM editions e JOIN works w ON w.id=e.work_id WHERE e.id=@id;
            """, new { id }, transaction);

    private static bool IsAllowedMedia(string? mediaType) => mediaType is
        "Books" or "Book" or "Audiobooks" or "Audiobook" or "Movies" or "Movie"
        or "Comics" or "Comic" or "Music";

    private static string? ReadMusicReleaseId(IDbConnection connection,
        IDbTransaction? transaction, Guid editionId)
    {
        var canonical = connection.QuerySingleOrDefault<string>("""
            SELECT value FROM canonical_values
            WHERE entity_id=@editionId AND key=@releaseKey LIMIT 1;
            """, new { editionId, releaseKey = BridgeIdKeys.MusicBrainzReleaseId }, transaction);
        var bridge = connection.QuerySingleOrDefault<string>("""
            SELECT id_value FROM bridge_ids
            WHERE entity_id=@editionId AND id_type=@releaseKey LIMIT 1;
            """, new { editionId, releaseKey = BridgeIdKeys.MusicBrainzReleaseId }, transaction);
        if (!string.IsNullOrWhiteSpace(canonical) && !string.IsNullOrWhiteSpace(bridge)
            && !string.Equals(canonical, bridge, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }
        return canonical ?? bridge;
    }

    private static IReadOnlyList<VerifiedArtworkAssetLibrary>? ReadAffectedAssets(
        IDbConnection connection, IDbTransaction? transaction, Guid editionId)
    {
        var rows = connection.Query<AssetRow>("""
            SELECT id AS AssetId, library_id AS LibraryId,
                   status AS Status, is_orphaned AS IsOrphaned
            FROM media_assets WHERE edition_id=@editionId;
            """, new { editionId }, transaction).ToArray();
        if (rows.Length is < 1 or > 1000)
        {
            return null;
        }
        var result = new List<VerifiedArtworkAssetLibrary>(rows.Length);
        foreach (var row in rows)
        {
            if (row.Status != "Normal" || row.IsOrphaned
                || !Guid.TryParse(row.LibraryId, out var libraryId) || libraryId == Guid.Empty)
            {
                return null;
            }
            result.Add(new(row.AssetId, libraryId));
        }
        return result.OrderBy(item => item.AssetId).ToArray();
    }

    private static string ReadRevision(IDbConnection connection, IDbTransaction? transaction, EditionRow edition)
    {
        var identity = connection.Query<IdentityRow>("""
            SELECT key AS Key, value AS Value FROM canonical_values
            WHERE entity_id=@Id AND key IN (@revisionKey, @releaseKey)
            ORDER BY key;
            """, new { edition.Id, revisionKey = MetadataFieldConstants.IdentityRevision,
                releaseKey = BridgeIdKeys.MusicBrainzReleaseId }, transaction).ToArray();
        var bridges = connection.Query<string>("""
            SELECT id_value FROM bridge_ids
            WHERE entity_id=@Id AND id_type=@releaseKey ORDER BY id_value;
            """, new { edition.Id, releaseKey = BridgeIdKeys.MusicBrainzReleaseId }, transaction).ToArray();
        var links = connection.Query<PreferenceRow>("""
            SELECT id AS Id, artwork_asset_id AS ArtworkAssetId,
                   is_preferred AS IsPreferred, is_user_override AS IsUserOverride
            FROM entity_artwork_links WHERE entity_id=@Id AND entity_type='Edition'
              AND role='Primary' AND COALESCE(context,'')='' ORDER BY id;
            """, new { edition.Id }, transaction).ToArray();
        var legacy = connection.Query<PreferenceRow>("""
            SELECT id AS Id, is_preferred AS IsPreferred, is_user_override AS IsUserOverride
            FROM entity_assets WHERE entity_id=@Id AND entity_type='Edition'
              AND asset_type='CoverArt' ORDER BY id;
            """, new { edition.Id }, transaction).ToArray();
        var data = JsonSerializer.Serialize(new { Version = 1, edition.Id, edition.WorkId,
            edition.MediaType, edition.FormatLabel, Identity = identity, Bridges = bridges,
            Links = links, Legacy = legacy });
        return "v1:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(data)));
    }

    private sealed class EditionRow
    {
        public Guid Id { get; set; }
        public Guid WorkId { get; set; }
        public string? FormatLabel { get; set; }
        public string MediaType { get; set; } = "";
    }
    private class AssetIdentityRow
    {
        public Guid EditionId { get; set; }
        public Guid WorkId { get; set; }
        public string MediaType { get; set; } = "";
    }
    private sealed class AssetReviewRow : AssetIdentityRow
    {
        public Guid AssetId { get; set; }
    }
    private sealed class AssetRow
    {
        public Guid AssetId { get; set; }
        public string? LibraryId { get; set; }
        public string Status { get; set; } = "";
        public bool IsOrphaned { get; set; }
    }
    private sealed class VariantRow
    {
        public string ContentHash { get; set; } = "";
        public string? OriginalPath { get; set; }
    }
    private sealed class IdentityRow
    {
        public string Key { get; set; } = "";
        public string? Value { get; set; }
    }
    private sealed class PreferenceRow
    {
        public Guid Id { get; set; }
        public Guid? ArtworkAssetId { get; set; }
        public bool IsPreferred { get; set; }
        public bool IsUserOverride { get; set; }
    }
    private sealed class ReceiptRow
    {
        public string RequestHash { get; set; } = "";
        public Guid EditionId { get; set; }
    }
    private sealed class WorkspaceRow
    {
        public Guid LinkId { get; set; }
        public Guid ArtworkAssetId { get; set; }
        public string Role { get; set; } = "";
        public string? Context { get; set; }
        public string? SourceAssetType { get; set; }
        public bool IsPreferred { get; set; }
        public bool IsUserOverride { get; set; }
        public int? Width { get; set; }
        public int? Height { get; set; }
        public string? Aspect { get; set; }
        public string? SourceProvider { get; set; }
        public string? SourceUrl { get; set; }
    }
}
