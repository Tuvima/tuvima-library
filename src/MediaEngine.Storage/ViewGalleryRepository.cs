using Dapper;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.PersonalMedia;
using MediaEngine.Storage.Contracts;

namespace MediaEngine.Storage;

public sealed class ViewGalleryRepository(IDatabaseConnection database) : IViewGalleryRepository
{
    public Task<ViewGallery?> GetAsync(
        Guid galleryId, bool includeDocuments = true, CancellationToken ct = default,
        IReadOnlyCollection<string>? blockedKinds = null)
    {
        ValidateId(galleryId, nameof(galleryId));
        ct.ThrowIfCancellationRequested();
        using var connection = database.CreateConnection();
        var row = connection.QuerySingleOrDefault<GalleryRow>(new CommandDefinition(
            SelectGallerySql + " WHERE vg.id = @galleryId GROUP BY vg.id;",
            new DynamicParameters(new { galleryId, includeDocuments = includeDocuments ? 1 : 0 }).AddBlockedKinds(blockedKinds),
            cancellationToken: ct));
        return Task.FromResult(row is null ? null : MapWithDynamicCount(connection, row, includeDocuments, blockedKinds, ct));
    }

    public Task<IReadOnlyList<ViewGallery>> GetOwnedAsync(
        Guid ownerProfileId, bool includeDocuments = true, CancellationToken ct = default,
        IReadOnlyCollection<string>? blockedKinds = null)
    {
        ValidateId(ownerProfileId, nameof(ownerProfileId));
        ct.ThrowIfCancellationRequested();
        using var connection = database.CreateConnection();
        var rows = connection.Query<GalleryRow>(new CommandDefinition(
            SelectGallerySql + """
             WHERE vg.owner_profile_id = @ownerProfileId
             GROUP BY vg.id
             ORDER BY vg.sort_order, vg.updated_at DESC, vg.id;
            """, new DynamicParameters(new { ownerProfileId, includeDocuments = includeDocuments ? 1 : 0 }).AddBlockedKinds(blockedKinds),
            cancellationToken: ct));
        return Task.FromResult<IReadOnlyList<ViewGallery>>(
            rows.Select(row => MapWithDynamicCount(connection, row, includeDocuments, blockedKinds, ct)).ToList());
    }

    public Task<IReadOnlyList<ViewGallery>> GetSharedWithAsync(
        Guid profileId, bool includeDocuments = true, CancellationToken ct = default,
        IReadOnlyCollection<string>? blockedKinds = null)
    {
        ValidateId(profileId, nameof(profileId));
        ct.ThrowIfCancellationRequested();
        using var connection = database.CreateConnection();
        var rows = connection.Query<GalleryRow>(new CommandDefinition(
            SelectGallerySql + """
              JOIN view_gallery_shares vgs ON vgs.gallery_id = vg.id
             WHERE vgs.profile_id = @profileId
             GROUP BY vg.id
             ORDER BY vgs.shared_at DESC, vg.id;
            """, new DynamicParameters(new { profileId, includeDocuments = includeDocuments ? 1 : 0 }).AddBlockedKinds(blockedKinds),
            cancellationToken: ct));
        return Task.FromResult<IReadOnlyList<ViewGallery>>(
            rows.Select(row => MapWithDynamicCount(connection, row, includeDocuments, blockedKinds, ct)).ToList());
    }

    public Task<ViewGallery> CreateAsync(CreateViewGalleryCommand command, CancellationToken ct = default)
    {
        Validate(command);
        return database.ExecuteWriteAsync((connection, transaction, token) =>
        {
            token.ThrowIfCancellationRequested();
            RequireOwnedSpace(connection, transaction, command.PersonalSpaceId, command.OwnerProfileId, token);
            ValidateCover(connection, transaction, command.PersonalSpaceId, command.CoverItemId, token);
            ValidateSoundtrack(connection, transaction, command.OwnerProfileId, command.SoundtrackPlaylistId, token);
            var id = Guid.NewGuid();
            var now = DateTimeOffset.UtcNow;
            connection.Execute(new CommandDefinition("""
                INSERT INTO view_galleries
                    (id, owner_profile_id, personal_space_id, name, description, gallery_kind,
                     smart_rule_json, cover_item_id, soundtrack_playlist_id, sort_order, created_at, updated_at)
                VALUES
                    (@id, @OwnerProfileId, @PersonalSpaceId, @Name, @Description, @Kind,
                     @SmartRuleJson, @CoverItemId, @SoundtrackPlaylistId, @SortOrder, @now, @now);
                """, new
            {
                id,
                command.OwnerProfileId,
                command.PersonalSpaceId,
                Name = command.Name.Trim(),
                Description = NullIfWhiteSpace(command.Description),
                Kind = ToStorage(command.Kind),
                SmartRuleJson = NormalizeRule(command.Kind, command.SmartRuleJson),
                command.CoverItemId,
                command.SoundtrackPlaylistId,
                command.SortOrder,
                now,
            }, transaction, cancellationToken: token));
            return new ViewGallery(id, command.OwnerProfileId, command.PersonalSpaceId,
                command.Name.Trim(), NullIfWhiteSpace(command.Description), command.Kind,
                NormalizeRule(command.Kind, command.SmartRuleJson), command.CoverItemId,
                command.SortOrder, 0, now, now, command.SoundtrackPlaylistId);
        }, ct);
    }

