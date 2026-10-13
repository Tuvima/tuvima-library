using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Enums;
using MediaEngine.Ingestion.Contracts;
using MediaEngine.Ingestion.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MediaEngine.Ingestion.Services;

/// <summary>Default <see cref="IImportFolderFiler"/>.</summary>
public sealed class ImportFolderFiler : IImportFolderFiler
{
    // Most files looked at in one pass, so a very large import folder can't hold up the rest of the engine.
    private const int MaxFilesPerPass = 5000;

    // Identity work that has finished far enough for filing to be worth trying. A file whose job is still searching,
    // matching or hydrating is left for the pipeline, which tries to file it itself when that work is done.
    private static readonly HashSet<IdentityJobState> SettledStates =
    [
        IdentityJobState.RetailMatchedNeedsReview,
        IdentityJobState.RetailNoMatch,
        IdentityJobState.QidNeedsReview,
        IdentityJobState.QidNoMatch,
        IdentityJobState.UniverseEnriching,
        IdentityJobState.Ready,
        IdentityJobState.ReadyWithoutUniverse,
    ];

    private readonly IImportFolderService _importFolders;
    private readonly IMediaAssetRepository _assets;
    private readonly IIdentityJobRepository _identityJobs;
    private readonly ICanonicalValueRepository _canonicals;
    private readonly IAutoOrganizeService _autoOrganize;
    private readonly IFileOrganizer _organizer;
    private readonly ISourceMutationPolicyGate _gate;
    private readonly ImportFileMover _mover;
    private readonly IngestionOptions _options;
    private readonly ILogger<ImportFolderFiler> _logger;

    public ImportFolderFiler(
        IImportFolderService importFolders,
        IMediaAssetRepository assets,
        IIdentityJobRepository identityJobs,
        ICanonicalValueRepository canonicals,
        IAutoOrganizeService autoOrganize,
        IFileOrganizer organizer,
        ISourceMutationPolicyGate gate,
        ImportFileMover mover,
        IOptions<IngestionOptions> options,
        ILogger<ImportFolderFiler> logger)
    {
        _importFolders = importFolders;
        _assets = assets;
        _identityJobs = identityJobs;
        _canonicals = canonicals;
        _autoOrganize = autoOrganize;
        _organizer = organizer;
        _gate = gate;
        _mover = mover;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<int> FilePendingAsync(CancellationToken ct = default)
    {
        var root = _importFolders.GetImportPath();
        if (root is null || !Directory.Exists(root))
        {
            return 0;
        }

        var filed = 0;
        var inspected = 0;
        IEnumerable<string> files;
        try
        {
            files = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not read the import folder {Path} to file waiting items", root);
            return 0;
        }

        foreach (var file in SafeEnumerate(files, root))
        {
            ct.ThrowIfCancellationRequested();
            if (++inspected > MaxFilesPerPass)
            {
                break;
            }

            if (_importFolders.IsWorkingPath(file))
            {
                continue;
            }

            var asset = await _assets.FindByPathRootAsync(Path.GetFullPath(file), ct).ConfigureAwait(false);
            if (asset is null || !await IdentityWorkSettledAsync(asset.Id, ct).ConfigureAwait(false))
            {
                continue;
            }

            await _autoOrganize.TryAutoOrganizeAsync(asset.Id, ct).ConfigureAwait(false);
            if (!File.Exists(file))
            {
                filed++;
            }
        }

        if (filed > 0)
        {
            _logger.LogInformation("Filed {Count} waiting file(s) out of the import folder", filed);
        }

        return filed;
    }

    public async Task<bool> FileDuplicateCopyAsync(
        string copyPath,
        Guid heldAssetId,
        string contentHash,
        CancellationToken ct = default)
    {
        var held = await _assets.FindByIdAsync(heldAssetId, ct).ConfigureAwait(false);
        if (held is null || _importFolders.IsInImportFolder(held.FilePathRoot) || !File.Exists(copyPath))
        {
            return false;
        }

        var canonicals = await _canonicals.GetByEntityAsync(heldAssetId, ct).ConfigureAwait(false);
        var metadata = canonicals.ToDictionary(c => c.Key, c => c.Value, StringComparer.OrdinalIgnoreCase);
        if (!metadata.TryGetValue("media_type", out var mediaTypeText)
            || !Enum.TryParse<MediaType>(mediaTypeText, ignoreCase: true, out var mediaType)
            || mediaType == MediaType.Unknown)
        {
            return false;
        }

        var destination = _importFolders.ResolveDestination(mediaType, new FileInfo(copyPath).Length);
        if (!destination.IsReady)
        {
            _logger.LogDebug("The copy {Path} stays in the import folder: {State}", copyPath, destination.State);
            return false;
        }

        var synth = new IngestionCandidate
        {
            Path = copyPath,
            EventType = FileEventType.Created,
            DetectedAt = DateTimeOffset.UtcNow,
            ReadyAt = DateTimeOffset.UtcNow,
            Metadata = metadata,
            DetectedMediaType = mediaType,
        };
        var relative = LibraryOrganizationPath.Calculate(
            _organizer, synth, destination.Library, _options.ResolveTemplate(mediaType.ToString()));
        var destPath = Path.GetFullPath(Path.Combine(destination.Source!.Path, relative));
        if (!ImportFolderGate.CanFile(_gate, _importFolders, copyPath, destPath, destination, out var reason))
        {
            _logger.LogDebug("The copy {Path} stays in the import folder: {Reason}", copyPath, reason);
            return false;
        }

        // The name is the standard one, so it normally clashes with the copy that is held and the mover gives this one a unique name.
        var filedPath = await _mover.MoveAsync(copyPath, destPath, contentHash, adoptIdenticalCopy: false, ct: ct).ConfigureAwait(false);
        if (filedPath is null)
        {
            return false;
        }

        _importFolders.RemoveEmptyFolders(Path.GetDirectoryName(copyPath) ?? string.Empty);
        return true;
    }

    private async Task<bool> IdentityWorkSettledAsync(Guid assetId, CancellationToken ct)
    {
        var job = await _identityJobs.GetByEntityAsync(assetId, ct).ConfigureAwait(false);
        return job is not null
            && Enum.TryParse<IdentityJobState>(job.State, ignoreCase: true, out var state)
            && SettledStates.Contains(state);
    }

    private IEnumerable<string> SafeEnumerate(IEnumerable<string> files, string root)
    {
        using var enumerator = files.GetEnumerator();
        while (true)
        {
            try
            {
                if (!enumerator.MoveNext())
                {
                    yield break;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogDebug(ex, "Stopped reading the import folder {Path} early", root);
                yield break;
            }

            yield return enumerator.Current;
        }
    }
}
