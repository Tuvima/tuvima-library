using System.Globalization;
using MediaEngine.Domain.Contracts;
using MediaEngine.Ingestion.Contracts;
using MediaEngine.Ingestion.Models;
using MediaEngine.Ingestion.Services;
using Microsoft.Extensions.Logging;

namespace MediaEngine.Ingestion.Organise;

/// <summary>One video of a series, in the order it is shown, with the title its file name should carry.</summary>
public sealed record VideoRenameItem(Guid AssetId, string Title);

/// <summary>How many files were renamed and how many were left exactly as they were.</summary>
public sealed record VideoRenameResult(int Renamed, int Skipped);

/// <summary>
/// "Rename files to match this order" for the videos of a series. It uses the same pieces as the organise job: the shared file
/// mover (<see cref="IFileOrganizer.ExecuteMoveAsync"/>), the files that travel with a video (<see cref="SidecarMover"/>) and the
/// source mutation policy, so a folder used as-is or read-only is never touched. A file only ever gets a new name inside the
/// folder it is already in; nothing moves between folders.
/// </summary>
public sealed class VideoOrderRenamer
{
    private readonly IMediaAssetRepository _assets;
    private readonly IFileOrganizer _organizer;
    private readonly ILibraryFolderResolver _libraries;
    private readonly ISourceMutationPolicyGate _gate;
    private readonly SidecarMover? _sidecars;
    private readonly ILogger<VideoOrderRenamer> _logger;

    public VideoOrderRenamer(
        IMediaAssetRepository assets,
        IFileOrganizer organizer,
        ILibraryFolderResolver libraries,
        ISourceMutationPolicyGate gate,
        ILogger<VideoOrderRenamer> logger,
        SidecarMover? sidecars = null)
    {
        _assets = assets;
        _organizer = organizer;
        _libraries = libraries;
        _gate = gate;
        _sidecars = sidecars;
        _logger = logger;
    }

    /// <summary>True when the file lives in a Managed, writable folder that allows renaming. Anything else is refused.</summary>
    public bool CanRename(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || _libraries.ResolveSourceForPath(path) is not { } resolved)
        {
            return false;
        }

        if (!resolved.Source.AllowsFileMutation)
        {
            return false;
        }

        var policy = FileSourceMutationPolicyFactory.Create(resolved.Library, resolved.Source);
        return _gate.Evaluate(new SourceMutationRequest { Source = policy, Mutation = SourceMutationKind.Rename, Path = path }).Allowed;
    }

    /// <summary>True only when every video's file can be renamed; one file in an as-is folder makes the whole group refuse.</summary>
    public async Task<bool> CanRenameAllAsync(IEnumerable<Guid> assetIds, CancellationToken ct = default)
    {
        var any = false;
        foreach (var id in assetIds)
        {
            any = true;
            var asset = await _assets.FindByIdAsync(id, ct).ConfigureAwait(false);
            if (asset is null || !File.Exists(asset.FilePathRoot) || !CanRename(asset.FilePathRoot))
            {
                return false;
            }
        }

        return any;
    }

    /// <summary>The file name a video takes at a position: "01 - Title.mp4", padded to the width the whole list needs.</summary>
    public static string NameFor(int position, int total, string title, string extension)
    {
        var width = Math.Max(2, total.ToString(CultureInfo.InvariantCulture).Length);
        // The same set on every system, so a name made here is valid wherever the folder is later opened.
        var clean = new string(title.Select(ch => char.IsControl(ch) || "<>:\"/\\|?*".Contains(ch) ? ' ' : ch).ToArray());
        clean = string.Join(' ', clean.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).Trim('.', ' ');
        if (clean.Length == 0)
        {
            clean = "Video";
        }

        if (clean.Length > 120)
        {
            clean = clean[..120].TrimEnd();
        }

        return $"{position.ToString(CultureInfo.InvariantCulture).PadLeft(width, '0')} - {clean}{extension}";
    }

    public async Task<VideoRenameResult> RenameAsync(IReadOnlyList<VideoRenameItem> inOrder, CancellationToken ct = default)
    {
        var renamed = 0;
        var skipped = 0;
        for (var i = 0; i < inOrder.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var asset = await _assets.FindByIdAsync(inOrder[i].AssetId, ct).ConfigureAwait(false);
            if (asset is null || !File.Exists(asset.FilePathRoot) || !CanRename(asset.FilePathRoot))
            {
                skipped++;
                continue;
            }

            var directory = Path.GetDirectoryName(asset.FilePathRoot);
            if (string.IsNullOrEmpty(directory))
            {
                skipped++;
                continue;
            }

            var target = Path.Combine(directory, NameFor(i + 1, inOrder.Count, inOrder[i].Title, Path.GetExtension(asset.FilePathRoot)));
            // Never onto a name another file holds (the mover would add a suffix the next pass would change again).
            if (string.Equals(target, asset.FilePathRoot, StringComparison.Ordinal)
                || (File.Exists(target) && !string.Equals(target, asset.FilePathRoot, StringComparison.OrdinalIgnoreCase)))
            {
                skipped++;
                continue;
            }

            var moved = await _organizer.ExecuteMoveAsync(asset.FilePathRoot, target, ct).ConfigureAwait(false);
            if (moved is null)
            {
                skipped++;
                continue;
            }

            var from = asset.FilePathRoot;
            await _assets.UpdateFilePathAsync(asset.Id, moved, ct).ConfigureAwait(false);
            if (_sidecars is not null)
            {
                try
                {
                    _sidecars.Move(from, moved, (_, to) => CanRename(to), mayDelete: null, includeArtwork: true, renameArtworkToFolder: false);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    _logger.LogInformation(ex, "Left the files that travel with {Video} where they were", Path.GetFileName(from));
                }
            }

            renamed++;
        }

        return new VideoRenameResult(renamed, skipped);
    }
}
