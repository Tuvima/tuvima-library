using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Enums;
using MediaEngine.Ingestion.Contracts;
using MediaEngine.Ingestion.Models;
using Microsoft.Extensions.Logging;

namespace MediaEngine.Ingestion.Services;

/// <summary>Default <see cref="IImportFolderWaitingReader"/>. It reads the disk and the current folder settings each time.</summary>
public sealed class ImportFolderWaitingReader : IImportFolderWaitingReader
{
    private const int MaxFilesPerRead = 5000;

    private readonly IImportFolderService _importFolders;
    private readonly IMediaAssetRepository _assets;
    private readonly ICanonicalValueRepository _canonicals;
    private readonly ILogger<ImportFolderWaitingReader> _logger;

    public ImportFolderWaitingReader(
        IImportFolderService importFolders,
        IMediaAssetRepository assets,
        ICanonicalValueRepository canonicals,
        ILogger<ImportFolderWaitingReader> logger)
    {
        _importFolders = importFolders;
        _assets = assets;
        _canonicals = canonicals;
        _logger = logger;
    }

    public async Task<IReadOnlyList<ImportFolderWaiting>> GetWaitingAsync(CancellationToken ct = default)
    {
        var root = _importFolders.GetImportPath();
        if (root is null || !Directory.Exists(root))
        {
            return [];
        }

        // Count what is in each type, split into files in its own folder and files at the root of the Import folder.
        var counts = new Dictionary<MediaType, (int InFolder, int AtRoot)>();
        var inspected = 0;
        try
        {
            foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            {
                ct.ThrowIfCancellationRequested();
                if (++inspected > MaxFilesPerRead)
                {
                    break;
                }

                if (_importFolders.IsWorkingPath(file) || IngestionEngine.IsIgnoredScanFile(file))
                {
                    continue;
                }

                var prior = _importFolders.ResolveTypePrior(file);
                var type = await SettledTypeAsync(file, ct).ConfigureAwait(false) ?? prior;
                if (type is null)
                {
                    // Not classified yet, or waiting in Review for a person to say what it is.
                    continue;
                }

                counts.TryGetValue(type.Value, out var current);
                counts[type.Value] = prior is null ? (current.InFolder, current.AtRoot + 1) : (current.InFolder + 1, current.AtRoot);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not finish reading the import folder {Path}", root);
        }

        var waiting = new List<ImportFolderWaiting>();
        foreach (var (type, count) in counts)
        {
            var destination = _importFolders.ResolveDestination(type);
            if (destination.State == ImportDestinationState.Ready)
            {
                continue;
            }

            var total = count.InFolder + count.AtRoot;
            var folder = ImportFolderRules.SubfolderFor(destination.TypeLabel);
            var location = count.AtRoot == 0 ? $"Import\\{folder}" : "Import";
            waiting.Add(new ImportFolderWaiting(
                destination.TypeLabel,
                folder,
                total,
                destination.State,
                Describe(total, location, destination)));
        }

        return waiting
            .OrderBy(entry => entry.MediaType, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private async Task<MediaType?> SettledTypeAsync(string file, CancellationToken ct)
    {
        var asset = await _assets.FindByPathRootAsync(Path.GetFullPath(file), ct).ConfigureAwait(false);
        if (asset is null)
        {
            return null;
        }

        var canonicals = await _canonicals.GetByEntityAsync(asset.Id, ct).ConfigureAwait(false);
        var value = canonicals.FirstOrDefault(c => string.Equals(c.Key, "media_type", StringComparison.OrdinalIgnoreCase))?.Value;
        return Enum.TryParse<MediaType>(value, ignoreCase: true, out var type) && type != MediaType.Unknown ? type : null;
    }

    private static string Describe(int count, string location, ImportDestination destination)
    {
        var files = count == 1 ? "1 file" : $"{count} files";
        return destination.State == ImportDestinationState.NoManagedFolder
            ? $"{files} in {location} can't be filed until {destination.TypeLabel} has a managed folder."
            : $"{files} in {location} {(count == 1 ? "is" : "are")} waiting because the {destination.TypeLabel} folder isn't available right now.";
    }
}
