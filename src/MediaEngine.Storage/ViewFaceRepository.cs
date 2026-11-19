using Dapper;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.PersonalMedia;
using MediaEngine.Storage.Contracts;
using Microsoft.Data.Sqlite;

namespace MediaEngine.Storage;

public sealed class ViewFaceRepository(IDatabaseConnection database) : IViewFaceRepository
{
    public Task<IReadOnlyList<ViewFaceScanCandidate>> GetUnscannedPhotosAsync(
        Guid ownerProfileId, Guid libraryId, Guid? afterItemId, int limit, IReadOnlyCollection<string> imageExtensions, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var extensions = imageExtensions.Select(extension => extension.TrimStart('.').ToLowerInvariant()).Distinct().ToArray();
        if (extensions.Length == 0)
        {
            return Task.FromResult<IReadOnlyList<ViewFaceScanCandidate>>([]);
        }

        var parameters = new DynamicParameters();
        parameters.Add("owner", ownerProfileId);
        parameters.Add("library", libraryId);
        parameters.Add("after", afterItemId);
        parameters.Add("limit", Math.Clamp(limit, 1, 500));
        var matches = new List<string>();
        for (var i = 0; i < extensions.Length; i++)
        {
            parameters.Add($"ext{i}", "%." + extensions[i]);
            matches.Add($"lower(li.primary_file_name) LIKE @ext{i}");
        }

        using var connection = database.CreateConnection();
        var rows = connection.Query<(Guid ItemId, string FilePath)>($"""
            SELECT li.id AS ItemId, MIN(lfs.file_path) AS FilePath
              FROM local_items li
              JOIN local_item_files lif ON lif.item_id = li.id AND lif.role = 'primary'
              JOIN local_files lf ON lf.id = lif.file_id
              JOIN local_file_sources lfs ON lfs.file_id = lf.id AND lfs.library_id = li.library_id
             WHERE li.media_kind = 'image'
               AND li.library_id = @library
               AND li.scope_kind = 'personal'
               AND li.owner_profile_id = @owner
               AND li.trashed_at IS NULL
               AND li.missing_since IS NULL
               AND NOT EXISTS (SELECT 1 FROM vault_image_items vx WHERE vx.item_id = li.id)
               AND NOT EXISTS (SELECT 1 FROM view_face_scans s WHERE s.item_id = li.id)
               AND (@after IS NULL OR li.id > @after)
               AND ({string.Join(" OR ", matches)})
             GROUP BY li.id
             ORDER BY li.id
             LIMIT @limit;
            """, parameters).ToList();
        return Task.FromResult<IReadOnlyList<ViewFaceScanCandidate>>(rows.Select(row => new ViewFaceScanCandidate(row.ItemId, row.FilePath)).ToList());
    }

    public Task<IReadOnlyList<ViewFaceLook>> GetAssignedLooksAsync(Guid ownerProfileId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var connection = database.CreateConnection();
        var rows = connection.Query<(Guid Id, Guid PersonId, byte[] Embedding, long Confirmed)>(
            "SELECT id AS Id, person_id AS PersonId, embedding AS Embedding, confirmed AS Confirmed FROM view_faces WHERE owner_profile_id = @ownerProfileId AND person_id IS NOT NULL;",
            new { ownerProfileId }).ToList();
        return Task.FromResult<IReadOnlyList<ViewFaceLook>>(
            rows.Select(row => new ViewFaceLook(row.Id, row.PersonId, ToFloats(row.Embedding), row.Confirmed != 0)).ToList());
    }

