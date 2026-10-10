using System.Collections.Concurrent;
using System.Globalization;
using Dapper;
using MediaEngine.Api.Security;
using MediaEngine.Contracts.Playback;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Entities;
using MediaEngine.Domain.Playback;
using MediaEngine.Domain.Services;
using MediaEngine.Storage.Contracts;
using MediaEngine.Storage.Playback;

namespace MediaEngine.Api.Services.Playback;

public sealed class PlayerService
{
    private static readonly TimeSpan StaleSessionWindow = TimeSpan.FromSeconds(45);

    private readonly PlayerSessionRepository _sessions;
    private readonly PlaybackCapabilitiesService _playback;
    private readonly IDatabaseConnection _db;
    private readonly IMediaAssetRepository _assets;
    private readonly IUserStateStore _userStates;
    private readonly IUserPlaybackSettingsService _settings;
    private readonly AudiobookListenHistoryRepository _history;
    private readonly AudiobookBookmarkRepository _bookmarks;
    private readonly MusicPlayStatsRepository _musicStats;
    private readonly PlayerCatalogueScope _scope;
    private readonly PlaybackTelemetryLifecycleService _telemetry;
    private readonly ConcurrentDictionary<Guid, PlaybackConnectionContextDto> _connectionContexts = new();

    public PlayerService(
        PlayerSessionRepository sessions,
        PlaybackCapabilitiesService playback,
        IDatabaseConnection db,
        IMediaAssetRepository assets,
        IUserStateStore userStates,
        IUserPlaybackSettingsService settings,
        AudiobookListenHistoryRepository history,
        AudiobookBookmarkRepository bookmarks,
        MusicPlayStatsRepository musicStats,
        PlayerCatalogueScope scope,
        PlaybackTelemetryLifecycleService telemetry)
    {
        _sessions = sessions;
        _playback = playback;
        _db = db;
        _assets = assets;
        _userStates = userStates;
        _settings = settings;
        _history = history;
        _bookmarks = bookmarks;
        _musicStats = musicStats;
        _scope = scope;
        _telemetry = telemetry;
    }

    public async Task<PlayerStateDto> GetStateAsync(Guid? profileId, string? deviceId, string? client, CancellationToken ct = default)
    {
        var normalizedProfile = await _scope.RequireProfileAsync(profileId, ct);
        await _sessions.EnsureSessionAsync(
            normalizedProfile,
            Guid.NewGuid(),
            NormalizeDeviceId(deviceId),
            NormalizeClient(client),
            ct);

        return await EnrichStateAsync(
            await _sessions.GetStateAsync(normalizedProfile, StaleSessionWindow, ct)
                ?? EmptyState(normalizedProfile, NormalizeDeviceId(deviceId), NormalizeClient(client)),
            ct);
    }

    public async Task<PlayerStateDto> ReplaceQueueAsync(PlayerQueueMutationDto request, CancellationToken ct = default)
    {
        var profileId = await _scope.RequireProfileAsync(request.ProfileId, ct);
        var priorSession = await _sessions.GetStateAsync(profileId, StaleSessionWindow, ct);
        var deviceId = NormalizeDeviceId(request.DeviceId);
        var client = NormalizeClient(request.Client);
        var sessionId = Guid.NewGuid();
        var items = await ResolveQueueItemsAsync(request, profileId, ct);
        ValidateQueueReplacement(items);

        if (request.Shuffle)
        {
            items = items.OrderBy(_ => Guid.NewGuid()).ToList();
        }

        var currentQueueItemId = request.StartQueueItemId;
        if (!currentQueueItemId.HasValue && request.StartIndex.HasValue && items.Count > 0)
        {
            currentQueueItemId = items[Math.Clamp(request.StartIndex.Value, 0, items.Count - 1)].QueueItemId;
        }

        if (!currentQueueItemId.HasValue && request.StartWorkId.HasValue)
        {
            currentQueueItemId = items.FirstOrDefault(item => item.WorkId == request.StartWorkId.Value)?.QueueItemId;
        }

        await _sessions.ReplaceQueueAsync(
            profileId,
            sessionId,
            deviceId,
            client,
            items,
            currentQueueItemId,
            request.SourceLabel,
            request.Shuffle,
            request.ExpectedStateVersion,
            request.Force,
            ct);

        if (priorSession is not null)
        {
            await _telemetry.CloseAsync(priorSession.SessionId, PlaybackCompletionReasons.Replaced, ct);
        }

        return await GetStateAsync(profileId, deviceId, client, ct);
    }

    public async Task<PlayerStateDto> AddQueueItemsAsync(PlayerQueueMutationDto request, bool insertNext, CancellationToken ct = default)
    {
        var profileId = await _scope.RequireProfileAsync(request.ProfileId, ct);
        var deviceId = NormalizeDeviceId(request.DeviceId);
        var client = NormalizeClient(request.Client);
        var items = await ResolveQueueItemsAsync(request, profileId, ct);
        ValidateQueueAddition(items);

        await _sessions.AddQueueItemsAsync(
            profileId,
            Guid.NewGuid(),
            deviceId,
            client,
            items,
            insertNext,
            request.ExpectedStateVersion,
            request.Force,
            ct);

        return await GetStateAsync(profileId, deviceId, client, ct);
    }

    public async Task<PlayerStateDto> ReorderQueueAsync(PlayerQueueMutationDto request, CancellationToken ct = default)
    {
        var profileId = await _scope.RequireProfileAsync(request.ProfileId, ct);
        await RequireQueueItemsAsync(profileId, request.QueueItemIds, ct);
        await _sessions.ReorderQueueAsync(profileId, request.QueueItemIds, request.ExpectedStateVersion, request.Force, ct);
        return await GetStateAsync(profileId, request.DeviceId, request.Client, ct);
    }

    public async Task<PlayerStateDto> RemoveQueueItemAsync(Guid queueItemId, PlayerQueueMutationDto request, CancellationToken ct = default)
    {
        var profileId = await _scope.RequireProfileAsync(request.ProfileId, ct);
        await RequireQueueItemsAsync(profileId, [queueItemId], ct);
        await _sessions.RemoveQueueItemAsync(profileId, queueItemId, request.ExpectedStateVersion, request.Force, ct);
        return await GetStateAsync(profileId, request.DeviceId, request.Client, ct);
    }

    public async Task<PlayerStateDto> ClearQueueAsync(PlayerQueueMutationDto request, CancellationToken ct = default)
    {
        var profileId = await _scope.RequireProfileAsync(request.ProfileId, ct);
        var priorSession = await _sessions.GetStateAsync(profileId, StaleSessionWindow, ct);
        await _sessions.ClearQueueAsync(profileId, request.ExpectedStateVersion, request.Force, ct);
        if (priorSession is not null)
        {
            await _telemetry.CloseAsync(priorSession.SessionId, PlaybackCompletionReasons.Stopped, ct);
        }

        return await GetStateAsync(profileId, request.DeviceId, request.Client, ct);
    }

