using MediaEngine.Contracts.LocalAssets;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.PersonalMedia;

namespace MediaEngine.Api.Services.LocalAssets;

public enum ViewSharedSourceDeleteOutcome
{
    Deleted,
    NotFound,
    HasIndexedFiles,
    Protected,
}

/// <summary>Admin operations for sources owned by the server's own Shared library (the server administrator's household).</summary>
public sealed class ViewSharedSourceService(
    IViewSharedLibraryRepository shared,
    IViewPersonalSpaceRepository personalSpaces,
    ViewStorageService storage)
{
    public Task<IReadOnlyList<ViewSharedSource>> GetSourcesAsync(CancellationToken ct = default) =>
        shared.GetSourcesAsync(ct);

    public async Task<ViewSharedSource> CreateAsync(
        CreateViewSourceRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Name);
        if (request.StorageMode is not ("linked" or "managed"))
        {
            throw new ArgumentException("Choose managed or linked storage.", nameof(request));
        }

        var library = await shared.GetAsync(ct);
        var id = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        string? managedImportPath = null;
        ViewSharedSource source;
        if (string.Equals(request.StorageMode, "linked", StringComparison.Ordinal))
        {
            var path = await ValidateLinkedPathAsync(request.Path, ct);
            source = new ViewSharedSource(id, library.LibraryId, ViewSourceType.Folder,
                request.Name.Trim(), $"shared:linked:{id:N}", null, now, now,
                ViewSourceStorageMode.Linked, ExternalPath: path,
                IncludeSubdirectories: request.IncludeSubdirectories,
                Enabled: true, IncludeInTimeline: request.IncludeInTimeline);
        }
        else
        {
            managedImportPath = string.IsNullOrWhiteSpace(request.Path)
                ? null
                : await ValidateImportPathAsync(request.Path, ct);
            var relative = await UniqueManagedRelativePathAsync(request.Name, id, ct);
            source = new ViewSharedSource(id, library.LibraryId, ViewSourceType.Folder,
                request.Name.Trim(), $"shared:managed:{id:N}", null, now, now,
                ViewSourceStorageMode.Managed, RelativePath: relative,
                IncludeSubdirectories: true, Enabled: true,
                IncludeInTimeline: request.IncludeInTimeline);
        }

        source = await shared.UpsertSourceAsync(source, ct);
        if (managedImportPath is not null)
        {
            await CopyIntoManagedSourceAsync(managedImportPath, source, ct);
        }

        return source;
    }

    public async Task<ViewSharedSource?> UpdateAsync(
        Guid sourceId,
        UpdateViewSourceRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Name);
        var source = (await shared.GetSourcesAsync(ct)).SingleOrDefault(candidate => candidate.Id == sourceId);
        return source is null
            ? null
            : await shared.UpsertSourceAsync(source with
            {
                Name = request.Name.Trim(),
                Enabled = request.Enabled,
                IncludeInTimeline = request.IncludeInTimeline,
            }, ct);
    }

    public async Task<ViewSharedSourceDeleteOutcome> DeleteAsync(
        Guid sourceId,
        CancellationToken ct = default)
    {
        var source = (await shared.GetSourcesAsync(ct)).SingleOrDefault(candidate => candidate.Id == sourceId);
        if (source is null)
        {
            return ViewSharedSourceDeleteOutcome.NotFound;
        }

        if (source.SourceKey is "shared:timeline" or "shared:folders")
        {
            return ViewSharedSourceDeleteOutcome.Protected;
        }

        return await shared.DeleteSourceAsync(sourceId, ct)
            ? ViewSharedSourceDeleteOutcome.Deleted
            : ViewSharedSourceDeleteOutcome.HasIndexedFiles;
    }

    private async Task<string> UniqueManagedRelativePathAsync(string name, Guid id, CancellationToken ct)
    {
        var relative = $"Shared/Folders/{ViewStorageNames.FromDisplayName(name)}";
        var existing = await shared.GetSourcesAsync(ct);
        if (existing.Any(source => string.Equals(source.RelativePath, relative, PathComparison))
            || Directory.Exists(Path.Combine(storage.GetRootPath(), relative)))
        {
            relative += "-" + id.ToString("N");
        }

        return relative;
    }

    private async Task<string> ValidateLinkedPathAsync(string? path, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var fullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        EnsureNoReparsePoints(fullPath);
        if (!Directory.Exists(fullPath))
        {
            throw new DirectoryNotFoundException($"Linked View folder '{fullPath}' does not exist.");
        }

        if (IsWithin(storage.GetRootPath(), fullPath) || IsWithin(fullPath, storage.GetRootPath()))
        {
            throw new InvalidOperationException("Folders inside the managed View root are created as managed sources.");
        }

        var linkedPaths = await GetLinkedSourcePathsAsync(ct);
        if (linkedPaths.Any(other => IsWithin(other, fullPath) || IsWithin(fullPath, other)))
        {
            throw new InvalidOperationException("This folder overlaps an existing View source.");
        }

        return fullPath;
    }

    private async Task CopyIntoManagedSourceAsync(
        string origin,
        ViewSharedSource source,
        CancellationToken ct)
    {
        var destination = storage.GetSharedSourcePath(source);
        foreach (var file in Directory.EnumerateFiles(origin, "*", new EnumerationOptions
        { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint, IgnoreInaccessible = false }))
        {
            ct.ThrowIfCancellationRequested();
            var info = new FileInfo(file);
            if ((info.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                continue;
            }

            var target = Path.GetFullPath(Path.Combine(destination, Path.GetRelativePath(origin, file)));
            if (!IsWithin(destination, target))
            {
                throw new InvalidOperationException("An imported file resolved outside its managed Shared source.");
            }

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            await using var input = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read,
                81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
            await using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                81920, FileOptions.Asynchronous | FileOptions.WriteThrough);
            await input.CopyToAsync(output, ct);
            File.SetLastWriteTimeUtc(target, info.LastWriteTimeUtc);
        }
    }

    private async Task<string> ValidateImportPathAsync(string path, CancellationToken ct)
    {
        var origin = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        EnsureNoReparsePoints(origin);
        if (!Directory.Exists(origin))
        {
            throw new DirectoryNotFoundException($"Import folder '{origin}' does not exist.");
        }

        if (IsWithin(storage.GetRootPath(), origin) || IsWithin(origin, storage.GetRootPath()))
        {
            throw new InvalidOperationException("A folder already inside the View root does not need to be imported.");
        }

        if ((await GetLinkedSourcePathsAsync(ct)).Any(other =>
                IsWithin(other, origin) || IsWithin(origin, other)))
        {
            throw new InvalidOperationException(
                "A private or linked View source cannot be imported into the Shared Library.");
        }

        return origin;
    }

    private async Task<IReadOnlyList<string>> GetLinkedSourcePathsAsync(CancellationToken ct)
    {
        var linkedPaths = new List<string>();
        foreach (var space in await personalSpaces.GetAllAsync(ct))
        {
            linkedPaths.AddRange((await personalSpaces.GetSourcesAsync(space.Id, ct))
                .Where(source => source.StorageMode == ViewSourceStorageMode.Linked)
                .Select(source => source.ExternalPath)
                .OfType<string>());
        }

        linkedPaths.AddRange((await shared.GetSourcesAsync(ct))
            .Where(source => source.StorageMode == ViewSourceStorageMode.Linked)
            .Select(source => source.ExternalPath)
            .OfType<string>());
        return linkedPaths;
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
