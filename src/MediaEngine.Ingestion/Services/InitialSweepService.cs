using System.Diagnostics;
using MediaEngine.Contracts.Realtime;
using MediaEngine.Domain;
using MediaEngine.Domain.Contracts;
using MediaEngine.Ingestion.Contracts;
using MediaEngine.Ingestion.Models;
using MediaEngine.Storage.Contracts;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MediaEngine.Ingestion.Services;

/// <summary>
/// Walks every configured library source path, hashes each media file, and
/// stores the result in <see cref="IFileHashCacheRepository"/>. Publishes
/// progress over SignalR so the Dashboard can show a live progress bar.
///
/// The cache row keyed on <c>(absolute_path, size_bytes, mtime_utc) → sha256</c>
/// is the foundation of every later feature that has to reason about file
/// identity across moves, renames, and NAS unmounts: when a file "goes
/// missing" during the next sweep we can match it back to an orphaned asset
/// row by hash instead of treating it as a delete.
///
/// Spec: side-by-side-with-Plex plan §M.
/// </summary>
public interface IInitialSweepService
{
    /// <summary>
    /// Runs one sweep pass across every configured library source path.
    /// Emits <c>InitialSweepStarted</c>, <c>InitialSweepProgress</c>, and
    /// <c>InitialSweepCompleted</c> events. Safe to call repeatedly — the
    /// cache short-circuits files whose size + mtime haven't changed.
    /// </summary>
    Task<InitialSweepResult> RunAsync(CancellationToken ct = default);
}

/// <summary>
/// Summary counts returned by <see cref="IInitialSweepService.RunAsync"/>.
/// </summary>
public sealed record InitialSweepResult(
    int FilesDiscovered,
    int FilesHashed,
    int FilesFromCache,
    int FilesFailed,
    long BytesHashed,
    TimeSpan Elapsed);

/// <inheritdoc />
public sealed class InitialSweepService : IInitialSweepService
{
    // Publish progress every N files so the dashboard bar updates smoothly
    // without flooding the collection with one event per file.
    private const int ProgressBatchSize = 25;

    // Recognised media extensions — same shortlist used elsewhere in the
    // ingestion layer. Kept lower-cased and includes the dot.
    private readonly IAssetHasher _hasher;
    private readonly IFileHashCacheRepository _cache;
    private readonly IEventPublisher _publisher;
    private readonly IngestionOptions _options;
    private readonly IMediaTypeExtensionCatalog _extensionCatalog;
    private readonly ILogger<InitialSweepService> _logger;
    private readonly IVaultIngestionHook _vaultHook;

    public InitialSweepService(
        IAssetHasher hasher,
        IFileHashCacheRepository cache,
        IEventPublisher publisher,
        IOptions<IngestionOptions> options,
        IMediaTypeExtensionCatalog extensionCatalog,
        ILogger<InitialSweepService> logger,
        IVaultIngestionHook vaultHook)
    {
        _hasher = hasher;
        _cache = cache;
        _publisher = publisher;
        _options = options.Value;
        _extensionCatalog = extensionCatalog;
        _logger = logger;
        _vaultHook = vaultHook;
    }