    public async Task<PlayerStateDto> ApplyCommandAsync(PlayerCommandRequestDto request, CancellationToken ct = default)
    {
        var profileId = await _scope.RequireProfileAsync(request.ProfileId, ct);
        var command = NormalizeCommand(request.Command);
        if (command == PlayerCommands.Speed)
        {
            _ = RequirePlaybackRate(request.PlaybackRate);
        }
        if (command == PlayerCommands.ScanStart)
        {
            _ = RequireScanRate(request.PlaybackRate);
        }
        if (command == PlayerCommands.ScanStop)
        {
            _ = RequirePlaybackRate(request.PlaybackRate ?? 1d);
        }
        await _sessions.EnsureSessionAsync(profileId, Guid.NewGuid(), NormalizeDeviceId(request.DeviceId), NormalizeClient(request.Client), ct);
        var state = await _sessions.GetStateAsync(profileId, StaleSessionWindow, ct)
            ?? EmptyState(profileId, NormalizeDeviceId(request.DeviceId), NormalizeClient(request.Client));
        state = await _scope.FilterStateAsync(state, ct);

        if (request.QueueItemId.HasValue && !state.Queue.Any(item => item.QueueItemId == request.QueueItemId))
        {
            throw new PlayerResourceDeniedException();
        }

        switch (command)
        {
            case PlayerCommands.Play:
                await PlayAsync(profileId, state, request.QueueItemId, ct);
                break;
            case PlayerCommands.Pause:
                await _sessions.UpdateTransportAsync(profileId, playbackState: PlayerPlaybackStates.Paused, ct: ct);
                break;
            case PlayerCommands.Stop:
                await _sessions.UpdateTransportAsync(
                    profileId,
                    playbackState: PlayerPlaybackStates.Stopped,
                    positionSeconds: 0,
                    progressPct: 0,
                    ct: ct);
                break;
            case PlayerCommands.Next:
                await MoveRelativeAsync(profileId, state, 1, ct);
                break;
            case PlayerCommands.Previous:
                await MoveRelativeAsync(profileId, state, -1, ct);
                break;
            case PlayerCommands.Seek:
                await _sessions.UpdateTransportAsync(
                    profileId,
                    positionSeconds: Math.Max(0, request.PositionSeconds ?? state.PositionSeconds),
                    durationSeconds: request.DurationSeconds,
                    progressPct: CalculateProgress(request.PositionSeconds ?? state.PositionSeconds, request.DurationSeconds ?? state.DurationSeconds),
                    ct: ct);
                break;
            case PlayerCommands.RelativeSeek:
                var relativePosition = Math.Max(0, state.PositionSeconds + (request.DeltaSeconds ?? 0));
                await _sessions.UpdateTransportAsync(
                    profileId,
                    positionSeconds: relativePosition,
                    durationSeconds: request.DurationSeconds ?? state.DurationSeconds,
                    progressPct: CalculateProgress(relativePosition, request.DurationSeconds ?? state.DurationSeconds),
                    ct: ct);
                break;
            case PlayerCommands.Volume:
                await _sessions.UpdateTransportAsync(profileId, volume: ClampVolume(request.Volume), ct: ct);
                break;
            case PlayerCommands.Mute:
                await _sessions.UpdateTransportAsync(profileId, isMuted: request.IsMuted ?? !state.IsMuted, ct: ct);
                break;
            case PlayerCommands.Speed:
                await _sessions.UpdateTransportAsync(profileId, playbackRate: RequirePlaybackRate(request.PlaybackRate), ct: ct);
                break;
            case PlayerCommands.ScanStart:
                await _sessions.UpdateTransportAsync(profileId, playbackRate: RequireScanRate(request.PlaybackRate), ct: ct);
                break;
            case PlayerCommands.ScanStop:
                await _sessions.UpdateTransportAsync(profileId, playbackRate: RequirePlaybackRate(request.PlaybackRate ?? 1d), ct: ct);
                break;
            case PlayerCommands.Shuffle:
                await _sessions.UpdateTransportAsync(profileId, shuffleEnabled: request.ShuffleEnabled ?? !state.ShuffleEnabled, ct: ct);
                break;
            case PlayerCommands.Repeat:
                await _sessions.UpdateTransportAsync(profileId, repeatMode: NormalizeRepeatMode(request.RepeatMode), ct: ct);
                break;
        }

        await ObserveCommandAsync(profileId, state, command, request, ct);

        return await GetStateAsync(profileId, request.DeviceId, request.Client, ct);
    }

