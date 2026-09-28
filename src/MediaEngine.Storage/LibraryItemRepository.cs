using Dapper;
using MediaEngine.Domain;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Enums;
using MediaEngine.Domain.Models;
using MediaEngine.Domain.Services;
using MediaEngine.Storage.Contracts;
using Microsoft.Data.Sqlite;

namespace MediaEngine.Storage;

/// <summary>
/// SQLite implementation of <see cref="ILibraryItemRepository"/>.
/// </summary>
public sealed class LibraryItemRepository : ILibraryItemRepository
{
    private readonly IDatabaseConnection _db;

    public LibraryItemRepository(IDatabaseConnection db)
    {
        ArgumentNullException.ThrowIfNull(db);
        _db = db;
    }

    public Task<LibraryItemsPage> GetPageAsync(LibraryItemQuery query, CancellationToken ct = default)
    {
        using var conn = _db.CreateConnection();

        var sql = BuildProjectionSql();
        var conditions = new List<string>();

        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            conditions.Add(@"(fd.entity_id IN (SELECT si.entity_id FROM search_index si WHERE search_index MATCH @ftsQuery) OR fd.file_name LIKE @search)");
        }

        if (!string.IsNullOrWhiteSpace(query.MediaType))
        {
            var types = query.MediaType.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (types.Length == 1)
            {
                conditions.Add("fd.media_type = @mediaType");
            }
            else if (types.Length > 1)
            {
                var placeholders = string.Join(", ", types.Select((_, i) => $"@mt{i}"));
                conditions.Add($"fd.media_type IN ({placeholders})");
            }
        }

        if (!string.IsNullOrWhiteSpace(query.Status))
        {
            if (query.Status == "Approved")
            {
                conditions.Add("fd.status IN ('Identified', 'Confirmed', 'RetailMatched', 'QidNoMatch', 'Edited')");
            }
            else
            {
                conditions.Add("fd.status = @status");
            }
        }
        else if (!query.IncludeAll)
        {
            conditions.Add("fd.library_visibility = 'visible'");
        }

        if (query.MinConfidence.HasValue)
        {
            conditions.Add("fd.confidence >= @minConfidence");
        }

        if (!string.IsNullOrWhiteSpace(query.MatchSource))
        {
            conditions.Add("fd.match_source = @matchSource");
        }

        if (query.DuplicatesOnly)
        {
            conditions.Add("fd.has_duplicate = 1");
        }

        if (query.MissingUniverseOnly)
        {
            conditions.Add("(fd.wikidata_status IN ('missing', 'manual') OR fd.status = 'QidNoMatch')");
        }

        if (query.MaxDays.HasValue)
        {
            conditions.Add($"fd.created_at >= datetime('now', '-{query.MaxDays.Value} days')");
        }

        var whereClause = conditions.Count > 0 ? "WHERE " + string.Join(" AND ", conditions) : "";
        var orderBy = query.Sort switch
        {
            "oldest" => "ORDER BY fd.created_at ASC, fd.title ASC",
            "title" => "ORDER BY fd.title ASC, fd.created_at DESC",
            "-title" => "ORDER BY fd.title DESC, fd.created_at DESC",
            "-confidence" => "ORDER BY fd.confidence DESC, fd.title ASC",
            "confidence" => "ORDER BY fd.confidence ASC, fd.title ASC",
            _ => "ORDER BY fd.created_at DESC, fd.title ASC",
        };

        using var countCmd = conn.CreateCommand();
        countCmd.CommandText = sql + $"SELECT COUNT(*) FROM full_data fd {whereClause};";
        AddParameters(countCmd, query);
        var totalCount = Convert.ToInt32(countCmd.ExecuteScalar() ?? 0);

        using var dataCmd = conn.CreateCommand();
        dataCmd.CommandText = sql + $"""
            SELECT
                fd.entity_id,
                fd.title,
                fd.year,
                fd.media_type,
                fd.cover_url,
                fd.background_url,
                fd.banner_url,
                fd.match_source,
                fd.confidence,
                fd.status,
                fd.has_duplicate,
                fd.review_id,
                fd.review_trigger,
                fd.has_user_locks,
                fd.file_name,
                fd.author,
                fd.file_path,
                fd.wikidata_status,
                fd.wikidata_match,
                fd.retail_match,
                fd.wikidata_qid,
                fd.hero_url,
                fd.created_at,
                fd.director,
                fd.artist,
                fd.retail_match_detail,
                fd.series,
                fd.series_position,
                fd.narrator,
                fd.genre,
                fd.runtime,
                fd.rating,
                fd.album,
                fd.track_number,
                fd.season_number,
                fd.episode_number,
                fd.show_name,
                fd.duration,
                fd.episode_title,
                fd.network,
                fd.top_cast,
                fd.qid_resolution_method,
                fd.pipeline_step,
                fd.library_visibility,
                fd.is_ready_for_library,
                fd.artwork_state,
                fd.artwork_source,
                fd.artwork_settled_at
            FROM full_data fd
            {whereClause}
            {orderBy}
            LIMIT @limit OFFSET @offset;
            """;
        AddParameters(dataCmd, query);
        dataCmd.Parameters.AddWithValue("@limit", query.Limit);
        dataCmd.Parameters.AddWithValue("@offset", query.Offset);

        var items = new List<LibraryCatalogItem>();
        using var reader = dataCmd.ExecuteReader();
        while (reader.Read())
        {
            items.Add(new LibraryCatalogItem
            {
                EntityId = GuidSql.FromDb(reader.GetValue(0)),
                Title = reader.GetString(1),
                Year = reader.IsDBNull(2) ? null : reader.GetString(2),
                MediaType = reader.IsDBNull(3) ? "" : reader.GetString(3),
                CoverUrl = reader.IsDBNull(4) ? null : reader.GetString(4),
                BackgroundUrl = reader.IsDBNull(5) ? null : reader.GetString(5),
                BannerUrl = reader.IsDBNull(6) ? null : reader.GetString(6),
                MatchSource = reader.IsDBNull(7) ? null : GuidSql.ToText(reader.GetValue(7)),
                Confidence = reader.IsDBNull(8) ? 0.0 : reader.GetDouble(8),
                Status = reader.IsDBNull(9) ? "Confirmed" : reader.GetString(9),
                HasDuplicate = !reader.IsDBNull(10) && reader.GetInt32(10) == 1,
                ReviewItemId = reader.IsDBNull(11) ? null : GuidSql.FromDb(reader.GetValue(11)),
                ReviewTrigger = reader.IsDBNull(12) ? null : reader.GetString(12),
                HasUserLocks = !reader.IsDBNull(13) && reader.GetInt32(13) == 1,
                FileName = reader.IsDBNull(14) ? null : reader.GetString(14),
                Author = reader.IsDBNull(15) ? null : reader.GetString(15),
                FilePath = reader.IsDBNull(16) ? null : reader.GetString(16),
                WikidataStatus = reader.IsDBNull(17) ? null : reader.GetString(17),
                WikidataMatch = reader.IsDBNull(18) ? "none" : reader.GetString(18),
                RetailMatch = reader.IsDBNull(19) ? "none" : reader.GetString(19),
                WikidataQid = reader.IsDBNull(20) ? null : reader.GetString(20),
                HeroUrl = reader.IsDBNull(21) ? null : reader.GetString(21),
                CreatedAt = reader.IsDBNull(22) ? DateTimeOffset.MinValue : ParseDateTimeOffset(reader.GetString(22)) ?? DateTimeOffset.MinValue,
                Director = reader.IsDBNull(23) ? null : reader.GetString(23),
                Artist = reader.IsDBNull(24) ? null : reader.GetString(24),
                RetailMatchDetail = reader.IsDBNull(25) ? null : reader.GetString(25),
                Series = reader.IsDBNull(26) ? null : reader.GetString(26),
                SeriesPosition = reader.IsDBNull(27) ? null : reader.GetString(27),
                Narrator = reader.IsDBNull(28) ? null : reader.GetString(28),
                Genre = reader.IsDBNull(29) ? null : reader.GetString(29),
                Runtime = reader.IsDBNull(30) ? null : reader.GetString(30),
                Rating = reader.IsDBNull(31) ? null : reader.GetString(31),
                Album = reader.IsDBNull(32) ? null : reader.GetString(32),
                TrackNumber = reader.IsDBNull(33) ? null : reader.GetString(33),
                SeasonNumber = reader.IsDBNull(34) ? null : reader.GetString(34),
                EpisodeNumber = reader.IsDBNull(35) ? null : reader.GetString(35),
                ShowName = reader.IsDBNull(36) ? null : reader.GetString(36),
                Duration = reader.IsDBNull(37) ? null : reader.GetString(37),
                EpisodeTitle = reader.IsDBNull(38) ? null : reader.GetString(38),
                Network = reader.IsDBNull(39) ? null : reader.GetString(39),
                TopCast = reader.IsDBNull(40) ? null : reader.GetString(40),
                QidResolutionMethod = reader.IsDBNull(41) ? null : reader.GetString(41),
                PipelineStep = reader.IsDBNull(42) ? "Retail" : reader.GetString(42),
                LibraryVisibility = reader.IsDBNull(43) ? "hidden" : reader.GetString(43),
                IsReadyForLibrary = !reader.IsDBNull(44) && reader.GetInt32(44) == 1,
                ArtworkState = reader.IsDBNull(45) ? "pending" : reader.GetString(45),
                ArtworkSource = reader.IsDBNull(46) ? null : reader.GetString(46),
                ArtworkSettledAt = reader.IsDBNull(47) ? null : ParseDateTimeOffset(reader.GetString(47)),
            });
        }

