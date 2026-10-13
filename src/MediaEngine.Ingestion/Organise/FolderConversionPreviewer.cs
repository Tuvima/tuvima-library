using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Contracts;
using Microsoft.Extensions.Logging;

namespace MediaEngine.Ingestion.Organise;

/// <summary>One "old to new" example shown in the preview.</summary>
public sealed record FolderConversionSample(string OldPath, string NewPath);

/// <summary>Files the job would leave alone, grouped by the reason it would say.</summary>
public sealed record FolderConversionSkipGroup(string Reason, int Count);

/// <summary>What converting a folder would do, worked out without changing anything.</summary>
public sealed class FolderConversionPreview
{
    /// <summary>Every media file in the folder (a quick count, not a scan).</summary>
    public int TotalFiles { get; init; }

    /// <summary>How many files were examined; fewer than <see cref="TotalFiles"/> when the folder was sampled.</summary>
    public int Examined { get; init; }

    /// <summary>True when every file was examined.</summary>
    public bool Complete => Examined >= TotalFiles;

    public int ToRename { get; init; }

    public int AlreadyCorrect { get; init; }

    public int DuplicatesToRemove { get; init; }

    public int NeedsReview { get; init; }

    /// <summary>Different files that want the same standard name (they move to numbered names and open a Review item).</summary>
    public int NameCollisions { get; init; }

    public int Skipped { get; init; }

    public IReadOnlyList<FolderConversionSkipGroup> SkipReasons { get; init; } = [];

    public IReadOnlyList<FolderConversionSample> Samples { get; init; } = [];

    public int VideoFiles { get; init; }

    public string DuplicatePolicy { get; init; } = LibraryDuplicatePolicies.KeepOne;
}

/// <summary>Works out how long converting a folder is likely to take.</summary>
public static class FolderConversionEstimator
{
    public const double DefaultSecondsPerVideoFile = 5;
    public const double DefaultSecondsPerOtherFile = 1;

    /// <summary>The live rate replaces the estimate after this many files.</summary>
    public const int LiveRateAfterFiles = 50;

    /// <summary>
    /// The up-front estimate: files times the average measured by earlier conversions, or the defaults (about 5 seconds a
    /// video, 1 second a song or book) when there is no earlier job to learn from.
    /// </summary>
    public static TimeSpan Estimate(int videoFiles, int otherFiles, double? measuredSecondsPerFile = null)
    {
        if (measuredSecondsPerFile is > 0)
        {
            return TimeSpan.FromSeconds((videoFiles + otherFiles) * measuredSecondsPerFile.Value);
        }

        return TimeSpan.FromSeconds(
            (videoFiles * DefaultSecondsPerVideoFile) + (otherFiles * DefaultSecondsPerOtherFile));
    }

    /// <summary>
    /// How long is left for a running job: the estimate until <see cref="LiveRateAfterFiles"/> files are done, then the
    /// rate the job is actually achieving.
    /// </summary>
    public static TimeSpan? Remaining(int total, int completed, TimeSpan elapsed, TimeSpan upFrontEstimate)
    {
        var left = Math.Max(0, total - completed);
        if (left == 0)
        {
            return TimeSpan.Zero;
        }

        if (completed >= LiveRateAfterFiles && elapsed > TimeSpan.Zero)
        {
            return TimeSpan.FromSeconds(elapsed.TotalSeconds / completed * left);
        }

        if (total <= 0)
        {
            return null;
        }

        return TimeSpan.FromSeconds(upFrontEstimate.TotalSeconds * left / total);
    }
}

/// <summary>
/// The dry run behind "Preview first": the same decisions the job will make, over the catalogued files in a folder, with
/// nothing moved, deleted, written or saved.
/// </summary>
public sealed class FolderConversionPreviewer
{
    private const int SampleCount = 5;

    private readonly IConfigurationLoader _configuration;
    private readonly IMediaAssetRepository _assets;
    private readonly FolderConversionOrganiser _organiser;
    private readonly IMediaTypeExtensionCatalog? _extensions;
    private readonly ILogger<FolderConversionPreviewer> _logger;

