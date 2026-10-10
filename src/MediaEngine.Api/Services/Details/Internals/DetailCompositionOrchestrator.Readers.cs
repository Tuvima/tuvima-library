using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dapper;
using MediaEngine.Api.Endpoints;
using MediaEngine.Api.Models;
using MediaEngine.Api.Services.Display;
using MediaEngine.Api.Services.Playback;
using MediaEngine.Api.Services.ReadServices;
using MediaEngine.Contracts.Collections;
using MediaEngine.Contracts.Details;
using MediaEngine.Contracts.Persons;
using MediaEngine.Domain;
using MediaEngine.Domain.Aggregates;
using MediaEngine.Domain.Constants;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Entities;
using MediaEngine.Domain.Enums;
using MediaEngine.Domain.Models;
using MediaEngine.Domain.Services;
using MediaEngine.Storage;
using MediaEngine.Storage.Contracts;
using static MediaEngine.Api.Services.Details.Internals.DetailPresentationPolicy;
using SeriesManifestItemDto = MediaEngine.Domain.Models.SeriesManifestItemDto;
using SeriesManifestViewDto = MediaEngine.Domain.Models.SeriesManifestViewDto;

namespace MediaEngine.Api.Services.Details.Internals;

internal sealed partial class DetailCompositionOrchestrator
{
    private async Task<IReadOnlyList<CollectionWorkSummary>> LoadCollectionWorksAsync(
        Guid collectionId,
        Guid? rootWorkId,
        CancellationToken ct,
        IReadOnlyList<Guid>? resolvedWorkIds = null,
        Guid? profileId = null,
        IReadOnlyList<Guid>? authorizedAssetIds = null)
    {
        if (authorizedAssetIds is { Count: 0 })
        {
            return [];
        }

        using var conn = _db.CreateConnection();
        var displayYearSql = MediaDateSql.DisplayOriginalYear(
            "w.id",
            "COALESCE(gp.id, p.id, w.id)",
            "ma.id",
            "w.media_type");
        ct.ThrowIfCancellationRequested();
        var rawRows = conn.Query<dynamic>(new CommandDefinition(
            $"""
            SELECT w.id AS Id,
                   ma.id AS AssetId,
                   CAST(w.media_type AS TEXT) AS MediaType,
                   w.ordinal AS Ordinal,
                   CAST(w.display_overrides_json AS TEXT) AS WorkDisplayOverridesJson,
                   CAST(COALESCE(
                       (SELECT NULLIF(CAST(cv.value AS TEXT), '') FROM canonical_values cv WHERE cv.entity_id = ma.id AND cv.key = 'issue_title' LIMIT 1),
                       (SELECT NULLIF(CAST(cv.value AS TEXT), '') FROM canonical_values cv WHERE cv.entity_id = w.id AND cv.key = 'issue_title' LIMIT 1),
                       (SELECT NULLIF(CAST(cv.value AS TEXT), '') FROM canonical_values cv WHERE cv.entity_id = ma.id AND cv.key = 'episode_title' LIMIT 1),
                       (SELECT NULLIF(CAST(cv.value AS TEXT), '') FROM canonical_values cv WHERE cv.entity_id = w.id AND cv.key = 'episode_title' LIMIT 1),
                       (SELECT NULLIF(CAST(cv.value AS TEXT), '') FROM canonical_values cv WHERE cv.entity_id = ma.id AND cv.key = 'title' LIMIT 1),
                       (SELECT NULLIF(CAST(cv.value AS TEXT), '') FROM canonical_values cv WHERE cv.entity_id = w.id AND cv.key = 'title' LIMIT 1),
                       'Untitled') AS TEXT) AS Title,
                   CAST(COALESCE(
                       (SELECT NULLIF(CAST(cv.value AS TEXT), '') FROM canonical_values cv WHERE cv.entity_id = w.id AND cv.key = 'issue_description' LIMIT 1),
                       (SELECT NULLIF(CAST(cv.value AS TEXT), '') FROM canonical_values cv WHERE cv.entity_id = ma.id AND cv.key = 'issue_description' LIMIT 1),
                       (SELECT NULLIF(CAST(cv.value AS TEXT), '') FROM canonical_values cv WHERE cv.entity_id = w.id AND cv.key = 'episode_description' LIMIT 1),
                       (SELECT NULLIF(CAST(cv.value AS TEXT), '') FROM canonical_values cv WHERE cv.entity_id = ma.id AND cv.key = 'episode_description' LIMIT 1),
                       (SELECT NULLIF(CAST(claim_value AS TEXT), '') FROM metadata_claims WHERE entity_id = w.id AND claim_key IN ('issue_description', 'issue_overview') AND NULLIF(CAST(claim_value AS TEXT), '') IS NOT NULL ORDER BY confidence DESC, claimed_at DESC LIMIT 1),
                       (SELECT NULLIF(CAST(claim_value AS TEXT), '') FROM metadata_claims WHERE entity_id = ma.id AND claim_key IN ('issue_description', 'issue_overview') AND NULLIF(CAST(claim_value AS TEXT), '') IS NOT NULL ORDER BY confidence DESC, claimed_at DESC LIMIT 1),
                       (SELECT NULLIF(CAST(claim_value AS TEXT), '') FROM metadata_claims WHERE entity_id = w.id AND claim_key IN ('episode_description', 'episode_overview') AND NULLIF(CAST(claim_value AS TEXT), '') IS NOT NULL ORDER BY confidence DESC, claimed_at DESC LIMIT 1),
                       (SELECT NULLIF(CAST(claim_value AS TEXT), '') FROM metadata_claims WHERE entity_id = ma.id AND claim_key IN ('episode_description', 'episode_overview') AND NULLIF(CAST(claim_value AS TEXT), '') IS NOT NULL ORDER BY confidence DESC, claimed_at DESC LIMIT 1),
                       (SELECT NULLIF(CAST(cv.value AS TEXT), '') FROM canonical_values cv WHERE cv.entity_id = ma.id AND cv.key = 'description' LIMIT 1),
                       (SELECT NULLIF(CAST(cv.value AS TEXT), '') FROM canonical_values cv WHERE cv.entity_id = ma.id AND cv.key = 'overview' LIMIT 1),
                       (SELECT NULLIF(CAST(cv.value AS TEXT), '') FROM canonical_values cv WHERE cv.entity_id = w.id AND cv.key = 'description' LIMIT 1),
                       (SELECT NULLIF(CAST(cv.value AS TEXT), '') FROM canonical_values cv WHERE cv.entity_id = w.id AND cv.key = 'overview' LIMIT 1)) AS TEXT) AS Description,
                   CAST(COALESCE(
                       (SELECT NULLIF(CAST(cv.value AS TEXT), '') FROM canonical_values cv WHERE cv.entity_id = ma.id AND cv.key = 'season_number' LIMIT 1),
                       (SELECT NULLIF(CAST(cv.value AS TEXT), '') FROM canonical_values cv WHERE cv.entity_id = w.id AND cv.key = 'season_number' LIMIT 1),
                       '') AS TEXT) AS Season,
                   CAST(COALESCE(
                       (SELECT NULLIF(CAST(cv.value AS TEXT), '') FROM canonical_values cv WHERE cv.entity_id = ma.id AND cv.key = 'episode_number' LIMIT 1),
                       (SELECT NULLIF(CAST(cv.value AS TEXT), '') FROM canonical_values cv WHERE cv.entity_id = w.id AND cv.key = 'episode_number' LIMIT 1),
                       '') AS TEXT) AS Episode,
                   CAST(COALESCE(
                       (SELECT NULLIF(CAST(cv.value AS TEXT), '') FROM canonical_values cv WHERE cv.entity_id = ma.id AND cv.key = 'track_number' LIMIT 1),
                       (SELECT NULLIF(CAST(cv.value AS TEXT), '') FROM canonical_values cv WHERE cv.entity_id = w.id AND cv.key = 'track_number' LIMIT 1),
                       '') AS TEXT) AS TrackNumber,
                   CAST(COALESCE(
                       (SELECT NULLIF(CAST(cv.value AS TEXT), '') FROM canonical_values cv WHERE cv.entity_id = ma.id AND cv.key = 'disc_number' LIMIT 1),
                       (SELECT NULLIF(CAST(cv.value AS TEXT), '') FROM canonical_values cv WHERE cv.entity_id = w.id AND cv.key = 'disc_number' LIMIT 1),
                       '') AS TEXT) AS DiscNumber,
                   CAST(COALESCE(
                       (SELECT NULLIF(CAST(cv.value AS TEXT), '') FROM canonical_values cv WHERE cv.entity_id = ma.id AND cv.key = 'runtime' LIMIT 1),
                       (SELECT NULLIF(CAST(cv.value AS TEXT), '') FROM canonical_values cv WHERE cv.entity_id = ma.id AND cv.key = 'duration' LIMIT 1),
                       (SELECT NULLIF(CAST(cv.value AS TEXT), '') FROM canonical_values cv WHERE cv.entity_id = w.id AND cv.key = 'runtime' LIMIT 1),
                       (SELECT NULLIF(CAST(cv.value AS TEXT), '') FROM canonical_values cv WHERE cv.entity_id = w.id AND cv.key = 'duration' LIMIT 1)) AS TEXT) AS Duration,
                   CAST({displayYearSql} AS TEXT) AS Year,
                   CAST(COALESCE(
                       (SELECT NULLIF(cva.value, '') FROM canonical_value_arrays cva WHERE cva.entity_id = ma.id AND cva.key IN ('artist', 'album_artist') ORDER BY cva.ordinal LIMIT 1),
                       (SELECT NULLIF(cva.value, '') FROM canonical_value_arrays cva WHERE cva.entity_id = w.id AND cva.key IN ('artist', 'album_artist') ORDER BY cva.ordinal LIMIT 1),
                       (SELECT NULLIF(cva.value, '') FROM canonical_value_arrays cva WHERE cva.entity_id = COALESCE(gp.id, p.id, w.id) AND cva.key IN ('artist', 'album_artist') ORDER BY cva.ordinal LIMIT 1)) AS TEXT) AS Artist,
                   CAST(COALESCE(
                       (SELECT NULLIF(CAST(cv.value AS TEXT), '') FROM canonical_values cv WHERE cv.entity_id = ma.id AND cv.key IN ('explicit', 'is_explicit') LIMIT 1),
                       (SELECT NULLIF(CAST(cv.value AS TEXT), '') FROM canonical_values cv WHERE cv.entity_id = w.id AND cv.key IN ('explicit', 'is_explicit') LIMIT 1)) AS TEXT) AS Explicit,
                   CAST(COALESCE(
                       (SELECT NULLIF(CAST(cv.value AS TEXT), '') FROM canonical_values cv WHERE cv.entity_id = ma.id AND cv.key IN ('quality', 'audio_quality') LIMIT 1),
                       (SELECT NULLIF(CAST(cv.value AS TEXT), '') FROM canonical_values cv WHERE cv.entity_id = w.id AND cv.key IN ('quality', 'audio_quality') LIMIT 1),
                       (SELECT NULLIF(CAST(cv.value AS TEXT), '') FROM canonical_values cv WHERE cv.entity_id = COALESCE(gp.id, p.id, w.id) AND cv.key IN ('quality', 'audio_quality') LIMIT 1)) AS TEXT) AS Quality,
                   CAST(COALESCE(
                       (SELECT NULLIF(CAST(cv.value AS TEXT), '') FROM canonical_values cv WHERE cv.entity_id = ma.id AND cv.key IN ('cover_url', 'cover') LIMIT 1),
                       (SELECT NULLIF(CAST(cv.value AS TEXT), '') FROM canonical_values cv WHERE cv.entity_id = ma.id AND cv.key IN ('poster_url', 'poster') LIMIT 1),
                       (SELECT NULLIF(CAST(cv.value AS TEXT), '') FROM canonical_values cv WHERE cv.entity_id = w.id AND cv.key IN ('cover_url', 'cover') LIMIT 1),
                       (SELECT NULLIF(CAST(cv.value AS TEXT), '') FROM canonical_values cv WHERE cv.entity_id = w.id AND cv.key IN ('poster_url', 'poster') LIMIT 1),
                       (SELECT NULLIF(CAST(mc.claim_value AS TEXT), '') FROM metadata_claims mc WHERE mc.entity_id = w.id AND mc.claim_key IN ('cover_url', 'cover', 'poster_url', 'poster') ORDER BY mc.confidence DESC, mc.claimed_at DESC LIMIT 1),
                       (SELECT NULLIF(CAST(cv.value AS TEXT), '') FROM canonical_values cv WHERE cv.entity_id = COALESCE(gp.id, p.id, w.id) AND cv.key IN ('cover_url', 'cover') LIMIT 1),
                       (SELECT NULLIF(CAST(cv.value AS TEXT), '') FROM canonical_values cv WHERE cv.entity_id = COALESCE(gp.id, p.id, w.id) AND cv.key IN ('poster_url', 'poster') LIMIT 1),
                       (SELECT NULLIF(CAST(mc.claim_value AS TEXT), '') FROM metadata_claims mc WHERE mc.entity_id = COALESCE(gp.id, p.id, w.id) AND mc.claim_key IN ('cover_url', 'cover', 'poster_url', 'poster') ORDER BY mc.confidence DESC, mc.claimed_at DESC LIMIT 1)) AS TEXT) AS ArtworkUrl,
                   CAST(COALESCE(
                       (SELECT NULLIF(CAST(cv.value AS TEXT), '') FROM canonical_values cv WHERE cv.entity_id = ma.id AND cv.key IN ('episode_still_url', 'episode_still') LIMIT 1),
                       (SELECT NULLIF(CAST(cv.value AS TEXT), '') FROM canonical_values cv WHERE cv.entity_id = w.id AND cv.key IN ('episode_still_url', 'episode_still') LIMIT 1),
                       (SELECT NULLIF(CAST(cv.value AS TEXT), '') FROM canonical_values cv WHERE cv.entity_id = ma.id AND cv.key IN ('background_url', 'background') LIMIT 1),
                       (SELECT NULLIF(CAST(cv.value AS TEXT), '') FROM canonical_values cv WHERE cv.entity_id = w.id AND cv.key IN ('background_url', 'background') LIMIT 1),
                       (SELECT NULLIF(CAST(cv.value AS TEXT), '') FROM canonical_values cv WHERE cv.entity_id = ma.id AND cv.key IN ('hero_url', 'hero') LIMIT 1),
                       (SELECT NULLIF(CAST(cv.value AS TEXT), '') FROM canonical_values cv WHERE cv.entity_id = w.id AND cv.key IN ('hero_url', 'hero') LIMIT 1),
                       (SELECT NULLIF(CAST(cv.value AS TEXT), '') FROM canonical_values cv WHERE cv.entity_id = ma.id AND cv.key IN ('banner_url', 'banner') LIMIT 1),
                       (SELECT NULLIF(CAST(cv.value AS TEXT), '') FROM canonical_values cv WHERE cv.entity_id = w.id AND cv.key IN ('banner_url', 'banner') LIMIT 1),
                       (SELECT NULLIF(CAST(cv.value AS TEXT), '') FROM canonical_values cv WHERE cv.entity_id = COALESCE(gp.id, p.id, w.id) AND cv.key IN ('background_url', 'background') LIMIT 1),
                       (SELECT NULLIF(CAST(cv.value AS TEXT), '') FROM canonical_values cv WHERE cv.entity_id = COALESCE(gp.id, p.id, w.id) AND cv.key IN ('hero_url', 'hero') LIMIT 1),
                       (SELECT NULLIF(CAST(cv.value AS TEXT), '') FROM canonical_values cv WHERE cv.entity_id = COALESCE(gp.id, p.id, w.id) AND cv.key IN ('banner_url', 'banner') LIMIT 1)) AS TEXT) AS BackgroundUrl,
                   CAST(COALESCE(
                       (SELECT NULLIF(CAST(cv.value AS TEXT), '') FROM canonical_values cv WHERE cv.entity_id = ma.id AND cv.key = 'cover_state' LIMIT 1),
                       (SELECT NULLIF(CAST(cv.value AS TEXT), '') FROM canonical_values cv WHERE cv.entity_id = w.id AND cv.key = 'cover_state' LIMIT 1),
                       (SELECT NULLIF(CAST(cv.value AS TEXT), '') FROM canonical_values cv WHERE cv.entity_id = COALESCE(gp.id, p.id, w.id) AND cv.key = 'cover_state' LIMIT 1)) AS TEXT) AS CoverState,
                   CAST(COALESCE(
                       (SELECT NULLIF(CAST(cv.value AS TEXT), '') FROM canonical_values cv WHERE cv.entity_id = ma.id AND cv.key = 'background_state' LIMIT 1),
                       (SELECT NULLIF(CAST(cv.value AS TEXT), '') FROM canonical_values cv WHERE cv.entity_id = w.id AND cv.key = 'background_state' LIMIT 1),
                       (SELECT NULLIF(CAST(cv.value AS TEXT), '') FROM canonical_values cv WHERE cv.entity_id = ma.id AND cv.key = 'hero_state' LIMIT 1),
                       (SELECT NULLIF(CAST(cv.value AS TEXT), '') FROM canonical_values cv WHERE cv.entity_id = w.id AND cv.key = 'hero_state' LIMIT 1),
                       (SELECT NULLIF(CAST(cv.value AS TEXT), '') FROM canonical_values cv WHERE cv.entity_id = ma.id AND cv.key = 'banner_state' LIMIT 1),
                       (SELECT NULLIF(CAST(cv.value AS TEXT), '') FROM canonical_values cv WHERE cv.entity_id = w.id AND cv.key = 'banner_state' LIMIT 1),
                       (SELECT NULLIF(CAST(cv.value AS TEXT), '') FROM canonical_values cv WHERE cv.entity_id = COALESCE(gp.id, p.id, w.id) AND cv.key = 'background_state' LIMIT 1),
                       (SELECT NULLIF(CAST(cv.value AS TEXT), '') FROM canonical_values cv WHERE cv.entity_id = COALESCE(gp.id, p.id, w.id) AND cv.key = 'hero_state' LIMIT 1),
                       (SELECT NULLIF(CAST(cv.value AS TEXT), '') FROM canonical_values cv WHERE cv.entity_id = COALESCE(gp.id, p.id, w.id) AND cv.key = 'banner_state' LIMIT 1)) AS TEXT) AS BackgroundState,
                   MAX(us.progress_pct) AS ProgressPercent,
                   MAX(us.extended_properties) AS SavedPlaybackTiming,
                   MAX(us.last_accessed) AS LastAccessed,
                   CASE WHEN MAX(ma.id) IS NULL THEN 0 ELSE 1 END AS HasAsset,
                   CAST(COALESCE(w.ownership, 'Owned') AS TEXT) AS Ownership,
                   COALESCE(w.is_catalog_only, 0) AS IsCatalogOnly
            FROM works w
            LEFT JOIN works p ON p.id = w.parent_work_id
            LEFT JOIN works gp ON gp.id = p.parent_work_id
            LEFT JOIN collection_items ci ON ci.work_id = w.id AND ci.collection_id = @collectionId
            LEFT JOIN media_assets ma ON ma.id = (
                SELECT candidate.id FROM media_assets candidate
                JOIN work_owned_assets woa ON woa.asset_id=candidate.id
                LEFT JOIN user_states progress ON progress.asset_id=candidate.id AND progress.user_id=@defaultOwnerUserId
                WHERE woa.work_id=w.id
                  AND candidate.status='Normal'
                  AND candidate.is_orphaned=0
                  AND (@restrictAssets = 0 OR candidate.id IN @authorizedAssetIds)
                ORDER BY progress.last_accessed DESC, candidate.id LIMIT 1
            )
            LEFT JOIN user_states us ON us.asset_id = ma.id
                                    AND us.user_id = @defaultOwnerUserId
            WHERE w.collection_id = @collectionId
               OR ci.collection_id = @collectionId
               OR w.id IN @resolvedWorkIds
               OR (
                   @rootWorkId IS NOT NULL
                   AND (
                       p.parent_work_id = @rootWorkId
                       OR (
                           w.parent_work_id = @rootWorkId
                           AND EXISTS (
                               SELECT 1
                               FROM canonical_values child_marker
                               WHERE child_marker.entity_id = w.id
                                 AND child_marker.key IN ('episode_number', 'track_number')
                                 AND NULLIF(CAST(child_marker.value AS TEXT), '') IS NOT NULL
                           )
                       )
                   )
               )
            GROUP BY w.id
            ORDER BY COALESCE(ci.sort_order, 9999), CAST(NULLIF(Season, '') AS INTEGER), CAST(NULLIF(Episode, '') AS INTEGER), CAST(NULLIF(TrackNumber, '') AS INTEGER), COALESCE(w.ordinal, 9999), Title;
            """,
            new
            {
                collectionId = GuidSql.ToBlob(collectionId),
                rootWorkId = rootWorkId.HasValue ? GuidSql.ToBlob(rootWorkId.Value) : null,
                defaultOwnerUserId = GuidSql.ToBlob(profileId ?? DefaultOwnerUserId),
                resolvedWorkIds = resolvedWorkIds is { Count: > 0 }
                    ? resolvedWorkIds.Select(GuidSql.ToBlob).ToArray()
                    : [GuidSql.ToBlob(Guid.Empty)],
                restrictAssets = authorizedAssetIds is null ? 0 : 1,
                authorizedAssetIds = authorizedAssetIds is { Count: > 0 }
                    ? authorizedAssetIds.Select(GuidSql.ToBlob).ToArray()
                    : [GuidSql.ToBlob(Guid.Empty)],
            },
            cancellationToken: ct));
        var works = rawRows.Select(row => new CollectionWorkSummary(
            StringValue(row.Id) ?? string.Empty,
            StringValue(row.MediaType) ?? string.Empty,
            IntValue(row.Ordinal),
            StringHelpers.FirstNonBlankOr(string.Empty,
                ResolveDisplayTitleOverride(
                    (string?)StringValue(row.WorkDisplayOverridesJson),
                    InferMediaItemEntityType(StringValue(row.MediaType) ?? string.Empty, StringValue(row.Episode))),
                StringValue(row.Title),
                "Untitled"),
            StringValue(row.Description),
            StringValue(row.Season),
            StringValue(row.Episode),
            StringValue(row.TrackNumber),
            IntValue(row.DiscNumber),
            StringValue(row.Duration),
            StringValue(row.Year),
            StringValue(row.Artist),
            IsTruthy(StringValue(row.Explicit)),
            StringValue(row.Quality),
            DoubleValue(row.ProgressPercent),
            IsTruthy(StringValue(row.HasAsset)),
            StringValue(row.Ownership),
            IsTruthy(StringValue(row.IsCatalogOnly)),
            ResolveOwnedCollectionCoverUrl(
                StringValue(row.ArtworkUrl),
                StringValue(row.AssetId),
                StringValue(row.CoverState)),
            ResolveCollectionArtworkUrl(StringValue(row.BackgroundUrl), StringValue(row.AssetId), "background", StringValue(row.BackgroundState)),
            StringValue(row.AssetId))
        {
            LastAccessed = StringValue(row.LastAccessed),
            PositionSeconds = SavedPlaybackTiming.Read((string?)StringValue(row.SavedPlaybackTiming)).PositionSeconds,
            DurationSeconds = SavedPlaybackTiming.Read((string?)StringValue(row.SavedPlaybackTiming)).DurationSeconds,
        }).ToList();

        // Dynamic collections can include an owned work before its edition and
        // asset rows have been linked into this query. The canonical work detail
        // still knows the correct title and provider artwork, so use that same
        // presentation instead of emitting a misleading generated placeholder.
        for (var index = 0; index < works.Count; index++)
        {
            var work = works[index];
            if (!string.IsNullOrWhiteSpace(work.ArtworkUrl)
                || !Guid.TryParse(work.Id, out var workId))
            {
                continue;
            }

            var detail = await _libraryItems.GetDetailAsync(workId, ct);
            if (detail is null)
            {
                continue;
            }

            var artworkFallback = await LoadWorkArtworkFallbackAsync(workId, ct);
            var workValues = await LoadWorkCanonicalMapAsync(workId, detail, ct);
            var managedArtworkUrl = await _reader.LoadManagedWorkCoverUrlAsync(
                workId,
                InferWorkEntityType(detail.MediaType, detail),
                StringHelpers.FirstNonBlankOr(string.Empty,
                    detail.CoverUrl,
                    GetValue(workValues, "cover_url"),
                    GetValue(workValues, "cover"),
                    GetValue(workValues, "poster_url"),
                    GetValue(workValues, "poster"),
                    artworkFallback.CoverUrl,
                    artworkFallback.SquareUrl),
                ct);
            works[index] = work with
            {
                Title = StringHelpers.FirstNonBlankOr(string.Empty, detail.Title, work.Title) ?? work.Title,
                Description = StringHelpers.FirstNonBlankOr(string.Empty, work.Description, detail.Description),
                Year = StringHelpers.FirstNonBlankOr(string.Empty, work.Year, detail.Year),
                ArtworkUrl = StringHelpers.FirstNonBlankOr(string.Empty,
                    managedArtworkUrl,
                    artworkFallback.CoverUrl,
                    artworkFallback.SquareUrl),
            };
        }

        if (resolvedWorkIds is not { Count: > 0 })
        {
            return works;
        }

        var resolvedOrder = resolvedWorkIds
            .Select((id, index) => (id, index))
            .ToDictionary(item => item.id.ToString("D"), item => item.index, StringComparer.OrdinalIgnoreCase);
        return works
            .Select(work => resolvedOrder.ContainsKey(work.Id)
                ? work with { HasAsset = true, IsCatalogOnly = false, Ownership = "Owned" }
                : work)
            .OrderBy(work => resolvedOrder.TryGetValue(work.Id, out var index) ? index : int.MaxValue)
            .ToList();
    }

