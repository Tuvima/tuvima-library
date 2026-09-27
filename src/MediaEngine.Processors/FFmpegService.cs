using System.Diagnostics;
using System.Text.Json;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Models;
using Microsoft.Extensions.Logging;

namespace MediaEngine.Processors;

/// <summary>
/// Locates FFmpeg binaries and exposes probing and command execution.
///
/// <para>
/// <b>Auto-detection order (first found wins):</b>
/// <list type="number">
///   <item>Explicit paths from <c>config/transcoding.json</c> — authoritative deployment configuration.</item>
///   <item><c>tools/ffmpeg/</c> relative to the app base directory — self-contained deployment.</item>
///   <item>System PATH — user-managed system-wide installation.</item>
/// </list>
/// </para>
/// </summary>
public sealed class FFmpegService : IFFmpegService
{
    private readonly ILogger<FFmpegService> _logger;

    public string? FfmpegPath { get; }
    public string? FfprobePath { get; }
    public bool IsAvailable => FfmpegPath is not null && FfprobePath is not null;

    public HardwareCapabilities HardwareCapabilities { get; }

    public FFmpegService(IConfigurationLoader config, ILogger<FFmpegService> logger)
    {
        _logger = logger;

        var settings = config.LoadTranscoding();

        // ── Binary resolution ────────────────────────────────────────────────
        FfmpegPath = ResolveBinary("ffmpeg", settings.FfmpegBinaryPath);
        FfprobePath = ResolveBinary("ffprobe", settings.FfprobeBinaryPath);

        if (IsAvailable)
        {
            _logger.LogInformation("FFmpeg located: {Path}", FfmpegPath);
            HardwareCapabilities = DetectHardware(settings.HardwareAcceleration);
            _logger.LogInformation(
                "Hardware acceleration: {Accel} (preferred encoder: {Enc})",
                HardwareCapabilities.DetectedAccelerator ?? "none (software)",
                HardwareCapabilities.PreferredEncoder);
        }
        else
        {
            _logger.LogWarning(
                "FFmpeg binaries not found. Check explicit transcoding paths, tools/ffmpeg/, " +
                "or the system PATH. Transcoding and video metadata extraction are disabled.");
            HardwareCapabilities = new HardwareCapabilities();
        }
    }

    // ─────────────────────────────────────────────────────────────────────────

    public async Task<MediaProbeResult?> ProbeAsync(string filePath, CancellationToken ct = default)
    {
        if (!IsAvailable || !File.Exists(filePath))
        {
            return null;
        }

        // ffprobe -v quiet -print_format json -show_format -show_streams -show_chapters
        var args = $"-v quiet -print_format json -show_format -show_streams -show_chapters \"{filePath}\"";
        var (exit, stdout, _) = await RunProcessAsync(FfprobePath!, args, ct).ConfigureAwait(false);
        if (exit != 0 || string.IsNullOrWhiteSpace(stdout))
        {
            return null;
        }

        return ParseProbeJson(stdout, filePath);
    }

    public async Task<(int ExitCode, string Output, string Error)> RunAsync(
        string arguments, CancellationToken ct = default)
    {
        if (!IsAvailable)
        {
            throw new InvalidOperationException("FFmpeg is not available. Check tools/ffmpeg/ or system PATH.");
        }

        return await RunProcessAsync(FfmpegPath!, arguments, ct).ConfigureAwait(false);
    }