    public FolderConversionPreviewer(
        IConfigurationLoader configuration,
        IMediaAssetRepository assets,
        FolderConversionOrganiser organiser,
        ILogger<FolderConversionPreviewer> logger,
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
    /// A quick count of the media files in the folder. It reads folder listings only (no files are opened), so it is safe to
    /// call while the confirmation is on screen even for a very large folder.
    /// </summary>
    public Task<(int Total, int Video)?> CountAsync(Guid sourceId, CancellationToken ct = default)
    {
        var libraries = _configuration.LoadLibraries();
        var context = FolderConversionContext.TryCreateAsIfManaged(libraries, sourceId, out _);
        if (context is null)
        {
            return Task.FromResult<(int, int)?>(null);
        }

        var files = FolderMediaEnumerator.Enumerate(context.SourceRoot, context.Source.IncludeSubdirectories, MediaExtensions, ct: ct);
        var video = files.Count(path => IsVideo(path));
        return Task.FromResult<(int, int)?>((files.Count, video));
    }

    /// <summary>
    /// Examines up to <paramref name="fileLimit"/> files (0 for all) and reports what the job would do with them.
    /// </summary>
    public async Task<FolderConversionPreview?> PreviewAsync(Guid sourceId, int fileLimit, CancellationToken ct = default)
    {
        var libraries = _configuration.LoadLibraries();
        var context = FolderConversionContext.TryCreateAsIfManaged(libraries, sourceId, out _);
        if (context is null)
        {
            return null;
        }

        var disk = FolderMediaEnumerator.Enumerate(context.SourceRoot, context.Source.IncludeSubdirectories, MediaExtensions, ct: ct);
        var catalogued = new Dictionary<string, MediaAssetFileRef>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in await _assets.ListFilesUnderAsync(context.SourceRoot, ct).ConfigureAwait(false))
        {
            catalogued.TryAdd(Path.GetFullPath(file.FilePath), file);
        }

        var examine = fileLimit > 0 ? disk.Take(fileLimit).ToList() : disk;
        var planned = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var samples = new List<FolderConversionSample>();
        var skips = new Dictionary<string, int>(StringComparer.Ordinal);
        int toRename = 0, alreadyCorrect = 0, duplicates = 0, needsReview = 0, collisions = 0, skipped = 0;
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
                    duplicates++;
                }
                else
                {
                    skipped++;
                    skips[uncatalogued.Reason] = skips.GetValueOrDefault(uncatalogued.Reason) + 1;
                }

                continue;
            }

            var decision = await _organiser.DecideAsync(file, context, planned, ct).ConfigureAwait(false);
            switch (decision.Kind)
            {
                case OrganiseDecisionKind.AlreadyCorrect:
                    alreadyCorrect++;
                    planned.Add(decision.Target!);
                    break;
                case OrganiseDecisionKind.Move:
                    toRename++;
                    planned.Add(decision.Target!);
                    AddSample(samples, context, path, decision.Target!);
                    break;
                case OrganiseDecisionKind.Duplicate when removesDuplicates:
                    duplicates++;
                    break;
                case OrganiseDecisionKind.Duplicate:
                case OrganiseDecisionKind.Collision:
                    toRename++;
                    if (decision.Kind == OrganiseDecisionKind.Collision)
                    {
                        collisions++;
                    }

                    planned.Add(decision.Target!);
                    AddSample(samples, context, path, decision.Target!);
                    break;
                case OrganiseDecisionKind.NeedsReview:
                    needsReview++;
                    break;
                default:
                    skipped++;
                    var reason = decision.Reason ?? "Left as it is.";
                    skips[reason] = skips.GetValueOrDefault(reason) + 1;
                    break;
            }
        }

        _logger.LogDebug("Previewed {Examined} of {Total} files for source {SourceId}", examine.Count, disk.Count, sourceId);
        return new FolderConversionPreview
        {
            TotalFiles = disk.Count,
            Examined = examine.Count,
            ToRename = toRename,
            AlreadyCorrect = alreadyCorrect,
            DuplicatesToRemove = duplicates,
            NeedsReview = needsReview,
            NameCollisions = collisions,
            Skipped = skipped,
            SkipReasons = skips.OrderByDescending(pair => pair.Value)
                .Select(pair => new FolderConversionSkipGroup(pair.Key, pair.Value)).ToList(),
            Samples = samples,
            VideoFiles = disk.Count(IsVideo),
            DuplicatePolicy = context.DuplicatePolicy,
        };
    }

    private static void AddSample(List<FolderConversionSample> samples, FolderConversionContext context, string oldPath, string newPath)
    {
        if (samples.Count < SampleCount)
        {
            samples.Add(new FolderConversionSample(
                Path.GetRelativePath(context.SourceRoot, oldPath),
                Path.GetRelativePath(context.SourceRoot, newPath)));
        }
    }

    private bool IsVideo(string path) => _extensions?.IsVideoExtension(Path.GetExtension(path))
        ?? VideoExtensions.Contains(Path.GetExtension(path));

    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mkv", ".mp4", ".m4v", ".avi", ".mov", ".wmv", ".webm", ".ts", ".m2ts", ".mpg", ".mpeg",
    };
}
