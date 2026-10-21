using System.Data;
using System.Globalization;
using System.Text.Json;
using Dapper;
using MediaEngine.Domain;
using MediaEngine.Domain.Constants;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Entities;
using MediaEngine.Domain.Enums;
using MediaEngine.Domain.Models;
using MediaEngine.Storage.Contracts;
using MediaEngine.Storage.Services;
using Microsoft.Data.Sqlite;

namespace MediaEngine.Storage;

/// <summary>
/// The reads behind the Match step (the items to move, the places they can go, the destination's own children and parts) and the one
/// transaction that moves them. A move places each item in the destination by one of three ways: it takes a free slot (the Work is
/// relocated and numbered), it attaches to an entry that already has a Work (its files become Editions of that Work and the emptied
/// title is removed), or it is added anyway (relocated with no number and flagged "not matched"). Everything is checked again inside
/// the transaction; any failed check changes nothing.
/// </summary>
public sealed class LibraryMoveRepository(IDatabaseConnection db) : ILibraryMoveRepository
{
    // Jobs in these states are finished or waiting for a person, so they are not "processing" (same set as the delete preview).
    private static readonly string[] RestingJobStates =
    [
        nameof(IdentityJobState.Ready),
        nameof(IdentityJobState.ReadyWithoutUniverse),
        nameof(IdentityJobState.Failed),
        nameof(IdentityJobState.RetailNoMatch),
        nameof(IdentityJobState.QidNoMatch),
        nameof(IdentityJobState.QidNeedsReview),
    ];

    // The values that say where an item sits in its list. They come and go with a move, so they are never "details the person set".
    private static readonly string[] NumberKeys = PlacementNumbers.Keys;

    private const string PartNumberKey = PlacementNumbers.AudiobookPartNumberKey;
    private const string PartCountKey = "audiobook_part_count";
    private const string IdentityRevisionKey = "identity_revision";

    // ----- reads -----------------------------------------------------------------------------------------------------------

    public Task<IReadOnlyList<LibraryMoveItem>> GetItemsAsync(IReadOnlyList<Guid> itemIds, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(itemIds);
        ct.ThrowIfCancellationRequested();
        if (itemIds.Count == 0)
        {
            return Task.FromResult<IReadOnlyList<LibraryMoveItem>>([]);
        }

        using var connection = db.CreateConnection();
        IReadOnlyList<LibraryMoveItem> items = LoadItems(connection, null, itemIds);
        return Task.FromResult(items);
    }

    public Task<IReadOnlyList<LibraryMoveDestinationRow>> FindDestinationsAsync(
        LibraryMoveItem item,
        string? query,
        int limit,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(item);
        ct.ThrowIfCancellationRequested();
        limit = Math.Clamp(limit, 1, 100);
        var like = string.IsNullOrWhiteSpace(query) ? null : "%" + EscapeLike(query.Trim()) + "%";
        var exclude = GuidSql.ToDb(item.ParentWorkId);
        var results = new List<LibraryMoveDestinationRow>();

        using var connection = db.CreateConnection();

        if (item.IsEdition)
        {
            // An audiobook's file goes to another audiobook. Formats of the same title are never offered: they join by Match.
            if (string.Equals(item.MediaType, "Audiobooks", StringComparison.OrdinalIgnoreCase))
            {
                results.AddRange(connection.Query<DestinationRow>($"""
                    -- readiness: moves work on titles by id or by the place they sit in, whatever their readiness
                    SELECT * FROM (
                        SELECT w.id AS Id, 'audiobook' AS Kind, w.media_type AS MediaType,
                               {WorkTitleSql("w")} AS Label,
                               {AuthorSql("w")} AS ParentLabel,
                               NULL AS SeasonOrdinal
                        FROM works w
                        WHERE w.media_type = 'Audiobooks'
                          AND w.work_kind IN ('child', 'standalone')
                          AND w.id <> @workId
                          AND EXISTS (SELECT 1 FROM editions e WHERE e.work_id = w.id)
                    )
                    WHERE @like IS NULL OR Label LIKE @like ESCAPE '\' OR ParentLabel LIKE @like ESCAPE '\'
                    ORDER BY Label COLLATE NOCASE
                    LIMIT @limit;
                    """, new { workId = item.WorkId, like, limit }).Select(row => row.ToDestination()));
            }

            return Task.FromResult<IReadOnlyList<LibraryMoveDestinationRow>>(results);
        }

        if (string.Equals(item.MediaType, "Music", StringComparison.OrdinalIgnoreCase))
        {
            results.AddRange(connection.Query<DestinationRow>($"""
                -- readiness: moves work on titles by id or by the place they sit in, whatever their readiness
                SELECT * FROM (
                    SELECT w.id AS Id, 'album' AS Kind, w.media_type AS MediaType,
                           {ParentLabelSql("w")} AS Label,
                           {ArtistSql("w")} AS ParentLabel,
                           NULL AS SeasonOrdinal
                    FROM works w
                    WHERE w.media_type = 'Music' AND w.work_kind = 'parent' AND (@exclude IS NULL OR w.id <> @exclude)
                )
                WHERE @like IS NULL OR Label LIKE @like ESCAPE '\' OR ParentLabel LIKE @like ESCAPE '\'
                ORDER BY Label COLLATE NOCASE
                LIMIT @limit;
                """, new { exclude, like, limit }).Select(row => row.ToDestination()));
        }
        else if (string.Equals(item.MediaType, "TV", StringComparison.OrdinalIgnoreCase))
        {
            results.AddRange(connection.Query<DestinationRow>($"""
                -- readiness: moves work on titles by id or by the place they sit in, whatever their readiness
                SELECT * FROM (
                    SELECT w.id AS Id, 'season' AS Kind, w.media_type AS MediaType,
                           CASE WHEN COALESCE(w.ordinal, 0) = 0 THEN 'Specials' ELSE 'Season ' || w.ordinal END AS Label,
                           {ParentLabelSql("p")} AS ParentLabel,
                           COALESCE(w.ordinal, 0) AS SeasonOrdinal
                    FROM works w
                    JOIN works p ON p.id = w.parent_work_id
                    WHERE w.media_type = 'TV' AND w.work_kind = 'parent' AND (@exclude IS NULL OR w.id <> @exclude)
                )
                WHERE @like IS NULL OR Label LIKE @like ESCAPE '\' OR ParentLabel LIKE @like ESCAPE '\'
                ORDER BY ParentLabel COLLATE NOCASE, CASE WHEN SeasonOrdinal = 0 THEN 1 ELSE 0 END, SeasonOrdinal
                LIMIT @limit;
                """, new { exclude, like, limit }).Select(row => row.ToDestination()));
        }
        else if (item.MediaType is "Books" or "Comics" or "Audiobooks" or "Movies")
        {
            results.AddRange(connection.Query<DestinationRow>($"""
                -- readiness: moves work on titles by id or by the place they sit in, whatever their readiness
                SELECT * FROM (
                    SELECT w.id AS Id, 'series' AS Kind, w.media_type AS MediaType,
                           {ParentLabelSql("w")} AS Label,
                           {AuthorSql("w")} AS ParentLabel,
                           NULL AS SeasonOrdinal
                    FROM works w
                    WHERE w.media_type = @mediaType AND w.work_kind = 'parent' AND w.parent_work_id IS NULL
                      AND (@exclude IS NULL OR w.id <> @exclude)
                )
                WHERE @like IS NULL OR Label LIKE @like ESCAPE '\' OR ParentLabel LIKE @like ESCAPE '\'
                ORDER BY Label COLLATE NOCASE
                LIMIT @limit;
                """, new { mediaType = item.MediaType, exclude, like, limit }).Select(row => row.ToDestination()));
        }

        // Hand-picked collections the library owns (never a person's private one) that do not already hold the item.
        if (results.Count < limit)
        {
            results.AddRange(connection.Query<DestinationRow>($"""
                SELECT * FROM (
                    SELECT c.id AS Id, 'collection' AS Kind, COALESCE(c.media_type, '') AS MediaType,
                           COALESCE(NULLIF(TRIM(c.display_name), ''), 'Untitled collection') AS Label,
                           NULL AS ParentLabel, NULL AS SeasonOrdinal
                    FROM collections c
                    WHERE {MovableCollectionSql("c")}
                      AND NOT EXISTS (SELECT 1 FROM collection_items ci WHERE ci.collection_id = c.id AND ci.work_id = @workId)
                )
                WHERE @like IS NULL OR Label LIKE @like ESCAPE '\'
                ORDER BY Label COLLATE NOCASE
                LIMIT @limit;
                """, new { workId = item.WorkId, like, limit = limit - results.Count }).Select(row => row.ToDestination()));
        }

        return Task.FromResult<IReadOnlyList<LibraryMoveDestinationRow>>(results);
    }

    public Task<LibraryMoveDestinationInfo?> GetDestinationAsync(Guid destinationId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var connection = db.CreateConnection();
        return Task.FromResult(LoadDestination(connection, null, destinationId));
    }

    public Task<bool> IsSameOrBeneathAsync(Guid destinationId, IReadOnlyList<Guid> workIds, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(workIds);
        ct.ThrowIfCancellationRequested();
        using var connection = db.CreateConnection();
        return Task.FromResult(IsSameOrBeneath(connection, null, destinationId, workIds));
    }

