using System.Globalization;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Models;
using Microsoft.Extensions.Logging;

namespace MediaEngine.Providers.Services;

/// <summary>What one pass of the subtitle indexer did.</summary>
public sealed record SubtitleIndexRunResult(int TracksIndexed, int TracksFailed, int FilesProbed, int TracksRetagged)
{
    public bool DidWork => TracksIndexed + TracksFailed + FilesProbed + TracksRetagged > 0;
}

/// <summary>
/// Builds the subtitle text index (Universes U-WP14) from what the Library already holds: the stored, normalised subtitle
/// tracks (whichever source supplied them), the text subtitle streams embedded in video files, and the files' chapter times.
/// Names of Universe entities are matched to subtitle lines by <see cref="SubtitleNameMatcher"/>: plain matching, no AI,
/// no network. Runs in the background only; nothing here sits on the ingestion path.
/// </summary>
public sealed class SubtitleIndexer
{
    /// <summary>Largest subtitle file read, in bytes; a bigger file is almost certainly not subtitle text.</summary>
    public const long MaxSubtitleFileBytes = 20 * 1024 * 1024;

    /// <summary>Tags are checked against the Universe graph again after this long.</summary>
    public static readonly TimeSpan TagRefreshInterval = TimeSpan.FromHours(6);

    /// <summary>A failed read or probe is tried again after this long, up to the repository's attempt limit.</summary>
    public static readonly TimeSpan RetryDelay = TimeSpan.FromHours(1);

    // Text subtitle codecs ffmpeg can turn into WebVTT. Picture-based subtitles (PGS, VobSub) need OCR and are left alone.
    private static readonly HashSet<string> TextCodecs = new(StringComparer.OrdinalIgnoreCase)
    {
        "subrip", "srt", "ass", "ssa", "webvtt", "mov_text", "text",
    };

    private readonly ISubtitleIndexRepository _repository;
    private readonly IFFmpegService _ffmpeg;
    private readonly ILogger<SubtitleIndexer> _logger;
    private readonly TimeProvider _clock;

    public SubtitleIndexer(
        ISubtitleIndexRepository repository,
        IFFmpegService ffmpeg,
        ILogger<SubtitleIndexer> logger,
        TimeProvider? clock = null)
    {
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(ffmpeg);
        ArgumentNullException.ThrowIfNull(logger);
        _repository = repository;
        _ffmpeg = ffmpeg;
        _logger = logger;
        _clock = clock ?? TimeProvider.System;
    }

    /// <summary>One pass: read waiting stored tracks, probe waiting video files, then refresh out-of-date tags.</summary>
    public async Task<SubtitleIndexRunResult> RunOnceAsync(int batchSize, CancellationToken ct = default)
    {
        var now = _clock.GetUtcNow();
        var matchers = new Dictionary<Guid, AssetMatcher>();
        int indexed = 0, failed = 0, probed = 0, retagged = 0;

        foreach (var source in await _repository.GetStoredTracksNeedingIndexAsync(batchSize, now, ct).ConfigureAwait(false))
        {
            ct.ThrowIfCancellationRequested();
            if (await IndexStoredTrackAsync(source, matchers, ct).ConfigureAwait(false))
            {
                indexed++;
            }
            else
            {
                failed++;
            }
        }

        if (_ffmpeg.IsAvailable)
        {
            foreach (var asset in await _repository.GetAssetsAwaitingProbeAsync(batchSize, now, ct).ConfigureAwait(false))
            {
                ct.ThrowIfCancellationRequested();
                var result = await ProbeAssetAsync(asset, matchers, ct).ConfigureAwait(false);
                indexed += result.Indexed;
                failed += result.Failed;
                probed += result.Probed ? 1 : 0;
            }
        }

        foreach (var track in await _repository.GetTracksNeedingTagsAsync(batchSize, now - TagRefreshInterval, ct).ConfigureAwait(false))
        {
            ct.ThrowIfCancellationRequested();
            if (await RetagAsync(track, matchers, ct).ConfigureAwait(false))
            {
                retagged++;
            }
        }

        return new SubtitleIndexRunResult(indexed, failed, probed, retagged);
    }

    /// <summary>Reads one stored text track from its file and indexes it. Returns false when the read failed.</summary>
    public async Task<bool> IndexStoredTrackAsync(SubtitleTrackSource source, CancellationToken ct = default) =>
        await IndexStoredTrackAsync(source, new Dictionary<Guid, AssetMatcher>(), ct).ConfigureAwait(false);

    /// <summary>Indexes already-read subtitle text for a track (the embedded path and tests use this).</summary>
    public async Task IndexContentAsync(SubtitleTrackSource source, string content, string? sourceFormat, CancellationToken ct = default) =>
        await IndexContentAsync(source, content, sourceFormat, new Dictionary<Guid, AssetMatcher>(), ct).ConfigureAwait(false);

