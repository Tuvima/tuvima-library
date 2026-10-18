using MediaEngine.Contracts.Details;
using MediaEngine.Contracts.Playback;
using MediaEngine.Domain.Services;
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

/// <summary>A subtitle the player could show, reduced to what the profile defaults need. <paramref name="Key"/> is the player's caption key.</summary>
public sealed record VideoSubtitleCandidate(string Key, string? Language, string? Label, bool IsPreferred = false, bool IsForced = false);

public enum VideoCaptionDecision
{
    /// <summary>The profile has no opinion: the player keeps its own choice.</summary>
    Unchanged,
    /// <summary>Start with subtitles off.</summary>
    Off,
    /// <summary>Start with <see cref="VideoTrackDefaults.CaptionKey"/> showing.</summary>
    Show,
}

/// <summary>The audio track (position in the audio list) and subtitle a title starts with.</summary>
public sealed record VideoTrackDefaults(int? AudioTrackIndex, VideoCaptionDecision Captions, string? CaptionKey)
{
    public static VideoTrackDefaults Unchanged { get; } = new(null, VideoCaptionDecision.Unchanged, null);

    /// <summary>The player attribute value: "off", a caption key, or null for no opinion.</summary>
    public string? CaptionAttribute => Captions switch
    {
        VideoCaptionDecision.Off => "off",
        VideoCaptionDecision.Show => CaptionKey,
        _ => null,
    };
}