    public Task SaveScanAsync(
        Guid ownerProfileId, Guid itemId, string model, IReadOnlyList<ViewFaceInsert> faces, IReadOnlyCollection<Guid> newPeople, CancellationToken ct = default) =>
        database.ExecuteWriteAsync((connection, transaction, token) =>
        {
            token.ThrowIfCancellationRequested();
            var now = DateTimeOffset.UtcNow.ToString("O", System.Globalization.CultureInfo.InvariantCulture);
            foreach (var personId in newPeople)
            {
                connection.Execute(new CommandDefinition(
                    "INSERT INTO view_people (id, owner_profile_id, created_at, updated_at) VALUES (@personId, @ownerProfileId, @now, @now);",
                    new { personId, ownerProfileId, now }, transaction, cancellationToken: token));
            }

            foreach (var face in faces)
            {
                connection.Execute(new CommandDefinition("""
                    INSERT INTO view_faces (id, item_id, owner_profile_id, person_id, box_x, box_y, box_width, box_height, quality, embedding, created_at)
                    VALUES (@FaceId, @itemId, @ownerProfileId, @PersonId, @X, @Y, @Width, @Height, @Quality, @embedding, @now);
                    """,
                    new
                    {
                        face.FaceId,
                        itemId,
                        ownerProfileId,
                        face.PersonId,
                        face.Face.X,
                        face.Face.Y,
                        face.Face.Width,
                        face.Face.Height,
                        face.Face.Quality,
                        embedding = ToBytes(face.Face.Embedding),
                        now,
                    }, transaction, cancellationToken: token));
                // The first face of a new group is its cover until someone picks another.
                connection.Execute(new CommandDefinition(
                    "UPDATE view_people SET cover_face_id = @FaceId WHERE id = @PersonId AND cover_face_id IS NULL;",
                    new { face.FaceId, face.PersonId }, transaction, cancellationToken: token));
            }

            SyncAnnotations(connection, transaction, ownerProfileId, [itemId]);
            connection.Execute(new CommandDefinition("""
                INSERT INTO view_face_scans (item_id, owner_profile_id, scanned_at, face_count, model)
                VALUES (@itemId, @ownerProfileId, @now, @count, @model)
                ON CONFLICT(item_id) DO UPDATE SET scanned_at = excluded.scanned_at, face_count = excluded.face_count, model = excluded.model;
                """, new { itemId, ownerProfileId, now, count = faces.Count, model }, transaction, cancellationToken: token));
            return true;
        }, ct);

    public Task<ViewFaceScanProgress> GetProgressAsync(Guid ownerProfileId, Guid libraryId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var connection = database.CreateConnection();
        var looked = connection.ExecuteScalar<int>("SELECT COUNT(1) FROM view_face_scans WHERE owner_profile_id = @ownerProfileId;", new { ownerProfileId });
        var total = connection.ExecuteScalar<int>("""
            SELECT COUNT(1) FROM local_items li
             WHERE li.media_kind = 'image' AND li.library_id = @libraryId AND li.scope_kind = 'personal' AND li.owner_profile_id = @ownerProfileId
               AND li.trashed_at IS NULL AND li.missing_since IS NULL
               AND NOT EXISTS (SELECT 1 FROM vault_image_items vx WHERE vx.item_id = li.id);
            """, new { ownerProfileId, libraryId });
        var faces = connection.ExecuteScalar<int>("SELECT COUNT(1) FROM view_faces WHERE owner_profile_id = @ownerProfileId;", new { ownerProfileId });
        var people = connection.ExecuteScalar<int>("SELECT COUNT(1) FROM view_people WHERE owner_profile_id = @ownerProfileId AND name IS NOT NULL;", new { ownerProfileId });
        var groups = connection.ExecuteScalar<int>("SELECT COUNT(1) FROM view_people WHERE owner_profile_id = @ownerProfileId AND name IS NULL;", new { ownerProfileId });
        return Task.FromResult(new ViewFaceScanProgress(Math.Min(looked, Math.Max(total, looked)), Math.Max(total, looked), faces, people, groups));
    }

