using MediaEngine.Contracts.Details;
using MediaEngine.Contracts.Playback;
using MediaEngine.Web.Components.Watch;
using MediaEngine.Web.Services.Integration;

namespace MediaEngine.Web.Services.Playback;

public sealed record VideoPlaybackIdentity(Guid ProfileId, Guid WorkId, Guid AssetId, long RequestVersion)
{
    public static VideoPlaybackIdentity? Capture(PlaybackSessionController playback) =>
        playback.IsVideoMode && !playback.IsDismissed && playback.ActiveProfileId is Guid profile && profile != Guid.Empty
        && playback.CurrentItem is { WorkId: Guid work, AssetId: Guid asset } && work != Guid.Empty && asset != Guid.Empty
            ? new(profile, work, asset, playback.PlaybackRequestVersion) : null;
    public bool IsCurrent(PlaybackSessionController playback) => this == Capture(playback);
}

public sealed record VideoOwnedEpisode(Guid WorkId, string Title, string? StillUrl, string? Synopsis,
    string? Runtime, string SeasonKey, string SeasonTitle, int? SeasonNumber, int? EpisodeNumber,
    int? StillWidthPx = null, int? StillHeightPx = null);
public sealed record VideoPresentationContext(VideoPlaybackIdentity Identity, string? ShowTitle,
    IReadOnlyList<VideoOwnedEpisode> Episodes, VideoOwnedEpisode? NextEpisode, Guid? ShowWorkId = null)
{
    public static VideoPresentationContext Empty(VideoPlaybackIdentity identity) => new(identity, null, [], null);
}

/// <summary>Reads authorized presentation metadata. Starts still belong to the existing controller.</summary>
public sealed class VideoPresentationResolver(IEngineApiClient api, UIOrchestratorService orchestrator,
    PlaybackSessionController playback)
{
    public async Task<VideoPresentationContext> ResolveAsync(VideoPlaybackIdentity identity, CancellationToken ct)
    {
        void Check() { ct.ThrowIfCancellationRequested(); if (!identity.IsCurrent(playback))
        {
            throw new OperationCanceledException();
        } }
        Check();
        if (!IsTvEpisode(playback.CurrentItem))
        {
            return VideoPresentationContext.Empty(identity);
        }
        var page = await api.GetDetailPageAsync(DetailEntityType.TvEpisode, identity.WorkId,
            DetailPresentationContext.Watch, profileId: identity.ProfileId, ct: ct);
        Check();
        var sequence = page?.SequencePlacement;
        var episodes = OwnedEpisodes(sequence);
        var candidateIds = OwnedEpisodeQueuePlanner.NextPlayableCandidates(sequence, identity.WorkId, 12);
        VideoOwnedEpisode? next = null;
        foreach (var candidate in candidateIds)
        {
            var asset = await orchestrator.ResolveWorkToAssetAsync(candidate, ct);
            Check();
            if (asset is not Guid playable || playable == Guid.Empty)
            {
                continue;
            }
            var detail = await api.GetLibraryItemDetailAsync(candidate, ct);
            Check();
            if (detail is null)
            {
                continue;
            }
            next = episodes.FirstOrDefault(item => item.WorkId == candidate);
            if (next is null)
            {
                continue;
            }
            // Natural-ended and the card share the same real upcoming queue entry.
            if (!playback.Queue.Any(item => item.WorkId == candidate))
            {
                await playback.AppendVideoNextUpAsync(CreateItem(candidate, playable, detail), identity.WorkId, identity.RequestVersion, ct);
            }
            Check();
            break;
        }
        return new(identity, sequence?.CurrentItem.EpisodeContext?.ShowTitle, episodes, next,
            sequence?.CurrentItem.EpisodeContext?.ShowWorkId);
    }

    public async Task<bool> PlayEpisodeAsync(VideoPlaybackIdentity identity, Guid workId, CancellationToken ct,
        CancellationToken startCancellation = default)
    {
        void Check() { ct.ThrowIfCancellationRequested(); startCancellation.ThrowIfCancellationRequested(); if (!identity.IsCurrent(playback))
        {
            throw new OperationCanceledException();
        } }
        Check();
        var page = await api.GetDetailPageAsync(DetailEntityType.TvEpisode, identity.WorkId,
            DetailPresentationContext.Watch, profileId: identity.ProfileId, ct: ct);
        Check();
        if (!OwnedEpisodes(page?.SequencePlacement).Any(item => item.WorkId == workId))
        {
            return false;
        }
        var asset = await orchestrator.ResolveWorkToAssetAsync(workId, ct);
        Check();
        if (asset is not Guid playable || playable == Guid.Empty)
        {
            return false;
        }
        var detail = await api.GetLibraryItemDetailAsync(workId, ct);
        Check();
        if (detail is null)
        {
            return false;
        }
        // Committing the start invalidates old presentation metadata. The controller owns
        // its new request; only the caller's independent lifetime may cancel that start.
        await playback.PlayVideoAsync(CreateItem(workId, playable, detail), detail.ShowName ?? detail.Series ?? detail.Title, startCancellation);
        return true;
    }

    public static IReadOnlyList<VideoOwnedEpisode> OwnedEpisodes(SequencePlacementViewModel? sequence)
    {
        if (sequence is null)
        {
            return [];
        }
        return sequence.OrderedItems.Concat(sequence.Groups.SelectMany(group => group.Items))
            .Where(item => item.IsOwned && item.EntityType == DetailEntityType.TvEpisode)
            .Where(item => Guid.TryParse(item.Id, out var id) && id != Guid.Empty)
            .DistinctBy(item => item.Id)
            .Select(item => new VideoOwnedEpisode(Guid.Parse(item.Id), item.EpisodeContext?.EpisodeTitle ?? item.Title,
                item.EpisodeStillUrl, item.Description, item.Duration,
                item.GroupKey ?? item.EpisodeContext?.SeasonNumber?.ToString() ?? "owned",
                item.GroupTitle ?? (item.EpisodeContext?.SeasonNumber is int season ? $"Season {season}" : "Owned episodes"),
                item.EpisodeContext?.SeasonNumber, item.EpisodeContext?.EpisodeNumber,
                item.EpisodeStillWidthPx, item.EpisodeStillHeightPx)).ToList();
    }

    public static bool IsTvEpisode(ListenQueueItem? item) => item is not null
        && (item.MediaType.Contains("tv", StringComparison.OrdinalIgnoreCase)
            || item.MediaType.Equals("Television", StringComparison.OrdinalIgnoreCase)
            || !string.IsNullOrWhiteSpace(item.EpisodeNumber));
    public static string? ContextKind(bool tv, bool movie, int ownedEpisodes, int chapters, int upcoming) =>
        tv && ownedEpisodes > 0 ? "episodes" : !tv && movie && chapters >= 2 ? "chapters" : upcoming > 0 ? "queue" : null;

    private static ListenQueueItem CreateItem(Guid work, Guid asset, MediaEngine.Web.Models.ViewDTOs.LibraryItemDetailViewModel detail) => new()
    {
        WorkId = work, AssetId = asset, MediaType = "TvEpisode", Title = detail.EpisodeTitle ?? detail.Title,
        EpisodeTitle = detail.EpisodeTitle, Album = detail.ShowName ?? detail.Series, SeasonNumber = detail.SeasonNumber,
        EpisodeNumber = detail.EpisodeNumber, Year = detail.Year, CoverUrl = detail.CoverUrl,
        Duration = PlaybackVideoRuntime.NormalizeLibraryDetailRuntime(detail.Runtime), Quality = detail.PlaybackSummary?.VideoResolutionLabel,
    };
}