    internal async Task<Dictionary<string, string>> LoadWorkDisplayOverridesAsync(Guid workId, CancellationToken ct)
    {
        using var conn = _db.CreateConnection();
        var json = await conn.ExecuteScalarAsync<string?>(new CommandDefinition(
            "SELECT display_overrides_json FROM works WHERE id = @workId LIMIT 1;",
            new { workId },
            cancellationToken: ct));

        return ParseDisplayOverrides(json);
    }

    internal static Dictionary<string, string> ParseDisplayOverrides(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return new(StringComparer.OrdinalIgnoreCase);
        }

        try
        {
            var parsed = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
            return parsed is null
                ? new(StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, string>(parsed, StringComparer.OrdinalIgnoreCase);
        }
        catch (JsonException)
        {
            return new(StringComparer.OrdinalIgnoreCase);
        }
    }

    private static string? ResolveDisplayTitleOverride(string? json, DetailEntityType entityType) =>
        ResolveDisplayTitleOverride(ParseDisplayOverrides(json), entityType);

    private static string? ResolveDisplayTitleOverride(IReadOnlyDictionary<string, string> overrides, DetailEntityType entityType)
    {
        _ = entityType;
        return overrides.TryGetValue("title", out var value) && !string.IsNullOrWhiteSpace(value)
            ? value.Trim()
            : null;
    }