    public Task<int> DeleteAllAsync(Guid ownerProfileId, CancellationToken ct = default) =>
        database.ExecuteWriteAsync((connection, transaction, token) =>
        {
            token.ThrowIfCancellationRequested();
            var faces = connection.ExecuteScalar<int>(new CommandDefinition(
                "SELECT COUNT(1) FROM view_faces WHERE owner_profile_id = @ownerProfileId;", new { ownerProfileId }, transaction, cancellationToken: token));
            // Names Library wrote into photos' people lists go too (the file's own and typed names stay).
            var named = connection.Query<Guid>(
                "SELECT DISTINCT item_id FROM view_faces WHERE owner_profile_id = @ownerProfileId;", new { ownerProfileId }, transaction).ToList();
            foreach (var table in new[] { "view_faces", "view_people", "view_face_scans" })
            {
                connection.Execute(new CommandDefinition(
                    $"DELETE FROM {table} WHERE owner_profile_id = @ownerProfileId;", new { ownerProfileId }, transaction, cancellationToken: token));
            }

            SyncAnnotations(connection, transaction, ownerProfileId, named);
            return faces;
        }, ct);

    private const string LivePhoto = """
        EXISTS (SELECT 1 FROM local_items li
                 WHERE li.id = f.item_id AND li.trashed_at IS NULL AND li.missing_since IS NULL
                   AND NOT EXISTS (SELECT 1 FROM vault_image_items vx WHERE vx.item_id = li.id))
        """;

    public Task<IReadOnlyList<ViewFaceGroup>> GetGroupsAsync(Guid ownerProfileId, bool? named, bool includeHidden, int limit, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var connection = database.CreateConnection();
        var rows = connection.Query<(Guid Id, string? Name, long Hidden, Guid? Cover, int Faces, int Photos)>($"""
            SELECT p.id AS Id, p.name AS Name, p.hidden AS Hidden, p.cover_face_id AS Cover,
                   COUNT(f.id) AS Faces, COUNT(DISTINCT f.item_id) AS Photos
              FROM view_people p
              JOIN view_faces f ON f.person_id = p.id AND {LivePhoto}
             WHERE p.owner_profile_id = @ownerProfileId
               AND (@named IS NULL OR (@named = 1 AND p.name IS NOT NULL) OR (@named = 0 AND p.name IS NULL))
               AND (@includeHidden = 1 OR p.hidden = 0)
             GROUP BY p.id
             ORDER BY Faces DESC, p.created_at
             LIMIT @limit;
            """, new { ownerProfileId, named = named is null ? (int?)null : (named.Value ? 1 : 0), includeHidden = includeHidden ? 1 : 0, limit = Math.Clamp(limit, 1, 500) }).ToList();
        return Task.FromResult<IReadOnlyList<ViewFaceGroup>>(rows.Select(row => ToGroup(connection, row.Id, row.Name, row.Hidden != 0, row.Cover, row.Faces, row.Photos)).ToList());
    }

    public Task<ViewFaceGroup?> GetGroupAsync(Guid ownerProfileId, Guid personId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var connection = database.CreateConnection();
        var row = connection.QuerySingleOrDefault<(Guid Id, string? Name, long Hidden, Guid? Cover, int Faces, int Photos)?>($"""
            SELECT p.id AS Id, p.name AS Name, p.hidden AS Hidden, p.cover_face_id AS Cover,
                   COUNT(f.id) AS Faces, COUNT(DISTINCT f.item_id) AS Photos
              FROM view_people p
              LEFT JOIN view_faces f ON f.person_id = p.id AND {LivePhoto}
             WHERE p.owner_profile_id = @ownerProfileId AND p.id = @personId
             GROUP BY p.id;
            """, new { ownerProfileId, personId });
        return Task.FromResult(row is { } value ? ToGroup(connection, value.Id, value.Name, value.Hidden != 0, value.Cover, value.Faces, value.Photos) : null);
    }

    private static ViewFaceGroup ToGroup(System.Data.IDbConnection connection, Guid id, string? name, bool hidden, Guid? cover, int faces, int photos)
    {
        var samples = connection.Query<Guid>(
            "SELECT id FROM view_faces WHERE person_id = @id ORDER BY quality DESC LIMIT 6;", new { id }).ToList();
        return new ViewFaceGroup(id, name, hidden, faces, photos, cover ?? samples.FirstOrDefault(), samples);
    }

