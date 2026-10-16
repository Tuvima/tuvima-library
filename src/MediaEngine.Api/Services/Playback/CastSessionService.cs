using System.Globalization;
using MediaEngine.Contracts.Playback;
using MediaEngine.Domain.Authorization;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Enums;

namespace MediaEngine.Api.Services.Playback;

/// <summary>
/// Builds the cast session a paired phone hands to a home-network TV: how the title will be delivered (the file as it is, with
/// byte ranges, or the prepared HLS package), the one-title <see cref="CastAccessGrant"/>, and the poster and subtitle paths
/// behind it. It never creates an HLS grant for the phone; the TV only ever holds the cast grant.
/// </summary>
public sealed class CastSessionService
{
    public const string ContentTypeMp4 = "video/mp4";
    public const string ContentTypeWebm = "video/webm";
    public const string ContentTypeHls = "application/x-mpegURL";

    private readonly PlaybackCapabilitiesService _playback;
    private readonly AdaptiveHlsService _hls;
    private readonly CastAccessGrantService _grants;
    private readonly ITextTrackRepository _textTracks;
    private readonly IFFmpegService _ffmpeg;

    public CastSessionService(
        PlaybackCapabilitiesService playback,
        AdaptiveHlsService hls,
        CastAccessGrantService grants,
        ITextTrackRepository textTracks,
        IFFmpegService ffmpeg)
    {
        _playback = playback;
        _hls = hls;
        _grants = grants;
        _textTracks = textTracks;
        _ffmpeg = ffmpeg;
    }

    /// <summary>Null when the asset does not exist. Throws <see cref="UnauthorizedAccessException"/> unless the caller is a live paired device.</summary>
    public async Task<CastSessionDto?> CreateAsync(
        Guid assetId,
        RequestAuthority authority,
        Guid? tokenId,
        IReadOnlyCollection<string> scopes,
        CancellationToken ct = default)
    {
        var plan = await _playback.PlanCastAsync(assetId, authority.ActiveProfileId, ct).ConfigureAwait(false);
        if (plan is null)
        {
            return null;
        }

        if (!plan.IsVideo)
        {
            return Unavailable(assetId, plan, "Casting works for videos only.");
        }

        if (!plan.Inspected)
        {
            return Unavailable(assetId, plan, "This video is still being prepared. Try again in a moment.");
        }

        string delivery;
        string contentType;
        Guid? packageId = null;
        if (plan.DirectPlay)
        {
            delivery = PlaybackDeliveryModes.DirectStream;
            contentType = string.Equals(plan.Extension, ".webm", StringComparison.OrdinalIgnoreCase) ? ContentTypeWebm : ContentTypeMp4;
        }
        else
        {
            delivery = PlaybackDeliveryModes.Hls;
            contentType = ContentTypeHls;
            if (!_ffmpeg.HardwareCapabilities.AdaptiveHlsReady)
            {
                return Unavailable(assetId, plan, "This video has to be converted for your TV, and this server cannot convert it.", delivery);
            }

            var preparation = await _hls.EnsurePackageAsync(assetId, plan.SourceHash, plan.AudioTracks, ct).ConfigureAwait(false);
            if (preparation.Status == "preparing" || preparation.Status == "streaming")
            {
                // Casting starts only at "ready": a package that is still being written is an EVENT playlist and cannot be sought.
                return new CastSessionDto
                {
                    AssetId = assetId,
                    Status = CastSessionStatuses.Preparing,
                    Delivery = delivery,
                    DurationSeconds = plan.DurationSeconds,
                    ResumeSeconds = plan.ResumeSeconds,
                    Message = "Getting this ready for your TV...",
                };
            }

            if (preparation.Status != "ready")
            {
                return Unavailable(assetId, plan, "This video could not be prepared for your TV.", delivery);
            }

            packageId = preparation.PackageId;
        }

        var grant = await _grants.CreateAsync(assetId, packageId, authority, tokenId, scopes, plan.DurationSeconds, ct).ConfigureAwait(false);
        var segment = Uri.EscapeDataString(grant.Value);
        var tracks = (await _textTracks.GetByAssetAsync(assetId, TextTrackKind.Subtitles, ct).ConfigureAwait(false))
            .Where(track => !string.IsNullOrWhiteSpace(track.LocalPath) && File.Exists(track.LocalPath))
            .OrderByDescending(track => track.IsPreferred)
            .ThenBy(track => track.Language, StringComparer.OrdinalIgnoreCase)
            .Select(track => new CastTextTrackDto
            {
                Id = track.Id,
                Language = track.Language,
                Label = TrackLabel(track.Language, track.IsHearingImpaired),
                IsForced = false,
                IsDefault = track.IsPreferred,
                Path = $"/cast/v1/{segment}/text/{track.Id:D}",
                ContentType = "text/vtt",
            })
            .ToList();

        return new CastSessionDto
        {
            AssetId = assetId,
            Status = CastSessionStatuses.Ready,
            Delivery = delivery,
            MediaPath = packageId is { } package
                ? $"/cast/v1/{segment}/hls/{package:D}/master.m3u8"
                : $"/cast/v1/{segment}/media",
            ContentType = contentType,
            PosterPath = $"/cast/v1/{segment}/poster",
            TextTracks = tracks,
            DurationSeconds = plan.DurationSeconds,
            ResumeSeconds = plan.ResumeSeconds,
            ExpiresAt = grant.ExpiresAt,
        };
    }

    private static CastSessionDto Unavailable(Guid assetId, CastMediaPlan plan, string message, string? delivery = null) => new()
    {
        AssetId = assetId,
        Status = CastSessionStatuses.Unavailable,
        Delivery = delivery ?? PlaybackDeliveryModes.DirectStream,
        DurationSeconds = plan.DurationSeconds,
        ResumeSeconds = plan.ResumeSeconds,
        Message = message,
    };

    private static string TrackLabel(string language, bool hearingImpaired)
    {
        string name;
        try
        {
            name = string.IsNullOrWhiteSpace(language) || language == "und"
                ? "Subtitles"
                : CultureInfo.GetCultureInfo(language).EnglishName;
        }
        catch (CultureNotFoundException)
        {
            name = language;
        }

        return hearingImpaired ? $"{name} (for the hard of hearing)" : name;
    }
}
