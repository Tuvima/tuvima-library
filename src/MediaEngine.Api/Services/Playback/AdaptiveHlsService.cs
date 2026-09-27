using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MediaEngine.Contracts.Playback;
using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Enums;
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
    private readonly SemaphoreSlim _encodeSlots;

    public AdaptiveHlsService(
        AdaptiveHlsPackageRepository packages,
        IMediaAssetRepository assets,
        ITextTrackRepository textTracks,
        IFFmpegService ffmpeg,
        IConfigurationLoader configuration,
        IHostApplicationLifetime lifetime,
        ILogger<AdaptiveHlsService> logger)
    {
        _packages = packages;
        _assets = assets;
        _textTracks = textTracks;
        _ffmpeg = ffmpeg;
        _configuration = configuration;
        _lifetime = lifetime;
        _logger = logger;
        _encodeSlots = new SemaphoreSlim(Math.Clamp(configuration.LoadTranscoding().MaxConcurrentTranscodes, 1, 8));
    }

    public async Task<AdaptiveHlsPreparation> EnsurePackageAsync(
        Guid assetId,
        string sourceHash,
        IReadOnlyList<PlaybackTrackDto> audioTracks,
        CancellationToken ct = default)
    {
        var settings = _configuration.LoadTranscoding();
        var profileKey = BuildProfileKey(settings.AdaptiveHls);
        var existing = await _packages.FindAsync(assetId, sourceHash, profileKey, ct).ConfigureAwait(false);
        if (existing is not null && _preparations.ContainsKey(existing.Id)
            && File.Exists(Path.Combine(existing.RootPath, "master.m3u8")))
            return new AdaptiveHlsPreparation(existing.Id, "streaming", null);
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
            _ => new Lazy<Task>(() => PreparePackageAsync(package, audioTracks, _lifetime.ApplicationStopping))).Value;
        var wait = TimeSpan.FromSeconds(Math.Clamp(settings.AdaptiveHls.PreparationWaitSeconds, 1, 60));
        try
        {
            var deadline = DateTimeOffset.UtcNow + wait;
            while (!task.IsCompleted && DateTimeOffset.UtcNow < deadline)
            {
                if (File.Exists(Path.Combine(package.RootPath, "master.m3u8")))
                    return new AdaptiveHlsPreparation(package.Id, "streaming", null);
                await Task.Delay(250, ct).ConfigureAwait(false);
            }
            if (!task.IsCompleted) return new AdaptiveHlsPreparation(package.Id, "preparing", null);
            await task.ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            return new AdaptiveHlsPreparation(package.Id, "preparing", null);
        }

        var refreshed = await _packages.FindByIdAsync(package.Id, ct).ConfigureAwait(false);
        return new AdaptiveHlsPreparation(
            package.Id,
            refreshed?.Status ?? "failed",
            refreshed?.LastError);
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

    public bool IsActive(Guid packageId) => _preparations.ContainsKey(packageId) || (_activeReaders.TryGetValue(packageId, out var count) && count > 0);

    private async Task PreparePackageAsync(
        AdaptiveHlsPackageRecord package,
        IReadOnlyList<PlaybackTrackDto> audioTracks,
        CancellationToken ct)
    {
        await _encodeSlots.WaitAsync(ct).ConfigureAwait(false);
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
            var probe = await _ffmpeg.ProbeAsync(asset.FilePathRoot, ct).ConfigureAwait(false);
            if (probe?.Height is not > 0)
            {
                throw new InvalidOperationException("The source video dimensions could not be inspected.");
            }

            var renditions = SelectRenditions(settings.AdaptiveHls, probe.Height.Value).OrderBy(r => r.Height).ToArray();
            // Captions can require reading the entire source. Publish video independently.
            captionTask = PrepareCaptionsAsync(package.AssetId, asset.FilePathRoot, staging, probe, ct);
            var encoder = ResolveEncoder(settings.HardwareAcceleration);
            if (audioTracks.Count == 0 && !string.IsNullOrWhiteSpace(probe.AudioCodec))
            {
                audioTracks = [new PlaybackTrackDto { Index = 0, Codec = probe.AudioCodec, Language = probe.AudioLanguage, DisplayName = "Audio", IsDefault = true }];
            }
            if (audioTracks.Count > 0)
            {
                Directory.CreateDirectory(Path.Combine(staging, "a0"));
                firstAudio = EncodeAudioAsync(asset.FilePathRoot, Path.Combine(staging, "a0"), 0, settings.AdaptiveHls.SegmentSeconds, ct);
            }
            for (var index = 0; index < renditions.Length; index++)
            {
                var rendition = renditions[index];
                var directory = Path.Combine(staging, $"v{index}");
                Directory.CreateDirectory(directory);
                var encode = EncodeVideoAsync(asset.FilePathRoot, directory, rendition, encoder, settings.AdaptiveHls.SegmentSeconds, ct);
                if (index == 0)
                {
                    while (!encode.IsCompleted)
                    {
                        if (HasPublishedSegment(directory) && (firstAudio is null || HasPublishedSegment(Path.Combine(staging, "a0"))))
                        {
                            await WriteMasterPlaylistAsync(staging, [rendition], audioTracks.Take(1).ToArray(), captionTask.IsCompletedSuccessfully ? captionTask.Result : [], ct);
                            break;
                        }
                        await Task.Delay(250, ct);
                    }
                }
                var result = await encode.ConfigureAwait(false);
                if (result.ExitCode != 0 && !string.Equals(encoder, "libx264", StringComparison.Ordinal))
                {
                    _logger.LogWarning("Hardware HLS encode failed for {AssetId}; retrying with libx264: {Error}", package.AssetId, Tail(result.Error));
                    DeleteDirectory(directory);
                    Directory.CreateDirectory(directory);
                    encode = EncodeVideoAsync(asset.FilePathRoot, directory, rendition, "libx264", settings.AdaptiveHls.SegmentSeconds, ct);
                    if (index == 0)
                    {
                        while (!encode.IsCompleted)
                        {
                            if (HasPublishedSegment(directory) && (firstAudio is null || HasPublishedSegment(Path.Combine(staging, "a0"))))
                            {
                                await WriteMasterPlaylistAsync(staging, [rendition], audioTracks.Take(1).ToArray(), captionTask.IsCompletedSuccessfully ? captionTask.Result : [], ct);
                                break;
                            }
                            await Task.Delay(250, ct);
                        }
                    }
                    result = await encode.ConfigureAwait(false);
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
                var result = await (index == 0 && firstAudio is not null ? firstAudio : EncodeAudioAsync(asset.FilePathRoot, directory, index, settings.AdaptiveHls.SegmentSeconds, ct))
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
            await WriteMasterPlaylistAsync(staging, renditions, generatedAudio, captions, ct).ConfigureAwait(false);
            ValidatePackage(staging);

            var bytes = Directory.EnumerateFiles(package.RootPath, "*", SearchOption.AllDirectories)
                .Sum(path => new FileInfo(path).Length);
            await _packages.MarkReadyAsync(package.Id, package.RootPath, bytes, ct).ConfigureAwait(false);
            _logger.LogInformation("Adaptive HLS package {PackageId} is ready for asset {AssetId} ({Bytes} bytes)", package.Id, package.AssetId, bytes);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            await preparationCancellation.CancelAsync();
            if (firstAudio is not null) { try { await firstAudio; } catch (OperationCanceledException) { } }
            if (captionTask is not null) { try { await captionTask; } catch (Exception captionError) { _logger.LogDebug(captionError, "Caption preparation stopped"); } }
            DeleteDirectory(staging);
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

    private async Task<(int ExitCode, string Output, string Error)> EncodeVideoAsync(
        string input,
        string directory,
        HlsRenditionProfile rendition,
        string encoder,
        int segmentSeconds,
        CancellationToken ct)
    {
        var playlist = Path.Combine(directory, "index.m3u8");
        var segmentPattern = Path.Combine(directory, "segment_%05d.ts");
        var arguments = new List<string>
        {
            "-y", "-hide_banner", "-loglevel", "warning", "-i", input,
            "-map", "0:v:0", "-an", "-sn", "-vf", $"scale=-2:{rendition.Height}",
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

    private Task<(int ExitCode, string Output, string Error)> EncodeAudioAsync(
        string input,
        string directory,
        int streamIndex,
        int segmentSeconds,
        CancellationToken ct) => _ffmpeg.RunAsync(
        [
            "-y", "-hide_banner", "-loglevel", "warning", "-i", input,
            "-map", $"0:a:{streamIndex}", "-vn", "-sn", "-c:a", "aac", "-b:a", "160k", "-ac", "2",
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
            builder.Append("#EXT-X-STREAM-INF:BANDWIDTH=").Append(bandwidth)
                .Append(audioTracks.Count > 0
                    ? ",CODECS=\"avc1.4d401f,mp4a.40.2\""
                    : ",CODECS=\"avc1.4d401f\"");
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

    private string ResolveEncoder(string configured) => configured.ToLowerInvariant() switch
    {
        "none" or "cpu" => "libx264",
        "nvenc" when _ffmpeg.HardwareCapabilities.HasNvenc => "h264_nvenc",
        "quicksync" when _ffmpeg.HardwareCapabilities.HasQuickSync => "h264_qsv",
        "vaapi" when _ffmpeg.HardwareCapabilities.HasVaapi => "h264_vaapi",
        "gpu" or "auto" => _ffmpeg.HardwareCapabilities.PreferredEncoder,
        _ => "libx264",
    };

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

    private static string BuildProfileKey(AdaptiveHlsSettings settings)
    {
        var json = JsonSerializer.Serialize(settings);
        var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json)))[..16];
        return $"progressive-v1:{settings.ProfileName}:{fingerprint}";
    }

    private static bool HasPublishedSegment(string directory)
    {
        var playlist = Path.Combine(directory, "index.m3u8");
        if (!File.Exists(playlist)) return false;
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

public sealed record AdaptiveHlsPreparation(Guid PackageId, string Status, string? Error);

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