    public Task<ViewGallery?> UpdateAsync(UpdateViewGalleryCommand command, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        ValidateId(command.GalleryId, nameof(command));
        ArgumentException.ThrowIfNullOrWhiteSpace(command.Name);
        var normalizedRule = NormalizeRule(command.Kind, command.SmartRuleJson);
        return database.ExecuteWriteAsync((connection, transaction, token) =>
        {
            token.ThrowIfCancellationRequested();
            var current = connection.QuerySingleOrDefault<(Guid PersonalSpaceId, Guid OwnerProfileId, string Kind)>(new CommandDefinition("""
                SELECT personal_space_id AS PersonalSpaceId, owner_profile_id AS OwnerProfileId, gallery_kind AS Kind
                  FROM view_galleries WHERE id = @GalleryId;
                """, command, transaction, cancellationToken: token));
            if (current.PersonalSpaceId == Guid.Empty)
            {
                return null;
            }

            ValidateCover(connection, transaction, current.PersonalSpaceId, command.CoverItemId, token);
            ValidateSoundtrack(connection, transaction, current.OwnerProfileId, command.SoundtrackPlaylistId, token);
            if (command.Kind == ViewGalleryKind.Smart)
            {
                connection.Execute(new CommandDefinition(
                    "DELETE FROM view_gallery_items WHERE gallery_id = @GalleryId;",
                    command, transaction, cancellationToken: token));
            }
            var now = DateTimeOffset.UtcNow;
            connection.Execute(new CommandDefinition("""
                UPDATE view_galleries SET
                    name = @Name, description = @Description, gallery_kind = @Kind,
                    smart_rule_json = @SmartRuleJson,
                    -- A cover in Trash reads as no cover, so a save that sends none keeps it for Restore.
                    cover_item_id = CASE WHEN @CoverItemId IS NULL AND EXISTS (
                            SELECT 1 FROM local_items cv
                             WHERE cv.id = view_galleries.cover_item_id AND cv.trashed_at IS NOT NULL)
                        THEN cover_item_id ELSE @CoverItemId END,
                    soundtrack_playlist_id = @SoundtrackPlaylistId,
                    sort_order = @SortOrder, updated_at = @now
                 WHERE id = @GalleryId;
                """, new
            {
                command.GalleryId,
                Name = command.Name.Trim(),
                Description = NullIfWhiteSpace(command.Description),
                Kind = ToStorage(command.Kind),
                SmartRuleJson = normalizedRule,
                command.CoverItemId,
                command.SoundtrackPlaylistId,
                command.SortOrder,
                now,
            }, transaction, cancellationToken: token));
            var count = connection.ExecuteScalar<int>(new CommandDefinition(
                """
                SELECT COUNT(1) FROM view_gallery_items vgi
                 WHERE vgi.gallery_id = @GalleryId
                   AND NOT EXISTS (SELECT 1 FROM local_items li WHERE li.id = vgi.item_id AND li.trashed_at IS NOT NULL);
                """,
                command, transaction, cancellationToken: token));
            var row = connection.QuerySingle<GalleryRow>(new CommandDefinition(
                SelectGallerySql + " WHERE vg.id = @GalleryId GROUP BY vg.id;",
                new DynamicParameters(new { command.GalleryId, includeDocuments = 1 }).AddBlockedKinds(null), transaction, cancellationToken: token));
            return Map(row) with { ItemCount = count };
        }, ct);
    }

    public Task<bool> DeleteAsync(Guid galleryId, CancellationToken ct = default)
    {
        ValidateId(galleryId, nameof(galleryId));
        return database.ExecuteWriteAsync((connection, transaction, token) =>
            connection.Execute(new CommandDefinition(
                "DELETE FROM view_galleries WHERE id = @galleryId;",
                new { galleryId }, transaction, cancellationToken: token)) != 0, ct);
    }

