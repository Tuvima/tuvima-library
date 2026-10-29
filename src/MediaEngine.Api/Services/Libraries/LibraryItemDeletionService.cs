using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using MediaEngine.Contracts.Items;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Models;
using MediaEngine.Domain.Services;
using MediaEngine.Ingestion.Contracts;
using MediaEngine.Ingestion.Models;
using MediaEngine.Ingestion.Organise;
using MediaEngine.Ingestion.Services;
using MediaEngine.Storage.Services;
using Microsoft.Extensions.Logging;

namespace MediaEngine.Api.Services.Libraries;

/// <summary>Why a deletion request was turned away before any plan was made.</summary>
public enum LibraryItemDeletionRefusalKind
{
    /// <summary>The request itself is not acceptable (wrong number of ids, a group instead of a title, a catalog-only entry).</summary>
    Validation,

    /// <summary>One or more ids are not titles in the library.</summary>
    NotFound,

    /// <summary>The person confirmed a plan that is out of date: something changed since the preview. Nothing was changed; the refusal carries the fresh preview.</summary>
    Changed,
}

/// <summary>
/// A typed refusal. <see cref="WorkIds"/> lists the ids the refusal is about (the unknown ones for <see cref="LibraryItemDeletionRefusalKind.NotFound"/>).
/// <see cref="FreshPreview"/> is set only for <see cref="LibraryItemDeletionRefusalKind.Changed"/>.
/// </summary>
public sealed record LibraryItemDeletionRefusal(
    LibraryItemDeletionRefusalKind Kind,
    string Message,
    IReadOnlyList<Guid> WorkIds,
    LibraryItemDeletionPreview? FreshPreview = null);

/// <summary>Either the answer or a typed refusal.</summary>
public sealed class LibraryItemDeletionResult<T> where T : class
{
    private LibraryItemDeletionResult(T? value, LibraryItemDeletionRefusal? refusal)
    {
        Value = value;
        Refusal = refusal;
    }

    public T? Value { get; }

    public LibraryItemDeletionRefusal? Refusal { get; }

    public bool IsSuccess => Value is not null;

    public static LibraryItemDeletionResult<T> Ok(T value) => new(value, null);

    public static LibraryItemDeletionResult<T> Refused(LibraryItemDeletionRefusal refusal) => new(null, refusal);
}

/// <summary>
/// One file in a deletion plan. <see cref="FullPath"/> is the real disk path and never leaves the Engine; the wire row only carries
/// the file name and a folder label.
/// </summary>
/// <param name="Policy">The delete policy built for the file's source folder; null when no library folder contains the file or it is missing.</param>
public sealed record LibraryItemDeletionPlannedFile(
    Guid WorkId,
    Guid? AssetId,
    bool IsSidecar,
    string FullPath,
    string Outcome,
    string? Reason,
    long? SizeBytes,
    FileSourceMutationPolicy? Policy);

/// <summary>
/// Everything a deletion needs, worked out once: the titles and their files, the related facts, the wire preview and its
/// fingerprint. The delete action recomputes a plan, compares <see cref="Preview"/>.Fingerprint with what the person confirmed,
/// then acts only on the files whose outcome is <see cref="LibraryItemDeletionOutcomes.Delete"/>.
/// </summary>
public sealed record LibraryItemDeletionPlan(
    IReadOnlyList<Guid> WorkIds,
    IReadOnlyList<LibraryItemDeletionWork> Works,
    IReadOnlyList<LibraryItemDeletionPlannedFile> Files,
    LibraryItemDeletionRelatedFacts Related,
    LibraryItemDeletionPreview Preview);