    public async Task<PlayerStateDto> HeartbeatAsync(PlayerHeartbeatDto heartbeat, CancellationToken ct = default)
    {
        if (heartbeat.PlaybackRate is double requestedRate && !PlaybackRatePolicy.IsValid(requestedRate))
        {
            throw new ArgumentOutOfRangeException(nameof(heartbeat.PlaybackRate), requestedRate,
                $"Playback speed must be finite and between {PlaybackRatePolicy.Minimum} and {PlaybackRatePolicy.Maximum}.");
        }
        var profileId = await _scope.RequireProfileAsync(heartbeat.ProfileId, ct);
        var deviceId = NormalizeDeviceId(heartbeat.DeviceId);
        var client = NormalizeClient(heartbeat.Client);
        var activeSession = await _sessions.GetStateAsync(profileId, StaleSessionWindow, ct);
        if (heartbeat.SessionId.HasValue && activeSession is not null && activeSession.SessionId != heartbeat.SessionId.Value)
        {
            return await GetStateAsync(profileId, deviceId, client, ct);
        }

        var heartbeatAsset = heartbeat.AssetId ?? activeSession?.Queue
            .FirstOrDefault(item => item.QueueItemId == (heartbeat.QueueItemId ?? activeSession.CurrentQueueItemId))?.AssetId;
        if (heartbeatAsset.HasValue)
        {
            await _scope.RequireAssetAsync(heartbeatAsset.Value, ct);
        }

        if (heartbeat.QueueItemId.HasValue && activeSession?.Queue.Any(item =>
                item.QueueItemId == heartbeat.QueueItemId && item.AssetId == heartbeatAsset) != true)
        {
            throw new PlayerResourceDeniedException();
        }

        if (heartbeat.Connection is not null)
        {
            _connectionContexts[profileId] = NormalizeConnection(heartbeat.Connection);
        }
        var trustedPlayerSessionId = activeSession?.SessionId ?? Guid.NewGuid();
        await _sessions.EnsureSessionAsync(profileId, trustedPlayerSessionId, deviceId, client, ct);
        var priorState = await _sessions.GetStateAsync(profileId, StaleSessionWindow, ct);
        if (priorState is not null)
        {
            priorState = await _scope.FilterStateAsync(priorState, ct);
        }

        var position = Math.Max(0, heartbeat.PositionSeconds);
        var duration = heartbeat.DurationSeconds;
        var progress = heartbeat.ProgressPct ?? CalculateProgress(position, duration);
        var state = heartbeat.IsPlaying ? PlayerPlaybackStates.Playing : PlayerPlaybackStates.Paused;

        await _sessions.UpdateTransportAsync(
            profileId,
            playbackState: state,
            currentQueueItemId: heartbeat.QueueItemId,
            setCurrentQueueItem: heartbeat.QueueItemId.HasValue,
            positionSeconds: position,
            durationSeconds: duration,
            progressPct: progress,
            volume: heartbeat.Volume.HasValue ? ClampVolume(heartbeat.Volume) : null,
            isMuted: heartbeat.IsMuted,
            playbackRate: heartbeat.PlaybackRate,
            heartbeat: true,
            ct: ct);

        var assetId = heartbeatAsset;

        UserPlaybackSettingsDto? settings = null;
        if (assetId.HasValue)
        {
            settings = await GetPlaybackSettingsOrDefaultAsync(profileId, ct);
            await SaveResumeAsync(
                profileId,
                assetId.Value,
                position,
                duration,
                progress,
                state,
                trustedPlayerSessionId,
                deviceId,
                client,
                settings.Listening,
                heartbeat.AudiobookStartKind,
                ct);
        }

        var historyItem = ResolveHistoryItem(priorState, heartbeat, assetId);
        if (historyItem is not null && IsAudiobook(historyItem.MediaType))
        {
            settings ??= await GetPlaybackSettingsOrDefaultAsync(profileId, ct);
            historyItem = await EnrichHistoryItemAsync(historyItem, profileId, ct);
            await _history.TrackHeartbeatAsync(
                profileId,
                historyItem,
                heartbeat,
                settings.Listening.AudiobookListenQualificationSeconds,
                settings.Listening.AudiobookHistoryLimit,
                settings.Listening.AudiobookHistoryActiveSegmentGapSeconds,
                settings.Listening.AudiobookHistoryPositionJumpToleranceSeconds,
                deviceId,
                client,
                ct);
        }
        else if (historyItem is not null && IsMusic(historyItem.MediaType))
        {
            await _musicStats.TrackHeartbeatAsync(profileId, historyItem, heartbeat, ct);
        }

        if (assetId.HasValue)
        {
            await _telemetry.ObserveAsync(
                trustedPlayerSessionId,
                assetId.Value,
                state,
                position,
                duration,
                heartbeat.Sequence,
                heartbeat.PlaybackRate ?? 1d,
                isExplicitSeek: false,
                heartbeat.HasPlaybackEnded,
                completionReason: null,
                client,
                heartbeat.Connection,
                ct);
        }

        return await GetStateAsync(profileId, deviceId, client, ct);
    }

    private async Task ObserveCommandAsync(
        Guid profileId,
        PlayerStateDto prior,
        string command,
        PlayerCommandRequestDto request,
        CancellationToken ct)
    {
        if (command is not (PlayerCommands.Play or PlayerCommands.Pause or PlayerCommands.Stop or
            PlayerCommands.Next or PlayerCommands.Previous or PlayerCommands.Seek or PlayerCommands.RelativeSeek))
        {
            return;
        }

        var updated = await _sessions.GetStateAsync(profileId, StaleSessionWindow, ct);
        if (updated is null)
        {
            return;
        }

        updated = await _scope.FilterStateAsync(updated, ct);
        var stopped = command == PlayerCommands.Stop;
        var item = stopped ? prior.CurrentItem : updated.CurrentItem;
        if (item?.AssetId is not { } assetId)
        {
            return;
        }

        var position = stopped ? prior.PositionSeconds : updated.PositionSeconds;
        var duration = stopped ? prior.DurationSeconds : updated.DurationSeconds;
        await _telemetry.ObserveAsync(
            updated.SessionId,
            assetId,
            stopped ? PlayerPlaybackStates.Stopped : updated.PlaybackState,
            position,
            duration,
            sequence: null,
            updated.PlaybackRate,
            isExplicitSeek: command is PlayerCommands.Seek or PlayerCommands.RelativeSeek,
            hasPlaybackEnded: false,
            completionReason: stopped ? PlaybackCompletionReasons.Stopped : null,
            request.Client,
            connection: null,
            ct);
    }

    public async Task<PlayerStateDto> TakeoverAsync(PlayerSessionTakeoverRequestDto request, CancellationToken ct = default)
    {
        var profileId = await _scope.RequireProfileAsync(request.ProfileId, ct);
        var priorSession = await _sessions.GetStateAsync(profileId, StaleSessionWindow, ct);
        var deviceId = NormalizeDeviceId(request.DeviceId);
        var client = NormalizeClient(request.Client);
        await _sessions.TakeoverAsync(profileId, Guid.NewGuid(), deviceId, client, request.Force, StaleSessionWindow, ct);
        if (priorSession is not null)
        {
            await _telemetry.CloseAsync(priorSession.SessionId, PlaybackCompletionReasons.Takeover, ct);
        }

        return await GetStateAsync(profileId, deviceId, client, ct);
    }

    public PlayerCapabilitiesDto GetCapabilities() => new();

    private async Task RequireQueueItemsAsync(Guid profileId, IEnumerable<Guid> requestedIds, CancellationToken ct)
    {
        var state = await _sessions.GetStateAsync(profileId, StaleSessionWindow, ct);
        var allowed = state is null ? new HashSet<Guid>()
            : (await _scope.FilterStateAsync(state, ct)).Queue.Select(item => item.QueueItemId).ToHashSet();
        if (requestedIds.Any(id => !allowed.Contains(id)))
        {
            throw new PlayerResourceDeniedException();
        }
    }

    private async Task PlayAsync(Guid profileId, PlayerStateDto state, Guid? requestedQueueItemId, CancellationToken ct)
    {
        var queueItemId = requestedQueueItemId ?? state.CurrentQueueItemId ?? state.Queue.FirstOrDefault()?.QueueItemId;
        var item = queueItemId.HasValue
            ? state.Queue.FirstOrDefault(candidate => candidate.QueueItemId == queueItemId.Value)
            : null;

        await _sessions.UpdateTransportAsync(
            profileId,
            playbackState: item is null ? PlayerPlaybackStates.Stopped : PlayerPlaybackStates.Playing,
            currentQueueItemId: item?.QueueItemId,
            setCurrentQueueItem: item is not null,
            positionSeconds: item is null ? null : Math.Max(0, item.PositionSeconds ?? 0),
            durationSeconds: item?.DurationSeconds,
            progressPct: CalculateProgress(item?.PositionSeconds, item?.DurationSeconds),
            ct: ct);
    }

