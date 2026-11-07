using System.Globalization;
using MediaEngine.Contracts.Playback;
using MediaEngine.Domain.Authorization;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Entities;
using MediaEngine.Domain.Enums;

namespace MediaEngine.Api.Services.Playback;

/// <summary>
/// Builds the cast session a paired phone, or a signed-in Dashboard session, hands to a receiver: how the title will be delivered
/// (the file as it is, with byte ranges, or the prepared HLS package), the one-title <see cref="CastAccessGrant"/>, and the poster
/// and subtitle paths behind it. A video may use the HLS package; music and audiobooks are always the file as it is, with no subtitle
/// tracks. A Vault item never casts. It never creates an HLS grant for the caller; the receiver only ever holds the cast grant.
/// </summary>
public sealed class CastSessionService
{
    public const string ContentTypeMp4 = "video/mp4";
    public const string ContentTypeWebm = "video/webm";
    public const string ContentTypeHls = "application/x-mpegURL";

    private static readonly Dictionary<string, string> AudioContentTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        [".mp3"] = "audio/mpeg",
        [".m4a"] = "audio/mp4",
        [".m4b"] = "audio/mp4",
        [".aac"] = "audio/aac",
        [".flac"] = "audio/flac",
        [".ogg"] = "audio/ogg",
        [".wav"] = "audio/wav",
    };

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

    /// <summary>The content type a music or audiobook file is cast as, by extension. Null for anything that is not one of those audio files.</summary>
    public static string? AudioContentTypeOf(string? extension) =>
        extension is not null && AudioContentTypes.TryGetValue(extension, out var type) ? type : null;

    /// <summary>
    /// Null when the asset does not exist. Throws <see cref="UnauthorizedAccessException"/> for a Vault item, and unless the caller is
    /// a live paired device or a live signed-in Dashboard session (<see cref="PrincipalKind.Human"/> with a managed session id).
    /// </summary>
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

        // A Vault item never casts, on either path, however the caller is signed in.
        if (plan.IsVault)
        {
            throw new UnauthorizedAccessException("A Vault item cannot be cast.");
        }

        var audioType = plan.IsAudio ? AudioContentTypeOf(plan.Extension) : null;
        if (!plan.IsVideo && audioType is null)
        {
            return Unavailable(assetId, plan, "Casting works for videos, music and audiobooks only.");
        }

        if (audioType is null && !plan.Inspected)
        {
            return Unavailable(assetId, plan, "This video is still being prepared. Try again in a moment.");
        }

        string delivery;
        string contentType;
        Guid? packageId = null;
        if (audioType is not null)
        {
            // Music and audiobooks: the file as it is, with byte ranges, and the content type its extension gives.
            delivery = PlaybackDeliveryModes.DirectStream;
            contentType = audioType;
        }
        else if (plan.DirectPlay)
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

        // A signed-in Dashboard session gets a session grant; every other caller goes through the paired-device grant, which
        // refuses anything that is not a live paired device.
        var grant = authority.PrincipalKind == PrincipalKind.Human
            ? await _grants.CreateForSessionAsync(assetId, packageId, authority, plan.DurationSeconds, ct).ConfigureAwait(false)
            : await _grants.CreateAsync(assetId, packageId, authority, tokenId, scopes, plan.DurationSeconds, ct).ConfigureAwait(false);
        var segment = Uri.EscapeDataString(grant.Value);
        IReadOnlyList<TextTrack> subtitleTracks = audioType is not null
            ? []
            : await _textTracks.GetByAssetAsync(assetId, TextTrackKind.Subtitles, ct).ConfigureAwait(false);
        var tracks = subtitleTracks
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
