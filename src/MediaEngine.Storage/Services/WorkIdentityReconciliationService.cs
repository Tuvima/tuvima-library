using Dapper;
using MediaEngine.Domain.Contracts;
using MediaEngine.Storage.Contracts;
using Microsoft.Extensions.Logging;

namespace MediaEngine.Storage.Services;

public sealed class WorkIdentityReconciliationService : IWorkIdentityReconciliationService
{
    private readonly IDatabaseConnection _db;
    private readonly ILogger<WorkIdentityReconciliationService>? _logger;

    public WorkIdentityReconciliationService(
        IDatabaseConnection db,
        ILogger<WorkIdentityReconciliationService>? logger = null)
    {
        _db = db;
        _logger = logger;
    }

    public async Task<int> MergeDuplicateReadWorksByQidAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        using var conn = _db.CreateConnection();
        var rows = conn.Query<ReadWorkIdentityRow>("""
            WITH work_assets AS (
                SELECT
                    w.id AS WorkId,
                    w.collection_id AS CollectionId,
                    w.media_type AS MediaType,
                    w.work_kind AS WorkKind,
                    w.parent_work_id AS ParentWorkId,
                    w.ordinal AS Ordinal,
                    COALESCE(
                        NULLIF(TRIM(w.wikidata_qid), ''),
                        NULLIF(TRIM((SELECT value FROM canonical_values WHERE entity_id = w.id AND key = 'wikidata_qid' LIMIT 1)), ''),
                        NULLIF(TRIM((SELECT value FROM canonical_values WHERE entity_id = ma.id AND key = 'wikidata_qid' LIMIT 1)), '')
                    ) AS IdentityQid,
                    (SELECT cv.value FROM canonical_values cv
                     WHERE cv.key = 'calibre_uuid' AND (cv.entity_id = w.id OR cv.entity_id IN
                         (SELECT a.id FROM editions ed JOIN media_assets a ON a.edition_id = ed.id WHERE ed.work_id = w.id))
                     GROUP BY cv.key HAVING COUNT(DISTINCT LOWER(cv.value)) = 1) AS CalibreUuid,
                    MIN(mc.claimed_at) AS CreatedAt,
                    COUNT(DISTINCT ma.id) AS AssetCount
                FROM works w
                INNER JOIN editions e ON e.work_id = w.id
                INNER JOIN media_assets ma ON ma.edition_id = e.id
                LEFT JOIN metadata_claims mc ON mc.entity_id = ma.id
                WHERE w.work_kind IN ('standalone', 'child')
                  AND w.media_type IN ('Books', 'Audiobooks')
                GROUP BY w.id
            )
            SELECT WorkId, CollectionId, MediaType, WorkKind, ParentWorkId, Ordinal,
                   IdentityQid, CalibreUuid, CreatedAt, AssetCount
            FROM work_assets
            WHERE (IdentityQid IS NOT NULL
              AND IdentityQid <> ''
              AND IdentityQid NOT LIKE 'NF%') OR CalibreUuid IS NOT NULL;
            """).AsList();