    private async Task MoveRelativeAsync(Guid profileId, PlayerStateDto state, int delta, CancellationToken ct)
    {
        if (state.Queue.Count == 0)
        {
            await _sessions.UpdateTransportAsync(profileId, playbackState: PlayerPlaybackStates.Stopped, ct: ct);
            return;
        }

        var currentIndex = state.CurrentQueueItemId.HasValue
            ? state.Queue.ToList().FindIndex(item => item.QueueItemId == state.CurrentQueueItemId.Value)
            : -1;
        var nextIndex = currentIndex + delta;

        if (nextIndex < 0)
        {
            nextIndex = string.Equals(state.RepeatMode, PlayerRepeatModes.All, StringComparison.OrdinalIgnoreCase)
                ? state.Queue.Count - 1
                : 0;
        }
        else if (nextIndex >= state.Queue.Count)
        {
            if (!string.Equals(state.RepeatMode, PlayerRepeatModes.All, StringComparison.OrdinalIgnoreCase))
            {
                await _sessions.UpdateTransportAsync(profileId, playbackState: PlayerPlaybackStates.Paused, ct: ct);
                return;
            }

            nextIndex = 0;
        }

        var next = state.Queue[nextIndex];
        await _sessions.UpdateTransportAsync(
            profileId,
            playbackState: PlayerPlaybackStates.Playing,
            currentQueueItemId: next.QueueItemId,
            setCurrentQueueItem: true,
            positionSeconds: 0,
            durationSeconds: next.DurationSeconds,
            progressPct: 0,
            ct: ct);
    }

    internal async Task<IReadOnlyList<PlayerQueueItemDto>> ResolveQueueItemsAsync(
        PlayerQueueMutationDto request, Guid profileId, CancellationToken ct)
    {
        var items = new List<PlayerQueueItemDto>();
        var explicitWorkIds = new HashSet<Guid>();

        foreach (var requested in request.Items.Where(item => item.WorkId != Guid.Empty))
        {
            var resolved = await ResolvePlayableWorkAsync(requested.WorkId, requested.AssetId, profileId, ct);
            if (resolved is null)
            {
                continue;
            }

            explicitWorkIds.Add(requested.WorkId);
            AddQueueItem(items, MergeQueueItem(resolved, requested, request.SourceLabel), request.StartWorkId);
        }

        foreach (var workId in request.WorkIds.Where(id => id != Guid.Empty).Distinct().Where(id => !explicitWorkIds.Contains(id)))
        {
            var resolved = await ResolvePlayableWorkAsync(workId, null, profileId, ct);
            if (resolved is null)
            {
                continue;
            }

            AddQueueItem(items, resolved with
            {
                QueueItemId = Guid.NewGuid(),
                AddedAt = DateTimeOffset.UtcNow,
                Subtitle = StringHelpers.FirstNonBlank(resolved.Subtitle, resolved.Artist, resolved.Author, resolved.Narrator, request.SourceLabel),
            }, request.StartWorkId);
        }

        return items;
    }

    /// <summary>
    /// Adds a resolved item, collapsing episodes that share one combined file into a single
    /// queue entry so the file never plays twice in a row. The entry for the requested start
    /// episode wins, so starting on episode 2 keeps its start offset.
    /// </summary>
    private static void AddQueueItem(List<PlayerQueueItemDto> items, PlayerQueueItemDto item, Guid? startWorkId)
    {
        var shared = item.AssetId.HasValue && IsVideo(item.MediaType)
            ? items.FindIndex(existing => existing.AssetId == item.AssetId)
            : -1;
        if (shared < 0)
        {
            items.Add(item);
        }
        else if (startWorkId.HasValue && item.WorkId == startWorkId.Value)
        {
            items[shared] = item;
        }
    }

    private static PlayerQueueItemDto MergeQueueItem(
        PlayerQueueItemDto resolved,
        PlayerQueueMutationItemDto requested,
        string? sourceLabel)
    {
        var assetId = requested.AssetId ?? resolved.AssetId;
        return resolved with
        {
            QueueItemId = Guid.NewGuid(),
            AddedAt = DateTimeOffset.UtcNow,
            AssetId = assetId,
            CollectionId = requested.CollectionId ?? resolved.CollectionId,
            MediaType = StringHelpers.FirstNonBlank(requested.MediaType, resolved.MediaType) ?? resolved.MediaType,
            Title = StringHelpers.FirstNonBlank(requested.Title, resolved.Title) ?? resolved.Title,
            Subtitle = StringHelpers.FirstNonBlank(requested.Subtitle, resolved.Subtitle, requested.Artist, resolved.Artist, requested.Author, resolved.Author, requested.Narrator, resolved.Narrator, sourceLabel),
            Album = StringHelpers.FirstNonBlank(requested.Album, resolved.Album),
            Author = StringHelpers.FirstNonBlank(requested.Author, resolved.Author),
            Artist = StringHelpers.FirstNonBlank(requested.Artist, resolved.Artist),
            Narrator = StringHelpers.FirstNonBlank(requested.Narrator, resolved.Narrator),
            Series = StringHelpers.FirstNonBlank(requested.Series, resolved.Series),
            Year = StringHelpers.FirstNonBlank(requested.Year, resolved.Year),
            ContentRating = StringHelpers.FirstNonBlank(requested.ContentRating, resolved.ContentRating),
            SeasonNumber = StringHelpers.FirstNonBlank(requested.SeasonNumber, resolved.SeasonNumber),
            EpisodeNumber = StringHelpers.FirstNonBlank(requested.EpisodeNumber, resolved.EpisodeNumber),
            EpisodeTitle = StringHelpers.FirstNonBlank(requested.EpisodeTitle, resolved.EpisodeTitle),
            Quality = StringHelpers.FirstNonBlank(requested.Quality, resolved.Quality),
            CoverUrl = StringHelpers.FirstNonBlank(requested.CoverUrl, resolved.CoverUrl, assetId.HasValue ? $"/stream/{assetId.Value}/cover" : null),
            DurationSeconds = requested.DurationSeconds ?? resolved.DurationSeconds,
            PositionSeconds = requested.PositionSeconds.HasValue ? Math.Max(0, requested.PositionSeconds.Value) : resolved.PositionSeconds,
            StreamUrl = resolved.StreamUrl,
            DownloadUrl = resolved.DownloadUrl,
        };
    }

