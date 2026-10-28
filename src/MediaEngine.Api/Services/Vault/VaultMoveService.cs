using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MediaEngine.Contracts.Vault;
using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Contracts;
using MediaEngine.Ingestion.Contracts;
using MediaEngine.Ingestion.Organise;
using MediaEngine.Storage.Configuration;

namespace MediaEngine.Api.Services.Vault;

/// <summary>What a move plan found, with the pieces the move itself needs. Nothing in it is sent to the Dashboard as is.</summary>
internal sealed record VaultMovePlan(
    string State,
    string? Message,
    string Direction,
    string KindKey,
    LibraryFolderConfig? SourceLibrary,
    LibrarySourceConfig? Source,
    LibraryFolderConfig? DestinationLibrary,
    LibrarySourceConfig? Destination,
    IReadOnlyList<VaultMoveCandidate> Movable,
    int Skipped,
    long Bytes,
    bool OriginalsStay,
    string Fingerprint)
{
    public VaultMovePreviewDto ToPreview(string? message = null) => new()
    {
        State = State,
        Message = message ?? Message,
        Direction = Direction,
        Items = Movable.Count,
        Skipped = Skipped,
        Bytes = Bytes,
        SourceName = SourceLibrary?.Name ?? string.Empty,
        DestinationName = DestinationLibrary?.Name ?? string.Empty,
        OriginalsStay = OriginalsStay,
        Fingerprint = Fingerprint,
    };
}

/// <summary>The sidecars (subtitles, notes, named pictures) that travel with a title's file, saved on the item so a restart finishes the same copy.</summary>
internal sealed record VaultMoveSidecar(string Source, string Staging, string Final);

/// <summary>Why a move did not start.</summary>
public enum VaultMoveStartStatus
{
    Started,
    NotAllowed,
    Changed,
}

/// <summary>The answer to starting a move. <see cref="Preview"/> is set when nothing could start, so the Dashboard can show what is true now.</summary>
public sealed record VaultMoveStartOutcome(VaultMoveStartStatus Status, Guid? JobId = null, VaultMovePreviewDto? Preview = null);