    public Task<IReadOnlyList<ViewFaceBox>> GetFacesOfPersonAsync(Guid ownerProfileId, Guid personId, int limit, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var connection = database.CreateConnection();
        var rows = connection.Query<FaceRow>($"""
            SELECT f.id AS FaceId, f.item_id AS ItemId, f.person_id AS PersonId, p.name AS PersonName,
                   f.box_x AS X, f.box_y AS Y, f.box_width AS Width, f.box_height AS Height
              FROM view_faces f LEFT JOIN view_people p ON p.id = f.person_id
             WHERE f.owner_profile_id = @ownerProfileId AND f.person_id = @personId AND {LivePhoto}
             ORDER BY f.quality DESC LIMIT @limit;
            """, new { ownerProfileId, personId, limit = Math.Clamp(limit, 1, 500) }).ToList();
        return Task.FromResult<IReadOnlyList<ViewFaceBox>>(rows.Select(ToBox).ToList());
    }

    public Task<IReadOnlyList<ViewFaceBox>> GetFacesOfItemAsync(Guid ownerProfileId, Guid itemId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var connection = database.CreateConnection();
        var rows = connection.Query<FaceRow>($"""
            SELECT f.id AS FaceId, f.item_id AS ItemId, f.person_id AS PersonId, p.name AS PersonName,
                   f.box_x AS X, f.box_y AS Y, f.box_width AS Width, f.box_height AS Height
              FROM view_faces f LEFT JOIN view_people p ON p.id = f.person_id
             WHERE f.owner_profile_id = @ownerProfileId AND f.item_id = @itemId AND {LivePhoto}
             ORDER BY f.box_x;
            """, new { ownerProfileId, itemId }).ToList();
        return Task.FromResult<IReadOnlyList<ViewFaceBox>>(rows.Select(ToBox).ToList());
    }

    public Task<ViewFaceBox?> GetFaceAsync(Guid ownerProfileId, Guid faceId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var connection = database.CreateConnection();
        var row = connection.QuerySingleOrDefault<FaceRow>($"""
            SELECT f.id AS FaceId, f.item_id AS ItemId, f.person_id AS PersonId, p.name AS PersonName,
                   f.box_x AS X, f.box_y AS Y, f.box_width AS Width, f.box_height AS Height
              FROM view_faces f LEFT JOIN view_people p ON p.id = f.person_id
             WHERE f.owner_profile_id = @ownerProfileId AND f.id = @faceId AND {LivePhoto};
            """, new { ownerProfileId, faceId });
        return Task.FromResult(row is null ? null : ToBox(row));
    }

    public Task<ISet<Guid>> GetRejectedPeopleAsync(Guid ownerProfileId, IReadOnlyCollection<Guid> faceIds, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (faceIds.Count == 0)
        {
            return Task.FromResult<ISet<Guid>>(new HashSet<Guid>());
        }

        using var connection = database.CreateConnection();
        var rows = connection.Query<Guid>(
            "SELECT DISTINCT person_id FROM view_face_rejections WHERE owner_profile_id = @ownerProfileId AND face_id IN @ids;",
            new { ownerProfileId, ids = faceIds.Select(GuidSql.ToBlob).ToArray() });
        return Task.FromResult<ISet<Guid>>(rows.ToHashSet());
    }

    public Task<ViewNamedPerson?> NamePersonAsync(Guid ownerProfileId, Guid personId, string name, CancellationToken ct = default)
    {
        var trimmed = (name ?? string.Empty).Trim();
        if (trimmed.Length is 0 or > 120)
        {
            throw new ArgumentException("A name is 1 to 120 characters.", nameof(name));
        }

        return database.ExecuteWriteAsync<ViewNamedPerson?>((connection, transaction, token) =>
        {
            token.ThrowIfCancellationRequested();
            if (!Owns(connection, transaction, ownerProfileId, personId))
            {
                return null;
            }

            var existing = connection.QueryFirstOrDefault<Guid?>(
                "SELECT id FROM view_people WHERE owner_profile_id = @ownerProfileId AND id <> @personId AND name IS NOT NULL AND lower(name) = lower(@trimmed) LIMIT 1;",
                new { ownerProfileId, personId, trimmed }, transaction);
            if (existing is { } target)
            {
                MergeCore(connection, transaction, ownerProfileId, personId, target);
                return new ViewNamedPerson(target, true);
            }

            connection.Execute(
                "UPDATE view_people SET name = @trimmed, updated_at = @now WHERE id = @personId;",
                new { personId, trimmed, now = Now() }, transaction);
            SyncAnnotations(connection, transaction, ownerProfileId, ItemsOf(connection, transaction, personId));
            return new ViewNamedPerson(personId, false);
        }, ct);
    }

