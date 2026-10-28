using MediaEngine.Contracts.Settings;
using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Services;
using MediaEngine.Ingestion.Videos;
using MediaEngine.Storage.Services;

namespace MediaEngine.Api.Services.Settings;

/// <summary>
/// Browses and validates only administrator-approved server/container roots.
/// Every operation canonicalizes paths and resolves links before returning a result.
/// </summary>
public sealed class ServerFolderBrowserService(
    IConfigurationLoader configuration,
    ILogger<ServerFolderBrowserService> logger,
    VideoNamingConfigurationProvider? videoNaming = null,
    IMediaTypeExtensionCatalog? extensionCatalog = null,
    ILibraryKindSource? kindSource = null)
{
    private readonly IMediaTypeExtensionCatalog _extensions = extensionCatalog ?? new MediaTypeExtensionCatalog(configuration);

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

    public BrowseServerFoldersResultDto Browse(BrowseServerFoldersRequest request) => Browse(request, NoHiddenRoots);

    /// <summary>
    /// Browses one approved root. <paramref name="hiddenRoots"/> are folders the caller may not know about (the Vault's folders while
    /// Vault mode is off): they and everything under them are left out of listings and answer exactly as a folder that does not exist
    /// (Videos spec D17).
    /// </summary>
    public BrowseServerFoldersResultDto Browse(BrowseServerFoldersRequest request, IReadOnlyCollection<string> hiddenRoots)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(hiddenRoots);
        var hidden = NormalizeHiddenRoots(hiddenRoots);
        var configurationSnapshot = configuration.LoadLibraries();
        var location = FindLocation(configurationSnapshot, request.StorageLocationId);
        // Tuvima's own folders are never browsed, even when they are still reachable by id for validation.
        if (TuvimaOwnFolders.IsOwnLocation(configurationSnapshot, location.Id))
        {
            throw new ServerFolderAccessException("This folder belongs to Library and cannot be browsed.");
        }

        var resolved = ResolveSelection(location, request.RelativePath);
        if (IsHidden(hidden, resolved.Path))
        {
            throw new ServerFolderAccessException(MissingFolderMessage);
        }

        // Browsing also hides Tuvima's own locations (its library, import and View folders) when they are reached
        // through a drive or the server root; they stay resolvable by id for validation only.
        var ownFolders = OwnFolderPaths();
        ownFolders.AddRange(OwnLocationRoots(configurationSnapshot));
        if (IsInsideOwnFolder(ownFolders, resolved.Path))
        {
            throw new ServerFolderAccessException("This folder belongs to Library and cannot be browsed.");
        }

        var search = request.Search?.Trim();

        List<ServerFolderEntryDto> directories;
        try
        {
            directories = Directory.EnumerateDirectories(resolved.Path)
                .Select(path => new DirectoryInfo(path))
                .Where(directory => !directory.Name.StartsWith('.')
                    && (directory.Attributes & (FileAttributes.Hidden | FileAttributes.System)) == 0
                    && !IsInsideOwnFolder(ownFolders, PhysicalOrFull(directory.FullName))
                    && !IsHidden(hidden, PhysicalOrFull(directory.FullName)))
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
            throw new ServerFolderAccessException("Library cannot read this folder from the server.", exception);
        }

        var relative = NormalizeRelative(request.RelativePath);
        return new BrowseServerFoldersResultDto
        {
            StorageLocation = ToDto(location, GetDriveDetails(resolved.RootPath)),
            RelativePath = relative,
            ParentRelativePath = ParentRelativePath(relative),
            DisplayPath = resolved.Path,
            Directories = directories,
            Files = request.IncludeFiles ? ListFiles(resolved, request.KindKey, search, ownFolders, hidden) : [],
        };
    }

    // The files of one folder for Add files (Libraries spec D8). Hidden and system files, links that leave the approved root and
    // Tuvima's own folders are left out, exactly as they are for folders. A file the kind cannot take is still listed, marked unsupported.
    private List<ServerFileEntryDto> ListFiles(
        ResolvedSelection folder, string? kindKey, string? search, IReadOnlyCollection<string> ownFolders, IReadOnlyCollection<string> hidden)
    {
        var supported = ExtensionsForKind(kindKey);
        var files = new List<ServerFileEntryDto>();
        try
        {
            foreach (var path in Directory.EnumerateFiles(folder.Path))
            {
                var info = new FileInfo(path);
                if (info.Name.StartsWith('.')
                    || (info.Attributes & (FileAttributes.Hidden | FileAttributes.System)) != 0
                    || (!string.IsNullOrWhiteSpace(search) && !info.Name.Contains(search, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                var physical = ResolveFilePhysicalPath(info, folder.RootPath);
                if (physical is null || IsInsideOwnFolder(ownFolders, physical) || IsHidden(hidden, physical))
                {
                    continue;
                }

                files.Add(new ServerFileEntryDto
                {
                    Name = info.Name,
                    RelativePath = Path.GetRelativePath(folder.RootPath, physical),
                    Path = physical,
                    SizeBytes = info.Exists ? info.Length : 0,
                    ModifiedAt = info.LastWriteTimeUtc,
                    Supported = supported is null || supported.Contains(info.Extension),
                });
            }
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException)
        {
            throw new ServerFolderAccessException("Library cannot read this folder from the server.", exception);
        }

        return files.OrderBy(file => file.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    // The extensions the kind's format takes; null when no kind was asked about (every file then reads as supported).
    private IReadOnlySet<string>? ExtensionsForKind(string? kindKey)
    {
        if (string.IsNullOrWhiteSpace(kindKey))
        {
            return null;
        }

        var kind = (kindSource?.GetKinds() ?? configuration.LoadLibraryKinds().Kinds)
            .FirstOrDefault(candidate => string.Equals(candidate.Key, kindKey.Trim(), StringComparison.OrdinalIgnoreCase));
        return kind is null ? new HashSet<string>(StringComparer.OrdinalIgnoreCase) : SupportedFileCounter.ExtensionsFor(_extensions, kind.Format);
    }

    // A file's real location, or null when it is a link that leaves the approved root (links are never followed out of it).
    private static string? ResolveFilePhysicalPath(FileInfo file, string rootPath)
    {
        if (file.LinkTarget is null)
        {
            return Path.GetFullPath(file.FullName);
        }

        var target = file.ResolveLinkTarget(true);
        if (target is null)
        {
            return null;
        }

        var full = Path.GetFullPath(target.FullName);
        return IsWithin(rootPath, full) ? full : null;
    }

    /// <summary>
    /// Resolves a file the person picked for Add files (Libraries spec D8): the path must be absolute, sit inside an administrator-approved
    /// storage location once every link is followed, exist as a file, and not be one of Library's own files or a hidden Vault file. A link
    /// that leaves the approved location is refused. <paramref name="refusal"/> says why, in plain words, when this answers false.
    /// </summary>
    public bool TryResolveApprovedFile(string? path, IReadOnlyCollection<string> hiddenRoots, out string physicalPath, out string refusal)
    {
        physicalPath = string.Empty;
        refusal = string.Empty;
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path.Trim()))
        {
            refusal = "Choose a file by its full path on the Library server.";
            return false;
        }

        try
        {
            var snapshot = configuration.LoadLibraries();
            var full = Path.GetFullPath(path.Trim());
            var directory = Path.GetDirectoryName(full);
            if (string.IsNullOrEmpty(directory))
            {
                refusal = MissingTypedFolderMessage;
                return false;
            }

            var location = FindLocationForManualPath(snapshot, directory);
            var resolvedDirectory = ResolveManualSelection(location, directory);
            var info = new FileInfo(Path.Combine(resolvedDirectory.Path, Path.GetFileName(full)));
            var hidden = NormalizeHiddenRoots(hiddenRoots);
            // A hidden Vault path must answer exactly like a path that does not exist (Videos spec D17).
            if (IsHidden(hidden, resolvedDirectory.Path) || IsHidden(hidden, info.FullName) || !info.Exists)
            {
                refusal = "That file is no longer there.";
                return false;
            }

            var physical = ResolveFilePhysicalPath(info, resolvedDirectory.RootPath);
            if (physical is null)
            {
                refusal = "This file is a link that leads outside the approved storage location.";
                return false;
            }

            var ownFolders = OwnFolderPaths();
            ownFolders.AddRange(OwnLocationRoots(snapshot));
            if (IsInsideOwnFolder(ownFolders, physical) || IsHidden(hidden, physical))
            {
                refusal = "That file is no longer there.";
                return false;
            }

            physicalPath = physical;
            return true;
        }
        catch (ServerFolderAccessException exception)
        {
            // Same wording as a missing file so a hidden Vault folder cannot be told apart from one that is not there.
            logger.LogDebug(exception, "Add files could not resolve a picked file");
            refusal = "That file is no longer there.";
            return false;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            logger.LogDebug(exception, "Add files could not read a picked file");
            refusal = "Library cannot read this file from the server.";
            return false;
        }
    }

    private static readonly IReadOnlyCollection<string> NoHiddenRoots = [];

    private const string MissingFolderMessage = "Library cannot access this folder from the server.";
    private const string MissingTypedFolderMessage = "Library cannot access this folder inside an approved server storage location.";

    public ServerFolderValidationResultDto Validate(ValidateServerFolderRequest request, CancellationToken cancellationToken = default) =>
        Validate(request, configuration.LoadLibraries(), cancellationToken);

    /// <summary>Validates a folder for a caller who may not know about <paramref name="hiddenRoots"/> (see the browse overload).</summary>
    public ServerFolderValidationResultDto Validate(ValidateServerFolderRequest request, IReadOnlyCollection<string> hiddenRoots, CancellationToken cancellationToken = default) =>
        Validate(request, configuration.LoadLibraries(), hiddenRoots, cancellationToken);

    public ServerFolderValidationResultDto Validate(ValidateServerFolderRequest request, LibrariesConfiguration configurationSnapshot, CancellationToken cancellationToken = default) =>
        Validate(request, configurationSnapshot, NoHiddenRoots, cancellationToken);

    public ServerFolderValidationResultDto Validate(
        ValidateServerFolderRequest request, LibrariesConfiguration configurationSnapshot, IReadOnlyCollection<string> hiddenRoots, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(hiddenRoots);
        var hidden = NormalizeHiddenRoots(hiddenRoots);
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
            if (IsHidden(hidden, resolved.Path))
            {
                // A hidden (Vault) folder answers exactly as a folder that is not there.
                resolved = null;
                throw new ServerFolderAccessException(string.IsNullOrWhiteSpace(request.ManualPath) ? MissingFolderMessage : MissingTypedFolderMessage);
            }
        }
        catch (ServerFolderAccessException exception)
        {
            // A typed path that is simply not there gets its own code, so the picker can say so in plain words.
            var missing = !string.IsNullOrWhiteSpace(request.ManualPath)
                && (!TypedFolderExists(request.ManualPath) || IsHidden(hidden, PhysicalOrFull(request.ManualPath.Trim())));
            issues.Add(Issue(missing ? "not_found" : "invalid_path", exception.Message));
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
            issues.Add(Issue("read_required", "Library cannot read this folder from the server."));
        }

        if (ServerFolderSelectionModes.RequiresWrite(request.SelectionMode) && !hasWrite)
        {
            issues.Add(Issue("write_required", "This folder must be writable for the selected managed mode."));
        }

        if (request.SelectionMode == ServerFolderSelectionModes.ToolLocation)
        {
            // A tool's folder may be anywhere it was installed (often a folder named bin, or under Program Files) and
            // may sit beside a library, so only the Library's own folders are refused.
            AddOwnFolderIssue(resolved, issues);
        }
        else
        {
            AddProtectedPathIssue(resolved, location, request.SelectionMode, issues);
            AddConfiguredPathIssues(configurationSnapshot, resolved, request.CurrentSourceId, issues);
        }

        var (availableBytes, fileSystem) = GetDriveDetails(resolved.Path);

        // The count is only worth making for a folder that can be used: a refused folder shows its refusal, not a number.
        var canSelect = issues.All(issue => !string.Equals(issue.Severity, "error", StringComparison.OrdinalIgnoreCase));
        var count = canSelect && exists && hasRead ? CountSupportedFiles(request.KindKey, resolved.Path, cancellationToken) : null;
        return new ServerFolderValidationResultDto
        {
            StorageLocationId = location.Id,
            RelativePath = RelativeFor(location, request.ManualPath, request.RelativePath),
            Path = resolved.Path,
            Exists = exists,
            HasRead = hasRead,
            HasWrite = hasWrite,
            ContainsFiles = exists && hasRead && ProbeContainsFiles(resolved.Path),
            LooksLikePhoneFootage = exists && hasRead && videoNaming is not null && VideoPhoneFootageDetector.Looks(resolved.Path, videoNaming.Current.PhoneFootage),
            AvailableBytes = availableBytes,
            FileSystem = fileSystem,
            CanSelect = canSelect,
            SupportedFileCount = count?.Count,
            SupportedFileCountCapped = count?.Capped ?? false,
            SupportedFileCountUnavailable = count?.Unavailable ?? false,
            SupportedFileKindLabel = count?.KindLabel,
            Issues = issues,
        };
    }

    // The supported-file count for the kind the folder is for, using the extensions ingestion uses for that kind's format. A kind with
    // no format (View) or an unknown kind has nothing to count, so the answer is simply left out.
    private SupportedFileCount? CountSupportedFiles(string? kindKey, string path, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(kindKey))
        {
            return null;
        }

        var kind = (kindSource?.GetKinds() ?? configuration.LoadLibraryKinds().Kinds)
            .FirstOrDefault(candidate => string.Equals(candidate.Key, kindKey.Trim(), StringComparison.OrdinalIgnoreCase));
        var label = SupportedFileCounter.KindLabelFor(kind?.Format);
        if (kind is null || label is null)
        {
            return null;
        }

        return SupportedFileCounter.Count(path, SupportedFileCounter.ExtensionsFor(_extensions, kind.Format), label, cancellationToken);
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
            throw new ServerFolderAccessException("Enter an absolute path that is visible to the Library server.");
        }

        var candidate = Path.TrimEndingDirectorySeparator(Path.GetFullPath(manualPath.Trim()));
        return EffectiveLocations(configurationSnapshot)
            .Select(location => new { Location = location, Valid = TryResolveLocationRoot(location, out var root, out _), Root = root })
            .Where(item => item.Valid && IsWithin(item.Root, candidate))
            .OrderByDescending(item => item.Root.Length)
            .Select(item => item.Location)
            .FirstOrDefault()
            ?? throw new ServerFolderAccessException("This path is outside the server storage locations approved for Library.");
    }

    private static bool TypedFolderExists(string manualPath)
    {
        try
        {
            return Directory.Exists(Path.GetFullPath(manualPath.Trim()));
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or NotSupportedException or UnauthorizedAccessException)
        {
            return false;
        }
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
            throw new ServerFolderAccessException("Library cannot access this folder inside an approved server storage location.");
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
            throw new ServerFolderAccessException("Library cannot access this folder from the server.");
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

    internal const string NeutralLibraryLabel = "another library";

    // How a library reads in a refusal: by name in quotes (the library "Movies"), or just "another library" where the name must not be given.
    private static string LibraryLabel(string? name) =>
        string.IsNullOrWhiteSpace(name) || string.Equals(name, NeutralLibraryLabel, StringComparison.Ordinal)
            ? NeutralLibraryLabel
            : $"the library \"{name}\"";

    private static bool IsVaultEntryName(string? name) =>
        string.Equals(name?.Trim(), MediaEngine.Api.Services.Vault.VaultFolderAttachmentService.LibraryName, StringComparison.OrdinalIgnoreCase);

    private static void AddConfiguredPathIssues(
        LibrariesConfiguration configurationSnapshot,
        ResolvedSelection selected,
        string? currentSourceId,
        List<ServerFolderValidationIssueDto> issues)
    {
        // The Vault's settings entry is never named in a folder message: it reads as "another library" (Videos spec D17, D45).
        var configured = configurationSnapshot.Libraries
            .SelectMany(library => library.Sources.Select(source => (source.Id, source.Path, Name: IsVaultEntryName(library.Name) ? NeutralLibraryLabel : library.Name)))
            .Select(source => (source.Id, source.Path, Label: LibraryLabel(source.Name), source.Name))
            .ToList();

        // The import folder is kept apart from every library folder, so it is never offered as one either.
        if (ImportFolderRules.Find(configurationSnapshot) is { } importFolder)
        {
            configured.Add((ImportFolderRules.LocationId, importFolder.Path, "the import folder", "the import folder"));
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
                issues.Add(new ServerFolderValidationIssueDto { Code = "already_configured", Message = $"This folder is already used by {source.Label}. Choose a different folder.", LibraryName = source.Name });
            }
            else if (IsWithin(configuredPath, selected.Path))
            {
                issues.Add(new ServerFolderValidationIssueDto { Code = "inside_configured_source", Message = $"This folder is inside a folder that {source.Label} already uses. Choose a folder that does not overlap it.", LibraryName = source.Name });
            }
            else if (!IsDriveRoot(selected.Path) && IsWithin(selected.Path, configuredPath))
            {
                // Pointing at a whole drive is allowed even though it holds other libraries' folders.
                issues.Add(new ServerFolderValidationIssueDto { Code = "contains_configured_source", Message = $"This folder holds a folder that {source.Label} already uses. Choose a folder that does not overlap it.", LibraryName = source.Name });
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
            var own = OwnFolderPaths();
            if (RelativeSegments(selection.Path).Any(segment => segment == ".data")
                || own.Any(path => IsWithin(path, selection.Path) || (!IsDriveRoot(selection.Path) && IsWithin(selection.Path, path))))
            {
                issues.Add(Issue("protected_path", "This folder is reserved for the Library's own files."));
            }

            if (IsSystemPath(selection.Path))
            {
                issues.Add(Issue("system_path", "This is an operating-system folder and cannot be used for a library."));
            }

            if (PathEquals(selection.Path, selection.RootPath) && ServerFolderSelectionModes.RequiresWrite(selectionMode))
            {
                issues.Add(Issue("drive_root_managed", "Library cannot manage a whole drive. Choose a folder on it, or leave the drive as read-only."));
            }

            return;
        }

        // A typed path under a configured location (such as the managed library folder) is matched to that location
        // rather than to the whole drive, so it gets the same own-folder check here instead of skipping it. An own
        // folder that contains the location itself (the data folder around the managed library folder) does not
        // count: only the own folders inside the location do.
        var relative = Path.GetRelativePath(selection.RootPath, selection.Path);
        var ownInsideLocation = OwnFolderPaths().Where(own => !IsWithin(own, selection.RootPath));
        if (RelativeSegments(relative).Any(segment => ProtectedSegments.Contains(segment, StringComparer.OrdinalIgnoreCase))
            || IsInsideOwnFolder(ownInsideLocation, selection.Path))
        {
            issues.Add(Issue("protected_path", "This folder is reserved for the Library's own files."));
        }
    }

    private void AddOwnFolderIssue(ResolvedSelection selection, List<ServerFolderValidationIssueDto> issues)
    {
        var own = OwnFolderPaths();
        if (RelativeSegments(selection.Path).Any(segment => segment == ".data")
            || own.Any(path => IsWithin(path, selection.Path) || (!IsDriveRoot(selection.Path) && IsWithin(selection.Path, path))))
        {
            issues.Add(Issue("protected_path", "This folder is reserved for the Library's own files."));
        }
    }

    /// <summary>Tuvima's own folders on this machine, as physical paths: configuration, data, logs, backups, keys and models.</summary>
    private List<string> OwnFolderPaths()
    {
        var ownFolders = new List<string?>
        {
            configuration.ConfigDirectoryPath,
            Environment.GetEnvironmentVariable("TUVIMA_CONFIG_DIR"),
            Environment.GetEnvironmentVariable("TUVIMA_DATA_PROTECTION_DIR"),
            Environment.GetEnvironmentVariable("TUVIMA_BACKUP_DIR"),
            Environment.GetEnvironmentVariable("TUVIMA_LOG_DIR") ?? "logs",
            Environment.GetEnvironmentVariable("TUVIMA_MODELS_DIR"),
        };
        var databasePath = Environment.GetEnvironmentVariable("TUVIMA_DB_PATH");
        if (!string.IsNullOrWhiteSpace(databasePath))
        {
            ownFolders.Add(Path.GetDirectoryName(Path.GetFullPath(databasePath)));
        }

        var core = configuration.LoadCore();
        ownFolders.Add(core.DataRoot);
        ownFolders.Add(new TuvimaDataPaths().Root);
        if (!string.IsNullOrWhiteSpace(core.LibraryRoot))
        {
            ownFolders.Add(Path.Combine(core.LibraryRoot, ".data"));
        }

        return ownFolders
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => PhysicalOrFull(path!))
            .ToList();
    }

    /// <summary>The roots of the locations that are Tuvima's own (library, import folder, View), where they can be resolved.</summary>
    private static IEnumerable<string> OwnLocationRoots(LibrariesConfiguration snapshot) =>
        snapshot.StorageLocations
            .Where(location => TuvimaOwnFolders.IsOwnLocation(snapshot, location.Id))
            .Select(location => TryResolveLocationRoot(location, out var root, out _) ? root : null)
            .Where(root => root is not null)
            .Select(root => root!);

    private static List<string> NormalizeHiddenRoots(IReadOnlyCollection<string> roots) =>
        roots.Where(root => !string.IsNullOrWhiteSpace(root) && Path.IsPathFullyQualified(root.Trim()))
            .Select(root => PhysicalOrFull(root.Trim()))
            .ToList();

    private static bool IsHidden(IEnumerable<string> hiddenRoots, string path) =>
        hiddenRoots.Any(root => IsWithin(root, Path.TrimEndingDirectorySeparator(Path.GetFullPath(path))));

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