        return Task.FromResult(new LibraryItemsPage(items, totalCount, query.Offset + items.Count < totalCount));
    }

    public Task<LibraryItemDetail?> GetDetailAsync(Guid entityId, CancellationToken ct = default) =>
        GetDetailCoreAsync(entityId, preferredAssetId: null, ct);

    public Task<LibraryItemDetail?> GetDetailAsync(
        Guid entityId,
        Guid preferredAssetId,
        CancellationToken ct = default) =>
        preferredAssetId == Guid.Empty
            ? Task.FromResult<LibraryItemDetail?>(null)
            : GetDetailCoreAsync(entityId, preferredAssetId, ct);

    private Task<LibraryItemDetail?> GetDetailCoreAsync(
        Guid entityId,
        Guid? preferredAssetId,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        using var conn = _db.CreateConnection();
        entityId = WorkRedirects.Resolve(conn, entityId);

        if (preferredAssetId.HasValue)
        {
            var belongsToWork = conn.ExecuteScalar<long>(
                """
                SELECT COUNT(*)
                FROM media_assets ma
                JOIN editions e ON e.id=ma.edition_id
                WHERE ma.id=@preferredAssetId AND e.work_id=@entityId
                  AND ma.status='Normal' AND ma.is_orphaned=0;
                """,
                new { entityId, preferredAssetId = preferredAssetId.Value });
            if (belongsToWork != 1)
            {
                return Task.FromResult<LibraryItemDetail?>(null);
            }
        }

        var projection = conn.QueryFirstOrDefault<ProjectionRow>(
            BuildProjectionSql(preferredAssetId.HasValue) + """
            SELECT
                fd.entity_id             AS EntityId,
                fd.title                 AS Title,
                fd.year                  AS Year,
                fd.media_type            AS MediaType,
                fd.cover_url             AS CoverUrl,
                fd.background_url        AS BackgroundUrl,
                fd.banner_url            AS BannerUrl,
                fd.hero_url              AS HeroUrl,
                fd.confidence            AS Confidence,
                fd.status                AS Status,
                fd.match_source          AS MatchSource,
                fd.author                AS Author,
                fd.director              AS Director,
                fd.artist                AS Artist,
                fd.genre                 AS Genre,
                fd.runtime               AS Runtime,
                fd.description           AS Description,
                fd.series                AS Series,
                fd.series_position       AS SeriesPosition,
                fd.show_name             AS ShowName,
                fd.season_number         AS SeasonNumber,
                fd.episode_number        AS EpisodeNumber,
                fd.episode_title         AS EpisodeTitle,
                fd.narrator              AS Narrator,
                fd.rating                AS Rating,
                fd.wikidata_qid          AS WikidataQid,
                fd.wikidata_status       AS WikidataStatus,
                fd.file_name             AS FileName,
                fd.file_path             AS FilePath,
                fd.review_id             AS ReviewId,
                fd.review_trigger        AS ReviewTrigger,
                fd.candidates_json       AS CandidatesJson,
                fd.has_user_locks        AS HasUserLocks,
                fd.pipeline_step         AS PipelineStep,
                fd.library_visibility      AS LibraryVisibility,
                fd.is_ready_for_library    AS IsReadyForLibrary,
                fd.artwork_state         AS ArtworkState,
                fd.artwork_source        AS ArtworkSource,
                fd.artwork_settled_at    AS ArtworkSettledAt,
                fd.qid_resolution_method AS QidResolutionMethod
            FROM full_data fd
            WHERE fd.entity_id = @entityId;
            """,
            new { entityId, preferredAssetId });

        var lineageRow = conn.QueryFirstOrDefault<(Guid? AssetId, Guid RootParentWorkId, string MediaType)>("""
            WITH RECURSIVE work_tree(id, depth) AS (
                SELECT w.id, 0
                FROM works w
                WHERE w.id = @entityId
                UNION ALL
                SELECT child.id, work_tree.depth + 1
                FROM works child
                INNER JOIN work_tree ON child.parent_work_id = work_tree.id
            ),
            representative_asset AS (
                SELECT ma.id AS AssetId
                FROM work_tree
                INNER JOIN editions e ON e.work_id = work_tree.id
                INNER JOIN media_assets ma ON ma.edition_id = e.id
                ORDER BY CASE WHEN ma.id=@preferredAssetId THEN 0 ELSE 1 END,
                         work_tree.depth,
                         CASE WHEN LOWER(ma.file_path_root) LIKE '%.epub' THEN 0 ELSE 1 END,
                         ma.id
                LIMIT 1
            )
            SELECT (SELECT AssetId FROM representative_asset) AS AssetId,
                   COALESCE(gp.id, p.id, w.id) AS RootParentWorkId,
                   w.media_type AS MediaType
            FROM works w
            LEFT JOIN works p ON p.id = w.parent_work_id
            LEFT JOIN works gp ON gp.id = p.parent_work_id
            WHERE w.id = @entityId
            LIMIT 1;
            """, new { entityId, preferredAssetId });

        if (lineageRow == default)
        {
            return Task.FromResult<LibraryItemDetail?>(null);
        }

        var assetId = lineageRow.AssetId;
        var rootParentId = lineageRow.RootParentWorkId;

        var selfValues = conn.Query<(string Key, string Value, int IsConflicted, Guid? WinningProviderId, int NeedsReview, string LastScoredAt)>("""
            SELECT key AS Key,
                   value AS Value,
                   is_conflicted AS IsConflicted,
                   winning_provider_id AS WinningProviderId,
                   needs_review AS NeedsReview,
                   last_scored_at AS LastScoredAt
            FROM canonical_values
            WHERE entity_id = @assetId
            ORDER BY key;
            """, new { assetId })
            .Select(r => new LibraryItemCanonicalValue(
                Key: r.Key,
                Value: r.Value,
                IsConflicted: r.IsConflicted == 1,
                WinningProviderId: r.WinningProviderId?.ToString(),
                NeedsReview: r.NeedsReview == 1,
                LastScoredAt: ParseDateTimeOffset(r.LastScoredAt) ?? DateTimeOffset.MinValue))
            .ToList();

        var targetValues = conn.Query<(string Key, string Value, int IsConflicted, Guid? WinningProviderId, int NeedsReview, string LastScoredAt)>("""
            SELECT key AS Key,
                   value AS Value,
                   is_conflicted AS IsConflicted,
                   winning_provider_id AS WinningProviderId,
                   needs_review AS NeedsReview,
                   last_scored_at AS LastScoredAt
            FROM canonical_values
            WHERE entity_id = @entityId
            ORDER BY key;
            """, new { entityId })
            .Select(r => new LibraryItemCanonicalValue(
                Key: r.Key,
                Value: r.Value,
                IsConflicted: r.IsConflicted == 1,
                WinningProviderId: r.WinningProviderId?.ToString(),
                NeedsReview: r.NeedsReview == 1,
                LastScoredAt: ParseDateTimeOffset(r.LastScoredAt) ?? DateTimeOffset.MinValue))
            .ToList();

        var parentValues = conn.Query<(string Key, string Value, int IsConflicted, Guid? WinningProviderId, int NeedsReview, string LastScoredAt)>("""
            SELECT key AS Key,
                   value AS Value,
                   is_conflicted AS IsConflicted,
                   winning_provider_id AS WinningProviderId,
                   needs_review AS NeedsReview,
                   last_scored_at AS LastScoredAt
            FROM canonical_values
            WHERE entity_id = @parentId
            ORDER BY key;
            """, new { parentId = rootParentId })
            .Select(r => new LibraryItemCanonicalValue(
                Key: r.Key,
                Value: r.Value,
                IsConflicted: r.IsConflicted == 1,
                WinningProviderId: r.WinningProviderId?.ToString(),
                NeedsReview: r.NeedsReview == 1,
                LastScoredAt: ParseDateTimeOffset(r.LastScoredAt) ?? DateTimeOffset.MinValue))
            .ToList();

        var canonicalValues = parentValues
            .Where(p => !targetValues.Any(t => t.Key == p.Key) && !selfValues.Any(s => s.Key == p.Key))
            .Concat(targetValues.Where(t => !selfValues.Any(s => s.Key == t.Key)))
            .Concat(selfValues)
            .ToList();

        var claims = conn.Query<(Guid Id, string ClaimKey, string ClaimValue, Guid ProviderId, double Confidence, int IsUserLocked, string ClaimedAt)>("""
            SELECT id AS Id,
                   claim_key AS ClaimKey,
                   claim_value AS ClaimValue,
                   provider_id AS ProviderId,
                   confidence AS Confidence,
                   is_user_locked AS IsUserLocked,
                   claimed_at AS ClaimedAt
            FROM metadata_claims
            WHERE entity_id IN (@assetId, @entityId, @parentId)
            ORDER BY claimed_at DESC;
            """, new { assetId, entityId, parentId = rootParentId })
            .Select(r => new LibraryItemClaimRecord(
                Id: r.Id,
                ClaimKey: r.ClaimKey,
                ClaimValue: r.ClaimValue,
                ProviderId: r.ProviderId,
                Confidence: r.Confidence,
                IsUserLocked: r.IsUserLocked == 1,
                ClaimedAt: ParseDateTimeOffset(r.ClaimedAt) ?? DateTimeOffset.MinValue))
            .ToList();

        var providerNamesById = LoadProviderNamesById(conn);
        var retailProviderName = canonicalValues
            .FirstOrDefault(value => string.Equals(
                value.Key,
                MetadataFieldConstants.IdentityProvider,
                StringComparison.OrdinalIgnoreCase))?.Value;
        if (string.IsNullOrWhiteSpace(retailProviderName))
        {
            retailProviderName = ResolveRetailProviderName(canonicalValues, claims, providerNamesById);
        }

        var retailProviderItemId = canonicalValues
            .FirstOrDefault(value => string.Equals(
                value.Key,
                MetadataFieldConstants.IdentityProviderItemId,
                StringComparison.OrdinalIgnoreCase))?.Value;

        var rqRow = conn.QueryFirstOrDefault<(Guid Id, string Trigger, double? ConfidenceScore, string? Detail, string? CandidatesJson)>("""
            SELECT id AS Id,
                   trigger AS Trigger,
                   confidence_score AS ConfidenceScore,
                   detail AS Detail,
                   candidates_json AS CandidatesJson
            FROM review_queue
            WHERE (entity_id = @assetId OR entity_id = @entityId) AND status = 'Pending'
            ORDER BY created_at DESC
            LIMIT 1;
            """, new { assetId, entityId });

        var maRow = conn.QueryFirstOrDefault<(string? FilePath, string? ContentHash)>("""
            SELECT ma.file_path_root AS FilePath,
                   ma.content_hash AS ContentHash
            FROM editions e
            INNER JOIN media_assets ma ON ma.edition_id = e.id
            WHERE e.work_id = @entityId
            LIMIT 1;
            """, new { entityId });

        var workIdentity = conn.QueryFirstOrDefault<(string? MatchLevel, string? WorkKind)>(
            "SELECT match_level AS MatchLevel, work_kind AS WorkKind FROM works WHERE id = @entityId;",
            new { entityId });
        var matchLevel = workIdentity.MatchLevel ?? "work";

        string? Canonical(string key)
        {
            return canonicalValues
                .FirstOrDefault(v => v.Key.Equals(key, StringComparison.OrdinalIgnoreCase))
                ?.Value;
        }

        var bridgeKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            BridgeIdKeys.Isbn,
            BridgeIdKeys.Isbn13,
            BridgeIdKeys.Isbn10,
            BridgeIdKeys.Asin,
            BridgeIdKeys.TmdbId,
            BridgeIdKeys.TmdbEpisodeId,
            BridgeIdKeys.ImdbId,
            BridgeIdKeys.WikidataQid,
            BridgeIdKeys.AppleBooksId,
            BridgeIdKeys.AudibleId,
            BridgeIdKeys.GoodreadsId,
            BridgeIdKeys.MusicBrainzId,
            BridgeIdKeys.ComicVineId,
        };

        var bridgeIds = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var cvEntry in canonicalValues)
        {
            if (bridgeKeys.Contains(cvEntry.Key) && !string.IsNullOrWhiteSpace(cvEntry.Value))
            {
                bridgeIds[cvEntry.Key] = cvEntry.Value;
            }
        }

        if (!string.IsNullOrWhiteSpace(projection?.WikidataQid) && !bridgeIds.ContainsKey(BridgeIdKeys.WikidataQid))
        {
            bridgeIds[BridgeIdKeys.WikidataQid] = projection.WikidataQid;
        }

        var latestJobEntityId = assetId ?? entityId;
        var latestJobState = conn.QueryFirstOrDefault<string?>("""
            SELECT state
            FROM identity_jobs
            WHERE entity_id = @entityId
            ORDER BY updated_at DESC, created_at DESC
            LIMIT 1;
            """, new { entityId = latestJobEntityId });

        if (projection is null && canonicalValues.Count == 0)
        {
            return Task.FromResult<LibraryItemDetail?>(null);
        }

        // Detail reads must remain database-only. File inspection belongs to ingestion or the
        // playback inspection cache; starting ffprobe here made a normal page request wait on disk.
        var playbackSummary = BuildPlaybackSummary(Canonical, ct);

        Guid? reviewItemId = rqRow == default ? null : rqRow.Id;
        var universeQid = Canonical("fictional_universe_qid")
            ?? Canonical("franchise_qid")
            ?? Canonical("series_qid");
        var universeName = Canonical("fictional_universe")
            ?? Canonical("franchise")
            ?? Canonical("series");
        var narrativeRootQid = Canonical("fictional_universe_qid")
            ?? Canonical("franchise_qid")
            ?? Canonical("series_qid");
        var stage3EnrichedAt = ParseDateTimeOffset(Canonical("stage3_enriched_at"));
        var workQidForUniverse = projection?.WikidataQid ?? Canonical(BridgeIdKeys.WikidataQid);
        var linkedEntityCount = 0;
        var linkedRelationshipCount = 0;
        var linkedPortraitCount = 0;

        if (!string.IsNullOrWhiteSpace(workQidForUniverse))
        {
            linkedEntityCount = conn.QueryFirstOrDefault<int>("""
                SELECT COUNT(DISTINCT entity_id)
                FROM fictional_entity_work_links
                WHERE work_qid = @workQid;
                """, new { workQid = workQidForUniverse });

            linkedRelationshipCount = conn.QueryFirstOrDefault<int>("""
                WITH linked_qids AS (
                    SELECT fe.wikidata_qid
                    FROM fictional_entities fe
                    INNER JOIN fictional_entity_work_links fewl ON fewl.entity_id = fe.id
                    WHERE fewl.work_qid = @workQid
                )
                SELECT COUNT(*)
                FROM entity_relationships
                WHERE subject_qid IN (SELECT wikidata_qid FROM linked_qids)
                   OR object_qid IN (SELECT wikidata_qid FROM linked_qids);
                """, new { workQid = workQidForUniverse });

            linkedPortraitCount = conn.QueryFirstOrDefault<int>("""
                SELECT COUNT(DISTINCT cp.id)
                FROM character_portraits cp
                INNER JOIN fictional_entity_work_links fewl ON fewl.entity_id = cp.fictional_entity_id
                WHERE fewl.work_qid = @workQid;
                """, new { workQid = workQidForUniverse });
        }

        var hasUniverseLink = !string.IsNullOrWhiteSpace(universeQid);
        var hasStage3GraphEvidence = linkedEntityCount > 0
            || linkedRelationshipCount > 0
            || linkedPortraitCount > 0;

        var stage3Status = latestJobState switch
        {
            nameof(IdentityJobState.UniverseEnriching) => "UniverseEnriching",
            nameof(IdentityJobState.ReadyWithoutUniverse) => "ReadyWithoutUniverse",
            nameof(IdentityJobState.Failed) => "Failed",
            nameof(IdentityJobState.Ready)
                when !hasUniverseLink => "ReadyWithoutUniverse",
            nameof(IdentityJobState.Ready)
                when hasStage3GraphEvidence => "Ready",
            _ when stage3EnrichedAt.HasValue && hasStage3GraphEvidence => "Ready",
            _ when stage3EnrichedAt.HasValue && !hasUniverseLink => "ReadyWithoutUniverse",
            _ when hasUniverseLink => "Pending",
            _ => "Pending",
        };
        var universeSummary = new UniverseSummaryDto
        {
            UniverseStatus = hasUniverseLink
                ? "linked"
                : stage3Status == "ReadyWithoutUniverse" ? "not_applicable" : "unlinked",
            UniverseName = universeName,
            UniverseQid = universeQid,
            NarrativeRootQid = narrativeRootQid,
            Stage3Status = stage3Status,
            Stage3EnrichedAt = stage3EnrichedAt,
            EntityCount = linkedEntityCount,
            RelationshipCount = linkedRelationshipCount,
            PortraitCount = linkedPortraitCount,
        };
        var resolvedAuthor = ResolveAuthorForDetail(projection?.Author ?? Canonical("author"), canonicalValues, claims);
        var isTelevisionChild = string.Equals(lineageRow.MediaType, "TV", StringComparison.OrdinalIgnoreCase)
            && string.Equals(workIdentity.WorkKind, "child", StringComparison.OrdinalIgnoreCase);
        var resolvedDescription = isTelevisionChild
            ? Canonical("episode_description")
                ?? Canonical("description")
                ?? Canonical("overview")
                ?? Canonical("plot_summary")
                ?? projection?.Description
            : projection?.Description
                ?? Canonical("episode_description")
                ?? Canonical("description")
                ?? Canonical("overview")
                ?? Canonical("plot_summary");

        var resolvedMediaType = projection?.MediaType ?? lineageRow.MediaType ?? "";
        var explicitOriginalYear = MediaDateSemantics.ResolveExplicitOriginalYear(resolvedMediaType, Canonical);
        var relatedBookYear = string.Empty;
        if (resolvedMediaType.Contains("audio", StringComparison.OrdinalIgnoreCase)
            && explicitOriginalYear is null
            && assetId.HasValue)
        {
            var relatedBookYearSql = MediaDateSql.RelatedBookOriginalYear("@workId", "@assetId");
            relatedBookYear = conn.QueryFirstOrDefault<string?>(
                $"SELECT {relatedBookYearSql};",
                new { workId = entityId, assetId }) ?? string.Empty;
        }

        var originalYear = explicitOriginalYear
            ?? (!string.IsNullOrWhiteSpace(relatedBookYear) ? relatedBookYear : null)
            ?? MediaDateSemantics.ResolveOriginalYear(resolvedMediaType, Canonical)
            ?? projection?.Year;
        var originalDate = explicitOriginalYear is null && !string.IsNullOrWhiteSpace(relatedBookYear)
            ? relatedBookYear
            : MediaDateSemantics.ResolveOriginalDate(resolvedMediaType, Canonical);
        var detail = new LibraryItemDetail
        {
            EntityId = entityId,
            Title = projection?.Title ?? Canonical("title") ?? "Untitled",
            Year = originalYear ?? projection?.Year,
            MediaType = resolvedMediaType,
            CoverUrl = projection?.CoverUrl,
            BackgroundUrl = projection?.BackgroundUrl,
            BannerUrl = projection?.BannerUrl,
            HeroUrl = projection?.HeroUrl,
            Confidence = projection?.Confidence ?? (rqRow == default ? 0.0 : rqRow.ConfidenceScore ?? 0.0),
            Status = projection?.Status ?? "Confirmed",
            MatchSource = projection?.MatchSource?.ToString(),
            MatchMethod = projection?.PipelineStep,
            RetailProviderName = retailProviderName,
            RetailProviderItemId = !string.IsNullOrWhiteSpace(retailProviderItemId)
                ? retailProviderItemId
                : GetProviderBridgeId(bridgeIds, retailProviderName),
            Author = resolvedAuthor,
            Director = projection?.Director ?? Canonical("director"),
            Artist = projection?.Artist ?? Canonical("artist"),
            Album = Canonical("album"),
            Composer = Canonical("composer"),
            Illustrator = Canonical("illustrator"),
            Writer = Canonical("writer") ?? Canonical("screenwriter"),
            Cast = Canonical("cast"),
            Language = Canonical("language"),
            Genre = projection?.Genre ?? Canonical("genre"),
            Runtime = projection?.Runtime ?? Canonical("runtime"),
            Description = resolvedDescription,
            Tagline = Canonical("tagline") ?? Canonical("short_description"),
            Series = projection?.Series ?? Canonical("series"),
            SeriesPosition = projection?.SeriesPosition ?? Canonical("series_position"),
            ShowName = projection?.ShowName ?? Canonical("show_name"),
            SeasonNumber = projection?.SeasonNumber ?? Canonical("season_number"),
            EpisodeNumber = projection?.EpisodeNumber ?? Canonical("episode_number"),
            EpisodeTitle = projection?.EpisodeTitle ?? Canonical("episode_title"),
            ReleaseDate = NormalizeReleaseDate(originalDate),
            Narrator = projection?.Narrator ?? Canonical("narrator"),
            Rating = projection?.Rating ?? Canonical("rating"),
            WikidataQid = projection?.WikidataQid ?? Canonical(BridgeIdKeys.WikidataQid),
            PlaybackSummary = playbackSummary,
            WikidataStatus = projection?.WikidataStatus,
            FileName = projection?.FileName ?? (maRow == default ? null : Path.GetFileName(maRow.FilePath)),
            FilePath = projection?.FilePath ?? maRow.FilePath,
            ContentHash = maRow.ContentHash,
            ReviewItemId = reviewItemId,
            ReviewTrigger = projection?.ReviewTrigger ?? (rqRow == default ? null : rqRow.Trigger),
            ReviewDetail = rqRow == default ? null : rqRow.Detail,
            CandidatesJson = projection?.CandidatesJson ?? (rqRow == default ? null : rqRow.CandidatesJson),
            HasUserLocks = projection?.HasUserLocks ?? claims.Any(c => c.IsUserLocked),
            MatchLevel = matchLevel,
            CanonicalValues = canonicalValues,
            ClaimHistory = claims,
            BridgeIds = bridgeIds,
            PipelineStep = projection?.PipelineStep ?? "Retail",
            LibraryVisibility = projection?.LibraryVisibility ?? "hidden",
            IsReadyForLibrary = projection?.IsReadyForLibrary ?? false,
            ArtworkState = projection?.ArtworkState ?? "pending",
            ArtworkSource = projection?.ArtworkSource,
            ArtworkSettledAt = ParseDateTimeOffset(projection?.ArtworkSettledAt),
            UniverseSummary = universeSummary,
        };

        return Task.FromResult<LibraryItemDetail?>(detail);
    }

    public Task<LibraryItemStatusCounts> GetStatusCountsAsync(CancellationToken ct = default)
    {
        using var conn = _db.CreateConnection();
        var row = conn.QueryFirst<StatusCountsRow>(BuildProjectionSql() + """
            SELECT
                COUNT(*) AS Total,
                SUM(CASE WHEN fd.status = 'InReview' OR fd.library_visibility = 'review_only' THEN 1 ELSE 0 END) AS NeedsReview,
                SUM(CASE WHEN fd.status IN ('Identified', 'Confirmed', 'RetailMatched', 'QidNoMatch', 'Edited') AND fd.library_visibility = 'visible' THEN 1 ELSE 0 END) AS AutoApproved,
                SUM(CASE WHEN fd.status = 'Edited' THEN 1 ELSE 0 END) AS Edited,
                SUM(CASE WHEN fd.has_duplicate = 1 THEN 1 ELSE 0 END) AS Duplicate,
                SUM(CASE WHEN fd.library_visibility = 'hidden' THEN 1 ELSE 0 END) AS Staging,
                SUM(CASE WHEN fd.artwork_state != 'present' THEN 1 ELSE 0 END) AS MissingImages,
                SUM(CASE WHEN fd.artwork_settled_at >= datetime('now', '-24 hours') THEN 1 ELSE 0 END) AS RecentlyUpdated,
                SUM(CASE WHEN fd.confidence BETWEEN 0.40 AND 0.85 AND fd.library_visibility != 'review_only' THEN 1 ELSE 0 END) AS LowConfidence,
                SUM(CASE WHEN fd.status = 'Rejected' THEN 1 ELSE 0 END) AS Rejected
            FROM full_data fd;
            """);

        return Task.FromResult(new LibraryItemStatusCounts(
            row.Total,
            row.NeedsReview,
            row.AutoApproved,
            row.Edited,
            row.Duplicate,
            row.Staging,
            row.MissingImages,
            row.RecentlyUpdated,
            row.LowConfidence,
            row.Rejected));
    }

    public Task<LibraryItemProjectionSummary> GetProjectionSummaryAsync(CancellationToken ct = default)
    {
        using var conn = _db.CreateConnection();
        var staleThreshold = DateTimeOffset.UtcNow.AddDays(-30).ToString("o");
        var row = conn.QueryFirst<ProjectionSummaryRow>(BuildProjectionSql() + """
            SELECT
                COUNT(*) AS TotalItems,
                SUM(CASE WHEN fd.wikidata_qid IS NOT NULL AND fd.wikidata_qid != '' AND fd.wikidata_qid NOT LIKE 'NF%' THEN 1 ELSE 0 END) AS WithQid,
                SUM(CASE WHEN fd.wikidata_qid IS NULL OR fd.wikidata_qid = '' OR fd.wikidata_qid LIKE 'NF%' THEN 1 ELSE 0 END) AS WithoutQid,
                SUM(CASE WHEN fd.pipeline_step = 'Enrichment' THEN 1 ELSE 0 END) AS EnrichedStage3,
                SUM(CASE WHEN fd.pipeline_step != 'Enrichment' THEN 1 ELSE 0 END) AS NotEnrichedStage3,
                SUM(CASE WHEN fd.universe_assigned = 1 THEN 1 ELSE 0 END) AS UniverseAssigned,
                SUM(CASE WHEN fd.universe_assigned = 0 THEN 1 ELSE 0 END) AS UniverseUnassigned,
                SUM(CASE WHEN fd.artwork_settled_at IS NOT NULL
                           AND fd.artwork_settled_at < @staleThreshold
                           AND fd.wikidata_qid IS NOT NULL
                           AND fd.wikidata_qid != ''
                           AND fd.wikidata_qid NOT LIKE 'NF%'
                         THEN 1 ELSE 0 END) AS StaleItems,
                SUM(CASE WHEN fd.library_visibility = 'hidden' THEN 1 ELSE 0 END) AS HiddenByQualityGate,
                SUM(CASE WHEN fd.artwork_state = 'pending' THEN 1 ELSE 0 END) AS ArtPending,
                SUM(CASE WHEN fd.library_visibility = 'review_only' AND fd.pipeline_step = 'Retail' THEN 1 ELSE 0 END) AS RetailNeedsReview,
                SUM(CASE WHEN fd.status = 'QidNoMatch' THEN 1 ELSE 0 END) AS QidNoMatch,
                SUM(CASE WHEN fd.library_visibility = 'visible' AND fd.artwork_state = 'present' THEN 1 ELSE 0 END) AS CompletedWithArt
            FROM full_data fd;
            """, new { staleThreshold });

        return Task.FromResult(new LibraryItemProjectionSummary(
            row.TotalItems,
            row.WithQid,
            row.WithoutQid,
            row.EnrichedStage3,
            row.NotEnrichedStage3,
            row.UniverseAssigned,
            row.UniverseUnassigned,
            row.StaleItems,
            row.HiddenByQualityGate,
            row.ArtPending,
            row.RetailNeedsReview,
            row.QidNoMatch,
            row.CompletedWithArt));
    }

    public Task<LibraryItemLifecycleCounts> GetFourStateCountsAsync(Guid? batchId = null, CancellationToken ct = default)
    {
        using var conn = _db.CreateConnection();

        if (batchId.HasValue)
        {
            var batch = conn.QueryFirstOrDefault<(int FilesIdentified, int FilesReview, int FilesNoMatch, int FilesFailed)>("""
                SELECT files_registered AS FilesIdentified,
                       files_review AS FilesReview,
                       files_no_match AS FilesNoMatch,
                       files_failed AS FilesFailed
                FROM ingestion_batches
                WHERE id = @batchId;
                """, new { batchId = batchId.Value });

            var batchTriggers = conn.Query<(string Trigger, int Count)>("""
                SELECT rq.trigger AS Trigger, COUNT(*) AS Count
                FROM review_queue rq
                INNER JOIN media_assets ma ON ma.id = rq.entity_id
                INNER JOIN ingestion_log il ON il.content_hash = ma.content_hash
                WHERE rq.status = 'Pending'
                  AND il.ingestion_run_id = @batchId
                GROUP BY rq.trigger
                ORDER BY Count DESC;
                """, new { batchId = batchId.Value })
                .ToDictionary(r => r.Trigger, r => r.Count);

            return Task.FromResult(new LibraryItemLifecycleCounts(
                batch.FilesIdentified,
                batch.FilesReview + batch.FilesNoMatch + batch.FilesFailed,
                0,
                0,
                0,
                0,
                batchTriggers));
        }

        using var cmd = conn.CreateCommand();
        cmd.CommandText = BuildProjectionSql() + """
            SELECT
                COALESCE(SUM(CASE
                        WHEN fd.status IN ('Identified', 'Confirmed', 'RetailMatched', 'QidNoMatch', 'Edited')
                             AND fd.library_visibility = 'visible'
                            THEN 1
                        ELSE 0
                    END), 0) AS Identified,
                COALESCE(SUM(CASE WHEN fd.status = 'InReview' THEN 1 ELSE 0 END), 0) AS InReview,
                COALESCE(SUM(CASE WHEN fd.status = 'Provisional' THEN 1 ELSE 0 END), 0) AS Provisional,
                COALESCE(SUM(CASE WHEN fd.status = 'Rejected' THEN 1 ELSE 0 END), 0) AS Rejected,
                (SELECT COUNT(*) FROM persons) AS PersonCount,
                (SELECT COUNT(DISTINCT id) FROM collections) AS CollectionCount
            FROM full_data fd;
            """;

        int identified = 0, inReview = 0, provisional = 0, rejected = 0, personCount = 0, collectionCount = 0;
        using (var reader = cmd.ExecuteReader())
        {
            if (reader.Read())
            {
                identified = reader.GetInt32(0);
                inReview = reader.GetInt32(1);
                provisional = reader.GetInt32(2);
                rejected = reader.GetInt32(3);
                personCount = reader.GetInt32(4);
                collectionCount = reader.GetInt32(5);
            }
        }

        var triggerCounts = conn.Query<(string Trigger, int Count)>(BuildProjectionSql() + """
            SELECT fd.review_trigger AS Trigger, COUNT(*) AS Count
            FROM full_data fd
            WHERE fd.status = 'InReview'
              AND fd.review_trigger IS NOT NULL
              AND fd.review_trigger != ''
            GROUP BY fd.review_trigger
            ORDER BY Count DESC;
            """)
            .ToDictionary(r => r.Trigger, r => r.Count);

        return Task.FromResult(new LibraryItemLifecycleCounts(
            identified, inReview, provisional, rejected, personCount, collectionCount, triggerCounts));
    }

    public Task<Dictionary<string, int>> GetMediaTypeCountsAsync(CancellationToken ct = default)
    {
        using var conn = _db.CreateConnection();
        var rows = conn.Query<(string MediaType, int Count)>(BuildProjectionSql() + """
            SELECT fd.media_type AS MediaType, COUNT(*) AS Count
            FROM full_data fd
            WHERE fd.media_type IS NOT NULL AND fd.media_type != ''
              AND fd.status NOT IN ('Rejected', 'Provisional', 'InReview')
              AND fd.library_visibility = 'visible'
            GROUP BY fd.media_type;
            """);
        return Task.FromResult(rows.ToDictionary(r => r.MediaType, r => r.Count));
    }

    public Task<Dictionary<string, int>> GetOwnedMediaTypeCountsAsync(CancellationToken ct = default)
    {
        using var conn = _db.CreateConnection();
        var rows = conn.Query<(string MediaType, int Count)>("""
            SELECT w.media_type AS MediaType, COUNT(DISTINCT ma.id) AS Count
            FROM media_assets ma
            INNER JOIN editions e ON e.id = ma.edition_id
            INNER JOIN works w ON w.id = e.work_id
            WHERE w.media_type IS NOT NULL
              AND w.media_type != ''
              AND w.media_type != 'Unknown'
            GROUP BY w.media_type;
            """);
        return Task.FromResult(rows.ToDictionary(r => r.MediaType, r => r.Count));
    }

    private static string BuildProjectionSql(bool preferAsset = false)
    {
        var wikidataId = SqlGuidLiteral(WellKnownProviders.Wikidata);
        var localProcessorId = SqlGuidLiteral(WellKnownProviders.LocalProcessor);
        var libraryScanId = SqlGuidLiteral(WellKnownProviders.LibraryScanner);
        var comicVineId = SqlGuidLiteral(WellKnownProviders.ComicVine);
        var workYearSql = MediaDateSql.OriginalYearFromGroupedRows("w.media_type", "wcv.key", "wcv.value");
        var assetYearSql = MediaDateSql.OriginalYearFromGroupedRows("w.media_type", "acv.key", "acv.value");
        var explicitWorkYearSql = MediaDateSql.ExplicitOriginalYearFromGroupedRows("w.media_type", "wcv.key", "wcv.value");
        var explicitAssetYearSql = MediaDateSql.ExplicitOriginalYearFromGroupedRows("w.media_type", "acv.key", "acv.value");
        var primaryAssetIdSql = "(SELECT year_asset.asset_id FROM primary_asset_data year_asset WHERE year_asset.work_id = w.id)";
        var relatedBookYearSql = MediaDateSql.RelatedBookOriginalYear("w.id", primaryAssetIdSql);

        var primaryAssetOrder = preferAsset
            ? "CASE WHEN ma.id=@preferredAssetId THEN 0 ELSE 1 END, ma.id"
            : "CASE WHEN LOWER(ma.file_path_root) LIKE '%.epub' THEN 0 ELSE 1 END, ma.id";
        var sql = $"""
            WITH ranked_primary_assets AS (
                SELECT
                    e.work_id,
                    ma.id AS asset_id,
                    ma.file_path_root,
                    ma.status AS asset_status,
                    ROW_NUMBER() OVER (
                        PARTITION BY e.work_id
                        ORDER BY {primaryAssetOrder}) AS asset_rank
                FROM editions e
                INNER JOIN media_assets ma ON ma.edition_id = e.id
            ),
            primary_asset_data AS (
                SELECT work_id, asset_id, file_path_root, asset_status
                FROM ranked_primary_assets
                WHERE asset_rank=1
            ),
            root_work_data AS (
                SELECT
                    w.id AS work_id,
                    COALESCE(gp.id, p.id, w.id) AS root_work_id
                FROM works w
                LEFT JOIN works p ON p.id = w.parent_work_id
                LEFT JOIN works gp ON gp.id = p.parent_work_id
            ),
            asset_cv AS (
                SELECT
                    pad.work_id,
                    cv.key,
                    cv.value,
                    cv.winning_provider_id,
                    cv.is_conflicted,
                    cv.last_scored_at
                FROM primary_asset_data pad
                INNER JOIN canonical_values cv ON cv.entity_id = pad.asset_id
            ),
            work_cv AS (
                SELECT
                    rwd.work_id,
                    cv.key,
                    cv.value,
                    cv.last_scored_at
                FROM root_work_data rwd
                INNER JOIN canonical_values cv ON cv.entity_id = rwd.root_work_id
            ),
            work_data AS (
                SELECT
                    w.id AS entity_id,
                    w.media_type,
                    w.wikidata_status,
                    w.curator_state,
                    CASE WHEN w.collection_id IS NOT NULL THEN 1 ELSE 0 END AS universe_assigned,
                    MAX(CASE WHEN acv.key = 'title' THEN acv.value END) AS title,
                    MAX(CASE WHEN acv.key = 'issue_title' THEN acv.value END) AS issue_title,
                    MAX(CASE WHEN acv.key = 'issue_number' THEN acv.value END) AS issue_number,
                    MAX(CASE WHEN acv.key = 'series_position' THEN acv.value END) AS series_position,
                    MAX(CASE WHEN acv.key = 'rating' THEN acv.value END) AS rating,
                    MAX(CASE WHEN acv.key = 'track_number' THEN acv.value END) AS track_number,
                    MAX(CASE WHEN acv.key = 'season_number' THEN acv.value END) AS season_number,
                    MAX(CASE WHEN acv.key = 'episode_number' THEN acv.value END) AS episode_number,
                    MAX(CASE WHEN acv.key = 'episode_title' THEN acv.value END) AS episode_title,
                    MAX(CASE WHEN acv.key = 'duration' THEN acv.value END) AS duration,
                    MAX(CASE WHEN acv.key = 'file_name' THEN acv.value END) AS file_name,
                    MAX(CASE WHEN acv.key = 'language' THEN acv.value END) AS language,
                    MAX(CASE WHEN acv.key = 'title' THEN acv.winning_provider_id END) AS title_provider_id,
                    MAX(CASE WHEN acv.key = 'wikidata_qid' THEN acv.value END) AS wikidata_qid,
                    MAX(CASE WHEN acv.key = 'qid_resolution_method' THEN acv.value END) AS qid_resolution_method,
                    MAX(CASE WHEN acv.key = 'cover_state' THEN acv.value END) AS self_cover_state,
                    MAX(CASE WHEN acv.key = 'cover_source' THEN acv.value END) AS self_cover_source,
                    MAX(CASE WHEN acv.key = 'hero_state' THEN acv.value END) AS self_hero_state,
                    MAX(CASE WHEN acv.key = 'artwork_settled_at' THEN acv.value END) AS self_artwork_settled_at,
                    MAX(CASE WHEN acv.key = 'cover_url' THEN acv.value END) AS self_cover_url,
                    MAX(CASE WHEN acv.key = 'cover_url' THEN acv.last_scored_at END) AS self_cover_last_scored_at,
                    MAX(CASE WHEN acv.key = 'hero' THEN acv.value END) AS self_hero_url,
                    MAX(CASE WHEN acv.key = 'background' THEN acv.value END) AS self_background_url,
                    MAX(CASE WHEN acv.key = 'banner' THEN acv.value END) AS self_banner_url,
                    CASE
                        WHEN LOWER(w.media_type) LIKE '%audio%'
                        THEN COALESCE(
                            {explicitWorkYearSql},
                            {explicitAssetYearSql},
                            {relatedBookYearSql},
                            {assetYearSql},
                            {workYearSql})
                        WHEN LOWER(w.media_type) LIKE '%music%'
                        THEN COALESCE(
                            {explicitWorkYearSql},
                            {explicitAssetYearSql},
                            {assetYearSql},
                            {workYearSql})
                        WHEN LOWER(w.media_type) LIKE '%tv%'
                          OR LOWER(w.media_type) LIKE '%television%'
                        THEN COALESCE({workYearSql}, {assetYearSql})
                        ELSE COALESCE({assetYearSql}, {workYearSql})
                    END AS year,
                    COALESCE(
                        CASE WHEN w.media_type = 'Comics' THEN (
                            SELECT CASE
                                WHEN cnt.total = 1 THEN a1.value
                                WHEN cnt.total = 2 THEN a1.value || ' & ' || a2.value
                                ELSE a1.value || ' & ' || a2.value || ' + ' || (cnt.total - 2) || ' more'
                            END
                            FROM (
                                SELECT COUNT(DISTINCT cva0.value) AS total
                                FROM canonical_value_arrays cva0
                                INNER JOIN media_assets ma0 ON ma0.id = cva0.entity_id
                                INNER JOIN editions e0 ON e0.id = ma0.edition_id
                                WHERE e0.work_id = w.id
                                  AND cva0.key = 'author'
                            ) cnt
                            LEFT JOIN (
                                SELECT DISTINCT cva1.value
                                FROM canonical_value_arrays cva1
                                INNER JOIN media_assets ma1 ON ma1.id = cva1.entity_id
                                INNER JOIN editions e1 ON e1.id = ma1.edition_id
                                WHERE e1.work_id = w.id
                                  AND cva1.key = 'author'
                                ORDER BY cva1.ordinal
                                LIMIT 1
                            ) a1 ON 1 = 1
                            LEFT JOIN (
                                SELECT DISTINCT cva2.value
                                FROM canonical_value_arrays cva2
                                INNER JOIN media_assets ma2 ON ma2.id = cva2.entity_id
                                INNER JOIN editions e2 ON e2.id = ma2.edition_id
                                WHERE e2.work_id = w.id
                                  AND cva2.key = 'author'
                                ORDER BY cva2.ordinal
                                LIMIT 1 OFFSET 1
                            ) a2 ON cnt.total >= 2
                            WHERE cnt.total >= 1
                        ) END,
                        CASE WHEN w.media_type = 'Comics' THEN (
                            SELECT mc_author.claim_value
                            FROM metadata_claims mc_author
                            INNER JOIN media_assets ma_author ON ma_author.id = mc_author.entity_id
                            INNER JOIN editions e_author ON e_author.id = ma_author.edition_id
                            WHERE e_author.work_id = w.id
                              AND mc_author.claim_key = 'author'
                              AND mc_author.claim_value IS NOT NULL
                              AND TRIM(mc_author.claim_value) != ''
                            ORDER BY
                                CASE
                                    WHEN mc_author.provider_id = {localProcessorId} THEN 0
                                    WHEN mc_author.provider_id = {comicVineId} THEN 1
                                    WHEN mc_author.provider_id = {wikidataId} THEN 2
                                    ELSE 3
                                END,
                                mc_author.confidence DESC,
                                mc_author.claimed_at DESC
                            LIMIT 1
                        ) END,
                        (
                            SELECT CASE
                                WHEN cnt.total = 1 THEN a1.value
                                WHEN cnt.total = 2 THEN a1.value || ' & ' || a2.value
                                ELSE a1.value || ' & ' || a2.value || ' + ' || (cnt.total - 2) || ' more'
                            END
                            FROM (
                                SELECT COUNT(DISTINCT cva0.value) AS total
                                FROM canonical_value_arrays cva0
                                LEFT JOIN works pw0 ON pw0.id = w.parent_work_id
                                LEFT JOIN works gpw0 ON gpw0.id = pw0.parent_work_id
                                WHERE cva0.entity_id = COALESCE(gpw0.id, pw0.id, w.id)
                                  AND cva0.key = 'author'
                            ) cnt
                            LEFT JOIN (
                                SELECT DISTINCT cva1.value
                                FROM canonical_value_arrays cva1
                                LEFT JOIN works pw1 ON pw1.id = w.parent_work_id
                                LEFT JOIN works gpw1 ON gpw1.id = pw1.parent_work_id
                                WHERE cva1.entity_id = COALESCE(gpw1.id, pw1.id, w.id)
                                  AND cva1.key = 'author'
                                ORDER BY cva1.ordinal
                                LIMIT 1
                            ) a1 ON 1 = 1
                            LEFT JOIN (
                                SELECT DISTINCT cva2.value
                                FROM canonical_value_arrays cva2
                                LEFT JOIN works pw2 ON pw2.id = w.parent_work_id
                                LEFT JOIN works gpw2 ON gpw2.id = pw2.parent_work_id
                                WHERE cva2.entity_id = COALESCE(gpw2.id, pw2.id, w.id)
                                  AND cva2.key = 'author'
                                ORDER BY cva2.ordinal
                                LIMIT 1 OFFSET 1
                            ) a2 ON cnt.total >= 2
                            WHERE cnt.total >= 1
                        )
                    ) AS author,
                    (
                        SELECT value
                        FROM canonical_value_arrays cva_artist
                        INNER JOIN root_work_data rwd_artist ON rwd_artist.root_work_id = cva_artist.entity_id
                        WHERE rwd_artist.work_id = w.id
                          AND cva_artist.key IN ('album_artist', 'artist', 'performer')
                        ORDER BY CASE cva_artist.key WHEN 'album_artist' THEN 0 WHEN 'artist' THEN 1 ELSE 2 END,
                                 cva_artist.ordinal
                        LIMIT 1
                    ) AS artist,
                    MAX(CASE WHEN wcv.key = 'series' THEN wcv.value END) AS series,
                    (
                        SELECT value
                        FROM canonical_value_arrays cva_narrator
                        INNER JOIN root_work_data rwd_narrator ON rwd_narrator.root_work_id = cva_narrator.entity_id
                        WHERE rwd_narrator.work_id = w.id
                          AND cva_narrator.key = 'narrator'
                        ORDER BY cva_narrator.ordinal
                        LIMIT 1
                    ) AS narrator,
                    (
                    SELECT GROUP_CONCAT(value, '; ')
                        FROM (
                            SELECT cva_genre.value
                            FROM canonical_value_arrays cva_genre
                            INNER JOIN root_work_data rwd_genre ON rwd_genre.root_work_id = cva_genre.entity_id
                            WHERE rwd_genre.work_id = w.id
                              AND cva_genre.key = 'genre'
                            ORDER BY cva_genre.ordinal
                        )
                    ) AS genre,
                    MAX(CASE WHEN wcv.key = 'album' THEN wcv.value END) AS album,
                    MAX(CASE WHEN wcv.key = 'show_name' THEN wcv.value END) AS show_name,
                    MAX(CASE WHEN wcv.key = 'network' THEN wcv.value END) AS network,
                    MAX(CASE WHEN wcv.key = 'description' THEN wcv.value END) AS description,
                    MAX(CASE WHEN wcv.key = 'cover_state' THEN wcv.value END) AS parent_cover_state,
                    MAX(CASE WHEN wcv.key = 'cover_source' THEN wcv.value END) AS parent_cover_source,
                    MAX(CASE WHEN wcv.key = 'hero_state' THEN wcv.value END) AS parent_hero_state,
                    MAX(CASE WHEN wcv.key = 'artwork_settled_at' THEN wcv.value END) AS parent_artwork_settled_at,
                    MAX(CASE WHEN wcv.key = 'cover_url' THEN wcv.value END) AS parent_cover_url,
                    MAX(CASE WHEN wcv.key = 'cover_url' THEN wcv.last_scored_at END) AS parent_cover_last_scored_at,
                    MAX(CASE WHEN wcv.key = 'hero' THEN wcv.value END) AS parent_hero_url,
                    MAX(CASE WHEN wcv.key = 'background' THEN wcv.value END) AS parent_background_url,
                    MAX(CASE WHEN wcv.key = 'banner' THEN wcv.value END) AS parent_banner_url,
                    (
                        SELECT GROUP_CONCAT(sub.value, ', ')
                        FROM (
                            SELECT cva.value
                            FROM canonical_value_arrays cva
                            LEFT JOIN works pw ON pw.id = w.parent_work_id
                            LEFT JOIN works gpw ON gpw.id = pw.parent_work_id
                            WHERE cva.entity_id = COALESCE(gpw.id, pw.id, w.id)
                              AND cva.key = 'cast_member'
                            ORDER BY cva.ordinal
                            LIMIT 3
                        ) sub
                    ) AS top_cast,
                    COALESCE(
                        (
                    SELECT GROUP_CONCAT(value, '; ')
                            FROM (
                                SELECT cva_director.value
                                FROM canonical_value_arrays cva_director
                                INNER JOIN root_work_data rwd_director ON rwd_director.root_work_id = cva_director.entity_id
                                WHERE rwd_director.work_id = w.id
                                  AND cva_director.key = 'director'
                                ORDER BY cva_director.ordinal
                            )
                        ),
                        (
                    SELECT GROUP_CONCAT(value, '; ')
                            FROM (
                                SELECT cva_director_asset.value
                                FROM canonical_value_arrays cva_director_asset
                                INNER JOIN primary_asset_data pad_director ON pad_director.asset_id = cva_director_asset.entity_id
                                WHERE pad_director.work_id = w.id
                                  AND cva_director_asset.key = 'director'
                                ORDER BY cva_director_asset.ordinal
                            )
                        )
                    ) AS director,
                    CASE
                        WHEN w.media_type = 'Movies' THEN MAX(CASE WHEN wcv.key = 'runtime' THEN wcv.value END)
                        ELSE MAX(CASE WHEN acv.key = 'runtime' THEN acv.value END)
                    END AS runtime,
                    (
                        SELECT pr.name || ': ' || mc_rt.claim_value
                        FROM metadata_claims mc_rt
                        INNER JOIN media_assets ma_rt ON ma_rt.id = mc_rt.entity_id
                        INNER JOIN editions e_rt ON e_rt.id = ma_rt.edition_id
                        INNER JOIN metadata_providers pr ON pr.id = mc_rt.provider_id
                        WHERE e_rt.work_id = w.id
                          AND mc_rt.claim_key IN ('title', 'episode_title', 'show_name', 'album')
                          AND mc_rt.provider_id != {wikidataId}
                          AND mc_rt.provider_id != 'local_filesystem'
                          AND mc_rt.provider_id != {localProcessorId}
                          AND mc_rt.provider_id != {libraryScanId}
                        ORDER BY mc_rt.confidence DESC
                        LIMIT 1
                    ) AS retail_match_detail
                FROM works w
                LEFT JOIN asset_cv acv ON acv.work_id = w.id
                LEFT JOIN work_cv wcv ON wcv.work_id = w.id
                WHERE w.work_kind != 'parent'
                GROUP BY w.id, w.media_type, w.wikidata_status, w.curator_state, w.collection_id
            )
            """;

        sql += """
            ,
            ingest_date_data AS (
                SELECT
                    e.work_id,
                    MIN(mc.claimed_at) AS first_claimed_at
                FROM editions e
                INNER JOIN media_assets ma ON ma.edition_id = e.id
                INNER JOIN metadata_claims mc ON mc.entity_id = ma.id
                GROUP BY e.work_id
            ),
            review_targets AS (
                SELECT
                    e_rv.work_id,
                    rq.id,
                    rq.trigger,
                    rq.confidence_score,
                    rq.candidates_json,
                    rq.created_at
                FROM review_queue rq
                INNER JOIN media_assets ma_rv ON ma_rv.id = rq.entity_id
                INNER JOIN editions e_rv ON e_rv.id = ma_rv.edition_id
                WHERE rq.status = 'Pending'
                UNION ALL
                SELECT
                    w_rv.id AS work_id,
                    rq.id,
                    rq.trigger,
                    rq.confidence_score,
                    rq.candidates_json,
                    rq.created_at
                FROM review_queue rq
                INNER JOIN works w_rv ON w_rv.id = rq.entity_id
                WHERE rq.status = 'Pending'
            ),
            review_data AS (
                SELECT
                    rt.work_id AS entity_id,
                    (
                        SELECT rt2.id
                        FROM review_targets rt2
                        WHERE rt2.work_id = rt.work_id
                        ORDER BY rt2.created_at DESC
                        LIMIT 1
                    ) AS review_id,
                    (
                        SELECT rt2.trigger
                        FROM review_targets rt2
                        WHERE rt2.work_id = rt.work_id
                        ORDER BY rt2.created_at DESC
                        LIMIT 1
                    ) AS review_trigger,
                    (
                        SELECT MAX(rt2.confidence_score)
                        FROM review_targets rt2
                        WHERE rt2.work_id = rt.work_id
                    ) AS review_confidence,
                    (
                        SELECT rt2.candidates_json
                        FROM review_targets rt2
                        WHERE rt2.work_id = rt.work_id
                          AND rt2.candidates_json IS NOT NULL
                        ORDER BY rt2.created_at DESC
                        LIMIT 1
                    ) AS candidates_json
                FROM review_targets rt
                GROUP BY rt.work_id
            ),
            identity_job_data AS (
                SELECT
                    e_ij.work_id AS entity_id,
                    (
                        SELECT ij2.state
                        FROM identity_jobs ij2
                        INNER JOIN media_assets ma_ij2 ON ma_ij2.id = ij2.entity_id
                        INNER JOIN editions e_ij2 ON e_ij2.id = ma_ij2.edition_id
                        WHERE e_ij2.work_id = e_ij.work_id
                        ORDER BY ij2.updated_at DESC, ij2.created_at DESC
                        LIMIT 1
                    ) AS job_state,
                    (
                        SELECT ij2.updated_at
                        FROM identity_jobs ij2
                        INNER JOIN media_assets ma_ij2 ON ma_ij2.id = ij2.entity_id
                        INNER JOIN editions e_ij2 ON e_ij2.id = ma_ij2.edition_id
                        WHERE e_ij2.work_id = e_ij.work_id
                        ORDER BY ij2.updated_at DESC, ij2.created_at DESC
                        LIMIT 1
                    ) AS job_updated_at,
                    (
                        SELECT ij2.resolved_qid
                        FROM identity_jobs ij2
                        INNER JOIN media_assets ma_ij2 ON ma_ij2.id = ij2.entity_id
                        INNER JOIN editions e_ij2 ON e_ij2.id = ma_ij2.edition_id
                        WHERE e_ij2.work_id = e_ij.work_id
                        ORDER BY ij2.updated_at DESC, ij2.created_at DESC
                        LIMIT 1
                    ) AS resolved_qid
                FROM identity_jobs ij
                INNER JOIN media_assets ma_ij ON ma_ij.id = ij.entity_id
                INNER JOIN editions e_ij ON e_ij.id = ma_ij.edition_id
                GROUP BY e_ij.work_id
            ),
            user_lock_data AS (
                SELECT
                    pad.work_id AS entity_id,
                    CASE WHEN EXISTS (
                        SELECT 1
                        FROM metadata_claims mc
                        WHERE mc.entity_id = pad.asset_id
                          AND mc.is_user_locked = 1
                    ) THEN 1 ELSE 0 END AS has_locks
                FROM primary_asset_data pad
            ),
            raw_data AS (
                SELECT
                    wd.entity_id,
                    CASE
                        WHEN wd.media_type = 'Comics' THEN COALESCE(
                            CASE
                                WHEN NULLIF(wd.issue_title, '') IS NOT NULL
                                     AND (
                                         NULLIF(wd.series, '') IS NULL
                                         OR lower(trim(wd.issue_title)) != lower(trim(wd.series))
                                     )
                                    THEN NULLIF(wd.issue_title, '')
                                ELSE NULL
                            END,
                            CASE
                                WHEN COALESCE(NULLIF(wd.issue_number, ''), NULLIF(wd.series_position, '')) IS NOT NULL
                                    THEN 'Issue #' || COALESCE(NULLIF(wd.issue_number, ''), NULLIF(wd.series_position, ''))
                                ELSE NULL
                            END,
                            NULLIF(wd.title, ''),
                            'Untitled')
                        ELSE COALESCE(NULLIF(wd.title, ''), 'Untitled')
                    END AS title,
                    wd.year,
                    wd.media_type,
                    wd.author,
                    wd.director,
                    wd.artist,
                    wd.series,
                    wd.series_position,
                    wd.narrator,
                    wd.genre,
                    wd.runtime,
                    wd.rating,
                    wd.album,
                    wd.track_number,
                    wd.season_number,
                    wd.episode_number,
                    wd.show_name,
                    wd.episode_title,
                    wd.network,
                    wd.top_cast,
                    wd.duration,
                    wd.description,
                    wd.language,
                    wd.file_name,
                    wd.title_provider_id AS match_source,
                    wd.retail_match_detail,
                    COALESCE(NULLIF(wd.wikidata_qid, ''), NULLIF(ij.resolved_qid, '')) AS wikidata_qid,
                    wd.qid_resolution_method,
                    wd.wikidata_status,
                    wd.curator_state,
                    wd.universe_assigned,
                    pad.asset_id,
                    pad.file_path_root AS file_path,
                    pad.asset_status,
                    rd.review_id,
                    rd.review_trigger,
                    rd.review_confidence,
                    rd.candidates_json,
                    COALESCE(ul.has_locks, 0) AS has_user_locks,
                    ij.job_state,
                    ij.job_updated_at,
                    idd.first_claimed_at AS created_at,
                    COALESCE(NULLIF(wd.self_cover_state, ''), NULLIF(wd.parent_cover_state, ''), CASE
                        WHEN COALESCE(NULLIF(wd.self_cover_url, ''), NULLIF(wd.parent_cover_url, '')) IS NOT NULL THEN 'present'
                        WHEN ij.job_state IN ('RetailNoMatch', 'QidNoMatch', 'QidNeedsReview', 'Ready', 'ReadyWithoutUniverse', 'Failed')
                             OR rd.review_id IS NOT NULL
                            THEN 'missing'
                        ELSE 'pending'
                    END) AS artwork_state,
                    COALESCE(NULLIF(wd.self_cover_source, ''), NULLIF(wd.parent_cover_source, ''), CASE
                        WHEN COALESCE(NULLIF(wd.self_cover_url, ''), NULLIF(wd.parent_cover_url, '')) LIKE 'http%' THEN 'provider'
                        WHEN COALESCE(NULLIF(wd.self_cover_url, ''), NULLIF(wd.parent_cover_url, '')) LIKE '/stream/%' THEN 'embedded'
                        ELSE NULL
                    END) AS artwork_source,
                    COALESCE(NULLIF(wd.self_artwork_settled_at, ''), NULLIF(wd.parent_artwork_settled_at, ''), CASE
                        WHEN COALESCE(NULLIF(wd.self_cover_url, ''), NULLIF(wd.parent_cover_url, '')) IS NOT NULL
                            THEN COALESCE(wd.self_cover_last_scored_at, wd.parent_cover_last_scored_at, ij.job_updated_at)
                        WHEN ij.job_state IN ('RetailNoMatch', 'QidNoMatch', 'QidNeedsReview', 'Ready', 'ReadyWithoutUniverse', 'Failed')
                             OR rd.review_id IS NOT NULL
                            THEN COALESCE(ij.job_updated_at, idd.first_claimed_at)
                        ELSE NULL
                    END) AS artwork_settled_at,
                    COALESCE(NULLIF(wd.self_hero_state, ''), NULLIF(wd.parent_hero_state, ''), CASE
                        WHEN COALESCE(NULLIF(wd.self_hero_url, ''), NULLIF(wd.parent_hero_url, '')) IS NOT NULL THEN 'present'
                        ELSE 'pending'
                    END) AS hero_state,
                    COALESCE(NULLIF(wd.self_cover_url, ''), NULLIF(wd.parent_cover_url, '')) AS cover_url_value,
                    COALESCE(NULLIF(wd.self_background_url, ''), NULLIF(wd.parent_background_url, '')) AS background_url_value,
                    COALESCE(NULLIF(wd.self_banner_url, ''), NULLIF(wd.parent_banner_url, '')) AS banner_url_value,
                    CASE
                        WHEN COALESCE(NULLIF(wd.wikidata_qid, ''), NULLIF(ij.resolved_qid, '')) IS NOT NULL
                             AND COALESCE(NULLIF(wd.wikidata_qid, ''), NULLIF(ij.resolved_qid, '')) NOT LIKE 'NF%'
                            THEN 1 ELSE 0
                    END AS has_valid_qid,
                    CASE WHEN COALESCE(NULLIF(wd.title, ''), '') NOT IN ('', 'Untitled', 'Unknown') THEN 1 ELSE 0 END AS has_quality_title,
                    CASE WHEN COALESCE(NULLIF(wd.media_type, ''), '') NOT IN ('', 'Unknown') THEN 1 ELSE 0 END AS has_resolved_media_type
                FROM work_data wd
                LEFT JOIN primary_asset_data pad ON pad.work_id = wd.entity_id
                LEFT JOIN review_data rd ON rd.entity_id = wd.entity_id
                LEFT JOIN identity_job_data ij ON ij.entity_id = wd.entity_id
                LEFT JOIN user_lock_data ul ON ul.entity_id = wd.entity_id
                LEFT JOIN ingest_date_data idd ON idd.work_id = wd.entity_id
            )
            """;

        sql += """
            ,
            full_data AS (
                SELECT
                    rd.entity_id,
                    rd.title,
                    rd.year,
                    rd.media_type,
                    CASE
                        WHEN rd.artwork_state = 'present' THEN rd.cover_url_value
                        ELSE NULL
                    END AS cover_url,
                    CASE
                        WHEN rd.hero_state = 'present' AND rd.asset_id IS NOT NULL THEN '/stream/' || rd.asset_id || '/hero'
                        ELSE NULL
                    END AS hero_url,
                    CASE
                        WHEN rd.background_url_value IS NOT NULL THEN rd.background_url_value
                        ELSE NULL
                    END AS background_url,
                    CASE
                        WHEN rd.banner_url_value IS NOT NULL THEN rd.banner_url_value
                        ELSE NULL
                    END AS banner_url,
                    rd.author,
                    rd.director,
                    rd.artist,
                    rd.series,
                    rd.series_position,
                    rd.narrator,
                    rd.genre,
                    rd.runtime,
                    rd.rating,
                    rd.album,
                    rd.track_number,
                    rd.season_number,
                    rd.episode_number,
                    rd.show_name,
                    rd.episode_title,
                    rd.network,
                    rd.top_cast,
                    rd.duration,
                    rd.description,
                    rd.language,
                    rd.file_name,
                    rd.match_source,
                    rd.review_id,
                    rd.review_trigger,
                    rd.review_confidence,
                    rd.candidates_json,
                    rd.has_user_locks,
                    rd.file_path,
                    rd.wikidata_status,
                    rd.wikidata_qid,
                    rd.qid_resolution_method,
                    rd.retail_match_detail,
                    rd.artwork_state,
                    rd.artwork_source,
                    rd.artwork_settled_at,
                    rd.universe_assigned,
                    rd.created_at,
                    rd.asset_status,
                    CASE
                        WHEN rd.has_quality_title = 1
                             AND rd.has_resolved_media_type = 1
                             AND COALESCE(rd.job_state, '') NOT IN ('RetailNoMatch', 'Failed')
                             AND (
                                 rd.artwork_state = 'present'
                                 OR rd.artwork_settled_at IS NOT NULL
                             )
                            THEN 1 ELSE 0
                    END AS is_ready_for_library,
                    CASE
                        WHEN rd.curator_state IN ('rejected', 'provisional')
                             OR rd.job_state = 'QidNeedsReview'
                             OR rd.job_state = 'RetailMatchedNeedsReview'
                             OR rd.job_state = 'RetailNoMatch'
                             OR rd.job_state = 'Failed'
                             OR (
                                 rd.review_id IS NOT NULL
                                 AND rd.review_trigger != 'WritebackFailed'
                                 AND (
                                     rd.job_state IS NULL
                                     OR rd.job_state NOT IN (
                                         'Queued',
                                         'RetailSearching',
                                         'RetailMatched',
                                         'RetailMatchedNeedsReview',
                                         'BridgeSearching',
                                         'QidResolved',
                                         'Hydrating'
                                     )
                                 )
                             )
                            THEN 'review_only'
                        WHEN rd.has_quality_title = 1
                             AND rd.has_resolved_media_type = 1
                             AND (
                                 rd.artwork_state = 'present'
                                 OR rd.artwork_settled_at IS NOT NULL
                             )
                            THEN 'visible'
                        ELSE 'hidden'
                    END AS library_visibility,
                    CASE
                        WHEN rd.curator_state = 'rejected' THEN 'Rejected'
                        WHEN rd.curator_state = 'provisional' THEN 'Provisional'
                        WHEN rd.job_state = 'QidNeedsReview' THEN 'InReview'
                        WHEN rd.job_state = 'RetailMatchedNeedsReview' THEN 'InReview'
                        WHEN rd.review_id IS NOT NULL
                             AND rd.review_trigger != 'WritebackFailed'
                             AND (
                                 rd.job_state IS NULL
                                 OR rd.job_state NOT IN (
                                     'Queued',
                                     'RetailSearching',
                                     'RetailMatched',
                                     'RetailMatchedNeedsReview',
                                     'BridgeSearching',
                                     'QidResolved',
                                     'Hydrating'
                                 )
                            )
                            THEN 'InReview'
                        WHEN rd.has_valid_qid = 1 THEN 'Identified'
                        WHEN rd.job_state = 'QidNoMatch' THEN 'QidNoMatch'
                        WHEN rd.job_state = 'RetailNoMatch' THEN 'NoMatch'
                        WHEN rd.job_state = 'Failed' THEN 'Failed'
                        WHEN rd.job_state IN ('RetailMatched', 'BridgeSearching', 'QidResolved', 'Hydrating', 'Completed') THEN 'RetailMatched'
                        WHEN rd.has_user_locks = 1 THEN 'Edited'
                        ELSE 'Confirmed'
                    END AS status,
                    CASE WHEN rd.asset_status = 'Conflicted' THEN 1 ELSE 0 END AS has_duplicate,
                    CASE
                        WHEN rd.has_valid_qid = 1 THEN 'matched'
                        WHEN rd.job_state = 'QidNoMatch' THEN 'failed'
                        WHEN rd.review_trigger IN ('MissingQid', 'MultipleQidMatches', 'WikidataBridgeFailed')
                             OR rd.job_state = 'QidNeedsReview'
                            THEN 'warning'
                        WHEN rd.job_state IN ('BridgeSearching', 'RetailMatched', 'RetailMatchedNeedsReview', 'QidResolved', 'Hydrating')
                            THEN 'warning'
                        ELSE 'none'
                    END AS wikidata_match,
                    CASE
                        WHEN COALESCE(NULLIF(rd.retail_match_detail, ''), '') != ''
                            THEN 'matched'
                        WHEN rd.job_state IN ('RetailMatched', 'RetailMatchedNeedsReview', 'BridgeSearching', 'QidResolved', 'QidNeedsReview', 'QidNoMatch', 'Hydrating', 'UniverseEnriching', 'Ready', 'ReadyWithoutUniverse', 'Completed')
                            THEN 'matched'
                        WHEN rd.job_state = 'RetailNoMatch' THEN 'failed'
                        WHEN rd.review_trigger IN ('RetailMatchFailed')
                            THEN 'warning'
                        ELSE 'none'
                    END AS retail_match,
                    CASE
                        WHEN rd.review_trigger IN ('RetailMatchFailed')
                             OR rd.job_state IN ('Queued', 'RetailSearching', 'RetailMatchedNeedsReview', 'RetailNoMatch', 'Failed')
                            THEN 'Retail'
                        WHEN rd.job_state = 'QidNoMatch'
                            THEN 'Wikidata'
                        WHEN rd.has_valid_qid = 0
                             AND rd.job_state IS NOT NULL
                             AND rd.job_state NOT IN ('Queued', 'RetailSearching', 'RetailMatchedNeedsReview', 'RetailNoMatch', 'Failed')
                            THEN 'Wikidata'
                        ELSE 'Enrichment'
                    END AS pipeline_step,
                    CASE
                        WHEN rd.review_confidence IS NOT NULL THEN rd.review_confidence
                        WHEN rd.has_valid_qid = 1 THEN 1.0
                        WHEN rd.job_state IN ('RetailMatched', 'BridgeSearching', 'QidResolved', 'Hydrating', 'Completed', 'QidNoMatch') THEN 0.85
                        WHEN rd.job_state = 'RetailMatchedNeedsReview' THEN 0.65
                        WHEN rd.job_state = 'RetailNoMatch' THEN 0.30
                        ELSE 0.0
                    END AS confidence
                FROM raw_data rd
            )
            """;

        return sql;
    }

    private static string SqlGuidLiteral(Guid id)
    {
        return $"X'{Convert.ToHexString(GuidSql.ToBlob(id))}'";
    }

    private static void AddParameters(SqliteCommand cmd, LibraryItemQuery query)
    {
        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            cmd.Parameters.AddWithValue("@search", $"%{query.Search}%");
            var escaped = query.Search.Trim().Replace("\"", "\"\"");
            cmd.Parameters.AddWithValue("@ftsQuery", $"\"{escaped}\"*");
        }

        if (!string.IsNullOrWhiteSpace(query.MediaType))
        {
            var types = query.MediaType.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (types.Length == 1)
            {
                cmd.Parameters.AddWithValue("@mediaType", NormalizeMediaType(types[0]));
            }
            else
            {
                for (var i = 0; i < types.Length; i++)
                {
                    cmd.Parameters.AddWithValue($"@mt{i}", NormalizeMediaType(types[i]));
                }
            }
        }

        if (!string.IsNullOrWhiteSpace(query.Status) && query.Status != "Approved")
        {
            cmd.Parameters.AddWithValue("@status", query.Status);
        }

        if (query.MinConfidence.HasValue)
        {
            cmd.Parameters.AddWithValue("@minConfidence", query.MinConfidence.Value);
        }

        if (!string.IsNullOrWhiteSpace(query.MatchSource))
        {
            cmd.Parameters.AddWithValue("@matchSource", query.MatchSource);
        }
    }

    private static IReadOnlyDictionary<Guid, string> LoadProviderNamesById(SqliteConnection conn)
    {
        return conn.Query<(Guid Id, string Name)>("""
            SELECT id AS Id,
                   name AS Name
            FROM metadata_providers;
            """)
            .ToDictionary(row => row.Id, row => row.Name, EqualityComparer<Guid>.Default);
    }

    private static string? ResolveRetailProviderName(
        IReadOnlyList<LibraryItemCanonicalValue> canonicalValues,
        IReadOnlyList<LibraryItemClaimRecord> claims,
        IReadOnlyDictionary<Guid, string> providerNamesById)
    {
        var claimProvider = claims
            .Where(claim => IsRetailProviderClaim(claim.ClaimKey))
            .Where(claim => providerNamesById.TryGetValue(claim.ProviderId, out var providerName)
                            && !IsNonRetailProvider(providerName))
            .OrderByDescending(claim => claim.Confidence)
            .ThenByDescending(claim => claim.ClaimedAt)
            .Select(claim => providerNamesById[claim.ProviderId])
            .FirstOrDefault();

        if (!string.IsNullOrWhiteSpace(claimProvider))
        {
            return claimProvider;
        }

        return canonicalValues
            .Where(value => IsRetailProviderClaim(value.Key))
            .Select(value => Guid.TryParse(value.WinningProviderId, out var providerId)
                && providerNamesById.TryGetValue(providerId, out var providerName)
                && !IsNonRetailProvider(providerName)
                    ? providerName
                    : null)
            .FirstOrDefault(providerName => !string.IsNullOrWhiteSpace(providerName));
    }

    private static bool IsRetailProviderClaim(string claimKey) =>
        claimKey is MetadataFieldConstants.Title
            or MetadataFieldConstants.Album
            or MetadataFieldConstants.ShowName
            or MetadataFieldConstants.EpisodeTitle
            or MetadataFieldConstants.Series
            or MetadataFieldConstants.Description;

    private static bool IsNonRetailProvider(string? providerName) =>
        string.IsNullOrWhiteSpace(providerName)
        || providerName.Contains("wikidata", StringComparison.OrdinalIgnoreCase)
        || providerName.Contains("wikipedia", StringComparison.OrdinalIgnoreCase)
        || providerName.Contains("local", StringComparison.OrdinalIgnoreCase)
        || providerName.Contains("libraryscan", StringComparison.OrdinalIgnoreCase)
        || providerName.Contains("user_manual", StringComparison.OrdinalIgnoreCase)
        || providerName.Contains("ai", StringComparison.OrdinalIgnoreCase);

    private static string? GetProviderBridgeId(IReadOnlyDictionary<string, string> bridgeIds, string? providerName)
    {
        if (bridgeIds.Count == 0)
        {
            return null;
        }

        var normalizedProvider = providerName?.Trim().ToLowerInvariant() ?? string.Empty;
        var preferredKeys = normalizedProvider switch
        {
            var provider when provider.Contains("tmdb") => new[] { "tmdb_id", "tmdb_movie_id", "tmdb_tv_id" },
            var provider when provider.Contains("comic") => new[] { "comic_vine_id", "comicvine_id" },
            var provider when provider.Contains("apple") => new[] { "apple_books_id", "apple_music_id", "apple_music_collection_id", "apple_itunes_id" },
            var provider when provider.Contains("open_library") => new[] { "open_library_id", "isbn_13", "isbn" },
            var provider when provider.Contains("musicbrainz") => new[] { "musicbrainz_id" },
            _ => Array.Empty<string>(),
        };

        foreach (var key in preferredKeys)
        {
            if (bridgeIds.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        return bridgeIds
            .OrderBy(entry => entry.Key, StringComparer.OrdinalIgnoreCase)
            .Select(entry => entry.Value)
            .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
    }

    private static string? ResolveAuthorForDetail(
        string? currentAuthor,
        IReadOnlyList<LibraryItemCanonicalValue> canonicalValues,
        IReadOnlyList<LibraryItemClaimRecord> claims)
    {
        var wikidataClaim = claims
            .Where(claim => string.Equals(claim.ClaimKey, MetadataFieldConstants.Author, StringComparison.OrdinalIgnoreCase))
            .Where(claim => claim.ProviderId == WellKnownProviders.Wikidata)
            .OrderByDescending(claim => claim.Confidence)
            .ThenByDescending(claim => claim.ClaimedAt)
            .FirstOrDefault();

        if (wikidataClaim is null)
        {
            return currentAuthor;
        }

        var embeddedOrManualClaim = claims
            .Where(claim => string.Equals(claim.ClaimKey, MetadataFieldConstants.Author, StringComparison.OrdinalIgnoreCase))
            .Where(claim => WellKnownProviders.IsFileSource(claim.ProviderId) || WellKnownProviders.IsUserSource(claim.ProviderId))
            .OrderByDescending(claim => claim.Confidence)
            .ThenByDescending(claim => claim.ClaimedAt)
            .FirstOrDefault();

        if (embeddedOrManualClaim is not null && embeddedOrManualClaim.Confidence > wikidataClaim.Confidence)
        {
            return embeddedOrManualClaim.ClaimValue;
        }

        var currentProvider = canonicalValues
            .FirstOrDefault(value => string.Equals(value.Key, MetadataFieldConstants.Author, StringComparison.OrdinalIgnoreCase))
            ?.WinningProviderId;

        if (Guid.TryParse(currentProvider, out var currentProviderId)
            && (WellKnownProviders.IsFileSource(currentProviderId) || WellKnownProviders.IsUserSource(currentProviderId)))
        {
            return currentAuthor;
        }

        return wikidataClaim.ClaimValue;
    }

    private static string NormalizeMediaType(string raw) => raw.ToUpperInvariant() switch
    {
        "EPUB" or "BOOK" or "EBOOK" => "Books",
        "AUDIOBOOK" => "Audiobooks",
        "MOVIE" => "Movies",
        "COMIC" => "Comics",
        _ => raw,
    };

    private static DateTimeOffset? ParseDateTimeOffset(string? value) =>
        DateTimeOffset.TryParse(value, out var parsed) ? parsed : null;

    private static PlaybackTechnicalSummary? BuildPlaybackSummary(
        Func<string, string?> canonical,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        string? videoCodec = canonical("video_codec");
        string? audioLanguage = canonical("audio_language");
        string? audioCodec = canonical("audio_codec");
        string? audioChannels = canonical("audio_channels");
        var subtitleLanguages = SplitValues(canonical("subtitle_languages"));

        int? width = ParseNullableInt(canonical("video_width"));
        int? height = ParseNullableInt(canonical("video_height"));

        var audioLanguages = SplitValues(audioLanguage);
        var summary = new PlaybackTechnicalSummary
        {
            VideoResolutionLabel = FormatResolution(width, height),
            VideoCodec = NormalizeCodec(videoCodec),
            AudioLanguage = audioLanguages.FirstOrDefault(),
            AudioCodec = NormalizeCodec(audioCodec),
            AudioChannels = FormatAudioChannels(audioChannels),
            SubtitleSummary = FormatSubtitleSummary(subtitleLanguages),
            AudioLanguages = audioLanguages,
            SubtitleLanguages = subtitleLanguages,
        };

        if (string.IsNullOrWhiteSpace(summary.VideoResolutionLabel)
            && string.IsNullOrWhiteSpace(summary.VideoCodec)
            && string.IsNullOrWhiteSpace(summary.AudioLanguage)
            && string.IsNullOrWhiteSpace(summary.AudioCodec)
            && string.IsNullOrWhiteSpace(summary.AudioChannels)
            && string.IsNullOrWhiteSpace(summary.SubtitleSummary))
        {
            return null;
        }

        return summary;
    }

    private static string? NormalizeReleaseDate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        if (DateTimeOffset.TryParse(value, out var parsed))
        {
            return parsed.ToString("MMMM d, yyyy");
        }

        return value.Length > 10 && DateTime.TryParse(value, out var parsedDate)
            ? parsedDate.ToString("MMMM d, yyyy")
            : value;
    }

    private static int? ParseNullableInt(string? value) =>
        int.TryParse(value, out var parsed) ? parsed : null;

    private static List<string> SplitValues(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? []
            : value
                .Split(['|', ',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(item => !string.IsNullOrWhiteSpace(item))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

    private static string? FormatResolution(int? width, int? height)
    {
        if (width is null || height is null || width <= 0 || height <= 0)
        {
            return null;
        }

        var h = height.Value;
        return h switch
        {
            >= 2160 => "2160p",
            >= 1440 => "1440p",
            >= 1080 => "1080p",
            >= 720 => "720p",
            >= 480 => "480p",
            _ => $"{h}p",
        };
    }

    private static string? FormatAudioChannels(string? value)
    {
        if (!int.TryParse(value, out var parsed) || parsed <= 0)
        {
            return null;
        }

        return parsed switch
        {
            1 => "Mono",
            2 => "2.0",
            _ => $"{parsed - 1}.1",
        };
    }

    private static string? FormatSubtitleSummary(IReadOnlyList<string> languages)
    {
        if (languages.Count == 0)
        {
            return null;
        }

        if (languages.Count == 1)
        {
            return languages[0];
        }

        return $"{languages[0]} + {languages.Count - 1} more";
    }

    private static string? NormalizeCodec(string? codec)
    {
        if (string.IsNullOrWhiteSpace(codec))
        {
            return null;
        }

        return codec.ToLowerInvariant() switch
        {
            "h264" => "H.264",
            "hevc" => "HEVC",
            "aac" => "AAC",
            "ac3" => "AC3",
            "eac3" => "EAC3",
            "dts" => "DTS",
            "truehd" => "TrueHD",
            "opus" => "Opus",
            "flac" => "FLAC",
            "subrip" => "SRT",
            _ => codec.ToUpperInvariant(),
        };
    }

    private sealed class ProjectionRow
    {
        public Guid EntityId { get; set; }
        public string Title { get; set; } = "";
        public string? Year { get; set; }
        public string MediaType { get; set; } = "";
        public string? CoverUrl { get; set; }
        public string? BackgroundUrl { get; set; }
        public string? BannerUrl { get; set; }
        public string? HeroUrl { get; set; }
        public double Confidence { get; set; }
        public string Status { get; set; } = "Confirmed";
        public Guid? MatchSource { get; set; }
        public string? Author { get; set; }
        public string? Director { get; set; }
        public string? Artist { get; set; }
        public string? Genre { get; set; }
        public string? Runtime { get; set; }
        public string? Description { get; set; }
        public string? Series { get; set; }
        public string? SeriesPosition { get; set; }
        public string? ShowName { get; set; }
        public string? SeasonNumber { get; set; }
        public string? EpisodeNumber { get; set; }
        public string? EpisodeTitle { get; set; }
        public string? Narrator { get; set; }
        public string? Rating { get; set; }
        public string? WikidataQid { get; set; }
        public string? WikidataStatus { get; set; }
        public string? FileName { get; set; }
        public string? FilePath { get; set; }
        public Guid? ReviewId { get; set; }
        public string? ReviewTrigger { get; set; }
        public string? CandidatesJson { get; set; }
        public bool HasUserLocks { get; set; }
        public string PipelineStep { get; set; } = "Retail";
        public string LibraryVisibility { get; set; } = "hidden";
        public bool IsReadyForLibrary { get; set; }
        public string ArtworkState { get; set; } = "pending";
        public string? ArtworkSource { get; set; }
        public string? ArtworkSettledAt { get; set; }
        public string? QidResolutionMethod { get; set; }
    }

    private sealed class StatusCountsRow
    {
        public int Total { get; set; }
        public int NeedsReview { get; set; }
        public int AutoApproved { get; set; }
        public int Edited { get; set; }
        public int Duplicate { get; set; }
        public int Staging { get; set; }
        public int MissingImages { get; set; }
        public int RecentlyUpdated { get; set; }
        public int LowConfidence { get; set; }
        public int Rejected { get; set; }
    }

    private sealed class ProjectionSummaryRow
    {
        public int TotalItems { get; set; }
        public int WithQid { get; set; }
        public int WithoutQid { get; set; }
        public int EnrichedStage3 { get; set; }
        public int NotEnrichedStage3 { get; set; }
        public int UniverseAssigned { get; set; }
        public int UniverseUnassigned { get; set; }
        public int StaleItems { get; set; }
        public int HiddenByQualityGate { get; set; }
        public int ArtPending { get; set; }
        public int RetailNeedsReview { get; set; }
        public int QidNoMatch { get; set; }
        public int CompletedWithArt { get; set; }
    }

}