    public Task<ViewGalleryItemPage> GetItemsAsync(
        Guid galleryId,
        int? afterPosition = null,
        Guid? afterItemId = null,
        int limit = 100,
        bool includeDocuments = true,
        CancellationToken ct = default,
        IReadOnlyCollection<string>? blockedKinds = null)
    {
        ValidateId(galleryId, nameof(galleryId));
        if ((afterPosition.HasValue) != (afterItemId.HasValue))
        {
            throw new ArgumentException("Both Gallery item cursor values are required together.");
        }

        if (afterPosition < 0 || limit is < 1 or > 500)
        {
            throw new ArgumentOutOfRangeException(nameof(limit));
        }

        ct.ThrowIfCancellationRequested();
        using var connection = database.CreateConnection();
        var rows = connection.Query<GalleryItemRow>(new CommandDefinition("""
            SELECT gallery_id AS GalleryId, item_id AS ItemId, position AS Position, added_at AS AddedAt
              FROM view_gallery_items
             WHERE gallery_id = @galleryId
               AND (@afterPosition IS NULL OR position > @afterPosition
                    OR (position = @afterPosition AND item_id > @afterItemId))
               AND (@includeDocuments = 1 OR NOT EXISTS (
                        SELECT 1 FROM local_items li
                         WHERE li.id = view_gallery_items.item_id AND li.media_kind = 'document'))
               AND (@hasBlockedKinds = 0 OR NOT EXISTS (
                        SELECT 1 FROM local_items li
                         WHERE li.id = view_gallery_items.item_id AND li.content_kind IN @blockedKinds))
               -- An item in Trash leaves the Gallery while it is there; its place is kept, so Restore brings it back.
               AND NOT EXISTS (
                        SELECT 1 FROM local_items li
                         WHERE li.id = view_gallery_items.item_id AND li.trashed_at IS NOT NULL)
             ORDER BY position, item_id
             LIMIT @take;
            """, new DynamicParameters(new
        {
            galleryId,
            afterPosition,
            afterItemId,
            take = limit + 1,
            includeDocuments = includeDocuments ? 1 : 0,
        }).AddBlockedKinds(blockedKinds), cancellationToken: ct)).ToList();
        var hasMore = rows.Count > limit;
        if (hasMore)
        {
            rows.RemoveAt(rows.Count - 1);
        }

        var items = rows.Select(row => new ViewGalleryItem(
            row.GalleryId, row.ItemId, row.Position, DateTimeOffset.Parse(row.AddedAt))).ToList();
        var last = items.LastOrDefault();
        return Task.FromResult(new ViewGalleryItemPage(
            items, hasMore ? last!.Position : null, hasMore ? last!.ItemId : null, hasMore));
    }

    public Task<AddViewGalleryItemsResult> AddItemsAsync(
        Guid galleryId,
        IReadOnlyCollection<Guid> itemIds,
        bool includeDocuments = true,
        CancellationToken ct = default)
    {
        ValidateId(galleryId, nameof(galleryId));
        ArgumentNullException.ThrowIfNull(itemIds);
        var distinctIds = itemIds.Where(id => id != Guid.Empty).Distinct().ToArray();
        return database.ExecuteWriteAsync((connection, transaction, token) =>
        {
            token.ThrowIfCancellationRequested();
            var gallery = RequireManualGallery(connection, transaction, galleryId, token);
            if (distinctIds.Any(itemId => connection.ExecuteScalar<long>(new CommandDefinition("""
                    SELECT COUNT(1) FROM local_items
                     WHERE personal_space_id = @personalSpaceId AND id = @itemId;
                    """, new { personalSpaceId = gallery.PersonalSpaceId, itemId },
                    transaction, cancellationToken: token)) == 0))
            {
                throw new InvalidOperationException("Every Gallery item must belong to the Gallery owner's Personal Space.");
            }

            // A profile without Documents cannot put one in a Gallery (the items are already known to be the owner's).
            if (!includeDocuments && distinctIds.Any(itemId => connection.ExecuteScalar<long>(new CommandDefinition("""
                    SELECT COUNT(1) FROM local_items WHERE id = @itemId AND media_kind = 'document';
                    """, new { itemId }, transaction, cancellationToken: token)) != 0))
            {
                throw new UnauthorizedAccessException("Documents are not available for this profile.");
            }
            var position = connection.ExecuteScalar<int>(new CommandDefinition(
                "SELECT COALESCE(MAX(position), -1) + 1 FROM view_gallery_items WHERE gallery_id = @galleryId;",
                new { galleryId }, transaction, cancellationToken: token));
            var now = DateTimeOffset.UtcNow;
            var added = 0;
            foreach (var itemId in distinctIds)
            {
                token.ThrowIfCancellationRequested();
                added += connection.Execute(new CommandDefinition("""
                    INSERT OR IGNORE INTO view_gallery_items (gallery_id, item_id, position, added_at)
                    SELECT @galleryId, @itemId, @position, @now;
                    """, new { galleryId, itemId, position, now, personalSpaceId = gallery.PersonalSpaceId },
                    transaction, cancellationToken: token));
                position++;
            }
            if (added != 0)
            {
                connection.Execute(new CommandDefinition(
                    "UPDATE view_galleries SET updated_at = @now WHERE id = @galleryId;",
                    new { galleryId, now }, transaction, cancellationToken: token));
            }
            return new AddViewGalleryItemsResult(added, distinctIds.Length - added);
        }, ct);
    }

