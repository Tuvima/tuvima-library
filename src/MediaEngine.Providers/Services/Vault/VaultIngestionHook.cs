using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Services;
using MediaEngine.Providers.Services.StashBox;
using Microsoft.Extensions.Logging;

namespace MediaEngine.Providers.Services.Vault;

/// <summary>
/// The Vault's part of ingestion (Videos spec WP12). Once a file in a Vault library is registered it keeps the still frame with the
/// Vault's own artwork (never in the shared artwork tables), looks the file up when the library asks for it, and writes one quiet line
/// to the Vault activity list. A weak or doubtful match leaves the video Unmatched and queues nothing: no review item, no event, no
/// notification (D32). A failed lookup never fails ingestion.
/// </summary>
public sealed class VaultIngestionHook : IVaultIngestionHook
{
    private readonly IVaultLibraryRepository _vault;
    private readonly IVaultArtworkRepository _artwork;
    private readonly IVaultActivityRepository _activity;
    private readonly IVaultMatchRepository _matches;
    private readonly VaultVideoDetailsService _details;
    private readonly TuvimaDataPaths _paths;
    private readonly TimeProvider _time;
    private readonly ILogger<VaultIngestionHook> _logger;

    public VaultIngestionHook(
        IVaultLibraryRepository vault,
        IVaultArtworkRepository artwork,
        IVaultActivityRepository activity,
        IVaultMatchRepository matches,
        VaultVideoDetailsService details,
        TuvimaDataPaths paths,
        TimeProvider time,
        ILogger<VaultIngestionHook> logger)
    {
        _vault = vault;
        _artwork = artwork;
        _activity = activity;
        _matches = matches;
        _details = details;
        _paths = paths;
        _time = time;
        _logger = logger;
    }

    public async Task<bool> IsVaultLibraryAsync(string? libraryId, CancellationToken ct = default) =>
        !string.IsNullOrWhiteSpace(libraryId) && (await _vault.GetIdsAsync(ct).ConfigureAwait(false)).Contains(libraryId);

    public Task<bool> IsVaultAssetAsync(Guid assetId, CancellationToken ct = default) => _vault.IsInVaultAsync(assetId, ct);

    public async Task OnFileRegisteredAsync(VaultRegisteredFile file, CancellationToken ct = default)
    {
        var name = Path.GetFileName(file.FilePath);
        var now = _time.GetUtcNow();
        await _activity.AddAsync(file.AssetId, VaultActivityKinds.Added, name, "Added to the Vault.", now, ct).ConfigureAwait(false);

        await StoreStillFrameAsync(file, now, ct).ConfigureAwait(false);

        VaultVideoDetailsOutcome outcome;
        try
        {
            outcome = await _details.LookupFileAsync(file.AssetId, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Whatever went wrong with the lookup, the file is already registered; it stays Unmatched and Identify can be tried by hand.
            _logger.LogWarning("A Vault lookup could not finish ({Type}); the video stays Unmatched", ex.GetType().Name);
            await _activity.AddAsync(
                file.AssetId, VaultActivityKinds.Failed, name, "The lookup could not finish. The video stays Unmatched.", _time.GetUtcNow(), ct).ConfigureAwait(false);
            return;
        }

        switch (outcome.Status)
        {
            case VaultVideoDetailsOutcome.Filled:
            case VaultVideoDetailsOutcome.NothingNew:
                var match = await _matches.GetMatchAsync(file.AssetId, ct).ConfigureAwait(false);
                await _activity.AddAsync(
                    file.AssetId, VaultActivityKinds.Matched, name, $"Matched by {SignalWords(match?.MatchedBy)}.", _time.GetUtcNow(), ct).ConfigureAwait(false);
                break;
            case VaultVideoDetailsOutcome.NotFound:
                await _activity.AddAsync(
                    file.AssetId, VaultActivityKinds.Unmatched, name,
                    "No strong match was found. The video stays Unmatched; use Identify to look it up by hand.", _time.GetUtcNow(), ct).ConfigureAwait(false);
                break;
            default:
                await _activity.AddAsync(
                    file.AssetId, VaultActivityKinds.Unmatched, name, "This library does not look videos up. The video stays Unmatched.",
                    _time.GetUtcNow(), ct).ConfigureAwait(false);
                break;
        }
    }

    private async Task StoreStillFrameAsync(VaultRegisteredFile file, DateTimeOffset now, CancellationToken ct)
    {
        if (file.StillFrame is not { Length: > 0 } frame)
        {
            return;
        }

        var current = await _artwork.GetForOwnerAsync(VaultArtworkOwnerKinds.Asset, file.AssetId, ct).ConfigureAwait(false);
        if (current.Any(a => a.Kind == VaultArtworkKinds.Still))
        {
            return;
        }

        var (image, refusal) = VaultArtworkValidator.Validate(frame, VaultImagePurpose.Still);
        if (image is null)
        {
            _logger.LogInformation("A Vault still frame was refused ({Reason})", refusal);
            return;
        }

        var folder = Path.Combine(_paths.Root, "vault-artwork", file.AssetId.ToString("N"));
        var path = Path.Combine(folder, "still" + image.Extension);
        try
        {
            Directory.CreateDirectory(folder);
            await File.WriteAllBytesAsync(path, image.Bytes, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            if (ct.IsCancellationRequested)
            {
                throw;
            }

            _logger.LogWarning("A Vault still frame could not be written ({Type})", ex.GetType().Name);
            return;
        }

        await _artwork.AddAsync(
            VaultArtworkOwnerKinds.Asset, file.AssetId, VaultArtworkKinds.Still, path, image.Hash, image.Width, image.Height, "video", now, ct)
            .ConfigureAwait(false);
    }

    private static string SignalWords(string? signal) => signal switch
    {
        AdultMatchSignals.Fingerprint => "an exact file fingerprint",
        AdultMatchSignals.ProductCode => "its product code and length",
        AdultMatchSignals.Title => "its title and length",
        _ => "a strong signal",
    };
}
