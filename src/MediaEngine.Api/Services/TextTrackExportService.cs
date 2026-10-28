using MediaEngine.Domain.Aggregates;
using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Entities;
using MediaEngine.Domain.Enums;
using MediaEngine.Domain.Services;
using MediaEngine.Ingestion.Contracts;
using MediaEngine.Ingestion.Models;
using MediaEngine.Ingestion.Services;

namespace MediaEngine.Api.Services;

/// <summary>
/// Keeps provider downloads in central managed storage and treats a source-side
/// subtitle as an optional compatibility export governed by source policy.
/// </summary>
public sealed class TextTrackExportService(
    ILibraryFolderResolver libraryFolderResolver,
    ISourceMutationPolicyGate mutationPolicyGate,
    IConfigurationLoader configurationLoader,
    ILogger<TextTrackExportService> logger,
    IFilingHold? filingHold = null,
    ITextTrackRepository? textTracks = null) : ITextTrackExportService
{
    public async Task<string?> ExportPreferredLyricsAsync(
        MediaAsset asset,
        TextTrack track,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (track.Kind != TextTrackKind.Lyrics
            || track.IsUserOwned
            || string.IsNullOrWhiteSpace(track.LocalPath)
            || !File.Exists(track.LocalPath))
        {
            return null;
        }

        var resolvedSource = libraryFolderResolver.ResolveSourceForPath(asset.FilePathRoot);
        if (resolvedSource is null
            || filingHold?.IsHeld(resolvedSource.Library.Id, resolvedSource.Source.Id, asset.FilePathRoot) == true)
        {
            return null;
        }

        var extension = string.Equals(track.TimingMode, "Plain", StringComparison.OrdinalIgnoreCase) ? ".txt" : ".lrc";
        var exportPath = AssetPathService.BuildLyricsSidecarPath(asset.FilePathRoot, extension);
        var writebackEnabled = configurationLoader
            .LoadConfig<WriteBackConfiguration>(string.Empty, "writeback")?.Enabled == true;
        var decision = writebackEnabled
            ? mutationPolicyGate.Evaluate(new SourceMutationRequest
            {
                Source = FileSourceMutationPolicyFactory.Create(resolvedSource.Library, resolvedSource.Source),
                Mutation = SourceMutationKind.MetadataWriteback,
                Path = exportPath,
            })
            : SourceMutationDecision.Deny("Write-back is turned off.");
        if (!decision.Allowed)
        {
            logger.LogInformation("Lyrics export skipped for asset {AssetId}: {Reason}", asset.Id, decision.Reason);
            return null;
        }

        // The Library's own earlier files for this song, found from what it recorded when it wrote them. A person's own file also records its
        // path, so those tracks are left out and can never be mistaken for the Library's.
        var ownFiles = new List<TextTrack>();
        if (textTracks is not null)
        {
            ownFiles.AddRange((await textTracks.GetByAssetAsync(asset.Id, TextTrackKind.Lyrics, ct).ConfigureAwait(false))
                .Where(other => other.Id != track.Id && !other.IsUserOwned && !string.IsNullOrWhiteSpace(other.SidecarPath)));
        }

        bool IsOurs(string path) =>
            string.Equals(path, track.SidecarPath, StringComparison.OrdinalIgnoreCase)
            || ownFiles.Any(other => string.Equals(other.SidecarPath, path, StringComparison.OrdinalIgnoreCase));

        // A person's own lyrics file wins: if either kind of file is there and the Library did not write it, nothing is written or removed.
        foreach (var candidate in new[] { ".lrc", ".txt" })
        {
            var existing = AssetPathService.BuildLyricsSidecarPath(asset.FilePathRoot, candidate);
            if (File.Exists(existing) && !IsOurs(existing))
            {
                logger.LogInformation("Lyrics export skipped for asset {AssetId}; a lyrics file that is not the Library's own is beside the song at {Path}", asset.Id, existing);
                return null;
            }
        }

        // One lyrics file only: the Library's earlier file (possibly the other kind) is replaced, never left beside the new one.
        foreach (var other in ownFiles)
        {
            if (!string.Equals(other.SidecarPath, exportPath, StringComparison.OrdinalIgnoreCase) && File.Exists(other.SidecarPath))
            {
                File.Delete(other.SidecarPath!);
            }

            other.SidecarPath = null;
            await textTracks!.UpsertAsync(other, ct).ConfigureAwait(false);
        }

        AssetPathService.EnsureDirectory(exportPath);
        File.Copy(track.LocalPath, exportPath, overwrite: true);
        return exportPath;
    }

    public Task<string?> ExportPreferredSubtitleAsync(
        MediaAsset asset,
        TextTrack track,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(track.LocalPath) || !File.Exists(track.LocalPath))
        {
            return Task.FromResult<string?>(null);
        }

        var resolvedSource = libraryFolderResolver.ResolveSourceForPath(asset.FilePathRoot);
        if (resolvedSource is null)
        {
            logger.LogDebug(
                "Subtitle export skipped for asset {AssetId}; its media path is not governed by a configured source",
                asset.Id);
            return Task.FromResult<string?>(null);
        }

        // A folder whose first check is running or paused changes nothing on disk (Library setup D61); the export is made when the hold ends
        // (HeldSubtitleExporter).
        if (filingHold?.IsHeld(resolvedSource.Library.Id, resolvedSource.Source.Id, asset.FilePathRoot) == true)
        {
            logger.LogInformation(
                "Subtitle export held for asset {AssetId}: its folder has not passed its first check, so nothing is written beside the file yet",
                asset.Id);
            return Task.FromResult<string?>(null);
        }

        var exportPath = AssetPathService.BuildSubtitleSidecarPath(asset.FilePathRoot, track.Language, ".vtt");
        var writebackEnabled = configurationLoader
            .LoadConfig<WriteBackConfiguration>(string.Empty, "writeback")?.Enabled == true;
        var decision = writebackEnabled
            ? mutationPolicyGate.Evaluate(new SourceMutationRequest
            {
                Source = FileSourceMutationPolicyFactory.Create(resolvedSource.Library, resolvedSource.Source),
                Mutation = SourceMutationKind.MetadataWriteback,
                Path = exportPath,
            })
            : SourceMutationDecision.Deny("Write-back is turned off.");
        if (!decision.Allowed)
        {
            logger.LogInformation(
                "Subtitle export skipped for asset {AssetId}: {Reason}",
                asset.Id,
                decision.Reason);
            return Task.FromResult<string?>(null);
        }

        if (File.Exists(exportPath)
            && !string.Equals(exportPath, track.SidecarPath, StringComparison.OrdinalIgnoreCase))
        {
            logger.LogInformation(
                "Subtitle export skipped for asset {AssetId}; an unmanaged sidecar already exists at {Path}",
                asset.Id,
                exportPath);
            return Task.FromResult<string?>(null);
        }

        AssetPathService.EnsureDirectory(exportPath);
        File.Copy(track.LocalPath, exportPath, overwrite: true);
        return Task.FromResult<string?>(exportPath);
    }
}
