using Dapper;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.PersonalMedia;
using MediaEngine.Storage.Contracts;

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
            foreach (var table in new[] { "view_faces", "view_people", "view_face_scans" })
            {
                connection.Execute(new CommandDefinition(
                    $"DELETE FROM {table} WHERE owner_profile_id = @ownerProfileId;", new { ownerProfileId }, transaction, cancellationToken: token));
            }

            return faces;
        }, ct);

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