    private static bool IsSameOrBeneath(IDbConnection connection, IDbTransaction? transaction, Guid destinationId, IReadOnlyList<Guid> workIds)
    {
        var set = workIds.ToHashSet();
        var current = (Guid?)destinationId;

        // The walk up is bounded so a loop that already exists in the data cannot hold this up.
        for (var depth = 0; current is { } id && depth < 64; depth++)
        {
            if (set.Contains(id))
            {
                return true;
            }

            current = connection.QueryFirstOrDefault<Guid?>(
                "-- readiness: moves work on titles by id or by the place they sit in, whatever their readiness\nSELECT parent_work_id FROM works WHERE id = @id;",
                new { id },
                transaction);
        }

        return false;
    }

    public Task<IReadOnlyList<LibraryMoveChild>> GetChildrenAsync(Guid parentWorkId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var connection = db.CreateConnection();
        var rows = connection.Query<ChildRow>($"""
            -- readiness: moves work on titles by id or by the place they sit in, whatever their readiness
            SELECT w.id AS WorkId,
                   {WorkTitleSql("w")} AS Title,
                   w.ordinal AS Ordinal,
                   w.ordinal_sort AS OrdinalSort,
                   (SELECT CAST(cv.value AS INTEGER) FROM canonical_values cv WHERE cv.entity_id = w.id AND cv.key = 'disc_number' LIMIT 1) AS DiscNumber,
                   {HasFileSql("w")} AS HasFile,
                   CASE WHEN COALESCE(w.is_catalog_only, 0) = 1 OR w.ownership = 'Unowned' OR w.work_kind = 'catalog' THEN 1 ELSE 0 END AS IsCatalogOnly,
                   w.placement_unmatched AS IsUnmatched,
                   {DurationSql("w")} AS DurationText
            FROM works w
            WHERE w.parent_work_id = @parentWorkId AND w.work_kind <> 'parent'
            ORDER BY COALESCE(w.ordinal_sort, w.ordinal, 1e9), w.id;
            """, new { parentWorkId }).ToList();
        var userManaged = UserManagedWorkIds(connection, null, rows.Select(row => row.WorkId).ToList());

        IReadOnlyList<LibraryMoveChild> children = rows
            .Select(row => new LibraryMoveChild(
                row.WorkId,
                string.IsNullOrWhiteSpace(row.Title) ? string.Empty : row.Title.Trim(),
                row.Ordinal,
                row.OrdinalSort,
                row.DiscNumber,
                row.HasFile != 0,
                row.IsCatalogOnly != 0,
                row.IsUnmatched != 0,
                ParseSeconds(row.DurationText),
                userManaged.Contains(row.WorkId)))
            .ToList();
        return Task.FromResult(children);
    }

    public Task<IReadOnlyList<LibraryMovePart>> GetPartsAsync(Guid audiobookWorkId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var connection = db.CreateConnection();
        var rows = connection.Query<PartRow>("""
            SELECT e.id AS EditionId,
                   e.placement_unmatched AS IsUnmatched,
                   (SELECT ma.file_path_root FROM media_assets ma WHERE ma.edition_id = e.id ORDER BY ma.file_path_root LIMIT 1) AS FilePath,
                   (SELECT cv.value FROM canonical_values cv JOIN media_assets ma ON ma.id = cv.entity_id
                    WHERE ma.edition_id = e.id AND cv.key = 'track_title' AND NULLIF(cv.value, '') IS NOT NULL LIMIT 1) AS TrackTitle,
                   (SELECT cv.value FROM canonical_values cv JOIN media_assets ma ON ma.id = cv.entity_id
                    WHERE ma.edition_id = e.id AND cv.key = @partKey LIMIT 1) AS PartNumberText,
                   (SELECT cv.value FROM canonical_values cv JOIN media_assets ma ON ma.id = cv.entity_id
                    WHERE ma.edition_id = e.id AND cv.key = @countKey LIMIT 1) AS PartCountText,
                   (SELECT cv.value FROM canonical_values cv JOIN media_assets ma ON ma.id = cv.entity_id
                    WHERE ma.edition_id = e.id AND cv.key IN ('duration_seconds', 'duration') AND NULLIF(cv.value, '') IS NOT NULL
                    ORDER BY CASE cv.key WHEN 'duration_seconds' THEN 0 ELSE 1 END LIMIT 1) AS DurationText
            FROM editions e
            WHERE e.work_id = @audiobookWorkId
              AND EXISTS (SELECT 1 FROM media_assets ma WHERE ma.edition_id = e.id)
            ORDER BY e.id;
            """, new { audiobookWorkId, partKey = PartNumberKey, countKey = PartCountKey }).ToList();

        IReadOnlyList<LibraryMovePart> parts = rows
            .Select(row => new LibraryMovePart(
                row.EditionId,
                !string.IsNullOrWhiteSpace(row.TrackTitle)
                    ? row.TrackTitle.Trim()
                    : Path.GetFileNameWithoutExtension(row.FilePath ?? string.Empty),
                ParseWholeNumber(row.PartNumberText),
                ParseWholeNumber(row.PartCountText),
                row.IsUnmatched != 0,
                ParseSeconds(row.DurationText)))
            .OrderBy(part => part.PartNumber ?? int.MaxValue)
            .ThenBy(part => part.Title, StringComparer.OrdinalIgnoreCase)
            .ToList();
        return Task.FromResult(parts);
    }

    public Task<IReadOnlyList<LibraryMoveSourceFacts>> GetSourcePlacesAsync(
        IReadOnlyList<LibraryMoveItem> items,
        Guid destinationId,
        Guid? sourceCollectionId,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(items);
        ct.ThrowIfCancellationRequested();
        using var connection = db.CreateConnection();
        return Task.FromResult<IReadOnlyList<LibraryMoveSourceFacts>>(FindSourcePlaces(connection, null, items, destinationId));
    }

    public Task<LibraryMoveSourceFacts?> GetSourceCollectionAsync(
        Guid collectionId,
        IReadOnlyList<LibraryMoveItem> items,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(items);
        ct.ThrowIfCancellationRequested();
        var workIds = items.Where(item => !item.IsEdition).Select(item => item.WorkId).Distinct().ToList();
        if (workIds.Count == 0)
        {
            return Task.FromResult<LibraryMoveSourceFacts?>(null);
        }

        using var connection = db.CreateConnection();
        var row = connection.QueryFirstOrDefault<SourceCollectionRow>($"""
            SELECT c.id AS CollectionId,
                   COALESCE(NULLIF(TRIM(c.display_name), ''), 'Untitled collection') AS Name,
                   (SELECT COUNT(DISTINCT ci.work_id) FROM collection_items ci WHERE ci.collection_id = c.id AND ci.work_id IN @ids) AS ItemsInRequest,
                   (SELECT COUNT(DISTINCT ci.work_id) FROM collection_items ci WHERE ci.collection_id = c.id) AS TotalItems,
                   1 AS IsRemovable
            FROM collections c
            WHERE c.id = @collectionId AND {MovableCollectionSql("c")};
            """, new { collectionId, ids = ToBlobs(workIds) });
        if (row is null || row.ItemsInRequest == 0)
        {
            return Task.FromResult<LibraryMoveSourceFacts?>(null);
        }

        return Task.FromResult<LibraryMoveSourceFacts?>(new LibraryMoveSourceFacts(
            row.CollectionId,
            row.Name,
            row.IsRemovable != 0 && row.ItemsInRequest >= row.TotalItems));
    }

    // ----- the move ---------------------------------------------------------------------------------------------------------

    public Task<LibraryMoveCommitOutcome> CommitAsync(
        LibraryMovePlan plan,
        Guid? sourceCollectionId,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ct.ThrowIfCancellationRequested();
        return CommitCoreAsync(plan, sourceCollectionId, ct);
    }

    private async Task<LibraryMoveCommitOutcome> CommitCoreAsync(LibraryMovePlan plan, Guid? sourceCollectionId, CancellationToken ct)
    {
        try
        {
            return await db.ExecuteWriteAsync(
                (connection, transaction, innerCt) => Commit(connection, transaction, plan, sourceCollectionId, innerCt),
                ct).ConfigureAwait(false);
        }
        catch (MoveRefusedException ex)
        {
            // A check failed after the move had started: the transaction rolled back, so nothing changed. The reason goes to the person.
            return ex.IsRefusal ? LibraryMoveCommitOutcome.Refusal(ex.Message) : LibraryMoveCommitOutcome.Conflict(ex.Message);
        }
    }