    private async Task<Dictionary<string, string>> LoadCanonicalMapAsync(Guid entityId, CancellationToken ct)
    {
        using var conn = _db.CreateConnection();
        var entityIdBlob = GuidSql.ToBlob(entityId);
        var rows = await conn.QueryAsync<CanonicalPair>(new CommandDefinition(
            "SELECT key AS Key, value AS Value FROM canonical_values WHERE entity_id = @entityId ORDER BY key;",
            new { entityId = entityIdBlob },
            cancellationToken: ct));
        var arrayRows = await conn.QueryAsync<CanonicalPair>(new CommandDefinition(
            "SELECT key AS Key, value AS Value FROM canonical_value_arrays WHERE entity_id = @entityId ORDER BY key, ordinal;",
            new { entityId = entityIdBlob },
            cancellationToken: ct));
        return BuildCanonicalMap(rows, arrayRows);
    }

    /// <summary>
    /// Scalar rows win by first-seen key; multi-valued rows (already ordered by key, ordinal) replace the
    /// scalar entry as one packed value. Shared by the single-entity and batched readers so both agree.
    /// </summary>
    private static Dictionary<string, string> BuildCanonicalMap(
        IEnumerable<CanonicalPair> scalarRows,
        IEnumerable<CanonicalPair> arrayRows)
    {
        var values = scalarRows.GroupBy(r => r.Key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().Value, StringComparer.OrdinalIgnoreCase);
        foreach (var group in arrayRows.GroupBy(row => row.Key, StringComparer.OrdinalIgnoreCase))
        {
            values[group.Key] = string.Join('|', group.Select(row => row.Value).Where(value => !string.IsNullOrWhiteSpace(value)));
        }

        return values;
    }