    public Task<int> RemoveItemsAsync(
        Guid galleryId, IReadOnlyCollection<Guid> itemIds, bool includeDocuments = true, CancellationToken ct = default)
    {
        ValidateId(galleryId, nameof(galleryId));
        ArgumentNullException.ThrowIfNull(itemIds);
        var ids = itemIds.Where(id => id != Guid.Empty).Distinct().ToArray();
        return database.ExecuteWriteAsync((connection, transaction, token) =>
        {
            RequireManualGallery(connection, transaction, galleryId, token);
            var removed = 0;
            foreach (var itemId in ids)
            {
                removed += connection.Execute(new CommandDefinition("""
                    DELETE FROM view_gallery_items
                     WHERE gallery_id = @galleryId AND item_id = @itemId
                       AND (@includeDocuments = 1 OR NOT EXISTS (
                            SELECT 1 FROM local_items li WHERE li.id = @itemId AND li.media_kind = 'document'));
                    """, new { galleryId, itemId, includeDocuments = includeDocuments ? 1 : 0 },
                    transaction, cancellationToken: token));
            }
            if (removed != 0)
            {
                connection.Execute(new CommandDefinition(
                "UPDATE view_galleries SET updated_at = @now WHERE id = @galleryId;",
                new { galleryId, now = DateTimeOffset.UtcNow }, transaction, cancellationToken: token));
            }

            return removed;
        }, ct);
    }

    public Task<bool> SetItemPositionAsync(
        Guid galleryId, Guid itemId, int position, bool includeDocuments = true, CancellationToken ct = default)
    {
        ValidateId(galleryId, nameof(galleryId)); ValidateId(itemId, nameof(itemId));
        if (position < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(position));
        }

