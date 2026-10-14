using MediaEngine.Contracts.Settings;
using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Services;

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
        var snapshot = configuration.LoadLibraries();
        foreach (var location in EffectiveLocations(snapshot))
        {
            // Tuvima's own library and import folders are created or chosen elsewhere and are never offered in a picker.
            // They stay resolvable by id so a typed or remembered path (such as the View root) still validates.
            if (TuvimaOwnFolders.IsOwnLocation(snapshot, location.Id))
            {
                continue;
            }

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
        // Tuvima's own folders are never browsed, even when they are still reachable by id for validation.
        if (TuvimaOwnFolders.IsOwnLocation(configurationSnapshot, location.Id))
        {
            throw new ServerFolderAccessException("This folder belongs to Tuvima and cannot be browsed.");
        }

        var resolved = ResolveSelection(location, request.RelativePath);
        var ownFolders = OwnFolderPaths(includeWholeDataRoot: true);
        if (IsInsideOwnFolder(ownFolders, resolved.Path))
        {
            throw new ServerFolderAccessException("This folder belongs to Tuvima and cannot be browsed.");
        }

        var search = request.Search?.Trim();

        List<ServerFolderEntryDto> directories;
        try
        {
            directories = Directory.EnumerateDirectories(resolved.Path)
                .Select(path => new DirectoryInfo(path))
                .Where(directory => !directory.Name.StartsWith('.')
                    && (directory.Attributes & (FileAttributes.Hidden | FileAttributes.System)) == 0
                    && !IsInsideOwnFolder(ownFolders, PhysicalOrFull(directory.FullName)))
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
        // A whole-drive root never overrides a configured location: if the folder sits inside one that is read-only
        // (for example protected real media), it stays read-only however it was reached.
        var allowWrite = location.AllowWrite
            && !configurationSnapshot.StorageLocations.Any(configured =>
                !configured.AllowWrite
                && Path.IsPathFullyQualified(configured.Path)
                && IsWithin(Path.GetFullPath(configured.Path), resolved.Path));
        var hasWrite = allowWrite && ProbeWrite(accessPath);
        if (!allowWrite && ServerFolderSelectionModes.RequiresWrite(request.SelectionMode))
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

        AddProtectedPathIssue(resolved, location, request.SelectionMode, issues);
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
            ContainsFiles = exists && hasRead && ProbeContainsFiles(resolved.Path),
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

    private const string ImplicitIdPrefix = "drive-";
    private const string ImplicitRootId = "server-root";

    /// <summary>
    /// The administrator-configured locations plus the roots of this machine (every ready drive on Windows,
    /// <c>/</c> elsewhere, which in a container is whatever the container can see). Only administrators and the
    /// first-run setup session reach these actions, so the person choosing folders is the one approving them.
    /// </summary>
    internal static IReadOnlyList<ServerStorageLocationConfig> EffectiveLocations(LibrariesConfiguration snapshot)
    {
        var result = new List<ServerStorageLocationConfig>(snapshot.StorageLocations);
        foreach (var root in MachineRoots())
        {
            if (!result.Any(existing => PathEquals(existing.Path, root.Path)))
            {
                result.Add(root);
            }
        }

        return result;
    }

    private static bool IsImplicit(ServerStorageLocationConfig location) =>
        location.Id.StartsWith(ImplicitIdPrefix, StringComparison.Ordinal)
        || string.Equals(location.Id, ImplicitRootId, StringComparison.Ordinal);

    private static IEnumerable<ServerStorageLocationConfig> MachineRoots()
    {
        if (!OperatingSystem.IsWindows())
        {
            yield return new ServerStorageLocationConfig { Id = ImplicitRootId, Label = "This server", Path = "/", AllowWrite = true };
            yield break;
        }

        DriveInfo[] drives;
        try
        {
            drives = DriveInfo.GetDrives();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            yield break;
        }

        foreach (var drive in drives)
        {
            bool ready;
            try
            {
                ready = drive.IsReady && drive.DriveType is DriveType.Fixed or DriveType.Network or DriveType.Removable;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            if (ready)
            {
                var letter = drive.Name.TrimEnd('\\', ':').ToLowerInvariant();
                yield return new ServerStorageLocationConfig
                {
                    Id = ImplicitIdPrefix + letter,
                    Label = drive.Name.TrimEnd('\\'),
                    Path = drive.Name,
                    AllowWrite = true,
                };
            }
        }
    }

    private static ServerStorageLocationConfig FindLocation(
        LibrariesConfiguration configurationSnapshot,
        string? id) =>
        EffectiveLocations(configurationSnapshot).FirstOrDefault(location =>
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
        return EffectiveLocations(configurationSnapshot)
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

    private bool ProbeContainsFiles(string path)
    {
        try
        {
            var options = new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = true,
                AttributesToSkip = FileAttributes.System | FileAttributes.ReparsePoint,
            };
            return Directory.EnumerateFiles(path, "*", options)
                .Any(file => !Path.GetFileName(file).StartsWith('.'));
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException)
        {
            logger.LogInformation(exception, "Could not look for existing files in server folder {FolderPath}", path);
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
            .SelectMany(library => library.Sources.Select(source => (source.Id, source.Path, Label: library.Name)))
            .ToList();

        // The import folder is kept apart from every library folder, so it is never offered as one either.
        if (ImportFolderRules.Find(configurationSnapshot) is { } importFolder)
        {
            configured.Add((ImportFolderRules.LocationId, importFolder.Path, "the import folder"));
        }

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
            else if (!IsDriveRoot(selected.Path) && IsWithin(selected.Path, configuredPath))
            {
                // Pointing at a whole drive is allowed even though it holds other libraries' folders.
                issues.Add(new ServerFolderValidationIssueDto { Code = "contains_configured_source", Message = $"This folder contains a source already configured for {source.Label}.", LibraryName = source.Label });
            }
        }
    }

    private static string PhysicalOrFull(string path)
    {
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        try
        {
            return Directory.Exists(full) ? ResolveLink(new DirectoryInfo(full)) : full;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return full;
        }
    }

    private static bool IsDriveRoot(string path) =>
        Path.GetPathRoot(path) is { Length: > 0 } root
        && PathEquals(Path.TrimEndingDirectorySeparator(root), Path.TrimEndingDirectorySeparator(path));

    private static bool IsSystemPath(string path)
    {
        var roots = new List<string>();
        if (OperatingSystem.IsWindows())
        {
            foreach (var folder in new[] { Environment.SpecialFolder.Windows, Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86, Environment.SpecialFolder.CommonApplicationData })
            {
                var value = Environment.GetFolderPath(folder);
                if (!string.IsNullOrWhiteSpace(value))
                {
                    roots.Add(value);
                }
            }
        }
        else
        {
            roots.AddRange(["/bin", "/boot", "/dev", "/etc", "/lib", "/lib64", "/proc", "/run", "/sbin", "/sys", "/usr"]);
        }

        return roots.Any(root => IsWithin(Path.TrimEndingDirectorySeparator(root), path));
    }

    private void AddProtectedPathIssue(
        ResolvedSelection selection,
        ServerStorageLocationConfig location,
        string selectionMode,
        List<ServerFolderValidationIssueDto> issues)
    {
        if (IsImplicit(location))
        {
            // A whole drive is browsable, so protect Tuvima's own folders by where they are rather than by name.
            var own = OwnFolderPaths(includeWholeDataRoot: true);
            if (RelativeSegments(selection.Path).Any(segment => segment == ".data")
                || own.Any(path => IsWithin(path, selection.Path) || (!IsDriveRoot(selection.Path) && IsWithin(selection.Path, path))))
            {
                issues.Add(Issue("protected_path", "This folder is reserved for Tuvima application or generated data."));
            }

            if (IsSystemPath(selection.Path))
            {
                issues.Add(Issue("system_path", "This is an operating-system folder and cannot be used for a library."));
            }

            if (PathEquals(selection.Path, selection.RootPath) && ServerFolderSelectionModes.RequiresWrite(selectionMode))
            {
                issues.Add(Issue("drive_root_managed", "Tuvima cannot manage a whole drive. Choose a folder on it, or leave the drive as-is (read-only)."));
            }

            return;
        }

        // A typed path under a configured location (such as the managed library folder) is matched to that location
        // rather than to the whole drive, so it gets the same own-folder check here instead of skipping it.
        var relative = Path.GetRelativePath(selection.RootPath, selection.Path);
        if (RelativeSegments(relative).Any(segment => ProtectedSegments.Contains(segment, StringComparer.OrdinalIgnoreCase))
            || IsInsideOwnFolder(OwnFolderPaths(includeWholeDataRoot: false), selection.Path))
        {
            issues.Add(Issue("protected_path", "This folder is reserved for Tuvima application or generated data."));
        }
    }

    private static readonly string[] DataRootOwnSubfolders = ["data", "config", "secrets", "logs"];

    /// <summary>
    /// Tuvima's own folders on this machine, as physical paths. With <paramref name="includeWholeDataRoot"/> the whole
    /// data folder counts (right for a drive-wide view, where nothing in it is a library); without it only the data
    /// folder's own data, config, secrets and logs folders do, because the managed library folder lives inside it.
    /// </summary>
    private List<string> OwnFolderPaths(bool includeWholeDataRoot)
    {
        var ownFolders = new List<string?>
        {
            configuration.ConfigDirectoryPath,
            Environment.GetEnvironmentVariable("TUVIMA_CONFIG_DIR"),
            Environment.GetEnvironmentVariable("TUVIMA_DATA_PROTECTION_DIR"),
            Environment.GetEnvironmentVariable("TUVIMA_BACKUP_DIR"),
            Environment.GetEnvironmentVariable("TUVIMA_LOG_DIR"),
        };
        var databasePath = Environment.GetEnvironmentVariable("TUVIMA_DB_PATH");
        if (!string.IsNullOrWhiteSpace(databasePath))
        {
            ownFolders.Add(Path.GetDirectoryName(Path.GetFullPath(databasePath)));
        }

        var core = configuration.LoadCore();
        foreach (var dataRoot in new[] { core.DataRoot, new TuvimaDataPaths().Root })
        {
            if (string.IsNullOrWhiteSpace(dataRoot))
            {
                continue;
            }

            if (includeWholeDataRoot)
            {
                ownFolders.Add(dataRoot);
            }
            else
            {
                ownFolders.AddRange(DataRootOwnSubfolders.Select(name => Path.Combine(dataRoot, name)));
            }
        }

        if (!string.IsNullOrWhiteSpace(core.LibraryRoot))
        {
            ownFolders.Add(Path.Combine(core.LibraryRoot, ".data"));
        }

        return ownFolders
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => PhysicalOrFull(path!))
            .ToList();
    }

    private static bool IsInsideOwnFolder(IEnumerable<string> ownFolders, string path) =>
        ownFolders.Any(own => IsWithin(own, path));

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

        var trimmed = Path.TrimEndingDirectorySeparator(root);
        var prefix = trimmed.EndsWith(Path.DirectorySeparatorChar) ? trimmed : trimmed + Path.DirectorySeparatorChar;
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