    private async Task<Dictionary<string, string>> LoadWorkCanonicalMapAsync(
        Guid workId,
        LibraryItemDetail detail,
        CancellationToken ct,
        IReadOnlyList<Guid>? authorizedAssetIds = null)
    {
        var values = await LoadWorkAndAssetCanonicalMapAsync(workId, ct, authorizedAssetIds);
        foreach (var canonical in detail.CanonicalValues)
        {
            // Repository detail values are edition/asset-oriented. They fill gaps,
            // but must not replace the shared work identity loaded above.
            values.TryAdd(canonical.Key, canonical.Value);
        }

        return values;
    }

    internal async Task<Dictionary<string, string>> LoadWorkAndAssetCanonicalMapAsync(
        Guid workId,
        CancellationToken ct,
        IReadOnlyList<Guid>? authorizedAssetIds = null)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        using var conn = _db.CreateConnection();
        var assetIds = await conn.QueryAsync<Guid>(new CommandDefinition(
            """
            SELECT ma.id
            FROM media_assets ma
            INNER JOIN editions e ON e.id = ma.edition_id
            WHERE e.work_id = @workId
              AND ma.status = 'Normal'
              AND (@filterAssets=0 OR ma.id IN @authorizedAssetIds)
            ORDER BY ma.id;
            """,
            new
            {
                workId = GuidSql.ToBlob(workId),
                filterAssets = authorizedAssetIds is null ? 0 : 1,
                authorizedAssetIds = authorizedAssetIds is { Count: > 0 }
                    ? authorizedAssetIds.Select(GuidSql.ToBlob).ToArray()
                    : [GuidSql.ToBlob(Guid.Empty)],
            },
            cancellationToken: ct));
        foreach (var assetId in assetIds.Distinct())
        {
            foreach (var (key, value) in await LoadCanonicalMapAsync(assetId, ct))
            {
                values.TryAdd(key, value);
            }

            await AddTechnicalClaimFallbacksAsync(conn, assetId, values, ct);
        }