    private static LibraryMoveCommitOutcome Commit(
        SqliteConnection connection,
        SqliteTransaction transaction,
        LibraryMovePlan plan,
        Guid? sourceCollectionId,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (plan.Rows.Count == 0)
        {
            return LibraryMoveCommitOutcome.Conflict("There is nothing to move.");
        }

        var destination = LoadDestination(connection, transaction, plan.DestinationId);
        if (destination is null || !string.Equals(destination.Kind, plan.DestinationKind, StringComparison.Ordinal))
        {
            return LibraryMoveCommitOutcome.Conflict("The place you are moving to has changed.");
        }

        if (sourceCollectionId is { } checkedSource
            && connection.ExecuteScalar<long>(
                $"SELECT COUNT(*) FROM collections c WHERE c.id = @id AND {MovableCollectionSql("c")};",
                new { id = checkedSource },
                transaction) == 0)
        {
            return LibraryMoveCommitOutcome.Conflict("Items can't be moved out of that collection.");
        }

        var items = LoadItems(connection, transaction, [.. plan.Rows.Select(row => row.ItemId)])
            .ToDictionary(item => item.ItemId);
        foreach (var row in plan.Rows)
        {
            if (!items.TryGetValue(row.ItemId, out var item))
            {
                return LibraryMoveCommitOutcome.Conflict("An item was removed from your library since you reviewed this.");
            }

            if (item.IsContainer)
            {
                return LibraryMoveCommitOutcome.Conflict("A whole show, album, series or audiobook can't be moved, only what is in it.");
            }

            // Never into itself or anything beneath it: that would make a loop. (An audiobook part moves a file, not its audiobook,
            // so only whole titles are looked at. Matching within the place an item already sits in is allowed.)
            if (!item.IsEdition && IsSameOrBeneath(connection, transaction, plan.DestinationId, [item.WorkId]))
            {
                return LibraryMoveCommitOutcome.Conflict("An item can't be moved into itself or into something inside it.");
            }

            if (item.ParentWorkId != row.ExpectedParentWorkId
                || !string.Equals(item.IdentityRevision, row.ExpectedIdentityRevision, StringComparison.Ordinal))
            {
                return LibraryMoveCommitOutcome.Conflict("An item changed since you reviewed this.");
            }

            if (item.ProcessingRunning)
            {
                return LibraryMoveCommitOutcome.Conflict("An item is being processed. Wait for that to finish, then review again.");
            }
        }

        var ordered = plan.Rows.Select(row => (Row: row, Item: items[row.ItemId])).ToList();
        var now = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);
        var token = "move:" + Guid.NewGuid().ToString("N");

        // What the move may empty is looked at before anything changes, so the names can be reported afterwards.
        var emptyCandidates = FindSourcePlaces(connection, transaction, [.. ordered.Select(pair => pair.Item)], plan.DestinationId)
            .Where(place => place.BecomesEmpty)
            .ToList();

        var removedContainers = new List<string>();
        var ownedFiles = new List<string>();
        var outcomes = new List<LibraryMoveRowOutcome>(ordered.Count);

        if (sourceCollectionId is { } sourceCollection)
        {
            connection.Execute(
                "DELETE FROM collection_items WHERE collection_id = @sourceCollection AND work_id IN @ids;",
                new { sourceCollection, ids = ToBlobs([.. ordered.Where(pair => !pair.Item.IsEdition).Select(pair => pair.Item.WorkId)]) },
                transaction);
        }

        LibraryMoveCommitOutcome? refusal = plan.DestinationKind switch
        {
            LibraryMoveKinds.Collection => CommitIntoCollection(connection, transaction, destination, ordered, outcomes),
            LibraryMoveKinds.Audiobook => CommitParts(connection, transaction, destination, ordered, outcomes, ownedFiles, token, now),
            _ => CommitIntoParent(connection, transaction, destination, ordered, outcomes, removedContainers, ownedFiles, token, now),
        };
        if (refusal is not null)
        {
            // The caller rolls the transaction back when the body throws, so nothing above is kept.
            throw new MoveRefusedException(refusal.ConflictReason ?? "The move could not be completed.", refusal.IsRefusal);
        }

        // Emptied places go in the same transaction. A place the person's own settings keep is left alone, so only the ones
        // that really went are reported.
        WorkHierarchyMaintenanceService.CleanupEmptyParentsCore(connection, transaction);
        foreach (var place in emptyCandidates)
        {
            var stillThere = connection.ExecuteScalar<long>("-- readiness: moves work on titles by id or by the place they sit in, whatever their readiness\nSELECT COUNT(*) FROM works WHERE id = @id;", new { id = place.ContainerId }, transaction) > 0;
            if (!stillThere && !removedContainers.Contains(place.Label, StringComparer.OrdinalIgnoreCase))
            {
                removedContainers.Add(place.Label);
            }
        }

        foreach (var removed in CuratedCollectionCleanup.DeleteEmpty(connection, transaction, "its last item was moved out"))
        {
            removedContainers.Add(removed.Name);
        }

