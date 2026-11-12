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
    public const string NameNotUsable = "That name cannot be used as a folder name, so the file stays where it is.";

    public async Task<VideoSeriesFilePlan?> PlanAsync(Guid entityId, string? targetId, string? token, CancellationToken ct = default)
    {
        var workId = await items.ResolveWorkIdForAssetAsync(entityId, ct).ConfigureAwait(false) ?? entityId;
        var membership = await series.GetMembershipAsync(workId, ct).ConfigureAwait(false);
        if (membership is null)
        {
            return null;
        }

        var assetId = membership.AssetId;
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
            var segment = FileOrganizer.SanitizeSegment(name).Trim('.', ' ');
            if (segment.Length == 0)
            {
                return VideoSeriesFilePlan.Stays(assetId, managed: true, NameNotUsable);
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