/// <summary>
/// Works out what deleting titles from the library would do (<see cref="PreviewAsync"/>, which changes nothing) and does it
/// (<c>DeleteAsync</c>, in the other part of this class). Whether a file may be deleted is decided only by the shared source-file
/// safety gate; the folder's management mode is read only to choose the words used for a file that stays.
/// </summary>
public sealed partial class LibraryItemDeletionService(
    ILibraryItemDeletionRepository repository,
    ILibraryFolderResolver folders,
    ISourceMutationPolicyGate gate,
    IMediaTypeExtensionCatalog extensions,
    IExcludedSourceFileRepository excludedFiles,
    IExcludedSourceFileFilter excludedFileFilter,
    WorkHierarchyMaintenanceService hierarchy,
    ISystemActivityRepository activity,
    IEventPublisher events,
    AssetPathService assetPaths,
    ILogger<LibraryItemDeletionService> logger)
{
    public const int MaxWorks = 200;

    private RecycleBinService? _bin;

    /// <summary>The recycle bin for library types that have it switched on.</summary>
    private RecycleBinService Bin => _bin ??= new RecycleBinService(assetPaths.DataRoot, logger);

    public const string OutsideLibraryReason = "This file is not inside any library folder Library manages.";
    public const string MissingReason = "This file is no longer on disk.";
    public const string UsedElsewhereReason = "Another item in your library uses this same file.";
    private const string GroupRefusal = "Only individual titles can be deleted.";
    private const string CatalogOnlyRefusal = "This title isn't in your library as a file, so there's nothing to delete.";
    private const string LabelSeparator = " › ";

    private static readonly StringComparer PathComparer =
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    public async Task<LibraryItemDeletionResult<LibraryItemDeletionPreview>> PreviewAsync(
        IReadOnlyList<Guid> workIds,
        CancellationToken ct = default)
    {
        var plan = await BuildPlanAsync(workIds, ct).ConfigureAwait(false);
        return plan.IsSuccess
            ? LibraryItemDeletionResult<LibraryItemDeletionPreview>.Ok(plan.Value!.Preview)
            : LibraryItemDeletionResult<LibraryItemDeletionPreview>.Refused(plan.Refusal!);
    }

    /// <summary>Builds the full plan. The delete action calls this again just before acting and compares fingerprints.</summary>
    public async Task<LibraryItemDeletionResult<LibraryItemDeletionPlan>> BuildPlanAsync(
        IReadOnlyList<Guid>? workIds,
        CancellationToken ct = default,
        bool vaultItems = false)
    {
        var requested = (workIds ?? []).Distinct().ToList();
        if (requested.Count == 0 || requested.Contains(Guid.Empty))
        {
            return Refused(LibraryItemDeletionRefusalKind.Validation, "Choose at least one item to delete.", []);
        }

        if (requested.Count > MaxWorks)
        {
            return Refused(
                LibraryItemDeletionRefusalKind.Validation,
                $"Up to {MaxWorks.ToString(CultureInfo.InvariantCulture)} items can be deleted at a time.",
                []);
        }

        var found = await repository.GetWorksAsync(requested, ct, vaultItems).ConfigureAwait(false);
        var foundIds = found.Select(work => work.WorkId).ToHashSet();
        var unknown = requested.Where(id => !foundIds.Contains(id)).ToList();
        if (unknown.Count > 0)
        {
            return Refused(LibraryItemDeletionRefusalKind.NotFound, "Some of these items are not in your library.", unknown);
        }

        var groups = found
            .Where(work => string.Equals(work.WorkKind, "parent", StringComparison.OrdinalIgnoreCase))
            .Select(work => work.WorkId)
            .ToList();
        if (groups.Count > 0)
        {
            return Refused(LibraryItemDeletionRefusalKind.Validation, GroupRefusal, groups);
        }

        var catalogOnly = found.Where(work => work.IsCatalogOnly).Select(work => work.WorkId).ToList();
        if (catalogOnly.Count > 0)
        {
            return Refused(LibraryItemDeletionRefusalKind.Validation, CatalogOnlyRefusal, catalogOnly);
        }

        // Keep the order the caller asked for so the preview reads the same way every time.
        var order = requested.Select((id, index) => (id, index)).ToDictionary(pair => pair.id, pair => pair.index);
        var works = found.OrderBy(work => order[work.WorkId]).ToList();

        var related = await repository.GetRelatedFactsAsync(requested, ct).ConfigureAwait(false);
        var mediaExtensions = extensions.GetAllMediaExtensions();

        // 1. Every media file of every requested title.
        var allAssetPaths = works.SelectMany(work => work.Assets).Select(asset => asset.FilePath)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .ToList();
        var usedElsewhere = await repository.FindPathsUsedOutsideRequestAsync(requested, allAssetPaths, ct).ConfigureAwait(false);
        var requestedAssetPaths = new HashSet<string>(PathComparer);
        foreach (var path in allAssetPaths)
        {
            requestedAssetPaths.Add(NormalizePath(path));
        }

        var mediaRows = new List<MediaRow>();
        var seenMedia = new HashSet<string>(PathComparer);
        foreach (var work in works)
        {
            foreach (var asset in work.Assets.OrderBy(asset => asset.FilePath, StringComparer.OrdinalIgnoreCase))
            {
                ct.ThrowIfCancellationRequested();
                if (!seenMedia.Add(NormalizePath(asset.FilePath)))
                {
                    continue;
                }

                mediaRows.Add(new MediaRow(work.WorkId, asset, DecideMedia(asset, usedElsewhere)));
            }
        }

        // 2. Sidecars of the media files that will be deleted. A folder-wide picture or metadata file goes only when no media
        // file that stays on disk (or belongs to something else) is left in its folder.
        var deletedMediaPaths = new HashSet<string>(PathComparer);
        foreach (var row in mediaRows.Where(row => row.Decision.Outcome == LibraryItemDeletionOutcomes.Delete))
        {
            deletedMediaPaths.Add(NormalizePath(row.Asset.FilePath));
        }

        var candidates = new List<SidecarCandidate>();
        var seenSidecars = new HashSet<string>(PathComparer);
        foreach (var row in mediaRows.Where(row => row.Decision.Outcome == LibraryItemDeletionOutcomes.Delete))
        {
            ct.ThrowIfCancellationRequested();
            var folder = Path.GetDirectoryName(row.Asset.FilePath);

            // Sidecars are matched by base name only, so a media file with the same name but another extension (Movie.mkv beside
            // Movie.mp4) shares them. They go only when every such file is going too.
            if (AnotherMediaFileWithTheSameNameStays(row.Asset.FilePath, mediaExtensions, deletedMediaPaths))
            {
                continue;
            }

            var includeFolderLevel = !string.IsNullOrEmpty(folder)
                && EveryMediaFileInFolderIsBeingDeleted(folder, mediaExtensions, deletedMediaPaths);
            var sidecars = SidecarFiles.Find(row.Asset.FilePath, mediaExtensions)
                .Concat(SidecarFiles.FindArtwork(row.Asset.FilePath, mediaExtensions, includeFolderLevel));
            foreach (var sidecar in sidecars)
            {
                var normalized = NormalizePath(sidecar.Path);
                // A file that is itself a media asset of the request already has its own row.
                if (requestedAssetPaths.Contains(normalized) || !seenSidecars.Add(normalized))
                {
                    continue;
                }

                candidates.Add(new SidecarCandidate(row, sidecar.Path));
            }
        }

        var sidecarsUsedElsewhere = candidates.Count == 0
            ? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            : await repository.FindPathsUsedOutsideRequestAsync(
                requested, candidates.Select(candidate => candidate.FilePath).ToList(), ct).ConfigureAwait(false);

        var sidecarRows = new List<(MediaRow Owner, LibraryItemDeletionPlannedFile File, string Label, string LibraryName)>();
        foreach (var candidate in candidates)
        {
            ct.ThrowIfCancellationRequested();
            // A sidecar that is the file of a title outside the request is never listed at all.
            if (sidecarsUsedElsewhere.Contains(candidate.FilePath))
            {
                continue;
            }

            var decision = DecideSidecar(candidate.Owner.Decision, candidate.FilePath);
            sidecarRows.Add((
                candidate.Owner,
                new LibraryItemDeletionPlannedFile(
                    candidate.Owner.WorkId, null, true, candidate.FilePath, decision.Outcome, decision.Reason, decision.SizeBytes, decision.Policy),
                LabelFor(decision.Resolved, candidate.FilePath),
                decision.Resolved?.Library.Name ?? string.Empty));
        }

        // 3. Assemble the plan: per title, its media files in order, each followed by its own sidecars.
        var planned = new List<LibraryItemDeletionPlannedFile>();
        var wire = new List<LibraryItemDeletionFileDto>();
        foreach (var row in mediaRows)
        {
            var file = new LibraryItemDeletionPlannedFile(
                row.WorkId, row.Asset.AssetId, false, row.Asset.FilePath,
                row.Decision.Outcome, row.Decision.Reason, row.Decision.SizeBytes, row.Decision.Policy);
            planned.Add(file);
            wire.Add(ToDto(file, row.Decision.Resolved?.Library.Name ?? string.Empty, LabelFor(row.Decision.Resolved, row.Asset.FilePath)));
            foreach (var sidecar in sidecarRows.Where(sidecar => ReferenceEquals(sidecar.Owner, row)))
            {
                planned.Add(sidecar.File);
                wire.Add(ToDto(sidecar.File, sidecar.LibraryName, sidecar.Label));
            }
        }

        var items = works.Select(work => new LibraryItemDeletionItemDto
        {
            WorkId = work.WorkId,
            Title = TitleOf(work),
            MediaType = work.MediaType,
            FileCount = work.Assets.Count,
        }).ToList();

        var collections = related.CuratedCollections
            .OrderBy(collection => collection.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(collection => collection.Name, StringComparer.Ordinal)
            .ThenBy(collection => collection.CollectionId)
            .Select(collection => new LibraryItemDeletionCollectionDto
            {
                CollectionId = collection.CollectionId,
                Name = collection.Name,
                ItemsInRequest = collection.ItemsInRequest,
                WillBeDeleted = collection.WillBeDeleted,
            }).ToList();

        var preview = new LibraryItemDeletionPreview
        {
            Items = items,
            Files = wire,
            CuratedCollections = collections,
            ProfilesWithProgress = related.ProfilesWithPersonalState,
            ProcessingRunning = related.ProcessingRunning,
            BytesFreed = planned.Where(file => file.Outcome == LibraryItemDeletionOutcomes.Delete).Sum(file => file.SizeBytes ?? 0),
            GoesToRecycleBin = planned.Any(file => file.Outcome == LibraryItemDeletionOutcomes.Delete
                && !string.IsNullOrWhiteSpace(file.FullPath)
                && folders.ResolveSourceForPath(file.FullPath)?.Library.RecycleBin == true),
            Fingerprint = ComputeFingerprint(requested, planned, collections, related.ProfilesWithPersonalState),
        };

        return LibraryItemDeletionResult<LibraryItemDeletionPlan>.Ok(
            new LibraryItemDeletionPlan(requested, works, planned, related, preview));
    }

    private Decision DecideMedia(LibraryItemDeletionAsset asset, IReadOnlySet<string> usedElsewhere)
    {
        var filePath = asset.FilePath;
        var resolved = string.IsNullOrWhiteSpace(filePath) ? null : folders.ResolveSourceForPath(filePath);

        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
        {
            return new Decision(LibraryItemDeletionOutcomes.KeepMissing, MissingReason, null, resolved, null);
        }

        var size = LengthOf(filePath);
        if (usedElsewhere.Contains(filePath))
        {
            return new Decision(LibraryItemDeletionOutcomes.KeepProtected, UsedElsewhereReason, size, resolved, null);
        }

        return Evaluate(resolved, filePath, size);
    }

    private Decision DecideSidecar(Decision owner, string sidecarPath)
    {
        var size = File.Exists(sidecarPath) ? LengthOf(sidecarPath) : null;
        return Evaluate(owner.Resolved, sidecarPath, size);
    }

    /// <summary>Asks the safety gate. The answer to "may this be deleted" comes only from the gate.</summary>
    private Decision Evaluate(ResolvedLibrarySource? resolved, string filePath, long? size)
    {
        if (resolved is null)
        {
            return new Decision(LibraryItemDeletionOutcomes.KeepProtected, OutsideLibraryReason, size, null, null);
        }

        // Built exactly as the ingestion pipeline and the Import filer build it when they delete a file.
        var policy = FileSourceMutationPolicyFactory.Create(
            resolved.Library,
            resolved.Source,
            allowDelete: true,
            isMergeTarget: false,
            isMergeJob: false);
        var verdict = gate.Evaluate(new SourceMutationRequest
        {
            Source = policy,
            Mutation = SourceMutationKind.Delete,
            Path = filePath,
        });
        if (verdict.Allowed)
        {
            return new Decision(LibraryItemDeletionOutcomes.Delete, null, size, resolved, policy);
        }

        // The management mode only picks the label; it never decides.
        var outcome = policy.ManagementMode == FileSourceManagementMode.ExistingLibrary
            ? LibraryItemDeletionOutcomes.KeepAsIs
            : LibraryItemDeletionOutcomes.KeepProtected;
        return new Decision(outcome, verdict.Reason ?? "The safety check refused to delete this file.", size, resolved, policy);
    }

    /// <summary>
    /// True when the folder holds another media file with the same base name (any media extension) that is not being deleted.
    /// Fails closed: a folder that cannot be listed counts as true, so no sidecar is listed.
    /// </summary>
    private static bool AnotherMediaFileWithTheSameNameStays(
        string mediaPath,
        IReadOnlySet<string> mediaExtensions,
        IReadOnlySet<string> deletedMediaPaths)
    {
        var folder = Path.GetDirectoryName(mediaPath);
        var stem = Path.GetFileNameWithoutExtension(mediaPath);
        if (string.IsNullOrEmpty(folder) || string.IsNullOrEmpty(stem))
        {
            return true;
        }

        try
        {
            foreach (var file in Directory.EnumerateFiles(folder))
            {
                if (mediaExtensions.Contains(Path.GetExtension(file))
                    && string.Equals(Path.GetFileNameWithoutExtension(file), stem, StringComparison.OrdinalIgnoreCase)
                    && !deletedMediaPaths.Contains(NormalizePath(file)))
                {
                    return true;
                }
            }

            return false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A folder that cannot be listed fails closed: its sidecars are left alone.
            _ = ex;
            return true;
        }
    }

    private static bool EveryMediaFileInFolderIsBeingDeleted(
        string folder,
        IReadOnlySet<string> mediaExtensions,
        IReadOnlySet<string> deletedMediaPaths)
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(folder))
            {
                if (mediaExtensions.Contains(Path.GetExtension(file)) && !deletedMediaPaths.Contains(NormalizePath(file)))
                {
                    return false;
                }
            }

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A folder that cannot be listed fails closed: its folder-wide files are left alone, only name-matched sidecars go.
            _ = ex;
            return false;
        }
    }

    private static long? LengthOf(string filePath)
    {
        try
        {
            return new FileInfo(filePath).Length;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The file vanished or is unreadable between the existence check and now; the size is simply unknown.
            _ = ex;
            return null;
        }
    }

    private static string LabelFor(ResolvedLibrarySource? resolved, string filePath)
    {
        var folder = Path.GetDirectoryName(filePath);
        if (resolved is null)
        {
            // No library folder contains the file, so only the name of its own folder is shown, never the full path.
            return string.IsNullOrEmpty(folder) ? string.Empty : Path.GetFileName(Path.TrimEndingDirectorySeparator(folder));
        }

        var library = resolved.Library.Name;
        if (string.IsNullOrEmpty(folder))
        {
            return library;
        }

        var relative = Path.GetRelativePath(resolved.Source.Path, folder);
        if (relative is "." or "" || relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative))
        {
            return library;
        }

        var segments = relative.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);
        return segments.Length == 0 ? library : library + LabelSeparator + string.Join(LabelSeparator, segments);
    }

    private static LibraryItemDeletionFileDto ToDto(LibraryItemDeletionPlannedFile file, string libraryName, string folderLabel) => new()
    {
        WorkId = file.WorkId,
        AssetId = file.AssetId,
        LibraryName = libraryName,
        FolderLabel = folderLabel,
        FileName = Path.GetFileName(file.FullPath),
        SizeBytes = file.SizeBytes,
        Outcome = file.Outcome,
        Reason = file.Reason,
        IsSidecar = file.IsSidecar,
    };

    private static string TitleOf(LibraryItemDeletionWork work)
    {
        if (!string.IsNullOrWhiteSpace(work.Title))
        {
            return work.Title;
        }

        var name = work.Assets
            .Select(asset => Path.GetFileNameWithoutExtension(asset.FilePath))
            .FirstOrDefault(stem => !string.IsNullOrWhiteSpace(stem));
        return name ?? "Untitled item";
    }

    /// <summary>
    /// Lowercase hex SHA-256 over a canonical, ordinal-sorted description of the plan: the titles, every file with its full path,
    /// outcome and size, every hand-picked collection with its count and fate, and the number of profiles with progress.
    /// Whether processing is running and any timestamps are left out because they change on their own.
    /// </summary>
    private static string ComputeFingerprint(
        IReadOnlyList<Guid> workIds,
        IReadOnlyList<LibraryItemDeletionPlannedFile> files,
        IReadOnlyList<LibraryItemDeletionCollectionDto> collections,
        int profilesWithProgress)
    {
        var lines = new List<string>();
        lines.AddRange(workIds
            .Select(id => "work|" + id.ToString("D", CultureInfo.InvariantCulture))
            .Order(StringComparer.Ordinal));
        lines.AddRange(files
            .Select(file => string.Join('|', "file", file.FullPath, file.Outcome,
                file.SizeBytes?.ToString(CultureInfo.InvariantCulture) ?? "-"))
            .Order(StringComparer.Ordinal));
        lines.AddRange(collections
            .Select(collection => string.Join('|', "collection",
                collection.CollectionId.ToString("D", CultureInfo.InvariantCulture),
                collection.ItemsInRequest.ToString(CultureInfo.InvariantCulture),
                collection.WillBeDeleted ? "1" : "0"))
            .Order(StringComparer.Ordinal));
        lines.Add("profiles|" + profilesWithProgress.ToString(CultureInfo.InvariantCulture));

        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', lines)));
        return Convert.ToHexStringLower(bytes);
    }

    private static string NormalizePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return string.Empty;
        }

        try
        {
            return Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            // An unusable path cannot be normalised; compare it as written.
            _ = ex;
            return path;
        }
    }

    private static LibraryItemDeletionResult<LibraryItemDeletionPlan> Refused(
        LibraryItemDeletionRefusalKind kind,
        string message,
        IReadOnlyList<Guid> ids) =>
        LibraryItemDeletionResult<LibraryItemDeletionPlan>.Refused(new LibraryItemDeletionRefusal(kind, message, ids));

    private sealed record Decision(
        string Outcome,
        string? Reason,
        long? SizeBytes,
        ResolvedLibrarySource? Resolved,
        FileSourceMutationPolicy? Policy);

    private sealed record MediaRow(Guid WorkId, LibraryItemDeletionAsset Asset, Decision Decision);

    private sealed record SidecarCandidate(MediaRow Owner, string FilePath);
}
