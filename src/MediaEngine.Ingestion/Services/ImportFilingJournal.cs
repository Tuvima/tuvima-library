using System.Text.Json;
using MediaEngine.Domain.Configuration;
using MediaEngine.Ingestion.Contracts;
using Microsoft.Extensions.Logging;

namespace MediaEngine.Ingestion.Services;

/// <summary>What a filing was about to do, written before the file moves so an interrupted filing can be finished.</summary>
public sealed record ImportFilingIntent(
    Guid AssetId,
    string SourcePath,
    string DestinationPath,
    string ContentHash,
    string? LibraryId,
    DateTimeOffset StartedAt);

/// <summary>
/// A small durable record of each filing in progress, kept in the Import folder's own working folder (<c>.data/filing</c>).
/// It is written before a file moves and removed once the data store points at the new location, so a crash between the
/// two can be finished on the next start instead of leaving the asset pointing at a path that is now empty.
/// </summary>
public sealed class ImportFilingJournal
{
    private const string FolderName = "filing";

    private readonly IImportFolderService _importFolders;
    private readonly ILogger<ImportFilingJournal> _logger;

    public ImportFilingJournal(IImportFolderService importFolders, ILogger<ImportFilingJournal> logger)
    {
        ArgumentNullException.ThrowIfNull(importFolders);
        ArgumentNullException.ThrowIfNull(logger);
        _importFolders = importFolders;
        _logger = logger;
    }

    /// <summary>Records the intent. Returns false when it could not be written, in which case the file must not be moved.</summary>
    public bool TryBegin(ImportFilingIntent intent)
    {
        if (FolderPath() is not { } folder)
        {
            return false;
        }

        try
        {
            Directory.CreateDirectory(folder);
            var path = FilePath(folder, intent.AssetId);
            using (var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, intent);
                stream.Flush(flushToDisk: true);
            }

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not record the filing of asset {Id}; it stays in the Import folder", intent.AssetId);
            return false;
        }
    }

    /// <summary>Removes the intent once the data store and the disk agree again.</summary>
    public void Complete(Guid assetId)
    {
        if (FolderPath() is not { } folder)
        {
            return;
        }

        try
        {
            var path = FilePath(folder, assetId);
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogDebug(ex, "Could not remove the filing record for asset {Id}", assetId);
        }
    }

    /// <summary>True when a filing of this asset has started and not yet finished.</summary>
    public bool IsInProgress(Guid assetId)
        => FolderPath() is { } folder && File.Exists(FilePath(folder, assetId));

    /// <summary>The filings that started and never finished.</summary>
    public IReadOnlyList<ImportFilingIntent> ReadPending()
    {
        if (FolderPath() is not { } folder || !Directory.Exists(folder))
        {
            return [];
        }

        var pending = new List<ImportFilingIntent>();
        try
        {
            foreach (var file in Directory.EnumerateFiles(folder, "*.json"))
            {
                try
                {
                    var intent = JsonSerializer.Deserialize<ImportFilingIntent>(File.ReadAllText(file));
                    if (intent is not null)
                    {
                        pending.Add(intent);
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
                {
                    _logger.LogWarning(ex, "Ignored an unreadable filing record {File}", file);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not read the filing records in {Folder}", folder);
        }

        return pending;
    }

    private string? FolderPath()
        => _importFolders.GetImportPath() is { } root
            ? Path.Combine(root, ImportFolderRules.WorkingFolderName, FolderName)
            : null;

    private static string FilePath(string folder, Guid assetId) => Path.Combine(folder, $"{assetId:N}.json");
}