    public async Task<(int ExitCode, string Output, string Error)> RunAsync(
        IReadOnlyList<string> arguments, CancellationToken ct = default)
    {
        if (!IsAvailable)
        {
            throw new InvalidOperationException("FFmpeg is not available. Check tools/ffmpeg/ or system PATH.");
        }

        return await RunProcessAsync(FfmpegPath!, arguments, ct).ConfigureAwait(false);
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    private static string? ResolveBinary(string name, string? configuredPath)
    {
        // 1. An explicit deployment path is authoritative. Do not silently
        // hide a stale container/NAS configuration by falling back elsewhere.
        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            return File.Exists(configuredPath) ? Path.GetFullPath(configuredPath) : null;
        }

        // 2. tools/ffmpeg/ relative to app base directory
        var appBase = AppContext.BaseDirectory;
        // Walk up from bin/Debug/net10.0/ to repo root looking for tools/ffmpeg/
        var dir = new DirectoryInfo(appBase);
        for (int depth = 0; depth < 6; depth++)
        {
            if (dir is null)
            {
                break;
            }

            var candidate = Path.Combine(dir.FullName, "tools", "ffmpeg",
                OperatingSystem.IsWindows() ? $"{name}.exe" : name);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            dir = dir.Parent;
        }

        // 3. System PATH
        var onPath = FindOnPath(name);
        return onPath;
    }

    private static string? FindOnPath(string name)
    {
        var exe = OperatingSystem.IsWindows() ? $"{name}.exe" : name;
        var paths = (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);
        return paths
            .Select(p => Path.Combine(p, exe))
            .FirstOrDefault(File.Exists);
    }