        // Connect formats through explicit identities only; never merge by title.
        var parents = rows.ToDictionary(row => row.WorkId, row => row.WorkId);
        Guid Root(Guid id) => parents[id] == id ? id : parents[id] = Root(parents[id]);
        var identities = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in rows)
        {
            foreach (var identity in IdentityKeys(row))
            {
                if (identities.TryGetValue(identity, out var other))
                {
                    parents[Root(row.WorkId)] = Root(other);
                }
                else
                {
                    identities[identity] = row.WorkId;
                }
            }
        }
        var groups = rows.GroupBy(row => Root(row.WorkId)).Where(group => group.Count() > 1).ToList();

        var merged = 0;
        foreach (var group in groups)
        {
            ct.ThrowIfCancellationRequested();

            var siblings = group.ToList();
            var qids = siblings.Select(row => row.IdentityQid).Where(IsResolvedQid)
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (qids.Count > 1)
            {
                _logger?.LogWarning("Skipped book format consolidation with conflicting Wikidata identities.");
                continue;
            }
            var target = ChooseCanonical(siblings);
            foreach (var source in siblings.Where(row => row.WorkId != target.WorkId))
            {
                // Each merge runs through its own ExecuteWriteAsync call so the
                // (non-reentrant) global write lock is acquired and released once per
                // merge, sequentially. This method's own connection (`conn`, above) is
                // read-only and holds no lock, so there is no nesting/deadlock risk here.
                merged += await MergeWorkIntoAsync(source.WorkId, target.WorkId, qids.FirstOrDefault(), ct)
                    .ConfigureAwait(false);
            }
        }

        if (merged > 0)
        {
            _logger?.LogInformation(
                "Merged {Count} duplicate Read work(s) by media type and trusted identity.",
                merged);
        }

        return merged;
    }

    private static bool IsResolvedQid(string? value) => !string.IsNullOrWhiteSpace(value)
        && !value.StartsWith("NF", StringComparison.OrdinalIgnoreCase);

    private static IEnumerable<string> IdentityKeys(ReadWorkIdentityRow row)
    {
        var mediaType = NormalizeMediaType(row.MediaType);
        if (IsResolvedQid(row.IdentityQid))
        {
            yield return $"{mediaType}:qid:{row.IdentityQid}";
        }
        if (mediaType == "BOOKS" && Guid.TryParse(row.CalibreUuid, out var uuid) && uuid != Guid.Empty)
        {
            yield return $"{mediaType}:calibre:{uuid:D}";
        }
    }

    public async Task<int> AlignAudiobookAuthorsWithBooksByQidAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        using var conn = _db.CreateConnection();
        var variants = conn.Query<ReadVariantAuthorScopeRow>(
            """
            SELECT DISTINCT
                   w.media_type AS MediaType,
                   COALESCE(gp.id, p.id, w.id) AS RootWorkId,
                   w.id AS WorkId,
                   ma.id AS AssetId,
                   COALESCE(
                       NULLIF(TRIM(w.wikidata_qid), ''),
                       NULLIF(TRIM((SELECT value
                                   FROM canonical_values
                                   WHERE entity_id = w.id
                                     AND key = 'wikidata_qid'
                                   LIMIT 1)), ''),
                       NULLIF(TRIM((SELECT value
                                   FROM canonical_values
                                   WHERE entity_id = ma.id
                                     AND key = 'wikidata_qid'
                                   LIMIT 1)), '')
                   ) AS IdentityQid
            FROM works w
            INNER JOIN editions e ON e.work_id = w.id
            INNER JOIN media_assets ma ON ma.edition_id = e.id
            LEFT JOIN works p ON p.id = w.parent_work_id
            LEFT JOIN works gp ON gp.id = p.parent_work_id
            WHERE w.work_kind IN ('standalone', 'child')
              AND w.media_type IN ('Books', 'Audiobooks')
              AND w.is_catalog_only = 0
              AND ma.file_path_root IS NOT NULL
              AND TRIM(ma.file_path_root) <> ''
              AND ma.status <> 'Orphaned';
            """).AsList();

        var crossFormatGroups = variants
            .Where(row => !string.IsNullOrWhiteSpace(row.IdentityQid)
                          && !row.IdentityQid.StartsWith("NF", StringComparison.OrdinalIgnoreCase))
            .GroupBy(row => row.IdentityQid, StringComparer.OrdinalIgnoreCase)
            .Where(group =>
                group.Any(row => string.Equals(row.MediaType, "Books", StringComparison.OrdinalIgnoreCase))
                && group.Any(row => string.Equals(row.MediaType, "Audiobooks", StringComparison.OrdinalIgnoreCase)))
            .ToList();
        if (crossFormatGroups.Count == 0)
        {
            return 0;
        }

        var bookAuthorScopeIds = crossFormatGroups
            .SelectMany(group => group)
            .Where(row => string.Equals(row.MediaType, "Books", StringComparison.OrdinalIgnoreCase))
            .SelectMany(row => new[] { row.RootWorkId, row.WorkId, row.AssetId })
            .Distinct()
            .ToList();
        var authorRows = conn.Query<CanonicalAuthorRow>(
            """
            SELECT entity_id AS EntityId,
                   ordinal AS Ordinal,
                   value AS Value,
                   value_qid AS ValueQid
            FROM canonical_value_arrays
            WHERE entity_id IN @BookAuthorScopeIds
              AND key = 'author'
              AND NULLIF(TRIM(value), '') IS NOT NULL
              AND NULLIF(TRIM(value_qid), '') IS NOT NULL
            ORDER BY entity_id, ordinal;
            """,
            new { BookAuthorScopeIds = bookAuthorScopeIds.Select(GuidSql.ToBlob).ToArray() }).AsList();
        var authorsByEntity = authorRows
            .GroupBy(row => row.EntityId)
            .ToDictionary(group => group.Key, group => group.ToList());

        var desiredByAudiobookScope = new Dictionary<Guid, List<CanonicalAuthorRow>>();
        foreach (var group in crossFormatGroups)
        {
            var sourceAuthors = group
                .Where(row => string.Equals(row.MediaType, "Books", StringComparison.OrdinalIgnoreCase))
                .SelectMany(row => PreferredAuthorRows(row, authorsByEntity))
                .GroupBy(row => row.ValueQid ?? row.Value, StringComparer.OrdinalIgnoreCase)
                .Select(authorGroup => authorGroup.First())
                .ToList();
            if (sourceAuthors.Count == 0)
            {
                continue;
            }

            foreach (var audiobook in group
                         .Where(row => string.Equals(row.MediaType, "Audiobooks", StringComparison.OrdinalIgnoreCase))
                         .DistinctBy(row => row.WorkId))
            {
                AddDesiredAuthors(audiobook.WorkId, sourceAuthors);
                AddDesiredAuthors(audiobook.RootWorkId, sourceAuthors);
            }

            void AddDesiredAuthors(Guid targetId, IReadOnlyList<CanonicalAuthorRow> authors)
            {
                if (!desiredByAudiobookScope.TryGetValue(targetId, out var desired))
                {
                    desired = [];
                    desiredByAudiobookScope[targetId] = desired;
                }

                foreach (var author in authors)
                {
                    if (!desired.Any(existing =>
                            string.Equals(
                                existing.ValueQid ?? existing.Value,
                                author.ValueQid ?? author.Value,
                                StringComparison.OrdinalIgnoreCase)))
                    {
                        desired.Add(author);
                    }
                }
            }
        }

        if (desiredByAudiobookScope.Count == 0)
        {
            return 0;
        }

        var targetIds = desiredByAudiobookScope.Keys.ToList();
        var currentRows = conn.Query<CanonicalAuthorRow>(
            """
            SELECT entity_id AS EntityId,
                   ordinal AS Ordinal,
                   value AS Value,
                   value_qid AS ValueQid
            FROM canonical_value_arrays
            WHERE entity_id IN @TargetIds
              AND key = 'author'
            ORDER BY entity_id, ordinal;
            """,
            new { TargetIds = targetIds.Select(GuidSql.ToBlob).ToArray() }).AsList();
        var currentByTarget = currentRows
            .GroupBy(row => row.EntityId)
            .ToDictionary(group => group.Key, group => group.ToList());
        var changes = desiredByAudiobookScope
            .Where(pair => !AuthorRowsEqual(
                currentByTarget.GetValueOrDefault(pair.Key) ?? [],
                pair.Value))
            .ToList();
        if (changes.Count == 0)
        {
            return 0;
        }

        await _db.ExecuteWriteAsync((writeConnection, transaction, innerCt) =>
        {
            foreach (var (targetId, authors) in changes)
            {
                innerCt.ThrowIfCancellationRequested();
                writeConnection.Execute(
                    """
                    DELETE FROM canonical_value_arrays
                    WHERE entity_id = @TargetId
                      AND key = 'author';
                    """,
                    new { TargetId = targetId },
                    transaction);

                writeConnection.Execute(
                    """
                    INSERT INTO canonical_value_arrays
                        (entity_id, key, ordinal, value, value_qid)
                    VALUES
                        (@EntityId, 'author', @Ordinal, @Value, @ValueQid);
                    """,
                    authors.Select((author, ordinal) => new
                    {
                        EntityId = targetId,
                        Ordinal = ordinal,
                        author.Value,
                        author.ValueQid,
                    }),
                    transaction);
            }

            return changes.Count;
        }, ct).ConfigureAwait(false);

        _logger?.LogInformation(
            "Aligned canonical book author identities onto {Count} audiobook work scope(s).",
            changes.Count);
        return changes.Count;
    }

    private static bool AuthorRowsEqual(
        IReadOnlyList<CanonicalAuthorRow> current,
        IReadOnlyList<CanonicalAuthorRow> desired)
    {
        if (current.Count != desired.Count)
        {
            return false;
        }

        return current
            .OrderBy(row => row.Ordinal)
            .Zip(desired, (left, right) =>
                string.Equals(left.Value, right.Value, StringComparison.Ordinal)
                && string.Equals(left.ValueQid, right.ValueQid, StringComparison.OrdinalIgnoreCase))
            .All(matches => matches);
    }

    private static IReadOnlyList<CanonicalAuthorRow> PreferredAuthorRows(
        ReadVariantAuthorScopeRow row,
        IReadOnlyDictionary<Guid, List<CanonicalAuthorRow>> authorsByEntity)
    {
        foreach (var entityId in new[] { row.RootWorkId, row.WorkId, row.AssetId }.Distinct())
        {
            if (authorsByEntity.TryGetValue(entityId, out var authors) && authors.Count > 0)
            {
                return authors;
            }
        }

        return [];
    }

    private static string NormalizeMediaType(string mediaType) =>
        string.IsNullOrWhiteSpace(mediaType)
            ? string.Empty
            : mediaType.Trim().ToUpperInvariant();

    private static ReadWorkIdentityRow ChooseCanonical(IReadOnlyList<ReadWorkIdentityRow> rows) =>
        rows
            .OrderBy(row => IsResolvedQid(row.IdentityQid) ? 0 : 1)
            .ThenBy(row => string.Equals(row.WorkKind, "child", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(row => row.CollectionId.HasValue ? 0 : 1)
            .ThenBy(row => string.Equals(row.MediaType, "Audiobooks", StringComparison.OrdinalIgnoreCase) ? 1 : 0)
            .ThenByDescending(row => row.AssetCount)
            .ThenBy(row => row.CreatedAt ?? string.Empty, StringComparer.Ordinal)
            .First();

    private Task<int> MergeWorkIntoAsync(Guid sourceWorkId, Guid targetWorkId, string? qid, CancellationToken ct) =>
        _db.ExecuteWriteAsync((conn, tx, innerCt) =>
        {
            innerCt.ThrowIfCancellationRequested();

            var sourceExists = conn.ExecuteScalar<int>(
                "SELECT COUNT(1) FROM works WHERE id = @source;",
                new { source = sourceWorkId },
                tx) > 0;
            if (!sourceExists)
            {
                return 0;
            }

            var now = DateTimeOffset.UtcNow.ToString("O");
            var args = new
            {
                source = sourceWorkId,
                target = targetWorkId,
                targetText = targetWorkId.ToString("D"),
                qid,
                now,
            };

            MergeDuplicateEntityAssets(conn, tx, sourceWorkId, targetWorkId, now, innerCt);

            conn.Execute("""
                UPDATE works
                SET wikidata_qid = COALESCE(NULLIF(TRIM(wikidata_qid), ''), @qid)
                WHERE id = @target;

                UPDATE editions
                SET work_id = @target
                WHERE work_id = @source;

                INSERT INTO profile_saved_items(profile_id, entity_kind, entity_id, saved_at, position)
                SELECT profile_id, entity_kind, @target, saved_at, position FROM profile_saved_items
                WHERE entity_id = @source AND entity_kind IN ('Book', 'Audiobook')
                ON CONFLICT(profile_id, entity_kind, entity_id) DO NOTHING;
                DELETE FROM profile_saved_items WHERE entity_id = @source AND entity_kind IN ('Book', 'Audiobook');

                INSERT INTO profile_reactions(profile_id, entity_kind, entity_id, reaction, updated_at)
                SELECT profile_id, entity_kind, @target, reaction, updated_at FROM profile_reactions
                WHERE entity_id = @source AND entity_kind IN ('Book', 'Audiobook')
                ON CONFLICT(profile_id, entity_kind, entity_id) DO UPDATE SET
                    reaction = excluded.reaction, updated_at = excluded.updated_at
                    WHERE excluded.updated_at > profile_reactions.updated_at;
                DELETE FROM profile_reactions WHERE entity_id = @source AND entity_kind IN ('Book', 'Audiobook');

                INSERT INTO profile_work_preferences(profile_id, work_id, local_tags_json, revision, updated_at)
                SELECT profile_id, @target, local_tags_json, revision, updated_at FROM profile_work_preferences WHERE work_id = @source
                ON CONFLICT(profile_id, work_id) DO UPDATE SET
                    local_tags_json = excluded.local_tags_json, revision = MAX(profile_work_preferences.revision, excluded.revision) + 1,
                    updated_at = excluded.updated_at WHERE excluded.updated_at > profile_work_preferences.updated_at;
                DELETE FROM profile_work_preferences WHERE work_id = @source;

                INSERT OR IGNORE INTO canonical_values
                    (entity_id, key, value, last_scored_at, is_conflicted,
                     winning_provider_id, needs_review)
                SELECT @target, key, value, last_scored_at, is_conflicted,
                       winning_provider_id, needs_review
                FROM canonical_values
                WHERE entity_id = @source;

                DELETE FROM canonical_values
                WHERE entity_id = @source;

                INSERT INTO canonical_values(entity_id, key, value, last_scored_at)
                VALUES (@source, 'merged_into_work_id', @targetText, @now);

                INSERT OR IGNORE INTO canonical_value_arrays
                    (entity_id, key, ordinal, value, value_qid)
                SELECT @target, key, ordinal, value, value_qid
                FROM canonical_value_arrays
                WHERE entity_id = @source;

                DELETE FROM canonical_value_arrays
                WHERE entity_id = @source;

                UPDATE metadata_claims
                SET entity_id = @target
                WHERE entity_id = @source;

                UPDATE OR IGNORE bridge_ids
                SET entity_id = @target
                WHERE entity_id = @source;

                DELETE FROM bridge_ids
                WHERE entity_id = @source;

                UPDATE entity_assets
                SET entity_id = @target
                WHERE entity_type = 'Work'
                  AND entity_id = @source;

                UPDATE collection_items
                SET work_id = @target
                WHERE work_id = @source;

                UPDATE series_manifest_items
                SET linked_work_id = @target
                WHERE linked_work_id = @source;

                UPDATE review_queue
                SET status = 'Resolved',
                    resolved_at = @now,
                    resolved_by = 'system:work-identity-merge'
                WHERE entity_type = 'Work'
                  AND entity_id = @source
                  AND status = 'Pending';

                UPDATE review_queue
                SET entity_id = @target
                WHERE entity_type = 'Work'
                  AND entity_id = @source;

                DELETE FROM works
                WHERE id = @source
                  AND NOT EXISTS (
                      SELECT 1 FROM editions e WHERE e.work_id = @source
                  )
                  AND NOT EXISTS (
                      SELECT 1 FROM works child WHERE child.parent_work_id = @source
                  );
                """, args, tx);

            return 1;
        }, ct);

    private static void MergeDuplicateEntityAssets(
        System.Data.IDbConnection conn,
        System.Data.IDbTransaction tx,
        Guid sourceWorkId,
        Guid targetWorkId,
        string now,
        CancellationToken ct)
    {
        var collisions = conn.Query<EntityAssetMergePair>(
            """
            SELECT
                s.id AS SourceId,
                t.id AS TargetId,
                s.local_image_path AS SourceLocalImagePath,
                t.local_image_path AS TargetLocalImagePath,
                s.local_image_path_s AS SourceLocalImagePathSmall,
                t.local_image_path_s AS TargetLocalImagePathSmall,
                s.local_image_path_m AS SourceLocalImagePathMedium,
                t.local_image_path_m AS TargetLocalImagePathMedium,
                s.local_image_path_l AS SourceLocalImagePathLarge,
                t.local_image_path_l AS TargetLocalImagePathLarge,
                s.source_provider AS SourceProvider,
                t.source_provider AS TargetProvider,
                s.width_px AS SourceWidthPx,
                t.width_px AS TargetWidthPx,
                s.height_px AS SourceHeightPx,
                t.height_px AS TargetHeightPx,
                s.aspect_class AS SourceAspectClass,
                t.aspect_class AS TargetAspectClass,
                s.primary_hex AS SourcePrimaryHex,
                t.primary_hex AS TargetPrimaryHex,
                s.secondary_hex AS SourceSecondaryHex,
                t.secondary_hex AS TargetSecondaryHex,
                s.accent_hex AS SourceAccentHex,
                t.accent_hex AS TargetAccentHex,
                s.asset_class AS SourceAssetClass,
                t.asset_class AS TargetAssetClass,
                s.storage_location AS SourceStorageLocation,
                t.storage_location AS TargetStorageLocation,
                s.owner_scope AS SourceOwnerScope,
                t.owner_scope AS TargetOwnerScope,
                s.is_preferred AS SourceIsPreferred,
                t.is_preferred AS TargetIsPreferred,
                s.is_user_override AS SourceIsUserOverride,
                t.is_user_override AS TargetIsUserOverride,
                s.is_locally_exported AS SourceIsLocallyExported,
                t.is_locally_exported AS TargetIsLocallyExported,
                s.is_preferred_exported AS SourceIsPreferredExported,
                t.is_preferred_exported AS TargetIsPreferredExported,
                s.created_at AS SourceCreatedAt,
                t.created_at AS TargetCreatedAt
            FROM entity_assets s
            INNER JOIN entity_assets t
                ON t.entity_type = 'Work'
               AND t.entity_id = @target
               AND t.asset_type = s.asset_type
               AND t.image_url = s.image_url COLLATE NOCASE
            WHERE s.entity_type = 'Work'
              AND s.entity_id = @source
              AND s.image_url IS NOT NULL
              AND length(trim(s.image_url)) > 0;
            """,
            new { source = sourceWorkId, target = targetWorkId },
            tx).AsList();

        foreach (var pair in collisions)
        {
            ct.ThrowIfCancellationRequested();

            var sourceWins = AssetPriority(pair.SourceIsUserOverride, pair.SourceIsPreferred,
                    pair.SourceLocalImagePath, pair.SourceLocalImagePathSmall, pair.SourceLocalImagePathMedium,
                    pair.SourceLocalImagePathLarge, pair.SourceWidthPx, pair.SourceHeightPx, pair.SourceCreatedAt)
                > AssetPriority(pair.TargetIsUserOverride, pair.TargetIsPreferred,
                    pair.TargetLocalImagePath, pair.TargetLocalImagePathSmall, pair.TargetLocalImagePathMedium,
                    pair.TargetLocalImagePathLarge, pair.TargetWidthPx, pair.TargetHeightPx, pair.TargetCreatedAt);

            var survivorId = sourceWins ? pair.SourceId : pair.TargetId;
            var duplicateId = sourceWins ? pair.TargetId : pair.SourceId;
            var primary = sourceWins ? pair.Source() : pair.Target();
            var secondary = sourceWins ? pair.Target() : pair.Source();

            conn.Execute(
                """
                DELETE FROM entity_assets WHERE id = @duplicateId;

                UPDATE entity_assets
                SET entity_id = @target,
                    local_image_path = @LocalImagePath,
                    local_image_path_s = @LocalImagePathSmall,
                    local_image_path_m = @LocalImagePathMedium,
                    local_image_path_l = @LocalImagePathLarge,
                    source_provider = @SourceProvider,
                    width_px = @WidthPx,
                    height_px = @HeightPx,
                    aspect_class = @AspectClass,
                    primary_hex = @PrimaryHex,
                    secondary_hex = @SecondaryHex,
                    accent_hex = @AccentHex,
                    asset_class = @AssetClass,
                    storage_location = @StorageLocation,
                    owner_scope = @OwnerScope,
                    is_preferred = @IsPreferred,
                    is_user_override = @IsUserOverride,
                    is_locally_exported = @IsLocallyExported,
                    is_preferred_exported = @IsPreferredExported,
                    updated_at = @now
                WHERE id = @survivorId;
                """,
                new
                {
                    duplicateId,
                    survivorId,
                    target = targetWorkId,
                    LocalImagePath = primary.LocalImagePath ?? secondary.LocalImagePath,
                    LocalImagePathSmall = primary.LocalImagePathSmall ?? secondary.LocalImagePathSmall,
                    LocalImagePathMedium = primary.LocalImagePathMedium ?? secondary.LocalImagePathMedium,
                    LocalImagePathLarge = primary.LocalImagePathLarge ?? secondary.LocalImagePathLarge,
                    SourceProvider = primary.SourceProvider ?? secondary.SourceProvider,
                    WidthPx = MaxNullable(primary.WidthPx, secondary.WidthPx),
                    HeightPx = MaxNullable(primary.HeightPx, secondary.HeightPx),
                    AspectClass = PreferMeaningful(primary.AspectClass, secondary.AspectClass, "UnsupportedRect"),
                    PrimaryHex = primary.PrimaryHex ?? secondary.PrimaryHex,
                    SecondaryHex = primary.SecondaryHex ?? secondary.SecondaryHex,
                    AccentHex = primary.AccentHex ?? secondary.AccentHex,
                    AssetClass = PreferMeaningful(primary.AssetClass, secondary.AssetClass, "Artwork"),
                    StorageLocation = PreferMeaningful(primary.StorageLocation, secondary.StorageLocation, "Central"),
                    OwnerScope = PreferMeaningful(primary.OwnerScope, secondary.OwnerScope, "Unknown"),
                    IsPreferred = primary.IsPreferred || secondary.IsPreferred,
                    IsUserOverride = primary.IsUserOverride || secondary.IsUserOverride,
                    IsLocallyExported = primary.IsLocallyExported || secondary.IsLocallyExported,
                    IsPreferredExported = primary.IsPreferredExported || secondary.IsPreferredExported,
                    now,
                },
                tx);
        }
    }

    private static long AssetPriority(
        bool userOverride,
        bool preferred,
        string? localPath,
        string? smallPath,
        string? mediumPath,
        string? largePath,
        int? width,
        int? height,
        string? createdAt)
    {
        var renditionCount = new[] { localPath, smallPath, mediumPath, largePath }.Count(path => !string.IsNullOrWhiteSpace(path));
        var pixels = Math.Min((long)(width ?? 0) * (height ?? 0), 999_999_999L);
        var stableAgeTieBreaker = DateTimeOffset.TryParse(createdAt, out var created)
            ? Math.Max(0, DateTimeOffset.MaxValue.UtcTicks - created.UtcTicks) % 1_000
            : 0;
        return (userOverride ? 1_000_000_000_000L : 0)
               + (preferred ? 100_000_000_000L : 0)
               + renditionCount * 10_000_000_000L
               + pixels * 10
               + stableAgeTieBreaker;
    }

    private static int? MaxNullable(int? left, int? right) =>
        left.HasValue || right.HasValue ? Math.Max(left ?? 0, right ?? 0) : null;

    private static string PreferMeaningful(string? primary, string? secondary, string defaultValue) =>
        !string.IsNullOrWhiteSpace(primary) && !string.Equals(primary, defaultValue, StringComparison.OrdinalIgnoreCase)
            ? primary
            : !string.IsNullOrWhiteSpace(secondary)
                ? secondary
                : defaultValue;

    private sealed class ReadWorkIdentityRow
    {
        public Guid WorkId { get; set; }
        public Guid? CollectionId { get; set; }
        public string MediaType { get; set; } = string.Empty;
        public string WorkKind { get; set; } = string.Empty;
        public Guid? ParentWorkId { get; set; }
        public int? Ordinal { get; set; }
        public string IdentityQid { get; set; } = string.Empty;
        public string? CalibreUuid { get; set; }
        public string? CreatedAt { get; set; }
        public int AssetCount { get; set; }
    }

    private sealed class ReadVariantAuthorScopeRow
    {
        public string MediaType { get; set; } = string.Empty;
        public Guid RootWorkId { get; set; }
        public Guid WorkId { get; set; }
        public Guid AssetId { get; set; }
        public string IdentityQid { get; set; } = string.Empty;
    }

    private sealed class CanonicalAuthorRow
    {
        public Guid EntityId { get; set; }
        public int Ordinal { get; set; }
        public string Value { get; set; } = string.Empty;
        public string? ValueQid { get; set; }
    }

    private sealed class EntityAssetMergePair
    {
        public Guid SourceId { get; set; }
        public Guid TargetId { get; set; }
        public string? SourceLocalImagePath { get; set; }
        public string? TargetLocalImagePath { get; set; }
        public string? SourceLocalImagePathSmall { get; set; }
        public string? TargetLocalImagePathSmall { get; set; }
        public string? SourceLocalImagePathMedium { get; set; }
        public string? TargetLocalImagePathMedium { get; set; }
        public string? SourceLocalImagePathLarge { get; set; }
        public string? TargetLocalImagePathLarge { get; set; }
        public string? SourceProvider { get; set; }
        public string? TargetProvider { get; set; }
        public int? SourceWidthPx { get; set; }
        public int? TargetWidthPx { get; set; }
        public int? SourceHeightPx { get; set; }
        public int? TargetHeightPx { get; set; }
        public string? SourceAspectClass { get; set; }
        public string? TargetAspectClass { get; set; }
        public string? SourcePrimaryHex { get; set; }
        public string? TargetPrimaryHex { get; set; }
        public string? SourceSecondaryHex { get; set; }
        public string? TargetSecondaryHex { get; set; }
        public string? SourceAccentHex { get; set; }
        public string? TargetAccentHex { get; set; }
        public string? SourceAssetClass { get; set; }
        public string? TargetAssetClass { get; set; }
        public string? SourceStorageLocation { get; set; }
        public string? TargetStorageLocation { get; set; }
        public string? SourceOwnerScope { get; set; }
        public string? TargetOwnerScope { get; set; }
        public bool SourceIsPreferred { get; set; }
        public bool TargetIsPreferred { get; set; }
        public bool SourceIsUserOverride { get; set; }
        public bool TargetIsUserOverride { get; set; }
        public bool SourceIsLocallyExported { get; set; }
        public bool TargetIsLocallyExported { get; set; }
        public bool SourceIsPreferredExported { get; set; }
        public bool TargetIsPreferredExported { get; set; }
        public string? SourceCreatedAt { get; set; }
        public string? TargetCreatedAt { get; set; }

        public EntityAssetMergeValues Source() => new(
            SourceLocalImagePath, SourceLocalImagePathSmall, SourceLocalImagePathMedium, SourceLocalImagePathLarge,
            SourceProvider, SourceWidthPx, SourceHeightPx, SourceAspectClass, SourcePrimaryHex, SourceSecondaryHex,
            SourceAccentHex, SourceAssetClass, SourceStorageLocation, SourceOwnerScope, SourceIsPreferred,
            SourceIsUserOverride, SourceIsLocallyExported, SourceIsPreferredExported);

        public EntityAssetMergeValues Target() => new(
            TargetLocalImagePath, TargetLocalImagePathSmall, TargetLocalImagePathMedium, TargetLocalImagePathLarge,
            TargetProvider, TargetWidthPx, TargetHeightPx, TargetAspectClass, TargetPrimaryHex, TargetSecondaryHex,
            TargetAccentHex, TargetAssetClass, TargetStorageLocation, TargetOwnerScope, TargetIsPreferred,
            TargetIsUserOverride, TargetIsLocallyExported, TargetIsPreferredExported);
    }

    private sealed record EntityAssetMergeValues(
        string? LocalImagePath,
        string? LocalImagePathSmall,
        string? LocalImagePathMedium,
        string? LocalImagePathLarge,
        string? SourceProvider,
        int? WidthPx,
        int? HeightPx,
        string? AspectClass,
        string? PrimaryHex,
        string? SecondaryHex,
        string? AccentHex,
        string? AssetClass,
        string? StorageLocation,
        string? OwnerScope,
        bool IsPreferred,
        bool IsUserOverride,
        bool IsLocallyExported,
        bool IsPreferredExported);
}
