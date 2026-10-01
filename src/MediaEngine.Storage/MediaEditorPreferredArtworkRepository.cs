using System.Data;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dapper;
using MediaEngine.Domain;
using MediaEngine.Storage.Contracts;

namespace MediaEngine.Storage;

/// <summary>
/// A storage-only preference change for a server-reviewed Work and managed image.
/// The caller must authorize the owner, image, and every affected asset. The
/// assignment cannot be constructed from unverified client artwork facts.
/// </summary>
internal sealed record VerifiedPreferredArtworkAssignment(
    string OperationToken,
    Guid OwnerWorkId,
    string Scope,
    string Role,
    Guid ArtworkAssetId,
    string ExpectedVariantContentHash,
    string ExpectedOwnerRevision,
    IReadOnlyList<VerifiedArtworkAssetLibrary> ExpectedAffectedAssetLibraries);

internal enum PreferredArtworkCommitOutcome { Committed, Replayed, Conflict }

internal sealed record PreferredArtworkCommitResult(PreferredArtworkCommitOutcome Outcome, string? ConflictReason = null);

/// <summary>
/// Explicit owner/role mappings only. ApplyVerifiedInTransaction is the seam
/// for a future editor Save that changes identity and artwork in one SQLite
/// transaction; this standalone entry point uses that same implementation.
/// </summary>
internal sealed class MediaEditorPreferredArtworkRepository(IDatabaseConnection database)
{
    private sealed record ArtworkRole(string SourceAssetType, string Context);

    // Edition/release covers use MediaEditorEditionArtworkRepository instead.
    // Its exact Edition identity review and inheritance rules differ from these
    // Work owner/descendant scopes.
    private static ArtworkRole? ResolveRole(string scope, string role) => (scope, role) switch
    {
        ("TvShow", "Primary") => new("CoverArt", ""),
        ("TvShow", "Background") => new("Background", ""),
        ("TvShow", "Logo") => new("Logo", ""),
        ("TvSeason", "Primary") => new("SeasonPoster", "Season"),
        ("MusicAlbum", "Primary") => new("CoverArt", ""),
        ("Movie", "Primary") => new("CoverArt", ""),
        _ => null,
    };

    public Task<string?> GetOwnerRevisionAsync(Guid ownerWorkId, string scope, string role,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var connection = database.CreateConnection();
        var mapping = ResolveRole(scope, role);
        return Task.FromResult(mapping is not null && IsValidOwner(connection, null, ownerWorkId, scope)
            ? ReadOwnerRevision(connection, null, ownerWorkId, scope, role, mapping)
            : null);
    }

    public Task<PreferredArtworkCommitResult> CommitVerifiedAsync(
        VerifiedPreferredArtworkAssignment assignment, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(assignment);
        return database.ExecuteWriteAsync((connection, transaction, token) =>
            ApplyVerifiedInTransaction(connection, transaction, assignment, token), ct);
    }

