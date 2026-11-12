using MediaEngine.Contracts.Editing;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Models;
using MediaEngine.Ingestion;
using MediaEngine.Ingestion.Contracts;

namespace MediaEngine.Api.Services.EditSession;

/// <summary>
/// <see cref="IVideoSeriesPlacement"/> over the stored series, the library folders and the organiser's own move rules. A series' folder is
/// the one folder its videos sit in (a section's videos sit in the section's folder); a series whose videos are in several folders, or in
/// none of its own, has no folder to move to, so the file stays and the choice is kept in Library.
/// </summary>
public sealed class VideoSeriesPlacementService(
    IVideoSeriesRepository series,
    IItemCanonicalRepository items,
    IMediaAssetRepository assets,
    ILibraryFolderResolver libraries,
    IAutoOrganizeService organizer) : IVideoSeriesPlacement
{
    public const string SeriesHasNoFolder = "That series has no folder of its own yet, so the file stays where it is.";
    public const string SeriesInManyFolders = "That series' videos are in more than one folder, so the file stays where it is.";
    public const string NameNotUsable = "That name cannot be used as a folder name. Choose another name for the series.";

    // Names Windows will not make a folder of, whatever the Engine runs on: the library may be opened there, or backed up to it.
    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM0", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT0", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    /// <summary>
    /// The folder name a new series gets, or null when none can be made from <paramref name="name"/>: nothing usable left after cleaning,
    /// a name that ends in a dot or a space (Windows drops it, so the folder would not be the one shown), or a reserved device name such
    /// as CON, NUL or COM1 (with or without an extension).
    /// </summary>
    public static string? FolderNameFor(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.EndsWith('.') || name.EndsWith(' '))
        {
            return null;
        }

        var segment = FileOrganizer.SanitizeSegment(name);
        if (segment.Length == 0 || segment.EndsWith('.') || segment.EndsWith(' ') || segment.TrimStart('.', ' ').Length == 0)
        {
            return null;
        }

        var stem = segment.Split('.')[0].TrimEnd(' ');
        return ReservedNames.Contains(stem) ? null : segment;
    }

    public async Task<VideoSeriesFilePlan?> PlanAsync(Guid entityId, string? targetId, string? token, CancellationToken ct = default)
    {
        var workId = await items.ResolveWorkIdForAssetAsync(entityId, ct).ConfigureAwait(false) ?? entityId;
        var membership = await series.GetMembershipAsync(workId, ct).ConfigureAwait(false);
        if (membership is null)
        {
            return null;
        }

        var assetId = membership.AssetId;
        if (!EditSessionVideo.IsOneChoice(targetId, token))
        {
            return new VideoSeriesFilePlan(false, assetId, null, null, null, EditSessionReasons.SeriesChoiceUnclear, true);
        }

        var asset = assetId == Guid.Empty ? null : await assets.FindByIdAsync(assetId, ct).ConfigureAwait(false);
        var current = asset?.FilePathRoot;
        if (asset is null || string.IsNullOrWhiteSpace(current) || libraries.ResolveSourceForPath(current) is not { } resolved)
        {
            return VideoSeriesFilePlan.Stays(assetId);
        }

        // Only a Managed folder follows a series; anywhere else the file is only ever read.
        if (!resolved.Source.IsManaged || !resolved.Source.IsWritable)
        {
            return VideoSeriesFilePlan.Stays(assetId);
        }

        var root = Path.GetFullPath(resolved.Source.Path);
        string folder;
        if (EditSessionVideo.IsNoSeries(token))
        {
            folder = root;
        }
        else if (EditSessionVideo.TryParseNewSeries(token, out var name))
        {
            var segment = FolderNameFor(name);
            if (segment is null)
            {
                // The save is refused, whatever the Engine runs on, so the folder a person is told about is the folder they get.
                return new VideoSeriesFilePlan(true, assetId, null, null, null, NameNotUsable, true);
            }

            folder = Path.Combine(root, segment);
        }
        else if (Guid.TryParse(targetId, out var target))
        {
            if (membership.ParentWorkId == target)
            {
                return VideoSeriesFilePlan.Stays(assetId, managed: true);
            }

            var homes = (await series.ListMemberFilePathsAsync(target, ct).ConfigureAwait(false))
                .Where(path => !string.IsNullOrWhiteSpace(path) && !string.Equals(Path.GetFullPath(path), Path.GetFullPath(current), StringComparison.OrdinalIgnoreCase))
                .Select(path => Path.GetDirectoryName(Path.GetFullPath(path)) ?? string.Empty)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (homes.Count > 1)
            {
                return VideoSeriesFilePlan.Stays(assetId, managed: true, SeriesInManyFolders);
            }

            // Videos loose in the library folder are not a series folder; neither is a folder in another library folder.
            if (homes.Count == 0
                || string.Equals(Path.TrimEndingDirectorySeparator(homes[0]), Path.TrimEndingDirectorySeparator(root), StringComparison.OrdinalIgnoreCase)
                || !AutoOrganizeService.StaysInsideRoot(current, Path.Combine(homes[0], "x"), root))
            {
                return VideoSeriesFilePlan.Stays(assetId, managed: true, SeriesHasNoFolder);
            }

            folder = homes[0];
        }
        else
        {
            return VideoSeriesFilePlan.Stays(assetId, managed: true);
        }

        var plan = await organizer.PlanMoveToFolderAsync(assetId, folder, ct).ConfigureAwait(false);
        return plan.Disposition switch
        {
            AssetRefileDisposition.Moves => new VideoSeriesFilePlan(true, assetId, folder, plan.CurrentPath, plan.TargetPath, null, false),
            AssetRefileDisposition.AlreadyFits => VideoSeriesFilePlan.Stays(assetId, managed: true),
            // A taken name is never overwritten and never renamed: the person is told, and the change is not saved.
            AssetRefileDisposition.Collision => new VideoSeriesFilePlan(
                true, assetId, folder, plan.CurrentPath, plan.TargetPath,
                $"A file named {Path.GetFileName(plan.CurrentPath)} is already in that folder, so this video cannot move there.", true),
            _ => VideoSeriesFilePlan.Stays(assetId, managed: true, plan.Reason),
        };
    }
}
