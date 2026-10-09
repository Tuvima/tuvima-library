using System.Globalization;
using System.Security.Claims;
using System.Text.Json;
using Dapper;
using MediaEngine.Api.Security;
using MediaEngine.Contracts.Authentication;
using MediaEngine.Contracts.Playback;
using MediaEngine.Domain.Aggregates;
using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Enums;
using MediaEngine.Domain.Models;
using MediaEngine.Domain.Services;
using MediaEngine.Storage.Contracts;
using MediaEngine.Storage.Playback;
using MediaInfo;

namespace MediaEngine.Api.Services.Playback;

public sealed class PlaybackCapabilitiesService
{
    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".m4v", ".mkv", ".webm", ".avi",
    };

    private static readonly HashSet<string> AudioExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp3", ".m4a", ".m4b", ".aac", ".flac", ".ogg", ".wav",
    };

    private readonly IMediaAssetRepository _assets;
    private readonly IFFmpegService _ffmpeg;
    private readonly IDatabaseConnection _db;
    private readonly PlaybackStateRepository _playbackState;
    private readonly IPlaybackSegmentRepository _segments;
    private readonly ITextTrackRepository _textTracks;
    private readonly AudiobookChapterTitleOverrideRepository _chapterTitleOverrides;
    private readonly IMediaTypeExtensionCatalog _extensionCatalog;
    private readonly IConfigurationLoader _configuration;
    private readonly AdaptiveHlsService _adaptiveHls;
    private readonly HlsAccessGrantService _hlsGrants;
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly ILogger<PlaybackCapabilitiesService> _logger;

    public PlaybackCapabilitiesService(
        IMediaAssetRepository assets,
        IFFmpegService ffmpeg,
        IDatabaseConnection db,
        PlaybackStateRepository playbackState,
        IPlaybackSegmentRepository segments,
        ITextTrackRepository textTracks,
        AudiobookChapterTitleOverrideRepository chapterTitleOverrides,
        IMediaTypeExtensionCatalog extensionCatalog,
        IConfigurationLoader configuration,
        AdaptiveHlsService adaptiveHls,
        HlsAccessGrantService hlsGrants,
        IHttpContextAccessor httpContextAccessor,
        ILogger<PlaybackCapabilitiesService> logger)
    {
        _assets = assets;
        _ffmpeg = ffmpeg;
        _db = db;
        _playbackState = playbackState;
        _segments = segments;
        _textTracks = textTracks;
        _chapterTitleOverrides = chapterTitleOverrides;
        _extensionCatalog = extensionCatalog;
        _configuration = configuration;
        _adaptiveHls = adaptiveHls;
        _hlsGrants = hlsGrants;
        _httpContextAccessor = httpContextAccessor;
        _logger = logger;
    }

    public async Task<PlaybackManifestDto?> BuildManifestAsync(
        Guid assetId,
        string? client,
        Guid? profileId = null,
        CancellationToken ct = default,
        PlaybackConnectionContextDto? connection = null,
        ClientCapabilitiesDto? negotiatedCapabilities = null,
        Guid? deviceId = null)
    {
        var asset = await _assets.FindByIdAsync(assetId, ct);
        if (asset is null)
        {
            return null;
        }

        var normalizedClient = NormalizeClient(client);
        var profile = negotiatedCapabilities is null
            ? ProfileFor(normalizedClient)
            : ProfileForCapabilities(normalizedClient, negotiatedCapabilities);
        var extension = Path.GetExtension(asset.FilePathRoot);
        var mediaType = await ResolveMediaTypeAsync(asset, extension, ct);
        var warnings = new List<string>();
        connection = NormalizeConnection(connection);
        var sourceHash = BuildSourceHash(asset);

        // Ingestion owns inspection. Request-time playback consumes durable facts only.
        MediaProbeResult? probe = null;
        MediaInfoWrapper? mediaInfo = null;
        var stored = await _playbackState.GetInspectionMetadataAsync(assetId, sourceHash, ct);
        if (!string.IsNullOrWhiteSpace(stored))
        {
            try { probe = JsonSerializer.Deserialize<MediaProbeResult>(stored); }
            catch (JsonException) { warnings.Add("Stored inspection needs an ingestion refresh."); }
        }
        if (probe is null)
        {
            warnings.Add("Technical inspection has not completed during ingestion.");
        }

        var directPlay = IsDirectPlaySupported(extension, profile, mediaInfo, probe);
        var recommendedDelivery = GetRecommendedDelivery(mediaType, normalizedClient, directPlay);
        string? conversionReason = directPlay
            ? null
            : GetConversionReason(extension, profile, mediaInfo, probe);

        var variants = await _playbackState.ListOfflineVariantsAsync(assetId, sourceHash, profileId, deviceId, ct);
        var chapters = await BuildChaptersAsync(assetId, mediaInfo, probe, profileId, ct);
        var durationSeconds = ResolveManifestDurationSeconds(chapters, mediaInfo, probe);
        var sourceBitrateKbps = (durationSeconds is > 0 && probe?.FileSizeBytes > 0 ? (int?)Math.Min(int.MaxValue, probe.FileSizeBytes * 8d / durationSeconds.Value / 1000d) : null);
        var technical = BuildTechnicalInfo(extension, mediaInfo, probe);
        if (ShouldUseAdaptiveRemoteDelivery(
            mediaType,
            connection,
            technical.Height,
            sourceBitrateKbps,
            _configuration.LoadNetwork().Streaming,
            out var networkReason))
        {
            recommendedDelivery = PlaybackDeliveryModes.Hls;
            conversionReason = networkReason;
            warnings.Add(networkReason);
        }

        var audioTracks = BuildAudioTracks(mediaInfo, probe);
        string? hlsUrl = null;
        string? hlsStatus = null;
        DateTimeOffset? hlsExpiresAt = null;
        if (recommendedDelivery == PlaybackDeliveryModes.Hls)
        {
            if (!_ffmpeg.HardwareCapabilities.AdaptiveHlsReady)
            {
                hlsStatus = "unavailable";
                warnings.Add("Adaptive delivery is unavailable because FFmpeg lacks a required HLS, H.264, or AAC capability.");
            }
            else
            {
                var preparation = await _adaptiveHls.EnsurePackageAsync(assetId, sourceHash, audioTracks, ct);
                hlsStatus = preparation.Status;
                if (preparation.Status is "ready" or "streaming")
                {
                    var request = _httpContextAccessor.HttpContext;
                    var authority = request is null
                        ? null
                        : await request.RequestServices.GetRequiredService<IRequestAuthorityResolver>()
                            .ResolveAsync(request, ct).ConfigureAwait(false);
                    if (authority is null)
                    {
                        hlsStatus = "unavailable";
                        warnings.Add("Adaptive playback requires a live request authority.");
                    }
                    else
                    {
                        var tokenId = ClaimGuid(request!.User, TuvimaClaimTypes.TokenId);
                        var scopes = request.User.FindAll(TuvimaClaimTypes.Scope)
                            .Select(claim => claim.Value)
                            .ToArray();
                        var grant = _hlsGrants.Create(assetId, preparation.PackageId, authority, tokenId, scopes);
                        hlsExpiresAt = grant.ExpiresAt;
                        hlsUrl = $"/stream/hls/{Uri.EscapeDataString(grant.Value)}/{preparation.PackageId:D}/master.m3u8";
                    }
                }
                else if (preparation.Status == "preparing")
                {
                    warnings.Add("Adaptive playback is being prepared. Retry playback when preparation completes.");
                }
                else
                {
                    warnings.Add(string.IsNullOrWhiteSpace(preparation.Error)
                        ? "Adaptive playback preparation failed."
                        : $"Adaptive playback preparation failed: {preparation.Error}");
                }
            }
        }

        var resume = NormalizeResumePosition(await LoadResumeAsync(assetId, ct), durationSeconds);

        return new PlaybackManifestDto
        {
            DurationSeconds = durationSeconds,
            AssetId = assetId,
            Client = normalizedClient,
            MediaType = mediaType,
            SourceExtension = extension,
            RecommendedDelivery = recommendedDelivery,
            DirectPlaySupported = directPlay,
            DirectStreamUrl = $"/stream/{assetId}",
            HlsUrl = hlsUrl,
            HlsStatus = hlsStatus,
            HlsExpiresAt = hlsExpiresAt,
            Profile = profile,
            AudioTracks = audioTracks,
            SubtitleTracks = await BuildSubtitleTracksAsync(assetId, mediaInfo, probe, ct),
            Chapters = chapters,
            OfflineVariants = variants,
            Resume = resume,
            Segments = (await _segments.ListByAssetAsync(assetId, ct)).Select(ToSegmentDto).ToList(),
            Technical = technical,
            Warnings = warnings,
            ConversionReason = conversionReason,
            SourceBitrateKbps = sourceBitrateKbps,
            Connection = connection,
        };
    }

    private static Guid? ClaimGuid(ClaimsPrincipal principal, string claimType) =>
        Guid.TryParse(principal.FindFirstValue(claimType), out var value) && value != Guid.Empty
            ? value
            : null;

    private static PlaybackProfileDto ProfileForCapabilities(string client, ClientCapabilitiesDto capabilities) => new()
    {
        Key = client,
        DisplayName = client,
        PreferredDelivery = PlaybackDeliveryModes.DirectStream,
        SupportedContainers = capabilities.Containers,
        SupportedVideoCodecs = capabilities.VideoCodecs,
        SupportedAudioCodecs = capabilities.AudioCodecs,
        SupportedSubtitleFormats = capabilities.SubtitleFormats,
        MaxHeight = capabilities.MaxHeight,
        MaxBitrateKbps = capabilities.MaxBitrateKbps,
        SupportsPlaybackSpeed = capabilities.SupportsPlaybackSpeed,
        SupportsAlternateAudio = capabilities.MaxAudioChannels is > 1,
        SupportsSubtitles = capabilities.SubtitleFormats.Count > 0,
        SupportsOfflineDownloads = capabilities.SupportsOfflineDownloads,
    };

    private static PlaybackConnectionContextDto NormalizeConnection(PlaybackConnectionContextDto? connection)
    {
        if (connection is null || !PlaybackConnectionPaths.IsKnown(connection.ConnectionPath))
        {
            return new PlaybackConnectionContextDto();
        }

        return connection with
        {
            RemoteConnectivityProvider = string.IsNullOrWhiteSpace(connection.RemoteConnectivityProvider)
                ? null
                : connection.RemoteConnectivityProvider.Trim(),
            EstimatedBandwidthMbps = connection.EstimatedBandwidthMbps is > 0
                ? Math.Min(connection.EstimatedBandwidthMbps.Value, 100_000)
                : null,
            LatencyMs = connection.LatencyMs is >= 0
                ? Math.Min(connection.LatencyMs.Value, 120_000)
                : null,
        };
    }

    private static bool ShouldUseAdaptiveRemoteDelivery(
        string mediaType,
        PlaybackConnectionContextDto connection,
        int? sourceHeight,
        int? sourceBitrateKbps,
        NetworkStreamingSettings settings,
        out string reason)
    {
        reason = string.Empty;
        if (connection.ConnectionPath == PlaybackConnectionPaths.Local
            || mediaType.Equals("Books", StringComparison.OrdinalIgnoreCase)
            || mediaType.Equals("Comics", StringComparison.OrdinalIgnoreCase)
            || settings.RemoteQuality == RemoteStreamingQualities.Original)
        {
            return false;
        }

        var maxHeight = settings.RemoteQuality switch
        {
            RemoteStreamingQualities.Hd1080 => 1080,
            RemoteStreamingQualities.Hd720 => 720,
            RemoteStreamingQualities.DataSaver => 480,
            _ => (int?)null,
        };
        if (maxHeight.HasValue && sourceHeight > maxHeight.Value)
        {
            reason = $"Remote streaming quality is capped at {maxHeight.Value}p for this connection.";
            return true;
        }

        var estimatedMbps = connection.EstimatedBandwidthMbps;
        if (settings.UploadProtectionEnabled && estimatedMbps.HasValue)
        {
            estimatedMbps = Math.Max(0.5d, estimatedMbps.Value - settings.ReservedUploadMbps);
        }

        if (sourceBitrateKbps.HasValue && estimatedMbps.HasValue
            && sourceBitrateKbps.Value > estimatedMbps.Value * 800d)
        {
            reason = $"The source bitrate exceeds the protected remote bandwidth budget ({estimatedMbps.Value:0.#} Mbps).";
            return true;
        }

        if (settings.RemoteQuality == RemoteStreamingQualities.DataSaver && sourceBitrateKbps is > 2_000)
        {
            reason = "Data Saver is enabled for remote streaming.";
            return true;
        }

        return false;
    }

    public async Task<EncodeJobDto?> QueueEncodeAsync(
        Guid assetId,
        QueueEncodeRequestDto request,
        Guid? ownerProfileId = null,
        Guid? ownerDeviceId = null,
        CancellationToken ct = default)
    {
        var asset = await _assets.FindByIdAsync(assetId, ct);
        if (asset is null)
        {
            return null;
        }

        var profileKey = string.IsNullOrWhiteSpace(request.ProfileKey)
            ? "mobile-standard"
            : request.ProfileKey.Trim().ToLowerInvariant();

        return await _playbackState.QueueEncodeJobAsync(
            assetId,
            profileKey,
            BuildSourceHash(asset),
            request.ScheduledFor,
            ownerProfileId,
            ownerDeviceId,
            ct);
    }

    public Task<IReadOnlyList<EncodeJobDto>> ListEncodeJobsAsync(
        Guid? ownerProfileId = null,
        Guid? ownerDeviceId = null,
        CancellationToken ct = default) =>
        _playbackState.ListEncodeJobsAsync(ownerProfileId, ownerDeviceId, ct);

    public Task<bool> CancelEncodeJobAsync(
        Guid jobId,
        Guid? ownerProfileId = null,
        Guid? ownerDeviceId = null,
        CancellationToken ct = default) =>
        _playbackState.CancelEncodeJobAsync(jobId, ownerProfileId, ownerDeviceId, ct);

    public async Task<PlaybackDiagnosticsDto> GetDiagnosticsAsync(CancellationToken ct = default)
    {
        var activeJobs = await _playbackState.ListActiveEncodeJobsAsync(ct);
        var warnings = new List<string>();
        string? ffmpegVersion = null;
        if (!_ffmpeg.IsAvailable)
        {
            warnings.Add("FFmpeg is not available. Direct file streaming can work, but HLS, transcoding, thumbnails, and offline variants cannot be generated.");
        }
        else
        {
            try
            {
                var versionResult = await _ffmpeg.RunAsync("-version", ct).ConfigureAwait(false);
                ffmpegVersion = versionResult.Output
                    .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
                    .FirstOrDefault();
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                warnings.Add($"FFmpeg was located but its version probe failed: {ex.Message}");
            }
            if (!_ffmpeg.HardwareCapabilities.AdaptiveHlsReady)
            {
                warnings.Add("FFmpeg is present but does not provide every HLS, H.264, and AAC capability required for adaptive delivery.");
            }
        }

        return new PlaybackDiagnosticsDto
        {
            FFmpegAvailable = _ffmpeg.IsAvailable,
            FFmpegVersion = ffmpegVersion,
            HlsMuxerAvailable = _ffmpeg.HardwareCapabilities.HasHlsMuxer,
            H264EncoderAvailable = _ffmpeg.HardwareCapabilities.HasH264Encoder,
            AacEncoderAvailable = _ffmpeg.HardwareCapabilities.HasAacEncoder,
            WebVttEncoderAvailable = _ffmpeg.HardwareCapabilities.HasWebVttEncoder,
            PreferredHardwareEncoder = _ffmpeg.HardwareCapabilities.PreferredEncoder,
            MediaInfoAvailable = TryGetMediaInfoVersion(out var mediaInfoVersion),
            MediaInfoVersion = mediaInfoVersion,
            ActiveJobs = activeJobs,
            Warnings = warnings,
        };
    }

    public Task<OfflineVariantFile?> GetOfflineVariantFileAsync(
        Guid assetId,
        Guid variantId,
        Guid? ownerProfileId = null,
        Guid? ownerDeviceId = null,
        CancellationToken ct = default) =>
        _playbackState.GetOfflineVariantFileAsync(assetId, variantId, ownerProfileId, ownerDeviceId, ct);

    private async Task<string> ResolveMediaTypeAsync(MediaAsset asset, string extension, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        using var conn = _db.CreateConnection();
        var mediaType = conn.QueryFirstOrDefault<string>("""
            SELECT w.media_type
            FROM media_assets ma
            JOIN editions e ON e.id = ma.edition_id
            JOIN works w ON w.id = e.work_id
            WHERE ma.id = @assetId
            LIMIT 1;
            """,
            new { assetId = asset.Id });

        if (!string.IsNullOrWhiteSpace(mediaType))
        {
            return mediaType;
        }

        return _extensionCatalog.ResolveMediaTypeFromExtension(extension).ToString();
    }

    private async Task<PlaybackResumeDto?> LoadResumeAsync(Guid assetId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        using var conn = _db.CreateConnection();
        var row = conn.QueryFirstOrDefault<ResumeRow>("""
            SELECT progress_pct AS ProgressPct,
                   last_accessed AS LastAccessed,
                   extended_properties AS ExtendedProperties
            FROM user_states
            WHERE asset_id = @assetId
            ORDER BY last_accessed DESC
            LIMIT 1;
            """,
            new { assetId });

        await Task.CompletedTask;
        if (row is null)
        {
            return null;
        }

        return new PlaybackResumeDto
        {
            ProgressPct = row.ProgressPct,
            PositionSeconds = TryGetPositionSeconds(row.ExtendedProperties),
            LastAccessed = DateTimeOffset.TryParse(row.LastAccessed, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed)
                ? parsed
                : null,
        };
    }

    private static double? ResolveManifestDurationSeconds(
        IReadOnlyList<PlaybackChapterDto> chapters,
        MediaInfoWrapper? mediaInfo,
        MediaProbeResult? probe)
    {
        var chapterEnd = chapters
            .Where(chapter => chapter.EndSeconds is > 0)
            .Select(chapter => chapter.EndSeconds!.Value)
            .DefaultIfEmpty()
            .Max();
        if (chapterEnd > 0)
        {
            return chapterEnd;
        }

        if (mediaInfo?.Duration is > 0)
        {
            return mediaInfo.Duration;
        }

        return probe?.Duration.TotalSeconds is > 0
            ? probe.Duration.TotalSeconds
            : null;
    }

    private static PlaybackResumeDto? NormalizeResumePosition(PlaybackResumeDto? resume, double? durationSeconds)
    {
        if (resume is null)
        {
            return null;
        }

        var duration = durationSeconds is > 0 ? durationSeconds.Value : (double?)null;
        var progress = Math.Clamp(resume.ProgressPct, 0, 100);
        var position = resume.PositionSeconds;
        if (!position.HasValue && progress is > 0 and < 100 && duration.HasValue)
        {
            position = duration.Value * progress / 100d;
        }

        if (position.HasValue)
        {
            position = duration.HasValue
                ? Math.Clamp(position.Value, 0, duration.Value)
                : Math.Max(0, position.Value);

            if (duration.HasValue && progress <= 0)
            {
                progress = Math.Clamp(position.Value / duration.Value * 100d, 0, 100);
            }
        }

        return resume with
        {
            PositionSeconds = position,
            ProgressPct = progress,
        };
    }

    private static double? TryGetPositionSeconds(string? extendedProperties)
    {
        if (string.IsNullOrWhiteSpace(extendedProperties))
        {
            return null;
        }

        try
        {
            var properties = JsonSerializer.Deserialize<Dictionary<string, string>>(extendedProperties);
            if (properties is not null
                && properties.TryGetValue("position_seconds", out var position)
                && double.TryParse(position, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds))
            {
                return seconds;
            }
        }
        catch (JsonException)
        {
            return null;
        }

        return null;
    }

    private static string BuildSourceHash(MediaAsset asset) => asset.ContentHash;

    private static bool IsDirectPlaySupported(string extension, PlaybackProfileDto profile, MediaInfoWrapper? mediaInfo, MediaProbeResult? probe)
    {
        var container = extension.TrimStart('.').ToLowerInvariant();
        if (!profile.SupportedContainers.Contains(container, StringComparer.OrdinalIgnoreCase))
        {
            return false;
        }

        var videoCodec = NormalizeCodecName(mediaInfo?.VideoCodec ?? probe?.VideoCodec);
        var audioCodec = NormalizeCodecName(mediaInfo?.AudioCodec ?? probe?.AudioCodec);
        var height = mediaInfo?.Height > 0 ? mediaInfo.Height : probe?.Height;

        if (!string.IsNullOrWhiteSpace(videoCodec)
            && profile.SupportedVideoCodecs.Count > 0
            && !profile.SupportedVideoCodecs.Contains(videoCodec, StringComparer.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(audioCodec)
            && profile.SupportedAudioCodecs.Count > 0
            && !profile.SupportedAudioCodecs.Contains(audioCodec, StringComparer.OrdinalIgnoreCase))
        {
            return false;
        }

        if (profile.MaxHeight.HasValue && height > profile.MaxHeight.Value)
        {
            return false;
        }

        return true;
    }

    private static string GetRecommendedDelivery(string mediaType, string client, bool directPlay)
    {
        if (mediaType.Equals("Books", StringComparison.OrdinalIgnoreCase)
            || mediaType.Equals("Comics", StringComparison.OrdinalIgnoreCase))
        {
            return PlaybackDeliveryModes.Reader;
        }

        if (client.Equals("mobile-download", StringComparison.OrdinalIgnoreCase))
        {
            return PlaybackDeliveryModes.OfflineVariant;
        }

        return directPlay ? PlaybackDeliveryModes.DirectStream : PlaybackDeliveryModes.Hls;
    }

    private static string GetConversionReason(string extension, PlaybackProfileDto profile, MediaInfoWrapper? mediaInfo, MediaProbeResult? probe)
    {
        var container = extension.TrimStart('.').ToLowerInvariant();
        var videoCodec = NormalizeCodecName(mediaInfo?.VideoCodec ?? probe?.VideoCodec);
        var audioCodec = NormalizeCodecName(mediaInfo?.AudioCodec ?? probe?.AudioCodec);
        var height = mediaInfo?.Height > 0 ? mediaInfo.Height : probe?.Height;

        if (!profile.SupportedContainers.Contains(container, StringComparer.OrdinalIgnoreCase))
        {
            return $"{profile.DisplayName} does not direct-play .{container} containers.";
        }

        if (!string.IsNullOrWhiteSpace(videoCodec)
            && !profile.SupportedVideoCodecs.Contains(videoCodec, StringComparer.OrdinalIgnoreCase))
        {
            return $"{profile.DisplayName} does not direct-play {videoCodec} video.";
        }

        if (!string.IsNullOrWhiteSpace(audioCodec)
            && !profile.SupportedAudioCodecs.Contains(audioCodec, StringComparer.OrdinalIgnoreCase))
        {
            return $"{profile.DisplayName} does not direct-play {audioCodec} audio.";
        }

        if (profile.MaxHeight.HasValue && height > profile.MaxHeight.Value)
        {
            return $"{profile.DisplayName} profile is capped at {profile.MaxHeight.Value}p.";
        }

        return "The client profile requires a compatible HLS or offline variant.";
    }

    private static IReadOnlyList<PlaybackTrackDto> BuildAudioTracks(MediaInfoWrapper? mediaInfo, MediaProbeResult? probe)
    {
        if (probe?.AudioStreams.Count > 0)
        {
            return probe.AudioStreams.Select(s => new PlaybackTrackDto
            {
                Index = s.Index,
                Kind = "audio",
                Language = s.Language,
                Codec = s.Codec,
                DisplayName = string.IsNullOrWhiteSpace(s.Language) ? $"Audio {s.Index + 1}" : $"Audio ({s.Language})",
                IsDefault = s.IsDefault
            }).ToList();
        }
        if (mediaInfo?.AudioStreams.Count > 0)
        {
            return mediaInfo.AudioStreams.Select((stream, index) => new PlaybackTrackDto
            {
                Index = index,
                Kind = "audio",
                Language = StringHelpers.FirstNonBlankOr(string.Empty, stream.LanguageIetf, stream.Language),
                Codec = NormalizeCodecName(stream.Codec.ToString()),
                DisplayName = StringHelpers.FirstNonBlankOr(string.Empty, stream.Name, stream.LanguageIetf, stream.Language, $"Audio {index + 1}"),
                IsDefault = stream.Default || index == 0,
                Channels = stream.Channel > 0 ? stream.Channel : null,
                BitrateKbps = stream.Bitrate > 0 ? (int)Math.Round(stream.Bitrate / 1000d) : null,
            }).ToList();
        }

        if (probe is null || string.IsNullOrWhiteSpace(probe.AudioCodec))
        {
            return [];
        }

        return
        [
            new PlaybackTrackDto
            {
                Index = 0,
                Kind = "audio",
                Language = probe.AudioLanguage,
                Codec = NormalizeCodecName(probe.AudioCodec),
                DisplayName = string.IsNullOrWhiteSpace(probe.AudioLanguage) ? "Default audio" : $"Audio ({probe.AudioLanguage})",
                IsDefault = true,
                Channels = probe.Channels,
                BitrateKbps = probe.AudioBitrate,
            }
        ];
    }

    private static PlaybackTechnicalInfoDto BuildTechnicalInfo(
        string extension,
        MediaInfoWrapper? mediaInfo,
        MediaProbeResult? probe)
    {
        var width = mediaInfo?.Width is > 0 ? mediaInfo.Width : probe?.Width;
        var height = mediaInfo?.Height is > 0 ? mediaInfo.Height : probe?.Height;
        return new PlaybackTechnicalInfoDto
        {
            Container = StringHelpers.FirstNonBlank(
                mediaInfo?.Format,
                extension.TrimStart('.').ToLowerInvariant()),
            VideoCodec = NormalizeCodecName(mediaInfo?.VideoCodec ?? probe?.VideoCodec),
            Width = width is > 0 ? width : null,
            Height = height is > 0 ? height : null,
            FrameRate = probe?.FrameRate is > 0 ? probe.FrameRate : null,
            AudioCodec = NormalizeCodecName(mediaInfo?.AudioCodec ?? probe?.AudioCodec),
            AudioBitrateKbps = probe?.AudioBitrate is > 0 ? probe.AudioBitrate : null,
            SampleRateHz = probe?.SampleRate is > 0 ? probe.SampleRate : null,
            Channels = probe?.Channels is > 0 ? probe.Channels : null,
        };
    }

    private async Task<IReadOnlyList<PlaybackSubtitleTrackDto>> BuildSubtitleTracksAsync(
        Guid assetId,
        MediaInfoWrapper? mediaInfo,
        MediaProbeResult? probe,
        CancellationToken ct)
    {
        var managedTracks = await _textTracks.GetByAssetAsync(assetId, TextTrackKind.Subtitles, ct);
        if (managedTracks.Count > 0)
        {
            return managedTracks.Select((track, index) => new PlaybackSubtitleTrackDto
            {
                Index = index,
                Language = track.Language,
                Codec = StringHelpers.FirstNonBlank(track.NormalizedFormat, track.SourceFormat),
                DisplayName = string.Equals(track.Language, "und", StringComparison.OrdinalIgnoreCase)
                    ? "Subtitles"
                    : $"Subtitles ({track.Language.ToUpperInvariant()})",
                IsDefault = track.IsPreferred,
                IsForced = false,
                DeliveryUrl = $"/stream/{assetId}/subtitles?language={Uri.EscapeDataString(track.Language)}",
            }).ToList();
        }

        if (mediaInfo?.Subtitles.Count > 0)
        {
            return mediaInfo.Subtitles.Select((stream, index) => new PlaybackSubtitleTrackDto
            {
                Index = index,
                Language = StringHelpers.FirstNonBlankOr(string.Empty, stream.LanguageIetf, stream.Language),
                Codec = NormalizeCodecName(stream.Codec.ToString()),
                DisplayName = StringHelpers.FirstNonBlankOr(string.Empty, stream.Name, stream.LanguageIetf, stream.Language, $"Subtitles {index + 1}"),
                IsDefault = stream.Default,
                IsForced = stream.Forced,
            }).ToList();
        }

        if (probe?.SubtitleLanguages.Count is not > 0)
        {
            return [];
        }

        return probe.SubtitleLanguages.Select((language, index) => new PlaybackSubtitleTrackDto
        {
            Index = index,
            Language = language,
            DisplayName = $"Subtitles ({language})",
            IsDefault = index == 0,
        }).ToList();
    }

    private async Task<IReadOnlyList<PlaybackChapterDto>> BuildChaptersAsync(
        Guid assetId,
        MediaInfoWrapper? mediaInfo,
        MediaProbeResult? probe,
        Guid? profileId,
        CancellationToken ct)
    {
        var chapters = BuildRawChapters(mediaInfo, probe);
        if (chapters.Count == 0)
        {
            return [];
        }

        var overrides = (await _chapterTitleOverrides.GetByAssetAsync(assetId, ct))
            .GroupBy(item => item.ChapterIndex)
            .ToDictionary(group => group.Key, group => group.OrderByDescending(item => item.UpdatedAt).First());
        return AudiobookChapterNormalizer.Normalize(chapters, overrides);
    }

    private static IReadOnlyList<PlaybackChapterDto> BuildRawChapters(MediaInfoWrapper? mediaInfo, MediaProbeResult? probe)
    {
        if (probe?.Chapters.Count > 0)
        {
            return CompleteChapterRanges(
                probe.Chapters.Select(chapter => new PlaybackChapterDto
                {
                    Index = chapter.Index,
                    Title = chapter.Title?.Trim() ?? string.Empty,
                    OriginalTitle = chapter.Title,
                    StartSeconds = chapter.StartSeconds,
                    EndSeconds = chapter.EndSeconds,
                }),
                probe.Duration.TotalSeconds);
        }

        if (mediaInfo?.Chapters.Count > 0)
        {
            var timedMediaInfoChapters = mediaInfo.Chapters
                .Select((chapter, index) =>
                {
                    var startSeconds = TryReadChapterSeconds(chapter, "StartSeconds", "Start", "StartTime", "TimeStart");
                    if (!startSeconds.HasValue)
                    {
                        return null;
                    }

                    var endSeconds = TryReadChapterSeconds(chapter, "EndSeconds", "End", "EndTime", "TimeEnd");
                    return new PlaybackChapterDto
                    {
                        Index = index,
                        Title = chapter.Name?.Trim() ?? string.Empty,
                        OriginalTitle = chapter.Name,
                        StartSeconds = Math.Max(0, startSeconds.Value),
                        EndSeconds = endSeconds,
                    };
                })
                .Where(chapter => chapter is not null)
                .Select(chapter => chapter!)
                .ToList();

            if (timedMediaInfoChapters.Count > 0)
            {
                return CompleteChapterRanges(timedMediaInfoChapters, mediaInfo.Duration);
            }
        }

        return [];
    }

    private static IReadOnlyList<PlaybackChapterDto> CompleteChapterRanges(
        IEnumerable<PlaybackChapterDto> chapters,
        double? totalDurationSeconds)
    {
        var ordered = chapters
            .Where(chapter => chapter.StartSeconds >= 0)
            .OrderBy(chapter => chapter.StartSeconds)
            .ThenBy(chapter => chapter.Index)
            .ToList();
        if (ordered.Count == 0)
        {
            return [];
        }

        var result = new List<PlaybackChapterDto>(ordered.Count);
        for (var i = 0; i < ordered.Count; i++)
        {
            var chapter = ordered[i];
            var nextStart = i + 1 < ordered.Count ? ordered[i + 1].StartSeconds : (double?)null;
            var endSeconds = chapter.EndSeconds;
            if (!endSeconds.HasValue || endSeconds.Value <= chapter.StartSeconds)
            {
                endSeconds = nextStart is > 0 && nextStart.Value > chapter.StartSeconds
                    ? nextStart
                    : totalDurationSeconds is > 0 && totalDurationSeconds.Value > chapter.StartSeconds
                        ? totalDurationSeconds
                        : null;
            }

            result.Add(chapter with { EndSeconds = endSeconds });
        }

        return result;
    }

    private static double? TryReadChapterSeconds(object chapter, params string[] propertyNames)
    {
        var type = chapter.GetType();
        foreach (var propertyName in propertyNames)
        {
            var property = type.GetProperty(propertyName);
            if (property is null)
            {
                continue;
            }

            var value = property.GetValue(chapter);
            if (value is null)
            {
                continue;
            }

            if (value is double doubleValue)
            {
                return doubleValue;
            }

            if (value is float floatValue)
            {
                return floatValue;
            }

            if (value is int intValue)
            {
                return intValue;
            }

            if (value is long longValue)
            {
                return longValue;
            }

            if (value is TimeSpan time)
            {
                return time.TotalSeconds;
            }

            if (double.TryParse(value.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
            {
                return parsed;
            }
        }

        return null;
    }

    private static string NormalizeCodecName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var normalized = value.Trim().ToLowerInvariant().Replace("_", string.Empty).Replace("-", string.Empty);
        return normalized switch
        {
            "avc" or "mpeg4avc" or "h264" => "h264",
            "hevc" or "h265" => "hevc",
            "aaclc" or "aac" => "aac",
            "mpeg1audiolayer3" or "mp3" => "mp3",
            "pcm" or "pcms16le" => "pcm_s16le",
            "vorbis" => "vorbis",
            "opus" => "opus",
            "flac" => "flac",
            "vp8" => "vp8",
            "vp9" => "vp9",
            "av1" => "av1",
            _ => value.Trim().ToLowerInvariant(),
        };
    }


    private static bool TryGetMediaInfoVersion(out string? version)
    {
        try
        {
            version = typeof(MediaInfoWrapper).Assembly.GetName().Version?.ToString();
            return !string.IsNullOrWhiteSpace(version);
        }
        catch
        {
            version = null;
            return false;
        }
    }

    private static string NormalizeClient(string? client) =>
        string.IsNullOrWhiteSpace(client) ? "web" : client.Trim().ToLowerInvariant();

    private static PlaybackSegmentDto ToSegmentDto(MediaEngine.Domain.Entities.PlaybackSegment segment) => new()
    {
        Id = segment.Id,
        AssetId = segment.AssetId,
        Kind = segment.Kind,
        StartSeconds = segment.StartSeconds,
        EndSeconds = segment.EndSeconds,
        Confidence = segment.Confidence,
        Source = segment.Source,
        PluginId = segment.PluginId,
        IsSkippable = segment.IsSkippable,
        ReviewStatus = segment.ReviewStatus,
    };

    private static PlaybackProfileDto ProfileFor(string client) => client switch
    {
        "android" => new PlaybackProfileDto
        {
            Key = "android",
            DisplayName = "Android",
            PreferredDelivery = PlaybackDeliveryModes.DirectStream,
            SupportedContainers = ["mp4", "m4v", "webm", "mp3", "m4a", "m4b", "aac", "ogg", "wav", "epub", "pdf", "cbz"],
            SupportedVideoCodecs = ["h264", "hevc", "vp8", "vp9", "av1"],
            SupportedAudioCodecs = ["aac", "mp3", "opus", "vorbis", "flac", "pcm_s16le"],
            SupportedSubtitleFormats = ["vtt", "srt"],
            SupportsPlaybackSpeed = true,
            SupportsAlternateAudio = true,
            SupportsSubtitles = true,
            SupportsOfflineDownloads = true,
        },
        "ios" => new PlaybackProfileDto
        {
            Key = "ios",
            DisplayName = "iOS",
            PreferredDelivery = PlaybackDeliveryModes.DirectStream,
            SupportedContainers = ["mp4", "m4v", "mp3", "m4a", "m4b", "aac", "epub", "pdf", "cbz"],
            SupportedVideoCodecs = ["h264", "hevc"],
            SupportedAudioCodecs = ["aac", "mp3", "alac"],
            SupportedSubtitleFormats = ["vtt"],
            MaxHeight = 2160,
            SupportsPlaybackSpeed = true,
            SupportsAlternateAudio = true,
            SupportsSubtitles = true,
            SupportsOfflineDownloads = true,
        },
        "android-tv" => new PlaybackProfileDto
        {
            Key = "android-tv",
            DisplayName = "Android TV",
            PreferredDelivery = PlaybackDeliveryModes.Hls,
            SupportedContainers = ["mp4", "m4v", "webm", "mp3", "m4a", "m4b", "aac", "ogg", "wav"],
            SupportedVideoCodecs = ["h264", "hevc", "vp9", "av1"],
            SupportedAudioCodecs = ["aac", "mp3", "opus", "vorbis", "flac"],
            SupportedSubtitleFormats = ["vtt", "srt"],
            MaxHeight = 2160,
            SupportsPlaybackSpeed = true,
            SupportsAlternateAudio = true,
            SupportsSubtitles = true,
        },
        "apple-tv" => new PlaybackProfileDto
        {
            Key = "apple-tv",
            DisplayName = "Apple TV",
            PreferredDelivery = PlaybackDeliveryModes.Hls,
            SupportedContainers = ["mp4", "m4v", "mp3", "m4a", "m4b", "aac"],
            SupportedVideoCodecs = ["h264", "hevc"],
            SupportedAudioCodecs = ["aac", "mp3", "alac"],
            SupportedSubtitleFormats = ["vtt"],
            MaxHeight = 2160,
            SupportsPlaybackSpeed = true,
            SupportsAlternateAudio = true,
            SupportsSubtitles = true,
        },
        "roku" => new PlaybackProfileDto
        {
            Key = "roku",
            DisplayName = "Roku",
            PreferredDelivery = PlaybackDeliveryModes.Hls,
            SupportedContainers = ["mp4", "m4v", "mp3", "m4a", "m4b", "aac"],
            SupportedVideoCodecs = ["h264", "hevc"],
            SupportedAudioCodecs = ["aac", "mp3"],
            SupportedSubtitleFormats = ["vtt", "srt"],
            MaxHeight = 2160,
            SupportsAlternateAudio = true,
            SupportsSubtitles = true,
        },
        "mobile-download" => new PlaybackProfileDto
        {
            Key = "mobile-download",
            DisplayName = "Mobile Download",
            PreferredDelivery = PlaybackDeliveryModes.OfflineVariant,
            SupportedContainers = ["mp4", "m4v", "mp3", "m4a", "m4b", "aac", "epub", "pdf", "cbz"],
            SupportedVideoCodecs = ["h264"],
            SupportedAudioCodecs = ["aac", "mp3"],
            SupportedSubtitleFormats = ["vtt"],
            MaxHeight = 720,
            SupportsPlaybackSpeed = true,
            SupportsAlternateAudio = true,
            SupportsSubtitles = true,
            SupportsOfflineDownloads = true,
        },
        _ => new PlaybackProfileDto
        {
            Key = "web",
            DisplayName = "Web",
            PreferredDelivery = PlaybackDeliveryModes.DirectStream,
            SupportedContainers = ["mp4", "m4v", "webm", "mp3", "m4a", "m4b", "aac", "ogg", "wav", "epub", "pdf", "cbz"],
            SupportedVideoCodecs = ["h264", "vp8", "vp9", "av1"],
            SupportedAudioCodecs = ["aac", "mp3", "opus", "vorbis", "flac", "pcm_s16le"],
            SupportedSubtitleFormats = ["vtt"],
            SupportsPlaybackSpeed = true,
            SupportsAlternateAudio = true,
            SupportsSubtitles = true,
            SupportsOfflineDownloads = true,
        },
    };

    private sealed record ResumeRow(double ProgressPct, string? LastAccessed, string? ExtendedProperties);
}