        return database.ExecuteWriteAsync((connection, transaction, token) =>
        {
            RequireManualGallery(connection, transaction, galleryId, token);
            var changed = connection.Execute(new CommandDefinition("""
                UPDATE view_gallery_items SET position = @position
                 WHERE gallery_id = @galleryId AND item_id = @itemId
                   AND (@includeDocuments = 1 OR NOT EXISTS (
                        SELECT 1 FROM local_items li WHERE li.id = @itemId AND li.media_kind = 'document'));
                """, new { galleryId, itemId, position, includeDocuments = includeDocuments ? 1 : 0 },
                transaction, cancellationToken: token)) != 0;
            if (changed)
            {
                connection.Execute(new CommandDefinition(
                "UPDATE view_galleries SET updated_at = @now WHERE id = @galleryId;",
                new { galleryId, now = DateTimeOffset.UtcNow }, transaction, cancellationToken: token));
            }

            return changed;
        }, ct);
    }

    public Task ReplaceSharesAsync(
        Guid galleryId,
        IReadOnlyCollection<(Guid ProfileId, ViewGallerySharePermission Permission)> shares,
        CancellationToken ct = default)
    {
        ValidateId(galleryId, nameof(galleryId)); ArgumentNullException.ThrowIfNull(shares);
        return database.ExecuteWriteAsync((connection, transaction, token) =>
        {
            var ownerId = connection.QuerySingleOrDefault<Guid?>(new CommandDefinition(
                "SELECT owner_profile_id FROM view_galleries WHERE id = @galleryId;",
                new { galleryId }, transaction, cancellationToken: token));
            if (!ownerId.HasValue)
            {
                throw new InvalidOperationException($"Gallery '{galleryId:D}' does not exist.");
            }

            connection.Execute(new CommandDefinition(
                "DELETE FROM view_gallery_shares WHERE gallery_id = @galleryId;",
                new { galleryId }, transaction, cancellationToken: token));
            var now = DateTimeOffset.UtcNow;
            foreach (var share in shares.Where(share => share.ProfileId != Guid.Empty && share.ProfileId != ownerId).DistinctBy(share => share.ProfileId))
            {
                if (connection.ExecuteScalar<long>(new CommandDefinition(
                        "SELECT COUNT(1) FROM profiles WHERE id = @profileId;",
                        new { profileId = share.ProfileId },
                        transaction, cancellationToken: token)) == 0)
                {
                    throw new InvalidOperationException($"Profile '{share.ProfileId:D}' does not exist.");
                }

                connection.Execute(new CommandDefinition("""
                    INSERT INTO view_gallery_shares (gallery_id, profile_id, permission, shared_at)
                    VALUES (@galleryId, @ProfileId, @Permission, @now);
                    """, new { galleryId, share.ProfileId, Permission = ToStorage(share.Permission), now },
                    transaction, cancellationToken: token));
            }
        }, ct);
    }

    public Task<IReadOnlyList<ViewGalleryShare>> GetSharesAsync(Guid galleryId, CancellationToken ct = default)
    {
        ValidateId(galleryId, nameof(galleryId)); ct.ThrowIfCancellationRequested();
        using var connection = database.CreateConnection();
        var rows = connection.Query<ShareRow>(new CommandDefinition("""
            SELECT gallery_id AS GalleryId, profile_id AS ProfileId,
                   permission AS Permission, shared_at AS SharedAt
              FROM view_gallery_shares WHERE gallery_id = @galleryId
             ORDER BY shared_at, profile_id;
            """, new { galleryId }, cancellationToken: ct));
        return Task.FromResult<IReadOnlyList<ViewGalleryShare>>(rows.Select(row => new ViewGalleryShare(
            row.GalleryId, row.ProfileId, ParsePermission(row.Permission), DateTimeOffset.Parse(row.SharedAt))).ToList());
    }

    private const string SelectGallerySql = """
        SELECT vg.id AS Id, vg.owner_profile_id AS OwnerProfileId,
               vg.personal_space_id AS PersonalSpaceId, vg.name AS Name,
               vg.description AS Description, vg.gallery_kind AS GalleryKind,
               vg.smart_rule_json AS SmartRuleJson,
               -- A cover in Trash reads as no cover (the Gallery falls back to its newest photo); Restore brings it back.
               CASE WHEN EXISTS (SELECT 1 FROM local_items cv WHERE cv.id = vg.cover_item_id AND cv.trashed_at IS NOT NULL)
                    THEN NULL ELSE vg.cover_item_id END AS CoverItemId,
               vg.soundtrack_playlist_id AS SoundtrackPlaylistId,
               vg.sort_order AS SortOrder,
               COUNT(CASE WHEN (@includeDocuments = 1 OR vli.media_kind <> 'document')
                               AND (@hasBlockedKinds = 0 OR vli.content_kind IS NULL OR vli.content_kind NOT IN @blockedKinds)
                               AND vli.trashed_at IS NULL THEN vgi.item_id END) AS ItemCount,
               vg.created_at AS CreatedAt, vg.updated_at AS UpdatedAt
          FROM view_galleries vg
          LEFT JOIN view_gallery_items vgi ON vgi.gallery_id = vg.id
          LEFT JOIN local_items vli ON vli.id = vgi.item_id
        """;

    private static (Guid PersonalSpaceId, string Kind) RequireManualGallery(
        System.Data.IDbConnection connection, System.Data.IDbTransaction transaction,
        Guid galleryId, CancellationToken ct)
    {
        var row = connection.QuerySingleOrDefault<(Guid PersonalSpaceId, string Kind)>(new CommandDefinition("""
            SELECT personal_space_id AS PersonalSpaceId, gallery_kind AS Kind
              FROM view_galleries WHERE id = @galleryId;
            """, new { galleryId }, transaction, cancellationToken: ct));
        if (row.PersonalSpaceId == Guid.Empty)
        {
            throw new InvalidOperationException($"Gallery '{galleryId:D}' does not exist.");
        }

        if (row.Kind != "manual")
        {
            throw new InvalidOperationException("Smart Galleries derive membership from rules and cannot be edited manually.");
        }

        return row;
    }

    private static void RequireOwnedSpace(System.Data.IDbConnection connection, System.Data.IDbTransaction transaction,
        Guid spaceId, Guid ownerId, CancellationToken ct)
    {
        if (connection.ExecuteScalar<long>(new CommandDefinition("""
                SELECT COUNT(1) FROM view_personal_spaces
                 WHERE id = @spaceId AND owner_profile_id = @ownerId;
                """, new { spaceId, ownerId }, transaction, cancellationToken: ct)) == 0)
        {
            throw new InvalidOperationException("Gallery owner must own its Personal Space.");
        }
    }

    private static void ValidateCover(System.Data.IDbConnection connection, System.Data.IDbTransaction transaction,
        Guid spaceId, Guid? coverItemId, CancellationToken ct)
    {
        if (coverItemId.HasValue && connection.ExecuteScalar<long>(new CommandDefinition("""
                SELECT COUNT(1) FROM local_items WHERE id = @coverItemId AND personal_space_id = @spaceId;
                """, new { coverItemId, spaceId }, transaction, cancellationToken: ct)) == 0)
        {
            throw new InvalidOperationException("Gallery cover must belong to its owner's Personal Space.");
        }

        // An item in Trash is out of every Manual Gallery, so it cannot be chosen as a cover.
        if (coverItemId.HasValue && connection.ExecuteScalar<long>(new CommandDefinition("""
                SELECT COUNT(1) FROM local_items WHERE id = @coverItemId AND trashed_at IS NOT NULL;
                """, new { coverItemId }, transaction, cancellationToken: ct)) != 0)
        {
            throw new InvalidOperationException("An item in Trash cannot be a Gallery cover.");
        }
    }

    private static void ValidateSoundtrack(
        System.Data.IDbConnection connection,
        System.Data.IDbTransaction transaction,
        Guid ownerProfileId,
        Guid? playlistId,
        CancellationToken ct)
    {
        if (!playlistId.HasValue)
        {
            return;
        }

        var isAccessiblePlaylist = connection.ExecuteScalar<long>(new CommandDefinition("""
            SELECT COUNT(1)
              FROM collections
             WHERE id = @playlistId
               AND collection_type IN ('Playlist', 'Smart')
               AND is_enabled = 1
               AND (scope = 'library' OR profile_id = @ownerProfileId);
            """, new { playlistId, ownerProfileId }, transaction, cancellationToken: ct)) != 0;
        if (!isAccessiblePlaylist)
        {
            throw new ArgumentException("Gallery soundtrack must be an accessible, enabled playlist.");
        }
    }

    private static void Validate(CreateViewGalleryCommand command)
    {
        ArgumentNullException.ThrowIfNull(command); ValidateId(command.OwnerProfileId, nameof(command));
        ValidateId(command.PersonalSpaceId, nameof(command)); ArgumentException.ThrowIfNullOrWhiteSpace(command.Name);
        NormalizeRule(command.Kind, command.SmartRuleJson);
    }

    private static string? NormalizeRule(ViewGalleryKind kind, string? json)
    {
        if (kind == ViewGalleryKind.Manual)
        {
            if (!string.IsNullOrWhiteSpace(json))
            {
                throw new ArgumentException("Manual Galleries cannot define a smart rule.");
            }

            return null;
        }
        if (string.IsNullOrWhiteSpace(json))
        {
            throw new ArgumentException("Smart Galleries require a rule definition.");
        }

        return ViewSmartGalleryRules.Normalize(json);
    }

    public Task<bool> IsItemSharedWithProfileAsync(
        Guid itemId,
        Guid profileId,
        CancellationToken ct = default)
    {
        ValidateId(itemId, nameof(itemId));
        ValidateId(profileId, nameof(profileId));
        ct.ThrowIfCancellationRequested();
        using var connection = database.CreateConnection();
        if (connection.ExecuteScalar<long>(new CommandDefinition("""
                SELECT EXISTS (
                    SELECT 1
                      FROM view_gallery_items vgi
                      JOIN view_galleries vg ON vg.id = vgi.gallery_id AND vg.gallery_kind = 'manual'
                      JOIN view_gallery_shares vgs ON vgs.gallery_id = vg.id
                      JOIN local_items li ON li.id = vgi.item_id AND li.trashed_at IS NULL
                     WHERE vgi.item_id = @itemId AND vgs.profile_id = @profileId
                );
                """, new { itemId, profileId }, cancellationToken: ct)) != 0)
        {
            return Task.FromResult(true);
        }

        var smartGalleries = connection.Query<SharedSmartGalleryRow>(new CommandDefinition("""
            SELECT vg.personal_space_id AS PersonalSpaceId, vg.smart_rule_json AS SmartRuleJson
              FROM view_galleries vg
              JOIN view_gallery_shares vgs ON vgs.gallery_id = vg.id
             WHERE vgs.profile_id = @profileId
               AND vg.gallery_kind = 'smart'
               AND vg.smart_rule_json IS NOT NULL;
            """, new { profileId }, cancellationToken: ct));
        foreach (var gallery in smartGalleries)
        {
            LocalAssetSmartRuleSql compiled;
            try
            {
                compiled = LocalAssetSmartRuleSqlCompiler.Compile(
                    ViewSmartGalleryRules.Parse(gallery.SmartRuleJson));
            }
            catch (ArgumentException)
            {
                // Obsolete or corrupt rules fail closed and never grant asset access.
                continue;
            }

            var parameters = new DynamicParameters();
            parameters.Add("SharedItemId", GuidSql.ToBlob(itemId), System.Data.DbType.Binary);
            parameters.Add("SharedSpaceId", GuidSql.ToBlob(gallery.PersonalSpaceId), System.Data.DbType.Binary);
            parameters.AddDynamicParams(compiled.Parameters);
            if (connection.ExecuteScalar<long>(new CommandDefinition($$"""
                    SELECT EXISTS (
                        SELECT 1
                          FROM local_items li
                          LEFT JOIN local_item_metadata lm ON lm.item_id = li.id
                         WHERE li.id = @SharedItemId
                           AND li.personal_space_id = @SharedSpaceId
                           AND ({{compiled.Predicate}})
                    );
                    """, parameters, cancellationToken: ct)) != 0)
            {
                return Task.FromResult(true);
            }
        }
        return Task.FromResult(false);
    }

    public Task<IReadOnlySet<Guid>> GetItemsSharedWithProfileAsync(
        IReadOnlyCollection<Guid> itemIds,
        Guid profileId,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(itemIds);
        ValidateId(profileId, nameof(profileId));
        var remaining = itemIds.Where(id => id != Guid.Empty).Distinct().ToList();
        var shared = new HashSet<Guid>();
        if (remaining.Count == 0)
        {
            return Task.FromResult<IReadOnlySet<Guid>>(shared);
        }

        using var connection = database.CreateConnection();
        // Exact shares of a manual Gallery.
        foreach (var chunk in remaining.Chunk(BulkChunkSize))
        {
            ct.ThrowIfCancellationRequested();
            shared.UnionWith(connection.Query<Guid>(new CommandDefinition("""
                SELECT DISTINCT vgi.item_id
                  FROM view_gallery_items vgi
                  JOIN view_galleries vg ON vg.id = vgi.gallery_id AND vg.gallery_kind = 'manual'
                  JOIN view_gallery_shares vgs ON vgs.gallery_id = vg.id
                  JOIN local_items li ON li.id = vgi.item_id AND li.trashed_at IS NULL
                 WHERE vgs.profile_id = @profileId AND vgi.item_id IN @itemIds;
                """, new { profileId, itemIds = chunk.Select(GuidSql.ToBlob).ToArray() }, cancellationToken: ct)));
        }

        remaining.RemoveAll(shared.Contains);
        if (remaining.Count == 0)
        {
            return Task.FromResult<IReadOnlySet<Guid>>(shared);
        }

        // Dynamic membership in a shared Smart Gallery: one query per Gallery and chunk, the same rule the single-item
        // check applies. The ids are written as BLOB literals (they are Guids, so there is nothing to escape).
        var smartGalleries = connection.Query<SharedSmartGalleryRow>(new CommandDefinition("""
            SELECT vg.personal_space_id AS PersonalSpaceId, vg.smart_rule_json AS SmartRuleJson
              FROM view_galleries vg
              JOIN view_gallery_shares vgs ON vgs.gallery_id = vg.id
             WHERE vgs.profile_id = @profileId
               AND vg.gallery_kind = 'smart'
               AND vg.smart_rule_json IS NOT NULL;
            """, new { profileId }, cancellationToken: ct)).ToList();
        foreach (var gallery in smartGalleries)
        {
            LocalAssetSmartRuleSql compiled;
            try
            {
                compiled = LocalAssetSmartRuleSqlCompiler.Compile(
                    ViewSmartGalleryRules.Parse(gallery.SmartRuleJson));
            }
            catch (ArgumentException)
            {
                // Obsolete or corrupt rules fail closed and never grant asset access.
                continue;
            }

            foreach (var chunk in remaining.Where(id => !shared.Contains(id)).Chunk(BulkChunkSize))
            {
                ct.ThrowIfCancellationRequested();
                var literals = string.Join(",", chunk.Select(id => $"X'{Convert.ToHexString(GuidSql.ToBlob(id))}'"));
                var parameters = new DynamicParameters();
                parameters.Add("SharedSpaceId", GuidSql.ToBlob(gallery.PersonalSpaceId), System.Data.DbType.Binary);
                parameters.AddDynamicParams(compiled.Parameters);
                shared.UnionWith(connection.Query<Guid>(new CommandDefinition($$"""
                    SELECT li.id
                      FROM local_items li
                      LEFT JOIN local_item_metadata lm ON lm.item_id = li.id
                     WHERE li.id IN ({{literals}})
                       AND li.personal_space_id = @SharedSpaceId
                       AND ({{compiled.Predicate}});
                    """, parameters, cancellationToken: ct)));
            }
        }

        return Task.FromResult<IReadOnlySet<Guid>>(shared);
    }

    private const int BulkChunkSize = 500;

    private static ViewGallery Map(GalleryRow row) => new(row.Id, row.OwnerProfileId, row.PersonalSpaceId,
        row.Name, row.Description, row.GalleryKind == "manual" ? ViewGalleryKind.Manual : ViewGalleryKind.Smart,
        row.SmartRuleJson, row.CoverItemId, row.SortOrder, row.ItemCount,
        DateTimeOffset.Parse(row.CreatedAt), DateTimeOffset.Parse(row.UpdatedAt), row.SoundtrackPlaylistId);

    private static ViewGallery MapWithDynamicCount(
        System.Data.IDbConnection connection,
        GalleryRow row,
        bool includeDocuments,
        IReadOnlyCollection<string>? blockedKinds,
        CancellationToken ct)
    {
        var gallery = Map(row);
        if (gallery.Kind != ViewGalleryKind.Smart || string.IsNullOrWhiteSpace(gallery.SmartRuleJson))
        {
            return gallery;
        }

        try
        {
            var compiled = LocalAssetSmartRuleSqlCompiler.Compile(
                ViewSmartGalleryRules.Parse(gallery.SmartRuleJson));
            var parameters = new DynamicParameters();
            parameters.Add("SmartSpaceId", GuidSql.ToBlob(gallery.PersonalSpaceId), System.Data.DbType.Binary);
            parameters.Add("SmartIncludeDocuments", includeDocuments ? 1 : 0);
            parameters.Add("SmartHasBlockedKinds", blockedKinds is { Count: > 0 } ? 1 : 0);
            parameters.Add("SmartBlockedKinds", blockedKinds is { Count: > 0 } ? blockedKinds.ToArray() : new[] { "photos" });
            parameters.AddDynamicParams(compiled.Parameters);
            var count = connection.ExecuteScalar<int>(new CommandDefinition($$"""
                SELECT COUNT(1)
                  FROM local_items li
                  LEFT JOIN local_item_metadata lm ON lm.item_id = li.id
                 WHERE li.personal_space_id = @SmartSpaceId
                   AND li.hidden = 0
                   AND li.archived_at IS NULL
                   AND li.trashed_at IS NULL
                   AND (@SmartIncludeDocuments = 1 OR li.media_kind <> 'document')
                   AND (@SmartHasBlockedKinds = 0 OR li.content_kind IS NULL OR li.content_kind NOT IN @SmartBlockedKinds)
                   AND ({{compiled.Predicate}});
                """, parameters, cancellationToken: ct));
            return gallery with { ItemCount = count };
        }
        catch (ArgumentException)
        {
            // Corrupt or obsolete rules fail closed. The Gallery remains visible
            // to its owner for repair without exposing an incorrect count.
            return gallery with { ItemCount = 0 };
        }
    }
    private static string ToStorage(ViewGalleryKind kind) => kind == ViewGalleryKind.Manual ? "manual" : "smart";
    private static string ToStorage(ViewGallerySharePermission permission) =>
        permission == ViewGallerySharePermission.View ? "view" : "contribute";
    private static ViewGallerySharePermission ParsePermission(string value) =>
        value == "view" ? ViewGallerySharePermission.View : ViewGallerySharePermission.Contribute;
    private static string? NullIfWhiteSpace(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    private static void ValidateId(Guid id, string parameterName)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("ID is required.", parameterName);
        }
    }

    private sealed class GalleryRow
    {
        public Guid Id { get; init; }
        public Guid OwnerProfileId { get; init; }
        public Guid PersonalSpaceId { get; init; }
        public string Name { get; init; } = string.Empty; public string? Description { get; init; }
        public string GalleryKind { get; init; } = string.Empty; public string? SmartRuleJson { get; init; }
        public Guid? CoverItemId { get; init; }
        public Guid? SoundtrackPlaylistId { get; init; }
        public int SortOrder { get; init; }
        public int ItemCount { get; init; }
        public string CreatedAt { get; init; } = string.Empty; public string UpdatedAt { get; init; } = string.Empty;
    }
    private sealed class GalleryItemRow
    { public Guid GalleryId { get; init; } public Guid ItemId { get; init; } public int Position { get; init; } public string AddedAt { get; init; } = string.Empty; }
    private sealed class ShareRow
    { public Guid GalleryId { get; init; } public Guid ProfileId { get; init; } public string Permission { get; init; } = string.Empty; public string SharedAt { get; init; } = string.Empty; }
    private sealed class SharedSmartGalleryRow
    { public Guid PersonalSpaceId { get; init; } public string SmartRuleJson { get; init; } = string.Empty; }
}