/// <summary>
/// Plans and starts Move to Vault and Move out (Vault sections D30, D31, D33, D36 to D38). A move takes the titles of one folder: it copies each
/// file into a Managed folder of the other side (the Vault library of the same content type, or the ordinary library of it), checks the copy
/// byte for byte, then re-adds the title there and removes the old one, carrying across what each profile had done with it. The job and every item
/// are saved before any file is touched. Move out goes only to a folder Library manages, never into a Read-only one (D38). Nothing here names a
/// title, and no answer or log line names a folder or a path.
/// </summary>
public sealed class VaultMoveService(
    IConfigurationLoader configuration,
    IVaultLibraryRepository vaultLibraries,
    IVaultMoveRepository moves,
    IMediaTypeExtensionCatalog extensions,
    VaultAdministrationService administration,
    VaultMoveSignal signal,
    TimeProvider clock,
    ILogger<VaultMoveService> logger)
{
    public const string UnknownFolderMessage = "This folder isn't part of your libraries.";
    public const string WrongSideMessage = "This folder isn't on the right side of the Vault for that move.";
    public const string BusyMessage = "This folder is being organised, merged or moved right now. Wait for that to finish first.";
    public const string NothingMessage = "There's nothing in this folder that can be moved.";
    public const string NoDestinationMessage = "There is no matching library on the other side yet.";
    public const string NeedsManagedMessage = "Moving needs a folder that Library manages on the other side, and it has none yet.";
    public const string NotEnoughSpaceMessage = "There isn't enough free space in the destination to hold the copies.";
    public const string NestedMessage = "The two folders are inside each other.";
    public const string LinkMessage = "The destination folder holds a link, so it can't be used for a move.";
    public const string NoLongerMessage = "The folder changed since you looked. Here is what is true now.";

    internal const string StagingSuffix = ".tuvima-move";
    private const long SpaceMargin = 64L * 1024 * 1024;

    /// <summary>Works out what moving this folder would do. Reads only; nothing is created or moved.</summary>
    internal async Task<VaultMovePlan> PlanAsync(string direction, string sourceId, CancellationToken ct)
    {
        if (direction is not (VaultMoveDirections.In or VaultMoveDirections.Out))
        {
            return Refuse(VaultMovePreviewStates.Refused, "Choose whether to move into the Vault or out of it.", direction);
        }

        var fromVault = direction == VaultMoveDirections.Out;
        var vaultIds = await vaultLibraries.GetIdsAsync(ct).ConfigureAwait(false);
        var settings = configuration.LoadLibraries();
        var (library, source) = Find(settings, sourceId);
        if (library is null || source is null)
        {
            return Refuse(VaultMovePreviewStates.Refused, UnknownFolderMessage, direction);
        }

        if (vaultIds.Contains(library.Id) != fromVault || string.IsNullOrWhiteSpace(library.KindKey))
        {
            return Refuse(VaultMovePreviewStates.Refused, WrongSideMessage, direction);
        }

        var destinationLibrary = settings.Libraries.FirstOrDefault(candidate =>
            string.Equals(candidate.KindKey, library.KindKey, StringComparison.Ordinal)
            && vaultIds.Contains(candidate.Id) != fromVault
            && !string.Equals(candidate.Id, library.Id, StringComparison.OrdinalIgnoreCase));
        if (destinationLibrary is null)
        {
            return Refuse(VaultMovePreviewStates.NoDestination, NoDestinationMessage, direction, library, source);
        }

        // Only a folder Library manages can receive files, whichever way the move goes (D33, D38).
        var destination = destinationLibrary.PrimaryDestination is { AllowsFileMutation: true } primary
            ? primary
            : destinationLibrary.CataloguedSources.FirstOrDefault(candidate => candidate.AllowsFileMutation);
        if (destination is null)
        {
            return Refuse(VaultMovePreviewStates.NeedsManagedFolder, NeedsManagedMessage, direction, library, source, destinationLibrary);
        }

        if (source.MergeState != LibrarySourceMergeStates.None
            || source.ConversionState == LibrarySourceConversionStates.Converting
            || await moves.HasOpenJobForFolderAsync(source.Path, ct).ConfigureAwait(false)
            || await moves.HasOpenJobForFolderAsync(destination.Path, ct).ConfigureAwait(false))
        {
            return Refuse(VaultMovePreviewStates.Busy, BusyMessage, direction, library, source, destinationLibrary, destination);
        }

        string sourceRoot;
        string destinationRoot;
        try
        {
            sourceRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(source.Path));
            destinationRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(destination.Path));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            _ = ex;
            return Refuse(VaultMovePreviewStates.Refused, UnknownFolderMessage, direction, library, source, destinationLibrary, destination);
        }

        if (Contains(sourceRoot, destinationRoot) || Contains(destinationRoot, sourceRoot))
        {
            return Refuse(VaultMovePreviewStates.Refused, NestedMessage, direction, library, source, destinationLibrary, destination);
        }

        if (!Directory.Exists(destinationRoot) || VaultFolderLinkCheck.HoldsLink(destinationRoot))
        {
            return Refuse(VaultMovePreviewStates.Refused, LinkMessage, direction, library, source, destinationLibrary, destination);
        }

        var candidates = await moves.FindCandidatesAsync(library.Id, sourceRoot, ct).ConfigureAwait(false);
        var movable = new List<VaultMoveCandidate>();
        var skipped = 0;
        var bytes = 0L;
        foreach (var candidate in candidates)
        {
            ct.ThrowIfCancellationRequested();
            // A title with several files, a file that is not set up as a title, a missing file and a link stay where they are.
            var info = SafeFile(candidate.FilePath);
            if (candidate.WorkId is null || candidate.FilesInTitle != 1 || info is null || info.LinkTarget is not null)
            {
                skipped++;
                continue;
            }

            movable.Add(candidate);
            bytes += info.Length;
        }

        var originalsStay = !source.AllowsFileMutation;
        var fingerprint = Fingerprint(direction, source.Id, destination.Id, originalsStay, movable);
        if (movable.Count == 0)
        {
            return new VaultMovePlan(
                VaultMovePreviewStates.NothingToMove, NothingMessage, direction, library.KindKey, library, source, destinationLibrary, destination,
                movable, skipped, 0, originalsStay, fingerprint);
        }

        if (FreeSpaceShort(destinationRoot, bytes))
        {
            return new VaultMovePlan(
                VaultMovePreviewStates.NotEnoughSpace, NotEnoughSpaceMessage, direction, library.KindKey, library, source, destinationLibrary, destination,
                movable, skipped, bytes, originalsStay, fingerprint);
        }

        return new VaultMovePlan(
            VaultMovePreviewStates.Ready, null, direction, library.KindKey, library, source, destinationLibrary, destination,
            movable, skipped, bytes, originalsStay, fingerprint);
    }

    public async Task<VaultMovePreviewDto> PreviewAsync(string direction, string sourceId, CancellationToken ct) =>
        (await PlanAsync(direction, sourceId, ct).ConfigureAwait(false)).ToPreview();

    /// <summary>
    /// Starts the move the administrator confirmed. Their own profile PIN is asked for again here. The plan is worked out again and compared
    /// with the one they saw; if the folder changed, nothing starts and the answer carries the fresh preview. The job and every item are saved
    /// before a file is touched, then the worker carries them out.
    /// </summary>
    public async Task<VaultMoveStartOutcome> StartAsync(Guid administratorProfileId, StartVaultMoveRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        await administration.RequireAdminPinAsync(administratorProfileId, request.AdminPin, ct).ConfigureAwait(false);
        return await QueueAsync(administratorProfileId, request, ct).ConfigureAwait(false);
    }

    /// <summary>The part of <see cref="StartAsync"/> after the PIN: plan again, compare with what the person saw, save the job and its items, wake the worker.</summary>
    internal async Task<VaultMoveStartOutcome> QueueAsync(Guid administratorProfileId, StartVaultMoveRequest request, CancellationToken ct)
    {
        var plan = await PlanAsync(request.Direction, request.SourceId, ct).ConfigureAwait(false);
        if (plan.State != VaultMovePreviewStates.Ready)
        {
            return new VaultMoveStartOutcome(VaultMoveStartStatus.NotAllowed, Preview: plan.ToPreview());
        }

        if (!string.Equals(plan.Fingerprint, request.Fingerprint, StringComparison.Ordinal))
        {
            return new VaultMoveStartOutcome(VaultMoveStartStatus.Changed, Preview: plan.ToPreview(NoLongerMessage));
        }

        var now = clock.GetUtcNow();
        var jobId = Guid.NewGuid();
        // Removing originals from a Read-only folder is its own explicit choice (D30), and only means something for a Read-only folder.
        var removeReadOnly = plan.OriginalsStay && request.RemoveReadOnlyOriginals;
        var job = new VaultMoveJob(
            jobId, plan.Direction, plan.KindKey, plan.SourceLibrary!.Id, plan.DestinationLibrary!.Id,
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(plan.Source!.Path)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(plan.Destination!.Path)),
            removeReadOnly, VaultMoveStates.Queued, null, administratorProfileId, now, now);

        var mediaExtensions = extensions.GetAllMediaExtensions();
        var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var items = new List<VaultMoveItem>(plan.Movable.Count);
        foreach (var candidate in plan.Movable)
        {
            ct.ThrowIfCancellationRequested();
            var finalName = UniqueName(CleanFileName(Path.GetFileName(candidate.FilePath)), usedNames);
            var sidecars = SidecarsOf(candidate.FilePath, finalName, job.DestinationFolder, mediaExtensions, usedNames);
            items.Add(new VaultMoveItem(
                Guid.NewGuid(), jobId, items.Count, candidate.WorkId, candidate.AssetId, null, candidate.FilePath, finalName,
                SafeFile(candidate.FilePath)?.Length ?? 0, candidate.ContentHash,
                sidecars.Count == 0 ? null : JsonSerializer.Serialize(sidecars), null, false, VaultMoveItemStates.Planned, null, now));
        }

        await moves.CreateAsync(job, items, ct).ConfigureAwait(false);
        logger.LogInformation("A Vault move was queued with {Count} item(s).", items.Count);
        signal.Wake();
        return new VaultMoveStartOutcome(VaultMoveStartStatus.Started, jobId);
    }

    /// <summary>The recent moves and how far each has got, for an unlocked administrator.</summary>
    public async Task<IReadOnlyList<VaultMoveJobDto>> ListAsync(int limit, CancellationToken ct)
    {
        var jobs = await moves.ListRecentAsync(limit, ct).ConfigureAwait(false);
        var result = new List<VaultMoveJobDto>(jobs.Count);
        foreach (var job in jobs)
        {
            result.Add(ToDto(job, await moves.GetItemsAsync(job.Id, ct).ConfigureAwait(false)));
        }

        return result;
    }

    public async Task<VaultMoveJobDto?> GetAsync(Guid id, CancellationToken ct)
    {
        var job = await moves.GetAsync(id, ct).ConfigureAwait(false);
        return job is null ? null : ToDto(job, await moves.GetItemsAsync(id, ct).ConfigureAwait(false));
    }

    internal static VaultMoveJobDto ToDto(VaultMoveJob job, IReadOnlyList<VaultMoveItem> items) => new()
    {
        Id = job.Id,
        Direction = job.Direction,
        State = job.State,
        Message = job.Message,
        Total = items.Count,
        Moved = items.Count(item => item.State == VaultMoveItemStates.Done),
        Waiting = items.Count(item => item.State is VaultMoveItemStates.Published or VaultMoveItemStates.Retired),
        Skipped = items.Count(item => item.State == VaultMoveItemStates.Skipped),
        Failed = items.Count(item => item.State == VaultMoveItemStates.Failed),
        OriginalLeft = items.Count(item => item.OriginalLeft && item.State != VaultMoveItemStates.Failed),
        CreatedAt = job.CreatedAt,
    };

    // ----- helpers ------------------------------------------------------------------------------------------------------

    private static VaultMovePlan Refuse(
        string state,
        string message,
        string direction,
        LibraryFolderConfig? library = null,
        LibrarySourceConfig? source = null,
        LibraryFolderConfig? destinationLibrary = null,
        LibrarySourceConfig? destination = null) =>
        new(state, message, direction, library?.KindKey ?? string.Empty, library, source, destinationLibrary, destination, [], 0, 0, false, string.Empty);

    private static (LibraryFolderConfig? Library, LibrarySourceConfig? Source) Find(LibrariesConfiguration settings, string sourceId)
    {
        foreach (var library in settings.Libraries)
        {
            var source = library.CataloguedSources.FirstOrDefault(candidate => string.Equals(candidate.Id, sourceId, StringComparison.OrdinalIgnoreCase));
            if (source is not null)
            {
                return (library, source);
            }
        }

        return (null, null);
    }

    private static bool Contains(string root, string path) =>
        string.Equals(root, path, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)
        || path.StartsWith(root + Path.DirectorySeparatorChar, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static FileInfo? SafeFile(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists ? info : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            _ = ex;
            return null;
        }
    }

    private static bool FreeSpaceShort(string folder, long bytes)
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(folder));
            return !string.IsNullOrEmpty(root) && new DriveInfo(root).AvailableFreeSpace < bytes + SpaceMargin;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // Free space that can't be read is not a reason to refuse; a copy that runs out of room fails safely, with the original untouched.
            _ = ex;
            return false;
        }
    }

    private static string Fingerprint(string direction, string sourceId, string destinationId, bool originalsStay, IReadOnlyList<VaultMoveCandidate> movable)
    {
        var builder = new StringBuilder();
        builder.Append(direction).Append('|').Append(sourceId).Append('|').Append(destinationId).Append('|').Append(originalsStay ? '1' : '0');
        foreach (var candidate in movable.OrderBy(candidate => candidate.AssetId))
        {
            builder.Append('|').Append(candidate.AssetId.ToString("N")).Append(':').Append(SafeFile(candidate.FilePath)?.Length ?? -1);
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString()))).ToLowerInvariant();
    }

    // The characters Windows rejects, whichever system the Engine runs on, so a library moved between systems keeps working names.
    private static readonly char[] WindowsInvalidFileNameChars = ['\\', '/', ':', '*', '?', '"', '<', '>', '|'];

    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    /// <summary>The file name the copy gets: characters a file system rejects are replaced, spaces are tidied and the length is capped.</summary>
    internal static string CleanFileName(string fileName)
    {
        var extension = Path.GetExtension(fileName);
        var stem = Path.GetFileNameWithoutExtension(fileName);
        var builder = new StringBuilder(stem.Length);
        foreach (var character in stem)
        {
            builder.Append(char.IsControl(character) || Array.IndexOf(WindowsInvalidFileNameChars, character) >= 0 ? '_' : character);
        }

        var cleaned = System.Text.RegularExpressions.Regex.Replace(builder.ToString(), @"\s+", " ").Trim().TrimEnd('.', ' ');
        if (cleaned.Length > 180)
        {
            cleaned = cleaned[..180].TrimEnd('.', ' ');
        }

        if (cleaned.Length == 0)
        {
            cleaned = "Video";
        }

        if (ReservedNames.Contains(cleaned))
        {
            cleaned = "_" + cleaned;
        }

        return cleaned + extension;
    }

    private static string UniqueName(string name, HashSet<string> used)
    {
        var candidate = name;
        var stem = Path.GetFileNameWithoutExtension(name);
        var extension = Path.GetExtension(name);
        for (var n = 2; !used.Add(candidate); n++)
        {
            candidate = $"{stem} ({n.ToString(System.Globalization.CultureInfo.InvariantCulture)}){extension}";
        }

        return candidate;
    }

    private static List<VaultMoveSidecar> SidecarsOf(
        string mediaPath, string finalName, string destinationFolder, IReadOnlySet<string> mediaExtensions, HashSet<string> usedNames)
    {
        var stem = Path.GetFileNameWithoutExtension(finalName);
        var result = new List<VaultMoveSidecar>();
        foreach (var sidecar in SidecarFiles.Find(mediaPath, mediaExtensions).Concat(SidecarFiles.FindArtwork(mediaPath, mediaExtensions, includeFolderLevel: false)))
        {
            // A folder-level picture keeps its own name and belongs to the folder, not to this one file; it is left behind.
            if (sidecar.KeepName || result.Any(existing => string.Equals(existing.Source, sidecar.Path, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            var name = UniqueName(CleanFileName(stem + sidecar.Remainder), usedNames);
            var final = Path.Combine(destinationFolder, name);
            result.Add(new VaultMoveSidecar(sidecar.Path, final + StagingSuffix, final));
        }

        return result;
    }
}

/// <summary>Wakes the move worker as soon as a job is saved, so a move starts without waiting for the next check.</summary>
public sealed class VaultMoveSignal
{
    private readonly SemaphoreSlim _semaphore = new(0, int.MaxValue);

    public void Wake() => _semaphore.Release();

    public Task<bool> WaitAsync(TimeSpan timeout, CancellationToken ct) => _semaphore.WaitAsync(timeout, ct);
}