    private async Task<PlayerQueueItemDto?> ResolvePlayableWorkAsync(Guid workId, Guid? requestedAsset, Guid profileId, CancellationToken ct)
    {
        var allowedAsset = await _scope.FindAssetAsync(workId, requestedAsset, ct);
        if (allowedAsset is null)
        {
            throw new PlayerResourceDeniedException();
        }

        ct.ThrowIfCancellationRequested();
        using var conn = _db.CreateConnection();
        var row = conn.QueryFirstOrDefault<PlayableWorkRow>("""
            SELECT w.id AS WorkId,
                   ma.id AS AssetId,
                   w.collection_id AS CollectionId,
                   w.media_type AS MediaType,
                   COALESCE(
                       MAX(CASE WHEN wcv.key = 'title' THEN wcv.value END),
                       MAX(CASE WHEN acv.key = 'title' THEN acv.value END),
                       'Untitled media'
                   ) AS Title,
                   COALESCE(
                       MAX(CASE WHEN wcv.key = 'subtitle' THEN wcv.value END),
                       MAX(CASE WHEN acv.key = 'subtitle' THEN acv.value END)
                   ) AS Subtitle,
                   COALESCE(
                       MAX(CASE WHEN wcv.key = 'album' THEN wcv.value END),
                       MAX(CASE WHEN acv.key = 'album' THEN acv.value END),
                       MAX(CASE WHEN wcv.key = 'series' THEN wcv.value END)
                   ) AS Album,
                   COALESCE(
                       (SELECT value FROM canonical_value_arrays WHERE entity_id = w.id AND key IN ('author', 'creator') ORDER BY ordinal LIMIT 1),
                       (SELECT value FROM canonical_value_arrays WHERE entity_id = ma.id AND key IN ('author', 'creator') ORDER BY ordinal LIMIT 1)
                   ) AS Author,
                   COALESCE(
                       (SELECT value FROM canonical_value_arrays WHERE entity_id = w.id AND key IN ('album_artist', 'artist', 'performer') ORDER BY ordinal LIMIT 1),
                       (SELECT value FROM canonical_value_arrays WHERE entity_id = ma.id AND key IN ('album_artist', 'artist', 'performer') ORDER BY ordinal LIMIT 1)
                   ) AS Artist,
                   COALESCE(
                       (SELECT value FROM canonical_value_arrays WHERE entity_id = w.id AND key = 'narrator' ORDER BY ordinal LIMIT 1),
                       (SELECT value FROM canonical_value_arrays WHERE entity_id = ma.id AND key = 'narrator' ORDER BY ordinal LIMIT 1)
                   ) AS Narrator,
                   COALESCE(
                       MAX(CASE WHEN wcv.key = 'series' THEN wcv.value END),
                       MAX(CASE WHEN acv.key = 'series' THEN acv.value END)
                   ) AS Series,
                   COALESCE(
                       MAX(CASE WHEN wcv.key IN ('year', 'release_year', 'premiere_year') THEN wcv.value END),
                       MAX(CASE WHEN acv.key IN ('year', 'release_year', 'premiere_year') THEN acv.value END)
                   ) AS Year,
                   COALESCE(
                       MAX(CASE WHEN wcv.key IN ('content_rating', 'certification', 'rating_classification') THEN wcv.value END),
                       MAX(CASE WHEN acv.key IN ('content_rating', 'certification', 'rating_classification') THEN acv.value END)
                   ) AS ContentRating,
                   COALESCE(
                       MAX(CASE WHEN wcv.key = 'season_number' THEN wcv.value END),
                       MAX(CASE WHEN acv.key = 'season_number' THEN acv.value END)
                   ) AS SeasonNumber,
                   COALESCE(
                       MAX(CASE WHEN wcv.key = 'episode_number' THEN wcv.value END),
                       MAX(CASE WHEN acv.key = 'episode_number' THEN acv.value END)
                   ) AS EpisodeNumber,
                   COALESCE(
                       MAX(CASE WHEN wcv.key = 'episode_title' THEN wcv.value END),
                       MAX(CASE WHEN acv.key = 'episode_title' THEN acv.value END),
                       MAX(CASE WHEN wcv.key = 'title' THEN wcv.value END)
                   ) AS EpisodeTitle,
                   COALESCE(
                       MAX(CASE WHEN acv.key IN ('quality', 'video_quality', 'resolution', 'video_resolution_label') THEN acv.value END),
                       MAX(CASE WHEN wcv.key IN ('quality', 'video_quality', 'resolution', 'video_resolution_label') THEN wcv.value END)
                   ) AS Quality,
                   COALESCE(
                       MAX(CASE WHEN wcv.key = 'duration' THEN wcv.value END),
                       MAX(CASE WHEN acv.key = 'duration' THEN acv.value END),
                       MAX(CASE WHEN wcv.key = 'runtime' THEN wcv.value END),
                       MAX(CASE WHEN acv.key = 'runtime' THEN acv.value END)
                   ) AS Duration,
                   (SELECT c.start_seconds FROM media_asset_coverage c
                    WHERE c.asset_id = ma.id AND c.work_id = w.id) AS CoverageStartSeconds,
                   (SELECT us.progress_pct FROM user_states us
                    WHERE us.asset_id = ma.id AND us.user_id = @profileId) AS FileProgressPct
            FROM works w
            INNER JOIN work_owned_assets woa ON woa.work_id = w.id
            INNER JOIN media_assets ma ON ma.id = woa.asset_id
            LEFT JOIN canonical_values wcv ON wcv.entity_id = w.id
            -- A covered episode shares the host file; its identity comes from its own work, never the file's.
            LEFT JOIN canonical_values acv ON acv.entity_id = ma.id
                AND (woa.is_covered = 0
                     OR acv.key NOT IN ('title', 'subtitle', 'episode_title', 'episode_number', 'season_number'))
            WHERE w.id = @workId
              AND ma.id = @allowedAsset
              AND LOWER(REPLACE(w.media_type, ' ', '')) IN
                  ('music', 'audiobooks', 'audiobook', 'audio', 'movie', 'movies', 'film', 'films',
                   'tv', 'television', 'tvepisode', 'episode')
            GROUP BY w.id, ma.id
            ORDER BY ma.presented_at IS NULL, ma.presented_at DESC, ma.file_path_root
            LIMIT 1;
            """, new { workId, allowedAsset, profileId });

        if (row is null)
        {
            return null;
        }

        return new PlayerQueueItemDto
        {
            WorkId = row.WorkId,
            AssetId = row.AssetId,
            CollectionId = row.CollectionId,
            MediaType = row.MediaType,
            Title = row.Title,
            Subtitle = row.Subtitle,
            Album = row.Album,
            Author = row.Author,
            Artist = row.Artist,
            Narrator = row.Narrator,
            Series = row.Series,
            Year = row.Year,
            ContentRating = row.ContentRating,
            SeasonNumber = row.SeasonNumber,
            EpisodeNumber = row.EpisodeNumber,
            EpisodeTitle = row.EpisodeTitle,
            Quality = row.Quality,
            CoverUrl = row.AssetId.HasValue ? $"/stream/{row.AssetId.Value}/cover" : null,
            DurationSeconds = TryParseDurationSeconds(row.Duration),
            PositionSeconds = StartOffsetFor(row),
            StreamUrl = row.AssetId.HasValue ? $"/stream/{row.AssetId.Value}" : null,
            DownloadUrl = row.AssetId.HasValue ? $"/stream/{row.AssetId.Value}" : null,
        };
    }