/// <summary>Reads authorized presentation metadata. Starts still belong to the existing controller.</summary>
public sealed class VideoPresentationResolver(IEngineApiClient api, UIOrchestratorService orchestrator,
    PlaybackSessionController playback, IUserPlaybackPreferencesAccessor? preferences = null)
{
    /// <summary>The active profile's subtitle and audio defaults, or null when they are not available.</summary>
    public async Task<SubtitleLanguageSettingsDto?> GetSubtitleSettingsAsync(CancellationToken ct) =>
        preferences is null ? null : (await preferences.GetAsync(ct))?.Subtitles;

    /// <summary>
    /// Picks the audio track and subtitle a title starts with from the profile's defaults. A per-title preferred subtitle
    /// ("Set preferred version") always wins. Show subtitles: Never = off; Always = the subtitle language when present;
    /// Auto = the subtitle language, but only when the audio that actually plays is not already in it (otherwise the
    /// player's own default stays, so forced or default tracks still show).
    /// <paramref name="canSwitchAudio"/> is true only when the player can really change the audio track (it reports every
    /// track); when it cannot, the profile's audio language is never assumed to play and the judgement uses the track that does.
    /// A track with no known language (und, blank) is taken to be in the profile's audio language.
    /// </summary>
    public static VideoTrackDefaults ChooseTrackDefaults(SubtitleLanguageSettingsDto? settings,
        IReadOnlyList<PlaybackTrackDto> audioTracks, IReadOnlyList<VideoSubtitleCandidate> subtitles, bool canSwitchAudio)
    {
        if (settings is null)
        {
            return VideoTrackDefaults.Unchanged;
        }
        int? audioIndex = null;
        if (canSwitchAudio && audioTracks.Count > 1)
        {
            for (var index = 0; index < audioTracks.Count && audioIndex is null; index++)
            {
                if (LanguageMatches(settings.AudioLanguage, audioTracks[index].Language, audioTracks[index].DisplayName))
                {
                    audioIndex = index;
                }
            }
        }
        var playing = audioIndex is int chosen ? audioTracks[chosen]
            : audioTracks.Count == 0 ? null : audioTracks.FirstOrDefault(track => track.IsDefault) ?? audioTracks[0];
        var heardCode = playing is null ? null : LanguageCode(playing.Language) ?? LanguageCode(playing.DisplayName);
        var heardInSubtitleLanguage = heardCode is null
            ? LanguageMatches(settings.DefaultSubtitleLanguage, settings.AudioLanguage, null)
            : LanguageMatches(settings.DefaultSubtitleLanguage, heardCode, null);
        if (subtitles.FirstOrDefault(candidate => candidate.IsPreferred) is { } preferred)
        {
            return new(audioIndex, VideoCaptionDecision.Show, preferred.Key);
        }
        if (string.Equals(settings.ForcedSubtitlesMode, PlaybackPreferenceValues.Never, StringComparison.OrdinalIgnoreCase))
        {
            return new(audioIndex, VideoCaptionDecision.Off, null);
        }
        var always = string.Equals(settings.ForcedSubtitlesMode, PlaybackPreferenceValues.Always, StringComparison.OrdinalIgnoreCase);
        var wanted = subtitles.Where(candidate => LanguageMatches(settings.DefaultSubtitleLanguage, candidate.Language, candidate.Label))
            .OrderBy(candidate => candidate.IsForced).FirstOrDefault();
        return wanted is null || (!always && heardInSubtitleLanguage)
            ? new(audioIndex, VideoCaptionDecision.Unchanged, null)
            : new(audioIndex, VideoCaptionDecision.Show, wanted.Key);
    }

    /// <summary>True when a track's language (code, name or label such as "English 5.1") is the language the profile named.</summary>
    public static bool LanguageMatches(string? wanted, string? trackLanguage, string? trackLabel)
    {
        if (string.IsNullOrWhiteSpace(wanted))
        {
            return false;
        }
        var wantedCode = LanguageCode(wanted);
        var trackCode = LanguageCode(trackLanguage) ?? LanguageCode(trackLabel);
        if (wantedCode is not null && trackCode is not null)
        {
            return wantedCode == trackCode;
        }
        return string.Equals(wanted.Trim(), trackLanguage?.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    private static string? LanguageCode(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }
        var code = LanguageCodeNormalizer.ToIso6391(value);
        if (code is null)
        {
            // A label such as "English 5.1": try its first word. A label of only separators has no word.
            var words = value.Split([' ', '(', '/', ',', '\u00b7'], StringSplitOptions.RemoveEmptyEntries);
            code = words.Length == 0 ? null : LanguageCodeNormalizer.ToIso6391(words[0]);
        }
        // Norwegian is one language to a viewer: the file may say nb or nn, the profile says Norwegian (no).
        return code is "nb" or "nn" ? "no" : code;
    }

    public async Task<VideoPresentationContext> ResolveAsync(VideoPlaybackIdentity identity, CancellationToken ct)
    {
        void Check()
        {
            ct.ThrowIfCancellationRequested(); if (!identity.IsCurrent(playback))
            {
                throw new OperationCanceledException();
            }
        }
        Check();
        if (!IsTvEpisode(playback.CurrentItem))
        {
            return VideoPresentationContext.Empty(identity);
        }
        var page = await api.GetDetailPageAsync(DetailEntityType.TvEpisode, identity.WorkId,
            DetailPresentationContext.Watch,
            sequence: SequenceWindowQuery.WholeList, ct: ct);
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
            // Episodes covered by the file already playing are not "next": that would replay the same file.
            if (playable == playback.CurrentItem?.AssetId)
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
        void Check()
        {
            ct.ThrowIfCancellationRequested(); startCancellation.ThrowIfCancellationRequested(); if (!identity.IsCurrent(playback))
            {
                throw new OperationCanceledException();
            }
        }
        Check();
        var page = await api.GetDetailPageAsync(DetailEntityType.TvEpisode, identity.WorkId,
            DetailPresentationContext.Watch,
            sequence: SequenceWindowQuery.WholeList, ct: ct);
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
        WorkId = work,
        AssetId = asset,
        MediaType = "TvEpisode",
        Title = detail.EpisodeTitle ?? detail.Title,
        EpisodeTitle = detail.EpisodeTitle,
        Album = detail.ShowName ?? detail.Series,
        SeasonNumber = detail.SeasonNumber,
        EpisodeNumber = detail.EpisodeNumber,
        Year = detail.Year,
        CoverUrl = detail.CoverUrl,
        Duration = PlaybackVideoRuntime.NormalizeLibraryDetailRuntime(detail.Runtime),
        Quality = detail.PlaybackSummary?.VideoResolutionLabel,
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
    /// <summary>False when the viewer turned autoplay off: the card stays up with no countdown and never asks to advance.</summary>
    public bool AutoAdvance { get; set; } = true;
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
        if (!AutoAdvance || !Visible || !active || !double.IsFinite(elapsedSeconds) || elapsedSeconds <= 0)
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