/// <summary>Pure presentation clock, scoped to a playback instance. It never advances the queue.</summary>
public sealed class VideoEndCardState
{
    private VideoPlaybackIdentity? _identity;
    private Guid? _next;
    private bool _dismissed;
    public bool Visible { get; private set; }
    public double RemainingSeconds { get; private set; } = 10;
    public void Observe(VideoPlaybackIdentity? identity, Guid? next, double position, double duration,
        IReadOnlyList<PlaybackSegmentDto> segments)
    {
        if (_identity != identity) { _identity = identity; _dismissed = false; _next = null; }
        var credits = segments.Where(segment => string.Equals(segment.Kind, "credits", StringComparison.OrdinalIgnoreCase)
            && double.IsFinite(segment.StartSeconds) && segment.StartSeconds >= 0 && segment.StartSeconds < duration
            && (segment.EndSeconds is null || IsVerifiedRange(segment, duration))
            && string.Equals(segment.ReviewStatus, "verified", StringComparison.OrdinalIgnoreCase))
            .Select(segment => (double?)segment.StartSeconds).Min();
        var eligible = identity is not null && next is not null && duration > 0 && double.IsFinite(duration)
            && position >= (credits ?? Math.Max(0, duration - 20)) && position <= duration + 1;
        if (!eligible || _next != next)
        {
            RemainingSeconds = 10;
        }
        _next = next;
        Visible = eligible && !_dismissed;
    }
    public bool Tick(double elapsedSeconds, bool active)
    {
        if (!Visible || !active || !double.IsFinite(elapsedSeconds) || elapsedSeconds <= 0)
        {
            return false;
        }
        RemainingSeconds = Math.Max(0, RemainingSeconds - Math.Min(1.5, elapsedSeconds));
        return RemainingSeconds == 0;
    }
    public bool Tick(VideoPlaybackIdentity identity, double elapsedSeconds, bool active) =>
        identity == _identity && Tick(elapsedSeconds, active);
    public void Dismiss() { _dismissed = true; Visible = false; }
    public static bool IsVerifiedRange(PlaybackSegmentDto segment, double duration) =>
        double.IsFinite(segment.StartSeconds) && segment.StartSeconds >= 0 && segment.StartSeconds < duration
        && segment.EndSeconds is double end && double.IsFinite(end) && end > segment.StartSeconds && end <= duration
        && string.Equals(segment.ReviewStatus, "verified", StringComparison.OrdinalIgnoreCase);
}