    /// <summary>
    /// Where a combined file should open for this episode: its known start inside the file, but
    /// only when the profile has nothing to resume (new or finished), so saved progress always wins.
    /// </summary>
    private static double? StartOffsetFor(PlayableWorkRow row) =>
        row.CoverageStartSeconds is > 0 && row.FileProgressPct is null or <= 0 or >= 99.5
            ? row.CoverageStartSeconds
            : null;

    private async Task<PlayerStateDto> EnrichStateAsync(PlayerStateDto state, CancellationToken ct)
    {
        state = await _scope.FilterStateAsync(state, ct);
        var connection = _connectionContexts.GetValueOrDefault(state.ProfileId) ?? new PlaybackConnectionContextDto();
        var current = state.CurrentItem;
        if (current?.AssetId is null)
        {
            return state with
            {
                Capabilities = GetCapabilities(),
                Experience = ExperienceFor(state.Queue),
                Connection = connection,
            };
        }

        var manifest = await _playback.BuildManifestAsync(current.AssetId.Value, state.Client, state.ProfileId, ct, connection);
        if (manifest is null)
        {
            return state with { Capabilities = GetCapabilities(), Connection = connection };
        }

        var enrichedCurrent = current with
        {
            StreamUrl = manifest.DirectStreamUrl ?? current.StreamUrl,
            DurationSeconds = current.DurationSeconds,
            PositionSeconds = state.PositionSeconds > 0 ? state.PositionSeconds : current.PositionSeconds ?? manifest.Resume?.PositionSeconds,
            ProgressPct = state.ProgressPct > 0 ? state.ProgressPct : current.ProgressPct ?? manifest.Resume?.ProgressPct,
            Chapters = manifest.Chapters,
            Manifest = manifest,
        };

        var queue = state.Queue
            .Select(item => item.QueueItemId == enrichedCurrent.QueueItemId ? enrichedCurrent : item)
            .ToList();

        return state with
        {
            CurrentItem = enrichedCurrent,
            Queue = queue,
            Experience = ExperienceFor(queue),
            Capabilities = GetCapabilities(),
            AudiobookHistory = IsAudiobook(enrichedCurrent.MediaType)
                ? await GetAudiobookHistoryAsync(state.ProfileId, enrichedCurrent.WorkId, null, ct)
                : [],
            Warnings = manifest.Warnings,
            Connection = connection,
        };
    }

    private static PlaybackConnectionContextDto NormalizeConnection(PlaybackConnectionContextDto connection) => connection with
    {
        ConnectionPath = PlaybackConnectionPaths.IsKnown(connection.ConnectionPath)
            ? connection.ConnectionPath
            : PlaybackConnectionPaths.Local,
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

    private async Task SaveResumeAsync(
        Guid profileId,
        Guid assetId,
        double positionSeconds,
        double? durationSeconds,
        double progressPct,
        string playbackState,
        Guid? sessionId,
        string deviceId,
        string client,
        ListeningSettingsDto listeningSettings,
        string? audiobookStartKind,
        CancellationToken ct)
    {
        var asset = await _assets.FindByIdAsync(assetId, ct);
        if (asset is null)
        {
            return;
        }

        var prior = await _userStates.GetAsync(profileId, assetId, ct);
        if (sessionId.HasValue && prior?.ExtendedProperties.GetValueOrDefault("blocked_player_session_id") == sessionId.Value.ToString("D"))
        {
            return;
        }

        if (ShouldPreservePriorResume(
            prior,
            positionSeconds,
            durationSeconds,
            progressPct,
            listeningSettings,
            audiobookStartKind))
        {
            return;
        }

        var extended = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["position_seconds"] = positionSeconds.ToString("0.###", CultureInfo.InvariantCulture),
            ["playback_state"] = playbackState,
            ["player_device_id"] = deviceId,
            ["player_client"] = client,
            ["updated_at"] = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
        };

        if (!string.IsNullOrWhiteSpace(audiobookStartKind))
        {
            extended["audiobook_start_kind"] = NormalizeAudiobookStartKind(audiobookStartKind);
        }

        if (durationSeconds.HasValue)
        {
            extended["duration_seconds"] = durationSeconds.Value.ToString("0.###", CultureInfo.InvariantCulture);
        }

        if (sessionId.HasValue)
        {
            extended["player_session_id"] = sessionId.Value.ToString("D");
        }

        foreach (var key in new[] { "hide_continue", "status_changed_at", "blocked_player_session_id" })
        {
            if (prior?.ExtendedProperties.TryGetValue(key, out var value) == true)
            {
                extended[key] = value;
            }
        }

        await _userStates.SaveAsync(new UserState
        {
            Revision = prior?.Revision ?? 0,
            UserId = profileId,
            AssetId = assetId,
            ContentHash = asset.ContentHash,
            ProgressPct = Math.Clamp(progressPct, 0, 100),
            LastAccessed = DateTimeOffset.UtcNow,
            ExtendedProperties = extended,
        }, ct);
    }

    private static bool ShouldPreservePriorResume(
        UserState? prior,
        double nextPositionSeconds,
        double? nextDurationSeconds,
        double nextProgressPct,
        ListeningSettingsDto settings,
        string? audiobookStartKind)
    {
        if (prior is null
            || string.IsNullOrWhiteSpace(audiobookStartKind)
            || !string.Equals(NormalizeAudiobookStartKind(audiobookStartKind), AudiobookStartKinds.Resume, StringComparison.Ordinal))
        {
            return false;
        }

        var priorPosition = ReadPositionSeconds(prior, nextDurationSeconds);
        if (priorPosition is not > 0)
        {
            return false;
        }

        var nearStartSeconds = Math.Max(0, settings.AudiobookNearStartGuardSeconds);
        var regressionGuardSeconds = Math.Max(0, settings.AudiobookResumeRegressionGuardSeconds);
        var regressionSeconds = priorPosition.Value - nextPositionSeconds;
        var nearStartRegression = nearStartSeconds > 0
            && nextPositionSeconds <= nearStartSeconds
            && priorPosition.Value > nearStartSeconds;

        return nearStartRegression
            || (regressionGuardSeconds > 0 && regressionSeconds >= regressionGuardSeconds && prior.ProgressPct > nextProgressPct);
    }