    public Task<bool> MergeAsync(Guid ownerProfileId, Guid fromPersonId, Guid intoPersonId, CancellationToken ct = default) =>
        database.ExecuteWriteAsync((connection, transaction, token) =>
        {
            token.ThrowIfCancellationRequested();
            if (fromPersonId == intoPersonId
                || !Owns(connection, transaction, ownerProfileId, fromPersonId)
                || !Owns(connection, transaction, ownerProfileId, intoPersonId))
            {
                return false;
            }

            MergeCore(connection, transaction, ownerProfileId, fromPersonId, intoPersonId);
            return true;
        }, ct);

    public Task<bool> ConfirmFacesAsync(
        Guid ownerProfileId, Guid personId, IReadOnlyCollection<Guid> acceptedFaceIds, IReadOnlyCollection<Guid> rejectedFaceIds, CancellationToken ct = default) =>
        database.ExecuteWriteAsync((connection, transaction, token) =>
        {
            token.ThrowIfCancellationRequested();
            if (!Owns(connection, transaction, ownerProfileId, personId))
            {
                return false;
            }

            var affected = new HashSet<Guid>(ItemsOf(connection, transaction, personId));
            foreach (var faceId in acceptedFaceIds)
            {
                var item = connection.QueryFirstOrDefault<Guid?>(
                    "SELECT item_id FROM view_faces WHERE id = @faceId AND owner_profile_id = @ownerProfileId;", new { faceId, ownerProfileId }, transaction);
                if (item is null)
                {
                    continue;
                }

                affected.Add(item.Value);
                connection.Execute(
                    "UPDATE view_faces SET person_id = @personId, confirmed = 1 WHERE id = @faceId;", new { personId, faceId }, transaction);
                connection.Execute(
                    "DELETE FROM view_face_rejections WHERE face_id = @faceId AND person_id = @personId;", new { personId, faceId }, transaction);
            }

            foreach (var faceId in rejectedFaceIds)
            {
                InsertRejection(connection, transaction, ownerProfileId, faceId, personId);
            }

            RemoveEmptyPeople(connection, transaction, ownerProfileId);
            SyncAnnotations(connection, transaction, ownerProfileId, affected);
            return true;
        }, ct);

    public Task<bool> RejectFacesAsync(Guid ownerProfileId, Guid personId, IReadOnlyCollection<Guid> faceIds, CancellationToken ct = default) =>
        database.ExecuteWriteAsync((connection, transaction, token) =>
        {
            token.ThrowIfCancellationRequested();
            if (!Owns(connection, transaction, ownerProfileId, personId))
            {
                return false;
            }

            foreach (var faceId in faceIds)
            {
                InsertRejection(connection, transaction, ownerProfileId, faceId, personId);
            }

            return true;
        }, ct);

    public Task<bool> SetHiddenAsync(Guid ownerProfileId, Guid personId, bool hidden, CancellationToken ct = default) =>
        database.ExecuteWriteAsync((connection, transaction, token) =>
        {
            token.ThrowIfCancellationRequested();
            if (!Owns(connection, transaction, ownerProfileId, personId))
            {
                return false;
            }

            connection.Execute(
                "UPDATE view_people SET hidden = @value, updated_at = @now WHERE id = @personId;",
                new { personId, value = hidden ? 1 : 0, now = Now() }, transaction);
            // A hidden person is not offered by search either.
            SyncAnnotations(connection, transaction, ownerProfileId, ItemsOf(connection, transaction, personId));
            return true;
        }, ct);

