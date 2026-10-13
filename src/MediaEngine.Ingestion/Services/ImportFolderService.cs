using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Enums;
using MediaEngine.Domain.Services;
using MediaEngine.Ingestion.Contracts;
using MediaEngine.Ingestion.Models;
using Microsoft.Extensions.Logging;

namespace MediaEngine.Ingestion.Services;

/// <summary>
/// Default <see cref="IImportFolderService"/>. It reads <c>libraries.json</c> through the configuration loader on each
/// call, so a folder that was just set, or a New media folder that was just marked, applies without a restart.
/// </summary>
public sealed class ImportFolderService : IImportFolderService
{
    // Left free on the destination drive after a file is filed, so a nearly full drive is reported before a copy starts.
    private const long FreeSpaceBufferBytes = 64L * 1024 * 1024;

    private readonly IConfigurationLoader _configLoader;
    private readonly ILogger<ImportFolderService> _logger;
    private readonly object _cacheLock = new();
    private LibrariesConfiguration? _cached;
    private (DateTime LastWriteUtc, long Length)? _cachedStamp;

    public ImportFolderService(IConfigurationLoader configLoader, ILogger<ImportFolderService> logger)
    {
        ArgumentNullException.ThrowIfNull(configLoader);
        ArgumentNullException.ThrowIfNull(logger);
        _configLoader = configLoader;
        _logger = logger;
    }

    public string DuplicatePolicy
        => Load()?.FileHandling.DuplicatePolicy ?? LibraryDuplicatePolicies.KeepOne;

    public string? GetImportPath()
    {
        var configuration = Load();
        return configuration is null ? null : ImportFolderRules.Find(configuration)?.Path;
    }

    public bool IsInImportFolder(string path)
        => GetImportPath() is { } root && IsContained(path, root);

    public bool IsWorkingPath(string path)
        => GetImportPath() is { } root
            && PathSafety.TryNormalizeRoot(root, out var normalizedRoot, out _)
            && IsContained(path, Path.Combine(normalizedRoot!, ImportFolderRules.WorkingFolderName));

    public MediaType? ResolveTypePrior(string path)
    {
        var folder = ResolveTypeFolder(path);
        if (folder is null)
        {
            return null;
        }

        var label = ImportFolderRules.LabelForSubfolder(Path.GetFileName(folder));
        var type = ImportFolderRules.Types.FirstOrDefault(candidate =>
            string.Equals(candidate.Label, label, StringComparison.OrdinalIgnoreCase));
        if (type.MediaTypes is null)
        {
            return null;
        }

        var mediaType = MediaTypeParser.Parse(type.MediaTypes[0]);
        return mediaType == MediaType.Unknown ? null : mediaType;
    }

    public string? ResolveTypeFolder(string path)
    {
        if (GetImportPath() is not { } root
            || !PathSafety.TryNormalizeRoot(root, out var normalizedRoot, out _)
            || !PathSafety.TryNormalizePath(path, out var normalizedPath, out _)
            || !PathSafety.IsContainedBy(normalizedPath!, normalizedRoot!))
        {
            return null;
        }

        var segments = Path.GetRelativePath(normalizedRoot!, normalizedPath!)
            .Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);

