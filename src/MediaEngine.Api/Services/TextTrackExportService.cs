using MediaEngine.Domain.Aggregates;
using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Entities;
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
    ILogger<TextTrackExportService> logger) : ITextTrackExportService
{
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