    internal static PreferredArtworkCommitResult ApplyVerifiedInTransaction(
        IDbConnection connection, IDbTransaction transaction,
        VerifiedPreferredArtworkAssignment assignment, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var mapping = ResolveRole(assignment.Scope, assignment.Role);
        var expected = assignment.ExpectedAffectedAssetLibraries;
        if (mapping is null || string.IsNullOrWhiteSpace(assignment.OperationToken)
            || assignment.OperationToken.Length > 128 || assignment.OwnerWorkId == Guid.Empty
            || assignment.ArtworkAssetId == Guid.Empty
            || string.IsNullOrWhiteSpace(assignment.ExpectedVariantContentHash)
            || string.IsNullOrWhiteSpace(assignment.ExpectedOwnerRevision)
            || expected is null || expected.Count is < 1 or > 1000
            || expected.Any(item => item.AssetId == Guid.Empty || item.LibraryId == Guid.Empty)
            || expected.Select(item => item.AssetId).Distinct().Count() != expected.Count)
            return new(PreferredArtworkCommitOutcome.Conflict, "The reviewed artwork assignment is incomplete.");

        var normalized = assignment with
        {
            ExpectedAffectedAssetLibraries = expected.OrderBy(item => item.AssetId).ToArray()
        };
        var requestHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
            JsonSerializer.Serialize(normalized))));
        var receipt = connection.QuerySingleOrDefault<ReceiptRow>("""
            SELECT request_hash AS RequestHash, owner_work_id AS OwnerWorkId,
                   artwork_asset_id AS ArtworkAssetId
            FROM media_editor_preferred_artwork_commits WHERE operation_token=@OperationToken;
            """, assignment, transaction);
        if (receipt is not null)
            return receipt.RequestHash == requestHash && receipt.OwnerWorkId == assignment.OwnerWorkId
                   && receipt.ArtworkAssetId == assignment.ArtworkAssetId
                ? new(PreferredArtworkCommitOutcome.Replayed)
                : new(PreferredArtworkCommitOutcome.Conflict,
                    "This operation token was already used for different artwork changes.");

        if (!IsValidOwner(connection, transaction, assignment.OwnerWorkId, assignment.Scope))
            return new(PreferredArtworkCommitOutcome.Conflict, "The artwork owner changed or is unavailable.");
        var actual = ReadAffectedAssets(connection, transaction, assignment.OwnerWorkId, assignment.Scope);
        if (actual is null || !actual.SequenceEqual(normalized.ExpectedAffectedAssetLibraries))
            return new(PreferredArtworkCommitOutcome.Conflict,
                "The owner's complete file set or a file's library changed after artwork review.");
        var variant = connection.QuerySingleOrDefault<VariantRow>("""
            SELECT content_hash AS ContentHash, original_path AS OriginalPath
            FROM artwork_assets WHERE id=@ArtworkAssetId;
            """, assignment, transaction);
        if (variant is null || variant.ContentHash != assignment.ExpectedVariantContentHash
            || string.IsNullOrWhiteSpace(variant.OriginalPath))
            return new(PreferredArtworkCommitOutcome.Conflict,
                "The managed artwork variant changed after review.");
        if (ReadOwnerRevision(connection, transaction, assignment.OwnerWorkId,
                assignment.Scope, assignment.Role, mapping) != assignment.ExpectedOwnerRevision)
            return new(PreferredArtworkCommitOutcome.Conflict,
                "The artwork owner's identity or preferred image changed after review.");

        var previousCanonical = connection.Query<Guid>("""
            SELECT artwork_asset_id FROM entity_artwork_links
            WHERE entity_id=@OwnerWorkId AND entity_type='Work'
              AND role=@Role AND COALESCE(context,'')=@Context AND is_preferred=1
            ORDER BY artwork_asset_id;
            """, new { assignment.OwnerWorkId, assignment.Role, mapping.Context }, transaction).ToArray();
        var previousLegacy = connection.Query<Guid>("""
            SELECT id FROM entity_assets WHERE entity_id=@OwnerWorkId AND entity_type='Work'
              AND asset_type=@SourceAssetType AND is_preferred=1 ORDER BY id;
            """, new { assignment.OwnerWorkId, mapping.SourceAssetType }, transaction).ToArray();
        var now = DateTimeOffset.UtcNow.ToString("O");
        var newLinkId = Guid.NewGuid();
        connection.Execute("""
            UPDATE entity_artwork_links SET is_preferred=0, updated_at=@now
            WHERE entity_id=@OwnerWorkId AND entity_type='Work'
              AND role=@Role AND COALESCE(context,'')=@Context AND is_preferred=1;
            INSERT INTO entity_artwork_links
                (id, entity_id, entity_type, artwork_asset_id, role, context,
                 source_asset_type, is_preferred, is_user_override, created_at)
            VALUES (@newLinkId, @OwnerWorkId, 'Work', @ArtworkAssetId, @Role, @Context,
                    @SourceAssetType, 1, 1, @now)
            ON CONFLICT(entity_id, entity_type, artwork_asset_id, role, context)
            DO UPDATE SET source_asset_type=excluded.source_asset_type,
                          is_preferred=1, is_user_override=1, updated_at=@now;
            """, new { assignment.OwnerWorkId, assignment.ArtworkAssetId,
                assignment.Role, mapping.Context, mapping.SourceAssetType, newLinkId, now }, transaction);
        var durableLinkId = connection.ExecuteScalar<Guid>("""
            SELECT id FROM entity_artwork_links
            WHERE entity_id=@OwnerWorkId AND entity_type='Work'
              AND artwork_asset_id=@ArtworkAssetId AND role=@Role AND context=@Context;
            """, new { assignment.OwnerWorkId, assignment.ArtworkAssetId,
                assignment.Role, mapping.Context }, transaction);
        connection.Execute("""
            UPDATE entity_assets SET is_preferred=0, updated_at=@now
            WHERE entity_id=@OwnerWorkId AND entity_type='Work'
              AND asset_type=@SourceAssetType AND is_preferred=1;
            INSERT INTO entity_assets
                (id, entity_id, entity_type, asset_type, image_url,
                 local_image_path, local_image_path_s, local_image_path_m, local_image_path_l,
                 source_provider, width_px, height_px, aspect_class,
                 primary_hex, secondary_hex, accent_hex, asset_class,
                 storage_location, owner_scope, is_preferred, is_user_override, created_at)
            SELECT @durableLinkId, @OwnerWorkId, 'Work', @SourceAssetType,
                   '/api/v1/display/artwork/assets/' || lower(hex(art.id)) || '/content',
                   art.original_path, art.small_path, art.medium_path, art.large_path,
                   art.source_provider, art.width_px, art.height_px, art.aspect_class,
                   art.primary_hex, art.secondary_hex, art.accent_hex,
                   'Artwork', 'Central', @OwnerScope, 1, 1, @now
            FROM artwork_assets art WHERE art.id=@ArtworkAssetId
            ON CONFLICT(id) DO UPDATE SET
                local_image_path=excluded.local_image_path,
                local_image_path_s=excluded.local_image_path_s,
                local_image_path_m=excluded.local_image_path_m,
                local_image_path_l=excluded.local_image_path_l,
                is_preferred=1, is_user_override=1, updated_at=@now;
            """, new { assignment.OwnerWorkId, assignment.ArtworkAssetId,
                mapping.SourceAssetType, OwnerScope = assignment.Scope, durableLinkId, now }, transaction);
        connection.Execute("""
            INSERT INTO media_editor_preferred_artwork_commits
                (operation_token, request_hash, owner_work_id, owner_scope, role,
                 artwork_asset_id, expected_owner_revision, previous_preferred_ids_json,
                 affected_assets_json, committed_at)
            VALUES (@OperationToken, @requestHash, @OwnerWorkId, @Scope, @Role,
                    @ArtworkAssetId, @ExpectedOwnerRevision, @previousJson,
                    @affectedJson, @now);
            """, new { assignment.OperationToken, requestHash, assignment.OwnerWorkId,
                assignment.Scope, assignment.Role, assignment.ArtworkAssetId,
                assignment.ExpectedOwnerRevision,
                previousJson = JsonSerializer.Serialize(new { Canonical = previousCanonical, Legacy = previousLegacy }),
                affectedJson = JsonSerializer.Serialize(normalized.ExpectedAffectedAssetLibraries), now }, transaction);
        return new(PreferredArtworkCommitOutcome.Committed);
    }

    private static bool IsValidOwner(IDbConnection connection, IDbTransaction? transaction,
        Guid ownerWorkId, string scope)
    {
        var owner = connection.QuerySingleOrDefault<OwnerRow>("""
            SELECT w.media_type AS MediaType, w.work_kind AS WorkKind,
                   w.parent_work_id AS ParentWorkId,
                   parent.media_type AS ParentMediaType, parent.work_kind AS ParentWorkKind,
                   parent.parent_work_id AS GrandparentWorkId
            FROM works w LEFT JOIN works parent ON parent.id=w.parent_work_id
            WHERE w.id=@ownerWorkId;
            """, new { ownerWorkId }, transaction);
        return owner is not null && scope switch
        {
            "TvShow" => owner.MediaType == "TV" && owner.WorkKind == "parent"
                && owner.ParentWorkId is null,
            "TvSeason" => owner.MediaType == "TV" && owner.WorkKind == "parent"
                && owner.ParentWorkId is not null && owner.ParentMediaType == "TV"
                && owner.ParentWorkKind == "parent" && owner.GrandparentWorkId is null,
            "MusicAlbum" => owner.MediaType == "Music" && owner.WorkKind == "parent"
                && owner.ParentWorkId is null,
            "Movie" => owner.MediaType is "Movies" or "Movie"
                && owner.WorkKind is "standalone" or "child",
            _ => false,
        };
    }

    private static IReadOnlyList<VerifiedArtworkAssetLibrary>? ReadAffectedAssets(
        IDbConnection connection, IDbTransaction? transaction, Guid ownerWorkId, string scope)
    {
        var rows = connection.Query<AssetRow>("""
            SELECT a.id AS AssetId, a.library_id AS LibraryId,
                   a.status AS Status, a.is_orphaned AS IsOrphaned,
                   w.media_type AS MediaType, w.work_kind AS WorkKind,
                   CASE WHEN w.id=@ownerWorkId THEN 0
                        WHEN w.parent_work_id=@ownerWorkId THEN 1 ELSE 2 END AS Depth
            FROM works w
            JOIN editions e ON e.work_id=w.id
            JOIN media_assets a ON a.edition_id=e.id
            WHERE w.id=@ownerWorkId
               OR (@IncludeChildren=1 AND w.parent_work_id=@ownerWorkId)
               OR (@IncludeGrandchildren=1 AND w.parent_work_id IN
                   (SELECT id FROM works WHERE parent_work_id=@ownerWorkId))
            ORDER BY a.id;
            """, new { ownerWorkId, IncludeChildren = scope == "Movie" ? 0 : 1,
                IncludeGrandchildren = scope == "TvShow" ? 1 : 0 }, transaction).ToArray();
        if (rows.Length is < 1 or > 1000) return null;
        var result = new List<VerifiedArtworkAssetLibrary>(rows.Length);
        foreach (var row in rows)
        {
            var expectedDepth = scope == "TvShow" ? 2 : scope == "Movie" ? 0 : 1;
            var expectedMediaType = scope == "MusicAlbum" ? "Music" : "TV";
            var validMediaType = scope == "Movie"
                ? row.MediaType is "Movies" or "Movie" : row.MediaType == expectedMediaType;
            var validWorkKind = scope == "Movie"
                ? row.WorkKind is "standalone" or "child" : row.WorkKind == "child";
            if (row.Depth != expectedDepth || !validMediaType || !validWorkKind
                || row.Status != "Normal" || row.IsOrphaned
                || !Guid.TryParse(row.LibraryId, out var libraryId) || libraryId == Guid.Empty)
                return null;
            result.Add(new(row.AssetId, libraryId));
        }
        return result.OrderBy(item => item.AssetId).ToArray();
    }

    private static string ReadOwnerRevision(IDbConnection connection, IDbTransaction? transaction,
        Guid ownerWorkId, string scope, string role, ArtworkRole mapping)
    {
        var owner = connection.QuerySingle<RevisionOwnerRow>("""
            SELECT w.media_type AS MediaType, w.work_kind AS WorkKind,
                   w.parent_work_id AS ParentWorkId,
                   COALESCE((SELECT value FROM canonical_values
                    WHERE entity_id=w.id AND key=@RevisionKey), '') AS IdentityRevision
            FROM works w WHERE w.id=@ownerWorkId;
            """, new { ownerWorkId, RevisionKey = MetadataFieldConstants.IdentityRevision }, transaction);
        var links = connection.Query<PreferenceRow>("""
            SELECT id AS Id, artwork_asset_id AS ArtworkAssetId,
                   is_preferred AS IsPreferred, is_user_override AS IsUserOverride
            FROM entity_artwork_links WHERE entity_id=@ownerWorkId AND entity_type='Work'
              AND role=@role AND COALESCE(context,'')=@Context ORDER BY id;
            """, new { ownerWorkId, role, mapping.Context }, transaction).ToArray();
        var legacy = connection.Query<PreferenceRow>("""
            SELECT id AS Id, is_preferred AS IsPreferred,
                   is_user_override AS IsUserOverride
            FROM entity_assets WHERE entity_id=@ownerWorkId AND entity_type='Work'
              AND asset_type=@SourceAssetType ORDER BY id;
            """, new { ownerWorkId, mapping.SourceAssetType }, transaction).ToArray();
        var data = JsonSerializer.Serialize(new { Version = 1, ownerWorkId, scope, role,
            owner.MediaType, owner.WorkKind, owner.ParentWorkId, owner.IdentityRevision,
            Links = links, Legacy = legacy });
        return "v1:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(data)));
    }

    private sealed class OwnerRow
    {
        public string MediaType { get; set; } = "";
        public string WorkKind { get; set; } = "";
        public Guid? ParentWorkId { get; set; }
        public string? ParentMediaType { get; set; }
        public string? ParentWorkKind { get; set; }
        public Guid? GrandparentWorkId { get; set; }
    }
    private sealed class RevisionOwnerRow
    {
        public string MediaType { get; set; } = "";
        public string WorkKind { get; set; } = "";
        public Guid? ParentWorkId { get; set; }
        public string IdentityRevision { get; set; } = "";
    }
    private sealed class AssetRow
    {
        public Guid AssetId { get; set; }
        public string? LibraryId { get; set; }
        public string Status { get; set; } = "";
        public bool IsOrphaned { get; set; }
        public string MediaType { get; set; } = "";
        public string WorkKind { get; set; } = "";
        public int Depth { get; set; }
    }
    private sealed class VariantRow
    {
        public string ContentHash { get; set; } = "";
        public string? OriginalPath { get; set; }
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
        public Guid OwnerWorkId { get; set; }
        public Guid ArtworkAssetId { get; set; }
    }
}