    private static double? ReadPositionSeconds(UserState state, double? currentDurationSeconds)
    {
        if (state.ExtendedProperties.TryGetValue("position_seconds", out var rawPosition)
            && double.TryParse(rawPosition, NumberStyles.Float, CultureInfo.InvariantCulture, out var position)
            && position > 0)
        {
            return position;
        }

        var duration = currentDurationSeconds;
        if (!duration.HasValue
            && state.ExtendedProperties.TryGetValue("duration_seconds", out var rawDuration)
            && double.TryParse(rawDuration, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsedDuration)
            && parsedDuration > 0)
        {
            duration = parsedDuration;
        }

        return duration is > 0 && state.ProgressPct is > 0 and < 100
            ? duration.Value * state.ProgressPct / 100d
            : null;
    }

    private static PlayerStateDto EmptyState(Guid profileId, string deviceId, string client) => new()
    {
        ProfileId = profileId,
        SessionId = Guid.NewGuid(),
        DeviceId = deviceId,
        Client = client,
        PlaybackState = PlayerPlaybackStates.Stopped,
        CreatedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow,
        Capabilities = new(),
        IsStale = true,
    };

    public async Task<IReadOnlyList<AudiobookListenHistoryItemDto>> GetAudiobookHistoryAsync(
        Guid? profileId,
        Guid workId,
        int? limit,
        CancellationToken ct = default)
    {
        var normalizedProfile = await _scope.RequireProfileAsync(profileId, ct);
        var settings = await GetPlaybackSettingsOrDefaultAsync(normalizedProfile, ct);
        return await _history.GetRecentAsync(
            normalizedProfile,
            workId,
            limit ?? settings.Listening.AudiobookHistoryLimit,
            ct, await _scope.AssetIdsAsync(ct));
    }

    public async Task<IReadOnlyList<AudiobookBookmarkDto>> GetAudiobookBookmarksAsync(
        Guid? profileId,
        Guid workId,
        CancellationToken ct = default)
    {
        var normalizedProfile = await _scope.RequireProfileAsync(profileId, ct);
        return await _bookmarks.GetByWorkAsync(normalizedProfile, workId, ct, await _scope.AssetIdsAsync(ct));
    }

    public async Task<AudiobookBookmarkDto> CreateAudiobookBookmarkAsync(
        Guid? profileId,
        Guid workId,
        CreateAudiobookBookmarkRequestDto request,
        CancellationToken ct = default)
    {
        if (!TryNormalizeAudiobookBookmarkRequest(request, out var normalizedRequest, out var validationError))
        {
            throw new ArgumentException(validationError, nameof(request));
        }

        var normalizedProfile = await _scope.RequireProfileAsync(profileId ?? request.ProfileId, ct);
        await _scope.RequireAssetAsync(normalizedRequest!.AssetId, ct, workId);
        return await _bookmarks.CreateAsync(normalizedProfile, workId, normalizedRequest, ct);
    }

    public static bool TryNormalizeAudiobookBookmarkRequest(
        CreateAudiobookBookmarkRequestDto request,
        out CreateAudiobookBookmarkRequestDto? normalizedRequest,
        out string? validationError)
    {
        normalizedRequest = null;
        validationError = null;
        if (request.AssetId == Guid.Empty)
        {
            validationError = "An asset id is required for an audiobook bookmark.";
            return false;
        }

        if (request.ChapterIndex is < 0)
        {
            validationError = "A bookmark chapter index cannot be negative.";
            return false;
        }

        if (!double.IsFinite(request.PositionSeconds) || request.PositionSeconds < 0)
        {
            validationError = "A bookmark position must be finite and nonnegative.";
            return false;
        }

        if (request.DurationSeconds is double duration
            && (!double.IsFinite(duration) || duration <= 0 || request.PositionSeconds > duration))
        {
            validationError = "A bookmark duration must be finite and positive, and cannot be before its position.";
            return false;
        }

        if (!AudiobookBookmarkNotePolicy.TryNormalize(request.Note, out var note))
        {
            validationError = $"A bookmark note cannot exceed {AudiobookBookmarkNotePolicy.MaximumLength} characters.";
            return false;
        }

        normalizedRequest = request with { Note = note };
        return true;
    }

    public async Task<bool> DeleteAudiobookBookmarkAsync(
        Guid? profileId,
        Guid bookmarkId,
        CancellationToken ct = default)
    {
        var normalizedProfile = await _scope.RequireProfileAsync(profileId, ct);
        return await _bookmarks.DeleteAsync(normalizedProfile, bookmarkId, ct, await _scope.AssetIdsAsync(ct));
    }

    private static string NormalizeDeviceId(string? deviceId) =>
        string.IsNullOrWhiteSpace(deviceId) ? "web" : deviceId.Trim();

    private static string NormalizeClient(string? client) =>
        string.IsNullOrWhiteSpace(client) ? "web" : client.Trim().ToLowerInvariant();

    private static string NormalizeCommand(string? command) =>
        string.IsNullOrWhiteSpace(command) ? PlayerCommands.Pause : command.Trim().ToLowerInvariant();

    private static string NormalizeRepeatMode(string? repeatMode) =>
        repeatMode?.Trim().ToLowerInvariant() switch
        {
            PlayerRepeatModes.One => PlayerRepeatModes.One,
            PlayerRepeatModes.All => PlayerRepeatModes.All,
            _ => PlayerRepeatModes.Off,
        };

    private static double? ClampVolume(double? volume) =>
        volume.HasValue ? Math.Clamp(volume.Value, 0d, 1d) : null;

    private static double RequirePlaybackRate(double? playbackRate) =>
        playbackRate.HasValue ? PlaybackRatePolicy.RequireValid(playbackRate.Value, nameof(playbackRate))
            : throw new ArgumentException("A playback speed is required.", nameof(playbackRate));

    private static double RequireScanRate(double? playbackRate) =>
        playbackRate.HasValue ? PlaybackRatePolicy.RequireValidScan(playbackRate.Value, nameof(playbackRate))
            : throw new ArgumentException("A scan speed is required.", nameof(playbackRate));