        LogActivity(connection, transaction, plan, destination, ordered.Count, removedContainers);
        return LibraryMoveCommitOutcome.Done(outcomes, removedContainers, [.. ownedFiles.Distinct(StringComparer.OrdinalIgnoreCase)]);
    }

    private static LibraryMoveCommitOutcome? CommitIntoCollection(
        SqliteConnection connection,
        SqliteTransaction transaction,
        LibraryMoveDestinationInfo destination,
        List<(LibraryMovePlanRow Row, LibraryMoveItem Item)> ordered,
        List<LibraryMoveRowOutcome> outcomes)
    {
        if (ordered.Any(pair => pair.Item.IsEdition))
        {
            return LibraryMoveCommitOutcome.Conflict("Only whole titles can be added to a collection.");
        }

        if (connection.ExecuteScalar<long>(
                $"SELECT COUNT(*) FROM collections c WHERE c.id = @id AND {MovableCollectionSql("c")};",
                new { id = destination.Id },
                transaction) == 0)
        {
            return LibraryMoveCommitOutcome.Conflict("That collection can no longer be added to.");
        }

        foreach (var (row, item) in ordered)
        {
            if (row.Mode != LibraryMoveMode.Match)
            {
                return LibraryMoveCommitOutcome.Conflict("A collection has no list to match against, so items simply join it.");
            }

            // Already in it: nothing to add, and it still counts as moved.
            connection.Execute(
                """
                INSERT INTO collection_items (id, collection_id, work_id, sort_order)
                SELECT @id, @collectionId, @workId,
                       COALESCE((SELECT MAX(sort_order) FROM collection_items WHERE collection_id = @collectionId), -1) + 1
                WHERE NOT EXISTS (SELECT 1 FROM collection_items WHERE collection_id = @collectionId AND work_id = @workId);
                """,
                new { id = Guid.NewGuid(), collectionId = destination.Id, workId = item.WorkId },
                transaction);
            outcomes.Add(new LibraryMoveRowOutcome(item.ItemId, "moved", item.WorkId));
        }

        return null;
    }

    private static LibraryMoveCommitOutcome? CommitParts(
        SqliteConnection connection,
        SqliteTransaction transaction,
        LibraryMoveDestinationInfo destination,
        List<(LibraryMovePlanRow Row, LibraryMoveItem Item)> ordered,
        List<LibraryMoveRowOutcome> outcomes,
        List<string> ownedFiles,
        string token,
        string now)
    {
        if (ordered.Any(pair => !pair.Item.IsEdition))
        {
            return LibraryMoveCommitOutcome.Conflict("Only a part of an audiobook can be moved into an audiobook.");
        }

        // An audiobook is emptied when the whole batch takes its last files, so the check counts what is left after the batch, not
        // before it. Details the person set on an audiobook that would be emptied would be lost with it.
        var movingEditions = ordered.Select(pair => pair.Item.EditionId!.Value).ToHashSet();
        foreach (var source in ordered
                     .Where(pair => pair.Item.HasUserManagedDetails && pair.Item.WorkId != destination.Id)
                     .Select(pair => pair.Item)
                     .DistinctBy(item => item.WorkId))
        {
            var left = connection.ExecuteScalar<long>(
                "SELECT COUNT(*) FROM editions WHERE work_id = @id AND id NOT IN @moving;",
                new { id = source.WorkId, moving = ToBlobs([.. movingEditions]) },
                transaction);
            if (left == 0)
            {
                return LibraryMoveCommitOutcome.Refusal("You changed details on this audiobook yourself, so its last file can't be moved out of it.");
            }
        }

        var emptiedSources = new List<LibraryMoveItem>();
        foreach (var (row, item) in ordered)
        {
            var assets = item.AssetIds;
            var staysInPlace = item.WorkId == destination.Id;
            switch (row.Mode)
            {
                case LibraryMoveMode.AddAnyway:
                    if (staysInPlace)
                    {
                        return LibraryMoveCommitOutcome.Conflict("That part is already in this audiobook, so adding it anyway would change nothing.");
                    }

                    break;

                case LibraryMoveMode.Match:
                    if (row.Target.PartNumber is not { } freeNumber
                        || PartNumberIsTaken(connection, transaction, destination.Id, freeNumber))
                    {
                        return LibraryMoveCommitOutcome.Conflict("That part is no longer free.");
                    }

                    break;

                default:
                    if (row.Target.PartNumber is not { } sharedNumber
                        || !PartNumberIsTaken(connection, transaction, destination.Id, sharedNumber)
                        || row.Target.TakenPartEditionId == item.EditionId)
                    {
                        return LibraryMoveCommitOutcome.Conflict("That part no longer has another file.");
                    }

                    break;
            }

            connection.Execute(
                "UPDATE editions SET work_id = @destinationId, placement_unmatched = @unmatched WHERE id = @editionId;",
                new
                {
                    destinationId = destination.Id,
                    unmatched = row.Mode == LibraryMoveMode.AddAnyway ? 1 : 0,
                    editionId = item.EditionId,
                },
                transaction);

            if (row.Mode == LibraryMoveMode.AddAnyway)
            {
                ClearNumbers(connection, transaction, assets, PlacementNumbers.PartKeys, now);
            }
            else
            {
                SetNumbers(connection, transaction, assets, [(PartNumberKey, row.Target.PartNumber!.Value.ToString(CultureInfo.InvariantCulture))], now);
            }

            connection.Execute(
                "UPDATE works SET ownership = 'Owned', is_catalog_only = 0 WHERE id = @id;",
                new { id = destination.Id },
                transaction);
            BumpRevision(connection, transaction, destination.Id, now);
            BumpRevision(connection, transaction, item.WorkId, now);
            MarkWritebackPending(connection, transaction, assets, token, now);
            emptiedSources.Add(item);
            outcomes.Add(new LibraryMoveRowOutcome(
                item.ItemId,
                row.Mode switch
                {
                    LibraryMoveMode.AddAnyway => "added_unmatched",
                    LibraryMoveMode.AddAsVersion => "added_as_version",
                    _ => "matched",
                },
                destination.Id));
        }

        // An audiobook left with no file is removed, as an emptied album or season is.
        foreach (var sourceWork in emptiedSources.Select(item => item.WorkId).Distinct())
        {
            if (connection.ExecuteScalar<long>("SELECT COUNT(*) FROM editions WHERE work_id = @id;", new { id = sourceWork }, transaction) == 0)
            {
                // What people did with the emptied audiobook goes to the one that took its files.
                WorkPersonalStateMerge.Carry(connection, transaction, sourceWork, destination.Id);
                ownedFiles.AddRange(LibraryItemDeletionRepository.DeleteWorksCore(connection, transaction, [sourceWork]).TuvimaOwnedFiles);
            }
        }

        return null;
    }

    private static LibraryMoveCommitOutcome? CommitIntoParent(
        SqliteConnection connection,
        SqliteTransaction transaction,
        LibraryMoveDestinationInfo destination,
        List<(LibraryMovePlanRow Row, LibraryMoveItem Item)> ordered,
        List<LibraryMoveRowOutcome> outcomes,
        List<string> removedContainers,
        List<string> ownedFiles,
        string token,
        string now)
    {
        if (ordered.Any(pair => pair.Item.IsEdition))
        {
            return LibraryMoveCommitOutcome.Conflict("Only whole titles can be moved into this place.");
        }

        foreach (var (row, item) in ordered)
        {
            if (destination.Kind == LibraryMoveKinds.Series && row.Mode != LibraryMoveMode.AddAnyway)
            {
                // D20: a Series is built from each title's match, never by hand. Only forcing a title in (Add anyway) is allowed.
                return LibraryMoveCommitOutcome.Refusal("Series are built from each title's match. Use Match and Identity to re-match it, or add it here without a number.");
            }

            if (row.Mode == LibraryMoveMode.AddAnyway)
            {
                if (item.ParentWorkId == destination.Id)
                {
                    return LibraryMoveCommitOutcome.Conflict("That item is already in this place, so adding it anyway would change nothing.");
                }

                Relocate(connection, transaction, destination, item, ordinal: null, ordinalSort: null, unmatched: true);
                ClearNumbers(connection, transaction, [item.WorkId, .. item.AssetIds], PlacementNumbers.WorkKeys, now);
                UnlinkManifest(connection, transaction, item.WorkId);
                outcomes.Add(new LibraryMoveRowOutcome(item.ItemId, "added_unmatched", item.WorkId));
            }
            else if (row.Target.ExistingWorkId is { } entryWork)
            {
                if (entryWork == item.WorkId)
                {
                    return LibraryMoveCommitOutcome.Conflict("That item already is this entry.");
                }

                var refusal = Attach(connection, transaction, destination, row, item, entryWork, removedContainers, ownedFiles, now);
                if (refusal is not null)
                {
                    return refusal;
                }

                outcomes.Add(new LibraryMoveRowOutcome(
                    item.ItemId,
                    row.Mode == LibraryMoveMode.AddAsVersion ? "added_as_version" : "matched",
                    entryWork));
            }
            else
            {
                if (row.Mode != LibraryMoveMode.Match || row.Target.OrdinalSort is not { } sort)
                {
                    return LibraryMoveCommitOutcome.Conflict("Choose an entry in the list for this item.");
                }

                // The slot must still be free of any Work, owned or not: a Work appearing there means the list changed.
                if (connection.ExecuteScalar<long>(
                        "-- readiness: moves work on titles by id or by the place they sit in, whatever their readiness\nSELECT COUNT(*) FROM works WHERE parent_work_id = @parent AND ordinal_sort = @sort;",
                        new { parent = destination.Id, sort },
                        transaction) > 0)
                {
                    return LibraryMoveCommitOutcome.Conflict("That place in the list is no longer free.");
                }

                if (row.Target.ManifestItemId is { } manifestItem
                    && connection.ExecuteScalar<long>(
                        "SELECT COUNT(*) FROM series_manifest_items WHERE id = @id AND (linked_work_id IS NULL OR ownership_state <> 'Owned');",
                        new { id = manifestItem },
                        transaction) == 0)
                {
                    return LibraryMoveCommitOutcome.Conflict("That entry in the series list is no longer free.");
                }

                Relocate(connection, transaction, destination, item, row.Target.Ordinal, sort, unmatched: false);
                SetNumbers(connection, transaction, [item.WorkId, .. item.AssetIds], NumbersFor(destination, row.Target.Ordinal, row.Target.DiscNumber), now);
                UnlinkManifest(connection, transaction, item.WorkId);
                if (row.Target.ManifestItemId is { } linked)
                {
                    connection.Execute(
                        "UPDATE series_manifest_items SET linked_work_id = @workId, ownership_state = 'Owned' WHERE id = @id;",
                        new { id = linked, workId = item.WorkId },
                        transaction);
                }

                outcomes.Add(new LibraryMoveRowOutcome(item.ItemId, "matched", item.WorkId));
            }

            BumpRevision(connection, transaction, row.Target.ExistingWorkId ?? item.WorkId, now);
            MarkWritebackPending(connection, transaction, item.AssetIds, token, now);
        }

        return null;
    }

    private static void Relocate(
        SqliteConnection connection,
        SqliteTransaction transaction,
        LibraryMoveDestinationInfo destination,
        LibraryMoveItem item,
        int? ordinal,
        double? ordinalSort,
        bool unmatched) =>
        connection.Execute(
            """
            UPDATE works
            SET parent_work_id = @parent,
                work_kind = CASE WHEN work_kind IN ('standalone', 'catalog') THEN 'child' ELSE work_kind END,
                ordinal = @ordinal,
                ordinal_sort = @ordinalSort,
                placement_unmatched = @unmatched,
                collection_id = CASE WHEN @samePlace = 1 THEN collection_id ELSE @collection END
            WHERE id = @workId;
            """,
            new
            {
                parent = destination.Id,
                ordinal,
                ordinalSort,
                unmatched = unmatched ? 1 : 0,
                collection = destination.CollectionId,
                samePlace = item.ParentWorkId == destination.Id ? 1 : 0,
                workId = item.WorkId,
            },
            transaction);

    /// <summary>
    /// The item's files become Editions of the entry's own Work (so the entry keeps its identity and the files keep everything they
    /// carry), and the emptied title is removed. Refuses when the entry's state is not what the preview showed, or when removing the
    /// emptied title would lose details the person set on it.
    /// </summary>
    private static LibraryMoveCommitOutcome? Attach(
        SqliteConnection connection,
        SqliteTransaction transaction,
        LibraryMoveDestinationInfo destination,
        LibraryMovePlanRow row,
        LibraryMoveItem item,
        Guid entryWork,
        List<string> removedContainers,
        List<string> ownedFiles,
        string now)
    {
        var entry = connection.QueryFirstOrDefault<EntryRow>(
            """
            -- readiness: moves work on titles by id or by the place they sit in, whatever their readiness
            SELECT w.id AS WorkId, w.ordinal AS Ordinal, w.ordinal_sort AS OrdinalSort,
                   (SELECT CAST(cv.value AS INTEGER) FROM canonical_values cv WHERE cv.entity_id = w.id AND cv.key = 'disc_number' LIMIT 1) AS DiscNumber
            FROM works w
            WHERE w.id = @id AND w.parent_work_id = @parent;
            """,
            new { id = entryWork, parent = destination.Id },
            transaction);
        if (entry is null)
        {
            return LibraryMoveCommitOutcome.Conflict("That entry is no longer in the list.");
        }

        var taken = connection.ExecuteScalar<long>(
            $"""
            -- readiness: moves work on titles by id or by the place they sit in, whatever their readiness
            SELECT {HasFileSql("w")} FROM works w WHERE w.id = @id;
            """,
            new { id = entryWork },
            transaction) > 0;
        if (row.Mode == LibraryMoveMode.AddAsVersion ? !taken : taken)
        {
            return LibraryMoveCommitOutcome.Conflict(taken
                ? "That entry already has a file."
                : "That entry has no file any more, so match this item to it instead.");
        }

        if (item.HasUserManagedDetails)
        {
            return LibraryMoveCommitOutcome.Refusal(LibraryMoveReasons.UserManagedMerge);
        }

        connection.Execute("UPDATE editions SET work_id = @entry WHERE work_id = @source;", new { entry = entryWork, source = item.WorkId }, transaction);
        connection.Execute(
            """
            UPDATE works
            SET ownership = 'Owned',
                is_catalog_only = 0,
                work_kind = CASE WHEN work_kind = 'catalog' THEN 'child' ELSE work_kind END,
                placement_unmatched = CASE WHEN @taken = 1 THEN placement_unmatched ELSE 0 END
            WHERE id = @entry;
            """,
            new { entry = entryWork, taken = taken ? 1 : 0 },
            transaction);
        SetNumbers(connection, transaction, item.AssetIds, NumbersFor(destination, entry.Ordinal, entry.DiscNumber), now);

        // What people did with the title (hearts, plays, listening, bookmarks, queue places, hand-picked collections, tags) goes to the
        // entry; a move never throws it away.
        WorkPersonalStateMerge.Carry(connection, transaction, item.WorkId, entryWork);

        UnlinkManifest(connection, transaction, item.WorkId);
        connection.Execute(
            "UPDATE series_manifest_items SET ownership_state = 'Owned' WHERE linked_work_id = @entry AND ownership_state = 'Missing';",
            new { entry = entryWork },
            transaction);

        var removed = LibraryItemDeletionRepository.DeleteWorksCore(connection, transaction, [item.WorkId]);
        ownedFiles.AddRange(removed.TuvimaOwnedFiles);
        foreach (var collection in removed.CollectionsDeleted)
        {
            removedContainers.Add(collection.Name);
        }

        return null;
    }

    // One definition, shared with the preview that works out the file name these numbers give.
    private static List<(string Key, string Value)> NumbersFor(LibraryMoveDestinationInfo destination, int? ordinal, int? disc) =>
        MovePlacementNumbers.For(destination.MediaType, destination.SeasonNumber, ordinal, disc);

    /// <summary>
    /// Writes where the item sits in its list, on the title and on each of its files (readers look at the file first). Each value is
    /// the person's choice, so it is a current claim from the manual provider and the chosen value.
    /// </summary>
    private static void SetNumbers(
        SqliteConnection connection,
        SqliteTransaction transaction,
        IReadOnlyList<Guid> entityIds,
        IReadOnlyList<(string Key, string Value)> numbers,
        string now)
    {
        foreach (var entity in entityIds)
        {
            foreach (var (key, value) in numbers)
            {
                connection.Execute(
                    """
                    UPDATE metadata_claims SET is_current = 0, superseded_at = @now
                    WHERE entity_id = @entity AND claim_key = @key AND is_current = 1;
                    INSERT INTO metadata_claims
                        (id, entity_id, provider_id, decision_source_provider_id, claim_key, claim_value, confidence, claimed_at, is_user_locked, is_current)
                    VALUES (@claim, @entity, @manual, @manual, @key, @value, 1.0, @now, 0, 1);
                    INSERT INTO canonical_values (entity_id, key, value, last_scored_at, winning_provider_id)
                    VALUES (@entity, @key, @value, @now, @manual)
                    ON CONFLICT(entity_id, key) DO UPDATE SET
                        value = excluded.value,
                        last_scored_at = excluded.last_scored_at,
                        winning_provider_id = excluded.winning_provider_id,
                        is_conflicted = 0;
                    """,
                    new { claim = Guid.NewGuid(), entity, key, value, now, manual = WellKnownProviders.UserManual },
                    transaction);
            }
        }
    }

    /// <summary>
    /// An item added anyway has no number in the list it joins: those numbers and the claims that would bring them back are retired.
    /// Only the keys of that list are cleared (a title's own numbers, or an audiobook part's number), never the other list's.
    /// </summary>
    private static void ClearNumbers(
        SqliteConnection connection,
        SqliteTransaction transaction,
        IReadOnlyList<Guid> entityIds,
        string[] keys,
        string now) =>
        connection.Execute(
            """
            UPDATE metadata_claims SET is_current = 0, superseded_at = @now
            WHERE entity_id IN @entities AND claim_key IN @keys AND is_current = 1;
            DELETE FROM canonical_values WHERE entity_id IN @entities AND key IN @keys;
            """,
            new { entities = ToBlobs(entityIds), keys, now },
            transaction);

    private static void UnlinkManifest(SqliteConnection connection, SqliteTransaction transaction, Guid workId) =>
        connection.Execute(
            """
            UPDATE series_manifest_items
            SET linked_work_id = NULL,
                ownership_state = CASE WHEN ownership_state = 'Owned' THEN 'Missing' ELSE ownership_state END
            WHERE linked_work_id = @workId;
            """,
            new { workId },
            transaction);

    private static void BumpRevision(SqliteConnection connection, SqliteTransaction transaction, Guid entityId, string now) =>
        connection.Execute(
            """
            INSERT INTO canonical_values (entity_id, key, value, last_scored_at)
            VALUES (@entityId, @key, @value, @now)
            ON CONFLICT(entity_id, key) DO UPDATE SET value = excluded.value, last_scored_at = excluded.last_scored_at;
            """,
            new { entityId, key = IdentityRevisionKey, value = Guid.NewGuid().ToString("N"), now },
            transaction);

    // The durable file request a move makes, in the same transaction as the library change: the queue renames and moves each file
    // that may be touched to fit where the item now sits, then brings its tags up to date. The trigger is kept by later editor saves
    // until the queue has done its work (see MediaEditorCommitRepository), so a save made in between never drops the file move.
    private static void MarkWritebackPending(
        SqliteConnection connection,
        SqliteTransaction transaction,
        IReadOnlyList<Guid> workAndAssetIds,
        string token,
        string now)
    {
        var assets = connection.Query<Guid>(
            "SELECT id FROM media_assets WHERE id IN @ids;",
            new { ids = ToBlobs(workAndAssetIds) },
            transaction).ToList();
        foreach (var assetId in assets)
        {
            connection.Execute(
                """
                UPDATE media_assets
                SET writeback_status = 'pending', writeback_fields_hash = @token, writeback_attempts = 0,
                    writeback_last_error = NULL, writeback_next_retry_at = NULL
                WHERE id = @assetId;
                INSERT INTO media_file_write_intents (asset_id, generation, operation_token, trigger, status, attempts, created_at, updated_at)
                VALUES (@assetId, 1, @token, @trigger, 'pending', 0, @now, @now)
                ON CONFLICT(asset_id) DO UPDATE SET generation = generation + 1, operation_token = excluded.operation_token,
                    trigger = excluded.trigger, status = 'pending', attempts = 0, lease_expires_at = NULL, last_error = NULL,
                    updated_at = excluded.updated_at;
                """,
                new { assetId, token, now, trigger = MediaFileWriteTriggers.LibraryMove },
                transaction);
        }
    }

    private static void LogActivity(
        SqliteConnection connection,
        SqliteTransaction transaction,
        LibraryMovePlan plan,
        LibraryMoveDestinationInfo destination,
        int itemCount,
        IReadOnlyList<string> removedContainers)
    {
        var what = itemCount == 1 ? "1 item" : $"{itemCount.ToString(CultureInfo.InvariantCulture)} items";
        var who = string.IsNullOrWhiteSpace(plan.ActorLabel) ? "An administrator" : plan.ActorLabel.Trim();
        SystemActivityRepository.Write(connection, transaction, new SystemActivityEntry
        {
            ActionType = SystemActionType.MediaUpdated,
            EntityId = destination.Id,
            EntityType = destination.Kind == LibraryMoveKinds.Collection ? "Collection" : "Work",
            CollectionName = destination.Label,
            Detail = $"{who} moved {what} into '{destination.Label}'.",
            ChangesJson = JsonSerializer.Serialize(new
            {
                destination_id = destination.Id,
                destination_kind = destination.Kind,
                items = plan.Rows.Select(row => new { item_id = row.ItemId, mode = row.Mode.ToString() }),
                removed_containers = removedContainers,
            }),
        });
    }

    // ----- shared loading ----------------------------------------------------------------------------------------------------

    private static List<LibraryMoveItem> LoadItems(IDbConnection connection, IDbTransaction? transaction, IReadOnlyList<Guid> itemIds)
    {
        var blobs = ToBlobs(itemIds);
        var workRows = connection.Query<ItemRow>($"""
            -- readiness: moves work on titles by id or by the place they sit in, whatever their readiness
            SELECT w.id AS ItemId, 0 AS IsEdition, NULL AS EditionId, {ItemColumns}
            FROM works w
            LEFT JOIN works p ON p.id = w.parent_work_id
            LEFT JOIN works gp ON gp.id = p.parent_work_id
            WHERE w.id IN @blobs;
            """, new { blobs }, transaction).ToList();
        var editionRows = connection.Query<ItemRow>($"""
            -- readiness: moves work on titles by id or by the place they sit in, whatever their readiness
            SELECT e.id AS ItemId, 1 AS IsEdition, e.id AS EditionId, {ItemColumns}
            FROM editions e
            JOIN works w ON w.id = e.work_id
            LEFT JOIN works p ON p.id = w.parent_work_id
            LEFT JOIN works gp ON gp.id = p.parent_work_id
            WHERE e.id IN @blobs;
            """, new { blobs }, transaction).ToList();

        var rows = workRows.Concat(editionRows).ToList();
        if (rows.Count == 0)
        {
            return [];
        }

        var workBlobs = ToBlobs(rows.Select(row => row.WorkId).ToList());
        var assets = connection.Query<(Guid EditionId, Guid WorkId, Guid AssetId)>(
            """
            SELECT e.id AS EditionId, e.work_id AS WorkId, ma.id AS AssetId
            FROM editions e
            JOIN media_assets ma ON ma.edition_id = e.id
            WHERE e.work_id IN @workBlobs
            ORDER BY ma.file_path_root;
            """, new { workBlobs }, transaction).ToList();
        var userManaged = UserManagedWorkIds(connection, transaction, rows.Select(row => row.WorkId).Distinct().ToList());
        var processing = connection.Query<Guid>(
            """
            -- readiness: moves work on titles by id or by the place they sit in, whatever their readiness
            SELECT DISTINCT w.id
            FROM works w
            WHERE w.id IN @workBlobs
              AND EXISTS (
                  SELECT 1 FROM identity_jobs j
                  WHERE j.state NOT IN @resting
                    AND (j.entity_id = w.id
                         OR j.entity_id IN (SELECT e.id FROM editions e WHERE e.work_id = w.id)
                         OR j.entity_id IN (SELECT ma.id FROM editions e JOIN media_assets ma ON ma.edition_id = e.id WHERE e.work_id = w.id)));
            """, new { workBlobs, resting = RestingJobStates }, transaction).ToHashSet();

        return rows.Select(row =>
        {
            var ownAssets = row.EditionId is { } editionId
                ? assets.Where(asset => asset.EditionId == editionId).Select(asset => asset.AssetId).ToList()
                : assets.Where(asset => asset.WorkId == row.WorkId).Select(asset => asset.AssetId).ToList();
            var title = row.EditionId is not null
                ? PartTitle(connection, transaction, row.EditionId.Value)
                : row.WorkTitle;
            return new LibraryMoveItem(
                row.ItemId,
                row.IsEdition != 0,
                row.WorkId,
                row.EditionId,
                row.MediaType ?? string.Empty,
                row.WorkKind ?? string.Empty,
                row.ParentWorkId,
                string.IsNullOrWhiteSpace(title) ? string.Empty : title.Trim(),
                string.IsNullOrWhiteSpace(row.WorkTitle) ? string.Empty : row.WorkTitle.Trim(),
                row.Ordinal,
                row.OrdinalSort,
                row.DiscNumber,
                row.EditionId is { } part ? PartNumberOf(connection, transaction, part) : null,
                row.EditionId is { } unmatchedEdition
                    ? connection.ExecuteScalar<long>("SELECT placement_unmatched FROM editions WHERE id = @id;", new { id = unmatchedEdition }, transaction) != 0
                    : row.WorkUnmatched != 0,
                row.ParentTitle,
                row.ParentOrdinal,
                row.GrandparentTitle,
                row.IdentityRevision ?? string.Empty,
                ParseSeconds(row.DurationText),
                processing.Contains(row.WorkId),
                userManaged.Contains(row.WorkId),
                row.EditionCount,
                row.IsContainer != 0,
                ownAssets);
        }).ToList();
    }

    private static string? PartTitle(IDbConnection connection, IDbTransaction? transaction, Guid editionId)
    {
        var row = connection.QueryFirstOrDefault<(string? TrackTitle, string? FilePath)>(
            """
            SELECT (SELECT cv.value FROM canonical_values cv JOIN media_assets ma ON ma.id = cv.entity_id
                    WHERE ma.edition_id = @editionId AND cv.key = 'track_title' AND NULLIF(cv.value, '') IS NOT NULL LIMIT 1) AS TrackTitle,
                   (SELECT ma.file_path_root FROM media_assets ma WHERE ma.edition_id = @editionId ORDER BY ma.file_path_root LIMIT 1) AS FilePath;
            """, new { editionId }, transaction);
        return !string.IsNullOrWhiteSpace(row.TrackTitle)
            ? row.TrackTitle
            : Path.GetFileNameWithoutExtension(row.FilePath ?? string.Empty);
    }

    private static int? PartNumberOf(IDbConnection connection, IDbTransaction? transaction, Guid editionId) =>
        ParseWholeNumber(connection.QueryFirstOrDefault<string>(
            """
            SELECT cv.value FROM canonical_values cv JOIN media_assets ma ON ma.id = cv.entity_id
            WHERE ma.edition_id = @editionId AND cv.key = @key LIMIT 1;
            """, new { editionId, key = PartNumberKey }, transaction));

    private static bool PartNumberIsTaken(IDbConnection connection, IDbTransaction transaction, Guid audiobookWorkId, int partNumber) =>
        connection.ExecuteScalar<long>(
            """
            SELECT COUNT(*)
            FROM editions e
            JOIN media_assets ma ON ma.edition_id = e.id
            JOIN canonical_values cv ON cv.entity_id = ma.id AND cv.key = @key
            WHERE e.work_id = @audiobookWorkId AND CAST(cv.value AS INTEGER) = @partNumber;
            """,
            new { audiobookWorkId, key = PartNumberKey, partNumber },
            transaction) > 0;

    /// <summary>
    /// Works with details the person set themselves: a locked field, an own name or sort (display overrides), or a chosen value
    /// from the manual provider. Where an item sits in its list is excluded: that comes and goes with a move.
    /// </summary>
    private static HashSet<Guid> UserManagedWorkIds(IDbConnection connection, IDbTransaction? transaction, IReadOnlyList<Guid> workIds)
    {
        if (workIds.Count == 0)
        {
            return [];
        }

        return connection.Query<Guid>(
            """
            -- readiness: moves work on titles by id or by the place they sit in, whatever their readiness
            SELECT w.id
            FROM works w
            WHERE w.id IN @ids
              AND (
                  COALESCE(NULLIF(w.display_overrides_json, ''), '{}') NOT IN ('{}', 'null')
                  OR EXISTS (SELECT 1 FROM metadata_claims c
                             WHERE c.entity_id = w.id AND c.is_current = 1 AND c.is_user_locked = 1 AND c.claim_key NOT IN @numberKeys)
                  OR EXISTS (SELECT 1 FROM canonical_values cv
                             WHERE cv.entity_id = w.id AND cv.winning_provider_id = @manual AND cv.key NOT IN @numberKeys));
            """,
            new { ids = ToBlobs(workIds), numberKeys = NumberKeys, manual = WellKnownProviders.UserManual },
            transaction).ToHashSet();
    }

    private static LibraryMoveDestinationInfo? LoadDestination(IDbConnection connection, IDbTransaction? transaction, Guid id)
    {
        var work = connection.QueryFirstOrDefault<WorkDestinationRow>($"""
            -- readiness: moves work on titles by id or by the place they sit in, whatever their readiness
            SELECT w.id AS Id, w.media_type AS MediaType, w.work_kind AS WorkKind, w.parent_work_id AS ParentWorkId, w.ordinal AS Ordinal,
                   {ParentLabelSql("w")} AS Label,
                   {WorkTitleSql("w")} AS RecordingLabel,
                   {ParentLabelSql("p")} AS ParentTitle,
                   {AuthorSql("w")} AS Author,
                   {ArtistSql("w")} AS Artist,
                   (SELECT cv.value FROM canonical_values cv WHERE cv.entity_id = w.id AND cv.key = 'child_entities_json' LIMIT 1) AS ManifestJson
            FROM works w
            LEFT JOIN works p ON p.id = w.parent_work_id
            WHERE w.id = @id;
            """, new { id }, transaction);
        if (work is null)
        {
            var collection = connection.QueryFirstOrDefault<(Guid Id, string Name)>(
                $"""
                SELECT c.id AS Id, COALESCE(NULLIF(TRIM(c.display_name), ''), 'Untitled collection') AS Name
                FROM collections c
                WHERE c.id = @id AND {MovableCollectionSql("c")};
                """, new { id }, transaction);
            return collection.Id == Guid.Empty
                ? null
                : new LibraryMoveDestinationInfo(collection.Id, LibraryMoveKinds.Collection, string.Empty, collection.Name, null, null, null, null, null);
        }

        var isParent = string.Equals(work.WorkKind, "parent", StringComparison.OrdinalIgnoreCase);
        var mediaType = work.MediaType ?? string.Empty;
        if (string.Equals(mediaType, "Music", StringComparison.OrdinalIgnoreCase) && isParent)
        {
            return new LibraryMoveDestinationInfo(
                work.Id, LibraryMoveKinds.Album, mediaType, work.Label ?? string.Empty, work.Artist, null, null,
                CollectionOf(connection, transaction, work.Id), work.ManifestJson);
        }

        if (string.Equals(mediaType, "TV", StringComparison.OrdinalIgnoreCase) && isParent && work.ParentWorkId is { } show)
        {
            var season = work.Ordinal ?? 0;
            return new LibraryMoveDestinationInfo(
                work.Id, LibraryMoveKinds.Season, mediaType, season == 0 ? "Specials" : $"Season {season.ToString(CultureInfo.InvariantCulture)}",
                work.ParentTitle, season, show, CollectionOf(connection, transaction, show), null);
        }

        if (mediaType is "Books" or "Comics" or "Audiobooks" or "Movies" && isParent && work.ParentWorkId is null)
        {
            return new LibraryMoveDestinationInfo(
                work.Id, LibraryMoveKinds.Series, mediaType, work.Label ?? string.Empty, work.Author, null, null,
                CollectionOf(connection, transaction, work.Id), null);
        }

        if (string.Equals(mediaType, "Audiobooks", StringComparison.OrdinalIgnoreCase) && !isParent)
        {
            return new LibraryMoveDestinationInfo(
                work.Id, LibraryMoveKinds.Audiobook, mediaType, work.RecordingLabel ?? string.Empty, work.Author, null, null, null, null);
        }

        return null;
    }

    /// <summary>The Series collection a place belongs to: the one on the place itself, else the one most of what is under it carries.</summary>
    private static Guid? CollectionOf(IDbConnection connection, IDbTransaction? transaction, Guid workId) =>
        connection.QueryFirstOrDefault<Guid?>(
            """
            -- readiness: moves work on titles by id or by the place they sit in, whatever their readiness
            SELECT cid FROM (
                SELECT w.collection_id AS cid, 0 AS tier, 1 AS n FROM works w WHERE w.id = @workId AND w.collection_id IS NOT NULL
                UNION ALL
                SELECT c.collection_id, 1, COUNT(*) FROM works c
                WHERE c.parent_work_id = @workId AND c.collection_id IS NOT NULL GROUP BY c.collection_id
                UNION ALL
                SELECT g.collection_id, 2, COUNT(*) FROM works c JOIN works g ON g.parent_work_id = c.id
                WHERE c.parent_work_id = @workId AND g.collection_id IS NOT NULL GROUP BY g.collection_id
            )
            ORDER BY tier, n DESC
            LIMIT 1;
            """, new { workId }, transaction);

    private static List<LibraryMoveSourceFacts> FindSourcePlaces(
        IDbConnection connection,
        IDbTransaction? transaction,
        IReadOnlyList<LibraryMoveItem> allItems,
        Guid destinationId)
    {
        // An item matched within the place it already sits in leaves nothing, so it empties nothing.
        var items = allItems.Where(item => (item.IsEdition ? item.WorkId : item.ParentWorkId) != destinationId).ToList();
        var movingWorks = items.Where(item => !item.IsEdition).Select(item => item.WorkId).ToHashSet();
        var movingEditions = items.Where(item => item.IsEdition).Select(item => item.EditionId!.Value).ToHashSet();
        var emptied = new HashSet<Guid>();
        var places = new Dictionary<Guid, LibraryMoveSourceFacts>();

        // An audiobook that loses its last file is itself a place that empties.
        foreach (var group in items.Where(item => item.IsEdition).GroupBy(item => item.WorkId))
        {
            var left = connection.ExecuteScalar<long>(
                "SELECT COUNT(*) FROM editions WHERE work_id = @id AND id NOT IN @moving;",
                new { id = group.Key, moving = ToBlobs([.. movingEditions]) },
                transaction);
            places[group.Key] = new LibraryMoveSourceFacts(group.Key, WorkLabel(connection, transaction, group.Key), left == 0);
            if (left == 0)
            {
                emptied.Add(group.Key);
                movingWorks.Add(group.Key);
            }
        }

        var startParents = items.Where(item => !item.IsEdition && item.ParentWorkId.HasValue).Select(item => item.ParentWorkId!.Value)
            .Concat(emptied.Select(work => connection.QueryFirstOrDefault<Guid?>("-- readiness: moves work on titles by id or by the place they sit in, whatever their readiness\nSELECT parent_work_id FROM works WHERE id = @id;", new { id = work }, transaction))
                .Where(parent => parent.HasValue).Select(parent => parent!.Value))
            .Distinct()
            .ToList();

        foreach (var start in startParents)
        {
            var current = (Guid?)start;
            while (current is { } parent && !places.ContainsKey(parent))
            {
                var left = connection.ExecuteScalar<long>(
                    "-- readiness: moves work on titles by id or by the place they sit in, whatever their readiness\nSELECT COUNT(*) FROM works WHERE parent_work_id = @id AND id NOT IN @moving;",
                    new { id = parent, moving = ToBlobs([.. movingWorks, .. emptied]) },
                    transaction);
                places[parent] = new LibraryMoveSourceFacts(parent, WorkLabel(connection, transaction, parent), left == 0);
                if (left != 0)
                {
                    break;
                }

                emptied.Add(parent);
                movingWorks.Add(parent);
                current = connection.QueryFirstOrDefault<Guid?>("-- readiness: moves work on titles by id or by the place they sit in, whatever their readiness\nSELECT parent_work_id FROM works WHERE id = @id;", new { id = parent }, transaction);
            }
        }

        return [.. places.Values];
    }

    /// <summary>What a place is called to a person: "OK Computer", "Season 2", "Specials", a series' or show's own name.</summary>
    private static string WorkLabel(IDbConnection connection, IDbTransaction? transaction, Guid workId)
    {
        var row = connection.QueryFirstOrDefault<(string? MediaType, string? WorkKind, Guid? ParentId, long? Ordinal, string? Label)>(
            $"""
            -- readiness: moves work on titles by id or by the place they sit in, whatever their readiness
            SELECT w.media_type AS MediaType, w.work_kind AS WorkKind, w.parent_work_id AS ParentId, w.ordinal AS Ordinal,
                   COALESCE({ParentLabelSql("w")}, {WorkTitleSql("w")}) AS Label
            FROM works w WHERE w.id = @workId;
            """, new { workId }, transaction);
        if (string.Equals(row.MediaType, "TV", StringComparison.OrdinalIgnoreCase)
            && string.Equals(row.WorkKind, "parent", StringComparison.OrdinalIgnoreCase)
            && row.ParentId is not null)
        {
            return (row.Ordinal ?? 0) == 0 ? "Specials" : $"Season {row.Ordinal!.Value.ToString(CultureInfo.InvariantCulture)}";
        }

        return string.IsNullOrWhiteSpace(row.Label) ? "This place" : row.Label.Trim();
    }

    // ----- SQL fragments ---------------------------------------------------------------------------------------------------

    // The columns both item reads select. The title, labels and duration come from the same fragments the other reads use.
    private static readonly string ItemColumns = $"""
        -- readiness: moves work on titles by id or by the place they sit in, whatever their readiness
        w.media_type AS MediaType,
        w.work_kind AS WorkKind,
        w.id AS WorkId,
        w.parent_work_id AS ParentWorkId,
        w.ordinal AS Ordinal,
        w.ordinal_sort AS OrdinalSort,
        w.placement_unmatched AS WorkUnmatched,
        (SELECT CAST(cv.value AS INTEGER) FROM canonical_values cv WHERE cv.entity_id = w.id AND cv.key = 'disc_number' LIMIT 1) AS DiscNumber,
        {WorkTitleSql("w")} AS WorkTitle,
        {ParentLabelSql("p")} AS ParentTitle,
        p.ordinal AS ParentOrdinal,
        {ParentLabelSql("gp")} AS GrandparentTitle,
        COALESCE((SELECT cv.value FROM canonical_values cv WHERE cv.entity_id = w.id AND cv.key = 'identity_revision' LIMIT 1), '') AS IdentityRevision,
        {DurationSql("w")} AS DurationText,
        (SELECT COUNT(*) FROM editions e2 WHERE e2.work_id = w.id) AS EditionCount,
        CASE WHEN w.work_kind = 'parent' OR EXISTS (SELECT 1 FROM works child WHERE child.parent_work_id = w.id) THEN 1 ELSE 0 END AS IsContainer
        """;

    private static string WorkTitleSql(string alias) => $"""
        COALESCE(
            (SELECT cv.value FROM canonical_values cv
             WHERE cv.entity_id = {alias}.id AND cv.key IN ('episode_title', 'track_title', 'title', 'album', 'series', 'show_name')
               AND NULLIF(cv.value, '') IS NOT NULL
             ORDER BY CASE cv.key WHEN 'episode_title' THEN 0 WHEN 'track_title' THEN 1 WHEN 'title' THEN 2 WHEN 'album' THEN 3 WHEN 'series' THEN 4 ELSE 5 END
             LIMIT 1),
            (SELECT cv.value FROM canonical_values cv
             WHERE cv.entity_id IN (SELECT ma.id FROM editions e JOIN media_assets ma ON ma.edition_id = e.id WHERE e.work_id = {alias}.id)
               AND cv.key IN ('episode_title', 'track_title', 'title') AND NULLIF(cv.value, '') IS NOT NULL
             ORDER BY CASE cv.key WHEN 'episode_title' THEN 0 WHEN 'track_title' THEN 1 ELSE 2 END
             LIMIT 1),
            {alias}.parent_key)
        """;

    private static string ParentLabelSql(string alias) => $"""
        COALESCE(
            (SELECT cv.value FROM canonical_values cv
             WHERE cv.entity_id = {alias}.id AND cv.key IN ('album', 'show_name', 'series', 'title') AND NULLIF(cv.value, '') IS NOT NULL
             ORDER BY CASE cv.key WHEN 'album' THEN 0 WHEN 'show_name' THEN 1 WHEN 'series' THEN 2 ELSE 3 END
             LIMIT 1),
            {alias}.parent_key)
        """;

    // Credits are lists: ingestion stores an author or an album's artist one name per row in canonical_value_arrays, so a label reads the
    // first name of the list (album artist before track artist, author before creator) and only then a single value.
    private static string AuthorSql(string alias) => $"""
        COALESCE(
            (SELECT a.value FROM canonical_value_arrays a
             WHERE a.entity_id = {alias}.id AND a.key IN ('author', 'creator', 'director') AND NULLIF(a.value, '') IS NOT NULL
             ORDER BY CASE a.key WHEN 'author' THEN 0 WHEN 'creator' THEN 1 ELSE 2 END, a.ordinal
             LIMIT 1),
            (SELECT cv.value FROM canonical_values cv
             WHERE cv.entity_id = {alias}.id AND cv.key IN ('author', 'creator', 'director') AND NULLIF(cv.value, '') IS NOT NULL
             ORDER BY CASE cv.key WHEN 'author' THEN 0 WHEN 'creator' THEN 1 ELSE 2 END
             LIMIT 1))
        """;

    private static string ArtistSql(string alias) => $"""
        COALESCE(
            (SELECT a.value FROM canonical_value_arrays a
             WHERE a.entity_id = {alias}.id AND a.key IN ('album_artist', 'artist') AND NULLIF(a.value, '') IS NOT NULL
             ORDER BY CASE a.key WHEN 'album_artist' THEN 0 ELSE 1 END, a.ordinal
             LIMIT 1),
            (SELECT cv.value FROM canonical_values cv
             WHERE cv.entity_id = {alias}.id AND cv.key IN ('album_artist', 'artist') AND NULLIF(cv.value, '') IS NOT NULL
             ORDER BY CASE cv.key WHEN 'album_artist' THEN 0 ELSE 1 END
             LIMIT 1))
        """;

    private static string DurationSql(string alias) => $"""
        COALESCE(
            (SELECT cv.value FROM canonical_values cv
             WHERE cv.entity_id = {alias}.id AND cv.key IN ('duration_seconds', 'duration') AND NULLIF(cv.value, '') IS NOT NULL
             ORDER BY CASE cv.key WHEN 'duration_seconds' THEN 0 ELSE 1 END LIMIT 1),
            (SELECT cv.value FROM canonical_values cv
             WHERE cv.entity_id IN (SELECT ma.id FROM editions e JOIN media_assets ma ON ma.edition_id = e.id WHERE e.work_id = {alias}.id)
               AND cv.key IN ('duration_seconds', 'duration') AND NULLIF(cv.value, '') IS NOT NULL
             ORDER BY CASE cv.key WHEN 'duration_seconds' THEN 0 ELSE 1 END LIMIT 1))
        """;

    // A file counts however the row looks: a lost file's row still means the entry is taken, and nothing is replaced silently.
    private static string HasFileSql(string alias) => $"""
        CASE WHEN EXISTS (SELECT 1 FROM work_owned_assets woa JOIN media_assets ma ON ma.id = woa.asset_id WHERE woa.work_id = {alias}.id)
                  OR EXISTS (SELECT 1 FROM editions e JOIN media_assets ma ON ma.edition_id = e.id WHERE e.work_id = {alias}.id)
             THEN 1 ELSE 0 END
        """;

    // The hand-picked collections a move can add to: curated (Custom, stored members), the library's own, no personal-media source.
    private static string MovableCollectionSql(string alias) => $"""
        {alias}.membership_mode = 'Manual' AND {alias}.collection_type = 'Custom' AND {alias}.resolution = 'materialized'
        AND {alias}.owner_kind = 'Library' AND {alias}.scope = 'library'
        AND NOT EXISTS (SELECT 1 FROM collection_view_sources s WHERE s.collection_id = {alias}.id)
        """;

    // ----- small helpers -----------------------------------------------------------------------------------------------------

    private static byte[][] ToBlobs(IReadOnlyList<Guid> ids) => [.. ids.Distinct().Select(GuidSql.ToBlob)];

    private static string EscapeLike(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("%", "\\%", StringComparison.Ordinal).Replace("_", "\\_", StringComparison.Ordinal);

    internal static double? ParseSeconds(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var value = text.Trim();
        if (value.Contains(':', StringComparison.Ordinal))
        {
            var parts = value.Split(':');
            if (parts.Length is 2 or 3 && parts.All(part => double.TryParse(part, NumberStyles.Float, CultureInfo.InvariantCulture, out _)))
            {
                var seconds = parts.Select(part => double.Parse(part, CultureInfo.InvariantCulture)).Aggregate(0d, (total, part) => total * 60 + part);
                return seconds > 0 ? seconds : null;
            }

            return null;
        }

        return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) && number > 0
            ? number >= 60000 ? number / 1000d : number
            : null;
    }

    private static int? ParseWholeNumber(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var digits = new string(text.Trim().TakeWhile(char.IsDigit).ToArray());
        return int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var value) ? value : null;
    }

    /// <summary>Thrown from the transaction body so the whole move rolls back; turned into a conflict result by the caller of the commit.</summary>
    private sealed class MoveRefusedException(string reason, bool isRefusal) : InvalidOperationException(reason)
    {
        /// <summary>True when the request is never allowed (a refusal, answered 400); false when the state changed (answered 409).</summary>
        public bool IsRefusal { get; } = isRefusal;
    }

    private sealed class ItemRow
    {
        public Guid ItemId { get; init; }
        public long IsEdition { get; init; }
        public Guid? EditionId { get; init; }
        public string? MediaType { get; init; }
        public string? WorkKind { get; init; }
        public Guid WorkId { get; init; }
        public Guid? ParentWorkId { get; init; }
        public int? Ordinal { get; init; }
        public double? OrdinalSort { get; init; }
        public long WorkUnmatched { get; init; }
        public int? DiscNumber { get; init; }
        public string? WorkTitle { get; init; }
        public string? ParentTitle { get; init; }
        public int? ParentOrdinal { get; init; }
        public string? GrandparentTitle { get; init; }
        public string? IdentityRevision { get; init; }
        public string? DurationText { get; init; }
        public int EditionCount { get; init; }
        public long IsContainer { get; init; }
    }

    private sealed class DestinationRow
    {
        public Guid Id { get; init; }
        public string Kind { get; init; } = string.Empty;
        public string? MediaType { get; init; }
        public string? Label { get; init; }
        public string? ParentLabel { get; init; }
        public long? SeasonOrdinal { get; init; }

        public LibraryMoveDestinationRow ToDestination() => new(
            Id,
            Kind,
            MediaType ?? string.Empty,
            string.IsNullOrWhiteSpace(Label) ? "Untitled" : Label.Trim(),
            string.IsNullOrWhiteSpace(ParentLabel) ? null : ParentLabel.Trim());
    }

    private sealed class WorkDestinationRow
    {
        public Guid Id { get; init; }
        public string? MediaType { get; init; }
        public string? WorkKind { get; init; }
        public Guid? ParentWorkId { get; init; }
        public int? Ordinal { get; init; }
        public string? Label { get; init; }
        public string? RecordingLabel { get; init; }
        public string? ParentTitle { get; init; }
        public string? Author { get; init; }
        public string? Artist { get; init; }
        public string? ManifestJson { get; init; }
    }

    private sealed class ChildRow
    {
        public Guid WorkId { get; init; }
        public string? Title { get; init; }
        public int? Ordinal { get; init; }
        public double? OrdinalSort { get; init; }
        public int? DiscNumber { get; init; }
        public long HasFile { get; init; }
        public long IsCatalogOnly { get; init; }
        public long IsUnmatched { get; init; }
        public string? DurationText { get; init; }
    }

    private sealed class PartRow
    {
        public Guid EditionId { get; init; }
        public long IsUnmatched { get; init; }
        public string? FilePath { get; init; }
        public string? TrackTitle { get; init; }
        public string? PartNumberText { get; init; }
        public string? PartCountText { get; init; }
        public string? DurationText { get; init; }
    }

    private sealed class EntryRow
    {
        public Guid WorkId { get; init; }
        public int? Ordinal { get; init; }
        public double? OrdinalSort { get; init; }
        public int? DiscNumber { get; init; }
    }

    private sealed class SourceCollectionRow
    {
        public Guid CollectionId { get; init; }
        public string Name { get; init; } = string.Empty;
        public int ItemsInRequest { get; init; }
        public int TotalItems { get; init; }
        public long IsRemovable { get; init; }
    }
}
