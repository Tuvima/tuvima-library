using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Contracts;
using MediaEngine.Ingestion.Services;
using Microsoft.Extensions.Logging;

namespace MediaEngine.Ingestion.Organise;

/// <summary>What merging a folder would do, worked out without changing anything.</summary>
public sealed class FolderMergePreview
{
    public string Mode { get; init; } = LibraryMergeModes.Copy;

    /// <summary>Every media file in the source folder (a quick count, not a scan).</summary>
    public int TotalFiles { get; init; }

    public int VideoFiles { get; init; }

    /// <summary>How many files were examined; fewer than <see cref="TotalFiles"/> when the folder was sampled.</summary>
    public int Examined { get; init; }

    public bool Complete => Examined >= TotalFiles;

    /// <summary>Files that would be copied (or moved) to their standard names, including those that get a numbered name.</summary>
    public int ToTransfer { get; init; }

    /// <summary>Files whose exact copy already has the standard name in the target (Keep one copy).</summary>
    public int AlreadyInTarget { get; init; }

    /// <summary>Files that cannot be named yet; they go to the From merge folder and wait in Review.</summary>
    public int NeedsReview { get; init; }

    public int NameCollisions { get; init; }

    public int Skipped { get; init; }

    public IReadOnlyList<FolderConversionSkipGroup> SkipReasons { get; init; } = [];

    public IReadOnlyList<FolderConversionSample> Samples { get; init; } = [];

    public string DuplicatePolicy { get; init; } = LibraryDuplicatePolicies.KeepOne;

    /// <summary>Total size of the source folder's media files.</summary>
    public long SourceBytes { get; init; }

    /// <summary>The room the target's drive needs for this merge.</summary>
    public long RequiredBytes { get; init; }

    /// <summary>Room left on the target's drive, or null when it cannot be read.</summary>
    public long? TargetFreeBytes { get; init; }

    /// <summary>Why the merge cannot start (not enough room), or null.</summary>
    public string? BlockedReason { get; init; }
}

/// <summary>
/// The dry run behind the merge sheet's Preview: the same decisions the job will make, over the files in the source folder,
/// with nothing copied, moved, deleted, written or saved.
/// </summary>
public sealed class FolderMergePreviewer
{
    // Left free on the target drive once the merge is done, so a nearly full drive is reported before a copy starts.
    private const long FreeSpaceBufferBytes = 64L * 1024 * 1024;
    private const int SampleCount = 5;

    private readonly IConfigurationLoader _configuration;
    private readonly IMediaAssetRepository _assets;
    private readonly FolderMergeOrganiser _organiser;
    private readonly IMediaTypeExtensionCatalog? _extensions;
    private readonly ILogger<FolderMergePreviewer> _logger;

    public FolderMergePreviewer(
        IConfigurationLoader configuration,
        IMediaAssetRepository assets,
        FolderMergeOrganiser organiser,
        ILogger<FolderMergePreviewer> logger,
        IMediaTypeExtensionCatalog? extensions = null)
    {
        _configuration = configuration;
        _assets = assets;
        _organiser = organiser;
        _extensions = extensions;
        _logger = logger;
    }

    private IReadOnlySet<string> MediaExtensions =>
        _extensions?.GetAllMediaExtensions() ?? FolderMediaEnumerator.DefaultMediaExtensions;

    /// <summary>
    /// A quick count of the media files in the source folder, their total size and the room on the target drive. It reads
    /// folder listings and file sizes only (no file is opened), so it is safe to call while the sheet is on screen.
    /// </summary>
    public FolderMergePreview? Count(Guid sourceId, string? targetSourceId, string? mode, out FolderMergeCheck check, CancellationToken ct = default)
    {
        var context = FolderMergeContext.TryCreateForPlan(_configuration.LoadLibraries(), sourceId, targetSourceId, mode, out check);
        if (context is null)
        {
            return null;
        }

        var files = FolderMediaEnumerator.Enumerate(context.SourceRoot, context.Source.IncludeSubdirectories, MediaExtensions, ct: ct);
        return Summarise(context, files, examined: 0, new Tally(), ct);
    }