        // A file at the root has one segment (its own name); the per-type folder is the first of several.
        return segments.Length >= 2 && ImportFolderRules.LabelForSubfolder(segments[0]) is not null
            ? Path.Combine(normalizedRoot!, segments[0])
            : null;
    }

    public LibraryFolderEntry? ResolveLibraryForType(MediaType mediaType)
    {
        var configuration = Load();
        return configuration is null ? null : FindLibrary(configuration, mediaType) is { } library
            ? LibraryFolderEntryFactory.Create(library)
            : null;
    }

    public ImportDestination ResolveDestination(MediaType mediaType, long requiredBytes = 0)
    {
        var type = ImportFolderRules.TypeForMediaType(mediaType.ToString());
        if (type is null)
        {
            return new ImportDestination(mediaType.ToString(), ImportDestinationState.NoManagedFolder);
        }

        var label = type.Value.Label;
        var configuration = Load();
        if (configuration is null)
        {
            return new ImportDestination(label, ImportDestinationState.Unavailable, Reason: "Tuvima can't read its folder settings right now.");
        }

        // FindLibrary only returns a library whose marked destination is a managed folder; an as-is folder never qualifies.
        var library = ImportFolderRules.FindLibrary(configuration, type.Value.MediaTypes);
        if (library?.PrimaryDestination is null)
        {
            return new ImportDestination(label, ImportDestinationState.NoManagedFolder);
        }

        var entry = LibraryFolderEntryFactory.Create(library);
        var source = entry.PrimaryDestination;
        if (source is null)
        {
            return new ImportDestination(label, ImportDestinationState.NoManagedFolder);
        }

        var problem = CheckAvailable(source, requiredBytes);
        return problem is null
            ? new ImportDestination(label, ImportDestinationState.Ready, entry, source)
            : new ImportDestination(label, ImportDestinationState.Unavailable, entry, source, problem);
    }

    public FileSourceMutationPolicy? CreateImportPolicy(bool allowDelete = false)
    {
        var configuration = Load();
        var location = configuration is null ? null : ImportFolderRules.Find(configuration);
        if (location is null)
        {
            return null;
        }

        // Built by hand rather than with FileSourceMutationPolicyFactory: the Import folder is a storage location, not a
        // library source, so there is no library to build from. It may be moved out of (and an exact duplicate removed from),
        // is never a destination, and is never renamed or tagged.
        return new FileSourceMutationPolicy
        {
            LibraryId = ImportFolderRules.LocationId,
            SourceId = ImportFolderRules.LocationId,
            RootPath = location.Path,
            ManagementMode = FileSourceManagementMode.ManagedByTuvima,
            IsWritable = location.AllowWrite,
            AllowMove = true,
            AllowRename = false,
            AllowMetadataWriteback = false,
            AllowDelete = allowDelete,
            AllowAsDestination = false,
        };
    }

    public IReadOnlyList<string> EnsureSubfolders(bool createImportFolder = false)
    {
        var root = GetImportPath();
        if (root is null || !PathSafety.TryNormalizeRoot(root, out var normalizedRoot, out _))
        {
            return [];
        }

        var created = new List<string>();
        try
        {
            if (!Directory.Exists(normalizedRoot))
            {
                if (!createImportFolder)
                {
                    return created;
                }

                Directory.CreateDirectory(normalizedRoot!);
            }

            foreach (var name in ImportFolderRules.Subfolders)
            {
                var folder = Path.Combine(normalizedRoot!, name);
                if (!Directory.Exists(folder))
                {
                    Directory.CreateDirectory(folder);
                    created.Add(folder);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not create the per-type folders inside the import folder {Path}", root);
        }

        return created;
    }

    public void RemoveEmptyFolders(string startFolder)
    {
        if (GetImportPath() is not { } root
            || !PathSafety.TryNormalizeRoot(root, out var normalizedRoot, out _)
            || !PathSafety.TryNormalizePath(startFolder, out var start, out _))
        {
            return;
        }

        var directory = new DirectoryInfo(start!);
        while (directory is { Exists: true }
               && PathSafety.IsContainedBy(directory.FullName, normalizedRoot!)
               && !PathSafety.Comparer.Equals(Path.TrimEndingDirectorySeparator(directory.FullName), normalizedRoot))
        {
            var parent = directory.Parent;
            var isProtected = IsWorkingPath(directory.FullName)
                || (parent is not null
                    && PathSafety.Comparer.Equals(Path.TrimEndingDirectorySeparator(parent.FullName), normalizedRoot)
                    && ImportFolderRules.LabelForSubfolder(directory.Name) is not null);
            if (isProtected || directory.EnumerateFileSystemInfos().Any())
            {
                return;
            }

            try
            {
                directory.Delete();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogDebug(ex, "Left the empty import folder {Path} in place", directory.FullName);
                return;
            }

            directory = parent!;
        }
    }

    /// <summary>
    /// The folder settings as they are on disk now. They are parsed again only when the file has changed, so a decision per
    /// file does not mean a parse per file, and a change still applies on the very next decision.
    /// </summary>
    private LibrariesConfiguration? Load()
    {
        try
        {
            var stamp = ReadStamp();
            lock (_cacheLock)
            {
                if (stamp is not null && _cached is not null && _cachedStamp == stamp)
                {
                    return _cached;
                }
            }

            var loaded = _configLoader.LoadLibraries();
            lock (_cacheLock)
            {
                _cached = stamp is null ? null : loaded;
                _cachedStamp = stamp;
            }

            return loaded;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Could not read the folder settings for the import folder");
            return null;
        }
    }

    private (DateTime LastWriteUtc, long Length)? ReadStamp()
    {
        var directory = _configLoader.ConfigDirectoryPath;
        if (string.IsNullOrWhiteSpace(directory))
        {
            return null;
        }

        var file = new FileInfo(Path.Combine(directory, "libraries.json"));
        return file.Exists ? (file.LastWriteTimeUtc, file.Length) : null;
    }

    private static LibraryFolderConfig? FindLibrary(LibrariesConfiguration configuration, MediaType mediaType)
    {
        var type = ImportFolderRules.TypeForMediaType(mediaType.ToString());
        if (type is null)
        {
            return null;
        }

        return configuration.Libraries.FirstOrDefault(library =>
            string.Equals(library.Kind, LibraryKinds.Catalogued, StringComparison.OrdinalIgnoreCase)
            && library.MediaTypes.Any(token => type.Value.MediaTypes.Contains(token, StringComparer.OrdinalIgnoreCase)));
    }

    private static bool IsContained(string path, string root)
        => PathSafety.TryNormalizePath(path, out var normalizedPath, out _)
            && PathSafety.TryNormalizeRoot(root, out var normalizedRoot, out _)
            && PathSafety.IsContainedBy(normalizedPath!, normalizedRoot!);

    /// <summary>A short plain-language reason the folder can't take a file now, or <see langword="null"/> when it can.</summary>
    private string? CheckAvailable(LibrarySourceEntry source, long requiredBytes)
    {
        if (!source.AllowsFileMutation)
        {
            return "The folder isn't writable.";
        }

        if (!Directory.Exists(source.Path))
        {
            return "The folder can't be reached.";
        }

        var probe = Path.Combine(source.Path, $".tuvima-write-check-{Guid.NewGuid():N}.part");
        try
        {
            using (File.Create(probe))
            {
            }

            File.Delete(probe);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogDebug(ex, "The New media folder {Path} did not accept a write check", source.Path);
            return "The folder isn't writable.";
        }

        return VolumeProbe.AvailableBytes(source.Path) is { } free && free < requiredBytes + FreeSpaceBufferBytes
            ? "The folder is full."
            : null;
    }
}