    public Task<bool> SetCoverAsync(Guid ownerProfileId, Guid personId, Guid faceId, CancellationToken ct = default) =>
        database.ExecuteWriteAsync((connection, transaction, token) =>
        {
            token.ThrowIfCancellationRequested();
            var belongs = connection.ExecuteScalar<long>(
                "SELECT COUNT(1) FROM view_faces WHERE id = @faceId AND person_id = @personId AND owner_profile_id = @ownerProfileId;",
                new { faceId, personId, ownerProfileId }, transaction) != 0;
            if (!belongs)
            {
                return false;
            }

            connection.Execute("UPDATE view_people SET cover_face_id = @faceId, updated_at = @now WHERE id = @personId;",
                new { faceId, personId, now = Now() }, transaction);
            return true;
        }, ct);

    public Task<bool> RemoveFaceAsync(Guid ownerProfileId, Guid faceId, CancellationToken ct = default) =>
        database.ExecuteWriteAsync((connection, transaction, token) =>
        {
            token.ThrowIfCancellationRequested();
            var row = connection.QueryFirstOrDefault<(Guid ItemId, Guid? PersonId)?>(
                "SELECT item_id AS ItemId, person_id AS PersonId FROM view_faces WHERE id = @faceId AND owner_profile_id = @ownerProfileId;",
                new { faceId, ownerProfileId }, transaction);
            if (row is not { } face)
            {
                return false;
            }

            connection.Execute("""
                UPDATE view_people
                   SET cover_face_id = (SELECT f.id FROM view_faces f WHERE f.person_id = view_people.id AND f.id <> @faceId ORDER BY f.quality DESC LIMIT 1)
                 WHERE cover_face_id = @faceId;
                """, new { faceId }, transaction);
            connection.Execute("DELETE FROM view_faces WHERE id = @faceId;", new { faceId }, transaction);
            RemoveEmptyPeople(connection, transaction, ownerProfileId);
            SyncAnnotations(connection, transaction, ownerProfileId, [face.ItemId]);
            return true;
        }, ct);

    public Task<bool> DeletePersonAsync(Guid ownerProfileId, Guid personId, CancellationToken ct = default) =>
        database.ExecuteWriteAsync((connection, transaction, token) =>
        {
            token.ThrowIfCancellationRequested();
            if (!Owns(connection, transaction, ownerProfileId, personId))
            {
                return false;
            }

            var items = ItemsOf(connection, transaction, personId);
            connection.Execute("DELETE FROM view_faces WHERE person_id = @personId;", new { personId }, transaction);
            connection.Execute("DELETE FROM view_people WHERE id = @personId;", new { personId }, transaction);
            SyncAnnotations(connection, transaction, ownerProfileId, items);
            return true;
        }, ct);

    private static string Now() => DateTimeOffset.UtcNow.ToString("O", System.Globalization.CultureInfo.InvariantCulture);

    private static bool Owns(SqliteConnection connection, SqliteTransaction transaction, Guid ownerProfileId, Guid personId) =>
        connection.ExecuteScalar<long>(
            "SELECT COUNT(1) FROM view_people WHERE id = @personId AND owner_profile_id = @ownerProfileId;",
            new { personId, ownerProfileId }, transaction) != 0;

    private static List<Guid> ItemsOf(SqliteConnection connection, SqliteTransaction transaction, Guid personId) =>
        connection.Query<Guid>("SELECT DISTINCT item_id FROM view_faces WHERE person_id = @personId;", new { personId }, transaction).ToList();

    private static void InsertRejection(SqliteConnection connection, SqliteTransaction transaction, Guid ownerProfileId, Guid faceId, Guid personId) =>
        connection.Execute("""
            INSERT OR IGNORE INTO view_face_rejections (face_id, person_id, owner_profile_id)
            SELECT @faceId, @personId, @ownerProfileId
             WHERE EXISTS (SELECT 1 FROM view_faces WHERE id = @faceId AND owner_profile_id = @ownerProfileId);
            """, new { faceId, personId, ownerProfileId }, transaction);

