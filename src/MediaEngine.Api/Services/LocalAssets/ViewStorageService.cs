using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.PersonalMedia;

namespace MediaEngine.Api.Services.LocalAssets;

/// <summary>
/// Owns the single managed View root and derives profile/source directories
/// from stable identities. Display names never participate in path authority.
/// </summary>
public sealed class ViewStorageService(
    IConfigurationLoader configuration,
    IViewPersonalSpaceRepository spaces,
    IViewSharedLibraryRepository sharedLibrary)
{
    private static readonly SemaphoreSlim PersonalSpaceGate = new(1, 1);
    private static readonly SemaphoreSlim ManagedSourceGate = new(1, 1);

    /// <summary>
    /// The hidden folder, directly inside a person's profile folder, where Empty Trash parks an item's files for a moment before
    /// they are deleted for good. It is never a View source, and scans and the folder watcher ignore anything beneath it.
    /// </summary>
    public const string StagingFolderName = ".tuvima-staging";

    /// <summary>
    /// What the hidden temporary copy of a file starts with while Library writes the profile name into it (batched write-back D9). The
    /// copy sits beside the file so it can replace the file in one step; scans and watchers never index a file named like this.
    /// </summary>
    public const string WriteCopyPrefix = ".tuvima-write-";

    /// <summary>
    /// True when the path is inside the staging folder of a profile folder (<c>Profiles/{label}/.tuvima-staging</c>), or is the hidden
    /// temporary copy of a file being written (<see cref="WriteCopyPrefix"/>), so scans and watchers can skip it. A folder that merely
    /// has the same name somewhere else is an ordinary folder.
    /// </summary>
    /// <remarks>
    /// This reads the path as written and does not follow links, on purpose (it runs for every file of every scan). A managed source
    /// reaches a profile only through <c>Profiles/{label}</c> and <c>ResolveManagedRelativePath</c> refuses any link on that way, and a
    /// linked source may not sit inside the managed root (<c>AddLinkedSourceAsync</c>), so no source can reach a profile's staging
    /// folder through a link. Empty Trash itself also never stages through a link (it resolves the folder before moving anything).
    /// </remarks>
    public static bool IsInsideStagingFolder(string path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return false;
        }

        var segments = path.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length > 0 && segments[^1].StartsWith(WriteCopyPrefix, PathComparison))
        {
            return true;
        }

        for (var index = 2; index < segments.Length; index++)
        {
            if (string.Equals(segments[index], StagingFolderName, PathComparison)
                && string.Equals(segments[index - 2], "Profiles", PathComparison))
            {
                return true;
            }
        }

        return false;
    }

    public string GetRootPath() => GetRootPath(requireWrite: true);

    /// <summary>
    /// Whether the configured View storage location allows writes. A folder on read-only storage can still be shown and
    /// planned against, but the safety gate must refuse to change anything in it.
    /// </summary>
    public bool IsStorageWritable()
    {
        var settings = configuration.LoadLibraries();
        return settings.StorageLocations.FirstOrDefault(candidate =>
            string.Equals(candidate.Id, settings.ViewStorage.StorageLocationId, StringComparison.OrdinalIgnoreCase))?.AllowWrite == true;
    }

    private string GetRootPath(bool requireWrite)
    {
        var settings = configuration.LoadLibraries();
        if (!string.Equals(settings.SchemaVersion, LibrariesConfiguration.CurrentSchemaVersion, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"View storage requires libraries.json schema_version {LibrariesConfiguration.CurrentSchemaVersion}.");
        }

        if (settings.Libraries.Any(library =>
                string.Equals(library.Kind, LibraryKinds.Personal, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException(
                "Personal libraries are obsolete. Configure view_storage and reindex each profile's Personal Space.");
        }

        var storage = settings.StorageLocations.FirstOrDefault(candidate =>
            string.Equals(candidate.Id, settings.ViewStorage.StorageLocationId, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException(
                $"View storage location '{settings.ViewStorage.StorageLocationId}' is not configured.");
        if (requireWrite && !storage.AllowWrite)
        {
            throw new InvalidOperationException("The configured View storage location must allow writes.");
        }

        var basePath = Path.GetFullPath(storage.Path);
        var relative = settings.ViewStorage.RelativeRoot?.Trim();
        if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative))
        {
            throw new InvalidOperationException("The View relative root must be a non-empty relative path.");
        }

        var root = Path.GetFullPath(Path.Combine(basePath, relative));
        EnsureContained(basePath, root, "The View root must remain within its configured storage location.");
        return Path.TrimEndingDirectorySeparator(root);
    }

    public string GetProfileRoot(ViewPersonalSpace space) => GetProfileRoot(space, requireWrite: true);

    /// <summary>
    /// The owner's profile folder. With <paramref name="requireWrite"/> false the folder is returned even when the storage
    /// location is read-only, for callers that only plan and leave the writability decision to the safety gate.
    /// </summary>
    public string GetProfileRoot(ViewPersonalSpace space, bool requireWrite)
    {
        if (!ViewStorageNames.IsValid(space.StorageLabel))
        {
            throw new InvalidOperationException("This Personal Space uses obsolete storage state. Recreate its disposable index and explicitly reimport originals; files are not relocated automatically.");
        }

        return ResolveManagedRelativePath($"Profiles/{space.StorageLabel}", requireWrite);
    }

    public string GetSharedRoot() => ResolveManagedRelativePath("Shared");

    public string GetSharedSourcePath(ViewSharedSource source)
    {
        if (source.StorageMode == ViewSourceStorageMode.Linked)
        {
            if (string.IsNullOrWhiteSpace(source.ExternalPath))
            {
                throw new InvalidOperationException($"Linked Shared source '{source.Name}' has no external path.");
            }

            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(source.ExternalPath));
        }

        if (string.IsNullOrWhiteSpace(source.RelativePath))
        {
            throw new InvalidOperationException($"Managed Shared source '{source.Name}' has no relative path.");
        }

        var path = ResolveManagedRelativePath(source.RelativePath);
        EnsureContained(GetSharedRoot(), path, "A managed Shared source must remain inside the Shared folder.");
        return path;
    }

    public string GetSourcePath(ViewPersonalSpace space, ViewSource source)
    {
        if (source.StorageMode == ViewSourceStorageMode.Linked)
        {
            if (string.IsNullOrWhiteSpace(source.ExternalPath))
            {
                throw new InvalidOperationException($"Linked View source '{source.Name}' has no external path.");
            }

            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(source.ExternalPath));
        }

        if (string.IsNullOrWhiteSpace(source.RelativePath))
        {
            throw new InvalidOperationException($"Managed View source '{source.Name}' has no relative path.");
        }

        var path = ResolveManagedRelativePath(source.RelativePath);
        EnsureContained(GetProfileRoot(space), path, "A personal source must remain inside its owning profile folder.");
        return path;
    }

    public async Task<ViewPersonalSpace> EnsurePersonalSpaceAsync(Guid ownerProfileId, CancellationToken ct = default)
    {
        if (ownerProfileId == Guid.Empty)
        {
            throw new ArgumentException("Profile ID is required.", nameof(ownerProfileId));
        }

        ViewPersonalSpace space;
        await PersonalSpaceGate.WaitAsync(ct);
        try
        {
            space = await spaces.GetByOwnerAsync(ownerProfileId, ct)
                ?? await spaces.CreateAsync(ownerProfileId, Guid.NewGuid(), ct);
        }
        finally { PersonalSpaceGate.Release(); }
        // Reserve stable identity now; physical folders are created by actual writes only.

        var policy = configuration.LoadLibraries().PersonalLibraryPolicy;
        if (policy.AllowBrowserUpload)
        {
            await EnsureManagedSourceAsync(space, "Browser uploads", ViewSourceType.BrowserUpload,
                "builtin:browser-uploads", ct);
        }

        return space;
    }

    public async Task<ViewSource> EnsureManagedSourceAsync(
        ViewPersonalSpace space,
        string name,
        ViewSourceType sourceType,
        string sourceKey,
        CancellationToken ct = default,
        string contentKind = ViewContentKinds.Photos,
        Guid? addedByProfileId = null)
    {
        await ManagedSourceGate.WaitAsync(ct);
        try
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(name);
            ArgumentException.ThrowIfNullOrWhiteSpace(sourceKey);
            var existing = (await spaces.GetSourcesAsync(space.Id, ct)).FirstOrDefault(candidate =>
                string.Equals(candidate.SourceKey, sourceKey, StringComparison.OrdinalIgnoreCase));
            if (existing is not null)
            {
                return existing;
            }

            var now = DateTimeOffset.UtcNow;
            var id = Guid.NewGuid();
            var relative = sourceType == ViewSourceType.BrowserUpload
                ? $"Profiles/{space.StorageLabel}/Timeline"
                : $"Profiles/{space.StorageLabel}/Folders/{ViewStorageNames.FromDisplayName(name)}";
            if (sourceType != ViewSourceType.BrowserUpload && (Directory.Exists(ResolveManagedRelativePath(relative))
                || (await spaces.GetSourcesAsync(space.Id, ct)).Any(item => string.Equals(item.RelativePath, relative, StringComparison.OrdinalIgnoreCase))))
            {
                relative += "-" + id.ToString("N");
            }

            var source = new ViewSource(
                id, space.Id, sourceType, name.Trim(), sourceKey.Trim(), null, now, now,
                ViewSourceStorageMode.Managed,
                relative,
                ExternalPath: null,
                IncludeSubdirectories: true,
                Enabled: true,
                IncludeInTimeline: sourceType == ViewSourceType.BrowserUpload,
                ContentKind: contentKind,
                AddedByProfileId: addedByProfileId);
            source = await spaces.UpsertSourceAsync(source, ct);
            return source;
        }
        finally { ManagedSourceGate.Release(); }
    }

    public async Task<ViewSource> AddLinkedSourceAsync(
        ViewPersonalSpace space,
        string name,
        string path,
        bool includeSubdirectories,
        CancellationToken ct = default,
        string contentKind = ViewContentKinds.Photos,
        Guid? addedByProfileId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!configuration.LoadLibraries().PersonalLibraryPolicy.AllowExistingFolderAttachment)
        {
            throw new InvalidOperationException("Linking an existing folder to View is disabled by policy.");
        }

        var fullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        EnsureNoReparsePoints(fullPath);
        if (IsWithin(GetRootPath(), fullPath) || IsWithin(fullPath, GetRootPath()))
        {
            throw new InvalidOperationException("Folders inside the managed View root are created as managed sources, not linked sources.");
        }

        if (!Directory.Exists(fullPath))
        {
            throw new DirectoryNotFoundException($"Linked View folder '{fullPath}' does not exist.");
        }

        var sources = new List<ViewSource>();
        foreach (var other in await spaces.GetAllAsync(ct))
        {
            sources.AddRange(await spaces.GetSourcesAsync(other.Id, ct));
        }

        if (sources.Any(candidate => candidate.StorageMode == ViewSourceStorageMode.Linked
            && !string.IsNullOrWhiteSpace(candidate.ExternalPath)
            && (IsWithin(candidate.ExternalPath, fullPath) || IsWithin(fullPath, candidate.ExternalPath))))
        {
            throw new InvalidOperationException("This folder overlaps an existing View source. Open the existing source rather than registering it again.");
        }

        var now = DateTimeOffset.UtcNow;
        return await spaces.UpsertSourceAsync(new ViewSource(
            Guid.NewGuid(), space.Id, ViewSourceType.Folder, name.Trim(), $"linked:{Guid.NewGuid():N}",
            null, now, now, ViewSourceStorageMode.Linked, RelativePath: null, ExternalPath: fullPath,
            IncludeSubdirectories: includeSubdirectories, Enabled: true, IncludeInTimeline: false,
            ContentKind: contentKind, AddedByProfileId: addedByProfileId), ct);
    }

    public Task<ViewSource> UpdateSourceAsync(
        ViewPersonalSpace space,
        ViewSource source,
        CancellationToken ct = default)
    {
        if (source.PersonalSpaceId != space.Id)
        {
            throw new InvalidOperationException("A source cannot move between Personal Spaces.");
        }

        return spaces.UpsertSourceAsync(source, ct);
    }

    public async Task<ViewSource> ImportFolderAsync(
        ViewPersonalSpace space,
        string name,
        string sourcePath,
        CancellationToken ct = default,
        string contentKind = ViewContentKinds.Photos,
        Guid? addedByProfileId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        if (!configuration.LoadLibraries().PersonalLibraryPolicy.AllowManagedStorage)
        {
            throw new InvalidOperationException("Importing folders into managed View storage is disabled by policy.");
        }

        var origin = Path.TrimEndingDirectorySeparator(Path.GetFullPath(sourcePath));
        if (!Directory.Exists(origin))
        {
            throw new DirectoryNotFoundException($"Import folder '{origin}' does not exist.");
        }

        EnsureNoReparsePoints(origin);
        if (IsWithin(GetRootPath(), origin) || IsWithin(origin, GetRootPath()))
        {
            throw new InvalidOperationException("A folder already inside the View root does not need to be imported.");
        }

        var source = await EnsureManagedSourceAsync(
            space, name, ViewSourceType.Folder, $"import:{Guid.NewGuid():N}", ct, contentKind, addedByProfileId);
        var destination = GetSourcePath(space, source);
        foreach (var file in Directory.EnumerateFiles(origin, "*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint, IgnoreInaccessible = false }))
        {
            ct.ThrowIfCancellationRequested();
            var info = new FileInfo(file);
            if ((info.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                continue;
            }

            var relative = Path.GetRelativePath(origin, file);
            var target = Path.GetFullPath(Path.Combine(destination, relative));
            EnsureContained(destination, target, "An imported file resolved outside its managed source folder.");
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            await using var input = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read,
                81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
            await using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                81920, FileOptions.Asynchronous | FileOptions.WriteThrough);
            await input.CopyToAsync(output, ct);
            File.SetLastWriteTimeUtc(target, info.LastWriteTimeUtc);
        }
        return source;
    }

    public async Task<IReadOnlyList<(ViewPersonalSpace Space, ViewSource Source, string Path)>>
        GetEnabledSourcesAsync(CancellationToken ct = default)
    {
        var result = new List<(ViewPersonalSpace, ViewSource, string)>();
        foreach (var space in await spaces.GetAllAsync(ct))
        {
            foreach (var source in (await spaces.GetSourcesAsync(space.Id, ct)).Where(candidate => candidate.Enabled))
            {
                result.Add((space, source, GetSourcePath(space, source)));
            }
        }
        return result;
    }

    public async Task<IReadOnlyList<(Guid LibraryId, string Path, bool IncludeSubdirectories)>>
        GetEnabledSourcePathsAsync(CancellationToken ct = default)
    {
        var result = (await GetEnabledSourcesAsync(ct))
            .Select(entry => (entry.Space.LibraryId, entry.Path, entry.Source.IncludeSubdirectories))
            .ToList();
        foreach (var source in (await sharedLibrary.GetSourcesAsync(ct)).Where(candidate => candidate.Enabled))
        {
            result.Add((source.LibraryId, GetSharedSourcePath(source), source.IncludeSubdirectories));
        }

        return result;
    }

    public static bool Contains(string root, string path, bool includeSubdirectories = true)
    {
        var fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var fullPath = Path.GetFullPath(path);
        if (!IsWithin(fullRoot, fullPath))
        {
            return false;
        }

        return includeSubdirectories
            || string.Equals(Path.GetDirectoryName(fullPath), fullRoot, PathComparison);
    }

    /// <summary>
    /// Decides whether a file path really lies beneath a folder once every symbolic link and junction is followed, including
    /// the file itself (the string-only <see cref="Contains"/> cannot see a link). On success <paramref name="resolved"/> holds the
    /// real folder and the real path the file is stored at (they differ from the inputs only when a folder above the root is a link). Anything that leaves the folder after resolution is refused, and so is a path that
    /// reaches the file through a link even when the link stays inside the folder: removing "the file" would then be
    /// ambiguous between the link and its target, so such a file is never treated as removable. The reason never names a path.
    /// </summary>
    public static bool TryResolveContainedPath(string root, string path, out ViewResolvedPath resolved, out string reason)
    {
        resolved = default;
        reason = string.Empty;
        string fullRoot;
        string fullPath;
        try
        {
            fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
            fullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            reason = "The file location is not a valid path.";
            return false;
        }

        if (string.Equals(fullRoot, fullPath, PathComparison) || !IsWithin(fullRoot, fullPath))
        {
            reason = "This file is outside your Personal Space folder.";
            return false;
        }

        string realRoot;
        string realPath;
        try
        {
            realRoot = ResolveRealPath(fullRoot, 0);
            realPath = ResolveRealPath(fullPath, 0);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException
            or NotSupportedException)
        {
            // The links on the way to the file could not be followed, so it cannot be shown to be inside the folder.
            _ = exception;
            reason = "This file's location could not be checked, so it was left where it is.";
            return false;
        }

        if (string.Equals(realRoot, realPath, PathComparison) || !IsWithin(realRoot, realPath))
        {
            reason = "This file is a link that leads outside your Personal Space folder.";
            return false;
        }

        // The same steps below the folder before and after following links means no link was crossed on the way.
        var lexical = Path.GetRelativePath(fullRoot, fullPath);
        var real = Path.GetRelativePath(realRoot, realPath);
        if (!string.Equals(lexical, real, PathComparison))
        {
            reason = "This file is reached through a link, so it was left where it is.";
            return false;
        }

        resolved = new ViewResolvedPath(realRoot, realPath);
        return true;
    }

    /// <summary>Follows every link on the way to <paramref name="fullPath"/>, directory or file, as the filesystem would.</summary>
    private static string ResolveRealPath(string fullPath, int depth)
    {
        const int MaximumLinkDepth = 40;
        if (depth > MaximumLinkDepth)
        {
            throw new IOException("Too many levels of symbolic links.");
        }

        var pathRoot = Path.GetPathRoot(fullPath) ?? string.Empty;
        var current = pathRoot;
        var segments = fullPath[pathRoot.Length..]
            .Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);
        for (var index = 0; index < segments.Length; index++)
        {
            var next = Path.Combine(current, segments[index]);
            FileSystemInfo info = Directory.Exists(next) ? new DirectoryInfo(next) : new FileInfo(next);
            if (info.LinkTarget is null)
            {
                current = next;
                continue;
            }

            var target = info.ResolveLinkTarget(returnFinalTarget: true);
            var targetPath = target?.FullName ?? Path.GetFullPath(info.LinkTarget, current);
            current = ResolveRealPath(Path.TrimEndingDirectorySeparator(targetPath), depth + 1);
        }

        return Path.TrimEndingDirectorySeparator(current);
    }

    private string ResolveManagedRelativePath(string relative, bool requireWrite = true)
    {
        if (Path.IsPathRooted(relative))
        {
            throw new InvalidOperationException("Managed View paths must be relative to the View root.");
        }

        var root = GetRootPath(requireWrite);
        var result = Path.GetFullPath(Path.Combine(root, relative));
        EnsureContained(root, result, "Managed View paths must remain within the View root.");
        EnsureNoReparsePoints(result);
        return Path.TrimEndingDirectorySeparator(result);
    }

    private static void EnsureNoReparsePoints(string path)
    {
        for (var directory = new DirectoryInfo(path); directory is not null; directory = directory.Parent)
        {
            if (directory.Exists && (directory.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidOperationException("View storage cannot traverse symbolic links or junctions.");
            }
        }
    }

    private static void EnsureContained(string root, string path, string message)
    {
        if (!IsWithin(root, path))
        {
            throw new InvalidOperationException(message);
        }
    }

    private static bool IsWithin(string root, string path)
    {
        var relative = Path.GetRelativePath(Path.GetFullPath(root), Path.GetFullPath(path));
        return relative != ".."
            && !relative.StartsWith(".." + Path.DirectorySeparatorChar, PathComparison)
            && !Path.IsPathRooted(relative);
    }

    private static StringComparison PathComparison => OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;
}

/// <summary>A folder and a file inside it, both with every symbolic link and junction followed.</summary>
public readonly record struct ViewResolvedPath(string Root, string Path);