    private static void ValidateQueueReplacement(IReadOnlyList<PlayerQueueItemDto> items)
    {
        if (items.Count == 0)
        {
            return;
        }

        var experiences = items
            .Select(item => ExperienceFor(item.MediaType))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (experiences.Count > 1)
        {
            throw new InvalidOperationException("Music, audiobooks, and video cannot share one player queue.");
        }

        if (items.Count(item => IsAudiobook(item.MediaType)) > 1)
        {
            throw new InvalidOperationException("Audiobooks play one title at a time. Replace the current audiobook instead of queueing chapters.");
        }
    }

    private static void ValidateQueueAddition(IReadOnlyList<PlayerQueueItemDto> items)
    {
        if (items.Any(item => IsAudiobook(item.MediaType) || IsVideo(item.MediaType)))
        {
            throw new InvalidOperationException("Audiobooks and video use scoped queue replacement. Only music can be appended to the active queue.");
        }
    }

    private static PlayerQueueItemDto? ResolveHistoryItem(PlayerStateDto? state, PlayerHeartbeatDto heartbeat, Guid? assetId)
    {
        if (state is null || state.Queue.Count == 0)
        {
            return null;
        }

        if (heartbeat.QueueItemId.HasValue)
        {
            return state.Queue.FirstOrDefault(item => item.QueueItemId == heartbeat.QueueItemId.Value);
        }

        if (state.CurrentItem is not null)
        {
            return state.CurrentItem;
        }

        return assetId.HasValue
            ? state.Queue.FirstOrDefault(item => item.AssetId == assetId.Value)
            : null;
    }

    private async Task<PlayerQueueItemDto> EnrichHistoryItemAsync(PlayerQueueItemDto item, Guid profileId, CancellationToken ct)
    {
        if (item.AssetId is not Guid assetId || item.Chapters.Count > 0)
        {
            return item;
        }

        try
        {
            var manifest = await _playback.BuildManifestAsync(assetId, "web", profileId, ct);
            if (manifest is null)
            {
                return item;
            }

            var duration = item.DurationSeconds
                ?? manifest.Chapters
                    .Where(chapter => chapter.EndSeconds.HasValue)
                    .Select(chapter => chapter.EndSeconds!.Value)
                    .DefaultIfEmpty()
                    .Max();

            return item with
            {
                Chapters = manifest.Chapters,
                DurationSeconds = duration > 0 ? duration : item.DurationSeconds,
                StreamUrl = manifest.DirectStreamUrl ?? item.StreamUrl,
            };
        }
        catch
        {
            // History enrichment is best-effort; heartbeat persistence should still succeed if manifest lookup fails.
            return item;
        }
    }

    private static string NormalizeAudiobookStartKind(string? value) =>
        value?.Trim() switch
        {
            AudiobookStartKinds.Chapter => AudiobookStartKinds.Chapter,
            AudiobookStartKinds.History => AudiobookStartKinds.History,
            AudiobookStartKinds.Bookmark => AudiobookStartKinds.Bookmark,
            _ => AudiobookStartKinds.Resume,
        };

    private async Task<UserPlaybackSettingsDto> GetPlaybackSettingsOrDefaultAsync(Guid profileId, CancellationToken ct)
    {
        try
        {
            return await _settings.GetOrCreateDefaultsAsync(profileId, ct);
        }
        catch
        {
            return UserPlaybackSettingsDto.CreateDefaults(profileId);
        }
    }

    private static string ExperienceFor(IReadOnlyList<PlayerQueueItemDto> queue) =>
        queue.Select(item => ExperienceFor(item.MediaType)).FirstOrDefault()
        ?? PlayerExperienceModes.Music;

    private static string ExperienceFor(string? mediaType) =>
        IsAudiobook(mediaType)
            ? PlayerExperienceModes.Audiobook
            : IsVideo(mediaType)
                ? PlayerExperienceModes.Video
                : PlayerExperienceModes.Music;

    private static bool IsAudiobook(string? mediaType) =>
        mediaType?.Contains("audio", StringComparison.OrdinalIgnoreCase) == true
        && mediaType.Contains("book", StringComparison.OrdinalIgnoreCase);

    private static bool IsMusic(string? mediaType) =>
        mediaType?.Contains("music", StringComparison.OrdinalIgnoreCase) == true;

    private static bool IsVideo(string? mediaType) =>
        mediaType?.Contains("movie", StringComparison.OrdinalIgnoreCase) == true
        || mediaType?.Contains("film", StringComparison.OrdinalIgnoreCase) == true
        || mediaType?.Contains("video", StringComparison.OrdinalIgnoreCase) == true
        || mediaType?.Contains("tv", StringComparison.OrdinalIgnoreCase) == true
        || mediaType?.Contains("television", StringComparison.OrdinalIgnoreCase) == true
        || mediaType?.Contains("episode", StringComparison.OrdinalIgnoreCase) == true;

    private static double CalculateProgress(double? positionSeconds, double? durationSeconds)
    {
        if (!positionSeconds.HasValue || !durationSeconds.HasValue || durationSeconds.Value <= 0)
        {
            return 0;
        }

        return Math.Clamp(positionSeconds.Value / durationSeconds.Value * 100d, 0d, 100d);
    }

    private static double? TryParseDurationSeconds(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var text = value.Trim();
        if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds))
        {
            return seconds;
        }

        var parts = text.Split(':', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length is 2 or 3 && parts.All(part => double.TryParse(part, NumberStyles.Float, CultureInfo.InvariantCulture, out _)))
        {
            var multiplier = 1d;
            var total = 0d;
            for (var i = parts.Length - 1; i >= 0; i--)
            {
                total += double.Parse(parts[i], CultureInfo.InvariantCulture) * multiplier;
                multiplier *= 60d;
            }

            return total;
        }

        if (text.EndsWith("min", StringComparison.OrdinalIgnoreCase)
            && double.TryParse(text[..^3].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var minutes))
        {
            return minutes * 60d;
        }

        return null;
    }

    private sealed record PlayableWorkRow
    {
        public Guid WorkId { get; init; }
        public Guid? AssetId { get; init; }
        public Guid? CollectionId { get; init; }
        public string MediaType { get; init; } = string.Empty;
        public string Title { get; init; } = string.Empty;
        public string? Subtitle { get; init; }
        public string? Album { get; init; }
        public string? Author { get; init; }
        public string? Artist { get; init; }
        public string? Narrator { get; init; }
        public string? Series { get; init; }
        public string? Year { get; init; }
        public string? ContentRating { get; init; }
        public string? SeasonNumber { get; init; }
        public string? EpisodeNumber { get; init; }
        public string? EpisodeTitle { get; init; }
        public string? Quality { get; init; }
        public string? Duration { get; init; }
        public double? CoverageStartSeconds { get; init; }
        public double? FileProgressPct { get; init; }
    }
}