    private static void RemoveEmptyPeople(SqliteConnection connection, SqliteTransaction transaction, Guid ownerProfileId) =>
        connection.Execute("""
            DELETE FROM view_people
             WHERE owner_profile_id = @ownerProfileId
               AND NOT EXISTS (SELECT 1 FROM view_faces f WHERE f.person_id = view_people.id);
            """, new { ownerProfileId }, transaction);

    private static void MergeCore(SqliteConnection connection, SqliteTransaction transaction, Guid ownerProfileId, Guid fromId, Guid intoId)
    {
        var affected = new HashSet<Guid>(ItemsOf(connection, transaction, fromId));
        affected.UnionWith(ItemsOf(connection, transaction, intoId));
        connection.Execute("UPDATE view_faces SET person_id = @intoId WHERE person_id = @fromId;", new { fromId, intoId }, transaction);
        connection.Execute("DELETE FROM view_face_rejections WHERE person_id = @intoId AND face_id IN (SELECT id FROM view_faces WHERE person_id = @intoId);", new { intoId }, transaction);
        connection.Execute("""
            UPDATE view_people
               SET name = COALESCE(name, (SELECT name FROM view_people WHERE id = @fromId)),
                   cover_face_id = COALESCE(cover_face_id, (SELECT cover_face_id FROM view_people WHERE id = @fromId)),
                   updated_at = @now
             WHERE id = @intoId;
            """, new { fromId, intoId, now = Now() }, transaction);
        connection.Execute("DELETE FROM view_people WHERE id = @fromId;", new { fromId }, transaction);
        SyncAnnotations(connection, transaction, ownerProfileId, affected);
    }

    /// <summary>
    /// Puts the names of the people found in each photo into the photo's own people list, so the existing People page, search and the
    /// search bar's People suggestions all see them without a second path. Only the names Library itself wrote are replaced; names from the
    /// file or typed by the person are never touched. A hidden person is not written.
    /// </summary>
    private static void SyncAnnotations(SqliteConnection connection, SqliteTransaction transaction, Guid ownerProfileId, IEnumerable<Guid> itemIds)
    {
        var now = Now();
        foreach (var itemId in itemIds.Distinct())
        {
            connection.Execute(
                "DELETE FROM local_item_annotations WHERE item_id = @itemId AND annotation_kind = 'face_name' AND source = 'faces';",
                new { itemId }, transaction);
            var names = connection.Query<string>("""
                SELECT DISTINCT p.name FROM view_faces f JOIN view_people p ON p.id = f.person_id
                 WHERE f.item_id = @itemId AND f.owner_profile_id = @ownerProfileId AND p.name IS NOT NULL AND p.hidden = 0;
                """, new { itemId, ownerProfileId }, transaction).ToList();
            foreach (var name in names)
            {
                connection.Execute("""
                    INSERT INTO local_item_annotations (id, item_id, annotation_kind, annotation_value, source, created_at, reviewed_at)
                    VALUES (@id, @itemId, 'face_name', @name, 'faces', @now, @now);
                    """, new { id = Guid.NewGuid(), itemId, name, now }, transaction);
            }

            LocalAssetRepository.RebuildSearchDocument(connection, transaction, itemId);
        }
    }

    private static ViewFaceBox ToBox(FaceRow row) =>
        new(row.FaceId, row.ItemId, row.PersonId, row.PersonName, row.X, row.Y, row.Width, row.Height);

    private sealed class FaceRow
    {
        public Guid FaceId { get; init; }
        public Guid ItemId { get; init; }
        public Guid? PersonId { get; init; }
        public string? PersonName { get; init; }
        public double X { get; init; }
        public double Y { get; init; }
        public double Width { get; init; }
        public double Height { get; init; }
    }

    internal static byte[] ToBytes(float[] values)
    {
        var bytes = new byte[values.Length * sizeof(float)];
        Buffer.BlockCopy(values, 0, bytes, 0, bytes.Length);
        return bytes;
    }

    internal static float[] ToFloats(byte[] bytes)
    {
        var values = new float[bytes.Length / sizeof(float)];
        Buffer.BlockCopy(bytes, 0, values, 0, values.Length * sizeof(float));
        return values;
    }
}
