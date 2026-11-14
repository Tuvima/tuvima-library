using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MediaEngine.Contracts.Playback;
using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Enums;
using MediaEngine.Domain.Models;
using MediaEngine.Storage.Contracts;
using MediaEngine.Storage.Playback;

namespace MediaEngine.Api.Services.Playback;

public sealed class AdaptiveHlsService
{
    private static readonly HashSet<string> AllowedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".m3u8", ".ts", ".vtt", ".m4s", ".mp4", ".json",
    };

    private readonly AdaptiveHlsPackageRepository _packages;
    private readonly IMediaAssetRepository _assets;
    private readonly ITextTrackRepository _textTracks;
    private readonly IFFmpegService _ffmpeg;
    private readonly IConfigurationLoader _configuration;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly ILogger<AdaptiveHlsService> _logger;
    private readonly ConcurrentDictionary<Guid, Lazy<Task>> _preparations = new();
    private readonly ConcurrentDictionary<Guid, int> _activeReaders = new();
    private long _lastServedTicks;
    private readonly EncodeSlotGate _encodeSlots;
    private readonly ConcurrentDictionary<Guid, AdaptiveHlsRunInfo> _runs = new();
    private readonly PlaybackStateRepository? _inspection;

    public AdaptiveHlsService(
        AdaptiveHlsPackageRepository packages,
        IMediaAssetRepository assets,
        ITextTrackRepository textTracks,
        IFFmpegService ffmpeg,
        IConfigurationLoader configuration,
        IHostApplicationLifetime lifetime,
        ILogger<AdaptiveHlsService> logger,
        PlaybackStateRepository? inspection = null)
    {
        _inspection = inspection;
        _packages = packages;
        _assets = assets;
        _textTracks = textTracks;
        _ffmpeg = ffmpeg;
        _configuration = configuration;
        _lifetime = lifetime;
        _logger = logger;
        // The limit is read each time a start asks for a slot, so changing "Maximum active encodes" applies at once (playback-path D9).
        _encodeSlots = new EncodeSlotGate(() => Math.Clamp(configuration.LoadTranscoding().MaxConcurrentTranscodes, 1, 8));
    }

    public async Task<AdaptiveHlsPreparation> EnsurePackageAsync(
        Guid assetId,
        string sourceHash,
        IReadOnlyList<PlaybackTrackDto> audioTracks,
        CancellationToken ct = default,
        HlsStartPlan? plan = null)
    {
        var settings = _configuration.LoadTranscoding();
        plan ??= HlsStartPlan.EncodeDefault;
        var profileKey = BuildProfileKey(settings.AdaptiveHls, plan);
        var existing = await _packages.FindAsync(assetId, sourceHash, profileKey, ct).ConfigureAwait(false);
        if (existing is { Status: "failed" })
        {
            return new AdaptiveHlsPreparation(existing.Id, "failed", existing.LastError, RunFor(existing.Id, existing.RootPath, settings.AdaptiveHls.SegmentSeconds));
        }
        if (existing is not null && _preparations.ContainsKey(existing.Id)
            && File.Exists(Path.Combine(existing.RootPath, "master.m3u8")))
        {
            return new AdaptiveHlsPreparation(existing.Id, "streaming", null, RunFor(existing.Id, existing.RootPath, settings.AdaptiveHls.SegmentSeconds));
        }
        if (existing is { Status: "ready" } && File.Exists(Path.Combine(existing.RootPath, "master.m3u8")))
        {
            await _packages.TouchAsync(existing.Id, ct).ConfigureAwait(false);
            return new AdaptiveHlsPreparation(existing.Id, "ready", null);
        }

        var root = ResolveCacheRoot(settings);
        Directory.CreateDirectory(Path.Combine(root, "hls"));
        var provisionalRoot = existing?.RootPath ?? Path.Combine(root, "hls", Guid.NewGuid().ToString("N"));
        var package = existing ?? await _packages.GetOrCreateAsync(
            assetId,
            sourceHash,
            profileKey,
            provisionalRoot,
            ct).ConfigureAwait(false);
        await _packages.MarkPreparingAsync(package.Id, ct).ConfigureAwait(false);

        var task = _preparations.GetOrAdd(
            package.Id,
            _ => new Lazy<Task>(() => PreparePackageAsync(package, audioTracks, plan, _lifetime.ApplicationStopping))).Value;
        var wait = TimeSpan.FromSeconds(1); // Report preparing promptly; the player retains start intent.
        try
        {
            var deadline = DateTimeOffset.UtcNow + wait;
            while (!task.IsCompleted && DateTimeOffset.UtcNow < deadline)
            {
                if (File.Exists(Path.Combine(package.RootPath, "master.m3u8")))
                {
                    return new AdaptiveHlsPreparation(package.Id, "streaming", null, RunFor(package.Id, package.RootPath, settings.AdaptiveHls.SegmentSeconds));
                }
                await Task.Delay(250, ct).ConfigureAwait(false);
            }
            if (!task.IsCompleted)
            {
                return new AdaptiveHlsPreparation(package.Id, "preparing", null, RunFor(package.Id, package.RootPath, settings.AdaptiveHls.SegmentSeconds));
            }
            await task.ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            return new AdaptiveHlsPreparation(package.Id, "preparing", null, RunFor(package.Id, package.RootPath, settings.AdaptiveHls.SegmentSeconds));
        }

        var refreshed = await _packages.FindByIdAsync(package.Id, ct).ConfigureAwait(false);
        return new AdaptiveHlsPreparation(
            package.Id,
            refreshed?.Status ?? "failed",
            refreshed?.LastError,
            RunFor(package.Id, package.RootPath, settings.AdaptiveHls.SegmentSeconds));
    }

    /// <summary>What the packager is doing for a package right now, with how much is ready; null when it was never started here.</summary>
    private AdaptiveHlsRunInfo? RunFor(Guid packageId, string root, int segmentSeconds)
    {
        if (!_runs.TryGetValue(packageId, out var run))
        {
            return null;
        }

        int? percent = null;
        try
        {
            var directory = Path.Combine(root, "v0");
            if (run.DurationSeconds > 0 && Directory.Exists(directory))
            {
                var segments = Directory.EnumerateFiles(directory, "segment_*.ts").Count();
                percent = segments == 0
                    ? null
                    : (int)Math.Clamp(segments * (double)segmentSeconds / run.DurationSeconds * 100d, 1, 100);
            }
        }
        catch (IOException)
        {
            // The folder was being rewritten while we counted; the next question gets the number.
            percent = null;
        }

        return run.Snapshot(percent);
    }

    public async Task<HlsResourceLease?> OpenResourceAsync(
        Guid packageId,
        Guid assetId,
        string resourcePath,
        CancellationToken ct = default)
    {
        var package = await _packages.FindByIdAsync(packageId, ct).ConfigureAwait(false);
        if (package is null || package.AssetId != assetId
            || (package.Status != "ready" && !(package.Status == "preparing" && _preparations.ContainsKey(packageId))))
        {
            return null;
        }

        var normalized = resourcePath.Replace('\\', '/').TrimStart('/');
        if (string.IsNullOrWhiteSpace(normalized)
            || normalized.Split('/').Any(segment => segment is ".." or "." || string.IsNullOrWhiteSpace(segment))
            || !AllowedExtensions.Contains(Path.GetExtension(normalized)))
        {
            return null;
        }

        var root = Path.GetFullPath(package.RootPath);
        var path = Path.GetFullPath(Path.Combine(root, normalized.Replace('/', Path.DirectorySeparatorChar)));
        if (!path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || !File.Exists(path))
        {
            return null;
        }

        _activeReaders.AddOrUpdate(packageId, 1, (_, current) => current + 1);
        Interlocked.Exchange(ref _lastServedTicks, DateTime.UtcNow.Ticks);
        await _packages.TouchAsync(packageId, ct).ConfigureAwait(false);
        try
        {
            var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read | FileShare.Delete,
                64 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            return new HlsResourceLease(stream, ContentTypeFor(path), () => Release(packageId));
        }
        catch
        {
            Release(packageId);
            throw;
        }
    }

    /// <summary>
    /// True while any adaptive package is being prepared or read, or one was read within <paramref name="quietFor"/>. A read-only signal
    /// that lets low-priority background work (scrub previews) stay out of the way of a transcode that is being served.
    /// </summary>
    public bool IsServing(TimeSpan quietFor)
    {
        if (!_preparations.IsEmpty || _activeReaders.Values.Any(count => count > 0))
        {
            return true;
        }

        var last = Interlocked.Read(ref _lastServedTicks);
        return last != 0 && DateTime.UtcNow - new DateTime(last, DateTimeKind.Utc) < quietFor;
    }

    public bool IsActive(Guid packageId) => _preparations.ContainsKey(packageId) || (_activeReaders.TryGetValue(packageId, out var count) && count > 0);

    private async Task PreparePackageAsync(
        AdaptiveHlsPackageRecord package,
        IReadOnlyList<PlaybackTrackDto> audioTracks,
        HlsStartPlan plan,
        CancellationToken ct)
    {
        var run = _runs.AddOrUpdate(
            package.Id,
            _ => new AdaptiveHlsRunInfo { WaitingForSlot = true },
            (_, _) => new AdaptiveHlsRunInfo { WaitingForSlot = true });
        await _encodeSlots.WaitAsync(ct).ConfigureAwait(false);
        run.WaitingForSlot = false;
        using var preparationCancellation = CancellationTokenSource.CreateLinkedTokenSource(ct);
        ct = preparationCancellation.Token;
        var staging = package.RootPath;
        Task<(int ExitCode, string Output, string Error)>? firstAudio = null;
        Task<IReadOnlyList<HlsCaption>>? captionTask = null;
        try
        {
            if (!_ffmpeg.IsAvailable || !_ffmpeg.HardwareCapabilities.AdaptiveHlsReady)
            {
                throw new InvalidOperationException("FFmpeg does not provide the HLS, H.264, and AAC capabilities required for adaptive delivery.");
            }

            var asset = await _assets.FindByIdAsync(package.AssetId, ct).ConfigureAwait(false)
                ?? throw new FileNotFoundException("The source asset no longer exists.");
            if (!File.Exists(asset.FilePathRoot))
            {
                throw new FileNotFoundException("The source media file is missing.", asset.FilePathRoot);
            }

            DeleteDirectory(staging);
            Directory.CreateDirectory(staging);
            var settings = _configuration.LoadTranscoding();
            var metadata = _inspection is null ? null : await _inspection.GetInspectionMetadataAsync(asset.Id, asset.ContentHash, ct);
            var probe = metadata is null ? null : JsonSerializer.Deserialize<MediaEngine.Domain.Models.MediaProbeResult>(metadata);
            if (probe?.Height is not > 0)
            {
                throw new InvalidOperationException("The source video dimensions could not be inspected.");
            }

            var copyVideo = plan.Decision.CopyVideo;
            run.DurationSeconds = probe.Duration.TotalSeconds;
            var renditions = (copyVideo
                    ? [SourceRendition(probe)]
                    : PickRenditions(settings.AdaptiveHls, probe.Height.Value, plan))
                .OrderBy(r => r.Height).ToArray();
            // Captions can require reading the entire source. Publish video independently.
            captionTask = PrepareCaptionsAsync(package.AssetId, asset.FilePathRoot, staging, probe, ct);
            var choice = copyVideo
                ? new HlsEncoderChoice("copy", PlaybackEncodePlaces.None, null)
                : ChooseEncoder(settings.HardwareAcceleration, _ffmpeg.HardwareCapabilities);
            var encoder = choice.Encoder;
            run.Encoder = encoder;
            run.RanOn = choice.RanOn;
            run.FallbackReason = choice.FallbackReason;
            _logger.LogInformation(
                "Play: {Rung}, because {Code}; encoder {Encoder}, runs on {RanOn}{Fallback} (asset {AssetId})",
                plan.Decision.Rung, plan.Decision.Code, encoder, choice.RanOn,
                choice.FallbackReason is null ? string.Empty : $" ({choice.FallbackReason})", package.AssetId);
            if (audioTracks.Count == 0 && !string.IsNullOrWhiteSpace(probe.AudioCodec))
            {
                audioTracks = [new PlaybackTrackDto { Index = 0, Codec = probe.AudioCodec, Language = probe.AudioLanguage, DisplayName = "Audio", IsDefault = true }];
            }
            if (audioTracks.Count > 0)
            {
                Directory.CreateDirectory(Path.Combine(staging, "a0"));
                firstAudio = EncodeAudioAsync(asset.FilePathRoot, Path.Combine(staging, "a0"), 0, settings.AdaptiveHls.SegmentSeconds, plan.Decision.CopyAudioCodecs, audioTracks[0].Codec, ct);
            }
            for (var index = 0; index < renditions.Length; index++)
            {
                var rendition = renditions[index];
                var directory = Path.Combine(staging, $"v{index}");
                Directory.CreateDirectory(directory);
                var firstRendition = index == 0;
                Task Publish() => WriteMasterPlaylistAsync(staging, [rendition], audioTracks.Take(1).ToArray(),
                    captionTask.IsCompletedSuccessfully ? captionTask.Result : [], copyVideo, ct);
                bool AudioReady() => firstAudio is null || HasPublishedSegment(Path.Combine(staging, "a0"));

                var result = await RunVideoAsync(
                    token => copyVideo
                        ? CopyVideoAsync(asset.FilePathRoot, directory, settings.AdaptiveHls.SegmentSeconds, token)
                        : EncodeVideoAsync(asset.FilePathRoot, directory, rendition, encoder, settings.AdaptiveHls.SegmentSeconds, probe, token),
                    directory, watchStart: !copyVideo && encoder != "libx264",
                    firstRendition ? AudioReady : null, firstRendition ? Publish : null, ct).ConfigureAwait(false);
                if (result.ExitCode != 0 && !copyVideo && !string.Equals(encoder, "libx264", StringComparison.Ordinal))
                {
                    // Restart this start on the processor at once, and say why (playback-path D5).
                    var reason = HardwareReasons.Plain(result.Error);
                    _logger.LogWarning("Graphics card encode failed for {AssetId} ({Reason}); restarting on the processor: {Error}",
                        package.AssetId, reason, Tail(result.Error));
                    encoder = "libx264";
                    run.Encoder = encoder;
                    run.RanOn = PlaybackEncodePlaces.Processor;
                    run.FallbackReason = reason;
                    DeleteDirectory(directory);
                    Directory.CreateDirectory(directory);
                    result = await RunVideoAsync(
                        token => EncodeVideoAsync(asset.FilePathRoot, directory, rendition, encoder, settings.AdaptiveHls.SegmentSeconds, probe, token),
                        directory, watchStart: false,
                        firstRendition ? AudioReady : null, firstRendition ? Publish : null, ct).ConfigureAwait(false);
                }
                if (result.ExitCode != 0)
                {
                    throw new InvalidOperationException($"FFmpeg video rendition failed: {Tail(result.Error)}");
                }

                NormalizePlaylist(Path.Combine(directory, "index.m3u8"));
            }

            var generatedAudio = new List<PlaybackTrackDto>();
            for (var index = 0; index < audioTracks.Count; index++)
            {
                var directory = Path.Combine(staging, $"a{index}");
                Directory.CreateDirectory(directory);
                var result = await (index == 0 && firstAudio is not null
                        ? firstAudio
                        : EncodeAudioAsync(asset.FilePathRoot, directory, index, settings.AdaptiveHls.SegmentSeconds, plan.Decision.CopyAudioCodecs, audioTracks[index].Codec, ct))
                    .ConfigureAwait(false);
                if (result.ExitCode != 0)
                {
                    if (index == 0)
                    {
                        throw new InvalidOperationException($"FFmpeg audio rendition failed: {Tail(result.Error)}");
                    }

                    DeleteDirectory(directory);
                    continue;
                }
                NormalizePlaylist(Path.Combine(directory, "index.m3u8"));
                generatedAudio.Add(audioTracks[index]);
            }

            var captions = await captionTask.ConfigureAwait(false);
            await WriteMasterPlaylistAsync(staging, renditions, generatedAudio, captions, copyVideo, ct).ConfigureAwait(false);
            ValidatePackage(staging);

            var bytes = Directory.EnumerateFiles(package.RootPath, "*", SearchOption.AllDirectories)
                .Sum(path => new FileInfo(path).Length);
            await _packages.MarkReadyAsync(package.Id, package.RootPath, bytes, ct).ConfigureAwait(false);
            _logger.LogInformation("Adaptive HLS package {PackageId} is ready for asset {AssetId} ({Bytes} bytes)", package.Id, package.AssetId, bytes);
            _runs.TryRemove(package.Id, out _);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            await preparationCancellation.CancelAsync();
            if (firstAudio is not null) { try { await firstAudio; } catch (OperationCanceledException) { } }
            if (captionTask is not null) { try { await captionTask; } catch (Exception captionError) { _logger.LogDebug(captionError, "Caption preparation stopped"); } }
            DeleteDirectory(staging);
            _runs.TryRemove(package.Id, out _);
        }
        catch (Exception ex)
        {
            await preparationCancellation.CancelAsync();
            if (firstAudio is not null) { try { await firstAudio; } catch (Exception audioError) { _logger.LogDebug(audioError, "Audio preparation stopped"); } }
            if (captionTask is not null) { try { await captionTask; } catch (Exception captionError) { _logger.LogDebug(captionError, "Caption preparation stopped"); } }
            DeleteDirectory(staging);
            await _packages.MarkFailedAsync(package.Id, ex.Message, CancellationToken.None).ConfigureAwait(false);
            _logger.LogError(ex, "Adaptive HLS package {PackageId} failed", package.Id);
        }
        finally
        {
            _preparations.TryRemove(package.Id, out _);
            _encodeSlots.Release();
        }
    }

    /// <summary>How long a graphics encoder gets to write its first segment before the start moves to the processor.</summary>
    private static readonly TimeSpan HardwareStartDeadline = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Runs one video encode or copy. Publishes the master playlist as soon as the first segment (and first audio segment) exist, and
    /// when <paramref name="watchStart"/> is set, stops an encoder that has not written a segment within the deadline.
    /// </summary>
    private static async Task<(int ExitCode, string Output, string Error)> RunVideoAsync(
        Func<CancellationToken, Task<(int ExitCode, string Output, string Error)>> start,
        string directory,
        bool watchStart,
        Func<bool>? readyToPublish,
        Func<Task>? publish,
        CancellationToken ct)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var task = start(stop.Token);
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var segmentSeen = false;
        while (!task.IsCompleted)
        {
            var segment = HasPublishedSegment(directory);
            segmentSeen |= segment;
            if (publish is not null && segment && (readyToPublish?.Invoke() ?? true))
            {
                await publish().ConfigureAwait(false);
                publish = null;
            }
            if (watchStart && !segmentSeen && clock.Elapsed > HardwareStartDeadline)
            {
                await stop.CancelAsync().ConfigureAwait(false);
                try { await task.ConfigureAwait(false); }
                catch (OperationCanceledException) { /* Stopped on purpose: the caller restarts on the processor. */ }
                return (-1, string.Empty, "The graphics encoder did not produce its first segment in time (did not finish starting).");
            }
            await Task.Delay(250, ct).ConfigureAwait(false);
        }
        return await task.ConfigureAwait(false);
    }

    private Task<(int ExitCode, string Output, string Error)> CopyVideoAsync(
        string input,
        string directory,
        int segmentSeconds,
        CancellationToken ct) => _ffmpeg.RunAsync(
        [
            "-y", "-hide_banner", "-loglevel", "warning", "-i", input,
            "-map", "0:v:0", "-an", "-sn", "-c:v", "copy",
            "-f", "hls", "-hls_time", segmentSeconds.ToString(CultureInfo.InvariantCulture),
            "-hls_playlist_type", "event", "-hls_flags", "independent_segments+temp_file",
            "-hls_segment_filename", Path.Combine(directory, "segment_%05d.ts"), Path.Combine(directory, "index.m3u8"),
        ], ct);

    /// <summary>A stand-in rendition for a copied picture: the source's own size and bitrate, never re-encoded.</summary>
    private static HlsRenditionProfile SourceRendition(MediaEngine.Domain.Models.MediaProbeResult probe)
    {
        var kbps = probe.Duration.TotalSeconds > 0 && probe.FileSizeBytes > 0
            ? (int)Math.Clamp(probe.FileSizeBytes * 8d / probe.Duration.TotalSeconds / 1000d, 500, 100_000)
            : 8000;
        return new HlsRenditionProfile
        {
            Name = "source",
            Height = probe.Height ?? 0,
            VideoBitrateKbps = kbps,
            MaxRateKbps = kbps,
            BufferSizeKbps = kbps * 2,
        };
    }

    private async Task<(int ExitCode, string Output, string Error)> EncodeVideoAsync(
        string input,
        string directory,
        HlsRenditionProfile rendition,
        string encoder,
        int segmentSeconds,
        MediaEngine.Domain.Models.MediaProbeResult probe,
        CancellationToken ct)
    {
        var playlist = Path.Combine(directory, "index.m3u8");
        var segmentPattern = Path.Combine(directory, "segment_%05d.ts");
        var arguments = new List<string>
        {
            "-y", "-hide_banner", "-loglevel", "warning", "-i", input,
            "-map", "0:v:0", "-an", "-sn", "-vf", BuildVideoFilter(rendition.Height, probe.ColorTransfer),
            "-c:v", encoder, "-b:v", $"{rendition.VideoBitrateKbps}k",
            "-maxrate", $"{rendition.MaxRateKbps}k", "-bufsize", $"{rendition.BufferSizeKbps}k",
            "-sc_threshold", "0", "-force_key_frames", $"expr:gte(t,n_forced*{segmentSeconds})",
            "-f", "hls", "-hls_time", segmentSeconds.ToString(CultureInfo.InvariantCulture),
            "-hls_playlist_type", "event", "-hls_flags", "independent_segments+temp_file",
            "-hls_segment_filename", segmentPattern, playlist,
        };
        if (encoder == "libx264")
        {
            arguments.InsertRange(12, ["-preset", "veryfast", "-profile:v", "main"]);
        }

        return await _ffmpeg.RunAsync(arguments, ct).ConfigureAwait(false);
    }

    internal static string BuildVideoFilter(int height, string? transfer) =>
        transfer is "smpte2084" or "arib-std-b67"
            ? $"zscale=t=linear:npl=100,format=gbrpf32le,zscale=p=bt709,tonemap=tonemap=hable:desat=0,zscale=t=bt709:m=bt709:r=tv,scale=-2:{height},format=yuv420p"
            : $"scale=-2:{height},format=yuv420p";

    private Task<(int ExitCode, string Output, string Error)> EncodeAudioAsync(
        string input,
        string directory,
        int streamIndex,
        int segmentSeconds,
        IReadOnlyList<string> copyCodecs,
        string? sourceCodec,
        CancellationToken ct) => _ffmpeg.RunAsync(
        [
            "-y", "-hide_banner", "-loglevel", "warning", "-i", input,
            "-map", $"0:a:{streamIndex}", "-vn", "-sn",
            ..(copyCodecs.Contains(PlaybackCapabilitiesService.NormalizeCodecName(sourceCodec), StringComparer.OrdinalIgnoreCase)
                ? new[] { "-c:a", "copy" }
                : new[] { "-c:a", "aac", "-b:a", "160k", "-ac", "2" }),
            "-f", "hls", "-hls_time", segmentSeconds.ToString(CultureInfo.InvariantCulture),
            "-hls_playlist_type", "event", "-hls_flags", "temp_file", "-hls_segment_filename", Path.Combine(directory, "segment_%05d.ts"),
            Path.Combine(directory, "index.m3u8"),
        ], ct);

    private async Task<IReadOnlyList<HlsCaption>> PrepareCaptionsAsync(
        Guid assetId,
        string sourcePath,
        string root,
        MediaEngine.Domain.Models.MediaProbeResult probe,
        CancellationToken ct)
    {
        var tracks = await _textTracks.GetByAssetAsync(assetId, TextTrackKind.Subtitles, ct).ConfigureAwait(false);
        var captions = new List<HlsCaption>();
        foreach (var track in tracks.Where(track => File.Exists(track.LocalPath)))
        {
            var index = captions.Count;
            var directory = Path.Combine(root, $"s{index}");
            Directory.CreateDirectory(directory);
            var target = Path.Combine(directory, "caption.vtt");
            if (string.Equals(Path.GetExtension(track.LocalPath), ".vtt", StringComparison.OrdinalIgnoreCase))
            {
                File.Copy(track.LocalPath, target, overwrite: true);
            }
            else
            {
                var conversion = await ConvertCaptionAsync(track.LocalPath, "0:s:0", target, ct).ConfigureAwait(false);
                if (conversion.ExitCode != 0)
                {
                    _logger.LogWarning("Managed caption conversion failed for asset {AssetId}: {Error}", assetId, Tail(conversion.Error));
                    DeleteDirectory(directory);
                    continue;
                }
            }
            await WriteCaptionPlaylistAsync(directory, probe.Duration.TotalSeconds, ct).ConfigureAwait(false);
            captions.Add(new HlsCaption(track.Language, track.IsPreferred));
        }

        for (var streamIndex = 0; streamIndex < probe.SubtitleLanguages.Count; streamIndex++)
        {
            var directory = Path.Combine(root, $"s{captions.Count}");
            Directory.CreateDirectory(directory);
            var target = Path.Combine(directory, "caption.vtt");
            var conversion = await ConvertCaptionAsync(sourcePath, $"0:s:{streamIndex}", target, ct).ConfigureAwait(false);
            if (conversion.ExitCode != 0)
            {
                _logger.LogWarning("Embedded caption conversion failed for asset {AssetId} stream {StreamIndex}: {Error}", assetId, streamIndex, Tail(conversion.Error));
                DeleteDirectory(directory);
                continue;
            }
            await WriteCaptionPlaylistAsync(directory, probe.Duration.TotalSeconds, ct).ConfigureAwait(false);
            var language = string.IsNullOrWhiteSpace(probe.SubtitleLanguages[streamIndex])
                ? "und"
                : probe.SubtitleLanguages[streamIndex];
            captions.Add(new HlsCaption(language, captions.Count == 0));
        }
        var manifest = Path.Combine(root, "captions.json");
        await File.WriteAllTextAsync(manifest + ".tmp", JsonSerializer.Serialize(captions.Select((caption, index) => new
        {
            language = caption.Language,
            isDefault = caption.IsDefault,
            path = $"s{index}/caption.vtt",
        })), ct).ConfigureAwait(false);
        File.Move(manifest + ".tmp", manifest, overwrite: true);
        return captions;
    }

    private Task<(int ExitCode, string Output, string Error)> ConvertCaptionAsync(
        string input,
        string map,
        string output,
        CancellationToken ct) => _ffmpeg.RunAsync(
        [
            "-y", "-hide_banner", "-loglevel", "warning", "-i", input,
            "-map", map, "-c:s", "webvtt", output,
        ], ct);

    private static Task WriteCaptionPlaylistAsync(
        string directory,
        double durationSeconds,
        CancellationToken ct)
    {
        var duration = Math.Max(1, durationSeconds);
        return File.WriteAllTextAsync(Path.Combine(directory, "index.m3u8"), $"""
            #EXTM3U
            #EXT-X-VERSION:3
            #EXT-X-TARGETDURATION:{Math.Ceiling(duration).ToString(CultureInfo.InvariantCulture)}
            #EXT-X-MEDIA-SEQUENCE:0
            #EXTINF:{duration.ToString("0.###", CultureInfo.InvariantCulture)},
            caption.vtt
            #EXT-X-ENDLIST
            """, new UTF8Encoding(false), ct);
    }

    private static async Task WriteMasterPlaylistAsync(
        string root,
        IReadOnlyList<HlsRenditionProfile> renditions,
        IReadOnlyList<PlaybackTrackDto> audioTracks,
        IReadOnlyList<HlsCaption> captions,
        bool copiedVideo,
        CancellationToken ct)
    {
        var builder = new StringBuilder("#EXTM3U\n#EXT-X-VERSION:3\n#EXT-X-INDEPENDENT-SEGMENTS\n");
        for (var index = 0; index < audioTracks.Count; index++)
        {
            var track = audioTracks[index];
            builder.Append("#EXT-X-MEDIA:TYPE=AUDIO,GROUP-ID=\"audio\",NAME=\"")
                .Append(Escape(track.DisplayName ?? $"Audio {index + 1}"))
                .Append("\",LANGUAGE=\"").Append(Escape(track.Language ?? "und"))
                .Append("\",DEFAULT=").Append(track.IsDefault || index == 0 ? "YES" : "NO")
                .Append(",AUTOSELECT=YES,URI=\"a").Append(index).Append("/index.m3u8\"\n");
        }
        for (var index = 0; index < captions.Count; index++)
        {
            var track = captions[index];
            builder.Append("#EXT-X-MEDIA:TYPE=SUBTITLES,GROUP-ID=\"subs\",NAME=\"")
                .Append(Escape(track.Language.ToUpperInvariant())).Append("\",LANGUAGE=\"")
                .Append(Escape(track.Language)).Append("\",DEFAULT=")
                .Append(track.IsDefault ? "YES" : "NO")
                .Append(",AUTOSELECT=YES,FORCED=NO,URI=\"s").Append(index).Append("/index.m3u8\"\n");
        }
        for (var index = 0; index < renditions.Count; index++)
        {
            var rendition = renditions[index];
            var bandwidth = (rendition.VideoBitrateKbps + 160) * 1000;
            builder.Append("#EXT-X-STREAM-INF:BANDWIDTH=").Append(bandwidth);
            // A copied picture keeps the file's own codec and level, which are not known here, so no CODECS guess is written for it.
            if (!copiedVideo)
            {
                builder.Append(audioTracks.Count > 0
                    ? ",CODECS=\"avc1.4d401f,mp4a.40.2\""
                    : ",CODECS=\"avc1.4d401f\"");
            }
            if (audioTracks.Count > 0)
            {
                builder.Append(",AUDIO=\"audio\"");
            }

            if (captions.Count > 0)
            {
                builder.Append(",SUBTITLES=\"subs\"");
            }

            builder.Append("\nv").Append(index).Append("/index.m3u8\n");
        }
        await File.WriteAllTextAsync(Path.Combine(root, "master.m3u8.tmp"), builder.ToString(), new UTF8Encoding(false), ct)
            .ConfigureAwait(false);
        File.Move(Path.Combine(root, "master.m3u8.tmp"), Path.Combine(root, "master.m3u8"), true);
    }

    /// <summary>
    /// Picks the encoder for one start and says where it runs and, when the graphics card was expected but is not used, why not.
    /// Automatic tries the graphics card; the processor is the stated fallback (playback-path D5).
    /// </summary>
    internal static HlsEncoderChoice ChooseEncoder(string configured, HardwareCapabilities hardware)
    {
        var mode = configured.Trim().ToLowerInvariant();
        const string processor = PlaybackEncodePlaces.Processor;
        if (mode is "none" or "cpu")
        {
            return new HlsEncoderChoice("libx264", processor, "Processor only is chosen in Playback settings");
        }

        var wanted = mode switch
        {
            "nvenc" => "h264_nvenc",
            "quicksync" => "h264_qsv",
            "vaapi" => "h264_vaapi",
            _ => null,
        };
        var available = mode switch
        {
            "nvenc" => hardware.HasNvenc,
            "quicksync" => hardware.HasQuickSync,
            "vaapi" => hardware.HasVaapi,
            _ => hardware.PreferredEncoder != "libx264",
        };
        if (available)
        {
            return new HlsEncoderChoice(wanted ?? hardware.PreferredEncoder, PlaybackEncodePlaces.GraphicsCard, null);
        }

        var failure = hardware.ProbeFailures.FirstOrDefault(pair => wanted is null || pair.Key == wanted);
        var reason = failure.Key is not null
            ? HardwareReasons.Plain(failure.Value)
            : "No graphics card encoding was found";
        return new HlsEncoderChoice("libx264", processor, reason);
    }

    /// <summary>
    /// One quality right-sized for the player (playback-path D4): the tallest configured quality that does not exceed the source, the
    /// player's own limit or the connection's cap. When a remote or slow connection asks, the next lower quality is added beside it.
    /// </summary>
    internal static IReadOnlyList<HlsRenditionProfile> PickRenditions(AdaptiveHlsSettings settings, int sourceHeight, HlsStartPlan plan)
    {
        var limit = sourceHeight;
        if (plan.MaxHeight is > 0)
        {
            limit = Math.Min(limit, plan.MaxHeight.Value);
        }

        var pool = SelectRenditions(settings, limit);
        if (plan.MaxBitrateKbps is > 0)
        {
            var affordable = pool.Where(r => r.VideoBitrateKbps <= plan.MaxBitrateKbps.Value).ToList();
            pool = affordable.Count > 0 ? affordable : [pool[^1]];
        }

        var chosen = pool[0];
        var picked = new List<HlsRenditionProfile> { chosen };
        if (plan.OfferLowerQuality)
        {
            var lower = pool.FirstOrDefault(r => r.Height < chosen.Height);
            if (lower is not null)
            {
                picked.Add(lower);
            }
        }

        return picked;
    }

    internal static IReadOnlyList<HlsRenditionProfile> SelectRenditions(AdaptiveHlsSettings settings, int sourceHeight)
    {
        var selected = settings.Renditions
            .Where(rendition => rendition.Height <= sourceHeight)
            .OrderByDescending(rendition => rendition.Height)
            .ToList();
        if (selected.Count == 0)
        {
            var height = Math.Max(144, sourceHeight / 2 * 2);
            selected.Add(new HlsRenditionProfile
            {
                Name = $"{height}p",
                Height = height,
                VideoBitrateKbps = 900,
                MaxRateKbps = 1000,
                BufferSizeKbps = 1800,
            });
        }
        return selected;
    }

    private static string BuildProfileKey(AdaptiveHlsSettings settings, HlsStartPlan plan)
    {
        var json = JsonSerializer.Serialize(settings);
        var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json)))[..16];
        var audio = string.Join('+', plan.Decision.CopyAudioCodecs.Order(StringComparer.Ordinal));
        return $"progressive-v3:{settings.ProfileName}:{plan.Decision.Rung}:h{plan.MaxHeight?.ToString() ?? "-"}:b{plan.MaxBitrateKbps?.ToString() ?? "-"}:{(plan.OfferLowerQuality ? "ladder" : "single")}:a{audio}:{fingerprint}";
    }

    private static bool HasPublishedSegment(string directory)
    {
        var playlist = Path.Combine(directory, "index.m3u8");
        if (!File.Exists(playlist))
        {
            return false;
        }
        try
        {
            return File.ReadLines(playlist).Any(line => !line.StartsWith('#') && !string.IsNullOrWhiteSpace(line)
                && File.Exists(Path.Combine(directory, Path.GetFileName(line.Trim()))));
        }
        catch (IOException) { return false; }
    }

    private string ResolveCacheRoot(TranscodingSettings settings)
    {
        var path = string.IsNullOrWhiteSpace(settings.VariantCachePath) ? ".data/variants" : settings.VariantCachePath;
        var libraryRoot = _configuration.LoadCore().LibraryRoot;
        if (string.IsNullOrWhiteSpace(libraryRoot))
        {
            libraryRoot = AppContext.BaseDirectory;
        }

        return Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(libraryRoot, path));
    }

    private static void NormalizePlaylist(string path)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("FFmpeg did not produce the expected HLS playlist.", path);
        }

        var lines = File.ReadAllLines(path);
        for (var index = 0; index < lines.Length; index++)
        {
            if (!string.IsNullOrWhiteSpace(lines[index]) && !lines[index].StartsWith('#'))
            {
                lines[index] = Path.GetFileName(lines[index].Replace('\\', '/'));
            }
        }
        File.WriteAllLines(path + ".normalized", lines, new UTF8Encoding(false));
        File.Move(path + ".normalized", path, true);
    }

    private static void ValidatePackage(string root)
    {
        var master = Path.Combine(root, "master.m3u8");
        if (!File.Exists(master))
        {
            throw new InvalidOperationException("The HLS master playlist was not generated.");
        }

        var playlists = Directory.EnumerateFiles(root, "*.m3u8", SearchOption.AllDirectories).ToList();
        if (playlists.Count < 2 || !Directory.EnumerateFiles(root, "*.ts", SearchOption.AllDirectories).Any())
        {
            throw new InvalidOperationException("The HLS package does not contain playable rendition segments.");
        }

        foreach (var playlist in playlists)
        {
            foreach (var rawLine in File.ReadLines(playlist).Where(line => !string.IsNullOrWhiteSpace(line)))
            {
                var line = rawLine.Trim();
                string? resource = null;
                if (!line.StartsWith('#'))
                {
                    resource = line;
                }
                else
                {
                    const string uriMarker = "URI=\"";
                    var uriStart = line.IndexOf(uriMarker, StringComparison.Ordinal);
                    if (uriStart >= 0)
                    {
                        uriStart += uriMarker.Length;
                        var uriEnd = line.IndexOf('"', uriStart);
                        if (uriEnd > uriStart)
                        {
                            resource = line[uriStart..uriEnd];
                        }
                    }
                }

                if (resource is null)
                {
                    continue;
                }

                var referenced = Path.GetFullPath(Path.Combine(
                    Path.GetDirectoryName(playlist)!,
                    resource.Replace('/', Path.DirectorySeparatorChar)));
                if (!referenced.StartsWith(Path.GetFullPath(root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                    || !File.Exists(referenced))
                {
                    throw new InvalidOperationException($"HLS playlist references missing resource '{resource}'.");
                }
            }
        }
    }

    private static string ContentTypeFor(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".m3u8" => "application/vnd.apple.mpegurl",
        ".ts" => "video/mp2t",
        ".vtt" => "text/vtt; charset=utf-8",
        ".m4s" => "video/iso.segment",
        ".mp4" => "video/mp4",
        ".json" => "application/json",
        _ => "application/octet-stream",
    };

    private void Release(Guid packageId) => _activeReaders.AddOrUpdate(packageId, 0, (_, current) => Math.Max(0, current - 1));

    private static string Escape(string value) => value.Replace("\\", string.Empty).Replace("\"", "'");
    private static string Tail(string value) => value.Length <= 1000 ? value : value[^1000..];
    private static void DeleteDirectory(string path)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }
    }

    private sealed record HlsCaption(string Language, bool IsDefault);
}