    private async Task<bool> IndexStoredTrackAsync(SubtitleTrackSource source, Dictionary<Guid, AssetMatcher> matchers, CancellationToken ct)
    {
        try
        {
            var path = source.LocalPath;
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                throw new FileNotFoundException("The stored subtitle file is missing.");
            }

            if (new FileInfo(path).Length > MaxSubtitleFileBytes)
            {
                throw new InvalidDataException("The stored subtitle file is too large to be subtitle text.");
            }

            var content = await File.ReadAllTextAsync(path, ct).ConfigureAwait(false);
            await IndexContentAsync(source, content, Path.GetExtension(path).TrimStart('.'), matchers, ct).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (IsRecoverable(ex))
        {
            await FailTrackAsync(source, ex, ct).ConfigureAwait(false);
            return false;
        }
    }

    private async Task IndexContentAsync(
        SubtitleTrackSource source,
        string content,
        string? sourceFormat,
        Dictionary<Guid, AssetMatcher> matchers,
        CancellationToken ct)
    {
        var lines = SubtitleLineParser.Parse(content, sourceFormat);
        var matcher = await GetMatcherAsync(source.AssetId, matchers, ct).ConfigureAwait(false);
        var tags = matcher.Matcher.Tag(lines);
        await _repository.SaveIndexedTrackAsync(source, lines, tags, matcher.Signature, _clock.GetUtcNow(), ct).ConfigureAwait(false);
        _logger.LogDebug(
            "Subtitle index: track {TrackId} of asset {AssetId} holds {Lines} lines and {Tags} name tags",
            source.TrackId, source.AssetId, lines.Count, tags.Count);
    }

    private async Task<bool> RetagAsync(SubtitleTrackToTag track, Dictionary<Guid, AssetMatcher> matchers, CancellationToken ct)
    {
        var matcher = await GetMatcherAsync(track.AssetId, matchers, ct).ConfigureAwait(false);
        var now = _clock.GetUtcNow();
        if (string.Equals(track.EntitySignature, matcher.Signature, StringComparison.Ordinal))
        {
            // The Universe graph is unchanged since the tags were made; only note that they were checked.
            await _repository.MarkTagsCheckedAsync(track.TrackId, now, ct).ConfigureAwait(false);
            return false;
        }

        var lines = await _repository.GetLinesAsync(track.TrackId, ct).ConfigureAwait(false);
        var tags = matcher.Matcher.Tag(lines);
        await _repository.SaveTagsAsync(track.TrackId, tags, matcher.Signature, now, ct).ConfigureAwait(false);
        return true;
    }

    /// <summary>
    /// Probes one video file once: stores its chapter times and indexes its embedded text subtitle streams that have no stored
    /// track in the same language already.
    /// </summary>
    private async Task<(int Indexed, int Failed, bool Probed)> ProbeAssetAsync(
        SubtitleAssetToProbe asset,
        Dictionary<Guid, AssetMatcher> matchers,
        CancellationToken ct)
    {
        var now = _clock.GetUtcNow();
        try
        {
            if (!File.Exists(asset.FilePath))
            {
                throw new FileNotFoundException("The video file is missing.");
            }

            var probe = await _ffmpeg.ProbeAsync(asset.FilePath, ct).ConfigureAwait(false)
                ?? throw new InvalidDataException("The video file could not be probed.");

            var chapters = probe.Chapters
                .Select(chapter => new MediaAssetChapter(
                    chapter.Index,
                    ToMilliseconds(chapter.StartSeconds),
                    chapter.EndSeconds is { } end ? ToMilliseconds(end) : null,
                    chapter.Title))
                .ToList();

            var storedLanguages = await _repository.GetStoredTrackLanguagesAsync(asset.AssetId, ct).ConfigureAwait(false);
            var wanted = SelectEmbeddedStreams(probe.SubtitleStreams, storedLanguages);

            int indexed = 0, failed = 0;
            foreach (var stream in wanted)
            {
                ct.ThrowIfCancellationRequested();
                var source = new SubtitleTrackSource(
                    SubtitleTrackIds.ForEmbedded(asset.AssetId, stream.StreamIndex),
                    asset.AssetId,
                    SubtitleSourceKind.Embedded,
                    null,
                    stream.StreamIndex,
                    stream.Language,
                    string.Join('|', "embedded", stream.StreamIndex.ToString(CultureInfo.InvariantCulture), stream.Codec, stream.Language),
                    null);
                if (await IndexEmbeddedStreamAsync(asset, source, matchers, ct).ConfigureAwait(false))
                {
                    indexed++;
                }
                else
                {
                    failed++;
                }
            }

            await _repository.SaveProbedAssetAsync(asset.AssetId, chapters, wanted.Count, now, ct).ConfigureAwait(false);
            return (indexed, failed, true);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (IsRecoverable(ex))
        {
            _logger.LogWarning(ex, "Subtitle index: probing {Path} failed; it will be tried again later", asset.FilePath);
            await _repository.MarkAssetProbeFailedAsync(asset.AssetId, ex.Message, now, now + RetryDelay, ct).ConfigureAwait(false);
            return (0, 1, false);
        }
    }

    /// <summary>The embedded text streams worth reading: not forced, one per language, none where a stored track already covers the language.</summary>
    public static IReadOnlyList<EmbeddedSubtitleStream> SelectEmbeddedStreams(
        IEnumerable<EmbeddedSubtitleStream> streams,
        IReadOnlySet<string> storedLanguages)
    {
        ArgumentNullException.ThrowIfNull(streams);
        ArgumentNullException.ThrowIfNull(storedLanguages);
        var chosen = new List<EmbeddedSubtitleStream>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var stream in streams.OrderBy(s => s.StreamIndex))
        {
            if (stream.IsForced
                || stream.Codec is null
                || !TextCodecs.Contains(stream.Codec)
                || storedLanguages.Contains(stream.Language)
                || !seen.Add(stream.Language))
            {
                continue;
            }

            chosen.Add(stream);
        }

        return chosen;
    }

