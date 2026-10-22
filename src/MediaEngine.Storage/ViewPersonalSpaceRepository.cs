using Dapper;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.PersonalMedia;
using MediaEngine.Storage.Contracts;

namespace MediaEngine.Storage;

public sealed class ViewPersonalSpaceRepository(IDatabaseConnection database) : IViewPersonalSpaceRepository
{
    public Task<(int Sources, int Items)> GetInventoryAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var connection = database.CreateConnection();
        var sources = connection.ExecuteScalar<int>(new CommandDefinition("SELECT COUNT(*) FROM view_sources;", cancellationToken: ct));
        var items = connection.ExecuteScalar<int>(new CommandDefinition("""
            SELECT COUNT(DISTINCT lif.file_id) FROM local_item_files lif
            JOIN local_items li ON li.id = lif.item_id
            WHERE lif.role = 'primary' AND li.trashed_at IS NULL;
            """, cancellationToken: ct));
        return Task.FromResult((sources, items));
    }

    public Task<ViewPersonalSpace?> GetByOwnerAsync(Guid ownerProfileId, CancellationToken ct = default) =>
        GetSingleAsync("owner_profile_id", ownerProfileId, ct);

    public Task<ViewPersonalSpace?> GetByLibraryAsync(Guid libraryId, CancellationToken ct = default) =>
        GetSingleAsync("library_id", libraryId, ct);

    public Task<IReadOnlyList<ViewPersonalSpace>> GetAllAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var connection = database.CreateConnection();
        var rows = connection.Query<SpaceRow>(new CommandDefinition("""
            SELECT id AS Id, owner_profile_id AS OwnerProfileId, library_id AS LibraryId,
                   (SELECT label FROM view_storage_labels WHERE personal_space_id = view_personal_spaces.id) AS StorageLabel,
                   created_at AS CreatedAt, updated_at AS UpdatedAt
              FROM view_personal_spaces ORDER BY owner_profile_id;
            """, cancellationToken: ct));
        return Task.FromResult<IReadOnlyList<ViewPersonalSpace>>(rows.Select(Map).ToList());
    }

    public Task<ViewPersonalSpace> CreateAsync(
        Guid ownerProfileId,
        Guid libraryId,
        CancellationToken ct = default)
    {
        ValidateId(ownerProfileId, nameof(ownerProfileId));
        ValidateId(libraryId, nameof(libraryId));
        return database.ExecuteWriteAsync((connection, transaction, token) =>
        {
            token.ThrowIfCancellationRequested();
            if (connection.ExecuteScalar<long>(new CommandDefinition(
                    "SELECT COUNT(1) FROM profiles WHERE id = @ownerProfileId;",
                    new { ownerProfileId }, transaction, cancellationToken: token)) == 0)
            {
                throw new InvalidOperationException($"Profile '{ownerProfileId:D}' does not exist.");
            }

            var existing = connection.QuerySingleOrDefault<SpaceRow>(new CommandDefinition("""
                SELECT id AS Id, owner_profile_id AS OwnerProfileId, library_id AS LibraryId,
                   (SELECT label FROM view_storage_labels WHERE personal_space_id = view_personal_spaces.id) AS StorageLabel,
                       created_at AS CreatedAt, updated_at AS UpdatedAt
                  FROM view_personal_spaces
                 WHERE owner_profile_id = @ownerProfileId OR library_id = @libraryId;
                """, new { ownerProfileId, libraryId }, transaction, cancellationToken: token));
            if (existing is not null)
            {
                if (existing.OwnerProfileId != ownerProfileId || existing.LibraryId != libraryId)
                {
                    throw new InvalidOperationException("A profile and configured library can each belong to only one Personal Space.");
                }
                return Map(existing);
            }

            var now = DateTimeOffset.UtcNow;
            var id = Guid.NewGuid();
            connection.Execute(new CommandDefinition("""
                INSERT INTO view_personal_spaces
                    (id, owner_profile_id, library_id, created_at, updated_at)
                VALUES (@id, @ownerProfileId, @libraryId, @now, @now);
                """, new { id, ownerProfileId, libraryId, now }, transaction, cancellationToken: token));
            var displayName = connection.ExecuteScalar<string>(new CommandDefinition(
                "SELECT display_name FROM profiles WHERE id = @ownerProfileId;",
                new { ownerProfileId }, transaction, cancellationToken: token)) ?? "profile";
            var label = ViewStorageNames.FromDisplayName(displayName);
            if (connection.ExecuteScalar<long>(new CommandDefinition(
                "SELECT COUNT(*) FROM view_storage_labels WHERE label = @label COLLATE NOCASE;",
                new { label }, transaction, cancellationToken: token)) > 0)
            {
                label += "-" + ownerProfileId.ToString("N");
            }

            connection.Execute(new CommandDefinition(
                "INSERT INTO view_storage_labels (personal_space_id, label) VALUES (@id, @label);",
                new { id, label }, transaction, cancellationToken: token));
            return new ViewPersonalSpace(id, ownerProfileId, libraryId, now, now, label);
        }, ct);
    }

    public Task<IReadOnlyList<ViewSource>> GetSourcesAsync(Guid personalSpaceId, CancellationToken ct = default)
    {
        ValidateId(personalSpaceId, nameof(personalSpaceId));
        ct.ThrowIfCancellationRequested();
        using var connection = database.CreateConnection();
        var rows = connection.Query<SourceRow>(new CommandDefinition($"""
            SELECT id AS Id, personal_space_id AS PersonalSpaceId, source_type AS SourceType,
                   name AS Name, source_key AS SourceKey, last_activity_at AS LastActivityAt,
                   storage_mode AS StorageMode, relative_path AS RelativePath,
                   external_path AS ExternalPath, include_subdirectories AS IncludeSubdirectories,
                   enabled AS Enabled,
                   content_kind AS ContentKind, added_by_profile_id AS AddedByProfileId,
                   skipped_file_count AS SkippedFileCount,
                   {ViewItemFileWriteRepository.KeptInLibraryCountSql} AS KeptInLibraryCount,
                   COALESCE((SELECT include_in_timeline FROM view_source_policies
                              WHERE source_id = view_sources.id),
                            CASE WHEN source_type = 'browser_upload' THEN 1 ELSE 0 END) AS IncludeInTimeline,
                   created_at AS CreatedAt, updated_at AS UpdatedAt
              FROM view_sources WHERE scope_kind='personal' AND personal_space_id = @personalSpaceId
             ORDER BY name COLLATE NOCASE, id;
            """, new { personalSpaceId }, cancellationToken: ct));
        return Task.FromResult<IReadOnlyList<ViewSource>>(rows.Select(Map).ToList());
    }

    public Task<ViewSource> UpsertSourceAsync(ViewSource source, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ValidateId(source.PersonalSpaceId, nameof(source));
        ArgumentException.ThrowIfNullOrWhiteSpace(source.Name);
        if (source.StorageMode == ViewSourceStorageMode.Managed
            && (string.IsNullOrWhiteSpace(source.RelativePath) || !string.IsNullOrWhiteSpace(source.ExternalPath)))
        {
            throw new ArgumentException("A managed View source requires only a relative path.", nameof(source));
        }

        if (source.StorageMode == ViewSourceStorageMode.Linked
            && (string.IsNullOrWhiteSpace(source.ExternalPath) || !string.IsNullOrWhiteSpace(source.RelativePath)))
        {
            throw new ArgumentException("A linked View source requires only an external path.", nameof(source));
        }

        return database.ExecuteWriteAsync((connection, transaction, token) =>
        {
            token.ThrowIfCancellationRequested();
            var libraryId = RequireSpace(connection, transaction, source.PersonalSpaceId, token);
            var id = source.Id == Guid.Empty ? Guid.NewGuid() : source.Id;
            var now = DateTimeOffset.UtcNow;
            var createdAt = source.CreatedAt == default ? now : source.CreatedAt;
            var previousKind = connection.ExecuteScalar<string?>(new CommandDefinition(
                "SELECT content_kind FROM view_sources WHERE id = @id;", new { id }, transaction, cancellationToken: token));
            var changed = connection.Execute(new CommandDefinition("""
                INSERT INTO view_sources
                    (id, scope_kind, personal_space_id, library_id, source_type, name, source_key, storage_mode,
                     relative_path, external_path, include_subdirectories, enabled,
                     last_activity_at, content_kind, added_by_profile_id, created_at, updated_at)
                VALUES (@id, 'personal', @PersonalSpaceId, @libraryId, @SourceType, @Name, @SourceKey, @StorageMode,
                        @RelativePath, @ExternalPath, @IncludeSubdirectories, @Enabled,
                        @LastActivityAt, @ContentKind, @AddedByProfileId, @createdAt, @now)
                ON CONFLICT(id) DO UPDATE SET
                    source_type = excluded.source_type, name = excluded.name,
                    source_key = excluded.source_key, storage_mode = excluded.storage_mode,
                    relative_path = excluded.relative_path, external_path = excluded.external_path,
                    include_subdirectories = excluded.include_subdirectories, enabled = excluded.enabled,
                    last_activity_at = excluded.last_activity_at,
                    content_kind = excluded.content_kind,
                    updated_at = excluded.updated_at
                WHERE view_sources.scope_kind='personal'
                  AND view_sources.personal_space_id = excluded.personal_space_id
                  AND view_sources.library_id = excluded.library_id;
                """, new
            {
                id,
                libraryId,
                source.PersonalSpaceId,
                SourceType = ToStorage(source.SourceType),
                Name = source.Name.Trim(),
                SourceKey = NullIfWhiteSpace(source.SourceKey),
                StorageMode = ToStorage(source.StorageMode),
                RelativePath = NullIfWhiteSpace(source.RelativePath),
                ExternalPath = NullIfWhiteSpace(source.ExternalPath),
                source.IncludeSubdirectories,
                source.Enabled,
                source.LastActivityAt,
                source.ContentKind,
                source.AddedByProfileId,
                createdAt,
                now,
            }, transaction, cancellationToken: token));
            if (changed == 0)
            {
                throw new InvalidOperationException("A source identity cannot move between Personal Spaces.");
            }

            connection.Execute(new CommandDefinition("""
                INSERT INTO view_source_policies (source_id, include_in_timeline, updated_at)
                VALUES (@id, @IncludeInTimeline, @now)
                ON CONFLICT(source_id) DO UPDATE SET
                    include_in_timeline = excluded.include_in_timeline,
                    updated_at = excluded.updated_at;
                """, new { id, source.IncludeInTimeline, now }, transaction, cancellationToken: token));
            if (previousKind is not null && !string.Equals(previousKind, source.ContentKind, StringComparison.Ordinal))
            {
                // Changing what a folder holds re-applies it to what is already indexed, in this same transaction.
                ViewSourceKindReapplication.Apply(connection, transaction, id, source.SourceKey, source.ContentKind, token);
            }

            return source with { Id = id, Name = source.Name.Trim(), CreatedAt = createdAt, UpdatedAt = now };
        }, ct);
    }

    public Task<bool> DeleteSourceAsync(Guid personalSpaceId, Guid sourceId, CancellationToken ct = default)
    {
        ValidateId(personalSpaceId, nameof(personalSpaceId));
        ValidateId(sourceId, nameof(sourceId));
        return database.ExecuteWriteAsync((connection, transaction, token) =>
            connection.Execute(new CommandDefinition("""
                DELETE FROM view_sources
                 WHERE id = @sourceId AND personal_space_id = @personalSpaceId;
                """, new { sourceId, personalSpaceId }, transaction, cancellationToken: token)) > 0, ct);
    }

    public Task SetSourceSkippedFileCountAsync(
        Guid personalSpaceId, Guid sourceId, int skippedFileCount, CancellationToken ct = default)
    {
        ValidateId(personalSpaceId, nameof(personalSpaceId));
        ValidateId(sourceId, nameof(sourceId));
        ArgumentOutOfRangeException.ThrowIfNegative(skippedFileCount);
        return database.ExecuteWriteAsync((connection, transaction, token) =>
            connection.Execute(new CommandDefinition("""
                UPDATE view_sources SET skipped_file_count = @skippedFileCount
                 WHERE id = @sourceId AND personal_space_id = @personalSpaceId
                   AND skipped_file_count <> @skippedFileCount;
                """, new { sourceId, personalSpaceId, skippedFileCount }, transaction, cancellationToken: token)), ct);
    }

    public Task<IReadOnlyList<ViewDevice>> GetDevicesAsync(Guid personalSpaceId, CancellationToken ct = default)
    {
        ValidateId(personalSpaceId, nameof(personalSpaceId));
        ct.ThrowIfCancellationRequested();
        using var connection = database.CreateConnection();
        var rows = connection.Query<DeviceRow>(new CommandDefinition("""
            SELECT id AS Id, personal_space_id AS PersonalSpaceId, source_id AS SourceId,
                   client_device_id AS ClientDeviceId, name AS Name, make AS Make, model AS Model,
                   last_backup_at AS LastBackupAt, backup_state AS BackupState,
                   created_at AS CreatedAt, updated_at AS UpdatedAt
              FROM view_devices WHERE personal_space_id = @personalSpaceId
             ORDER BY name COLLATE NOCASE, id;
            """, new { personalSpaceId }, cancellationToken: ct));
        return Task.FromResult<IReadOnlyList<ViewDevice>>(rows.Select(Map).ToList());
    }

    public Task<ViewDevice> UpsertDeviceAsync(ViewDevice device, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(device);
        ValidateId(device.PersonalSpaceId, nameof(device));
        ArgumentException.ThrowIfNullOrWhiteSpace(device.ClientDeviceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(device.Name);
        return database.ExecuteWriteAsync((connection, transaction, token) =>
        {
            token.ThrowIfCancellationRequested();
            RequireSpace(connection, transaction, device.PersonalSpaceId, token);
            if (device.SourceId.HasValue && connection.ExecuteScalar<long>(new CommandDefinition("""
                    SELECT COUNT(1) FROM view_sources
                     WHERE id = @SourceId AND personal_space_id = @PersonalSpaceId;
                    """, device, transaction, cancellationToken: token)) == 0)
            {
                throw new InvalidOperationException("A device source must belong to the same Personal Space.");
            }
            var id = device.Id == Guid.Empty ? Guid.NewGuid() : device.Id;
            var now = DateTimeOffset.UtcNow;
            var createdAt = device.CreatedAt == default ? now : device.CreatedAt;
            var changed = connection.Execute(new CommandDefinition("""
                INSERT INTO view_devices
                    (id, personal_space_id, source_id, client_device_id, name, make, model,
                     last_backup_at, backup_state, created_at, updated_at)
                VALUES
                    (@id, @PersonalSpaceId, @SourceId, @ClientDeviceId, @Name, @Make, @Model,
                     @LastBackupAt, @BackupState, @createdAt, @now)
                ON CONFLICT(id) DO UPDATE SET
                    source_id = excluded.source_id, client_device_id = excluded.client_device_id,
                    name = excluded.name, make = excluded.make, model = excluded.model,
                    last_backup_at = excluded.last_backup_at, backup_state = excluded.backup_state,
                    updated_at = excluded.updated_at
                WHERE view_devices.personal_space_id = excluded.personal_space_id;
                """, new
            {
                id,
                device.PersonalSpaceId,
                device.SourceId,
                ClientDeviceId = device.ClientDeviceId.Trim(),
                Name = device.Name.Trim(),
                Make = NullIfWhiteSpace(device.Make),
                Model = NullIfWhiteSpace(device.Model),
                device.LastBackupAt,
                BackupState = ToStorage(device.BackupState),
                createdAt,
                now,
            }, transaction, cancellationToken: token));
            if (changed == 0)
            {
                throw new InvalidOperationException("A device identity cannot move between Personal Spaces.");
            }

            return device with
            {
                Id = id,
                ClientDeviceId = device.ClientDeviceId.Trim(),
                Name = device.Name.Trim(),
                CreatedAt = createdAt,
                UpdatedAt = now,
            };
        }, ct);
    }

    private Task<ViewPersonalSpace?> GetSingleAsync(string column, Guid value, CancellationToken ct)
    {
        ValidateId(value, nameof(value));
        ct.ThrowIfCancellationRequested();
        using var connection = database.CreateConnection();
        var row = connection.QuerySingleOrDefault<SpaceRow>(new CommandDefinition($"""
            SELECT id AS Id, owner_profile_id AS OwnerProfileId, library_id AS LibraryId,
                   (SELECT label FROM view_storage_labels WHERE personal_space_id = view_personal_spaces.id) AS StorageLabel,
                   created_at AS CreatedAt, updated_at AS UpdatedAt
              FROM view_personal_spaces WHERE {column} = @value;
            """, new { value }, cancellationToken: ct));
        return Task.FromResult(row is null ? null : Map(row));
    }

    private static Guid RequireSpace(
        System.Data.IDbConnection connection,
        System.Data.IDbTransaction transaction,
        Guid personalSpaceId,
        CancellationToken ct)
    {
        var libraryId = connection.QuerySingleOrDefault<Guid?>(new CommandDefinition(
                "SELECT library_id FROM view_personal_spaces WHERE id = @personalSpaceId;",
                new { personalSpaceId }, transaction, cancellationToken: ct));
        if (libraryId is null)
        {
            throw new InvalidOperationException($"Personal Space '{personalSpaceId:D}' does not exist.");
        }
        return libraryId.Value;
    }

    private static ViewPersonalSpace Map(SpaceRow row) => new(
        row.Id, row.OwnerProfileId, row.LibraryId, ParseDate(row.CreatedAt), ParseDate(row.UpdatedAt), row.StorageLabel);

    private static ViewSource Map(SourceRow row) => new(
        row.Id, row.PersonalSpaceId, ParseSourceType(row.SourceType), row.Name, row.SourceKey,
        ParseNullableDate(row.LastActivityAt), ParseDate(row.CreatedAt), ParseDate(row.UpdatedAt),
        ParseStorageMode(row.StorageMode), row.RelativePath, row.ExternalPath,
        row.IncludeSubdirectories, row.Enabled, row.IncludeInTimeline,
        row.ContentKind, row.AddedByProfileId, checked((int)row.SkippedFileCount), checked((int)row.KeptInLibraryCount));

    private static ViewDevice Map(DeviceRow row) => new(
        row.Id, row.PersonalSpaceId, row.SourceId, row.ClientDeviceId, row.Name, row.Make, row.Model,
        ParseNullableDate(row.LastBackupAt), ParseBackupState(row.BackupState),
        ParseDate(row.CreatedAt), ParseDate(row.UpdatedAt));

    private static string ToStorage(ViewSourceType value) => value switch
    {
        ViewSourceType.Folder => "folder",
        ViewSourceType.BrowserUpload => "browser_upload",
        ViewSourceType.DeviceImport => "device_import",
        ViewSourceType.MobileBackup => "mobile_backup",
        ViewSourceType.Network => "network",
        ViewSourceType.Other => "other",
        _ => throw new ArgumentOutOfRangeException(nameof(value)),
    };

    private static ViewSourceType ParseSourceType(string value) => value switch
    {
        "folder" => ViewSourceType.Folder,
        "browser_upload" => ViewSourceType.BrowserUpload,
        "device_import" => ViewSourceType.DeviceImport,
        "mobile_backup" => ViewSourceType.MobileBackup,
        "network" => ViewSourceType.Network,
        "other" => ViewSourceType.Other,
        _ => throw new InvalidOperationException($"Unsupported stored source type '{value}'."),
    };

    private static string ToStorage(ViewSourceStorageMode value) => value switch
    {
        ViewSourceStorageMode.Managed => "managed",
        ViewSourceStorageMode.Linked => "linked",
        _ => throw new ArgumentOutOfRangeException(nameof(value)),
    };

    private static ViewSourceStorageMode ParseStorageMode(string value) => value switch
    {
        "managed" => ViewSourceStorageMode.Managed,
        "linked" => ViewSourceStorageMode.Linked,
        _ => throw new InvalidOperationException($"Unsupported stored View source storage mode '{value}'."),
    };

    private static string ToStorage(ViewDeviceBackupState value) => value switch
    {
        ViewDeviceBackupState.Unknown => "unknown",
        ViewDeviceBackupState.Idle => "idle",
        ViewDeviceBackupState.BackingUp => "backing_up",
        ViewDeviceBackupState.Complete => "complete",
        ViewDeviceBackupState.Error => "error",
        _ => throw new ArgumentOutOfRangeException(nameof(value)),
    };

    private static ViewDeviceBackupState ParseBackupState(string value) => value switch
    {
        "unknown" => ViewDeviceBackupState.Unknown,
        "idle" => ViewDeviceBackupState.Idle,
        "backing_up" => ViewDeviceBackupState.BackingUp,
        "complete" => ViewDeviceBackupState.Complete,
        "error" => ViewDeviceBackupState.Error,
        _ => throw new InvalidOperationException($"Unsupported stored backup state '{value}'."),
    };

    private static DateTimeOffset ParseDate(string value) => DateTimeOffset.Parse(value);
    private static DateTimeOffset? ParseNullableDate(string? value) =>
        DateTimeOffset.TryParse(value, out var parsed) ? parsed : null;
    private static string? NullIfWhiteSpace(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    private static void ValidateId(Guid value, string parameterName)
    {
        if (value == Guid.Empty)
        {
            throw new ArgumentException("ID is required.", parameterName);
        }
    }

    private sealed class SpaceRow
    {
        public string StorageLabel { get; init; } = "";
        public Guid Id { get; init; }
        public Guid OwnerProfileId { get; init; }
        public Guid LibraryId { get; init; }
        public string CreatedAt { get; init; } = string.Empty; public string UpdatedAt { get; init; } = string.Empty;
    }
    private sealed class SourceRow
    {
        public Guid Id { get; init; }
        public Guid PersonalSpaceId { get; init; }
        public string SourceType { get; init; } = string.Empty; public string Name { get; init; } = string.Empty;
        public string? SourceKey { get; init; }
        public string? LastActivityAt { get; init; }
        public string StorageMode { get; init; } = "managed"; public string? RelativePath { get; init; }
        public string? ExternalPath { get; init; }
        public bool IncludeSubdirectories { get; init; }
        public bool Enabled { get; init; }
        public bool IncludeInTimeline { get; init; }
        public string ContentKind { get; init; } = "photos";
        public Guid? AddedByProfileId { get; init; }
        public long SkippedFileCount { get; init; }
        public long KeptInLibraryCount { get; init; }
        public string CreatedAt { get; init; } = string.Empty; public string UpdatedAt { get; init; } = string.Empty;
    }
    private sealed class DeviceRow
    {
        public Guid Id { get; init; }
        public Guid PersonalSpaceId { get; init; }
        public Guid? SourceId { get; init; }
        public string ClientDeviceId { get; init; } = string.Empty; public string Name { get; init; } = string.Empty;
        public string? Make { get; init; }
        public string? Model { get; init; }
        public string? LastBackupAt { get; init; }
        public string BackupState { get; init; } = string.Empty; public string CreatedAt { get; init; } = string.Empty;
        public string UpdatedAt { get; init; } = string.Empty;
    }
}
