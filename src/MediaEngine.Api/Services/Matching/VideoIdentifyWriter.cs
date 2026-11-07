using MediaEngine.Domain;
using MediaEngine.Domain.Constants;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Entities;
using MediaEngine.Domain.Enums;
using MediaEngine.Domain.Models;
using MediaEngine.Domain.Services;
using MediaEngine.Ingestion.Contracts;
using MediaEngine.Providers.Services;
using MediaEngine.Storage.Contracts;

namespace MediaEngine.Api.Services.Matching;

/// <summary>How a video lookup ended. Each refusal has the sentence a person reads in <see cref="VideoIdentifyOutcome.Message"/>.</summary>
public enum VideoIdentifyStatus
{
    Ok,
    NotAVideo,
    BadLink,
    SwitchedOff,
    NotAvailable,
    NotFound,
}

/// <summary>The answer of a video lookup: what YouTube said, or why nothing was looked up.</summary>
public sealed record VideoIdentifyOutcome(VideoIdentifyStatus Status, string? Message = null, string? VideoId = null, VideoOnlineInfo? Info = null)
{
    public bool IsOk => Status == VideoIdentifyStatus.Ok;
}

/// <summary>
/// Looks a video up on YouTube by its ID and saves the person's choice as locked claims (the video half of the Match tab). Only the ID
/// is sent, only when the video's library has the lookup switched on, and never for a Vault video. The lookup alone (<see cref="LookupAsync"/>)
/// changes nothing; <see cref="ApplyAsync"/> is what the Match tab's staged change runs on Save changes, and what the older paste-a-link
/// action runs.
/// </summary>
public interface IVideoIdentifyWriter
{
    /// <summary>Checks the video and asks YouTube about <paramref name="pasted"/> (a link or the 11-character ID). Writes nothing.</summary>
    Task<VideoIdentifyOutcome> LookupAsync(Guid entityId, string? pasted, CancellationToken ct = default);

    /// <summary>As <see cref="LookupAsync"/>, then saves the match as locked claims.</summary>
    Task<VideoIdentifyOutcome> ApplyAsync(Guid entityId, string? pasted, CancellationToken ct = default);
}

public sealed class VideoIdentifyWriter(
    IVideoSeriesRepository series,
    IItemCanonicalRepository itemCanonicalData,
    IMediaAssetRepository assets,
    ILibraryFolderResolver libraries,
    IVideoOnlineLookup lookup,
    IProviderAudienceGate audienceGate,
    IMetadataClaimRepository claimRepo,
    ICanonicalValueRepository canonicalRepo,
    ISystemActivityRepository activityRepo) : IVideoIdentifyWriter
{
    public Task<VideoIdentifyOutcome> LookupAsync(Guid entityId, string? pasted, CancellationToken ct = default) =>
        RunAsync(entityId, pasted, write: false, ct);

    public Task<VideoIdentifyOutcome> ApplyAsync(Guid entityId, string? pasted, CancellationToken ct = default) =>
        RunAsync(entityId, pasted, write: true, ct);

    private async Task<VideoIdentifyOutcome> RunAsync(Guid entityId, string? pasted, bool write, CancellationToken ct)
    {
        var workId = await itemCanonicalData.ResolveWorkIdForAssetAsync(entityId, ct).ConfigureAwait(false) ?? entityId;
        var membership = await series.GetMembershipAsync(workId, ct).ConfigureAwait(false);
        if (membership is null || membership.AssetId == Guid.Empty)
        {
            return new VideoIdentifyOutcome(VideoIdentifyStatus.NotAVideo, "That is not a video.");
        }

        if (!YoutubeLink.TryGetVideoId(pasted, out var videoId))
        {
            return new VideoIdentifyOutcome(VideoIdentifyStatus.BadLink, "That does not look like a YouTube link. Paste the video's link or its 11-character ID.");
        }

        var asset = await assets.FindByIdAsync(membership.AssetId, ct).ConfigureAwait(false);
        if (asset is null || libraries.ResolveForPath(asset.FilePathRoot)?.YoutubeLookup != true)
        {
            return new VideoIdentifyOutcome(VideoIdentifyStatus.SwitchedOff, "Online lookup is switched off for this library. Turn it on in Settings first.");
        }

        // YouTube is never asked about a Vault video, even if its library's switch says yes (Videos spec D21, D26).
        if (!await audienceGate.MayAskAboutItemAsync("youtube", membership.AssetId, ct).ConfigureAwait(false))
        {
            return new VideoIdentifyOutcome(VideoIdentifyStatus.NotAvailable, "Online lookup from YouTube is not available for this library.");
        }

        var info = await lookup.LookupAsync(videoId, ct).ConfigureAwait(false);
        if (info is null || string.IsNullOrWhiteSpace(info.Title))
        {
            return new VideoIdentifyOutcome(VideoIdentifyStatus.NotFound, "No match found for that video.", videoId);
        }

        if (write)
        {
            await SaveAsync(membership.AssetId, workId, videoId, info, ct).ConfigureAwait(false);
        }

        return new VideoIdentifyOutcome(VideoIdentifyStatus.Ok, null, videoId, info);
    }

    private async Task SaveAsync(Guid assetId, Guid workId, string videoId, VideoOnlineInfo info, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var claims = new List<MetadataClaim>();
        var canonicals = new List<CanonicalValue>();
        void Lock(string key, string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return;
            }

            claims.Add(new MetadataClaim
            {
                Id = Guid.NewGuid(),
                EntityId = assetId,
                ProviderId = WellKnownProviders.UserManual,
                ClaimKey = key,
                ClaimValue = value,
                ClaimedAt = now,
                Confidence = 1.0,
                IsUserLocked = true,
            });
            canonicals.Add(new CanonicalValue
            {
                EntityId = assetId,
                Key = key,
                Value = value,
                LastScoredAt = now,
                WinningProviderId = WellKnownProviders.UserManual,
            });
        }

        Lock(MetadataFieldConstants.YoutubeVideoId, videoId);
        Lock(MetadataFieldConstants.VideoLookupSource, "youtube");
        Lock(MetadataFieldConstants.Title, info.Title);
        Lock(MetadataFieldConstants.Network, info.Channel);
        Lock(MetadataFieldConstants.Description, info.Description);
        if (info.Published is { } published)
        {
            Lock(MetadataFieldConstants.OriginalReleaseDate, published.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture));
            Lock(MetadataFieldConstants.Year, published.Year.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        await claimRepo.InsertBatchAsync(claims, ct).ConfigureAwait(false);
        await canonicalRepo.UpsertBatchAsync(canonicals, ct).ConfigureAwait(false);
        await activityRepo.LogAsync(new SystemActivityEntry
        {
            OccurredAt = now,
            ActionType = SystemActionType.MetadataManualOverride,
            EntityId = workId,
            EntityType = "Work",
            Detail = "Identified a video from a YouTube link.",
        }, ct).ConfigureAwait(false);
    }
}