    /// <summary>Examines up to <paramref name="fileLimit"/> files (0 for all) and reports what the merge would do with them.</summary>
    public async Task<FolderMergePreview?> PreviewAsync(
        Guid sourceId, string? targetSourceId, string? mode, int fileLimit, CancellationToken ct = default)
    {
        var context = FolderMergeContext.TryCreateForPlan(_configuration.LoadLibraries(), sourceId, targetSourceId, mode, out _);
        if (context is null)
        {
            return null;
        }

        var disk = FolderMediaEnumerator.Enumerate(context.SourceRoot, context.Source.IncludeSubdirectories, MediaExtensions, ct: ct);
        var catalogued = new Dictionary<string, MediaAssetFileRef>(PathSafety.Comparer);
        foreach (var file in await _assets.ListFilesUnderAsync(context.SourceRoot, ct).ConfigureAwait(false))
        {
            catalogued.TryAdd(Path.GetFullPath(file.FilePath), file);
        }

        var examine = fileLimit > 0 ? disk.Take(fileLimit).ToList() : disk;
        var planned = new HashSet<string>(PathSafety.Comparer);
        var tally = new Tally();
        var removesDuplicates = LibraryDuplicatePolicies.DeletesIncomingDuplicate(context.DuplicatePolicy);

        foreach (var path in examine)
        {
            ct.ThrowIfCancellationRequested();
            if (!catalogued.TryGetValue(path, out var file))
            {
                // Not in the library yet: an exact copy is dropped under Keep one copy, anything else is identified first.
                var uncatalogued = await _organiser.ClassifyUncataloguedAsync(path, context, ct).ConfigureAwait(false);
                if (uncatalogued.Duplicate && removesDuplicates)
                {
                    tally.AlreadyInTarget++;
                }
                else
                {
                    tally.NeedsReview++;
                    tally.AddSample(context, path, context.HoldingPathFor(path));
                }

                continue;
            }

            var decision = await _organiser.DecideAsync(file, context, planned, ct).ConfigureAwait(false);
            switch (decision.Kind)
            {
                case OrganiseDecisionKind.Move:
                    tally.ToTransfer++;
                    planned.Add(decision.Target!);
                    tally.AddSample(context, path, decision.Target!);
                    break;
                case OrganiseDecisionKind.Duplicate when removesDuplicates:
                    tally.AlreadyInTarget++;
                    break;
                case OrganiseDecisionKind.Duplicate:
                case OrganiseDecisionKind.Collision:
                    tally.ToTransfer++;
                    if (decision.Kind == OrganiseDecisionKind.Collision)
                    {
                        tally.NameCollisions++;
                    }

                    planned.Add(decision.Target!);
                    tally.AddSample(context, path, decision.Target!);
                    break;
                case OrganiseDecisionKind.NeedsReview:
                    tally.NeedsReview++;
                    planned.Add(decision.Target!);
                    tally.AddSample(context, path, decision.Target!);
                    break;
                default:
                    tally.Skipped++;
                    var reason = decision.Reason ?? "Left as it is.";
                    tally.SkipReasons[reason] = tally.SkipReasons.GetValueOrDefault(reason) + 1;
                    break;
            }
        }

        _logger.LogDebug("Previewed {Examined} of {Total} files to merge from {SourceId}", examine.Count, disk.Count, sourceId);
        return Summarise(context, disk, examine.Count, tally, ct);
    }

    private FolderMergePreview Summarise(FolderMergeContext context, List<string> files, int examined, Tally tally, CancellationToken ct)
    {
        long bytes = 0;
        long largest = 0;
        foreach (var path in files)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var length = new FileInfo(path).Length;
                bytes += length;
                largest = Math.Max(largest, length);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A file that cannot be measured is simply left out of the total.
                _ = ex;
            }
        }

        // A move within one drive needs room for one file at a time; a copy (or a move to another drive) needs the whole lot.
        var required = context.IsMove && VolumeProbe.SameVolume(context.SourceRoot, context.TargetRoot) ? largest : bytes;
        var free = VolumeProbe.AvailableBytes(context.TargetRoot);
        string? blocked = free is { } room && room < required + FreeSpaceBufferBytes
            ? "There is not enough room on the drive that holds the target folder."
            : null;

        return new FolderMergePreview
        {
            Mode = context.IsMove ? LibraryMergeModes.Move : LibraryMergeModes.Copy,
            TotalFiles = files.Count,
            VideoFiles = files.Count(IsVideo),
            Examined = examined,
            ToTransfer = tally.ToTransfer,
            AlreadyInTarget = tally.AlreadyInTarget,
            NeedsReview = tally.NeedsReview,
            NameCollisions = tally.NameCollisions,
            Skipped = tally.Skipped,
            SkipReasons = tally.SkipReasons.OrderByDescending(pair => pair.Value)
                .Select(pair => new FolderConversionSkipGroup(pair.Key, pair.Value)).ToList(),
            Samples = tally.Samples,
            DuplicatePolicy = context.DuplicatePolicy,
            SourceBytes = bytes,
            RequiredBytes = required,
            TargetFreeBytes = free,
            BlockedReason = blocked,
        };
    }

    private bool IsVideo(string path) => _extensions?.IsVideoExtension(Path.GetExtension(path))
        ?? VideoExtensions.Contains(Path.GetExtension(path));

    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mkv", ".mp4", ".m4v", ".avi", ".mov", ".wmv", ".webm", ".ts", ".m2ts", ".mpg", ".mpeg",
    };

    private sealed class Tally
    {
        public int ToTransfer;
        public int AlreadyInTarget;
        public int NeedsReview;
        public int NameCollisions;
        public int Skipped;
        public Dictionary<string, int> SkipReasons { get; } = new(StringComparer.Ordinal);
        public List<FolderConversionSample> Samples { get; } = [];

        public void AddSample(FolderMergeContext context, string oldPath, string newPath)
        {
            if (Samples.Count < SampleCount)
            {
                Samples.Add(new FolderConversionSample(
                    Path.GetRelativePath(context.SourceRoot, oldPath),
                    Path.GetRelativePath(context.TargetRoot, newPath)));
            }
        }
    }
}