    private async Task<bool> IndexEmbeddedStreamAsync(
        SubtitleAssetToProbe asset,
        SubtitleTrackSource source,
        Dictionary<Guid, AssetMatcher> matchers,
        CancellationToken ct)
    {
        var temp = Path.Combine(Path.GetTempPath(), $"tuvima-subtitle-{Guid.NewGuid():N}.vtt");
        try
        {
            var (exitCode, _, error) = await _ffmpeg.RunAsync(
                ["-y", "-v", "error", "-i", asset.FilePath, "-map", $"0:{source.StreamIndex}", "-c:s", "webvtt", temp],
                ct).ConfigureAwait(false);
            if (exitCode != 0 || !File.Exists(temp))
            {
                throw new InvalidDataException(string.IsNullOrWhiteSpace(error) ? "ffmpeg could not extract the subtitle stream." : error.Trim());
            }

            if (new FileInfo(temp).Length > MaxSubtitleFileBytes)
            {
                throw new InvalidDataException("The embedded subtitle stream is too large to be subtitle text.");
            }

            var content = await File.ReadAllTextAsync(temp, ct).ConfigureAwait(false);
            await IndexContentAsync(source, content, "vtt", matchers, ct).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (IsRecoverable(ex))
        {
            await FailTrackAsync(source, ex, ct).ConfigureAwait(false);
            return false;
        }
        finally
        {
            TryDelete(temp);
        }
    }

    private async Task FailTrackAsync(SubtitleTrackSource source, Exception ex, CancellationToken ct)
    {
        var now = _clock.GetUtcNow();
        _logger.LogWarning(ex, "Subtitle index: track {TrackId} of asset {AssetId} could not be read", source.TrackId, source.AssetId);
        await _repository.MarkTrackFailedAsync(source, ex.Message, now, now + RetryDelay, ct).ConfigureAwait(false);
    }

    private async Task<AssetMatcher> GetMatcherAsync(Guid assetId, Dictionary<Guid, AssetMatcher> cache, CancellationToken ct)
    {
        if (cache.TryGetValue(assetId, out var cached))
        {
            return cached;
        }

        var names = await _repository.GetEntityNamesForAssetAsync(assetId, ct).ConfigureAwait(false);
        var created = new AssetMatcher(new SubtitleNameMatcher(names), SubtitleNameMatcher.Signature(names));
        cache[assetId] = created;
        return created;
    }

    /// <summary>Problems with one file or stream that are recorded and retried later, rather than stopping the pass.</summary>
    private static bool IsRecoverable(Exception ex) =>
        ex is IOException or UnauthorizedAccessException or InvalidDataException or InvalidOperationException
            or System.ComponentModel.Win32Exception or System.Text.RegularExpressions.RegexMatchTimeoutException;

    private static long ToMilliseconds(double seconds) =>
        (long)Math.Round(Math.Max(0, seconds) * 1000, MidpointRounding.AwayFromZero);

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
            // A leftover temp file in the system temp folder is harmless; the operating system clears it.
        }
        catch (UnauthorizedAccessException)
        {
            // Same: nothing depends on this file once its text has been read.
        }
    }

    internal sealed record AssetMatcher(SubtitleNameMatcher Matcher, string Signature);
}
