using MediaEngine.Contracts.Settings;
using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Contracts;

namespace MediaEngine.Api.Services.Settings;

/// <summary>
/// Browses and validates only administrator-approved server/container roots.
/// Every operation canonicalizes paths and resolves links before returning a result.
/// </summary>
public sealed class ServerFolderBrowserService(
    IConfigurationLoader configuration,
    ILogger<ServerFolderBrowserService> logger)
{
    private static readonly string[] ProtectedSegments = [".data", "config", "logs", "bin", "obj"];

    public IReadOnlyList<ServerStorageLocationDto> GetStorageLocations()
    {
        var result = new List<ServerStorageLocationDto>();
        foreach (var location in configuration.LoadLibraries().StorageLocations)
        {
            if (!TryResolveLocationRoot(location, out var root, out var error))
            {
                logger.LogWarning(
                    "Configured server storage location {StorageLocationId} is unavailable: {Reason}",
                    location.Id,
                    error);
                continue;
            }

            var (availableBytes, fileSystem) = GetDriveDetails(root);
            result.Add(ToDto(location, availableBytes, fileSystem));
        }

        return result;
    }

    public BrowseServerFoldersResultDto Browse(BrowseServerFoldersRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var configurationSnapshot = configuration.LoadLibraries();
        var location = FindLocation(configurationSnapshot, request.StorageLocationId);
        var resolved = ResolveSelection(location, request.RelativePath);
        var search = request.Search?.Trim();

        List<ServerFolderEntryDto> directories;
        try
        {
            directories = Directory.EnumerateDirectories(resolved.Path)
                .Select(path => new DirectoryInfo(path))
                .Where(directory => !directory.Name.StartsWith('.'))
                .Where(directory => string.IsNullOrWhiteSpace(search)
                    || directory.Name.Contains(search, StringComparison.OrdinalIgnoreCase))
                .Select(directory => CreateEntry(resolved, directory))
                .Where(entry => entry is not null)
                .Cast<ServerFolderEntryDto>()
                .OrderBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException)
        {
            throw new ServerFolderAccessException("Tuvima cannot read this folder from the server.", exception);
        }

        var relative = NormalizeRelative(request.RelativePath);
        return new BrowseServerFoldersResultDto
        {
            StorageLocation = ToDto(location, GetDriveDetails(resolved.RootPath)),
            RelativePath = relative,
            ParentRelativePath = ParentRelativePath(relative),
            DisplayPath = resolved.Path,
            Directories = directories,
        };
    }

    public ServerFolderValidationResultDto Validate(ValidateServerFolderRequest request) => Validate(request, configuration.LoadLibraries());

    public ServerFolderValidationResultDto Validate(ValidateServerFolderRequest request, LibrariesConfiguration configurationSnapshot)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!ServerFolderSelectionModes.IsValid(request.SelectionMode))
        {
            throw new ServerFolderAccessException("The requested folder selection mode is not supported.");
        }

        var location = string.IsNullOrWhiteSpace(request.ManualPath)
            ? FindLocation(configurationSnapshot, request.StorageLocationId)
            : FindLocationForManualPath(configurationSnapshot, request.ManualPath);
        var issues = new List<ServerFolderValidationIssueDto>();
        ResolvedSelection? resolved = null;
        try
        {
            resolved = string.IsNullOrWhiteSpace(request.ManualPath)
                ? ResolveSelection(location, request.RelativePath, request.SelectionMode == ServerFolderSelectionModes.PersonalSpaceManaged)
                : ResolveManualSelection(location, request.ManualPath, request.SelectionMode == ServerFolderSelectionModes.PersonalSpaceManaged);
        }
        catch (ServerFolderAccessException exception)
        {
            issues.Add(Issue("invalid_path", exception.Message));
        }

        if (resolved is null)
        {
            return new ServerFolderValidationResultDto
            {
                StorageLocationId = location.Id,
                RelativePath = RelativeFor(location, request.ManualPath, request.RelativePath),
                Issues = issues,
            };
        }

        var exists = Directory.Exists(resolved.Path);
        var accessPath = resolved.Path;
        while (!Directory.Exists(accessPath))
        {
            accessPath = Path.GetDirectoryName(accessPath)!;
        }

        var hasRead = ProbeRead(accessPath);
        var hasWrite = location.AllowWrite && ProbeWrite(accessPath);
        if (!location.AllowWrite && ServerFolderSelectionModes.RequiresWrite(request.SelectionMode))
        {
            issues.Add(Issue("storage_location_read_only", "This storage location is not approved for server-side writes."));
        }

        if (!hasRead)
        {
            issues.Add(Issue("read_required", "Tuvima cannot read this folder from the server."));
        }

        if (ServerFolderSelectionModes.RequiresWrite(request.SelectionMode) && !hasWrite)
        {
            issues.Add(Issue("write_required", "This folder must be writable for the selected managed mode."));
        }

        AddProtectedPathIssue(resolved, issues);
        AddConfiguredPathIssues(configurationSnapshot, resolved, request.CurrentSourceId, issues);
        var (availableBytes, fileSystem) = GetDriveDetails(resolved.Path);
        return new ServerFolderValidationResultDto
        {
            StorageLocationId = location.Id,
            RelativePath = RelativeFor(location, request.ManualPath, request.RelativePath),
            Path = resolved.Path,
            Exists = exists,
            HasRead = hasRead,
            HasWrite = hasWrite,
            AvailableBytes = availableBytes,
            FileSystem = fileSystem,
            CanSelect = issues.All(issue => !string.Equals(issue.Severity, "error", StringComparison.OrdinalIgnoreCase)),
            Issues = issues,
        };
    }

    private ServerFolderEntryDto? CreateEntry(ResolvedSelection parent, DirectoryInfo directory)
    {
        try
        {
            var physical = ResolvePhysicalPath(parent.RootPath, directory.FullName);
            if (!IsWithin(parent.RootPath, physical))
            {
                logger.LogWarning(
                    "Skipped server folder link {FolderPath} because it resolves outside approved root {RootPath}",
                    directory.FullName,
                    parent.RootPath);
                return null;
            }

            return new ServerFolderEntryDto
            {
                Name = directory.Name,
                RelativePath = Path.GetRelativePath(parent.RootPath, physical) is "." ? string.Empty : Path.GetRelativePath(parent.RootPath, physical),
                ModifiedAt = directory.LastWriteTimeUtc,
            };
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(exception, "Skipped inaccessible server folder {FolderPath}", directory.FullName);
            return null;
        }
    }

    private static ServerStorageLocationConfig FindLocation(
        LibrariesConfiguration configurationSnapshot,
        string? id) =>
        configurationSnapshot.StorageLocations.FirstOrDefault(location =>
            string.Equals(location.Id, id?.Trim(), StringComparison.OrdinalIgnoreCase))
        ?? throw new ServerFolderAccessException("The selected server storage location is unavailable.");

    private static ServerStorageLocationConfig FindLocationForManualPath(
        LibrariesConfiguration configurationSnapshot,
        string manualPath)
    {
        if (!Path.IsPathFullyQualified(manualPath))
        {
            throw new ServerFolderAccessException("Enter an absolute path that is visible to the Tuvima server.");
        }

        var candidate = Path.TrimEndingDirectorySeparator(Path.GetFullPath(manualPath.Trim()));
        return configurationSnapshot.StorageLocations
            .Select(location => new { Location = location, Valid = TryResolveLocationRoot(location, out var root, out _), Root = root })
            .Where(item => item.Valid && IsWithin(item.Root, candidate))
            .OrderByDescending(item => item.Root.Length)
            .Select(item => item.Location)
            .FirstOrDefault()
            ?? throw new ServerFolderAccessException("This path is outside the server storage locations approved for Tuvima.");
    }

    private static ResolvedSelection ResolveManualSelection(ServerStorageLocationConfig location, string manualPath, bool allowCreate = false)
    {
        if (!TryResolveLocationRoot(location, out var root, out var rootError))
        {
            throw new ServerFolderAccessException(rootError ?? "The selected server storage location is unavailable.");
        }

        var candidate = Path.TrimEndingDirectorySeparator(Path.GetFullPath(manualPath.Trim()));
        if (!IsWithin(root, candidate) || (!allowCreate && !Directory.Exists(candidate)) || File.Exists(candidate))
        {
            throw new ServerFolderAccessException("Tuvima cannot access this folder inside an approved server storage location.");
        }

        var physical = ResolvePhysicalPath(root, candidate);
        if (!IsWithin(root, physical))
        {
            throw new ServerFolderAccessException("This folder resolves outside the approved storage location.");
        }

        return new ResolvedSelection(root, physical);
    }

    private static string RelativeFor(
        ServerStorageLocationConfig location,
        string? manualPath,
        string? relativePath)
    {
        if (string.IsNullOrWhiteSpace(manualPath))
        {
            return NormalizeRelative(relativePath);
        }

        if (!TryResolveLocationRoot(location, out var root, out _))
        {
            return string.Empty;
        }

        var relative = Path.GetRelativePath(root, Path.GetFullPath(manualPath.Trim()));
        return relative == "." ? string.Empty : relative;
    }

    private static ResolvedSelection ResolveSelection(ServerStorageLocationConfig location, string? relativePath, bool allowCreate = false)
    {
        if (!TryResolveLocationRoot(location, out var root, out var rootError))
        {
            throw new ServerFolderAccessException(rootError ?? "The selected server storage location is unavailable.");
        }

        var relative = NormalizeRelative(relativePath);
        if (Path.IsPathRooted(relative) || RelativeSegments(relative).Any(segment => segment == "..")
            || relative.Split('\\').Any(segment => segment == ".."))
        {
            throw new ServerFolderAccessException("Folder navigation cannot leave the approved storage location.");
        }

        var candidate = Path.GetFullPath(Path.Combine(root, relative));
        if (!IsWithin(root, candidate))
        {
            throw new ServerFolderAccessException("Folder navigation cannot leave the approved storage location.");
        }

        if ((!allowCreate && !Directory.Exists(candidate)) || File.Exists(candidate))
        {
            throw new ServerFolderAccessException("Tuvima cannot access this folder from the server.");
        }

        var physical = ResolvePhysicalPath(root, candidate);
        if (!IsWithin(root, physical))
        {
            throw new ServerFolderAccessException("This folder resolves outside the approved storage location.");
        }

        return new ResolvedSelection(root, physical);
    }

    private static bool TryResolveLocationRoot(
        ServerStorageLocationConfig location,
        out string root,
        out string? error)
    {
        root = string.Empty;
        error = null;
        if (string.IsNullOrWhiteSpace(location.Path) || !Path.IsPathFullyQualified(location.Path))
        {
            error = "The configured storage root is not an absolute server path.";
            return false;
        }

        try
        {
            var configuredRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(location.Path));
            if (!Directory.Exists(configuredRoot))
            {
                error = "The configured storage root does not exist on the server.";
                return false;
            }

            root = ResolveLink(new DirectoryInfo(configuredRoot));
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            error = "The configured storage root is not accessible on the server.";
            return false;
        }
    }

    private static string ResolvePhysicalPath(string root, string candidate)
    {
        var relative = Path.GetRelativePath(root, candidate);
        var current = root;
        foreach (var segment in RelativeSegments(relative))
        {
            current = ResolveLink(new DirectoryInfo(Path.Combine(current, segment)));
            if (!IsWithin(root, current))
            {
                return current;
            }
        }

        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(current));
    }

    private static string ResolveLink(DirectoryInfo directory)
    {
        var resolved = directory.LinkTarget is null ? directory : directory.ResolveLinkTarget(true) ?? directory;
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(resolved.FullName));
    }

    private bool ProbeRead(string path)
    {
        try
        {
            _ = Directory.EnumerateFileSystemEntries(path).Take(1).ToList();
            return true;
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException)
        {
            logger.LogInformation(exception, "Read validation failed for approved server folder {FolderPath}", path);
            return false;
        }
    }

    private bool ProbeWrite(string path)
    {
        var probePath = Path.Combine(path, $".tuvima-folder-check-{Guid.NewGuid():N}.tmp");
        try
        {
            using var stream = new FileStream(
                probePath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                1,
                FileOptions.DeleteOnClose);
            stream.WriteByte(0);
            return true;
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException)
        {
            logger.LogInformation(exception, "Write validation failed for approved server folder {FolderPath}", path);
            return false;
        }
        finally
        {
            if (File.Exists(probePath))
            {
                try
                {
                    File.Delete(probePath);
                }
                catch (Exception exception) when (exception is UnauthorizedAccessException or IOException)
                {
                    logger.LogWarning(exception, "Could not remove server-folder validation file {ProbePath}", probePath);
                }
            }
        }
    }

    private static void AddConfiguredPathIssues(
        LibrariesConfiguration configurationSnapshot,
        ResolvedSelection selected,
        string? currentSourceId,
        List<ServerFolderValidationIssueDto> issues)
    {
        var configured = configurationSnapshot.Libraries
            .SelectMany(library => library.Sources.Select(source => (source.Id, source.Path, Label: library.Name)));

        foreach (var source in configured)
        {
            if (string.Equals(source.Id, currentSourceId, StringComparison.OrdinalIgnoreCase)
                || string.IsNullOrWhiteSpace(source.Path)
                || !Path.IsPathFullyQualified(source.Path))
            {
                continue;
            }

            var configuredPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(source.Path));
            if (PathEquals(configuredPath, selected.Path))
            {
                issues.Add(new ServerFolderValidationIssueDto { Code = "already_configured", Message = $"This folder is already configured for {source.Label}.", LibraryName = source.Label });
            }
            else if (IsWithin(configuredPath, selected.Path))
            {
                issues.Add(new ServerFolderValidationIssueDto { Code = "inside_configured_source", Message = $"This folder is inside a source already configured for {source.Label}.", LibraryName = source.Label });
            }
            else if (IsWithin(selected.Path, configuredPath))
            {
                issues.Add(new ServerFolderValidationIssueDto { Code = "contains_configured_source", Message = $"This folder contains a source already configured for {source.Label}.", LibraryName = source.Label });
            }
        }
    }

    private static void AddProtectedPathIssue(
        ResolvedSelection selection,
        List<ServerFolderValidationIssueDto> issues)
    {
        var relative = Path.GetRelativePath(selection.RootPath, selection.Path);
        if (RelativeSegments(relative).Any(segment => ProtectedSegments.Contains(segment, StringComparer.OrdinalIgnoreCase)))
        {
            issues.Add(Issue("protected_path", "This folder is reserved for Tuvima application or generated data."));
        }
    }

    private (long? AvailableBytes, string? FileSystem) GetDriveDetails(string path)
    {
        try
        {
            var root = Path.GetPathRoot(path);
            if (string.IsNullOrWhiteSpace(root))
            {
                return (null, null);
            }

            var drive = new DriveInfo(root);
            return drive.IsReady ? (drive.AvailableFreeSpace, drive.DriveFormat) : (null, null);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            logger.LogDebug(exception, "Optional drive details are unavailable for {FolderPath}", path);
            return (null, null);
        }
    }

    private static ServerStorageLocationDto ToDto(
        ServerStorageLocationConfig location,
        (long? AvailableBytes, string? FileSystem) details) =>
        ToDto(location, details.AvailableBytes, details.FileSystem);

    private static ServerStorageLocationDto ToDto(
        ServerStorageLocationConfig location,
        long? availableBytes,
        string? fileSystem) => new()
        {
            Id = location.Id,
            Label = location.Label,
            Path = location.Path,
            AllowWrite = location.AllowWrite,
            AvailableBytes = availableBytes,
            FileSystem = fileSystem,
        };

    private static ServerFolderValidationIssueDto Issue(string code, string message) => new()
    {
        Code = code,
        Message = message,
    };

    private static string NormalizeRelative(string? path) =>
        string.IsNullOrWhiteSpace(path) || path.Trim() == "."
            ? string.Empty
            : path.Trim().Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar)
                .Trim(Path.DirectorySeparatorChar);

    private static IEnumerable<string> RelativeSegments(string path) =>
        path.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);

    private static string? ParentRelativePath(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            return null;
        }

        var parent = Path.GetDirectoryName(relativePath);
        return string.IsNullOrWhiteSpace(parent) || parent == "." ? string.Empty : parent;
    }

    private static bool IsWithin(string root, string candidate)
    {
        if (PathEquals(root, candidate))
        {
            return true;
        }

        var prefix = Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar;
        return candidate.StartsWith(prefix, PathComparison);
    }

    private static bool PathEquals(string left, string right) =>
        string.Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)),
            PathComparison);

    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private sealed record ResolvedSelection(string RootPath, string Path);
}

public sealed class ServerFolderAccessException : Exception
{
    public ServerFolderAccessException(string message) : base(message)
    {
    }

    public ServerFolderAccessException(string message, Exception innerException) : base(message, innerException)
    {
    }
}
