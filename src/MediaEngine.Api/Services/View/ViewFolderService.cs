using Dapper;
using MediaEngine.Api.Services.LocalAssets;
using MediaEngine.Contracts.LocalAssets;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.PersonalMedia;
using MediaEngine.Storage.Contracts;

namespace MediaEngine.Api.Services.View;

public sealed class ViewFolderService(
    IDatabaseConnection database,
    IViewPersonalSpaceRepository spaces,
    IProfileRepository profiles,
    ILocalAssetRepository assets,
    ViewStorageService storage)
{
    // Retained as a wire/test compatibility sentinel; persisted Shared sources use their own stable IDs.
    public static readonly Guid SharedLibrarySourceId = Guid.Parse("ffffffff-ffff-ffff-ffff-fffffffffff1");

    public async Task<ViewFolderPageDto> QueryAsync(
        Guid viewerProfileId,
        ResolvedViewScope scope,
        Guid? sourceId,
        string? relativePath,
        bool includeDescendants,
        string? search,
        int offset,
        int limit,
        bool includeDocuments = true,
        CancellationToken ct = default)
    {
        if (offset < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(offset));
        }

        if (limit is < 1 or > 200)
        {
            throw new ArgumentOutOfRangeException(nameof(limit));
        }

        // A profile without Documents never sees a document in a count, a folder node or a page.
        var configured = await ConfiguredSourcesAsync(scope, includeDocuments, ct);
        if (sourceId is null)
        {
            return new ViewFolderPageDto(configured.Select(value => value.Dto).ToList(),
                GetPins(viewerProfileId, configured, ct), null, null,
                string.Empty, false, false, null, [], [], [], 0, false);
        }

        var selected = configured.SingleOrDefault(value => value.SourceId == sourceId.Value)
            ?? throw new KeyNotFoundException("The folder source is unavailable in this View scope.");
        var normalized = NormalizeRelativePath(relativePath);
        var absolute = Path.GetFullPath(Path.Combine(selected.RootPath, normalized));
        if (!ViewStorageService.Contains(selected.RootPath, absolute))
        {
            throw new ArgumentException("The requested folder must remain inside its source.", nameof(relativePath));
        }

        var separator = Path.DirectorySeparatorChar.ToString();
        var prefix = Path.TrimEndingDirectorySeparator(absolute) + Path.DirectorySeparatorChar;
        var paths = QueryPaths(selected.LibraryId, selected.SourceId, prefix, search, selected.SharedOnly, includeDocuments, ct);
        var folderPreferences = GetFolderPreferences(viewerProfileId, selected.SourceId, ct);
        var folders = paths
            .Select(row => new { row.ItemId, Suffix = row.FilePath[prefix.Length..] })
            .Where(row => row.Suffix.Contains(Path.DirectorySeparatorChar))
            .GroupBy(row => row.Suffix[..row.Suffix.IndexOf(Path.DirectorySeparatorChar)], StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                var path = CombineRelative(normalized, group.Key);
                return new ViewFolderNodeDto(group.Key, path, group.Select(row => row.ItemId).Distinct().Count(),
                    folderPreferences.Pins.Contains(path),
                    folderPreferences.TimelinePolicies.GetValueOrDefault(path));
            })
            .OrderByDescending(folder => folder.Pinned)
            .ThenBy(folder => folder.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var itemIds = QueryItemIds(selected.LibraryId, selected.SourceId, prefix,
            includeDescendants, search, offset, limit + 1, selected.SharedOnly, includeDocuments, ct);
        var hasMore = itemIds.Count > limit;
        if (hasMore)
        {
            itemIds.RemoveAt(itemIds.Count - 1);
        }

        var items = assets.FindMany(itemIds, ct)
            .Select(item => ViewItemCapabilities.ForViewer(item, viewerProfileId)).ToList();
        var timelineOverride = folderPreferences.TimelinePolicies.GetValueOrDefault(normalized);
        var effectiveTimeline = EffectiveTimeline(selected.Dto.IncludeInTimeline, normalized,
            folderPreferences.TimelinePolicies);
        return new ViewFolderPageDto(configured.Select(value => value.Dto).ToList(),
            GetPins(viewerProfileId, configured, ct), selected.SourceId,
            selected.Dto.Name, normalized, folderPreferences.Pins.Contains(normalized),
            effectiveTimeline, timelineOverride, Breadcrumbs(selected.Dto.Name, normalized), folders,
            items, offset, hasMore);
    }

    public async Task SetPinAsync(Guid viewerProfileId, ResolvedViewScope scope, Guid sourceId,
        string? relativePath, bool pinned, CancellationToken ct = default)
    {
        var selected = (await ConfiguredSourcesAsync(scope, true, ct)).SingleOrDefault(value => value.SourceId == sourceId)
            ?? throw new KeyNotFoundException("The folder source is unavailable in this View scope.");
        if (selected.SharedOnly)
        {
            throw new ArgumentException("Pin a folder inside an indexed profile source.", nameof(sourceId));
        }

        var normalized = NormalizeRelativePath(relativePath);
        ValidateContainedPath(selected.RootPath, normalized);
        await database.ExecuteWriteAsync((connection, transaction, token) =>
        {
            token.ThrowIfCancellationRequested();
            if (pinned)
            {
                connection.Execute("""
                    INSERT INTO view_folder_pins (profile_id, source_id, relative_path, created_at)
                    VALUES (@viewerProfileId, @sourceId, @normalized, @now)
                    ON CONFLICT(profile_id, source_id, relative_path) DO NOTHING;
                    """, new { viewerProfileId, sourceId, normalized, now = DateTimeOffset.UtcNow }, transaction);
            }
            else
            {
                connection.Execute("""
                    DELETE FROM view_folder_pins
                     WHERE profile_id = @viewerProfileId AND source_id = @sourceId AND relative_path = @normalized;
                    """, new { viewerProfileId, sourceId, normalized }, transaction);
            }

            return true;
        }, ct);
    }

    public async Task SetTimelinePolicyAsync(Guid actorProfileId, bool isAdministrator,
        ResolvedViewScope scope, Guid sourceId, string? relativePath, bool? includeInTimeline,
        CancellationToken ct = default)
    {
        var selected = (await ConfiguredSourcesAsync(scope, true, ct)).SingleOrDefault(value => value.SourceId == sourceId)
            ?? throw new KeyNotFoundException("The folder source is unavailable in this View scope.");
        if (selected.SharedOnly)
        {
            throw new ArgumentException("Shared Library items always appear in the Shared timeline.", nameof(sourceId));
        }

        if (!isAdministrator && selected.Dto.OwnerProfileId != actorProfileId)
        {
            throw new UnauthorizedAccessException("Only the source owner or an administrator can change its Timeline policy.");
        }

        var normalized = NormalizeRelativePath(relativePath);
        var absolute = ValidateContainedPath(selected.RootPath, normalized);
        await database.ExecuteWriteAsync((connection, transaction, token) =>
        {
            token.ThrowIfCancellationRequested();
            if (includeInTimeline.HasValue)
            {
                connection.Execute("""
                    INSERT INTO view_folder_timeline_policies
                        (source_id, relative_path, absolute_path, include_in_timeline,
                         updated_by_profile_id, updated_at)
                    VALUES (@sourceId, @normalized, @absolute, @included, @actorProfileId, @now)
                    ON CONFLICT(source_id, relative_path) DO UPDATE SET
                        absolute_path = excluded.absolute_path,
                        include_in_timeline = excluded.include_in_timeline,
                        updated_by_profile_id = excluded.updated_by_profile_id,
                        updated_at = excluded.updated_at;
                    """, new
                {
                    sourceId,
                    normalized,
                    absolute,
                    included = includeInTimeline.Value ? 1 : 0,
                    actorProfileId,
                    now = DateTimeOffset.UtcNow
                }, transaction);
            }
            else
            {
                connection.Execute("""
                    DELETE FROM view_folder_timeline_policies
                     WHERE source_id = @sourceId AND relative_path = @normalized;
                    """, new { sourceId, normalized }, transaction);
            }

            return true;
        }, ct);
    }

    private async Task<IReadOnlyList<ConfiguredSource>> ConfiguredSourcesAsync(
        ResolvedViewScope scope, bool includeDocuments, CancellationToken ct)
    {
        var result = new List<ConfiguredSource>();
        if (scope.Kind == ViewScopeKind.Shared)
        {
            using var connection = database.CreateConnection();
            var sharedSources = connection.Query<SharedSourceRow>(new CommandDefinition("""
                SELECT vs.id AS Id, vs.library_id AS LibraryId, vs.name AS Name,
                       vs.storage_mode AS StorageMode, vs.relative_path AS RelativePath,
                       vs.external_path AS ExternalPath,
                       vs.include_subdirectories AS IncludeSubdirectories,
                       COALESCE(vsp.include_in_timeline, 0) AS IncludeInTimeline
                 FROM view_sources vs
                 LEFT JOIN view_source_policies vsp ON vsp.source_id = vs.id
                 WHERE vs.scope_kind='shared' AND vs.enabled=1
                 ORDER BY vs.name COLLATE NOCASE, vs.id;
                """, cancellationToken: ct));
            foreach (var source in sharedSources)
            {
                if (!scope.ContainsLibrary(source.LibraryId))
                {
                    continue;
                }

                var linked = string.Equals(source.StorageMode, "linked", StringComparison.Ordinal);
                if (linked && string.IsNullOrWhiteSpace(source.ExternalPath)
                    || !linked && string.IsNullOrWhiteSpace(source.RelativePath))
                {
                    continue;
                }

                var root = linked
                    ? Path.GetFullPath(source.ExternalPath!)
                    : Path.GetFullPath(Path.Combine(storage.GetRootPath(), source.RelativePath!));
                if (!linked && !ViewStorageService.Contains(storage.GetSharedRoot(), root))
                {
                    continue;
                }

                result.Add(new ConfiguredSource(source.LibraryId, source.Id, root, SharedOnly: true,
                    new ViewFolderSourceDto(source.Id, source.Name, Guid.Empty, "Server",
                        linked ? "linked" : "managed", source.IncludeInTimeline,
                        CountItems(source.LibraryId, source.Id, true, includeDocuments, ct), Directory.Exists(root))));
            }
        }

        foreach (var space in await spaces.GetAllAsync(ct))
        {
            if (!scope.ContainsLibrary(space.LibraryId))
            {
                continue;
            }

            var profile = await profiles.GetByIdAsync(space.OwnerProfileId, ct);
            foreach (var source in (await spaces.GetSourcesAsync(space.Id, ct)).Where(value => value.Enabled))
            {
                var root = storage.GetSourcePath(space, source);
                var count = CountItems(space.LibraryId, source.Id, false, includeDocuments, ct);
                result.Add(new ConfiguredSource(space.LibraryId, source.Id, root, SharedOnly: false, new ViewFolderSourceDto(
                    source.Id, source.Name, space.OwnerProfileId, profile?.DisplayName ?? "Profile",
                    source.StorageMode == ViewSourceStorageMode.Linked ? "linked" : "managed",
                    source.IncludeInTimeline, count, Directory.Exists(root))));
            }
        }
        return result.OrderBy(value => value.Dto.OwnerName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(value => value.Dto.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private int CountItems(Guid libraryId, Guid sourceId, bool sharedOnly, bool includeDocuments, CancellationToken ct)
    {
        using var connection = database.CreateConnection();
        return connection.ExecuteScalar<int>(new CommandDefinition("""
            SELECT COUNT(DISTINCT lif.item_id)
              FROM local_item_files lif
              JOIN local_file_sources lfs ON lfs.file_id = lif.file_id
              JOIN local_items li ON li.id = lif.item_id
             WHERE lfs.library_id = @libraryId AND lfs.source_id = @sourceId
               AND li.trashed_at IS NULL AND li.hidden = 0
               AND (@includeDocuments = 1 OR li.media_kind <> @documentKind)
               AND (@sharedOnly = 0 OR EXISTS (SELECT 1 FROM view_shared_assets vsa WHERE vsa.item_id = li.id));
            """, new { libraryId, sourceId, sharedOnly, includeDocuments, documentKind = LocalAssetMediaKinds.Document }, cancellationToken: ct));
    }

    private IReadOnlyList<ViewFolderPinDto> GetPins(Guid viewerProfileId,
        IReadOnlyList<ConfiguredSource> configured, CancellationToken ct)
    {
        var available = configured.Where(value => !value.SharedOnly).ToDictionary(value => value.SourceId);
        if (available.Count == 0)
        {
            return [];
        }

        using var connection = database.CreateConnection();
        return connection.Query<PinRow>(new CommandDefinition("""
            SELECT source_id AS SourceId, relative_path AS RelativePath
              FROM view_folder_pins WHERE profile_id = @viewerProfileId
             ORDER BY created_at DESC;
            """, new { viewerProfileId }, cancellationToken: ct))
            .Where(row => available.ContainsKey(row.SourceId))
            .Select(row => new ViewFolderPinDto(row.SourceId, available[row.SourceId].Dto.Name,
                row.RelativePath, string.IsNullOrWhiteSpace(row.RelativePath)
                    ? available[row.SourceId].Dto.Name : Path.GetFileName(row.RelativePath)))
            .ToList();
    }

    private FolderPreferences GetFolderPreferences(Guid viewerProfileId, Guid sourceId, CancellationToken ct)
    {
        using var connection = database.CreateConnection();
        var pins = connection.Query<string>(new CommandDefinition("""
            SELECT relative_path FROM view_folder_pins
             WHERE profile_id = @viewerProfileId AND source_id = @sourceId;
            """, new { viewerProfileId, sourceId }, cancellationToken: ct)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var policies = connection.Query<TimelinePolicyRow>(new CommandDefinition("""
            SELECT relative_path AS RelativePath, include_in_timeline AS IncludeInTimeline
              FROM view_folder_timeline_policies WHERE source_id = @sourceId;
            """, new { sourceId }, cancellationToken: ct)).ToDictionary(
                row => row.RelativePath, row => (bool?)(row.IncludeInTimeline != 0), StringComparer.OrdinalIgnoreCase);
        return new FolderPreferences(pins, policies);
    }

    private static bool EffectiveTimeline(bool sourceDefault, string relativePath,
        IReadOnlyDictionary<string, bool?> policies)
    {
        var current = relativePath;
        while (true)
        {
            if (policies.TryGetValue(current, out var value) && value.HasValue)
            {
                return value.Value;
            }

            if (string.IsNullOrEmpty(current))
            {
                return sourceDefault;
            }

            current = Path.GetDirectoryName(current) ?? string.Empty;
        }
    }

    private static string ValidateContainedPath(string root, string relative)
    {
        var absolute = Path.GetFullPath(Path.Combine(root, relative));
        if (!ViewStorageService.Contains(root, absolute))
        {
            throw new ArgumentException("The folder path must remain inside its source.", nameof(relative));
        }

        return absolute;
    }

    private sealed record FolderItemPath(Guid ItemId, string FilePath);
    private List<FolderItemPath> QueryPaths(Guid libraryId, Guid sourceId, string prefix, string? search,
        bool sharedOnly, bool includeDocuments, CancellationToken ct)
    {
        using var connection = database.CreateConnection();
        return connection.Query<FolderItemPath>(new CommandDefinition("""
            SELECT DISTINCT li.id AS ItemId, lfs.file_path AS FilePath
              FROM local_file_sources lfs
              JOIN local_item_files lif ON lif.file_id = lfs.file_id
              JOIN local_items li ON li.id = lif.item_id
             WHERE lfs.library_id = @libraryId AND lfs.source_id = @sourceId
               AND li.trashed_at IS NULL AND li.hidden = 0
               AND (@includeDocuments = 1 OR li.media_kind <> @documentKind)
               AND (@sharedOnly = 0 OR EXISTS (
                    SELECT 1 FROM view_shared_assets vsa WHERE vsa.item_id = lif.item_id))
               AND lfs.file_path LIKE @pathPrefix ESCAPE '~'
               AND (@search IS NULL OR li.title LIKE @searchLike ESCAPE '~'
                    OR li.primary_file_name LIKE @searchLike ESCAPE '~')
             ORDER BY lfs.file_path COLLATE NOCASE;
            """, new
        {
            libraryId,
            sourceId,
            sharedOnly = sharedOnly ? 1 : 0,
            includeDocuments = includeDocuments ? 1 : 0,
            documentKind = LocalAssetMediaKinds.Document,
            pathPrefix = EscapeLike(prefix) + "%",
            search = NullIfWhiteSpace(search),
            searchLike = "%" + EscapeLike(search?.Trim() ?? string.Empty) + "%",
        }, cancellationToken: ct)).ToList();
    }

    private List<Guid> QueryItemIds(Guid libraryId, Guid sourceId, string prefix,
        bool recursive, string? search, int offset, int take, bool sharedOnly, bool includeDocuments, CancellationToken ct)
    {
        using var connection = database.CreateConnection();
        return connection.Query<Guid>(new CommandDefinition("""
            SELECT DISTINCT li.id
              FROM local_items li
              JOIN local_item_files lif ON lif.item_id = li.id
              JOIN local_file_sources lfs ON lfs.file_id = lif.file_id
             WHERE lfs.library_id = @libraryId AND lfs.source_id = @sourceId
               AND (@sharedOnly = 0 OR EXISTS (
                    SELECT 1 FROM view_shared_assets vsa WHERE vsa.item_id = li.id))
               AND lfs.file_path LIKE @pathPrefix ESCAPE '~'
               AND (@recursive = 1 OR instr(substr(lfs.file_path, length(@prefix) + 1), @separator) = 0)
               AND (@search IS NULL OR li.title LIKE @searchLike ESCAPE '~'
                    OR li.primary_file_name LIKE @searchLike ESCAPE '~')
               AND li.trashed_at IS NULL AND li.hidden = 0
               AND (@includeDocuments = 1 OR li.media_kind <> @documentKind)
             ORDER BY COALESCE(li.captured_at, li.created_at) DESC, li.id DESC
             LIMIT @take OFFSET @offset;
            """, new
        {
            libraryId,
            sourceId,
            sharedOnly = sharedOnly ? 1 : 0,
            includeDocuments = includeDocuments ? 1 : 0,
            documentKind = LocalAssetMediaKinds.Document,
            prefix,
            pathPrefix = EscapeLike(prefix) + "%",
            recursive = recursive ? 1 : 0,
            separator = Path.DirectorySeparatorChar.ToString(),
            search = NullIfWhiteSpace(search),
            searchLike = "%" + EscapeLike(search?.Trim() ?? string.Empty) + "%",
            take,
            offset,
        }, cancellationToken: ct)).ToList();
    }

    private static IReadOnlyList<ViewFolderBreadcrumbDto> Breadcrumbs(string sourceName, string relative)
    {
        var result = new List<ViewFolderBreadcrumbDto> { new(sourceName, string.Empty) };
        var current = string.Empty;
        foreach (var segment in relative.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = CombineRelative(current, segment);
            result.Add(new ViewFolderBreadcrumbDto(segment, current));
        }
        return result;
    }

    private static string NormalizeRelativePath(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var normalized = value.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar).Trim(Path.DirectorySeparatorChar);
        if (Path.IsPathRooted(normalized) || normalized.Split(Path.DirectorySeparatorChar).Any(part => part is "." or ".."))
        {
            throw new ArgumentException("The folder path is invalid.", nameof(value));
        }

        return normalized;
    }

    private static string CombineRelative(string left, string right) =>
        string.IsNullOrEmpty(left) ? right : Path.Combine(left, right);
    private static string? NullIfWhiteSpace(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    private static string EscapeLike(string value) => value.Replace("~", "~~").Replace("%", "~%").Replace("_", "~_");

    private sealed record ConfiguredSource(
        Guid LibraryId, Guid SourceId, string RootPath, bool SharedOnly, ViewFolderSourceDto Dto);
    private sealed record FolderPreferences(HashSet<string> Pins, Dictionary<string, bool?> TimelinePolicies);
    private sealed class PinRow { public Guid SourceId { get; init; } public string RelativePath { get; init; } = ""; }
    private sealed class TimelinePolicyRow { public string RelativePath { get; init; } = ""; public long IncludeInTimeline { get; init; } }
    private sealed class SharedSourceRow
    {
        public Guid Id { get; init; }
        public Guid LibraryId { get; init; }
        public string Name { get; init; } = "";
        public string StorageMode { get; init; } = "managed";
        public string? RelativePath { get; init; }
        public string? ExternalPath { get; init; }
        public bool IncludeSubdirectories { get; init; }
        public bool IncludeInTimeline { get; init; }
    }
}