        var editionSubtitle = await conn.ExecuteScalarAsync<string?>(new CommandDefinition(
            """
            SELECT NULLIF(CAST(cv.value AS TEXT), '')
            FROM editions e
            INNER JOIN canonical_values cv ON cv.entity_id = e.id
            WHERE e.work_id = @workId AND cv.key = @subtitle
            ORDER BY e.id
            LIMIT 1;
            """,
            new { workId = GuidSql.ToBlob(workId), subtitle = MetadataFieldConstants.Subtitle },
            cancellationToken: ct));
        if (!string.IsNullOrWhiteSpace(editionSubtitle))
        {
            values[$"edition_{MetadataFieldConstants.Subtitle}"] = editionSubtitle;
        }

        foreach (var (key, value) in await LoadCanonicalMapAsync(workId, ct))
        {
            values[key] = value;
        }
        await AddTechnicalClaimFallbacksAsync(conn, workId, values, ct);

        return values;
    }

    private static async Task AddTechnicalClaimFallbacksAsync(
        System.Data.IDbConnection conn,
        Guid entityId,
        IDictionary<string, string> values,
        CancellationToken ct)
    {
        var rows = await conn.QueryAsync<CanonicalPair>(new CommandDefinition(
            """
            SELECT claim_key AS Key, claim_value AS Value
            FROM metadata_claims
            WHERE entity_id = @entityId
              AND claim_key IN ('duration_sec', 'duration_seconds', 'genre')
              AND NULLIF(CAST(claim_value AS TEXT), '') IS NOT NULL
            ORDER BY confidence DESC, claimed_at DESC, id;
            """,
            new { entityId = GuidSql.ToBlob(entityId) },
            cancellationToken: ct));
        ApplyTechnicalClaimFallbacks(rows, values);
    }

    /// <summary>
    /// Fills gaps from provider claims (ordered best first): durations only when missing, genres merged.
    /// Shared by the single-entity and batched readers so both agree.
    /// </summary>
    private static void ApplyTechnicalClaimFallbacks(IEnumerable<CanonicalPair> rows, IDictionary<string, string> values)
    {
        foreach (var row in rows)
        {
            if (!string.Equals(row.Key, MetadataFieldConstants.Genre, StringComparison.OrdinalIgnoreCase))
            {
                values.TryAdd(row.Key, row.Value);
                continue;
            }

            values.TryGetValue(MetadataFieldConstants.Genre, out var existingGenres);
            var genres = SplitMetadataValues(existingGenres)
                .Concat(SplitMetadataValues(row.Value))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (genres.Count > 0)
            {
                values[MetadataFieldConstants.Genre] = string.Join('|', genres);
            }
        }
    }

    private async Task<Guid?> LoadCollectionRootWorkIdAsync(
        Guid collectionId,
        bool requireRootWithChildren,
        CancellationToken ct)
    {
        using var conn = _db.CreateConnection();
        var rootValue = await conn.ExecuteScalarAsync<object?>(new CommandDefinition(
            """
            SELECT COALESCE(gp.id, p.id, w.id)
            FROM works w
            LEFT JOIN works p ON p.id = w.parent_work_id
            LEFT JOIN works gp ON gp.id = p.parent_work_id
            WHERE w.collection_id = @collectionId
              AND (
                    @requireRootWithChildren = 0
                 OR EXISTS (
                        SELECT 1
                        FROM works child
                        WHERE child.parent_work_id = COALESCE(gp.id, p.id, w.id)
                    )
              )
            ORDER BY COALESCE(w.ordinal, 9999), w.id
            LIMIT 1;
            """,
            new
            {
                collectionId = GuidSql.ToBlob(collectionId),
                requireRootWithChildren = requireRootWithChildren ? 1 : 0,
            },
            cancellationToken: ct));

        var rootId = StringValue(rootValue);
        return Guid.TryParse(rootId, out var rootGuid) ? rootGuid : null;
    }

    private static Dictionary<string, string> MergeCanonicalMaps(
        IReadOnlyDictionary<string, string> primary,
        IReadOnlyDictionary<string, string> fallback)
    {
        var merged = new Dictionary<string, string>(fallback, StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in primary)
        {
            merged[key] = value;
        }

        return merged;
    }

    private static DescriptionSelection ResolveLongDescription(
        LibraryItemDetail detail,
        IReadOnlyDictionary<string, string> canonicalValues,
        DetailEntityType entityType)
    {
        if (entityType == DetailEntityType.TvEpisode)
        {
            return FirstSelectedText(
                (MetadataFieldConstants.EpisodeDescription, GetValue(canonicalValues, MetadataFieldConstants.EpisodeDescription)));
        }

        if (entityType == DetailEntityType.ComicIssue)
        {
            var issueDescription = FirstSelectedText(
                (MetadataFieldConstants.IssueDescription, GetValue(canonicalValues, MetadataFieldConstants.IssueDescription)),
                ("issue_overview", GetValue(canonicalValues, "issue_overview")));
            if (!string.IsNullOrWhiteSpace(issueDescription.Text))
            {
                return issueDescription;
            }

            return new DescriptionSelection(
                BuildComicIssueFallbackDescription(detail, canonicalValues),
                SourceKey: null,
                IsGeneratedFallback: true);
        }

        return FirstSelectedText(
            (MetadataFieldConstants.Description, GetValue(canonicalValues, MetadataFieldConstants.Description)),
            ("overview", GetValue(canonicalValues, "overview")),
            ("plot_summary", GetValue(canonicalValues, "plot_summary")),
            (MetadataFieldConstants.Description, detail.Description));
    }

    private static DescriptionSelection FirstSelectedText(params (string Key, string? Text)[] values)
    {
        foreach (var (key, text) in values)
        {
            var normalized = FirstText(text);
            if (!string.IsNullOrWhiteSpace(normalized))
            {
                return new DescriptionSelection(normalized, key, IsGeneratedFallback: false);
            }
        }

        return new DescriptionSelection(null, null, IsGeneratedFallback: false);
    }

    private static string? BuildComicIssueFallbackDescription(
        LibraryItemDetail detail,
        IReadOnlyDictionary<string, string> values)
    {
        var issueNumber = StringHelpers.FirstNonBlankOr(string.Empty,
            GetValue(values, MetadataFieldConstants.IssueNumber),
            detail.SeriesPosition,
            GetValue(values, MetadataFieldConstants.SeriesPosition));
        var series = StringHelpers.FirstNonBlankOr(string.Empty, detail.Series, GetValue(values, MetadataFieldConstants.Series));

        if (!string.IsNullOrWhiteSpace(issueNumber) && !string.IsNullOrWhiteSpace(series))
        {
            return $"{FormatIssue(issueNumber)} in {series}";
        }

        return StringHelpers.FirstNonBlankOr(string.Empty, FormatIssue(issueNumber), string.IsNullOrWhiteSpace(series) ? null : $"Issue in {series}");
    }

    private static string? BuildHeroSummary(IReadOnlyDictionary<string, string> canonicalValues)
        => NormalizeHeroSummary(StringHelpers.FirstNonBlankOr(string.Empty,
            GetValue(canonicalValues, MetadataFieldConstants.ShortDescription),
            GetValue(canonicalValues, "tldr")));

    private static SecondaryTitleSelection ResolveSecondaryTitleText(
        DetailEntityType entityType,
        IReadOnlyDictionary<string, string> canonicalValues,
        string? explicitTagline,
        string? explicitSubtitle,
        string? longDescription)
    {
        string? semanticText = entityType switch
        {
            DetailEntityType.Movie or DetailEntityType.TvShow => explicitTagline,
            DetailEntityType.Book or DetailEntityType.ComicIssue or DetailEntityType.Work => explicitSubtitle,
            DetailEntityType.Audiobook => StringHelpers.FirstNonBlank(
                GetValue(canonicalValues, $"edition_{MetadataFieldConstants.Subtitle}"),
                explicitSubtitle),
            _ => null,
        };
        if (!string.IsNullOrWhiteSpace(semanticText))
        {
            var kind = entityType is DetailEntityType.Movie or DetailEntityType.TvShow ? "tagline" : "subtitle";
            return new SecondaryTitleSelection(semanticText.Trim(), kind, HasMore: false);
        }

        if (entityType == DetailEntityType.MusicAlbum)
        {
            return new SecondaryTitleSelection(null, null, HasMore: false);
        }

        var shortDescription = NormalizeHeroSummary(StringHelpers.FirstNonBlankOr(string.Empty,
            GetValue(canonicalValues, MetadataFieldConstants.ShortDescription),
            GetValue(canonicalValues, "tldr")));
        if (!string.IsNullOrWhiteSpace(shortDescription))
        {
            return new SecondaryTitleSelection(
                shortDescription,
                MetadataFieldConstants.ShortDescription,
                HasMore: !string.IsNullOrWhiteSpace(longDescription)
                    && !string.Equals(shortDescription, longDescription, StringComparison.OrdinalIgnoreCase));
        }

        if (string.IsNullOrWhiteSpace(longDescription))
        {
            return new SecondaryTitleSelection(null, null, HasMore: false);
        }

        const int maximumLength = 240;
        var normalized = longDescription.Trim();
        if (normalized.Length <= maximumLength)
        {
            return new SecondaryTitleSelection(normalized, MetadataFieldConstants.Description, HasMore: false);
        }

        var cut = normalized[..maximumLength];
        var wordEnd = cut.LastIndexOf(' ');
        return new SecondaryTitleSelection(
            $"{cut[..Math.Max(wordEnd, 1)].TrimEnd()}…",
            MetadataFieldConstants.Description,
            HasMore: true);
    }

    private sealed record SecondaryTitleSelection(string? Text, string? Kind, bool HasMore);

    private async Task<WorkArtworkFallback> LoadWorkArtworkFallbackAsync(Guid workId, CancellationToken ct)
    {
        using var conn = _db.CreateConnection();
        var row = await conn.QueryFirstOrDefaultAsync(new CommandDefinition(
            """
            WITH ranked_assets AS (
                SELECT
                    w.id AS WorkId,
                    COALESCE(gp.id, p.id, w.id) AS RootWorkId,
                    ma.id AS AssetId,
                    ROW_NUMBER() OVER (
                        PARTITION BY w.id
                        ORDER BY CASE WHEN mc.claimed_at IS NULL THEN 1 ELSE 0 END, mc.claimed_at ASC, ma.id
                    ) AS AssetRank
                FROM works w
                INNER JOIN editions e ON e.work_id = w.id
                INNER JOIN media_assets ma ON ma.edition_id = e.id
                LEFT JOIN metadata_claims mc ON mc.entity_id = ma.id
                LEFT JOIN works p ON p.id = w.parent_work_id
                LEFT JOIN works gp ON gp.id = p.parent_work_id
                WHERE w.id = @workId
            )
            SELECT
                AssetId,
                COALESCE(
                    (SELECT value FROM canonical_values WHERE entity_id = AssetId AND key IN ('cover_url', 'cover', 'poster_url', 'poster') LIMIT 1),
                    (SELECT value FROM canonical_values WHERE entity_id = WorkId AND key IN ('cover_url', 'cover', 'poster_url', 'poster') LIMIT 1),
                    (SELECT value FROM canonical_values WHERE entity_id = RootWorkId AND key IN ('cover_url', 'cover', 'poster_url', 'poster') LIMIT 1)) AS CoverUrl,
                COALESCE(
                    (SELECT value FROM canonical_values WHERE entity_id = AssetId AND key IN ('square_url', 'square') LIMIT 1),
                    (SELECT value FROM canonical_values WHERE entity_id = WorkId AND key IN ('square_url', 'square') LIMIT 1),
                    (SELECT value FROM canonical_values WHERE entity_id = RootWorkId AND key IN ('square_url', 'square') LIMIT 1)) AS SquareUrl,
                COALESCE(
                    (SELECT value FROM canonical_values WHERE entity_id = AssetId AND key IN ('episode_still_url', 'episode_still', 'still_url', 'still') LIMIT 1),
                    (SELECT value FROM canonical_values WHERE entity_id = WorkId AND key IN ('episode_still_url', 'episode_still', 'still_url', 'still') LIMIT 1),
                    (SELECT value FROM canonical_values WHERE entity_id = AssetId AND key IN ('background_url', 'background') LIMIT 1),
                    (SELECT value FROM canonical_values WHERE entity_id = WorkId AND key IN ('background_url', 'background') LIMIT 1),
                    (SELECT value FROM canonical_values WHERE entity_id = RootWorkId AND key IN ('background_url', 'background') LIMIT 1)) AS BackgroundUrl,
                COALESCE(
                    (SELECT value FROM canonical_values WHERE entity_id = AssetId AND key IN ('banner_url', 'banner') LIMIT 1),
                    (SELECT value FROM canonical_values WHERE entity_id = WorkId AND key IN ('banner_url', 'banner') LIMIT 1),
                    (SELECT value FROM canonical_values WHERE entity_id = RootWorkId AND key IN ('banner_url', 'banner') LIMIT 1)) AS BannerUrl,
                COALESCE((SELECT value FROM canonical_values WHERE entity_id = AssetId AND key = 'cover_state' LIMIT 1),
                         (SELECT value FROM canonical_values WHERE entity_id = WorkId AND key = 'cover_state' LIMIT 1),
                         (SELECT value FROM canonical_values WHERE entity_id = RootWorkId AND key = 'cover_state' LIMIT 1)) AS CoverState,
                COALESCE((SELECT value FROM canonical_values WHERE entity_id = AssetId AND key = 'square_state' LIMIT 1),
                         (SELECT value FROM canonical_values WHERE entity_id = WorkId AND key = 'square_state' LIMIT 1),
                         (SELECT value FROM canonical_values WHERE entity_id = RootWorkId AND key = 'square_state' LIMIT 1)) AS SquareState,
                COALESCE((SELECT value FROM canonical_values WHERE entity_id = AssetId AND key = 'background_state' LIMIT 1),
                         (SELECT value FROM canonical_values WHERE entity_id = WorkId AND key = 'background_state' LIMIT 1),
                         (SELECT value FROM canonical_values WHERE entity_id = RootWorkId AND key = 'background_state' LIMIT 1)) AS BackgroundState,
                COALESCE((SELECT value FROM canonical_values WHERE entity_id = AssetId AND key = 'banner_state' LIMIT 1),
                         (SELECT value FROM canonical_values WHERE entity_id = WorkId AND key = 'banner_state' LIMIT 1),
                         (SELECT value FROM canonical_values WHERE entity_id = RootWorkId AND key = 'banner_state' LIMIT 1)) AS BannerState
            FROM ranked_assets
            WHERE AssetRank = 1
            LIMIT 1;
            """,
            new { workId },
            cancellationToken: ct));

        if (row is null)
        {
            return new WorkArtworkFallback();
        }

        var assetIdValue = StringValue(row.AssetId);
        if (!Guid.TryParse(assetIdValue, out Guid assetId))
        {
            return new WorkArtworkFallback();
        }

        return new WorkArtworkFallback
        {
            CoverUrl = DisplayArtworkUrlResolver.Resolve(StringValue(row.CoverUrl), assetId, "cover", StringValue(row.CoverState)),
            SquareUrl = DisplayArtworkUrlResolver.Resolve(StringValue(row.SquareUrl), assetId, "square", StringValue(row.SquareState)),
            BackgroundUrl = DisplayArtworkUrlResolver.Resolve(StringValue(row.BackgroundUrl), assetId, "background", StringValue(row.BackgroundState)),
            BannerUrl = DisplayArtworkUrlResolver.Resolve(StringValue(row.BannerUrl), assetId, "banner", StringValue(row.BannerState)),
        };
    }

}