public sealed record AdaptiveHlsPreparation(Guid PackageId, string Status, string? Error, AdaptiveHlsRunInfo? Run = null);

/// <summary>How a stream is to be prepared: the ladder's decision plus the connection's limits (playback-path D1, D4).</summary>
/// <param name="Decision">Which rung, and whether picture and sound are copied.</param>
/// <param name="MaxHeight">The tallest picture the player or connection allows, or null for no limit.</param>
/// <param name="MaxBitrateKbps">The most video bitrate the connection can carry, or null.</param>
/// <param name="OfferLowerQuality">True when a remote or slow connection needs a second, lower quality beside the first.</param>
public sealed record HlsStartPlan(PlaybackDecision Decision, int? MaxHeight = null, int? MaxBitrateKbps = null, bool OfferLowerQuality = false)
{
    /// <summary>What a caller with no ladder (an older cast path) gets: encode the picture, copy nothing.</summary>
    public static HlsStartPlan EncodeDefault { get; } = new(new PlaybackDecision(PlaybackRungs.EncodePicture, "The player needs a prepared stream", false, []));
}

/// <summary>The encoder one start uses, where it runs, and why the graphics card was not used when it was expected.</summary>
public sealed record HlsEncoderChoice(string Encoder, string RanOn, string? FallbackReason);

