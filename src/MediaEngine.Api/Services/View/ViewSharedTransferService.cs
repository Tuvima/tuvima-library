using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Dapper;
using MediaEngine.Api.Services.LocalAssets;
using MediaEngine.Contracts.LocalAssets;
using MediaEngine.Storage.Contracts;

namespace MediaEngine.Api.Services.View;

public sealed class ViewSharedTransferService(
    IDatabaseConnection database,
    ILocalAssetRepository assets,
    ViewStorageService storage)
{
    private static readonly SemaphoreSlim TransferGate = new(1, 1);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public ViewSharedTransferPreviewDto Preview(Guid itemId, string destinationKind, string? folderName,
        CancellationToken ct = default)
    {
        var item = assets.Find(itemId, ct) ?? throw new KeyNotFoundException("The View item was not found.");
        var files = GetFiles(itemId, ct);
        if (files.Count == 0)
        {
            throw new InvalidOperationException("The item has no available original files.");
        }

        var kind = NormalizeDestinationKind(destinationKind);
        if (kind == "folder")
        {
            _ = SanitizeFolderName(folderName);
        }

        var move = files.All(file => string.Equals(file.StorageMode, "managed", StringComparison.Ordinal));
        using var connection = database.CreateConnection();
        var promoted = connection.ExecuteScalar<int>(new CommandDefinition(
            "SELECT COUNT(*) FROM view_shared_assets WHERE item_id = @itemId OR origin_item_id = @itemId;",
            new { itemId }, cancellationToken: ct)) > 0;
        return new ViewSharedTransferPreviewDto(itemId, move ? "move" : "copy", files.Count,
            files.Sum(file => file.ByteSize), DestinationRoot(item, kind, folderName), kind, move, promoted);
    }

    public async Task<ViewSharedTransferResultDto> ExecuteAsync(Guid itemId, Guid actorProfileId,
        string destinationKind, string? folderName, Guid? contributionItemId = null,
        CancellationToken ct = default)
    {
        await TransferGate.WaitAsync(ct);
        try
        {
            var existing = GetExistingTransfer(itemId, ct);
            if (existing?.State == "completed" && IsSharedLibraryAsset(itemId, ct))
            {
                return ToResult(itemId, existing);
            }

            var preview = Preview(itemId, destinationKind, folderName, ct);

            var item = assets.Find(itemId, ct)!;
            var files = GetFiles(itemId, ct);
            if (preview.AlreadyShared && existing?.State == "cleanup_pending")
            {
                return await FinishCleanupAsync(item, existing, ct);
            }

            var transferId = existing?.Id ?? Guid.NewGuid();
            var now = DateTimeOffset.UtcNow;
            var planned = ReuseOrPlanDestinations(existing, files, item, preview, transferId);
            var sourceManifest = JsonSerializer.Serialize(files.Select(file => new SourceManifestFile(
                file.FileId, file.FilePath, file.ContentHash, file.ByteSize, file.Role)), JsonOptions);
            var destinationManifest = SerializeDestinations(planned);
            await WriteAsync((connection, transaction) => connection.Execute("""
                INSERT INTO view_shared_transfers
                    (id, item_id, contribution_item_id, operation, state, source_manifest_json, destination_manifest_json,
                     error, created_at, updated_at, completed_at)
                VALUES (@transferId, @itemId, @contributionItemId, @operation, 'planned', @sourceManifest,
                        @destinationManifest, NULL, @now, @now, NULL)
                ON CONFLICT(item_id) DO UPDATE SET
                    operation = excluded.operation,
                    contribution_item_id = excluded.contribution_item_id,
                    state = excluded.state,
                    source_manifest_json = excluded.source_manifest_json,
                    destination_manifest_json = excluded.destination_manifest_json,
                    error = NULL,
                    updated_at = excluded.updated_at,
                    completed_at = NULL;
                """, new
            {
                transferId,
                itemId,
                contributionItemId,
                operation = preview.Operation,
                sourceManifest,
                destinationManifest,
                now,
            }, transaction), ct);

            try
            {
                await SetStateAsync(itemId, "transferring", null, ct);
                foreach (var file in planned)
                {
                    await EnsureVerifiedDestinationAsync(file, transferId, ct);
                }

                // Publish Shared Library ownership only after every group member is verified at its final path.
                await WriteAsync((connection, transaction) =>
                {
                    var sharedLibraryId = connection.QuerySingle<Guid>(
                        "SELECT library_id FROM view_shared_library WHERE singleton_key=1;", transaction: transaction);
                    var sourceKey = preview.DestinationKind == "timeline"
                        ? "shared:timeline"
                        : "shared:folder:" + SanitizeFolderName(folderName).ToLowerInvariant();
                    var sourceId = connection.QuerySingleOrDefault<Guid?>("""
                        SELECT id FROM view_sources
                         WHERE scope_kind='shared' AND library_id=@sharedLibraryId AND source_key=@sourceKey;
                        """, new { sharedLibraryId, sourceKey }, transaction);
                    if (sourceId is null)
                    {
                        sourceId = Guid.NewGuid();
                        connection.Execute("""
                            INSERT INTO view_sources
                                (id,scope_kind,personal_space_id,library_id,source_type,name,source_key,
                                 storage_mode,relative_path,external_path,include_subdirectories,enabled,
                                 last_activity_at,created_at,updated_at)
                            VALUES (@sourceId,'shared',NULL,@sharedLibraryId,'folder',@name,@sourceKey,
                                    'managed',@relativePath,NULL,1,1,@now,@now,@now);
                            """, new
                        {
                            sourceId,
                            sharedLibraryId,
                            sourceKey,
                            name = preview.DestinationKind == "timeline" ? "Timeline" : SanitizeFolderName(folderName),
                            relativePath = Path.GetRelativePath(storage.GetRootPath(),
                                    preview.DestinationKind == "timeline"
                                        ? Path.Combine(storage.GetSharedRoot(), "Timeline")
                                        : preview.DestinationRoot)
                                .Replace(Path.DirectorySeparatorChar, '/'),
                            now,
                        }, transaction);
                    }

                    var sharedItemId = connection.QuerySingleOrDefault<Guid?>(
                        "SELECT item_id FROM view_shared_assets WHERE origin_item_id=@itemId;",
                        new { itemId }, transaction) ?? Guid.NewGuid();
                    connection.Execute("""
                        INSERT OR IGNORE INTO local_items
                            (id,scope_kind,personal_space_id,owner_profile_id,library_id,media_kind,title,
                             primary_file_name,primary_mime_type,captured_at,created_at,updated_at,
                             favorite,hidden,archived_at,trashed_at)
                        SELECT @sharedItemId,'shared',NULL,NULL,@sharedLibraryId,media_kind,title,
                               primary_file_name,primary_mime_type,captured_at,@now,@now,0,0,NULL,NULL
                          FROM local_items WHERE id=@itemId;
                        INSERT OR IGNORE INTO local_item_metadata
                            (item_id,width,height,duration_seconds,page_count,device_make,device_model,
                             latitude,longitude,location_name,document_text,metadata_json,updated_at)
                        SELECT @sharedItemId,width,height,duration_seconds,page_count,device_make,device_model,
                               latitude,longitude,location_name,document_text,metadata_json,@now
                          FROM local_item_metadata WHERE item_id=@itemId;
                        INSERT OR IGNORE INTO local_item_files(item_id,file_id,role,derivative_kind,position,added_at)
                        SELECT @sharedItemId,file_id,role,derivative_kind,position,@now
                          FROM local_item_files WHERE item_id=@itemId;
                        INSERT OR IGNORE INTO local_item_tags(item_id,tag,added_at)
                        SELECT @sharedItemId,tag,@now FROM local_item_tags WHERE item_id=@itemId;
                        """, new { sharedItemId, sharedLibraryId, itemId, now }, transaction);
                    foreach (var file in planned)
                    {
                        connection.Execute("""
                            INSERT INTO local_file_sources
                                (id, file_id, library_id, source_id, device_id, file_path, modified_at, indexed_at)
                            VALUES (@id, @FileId, @sharedLibraryId, @sourceId, NULL, @Destination,
                                    @ModifiedAt, @now)
                            ON CONFLICT(library_id, file_path) DO NOTHING;
                            """, new
                        {
                            id = Guid.NewGuid(),
                            file.FileId,
                            sharedLibraryId,
                            sourceId,
                            file.Destination,
                            file.ModifiedAt,
                            now,
                        }, transaction);
                    }
                    connection.Execute("""
                        INSERT INTO view_shared_assets
                            (item_id, origin_item_id, original_profile_id, destination_kind, destination_label,
                             promoted_by_profile_id, promoted_at)
                        VALUES (@sharedItemId, @itemId, @OwnerProfileId, @kind, @label, @actorProfileId, @now)
                        ON CONFLICT(item_id) DO NOTHING;
                        """, new
                    {
                        itemId,
                        sharedItemId,
                        item.OwnerProfileId,
                        kind = preview.DestinationKind,
                        label = preview.DestinationKind == "folder" ? SanitizeFolderName(folderName) : null,
                        actorProfileId,
                        now,
                    }, transaction);
                    connection.Execute("""
                        UPDATE view_shared_transfers
                           SET state = @state, destination_manifest_json = @destinationManifest,
                               updated_at = @now, completed_at = @completedAt, error = NULL
                         WHERE item_id = @itemId;
                        """, new
                    {
                        itemId,
                        state = preview.Operation == "move" ? "cleanup_pending" : "completed",
                        destinationManifest,
                        now,
                        completedAt = preview.Operation == "move" ? (DateTimeOffset?)null : now,
                    }, transaction);
                }, ct);

                if (preview.Operation == "copy")
                {
                    return new ViewSharedTransferResultDto(itemId, "completed", "copy", planned.Count,
                        planned.Select(file => file.Destination).ToList(), false);
                }

                return await FinishCleanupAsync(item, GetExistingTransfer(itemId, ct)!, ct);
            }
            catch (Exception exception)
            {
                // Once Shared Library ownership is published, retain cleanup_pending so retry only removes
                // known source occurrences after re-verifying their Shared copies.
                var state = IsSharedLibraryAsset(itemId, CancellationToken.None) ? "cleanup_pending" : "failed";
                await SetStateAsync(itemId, state, exception.Message, CancellationToken.None);
                throw;
            }
        }
        finally { TransferGate.Release(); }
    }

    private async Task<ViewSharedTransferResultDto> FinishCleanupAsync(
        LocalAssetDto item, ExistingTransfer existing, CancellationToken ct)
    {
        var destinations = ParseDestinations(existing.Manifest);
        if (destinations.Count == 0)
        {
            throw new InvalidOperationException("The Shared transfer has no recovery manifest.");
        }

        var updated = new List<DestinationManifestFile>(destinations.Count);
        foreach (var file in destinations)
        {
            ct.ThrowIfCancellationRequested();
            if (!await IsVerifiedAsync(file.Destination, file.ContentHash, file.ByteSize, ct))
            {
                throw new InvalidDataException("A Shared destination no longer matches its verified transfer record.");
            }

            var removed = file.SourceRemoved || !File.Exists(file.Source);
            if (!removed)
            {
                try
                {
                    // Revalidate immediately before deletion. Recovery never deletes a changed original.
                    if (!await IsVerifiedAsync(file.Source, file.ContentHash, file.ByteSize, ct))
                    {
                        throw new InvalidDataException("A personal original changed while Shared cleanup was pending.");
                    }

                    File.Delete(file.Source);
                    removed = !File.Exists(file.Source);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    removed = false;
                }
            }
            updated.Add(file with { SourceRemoved = removed });
        }

        var cleanupPending = updated.Any(file => !file.SourceRemoved);
        var manifest = SerializeDestinations(updated);
        var now = DateTimeOffset.UtcNow;
        await WriteAsync((connection, transaction) =>
        {
            foreach (var file in updated.Where(value => value.SourceRemoved))
            {
                connection.Execute("""
                    DELETE FROM local_file_sources
                     WHERE file_id = @FileId AND library_id = @LibraryId AND file_path = @Source;
                    """, new { file.FileId, item.LibraryId, file.Source }, transaction);
            }
            connection.Execute("""
                UPDATE view_shared_transfers
                   SET state = @state, destination_manifest_json = @manifest,
                       error = @error, updated_at = @now,
                       completed_at = CASE WHEN @cleanupPending = 0 THEN @now ELSE NULL END
                 WHERE item_id = @itemId;
                """, new
            {
                itemId = item.Id,
                state = cleanupPending ? "cleanup_pending" : "completed",
                manifest,
                error = cleanupPending ? "The Shared copy is safe, but one or more personal originals still need cleanup." : null,
                now,
                cleanupPending,
            }, transaction);
        }, ct);

        return new ViewSharedTransferResultDto(item.Id,
            cleanupPending ? "cleanup_pending" : "completed", existing.Operation,
            updated.Count, updated.Select(file => file.Destination).ToList(), cleanupPending);
    }

    private IReadOnlyList<TransferFile> GetFiles(Guid itemId, CancellationToken ct)
    {
        using var connection = database.CreateConnection();
        return connection.Query<TransferFile>(new CommandDefinition("""
            SELECT lf.id AS FileId, lfs.file_path AS FilePath, lf.content_hash AS ContentHash,
                   lf.byte_size AS ByteSize, lif.role AS Role, lfs.modified_at AS ModifiedAt,
                   COALESCE(vs.storage_mode, 'linked') AS StorageMode
              FROM local_item_files lif
              JOIN local_files lf ON lf.id = lif.file_id
              JOIN local_items li ON li.id = lif.item_id
              JOIN local_file_sources lfs ON lfs.file_id = lf.id AND lfs.library_id = li.library_id
              LEFT JOIN view_sources vs ON vs.id = lfs.source_id
             WHERE lif.item_id = @itemId
             ORDER BY CASE COALESCE(vs.storage_mode, 'linked') WHEN 'managed' THEN 0 ELSE 1 END,
                      lif.position, lfs.indexed_at DESC;
            """, new { itemId }, cancellationToken: ct)).GroupBy(row => row.FileId)
            .Select(group => group.First()).ToList();
    }

    private ExistingTransfer? GetExistingTransfer(Guid itemId, CancellationToken ct)
    {
        using var connection = database.CreateConnection();
        return connection.QuerySingleOrDefault<ExistingTransfer>(new CommandDefinition("""
            SELECT id AS Id, operation AS Operation, state AS State,
                   destination_manifest_json AS Manifest
              FROM view_shared_transfers WHERE item_id = @itemId;
            """, new { itemId }, cancellationToken: ct));
    }

    private bool IsSharedLibraryAsset(Guid itemId, CancellationToken ct)
    {
        using var connection = database.CreateConnection();
        return connection.ExecuteScalar<int>(new CommandDefinition(
            "SELECT COUNT(*) FROM view_shared_assets WHERE item_id=@itemId OR origin_item_id=@itemId;",
            new { itemId }, cancellationToken: ct)) > 0;
    }

    private static ViewSharedTransferResultDto ToResult(Guid itemId, ExistingTransfer row)
    {
        var paths = ParseDestinations(row.Manifest).Select(value => value.Destination).ToList();
        return new ViewSharedTransferResultDto(itemId, row.State, row.Operation, paths.Count, paths,
            row.State == "cleanup_pending");
    }

    private IReadOnlyList<DestinationManifestFile> ReuseOrPlanDestinations(
        ExistingTransfer? existing,
        IReadOnlyList<TransferFile> files,
        LocalAssetDto item,
        ViewSharedTransferPreviewDto preview,
        Guid transferId)
    {
        var recovered = ParseDestinations(existing?.Manifest);
        if (recovered.Count == files.Count
            && recovered.All(value => files.Any(file => file.FileId == value.FileId))
            && recovered.All(value => ViewStorageService.Contains(storage.GetSharedRoot(), value.Destination)))
        {
            return recovered;
        }

        Directory.CreateDirectory(preview.DestinationRoot);
        var reserved = new HashSet<string>(PathComparer);
        var result = new List<DestinationManifestFile>(files.Count);
        foreach (var file in files)
        {
            var name = preview.DestinationKind == "timeline"
                ? TimelineFileName(item, file)
                : Path.GetFileName(file.FilePath);
            var destination = UniqueDestination(preview.DestinationRoot, name, reserved);
            reserved.Add(destination);
            result.Add(new DestinationManifestFile(file.FileId, file.FilePath, destination,
                file.ContentHash, file.ByteSize, file.Role, file.ModifiedAt, false, transferId));
        }
        return result;
    }

    private async Task EnsureVerifiedDestinationAsync(
        DestinationManifestFile planned, Guid transferId, CancellationToken ct)
    {
        if (await IsVerifiedAsync(planned.Destination, planned.ContentHash, planned.ByteSize, ct))
        {
            return;
        }

        if (File.Exists(planned.Destination))
        {
            throw new IOException("A different file now occupies the reserved Shared destination.");
        }

        if (!await IsVerifiedAsync(planned.Source, planned.ContentHash, planned.ByteSize, ct))
        {
            throw new InvalidDataException("A source file changed after it was indexed. Reconcile before trying again.");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(planned.Destination)!);
        var staging = planned.Destination + $".{transferId:N}.transferring";
        if (File.Exists(staging))
        {
            File.Delete(staging);
        }

        await CopyVerifiedAsync(planned, staging, ct);
        File.Move(staging, planned.Destination);
    }

    private string DestinationRoot(LocalAssetDto item, string kind, string? folderName)
    {
        if (kind == "folder")
        {
            return Path.Combine(storage.GetSharedRoot(), "Folders", SanitizeFolderName(folderName));
        }

        var date = (item.CapturedAt ?? item.CreatedAt).ToLocalTime();
        return Path.Combine(storage.GetSharedRoot(), "Timeline", date.Year.ToString("0000", CultureInfo.InvariantCulture),
            date.ToString("MM - MMM", CultureInfo.InvariantCulture));
    }

    private static string TimelineFileName(LocalAssetDto item, TransferFile file)
    {
        var date = (item.CapturedAt ?? item.CreatedAt).ToLocalTime();
        var suffix = item.MediaKind switch
        {
            "image" => "IMG",
            "video" => "VID",
            "document" => "DOC",
            "audio" => "AUD",
            _ => "FILE",
        };
        if (!string.Equals(file.Role, "primary", StringComparison.OrdinalIgnoreCase))
        {
            suffix += $"-{file.Role.ToUpperInvariant()}";
        }

        return date.ToString("MMM dd - HH.mm.ss", CultureInfo.InvariantCulture)
            + suffix + Path.GetExtension(file.FilePath).ToLowerInvariant();
    }

    private static string NormalizeDestinationKind(string value) => value.Trim().ToLowerInvariant() switch
    {
        "timeline" => "timeline",
        "folder" => "folder",
        _ => throw new ArgumentException("Choose Timeline or a named Shared folder.", nameof(value)),
    };

    private static string SanitizeFolderName(string? value)
    {
        var name = value?.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("A Shared folder name is required.", nameof(value));
        }

        if (name is "." or ".." || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            throw new ArgumentException("The Shared folder name is invalid.", nameof(value));
        }

        return name;
    }

    private static string UniqueDestination(string directory, string fileName, IReadOnlySet<string> reserved)
    {
        var path = Path.Combine(directory, fileName);
        if (!File.Exists(path) && !reserved.Contains(path))
        {
            return path;
        }

        var stem = Path.GetFileNameWithoutExtension(fileName);
        var extension = Path.GetExtension(fileName);
        for (var suffix = 2; ; suffix++)
        {
            path = Path.Combine(directory, $"{stem} ({suffix}){extension}");
            if (!File.Exists(path) && !reserved.Contains(path))
            {
                return path;
            }
        }
    }

    private static async Task CopyVerifiedAsync(DestinationManifestFile file, string staging, CancellationToken ct)
    {
        await using (var input = new FileStream(file.Source, FileMode.Open, FileAccess.Read, FileShare.Read,
            81920, FileOptions.Asynchronous | FileOptions.SequentialScan))
        {
            await using (var output = new FileStream(staging, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                81920, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await input.CopyToAsync(output, ct);
            }
        }

        if (!await IsVerifiedAsync(staging, file.ContentHash, file.ByteSize, ct))
        {
            File.Delete(staging);
            throw new InvalidDataException("The copied file did not pass content verification.");
        }
        File.SetLastWriteTimeUtc(staging, File.GetLastWriteTimeUtc(file.Source));
    }

    private static async Task<bool> IsVerifiedAsync(string path, string hash, long bytes, CancellationToken ct) =>
        File.Exists(path)
        && new FileInfo(path).Length == bytes
        && string.Equals(await HashAsync(path, ct), hash, StringComparison.OrdinalIgnoreCase);

    private static async Task<string> HashAsync(string path, CancellationToken ct)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, ct)).ToLowerInvariant();
    }

    private static IReadOnlyList<DestinationManifestFile> ParseDestinations(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        try { return JsonSerializer.Deserialize<List<DestinationManifestFile>>(json, JsonOptions) ?? []; }
        catch (JsonException) { return []; }
    }

    private static string SerializeDestinations(IEnumerable<DestinationManifestFile> values) =>
        JsonSerializer.Serialize(values, JsonOptions);

    private Task SetStateAsync(Guid itemId, string state, string? error, CancellationToken ct) =>
        WriteAsync((connection, transaction) => connection.Execute("""
            UPDATE view_shared_transfers SET state = @state, error = @error, updated_at = @now
             WHERE item_id = @itemId;
            """, new { itemId, state, error, now = DateTimeOffset.UtcNow }, transaction), ct);

    private Task WriteAsync(Action<System.Data.IDbConnection, System.Data.IDbTransaction> action, CancellationToken ct) =>
        database.ExecuteWriteAsync((connection, transaction, token) =>
        {
            token.ThrowIfCancellationRequested();
            action(connection, transaction);
            return true;
        }, ct);

    private static StringComparer PathComparer => OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private sealed class TransferFile
    {
        public Guid FileId { get; init; }
        public string FilePath { get; init; } = "";
        public string ContentHash { get; init; } = "";
        public long ByteSize { get; init; }
        public string Role { get; init; } = "";
        public string StorageMode { get; init; } = "linked";
        public DateTimeOffset ModifiedAt { get; init; }
    }

    private sealed record SourceManifestFile(
        Guid FileId, string FilePath, string ContentHash, long ByteSize, string Role);

    private sealed record DestinationManifestFile(
        Guid FileId,
        string Source,
        string Destination,
        string ContentHash,
        long ByteSize,
        string Role,
        DateTimeOffset ModifiedAt,
        bool SourceRemoved,
        Guid TransferId);

    private sealed class ExistingTransfer
    {
        public Guid Id { get; init; }
        public string Operation { get; init; } = "";
        public string State { get; init; } = "";
        public string? Manifest { get; init; }
    }

}