    private HardwareCapabilities DetectHardware(string mode)
    {
        try
        {
            mode = mode.Trim().ToLowerInvariant();
            // Query available encoders synchronously at startup.
            var psi = new ProcessStartInfo(FfmpegPath!, "-hide_banner -encoders")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var p = Process.Start(psi);
            if (p is null)
            {
                return new HardwareCapabilities();
            }

            var output = p.StandardOutput.ReadToEnd();
            p.WaitForExit(10_000);
            var muxers = QueryCapabilityOutput("-hide_banner -muxers");

            bool hasNvenc = ListsCapability(output, "h264_nvenc");
            bool hasQsv = ListsCapability(output, "h264_qsv");
            bool hasVaapi = ListsCapability(output, "h264_vaapi");
            bool hasLibx264 = ListsCapability(output, "libx264");
            bool hasAac = ListsCapability(output, "aac");
            bool hasWebVtt = ListsCapability(output, "webvtt");
            bool hasHls = ListsCapability(muxers, "hls");

            hasNvenc = hasNvenc && ProbeEncoder("h264_nvenc");
            hasQsv = hasQsv && ProbeEncoder("h264_qsv");
            hasVaapi = hasVaapi && ProbeEncoder("h264_vaapi");

            // If mode is "auto", pick best; if mode is explicit, honour it.
            bool softwareOnly = mode is "none" or "cpu";
            bool useNvenc = !softwareOnly && hasNvenc && (mode is "auto" or "nvenc");
            bool useQsv = !softwareOnly && hasQsv && (mode is "auto" or "quicksync") && !useNvenc;
            bool useVaapi = !softwareOnly && hasVaapi && (mode is "auto" or "vaapi") && !useNvenc && !useQsv;

            string encoder = useNvenc ? "h264_nvenc" : useQsv ? "h264_qsv" : useVaapi ? "h264_vaapi" : "libx264";
            string? accelName = useNvenc ? "NVIDIA NVENC" : useQsv ? "Intel Quick Sync" : useVaapi ? "VAAPI" : null;

            return new HardwareCapabilities
            {
                HasNvenc = hasNvenc,
                HasQuickSync = hasQsv,
                HasVaapi = hasVaapi,
                PreferredEncoder = encoder,
                DetectedAccelerator = accelName,
                HasHlsMuxer = hasHls,
                HasH264Encoder = hasLibx264 || hasNvenc || hasQsv || hasVaapi,
                HasAacEncoder = hasAac,
                HasWebVttEncoder = hasWebVtt,
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Hardware capability detection failed — using software encoding");
            return new HardwareCapabilities();
        }
    }

    private static async Task<(int ExitCode, string Output, string Error)> RunProcessAsync(
        string binary, string arguments, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(binary, arguments)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        using var process = new Process { StartInfo = psi };
        process.Start();

        // Drain both redirected pipes concurrently. Reading them serially can deadlock when
        // ffmpeg fills stderr while the caller is still waiting for stdout to complete.
        var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = process.StandardError.ReadToEndAsync(ct);
        await Task.WhenAll(stdoutTask, stderrTask, process.WaitForExitAsync(ct)).ConfigureAwait(false);

        return (process.ExitCode, await stdoutTask, await stderrTask);
    }

    private static bool ListsCapability(string output, string name) => output
        .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
        .Select(line => line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        .Any(parts => parts.Length >= 2 && string.Equals(parts[1], name, StringComparison.OrdinalIgnoreCase));

    private static async Task<(int ExitCode, string Output, string Error)> RunProcessAsync(
        string binary, IReadOnlyList<string> arguments, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(binary)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in arguments)
        {
            psi.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = psi };
        process.Start();
        var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = process.StandardError.ReadToEndAsync(ct);
        try
        {
            await Task.WhenAll(stdoutTask, stderrTask, process.WaitForExitAsync(ct)).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch (InvalidOperationException)
            {
            }
            throw;
        }

        return (process.ExitCode, await stdoutTask, await stderrTask);
    }

    private static MediaProbeResult? ParseProbeJson(string json, string filePath)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            // ── Format (container-level metadata) ────────────────────────────
            var format = root.TryGetProperty("format", out var f) ? f : (JsonElement?)null;
            var tags = format?.TryGetProperty("tags", out var t) == true ? t : (JsonElement?)null;

            double durationSec = 0;
            if (format?.TryGetProperty("duration", out var durEl) == true &&
                double.TryParse(durEl.GetString(), System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var d))
            {
                durationSec = d;
            }

            long fileSize = 0;
            if (format?.TryGetProperty("size", out var sizeEl) == true &&
                long.TryParse(sizeEl.GetString(), out var s))
            {
                fileSize = s;
            }

            string? Tag(string key)
            {
                if (tags is null)
                {
                    return null;
                }

                foreach (var kv in new[] { key, key.ToUpperInvariant(), key.ToLowerInvariant() })
                {
                    if (tags.Value.TryGetProperty(kv, out var v))
                    {
                        return v.GetString();
                    }
                }

                return null;
            }

            var captureText = Tag("com.apple.quicktime.creationdate") ?? Tag("creation_time");
            var capturedAt = DateTimeOffset.TryParse(
                captureText,
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeUniversal,
                out var parsedCapture) ? parsedCapture : (DateTimeOffset?)null;
            var location = Tag("com.apple.quicktime.location.ISO6709") ?? Tag("location");
            var (latitude, longitude) = ParseIso6709(location);

            // ── Streams ───────────────────────────────────────────────────────
            string? audioCodec = null;
            string? audioLanguage = null;
            int? bitrate = null;
            int? sampleRate = null;
            int? channels = null;
            string? videoCodec = null;
            int? width = null;
            int? height = null;
            double? frameRate = null;
            bool hasCover = false;
            var subtitleLanguages = new List<string>();
            var audioStreams = new List<MediaProbeAudioStream>();

            if (root.TryGetProperty("streams", out var streams))
            {
                foreach (var stream in streams.EnumerateArray())
                {
                    var codecType = stream.TryGetProperty("codec_type", out var ct2) ? ct2.GetString() : null;
                    var codecName = stream.TryGetProperty("codec_name", out var cn) ? cn.GetString() : null;

                    if (codecType == "audio")
                    {
                        var language = stream.TryGetProperty("tags", out var audioStreamTags) ? TryReadLanguage(audioStreamTags) : null;
                        var isDefault = stream.TryGetProperty("disposition", out var disposition)
                            && disposition.TryGetProperty("default", out var defaultFlag) && defaultFlag.GetInt32() == 1;
                        audioStreams.Add(new MediaProbeAudioStream(audioStreams.Count, codecName, language, isDefault));
                    }

                    if (codecType == "audio" && audioCodec is null)
                    {
                        audioCodec = codecName;
                        if (stream.TryGetProperty("tags", out var audioTags))
                        {
                            audioLanguage = TryReadLanguage(audioTags);
                        }
                        if (stream.TryGetProperty("bit_rate", out var br) &&
                            int.TryParse(br.GetString(), out var brv))
                        {
                            bitrate = brv / 1000;
                        }

                        if (stream.TryGetProperty("sample_rate", out var sr) &&
                            int.TryParse(sr.GetString(), out var srv))
                        {
                            sampleRate = srv;
                        }

                        if (stream.TryGetProperty("channels", out var ch))
                        {
                            channels = ch.GetInt32();
                        }
                    }
                    else if (codecType == "video")
                    {
                        // Check if this is an attached picture (cover art)
                        var disp = stream.TryGetProperty("disposition", out var dispEl) ? dispEl : (JsonElement?)null;
                        bool isAttached = disp?.TryGetProperty("attached_pic", out var ap) == true && ap.GetInt32() == 1;

                        if (isAttached)
                        {
                            hasCover = true;
                        }
                        else if (videoCodec is null)
                        {
                            videoCodec = codecName;
                            if (stream.TryGetProperty("width", out var w))
                            {
                                width = w.GetInt32();
                            }

                            if (stream.TryGetProperty("height", out var h))
                            {
                                height = h.GetInt32();
                            }

                            if (stream.TryGetProperty("r_frame_rate", out var fr))
                            {
                                var frs = fr.GetString() ?? "0/1";
                                var parts = frs.Split('/');
                                if (parts.Length == 2 &&
                                    double.TryParse(parts[0], out var num) &&
                                    double.TryParse(parts[1], out var den) && den != 0)
                                {
                                    frameRate = Math.Round(num / den, 3);
                                }
                            }
                        }
                    }
                    else if (codecType == "subtitle")
                    {
                        string? subtitleLanguage = null;
                        if (stream.TryGetProperty("tags", out var subtitleTags))
                        {
                            subtitleLanguage = TryReadLanguage(subtitleTags);
                        }

                        subtitleLanguage ??= codecName;
                        if (!string.IsNullOrWhiteSpace(subtitleLanguage))
                        {
                            subtitleLanguages.Add(subtitleLanguage);
                        }
                    }
                }
            }

            // ── Chapters ──────────────────────────────────────────────────────
            int chapterCount = 0;
            var parsedChapters = new List<MediaProbeChapter>();
            if (root.TryGetProperty("chapters", out var chapters))
            {
                chapterCount = chapters.GetArrayLength();
                var chapterIndex = 0;
                foreach (var chapter in chapters.EnumerateArray())
                {
                    var startSeconds = TryReadSeconds(chapter, "start_time");
                    if (startSeconds is null)
                    {
                        chapterIndex++;
                        continue;
                    }

                    var endSeconds = TryReadSeconds(chapter, "end_time");
                    if (endSeconds <= startSeconds)
                    {
                        endSeconds = null;
                    }

                    string? title = null;
                    if (chapter.TryGetProperty("tags", out var chapterTags))
                    {
                        title = TryReadTag(chapterTags, "title");
                    }

                    parsedChapters.Add(new MediaProbeChapter(
                        chapterIndex,
                        string.IsNullOrWhiteSpace(title) ? null : title,
                        Math.Max(0, startSeconds.Value),
                        endSeconds));
                    chapterIndex++;
                }
            }

            return new MediaProbeResult
            {
                Duration = TimeSpan.FromSeconds(durationSec),
                FileSizeBytes = fileSize > 0 ? fileSize : new FileInfo(filePath).Length,
                Title = Tag("title"),
                Artist = Tag("artist"),
                Album = Tag("album"),
                AlbumArtist = Tag("album_artist"),
                Genre = Tag("genre"),
                Date = Tag("date"),
                Comment = Tag("comment"),
                Narrator = Tag("narrator") ?? Tag("composer"),
                Publisher = Tag("publisher"),
                Description = Tag("description") ?? Tag("comment"),
                CapturedAt = capturedAt,
                DeviceMake = Tag("com.apple.quicktime.make") ?? Tag("make"),
                DeviceModel = Tag("com.apple.quicktime.model") ?? Tag("model"),
                Latitude = latitude,
                Longitude = longitude,
                ContentIdentifier = Tag("com.apple.quicktime.content.identifier"),
                IsLivePhoto = string.Equals(Tag("com.apple.quicktime.live-photo.auto"), "1", StringComparison.OrdinalIgnoreCase),
                AudioStreams = audioStreams,
                AudioLanguage = audioLanguage,
                AudioCodec = audioCodec,
                AudioBitrate = bitrate,
                SampleRate = sampleRate,
                Channels = channels,
                VideoCodec = videoCodec,
                Width = width,
                Height = height,
                FrameRate = frameRate,
                HasEmbeddedCover = hasCover,
                ChapterCount = chapterCount,
                Chapters = parsedChapters,
                SubtitleLanguages = subtitleLanguages,
            };
        }
        catch
        {
            return null;
        }
    }

    private string QueryCapabilityOutput(string arguments)
    {
        var psi = new ProcessStartInfo(FfmpegPath!, arguments)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using var process = Process.Start(psi);
        if (process is null)
        {
            return string.Empty;
        }

        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit(10_000);
        return output;
    }

    private bool ProbeEncoder(string encoder)
    {
        try
        {
            var psi = new ProcessStartInfo(FfmpegPath!)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            foreach (var argument in new[]
                     {
                         "-hide_banner", "-loglevel", "error", "-f", "lavfi", "-i", "color=size=64x64:rate=1",
                         "-frames:v", "1", "-c:v", encoder, "-f", "null", "-",
                     })
            {
                psi.ArgumentList.Add(argument);
            }

            using var process = Process.Start(psi);
            if (process is null)
            {
                return false;
            }

            _ = process.StandardOutput.ReadToEnd();
            _ = process.StandardError.ReadToEnd();
            return process.WaitForExit(10_000) && process.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    private static string? TryReadLanguage(JsonElement tags)
    {
        foreach (var key in new[] { "language", "LANGUAGE", "Language" })
        {
            if (tags.TryGetProperty(key, out var value))
            {
                var raw = value.GetString();
                if (!string.IsNullOrWhiteSpace(raw))
                {
                    return raw;
                }
            }
        }

        return null;
    }

    private static double? TryReadSeconds(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value))
        {
            return null;
        }

        if (value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var numeric))
        {
            return numeric;
        }

        if (value.ValueKind == JsonValueKind.String
            && double.TryParse(
                value.GetString(),
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture,
                out var parsed))
        {
            return parsed;
        }

        return null;
    }

    private static string? TryReadTag(JsonElement tags, string key)
    {
        foreach (var candidate in new[] { key, key.ToUpperInvariant(), key.ToLowerInvariant() })
        {
            if (tags.TryGetProperty(candidate, out var value))
            {
                return value.GetString();
            }
        }

        return null;
    }

    private static (double? Latitude, double? Longitude) ParseIso6709(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return (null, null);
        }

        var match = System.Text.RegularExpressions.Regex.Match(
            value,
            @"^(?<lat>[+-]\d+(?:\.\d+)?)(?<lon>[+-]\d+(?:\.\d+)?)");
        if (!match.Success
            || !double.TryParse(match.Groups["lat"].Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var latitude)
            || !double.TryParse(match.Groups["lon"].Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var longitude))
        {
            return (null, null);
        }

        return latitude is >= -90 and <= 90 && longitude is >= -180 and <= 180
            ? (latitude, longitude)
            : (null, null);
    }
}