/// <summary>A live view of one package being prepared. Written by the packager, read (as a snapshot) for the manifest.</summary>
public sealed class AdaptiveHlsRunInfo
{
    public bool WaitingForSlot { get; set; }
    public string Encoder { get; set; } = "libx264";
    public string RanOn { get; set; } = PlaybackEncodePlaces.Processor;
    public string? FallbackReason { get; set; }
    public int? PercentPrepared { get; init; }
    internal double DurationSeconds { get; set; }

    internal AdaptiveHlsRunInfo Snapshot(int? percent) => new()
    {
        WaitingForSlot = WaitingForSlot,
        Encoder = Encoder,
        RanOn = RanOn,
        FallbackReason = FallbackReason,
        PercentPrepared = percent,
        DurationSeconds = DurationSeconds,
    };
}

/// <summary>
/// Limits how many streams are prepared at once. Unlike a fixed semaphore, the limit is asked for again each time, so a change to
/// "Maximum active encodes" applies to the next start without restarting the Engine.
/// </summary>
public sealed class EncodeSlotGate(Func<int> limit)
{
    private readonly object _gate = new();
    private int _active;

    public async Task WaitAsync(CancellationToken ct)
    {
        while (true)
        {
            lock (_gate)
            {
                if (_active < Math.Max(1, limit()))
                {
                    _active++;
                    return;
                }
            }

            await Task.Delay(500, ct).ConfigureAwait(false);
        }
    }

    public void Release()
    {
        lock (_gate)
        {
            _active = Math.Max(0, _active - 1);
        }
    }
}

public sealed class HlsResourceLease(Stream stream, string contentType, Action release) : IAsyncDisposable
{
    public Stream Stream { get; } = stream;
    public string ContentType { get; } = contentType;

    public async ValueTask DisposeAsync()
    {
        await Stream.DisposeAsync().ConfigureAwait(false);
        release();
    }
}