    /// <inheritdoc />
    public async Task<InitialSweepResult> RunAsync(CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        int discovered = 0, hashed = 0, cached = 0, failed = 0;
        long bytes = 0;

        // Resolve every library source. The Vault's folders are left out: their files are never announced, counted or named in
        // the sweep's started, progress and completed events or in its log (Videos spec D17, D33); ingestion hashes them itself.
        var sweptLibraries = new List<LibraryFolderEntry>();
        foreach (var library in _options.LibraryFolders)
        {
            if (!await _vaultHook.IsVaultLibraryAsync(library.Id, ct).ConfigureAwait(false))
            {
                sweptLibraries.Add(library);
            }
        }

        var roots = sweptLibraries
            .SelectMany(lf => lf.EffectiveSourcePaths)
            .Where(p => !string.IsNullOrWhiteSpace(p) && Directory.Exists(p))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (roots.Count == 0)
        {
            _logger.LogInformation(
                "InitialSweep: no library source paths configured — nothing to sweep");
            return new InitialSweepResult(0, 0, 0, 0, 0, sw.Elapsed);
        }

        _logger.LogInformation(
            "InitialSweep: starting sweep across {Count} root(s) — {Roots}",
            roots.Count, string.Join(", ", roots));

        await SafePublishAsync(
            SignalREvents.InitialSweepStarted,
            new InitialSweepStartedEvent(roots, DateTimeOffset.UtcNow),
            ct).ConfigureAwait(false);

        // Step 1: enumerate first so we can report a total to the UI.
        var files = new List<string>(capacity: 1024);
        var mediaExtensions = _extensionCatalog.GetAllMediaExtensions();
        foreach (var root in roots)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                foreach (var path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
                {
                    var ext = Path.GetExtension(path);
                    if (mediaExtensions.Contains(ext))
                    {
                        files.Add(path);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "InitialSweep: enumeration failed for {Root} — skipping", root);
            }
        }

        discovered = files.Count;

        _logger.LogInformation(
            "InitialSweep: enumerated {Count} media file(s)", discovered);

        // Step 2: hash each file, short-circuiting via the cache when size + mtime unchanged.
        int sinceLastReport = 0;
        foreach (var path in files)
        {
            if (ct.IsCancellationRequested)
            {
                break;
            }

            try
            {
                var info = new FileInfo(path);
                if (!info.Exists) { failed++; continue; }

                var size = info.Length;
                var mtime = new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero);

                var existing = await _cache.TryGetAsync(path, size, mtime, ct)
                    .ConfigureAwait(false);

                if (existing is not null)
                {
                    cached++;
                }
                else
                {
                    var result = await _hasher.ComputeAsync(path, ct).ConfigureAwait(false);
                    await _cache.UpsertAsync(path, size, mtime, result.Hex, ct)
                        .ConfigureAwait(false);
                    hashed++;
                    bytes += result.FileSize;
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                failed++;
                _logger.LogDebug(ex,
                    "InitialSweep: hash failed for {Path} — continuing", path);
            }

            sinceLastReport++;
            if (sinceLastReport >= ProgressBatchSize)
            {
                sinceLastReport = 0;
                await SafePublishAsync(
                    SignalREvents.InitialSweepProgress,
                    new InitialSweepProgressEvent(
                        discovered,
                        hashed + cached + failed,
                        hashed,
                        cached,
                        failed,
                        bytes),
                    ct).ConfigureAwait(false);
            }
        }

        sw.Stop();

        var summary = new InitialSweepResult(
            FilesDiscovered: discovered,
            FilesHashed: hashed,
            FilesFromCache: cached,
            FilesFailed: failed,
            BytesHashed: bytes,
            Elapsed: sw.Elapsed);

        _logger.LogInformation(
            "InitialSweep: completed in {Elapsed} — {Discovered} discovered, {Hashed} hashed, {Cached} cached, {Failed} failed ({Bytes:N0} bytes hashed)",
            sw.Elapsed, discovered, hashed, cached, failed, bytes);

        await SafePublishAsync(
            SignalREvents.InitialSweepCompleted,
            new InitialSweepCompletedEvent(
                discovered,
                hashed,
                cached,
                failed,
                bytes,
                sw.Elapsed.TotalSeconds,
                DateTimeOffset.UtcNow),
            ct).ConfigureAwait(false);

        return summary;
    }

    private async Task SafePublishAsync<T>(string eventName, T payload, CancellationToken ct)
        where T : notnull
    {
        try
        {
            await _publisher.PublishAsync(eventName, payload, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex,
                "InitialSweep: publish of {Event} failed — sweep continues", eventName);
        }
    }
}
