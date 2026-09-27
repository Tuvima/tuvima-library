using System.Net;
using System.Net.Http.Json;
using MediaEngine.Contracts.Playback;
using MediaEngine.Web.Services.Integration;
using MediaEngine.Web.Services.Playback;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.JSInterop;

namespace MediaEngine.Web.Tests;

public sealed class PlaybackSessionControllerTests
{
    [Fact]
    public async Task AudiobookAdvancesToNextOriginalFileAndChapterSelectionUsesItsAsset()
    {
        var first = Guid.NewGuid(); var second = Guid.NewGuid();
        var service = new PlaybackSessionController(null!, null!);
        var book = CreateAudiobookItem("One recording", $"/stream/{first:D}") with
        {
            AssetId = first,
            Chapters = [new() {Index=0,AssetId=first,Title="Original part one",StartSeconds=0,EndSeconds=60},
                        new() {Index=1,AssetId=second,Title="Original part two",StartSeconds=0,EndSeconds=90}],
        };
        await service.PlayAudiobookAsync(book);
        Assert.Equal("Original part one",service.CurrentChapter!.Title);
        await service.CompleteCurrentAsync();
        Assert.Equal(second,service.CurrentItem!.AssetId);
        Assert.Equal($"/engine-stream/{second:D}",service.CurrentBrowserStreamUrl);
        Assert.Equal("Original part two",service.CurrentChapter!.Title);
        await service.PlayAudiobookChapterAsync(0);
        Assert.Equal(first,service.CurrentItem!.AssetId);
        Assert.Equal($"/engine-stream/{first:D}",service.CurrentBrowserStreamUrl);
    }

    [Theory]
    [InlineData("/media/assets/312274cc-8cf0-4ead-9934-1aa78eb2b195/stream")]
    [InlineData("http://engine.test/media/assets/312274cc-8cf0-4ead-9934-1aa78eb2b195/stream")]
    public async Task ResourceStreamRoutesUseAuthenticatedDashboardProxy(string stream)
    {
        var service = new PlaybackSessionController(null!, null!);
        await service.PlayAudiobookAsync(CreateAudiobookItem("Recording", stream));
        Assert.Equal("/engine-stream/312274cc-8cf0-4ead-9934-1aa78eb2b195", service.CurrentBrowserStreamUrl);
    }

    [Fact]
    public async Task HeartbeatsUseServerSessionMonotonicSequenceAndExplicitEndFact()
    {
        var profileId = Guid.NewGuid();
        var serverSessionId = Guid.NewGuid();
        var handler = new TelemetryHeartbeatHandler(profileId, serverSessionId);
        var apiClient = new EngineApiClient(
            new HttpClient(handler) { BaseAddress = new Uri("http://engine.test") },
            NullLogger<EngineApiClient>.Instance);
        using var profiles = new ActiveProfileSessionService(new NullJsRuntime(), apiClient);
        await using var orchestrator = new UIOrchestratorService(
            apiClient, new UniverseStateContainer(), profiles, new ConfigurationManager(),
            NullLogger<UIOrchestratorService>.Instance);
        var service = new PlaybackSessionController(orchestrator, apiClient);
        service.RestoreState(new ListenPlaybackSnapshot
        {
            Queue = [CreateAudiobookItem("Telemetry", "/stream/telemetry")],
            CurrentIndex = 0,
            CurrentTimeSeconds = 20,
            DurationSeconds = 60,
            IsPlaying = true,
        });

        await service.ReportHeartbeatAsync(force: true);
        await service.ReportHeartbeatAsync(force: true);
        await service.CompleteCurrentAsync();

        Assert.Equal([1, 2, 3], handler.Heartbeats.Select(value => value.Sequence));
        Assert.Null(handler.Heartbeats[0].SessionId);
        Assert.All(handler.Heartbeats.Skip(1), value => Assert.Equal(serverSessionId, value.SessionId));
        Assert.False(handler.Heartbeats[0].HasPlaybackEnded);
        Assert.True(handler.Heartbeats[^1].HasPlaybackEnded);
    }

    [Fact]
    public async Task PendingTransportCommands_CoalesceStateAndKeepDistinctUserActions()
    {
        var service = new PlaybackSessionController(null!, null!);
        var dispatched = new List<PlaybackTransportCommand>();
        service.TransportCommandRequested += command =>
        {
            dispatched.Add(command);
            return Task.CompletedTask;
        };

        service.SetTransportHostNotReady();
        await service.RequestTransportCommandAsync(new("seek", 10));
        await service.RequestTransportCommandAsync(new("seek", 20));
        await service.RequestTransportCommandAsync(new("set-volume", .25));
        await service.RequestTransportCommandAsync(new("set-volume", .75));
        await service.RequestTransportCommandAsync(new("toggle-play"));
        await service.RequestTransportCommandAsync(new("toggle-play"));

        await service.SetTransportHostReadyAsync();

        Assert.Equal(4, dispatched.Count);
        Assert.Single(dispatched, command => command.Action == "seek" && command.Value == 20);
        Assert.Single(dispatched, command => command.Action == "set-volume" && command.Value == .75);
        Assert.Equal(2, dispatched.Count(command => command.Action == "toggle-play"));
        Assert.Equal(dispatched.Count, dispatched.Select(command => command.RequestId).Distinct().Count());
    }

    [Fact]
    public async Task TransportCommand_WithSameRequestId_IsNotDispatchedTwice()
    {
        var service = new PlaybackSessionController(null!, null!);
        var dispatchCount = 0;
        service.TransportCommandRequested += _ =>
        {
            dispatchCount++;
            return Task.CompletedTask;
        };
        var reconnectReplay = new PlaybackTransportCommand("pause", RequestId: 9001);

        await service.RequestTransportCommandAsync(reconnectReplay);
        await service.RequestTransportCommandAsync(reconnectReplay);

        Assert.Equal(1, dispatchCount);
    }

    [Fact]
    public void CreateSnapshot_RoundTripsQueueHistoryAndTransportState()
    {
        var service = new PlaybackSessionController(null!, null!);
        var snapshot = new ListenPlaybackSnapshot
        {
            Queue =
            [
                CreateQueueItem("Current Song", "stream://current"),
                CreateQueueItem("Next Song", "stream://next"),
            ],
            History =
            [
                CreateQueueItem("Previous Song", "stream://previous") with { PlayedAt = new DateTimeOffset(2026, 4, 21, 12, 0, 0, TimeSpan.Zero) }
            ],
            CurrentIndex = 0,
            SourceLabel = "All Music",
            IsPanelOpen = true,
            ActiveTab = ListenPlaybackTabs.History,
            CurrentTimeSeconds = 42,
            DurationSeconds = 180,
            Volume = 0.55,
            IsMuted = true,
            PlaybackRate = 1.5d,
            Experience = PlayerExperienceModes.Audiobook,
            NeedsUserGestureToStart = true,
            IsPlaying = false,
            IsPopupOpen = true,
            AudiobookHistory =
            [
                new AudiobookListenHistoryItemDto
                {
                    Id = Guid.NewGuid(),
                    WorkId = Guid.NewGuid(),
                    AssetId = Guid.NewGuid(),
                    Title = "Previous audiobook position",
                    PositionSeconds = 1240,
                    ProgressPct = 27.5,
                    StartedAt = DateTimeOffset.UtcNow.AddMinutes(-3),
                    EndedAt = DateTimeOffset.UtcNow.AddMinutes(-2),
                },
            ],
            AudiobookBookmarks =
            [
                new AudiobookBookmarkDto
                {
                    Id = Guid.NewGuid(),
                    WorkId = Guid.NewGuid(),
                    AssetId = Guid.NewGuid(),
                    PositionSeconds = 512,
                    CreatedAt = DateTimeOffset.UtcNow,
                },
            ],
            PlaybackStartVersion = 7,
            SleepTimerMode = ListenSleepTimerModes.EndOfChapter,
        };

        service.RestoreState(snapshot);

        var roundTrip = service.CreateSnapshot();

        Assert.Equal(2, roundTrip.Queue.Count);
        Assert.Single(roundTrip.History);
        Assert.Equal(ListenPlaybackTabs.History, roundTrip.ActiveTab);
        Assert.Equal(42, roundTrip.CurrentTimeSeconds);
        Assert.Equal(180, roundTrip.DurationSeconds);
        Assert.Equal(0.55, roundTrip.Volume, 3);
        Assert.True(roundTrip.IsMuted);
        Assert.Equal(1.5d, roundTrip.PlaybackRate);
        Assert.Equal(PlayerExperienceModes.Audiobook, roundTrip.Experience);
        Assert.True(roundTrip.NeedsUserGestureToStart);
        Assert.False(roundTrip.IsPlaying);
        Assert.True(roundTrip.IsPopupOpen);
        Assert.Equal(7, roundTrip.PlaybackStartVersion);
        Assert.Single(roundTrip.AudiobookHistory);
        Assert.Single(roundTrip.AudiobookBookmarks);
        Assert.Equal(ListenSleepTimerModes.EndOfChapter, roundTrip.SleepTimerMode);
    }

    [Fact]
    public async Task PlayAudiobookAsync_UsesSingleItemModeAndAppliesResumeRewind()
    {
        var service = new PlaybackSessionController(null!, null!);
        var audiobook = CreateAudiobookItem("Dungeon Crawler Carl", "stream://dungeon-crawler-carl") with
        {
            InitialPositionSeconds = 123,
        };

        await service.PlayAudiobookAsync(audiobook, "Dungeon Crawler Carl");

        Assert.True(service.IsAudiobookMode);
        Assert.Single(service.Queue);
        Assert.Equal("Dungeon Crawler Carl", service.CurrentItem?.Title);
        Assert.Equal(113, service.CurrentTimeSeconds);
        Assert.Equal(1.25d, service.PlaybackRate);
        Assert.True(service.IsPlaying);
        Assert.False(service.NeedsUserGestureToStart);
    }

    [Fact]
    public async Task PlayAudiobookAsync_StartsFromBeginningWhenAutomaticResumeIsDisabled()
    {
        var settings = UserPlaybackSettingsDto.CreateDefaults(Guid.NewGuid());
        settings.General.ResumePlayback = false;
        var service = new PlaybackSessionController(null!, null!, preferences: new PlaybackPreferencesStub(settings));
        var audiobook = CreateAudiobookItem("Dungeon Crawler Carl", "stream://dungeon-crawler-carl") with
        {
            InitialPositionSeconds = 123,
        };

        await service.PlayAudiobookAsync(audiobook, "Dungeon Crawler Carl");

        Assert.Equal(0, service.CurrentTimeSeconds);
    }

    [Fact]
    public async Task PlayAudiobookChapterAsync_UsesExactChapterStartWithoutResumeRewind()
    {
        var service = new PlaybackSessionController(null!, null!);
        var chapter = new PlaybackChapterDto
        {
            Index = 2,
            Title = "003",
            StartSeconds = 1256.245,
            EndSeconds = 2664.814,
        };
        var audiobook = CreateAudiobookItem("Dungeon Crawler Carl", "stream://dungeon-crawler-carl") with
        {
            InitialPositionSeconds = 6400,
            Chapters = [chapter],
        };

        await service.PlayAudiobookChapterAsync(audiobook, chapter, "Dungeon Crawler Carl");

        Assert.Equal(1256.245, service.CurrentTimeSeconds, 3);
        Assert.Equal(2, service.CurrentItem?.ChapterIndex);
        Assert.True(service.CurrentItem?.StartAtExactPosition);
        Assert.Equal("003", service.CurrentItem?.Subtitle);
    }

    [Fact]
    public async Task PlayAudiobookAsync_IncrementsPlaybackStartVersionForSameStreamStarts()
    {
        var service = new PlaybackSessionController(null!, null!);
        var audiobook = CreateAudiobookItem("Dungeon Crawler Carl", "stream://dungeon-crawler-carl");

        await service.PlayAudiobookAsync(audiobook, "Dungeon Crawler Carl");
        var firstVersion = service.PlaybackStartVersion;

        await service.PlayAudiobookAsync(audiobook with { InitialPositionSeconds = 1200 }, "Dungeon Crawler Carl");

        Assert.True(service.PlaybackStartVersion > firstVersion);
        Assert.Equal(1190, service.CurrentTimeSeconds);
    }

    [Fact]
    public async Task PlayAudiobookAsync_CreatesStartCommandWithBootstrappedStreamAndResumePosition()
    {
        var service = new PlaybackSessionController(null!, null!);
        var assetId = Guid.NewGuid();
        PlaybackTransportCommand? command = null;
        service.TransportCommandRequested += next =>
        {
            command = next;
            return Task.CompletedTask;
        };
        var audiobook = CreateAudiobookItem("Dungeon Crawler Carl", "stream://placeholder") with
        {
            AssetId = assetId,
            StreamUrl = null,
            InitialPositionSeconds = 123,
            Chapters =
            [
                new PlaybackChapterDto
                {
                    Index = 0,
                    Title = "Intro",
                    StartSeconds = 0,
                    EndSeconds = 15,
                },
                new PlaybackChapterDto
                {
                    Index = 1,
                    Title = "Chapter 1",
                    StartSeconds = 15,
                    EndSeconds = 1255,
                },
            ],
        };

        await service.PlayAudiobookAsync(audiobook, "Dungeon Crawler Carl");

        Assert.NotNull(command);
        Assert.Equal("start", command.Action);
        Assert.Equal($"/engine-stream/{assetId:D}", command.StreamUrl);
        Assert.Equal(113, command.PositionSeconds);
        Assert.Equal(1.25d, command.PlaybackRate);
        Assert.Equal(service.PlaybackStartVersion, command.RequestId);
        Assert.Equal(113, service.CurrentTimeSeconds);
    }

    [Fact]
    public async Task PlayAudiobookAsync_EmitsStartCommandBeforeManifestRefreshCompletes()
    {
        var assetId = Guid.NewGuid();
        var handler = new BlockingManifestHandler(assetId);
        var apiClient = new EngineApiClient(
            new HttpClient(handler) { BaseAddress = new Uri("http://engine.test") },
            NullLogger<EngineApiClient>.Instance);
        var service = new PlaybackSessionController(null!, apiClient);
        var commandSeen = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        PlaybackTransportCommand? command = null;
        service.TransportCommandRequested += next =>
        {
            command = next;
            commandSeen.TrySetResult();
            return Task.CompletedTask;
        };
        var audiobook = CreateAudiobookItem("Dungeon Crawler Carl", "stream://placeholder") with
        {
            AssetId = assetId,
            StreamUrl = null,
            InitialPositionSeconds = 123,
        };

        var startTask = service.PlayAudiobookAsync(audiobook, "Dungeon Crawler Carl");
        var first = await Task.WhenAny(commandSeen.Task, Task.Delay(TimeSpan.FromSeconds(1)));

        Assert.Same(commandSeen.Task, first);
        Assert.False(startTask.IsCompleted);
        Assert.NotNull(command);
        Assert.Equal("start", command.Action);
        Assert.Equal($"/engine-stream/{assetId:D}", command.StreamUrl);
        Assert.Equal(113, command.PositionSeconds);

        handler.ReleaseManifest();
        await startTask;
    }

    [Fact]
    public async Task SetPlaybackRateAsync_SetsExactSelectedRate()
    {
        var service = new PlaybackSessionController(null!, null!);

        await service.SetPlaybackRateAsync(1.3d);

        Assert.Equal(1.3d, service.PlaybackRate);
    }

    [Fact]
    public void NormalizeChapter_PreservesEmbeddedNumericTrackTitles()
    {
        Assert.Equal("001", PlaybackSessionController.NormalizeChapter(new PlaybackChapterDto { Title = "001" }, 0).Title);
        Assert.Equal("Dedication", PlaybackSessionController.NormalizeChapter(new PlaybackChapterDto { Title = "Dedication" }, 1).Title);
        Assert.Equal("Track 3", PlaybackSessionController.NormalizeChapter(new PlaybackChapterDto(), 2).Title);
    }

    [Fact]
    public async Task AddQueueItemAsync_Audiobook_ReplacesMusicQueueInsteadOfAppending()
    {
        var service = new PlaybackSessionController(null!, null!);
        await service.AddQueueItemAsync(CreateQueueItem("Current Song", "stream://song"));

        await service.AddQueueItemAsync(CreateAudiobookItem("Dungeon Crawler Carl", "stream://book"));

        Assert.True(service.IsAudiobookMode);
        Assert.Single(service.Queue);
        Assert.Equal("Dungeon Crawler Carl", service.Queue[0].Title);
        Assert.Single(service.History);
        Assert.Equal("Current Song", service.History[0].Title);
    }

    [Fact]
    public async Task PlayVideoAsync_UsesSharedSessionAndExpandsPersistentVideo()
    {
        var service = new PlaybackSessionController(null!, null!);
        PlaybackTransportCommand? command = null;
        service.TransportCommandRequested += next =>
        {
            command = next;
            return Task.CompletedTask;
        };
        var video = CreateVideoItem("Inception", "stream://inception") with
        {
            InitialPositionSeconds = 3272,
            Quality = "1080p",
        };

        await service.PlayVideoAsync(video, "Inception");

        Assert.True(service.IsVideoMode);
        Assert.True(service.IsVideoExpanded);
        Assert.False(service.IsMusicMode);
        Assert.False(service.IsAudiobookMode);
        Assert.Single(service.Queue);
        Assert.Equal(3272, service.CurrentTimeSeconds);
        Assert.Equal(10, service.SkipBackSeconds);
        Assert.Equal(30, service.SkipForwardSeconds);
        Assert.Equal("1080p", service.CurrentItem?.Quality);
        Assert.Equal("start", command?.Action);
        Assert.Equal("stream://inception", command?.StreamUrl);
    }

    [Fact]
    public async Task PlayVideoAsync_UsesSignedHlsUrlWhenManifestRequiresAdaptiveDelivery()
    {
        var service = new PlaybackSessionController(null!, null!);
        PlaybackTransportCommand? command = null;
        service.TransportCommandRequested += next =>
        {
            command = next;
            return Task.CompletedTask;
        };
        var packageId = Guid.NewGuid();
        var video = CreateVideoItem("Inception", "/stream/source") with
        {
            Manifest = new PlaybackManifestDto
            {
                RecommendedDelivery = PlaybackDeliveryModes.Hls,
                DirectPlaySupported = false,
                DirectStreamUrl = "/stream/source",
                HlsUrl = $"/stream/hls/grant/{packageId:D}/master.m3u8",
            },
        };

        await service.PlayVideoAsync(video, "Inception");

        Assert.Equal($"/engine-hls/grant/{packageId:D}/master.m3u8", command?.StreamUrl);
        Assert.Equal($"/engine-hls/grant/{packageId:D}/master.m3u8", service.CurrentBrowserStreamUrl);
    }

    [Fact]
    public async Task AddQueueItemAsync_Video_ReplacesMusicQueueInsteadOfAppending()
    {
        var service = new PlaybackSessionController(null!, null!);
        await service.AddQueueItemAsync(CreateQueueItem("Current Song", "stream://song"));

        await service.AddQueueItemAsync(CreateVideoItem("Inception", "stream://inception"));

        Assert.True(service.IsVideoMode);
        Assert.Single(service.Queue);
        Assert.Equal("Inception", service.CurrentItem?.Title);
        Assert.Single(service.History);
        Assert.Equal("Current Song", service.History[0].Title);
    }

    [Fact]
    public void RestoreState_RoundTripsExpandedVideoExperience()
    {
        var service = new PlaybackSessionController(null!, null!);
        service.RestoreState(new ListenPlaybackSnapshot
        {
            Queue = [CreateVideoItem("The Expanse", "stream://episode")],
            CurrentIndex = 0,
            Experience = PlayerExperienceModes.Video,
            IsVideoExpanded = true,
            IsPlaying = true,
        });

        var snapshot = service.CreateSnapshot();

        Assert.True(service.IsVideoMode);
        Assert.True(service.IsVideoExpanded);
        Assert.Equal(PlayerExperienceModes.Video, snapshot.Experience);
        Assert.True(snapshot.IsVideoExpanded);
    }

    [Fact]
    public async Task PlayAudiobookAsync_NormalizesRelativeStreamUrlToEngineUrl()
    {
        var apiClient = new EngineApiClient(
            new HttpClient(new PlayerSyncHandler()) { BaseAddress = new Uri("http://engine.test") },
            NullLogger<EngineApiClient>.Instance);
        var service = new PlaybackSessionController(null!, apiClient);
        var audiobook = CreateAudiobookItem("Dungeon Crawler Carl", "/stream/312274cc-8cf0-4ead-9934-1aa78eb2b195");

        await service.PlayAudiobookAsync(audiobook, "Dungeon Crawler Carl");

        Assert.Equal("http://engine.test/stream/312274cc-8cf0-4ead-9934-1aa78eb2b195", service.CurrentStreamUrl);
    }

    [Fact]
    public async Task CurrentBrowserStreamUrl_UsesDashboardProxyForEngineDirectStreams()
    {
        var apiClient = new EngineApiClient(
            new HttpClient(new PlayerSyncHandler()) { BaseAddress = new Uri("http://engine.test") },
            NullLogger<EngineApiClient>.Instance);
        var service = new PlaybackSessionController(null!, apiClient);
        var audiobook = CreateAudiobookItem("Dungeon Crawler Carl", "/stream/312274cc-8cf0-4ead-9934-1aa78eb2b195");

        await service.PlayAudiobookAsync(audiobook, "Dungeon Crawler Carl");

        Assert.Equal("http://engine.test/stream/312274cc-8cf0-4ead-9934-1aa78eb2b195", service.CurrentStreamUrl);
        Assert.Equal("/engine-stream/312274cc-8cf0-4ead-9934-1aa78eb2b195", service.CurrentBrowserStreamUrl);
    }

    [Fact]
    public void ClearUpcoming_RemovesOnlyFutureQueueItems()
    {
        var service = new PlaybackSessionController(null!, null!);
        service.RestoreState(new ListenPlaybackSnapshot
        {
            Queue =
            [
                CreateQueueItem("Current", "stream://current"),
                CreateQueueItem("Upcoming One", "stream://one"),
                CreateQueueItem("Upcoming Two", "stream://two"),
            ],
            CurrentIndex = 0,
        });

        service.ClearUpcoming();

        Assert.Single(service.Queue);
        Assert.Equal("Current", service.Queue[0].Title);
    }

    [Fact]
    public void RemoveUpcomingAt_DoesNotRemoveCurrentItem()
    {
        var service = new PlaybackSessionController(null!, null!);
        service.RestoreState(new ListenPlaybackSnapshot
        {
            Queue =
            [
                CreateQueueItem("Current", "stream://current"),
                CreateQueueItem("Upcoming", "stream://upcoming"),
            ],
            CurrentIndex = 0,
        });

        service.RemoveUpcomingAt(0);

        Assert.Equal(2, service.Queue.Count);

        service.RemoveUpcomingAt(1);

        Assert.Single(service.Queue);
        Assert.Equal("Current", service.Queue[0].Title);
    }

    [Fact]
    public void ClosePlayer_ClearsQueueAndHistory()
    {
        var service = new PlaybackSessionController(null!, null!);
        service.RestoreState(new ListenPlaybackSnapshot
        {
            Queue = [CreateQueueItem("Current", "stream://current")],
            History = [CreateQueueItem("Previous", "stream://previous")],
            CurrentIndex = 0,
            IsPlaying = true,
            IsPopupOpen = true,
        });

        service.ClosePlayer();

        Assert.Empty(service.Queue);
        Assert.Empty(service.History);
        Assert.False(service.HasQueue);
        Assert.False(service.IsPopupOpen);
        Assert.True(service.IsDismissed);
    }

    private static ListenQueueItem CreateQueueItem(string title, string streamUrl) => new()
    {
        WorkId = Guid.NewGuid(),
        MediaType = "Music",
        Title = title,
        Subtitle = "Artist",
        Album = "Album",
        Duration = "3:30",
        StreamUrl = streamUrl,
    };

    private static ListenQueueItem CreateAudiobookItem(string title, string streamUrl) => new()
    {
        WorkId = Guid.NewGuid(),
        AssetId = Guid.NewGuid(),
        MediaType = "Audiobooks",
        Title = title,
        Subtitle = "Matt Dinniman",
        Album = title,
        Duration = "11:32:00",
        StreamUrl = streamUrl,
    };

    private static ListenQueueItem CreateVideoItem(string title, string streamUrl) => new()
    {
        WorkId = Guid.NewGuid(),
        AssetId = Guid.NewGuid(),
        MediaType = "Movie",
        Title = title,
        Year = "2010",
        Duration = "2:28:00",
        StreamUrl = streamUrl,
        Manifest = new PlaybackManifestDto
        {
            MediaType = "Movie",
            DirectPlaySupported = true,
            DirectStreamUrl = streamUrl,
            Technical = new PlaybackTechnicalInfoDto
            {
                Width = 1920,
                Height = 1080,
                VideoCodec = "h264",
            },
        },
    };

    private sealed class PlayerSyncHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            object payload = request.RequestUri?.AbsolutePath.Contains("/history", StringComparison.OrdinalIgnoreCase) == true
                ? Array.Empty<AudiobookListenHistoryItemDto>()
                : new PlayerStateDto { Experience = PlayerExperienceModes.Audiobook, PlaybackRate = 1.25d };

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(payload),
            });
        }
    }

    private sealed class TelemetryHeartbeatHandler(Guid profileId, Guid sessionId) : HttpMessageHandler
    {
        public List<PlayerHeartbeatDto> Heartbeats { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri?.AbsolutePath == "/profiles")
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(new[]
                    {
                        new
                        {
                            id = profileId,
                            display_name = "Viewer",
                            avatar_color = "#000000",
                            role = "RestrictedProfile",
                            created_at = DateTimeOffset.UtcNow,
                        },
                    }),
                };
            }
            if (request.RequestUri?.AbsolutePath == "/api/v1/player/heartbeat")
            {
                Heartbeats.Add((await request.Content!.ReadFromJsonAsync<PlayerHeartbeatDto>(cancellationToken))!);
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(new PlayerStateDto
                    {
                        SessionId = sessionId,
                        ProfileId = profileId,
                        Experience = PlayerExperienceModes.Audiobook,
                        PlaybackRate = 1,
                    }),
                };
            }
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }
    }

    private sealed class NullJsRuntime : IJSRuntime
    {
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) =>
            ValueTask.FromResult(default(TValue)!);
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args) =>
            ValueTask.FromResult(default(TValue)!);
    }

    private sealed class BlockingManifestHandler : HttpMessageHandler
    {
        private readonly Guid _assetId;
        private readonly TaskCompletionSource<PlaybackManifestDto> _manifest = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public BlockingManifestHandler(Guid assetId)
        {
            _assetId = assetId;
        }

        public void ReleaseManifest() =>
            _manifest.TrySetResult(new PlaybackManifestDto
            {
                AssetId = _assetId,
                MediaType = "Audiobooks",
                DirectPlaySupported = true,
                DirectStreamUrl = $"/stream/{_assetId:D}",
                Chapters =
                [
                    new PlaybackChapterDto
                    {
                        Index = 0,
                        Title = "Chapter 1",
                        StartSeconds = 0,
                        EndSeconds = 600,
                    },
                ],
            });

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri?.AbsolutePath.Contains("/manifest", StringComparison.OrdinalIgnoreCase) == true)
            {
                var manifest = await _manifest.Task.WaitAsync(cancellationToken);
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(manifest),
                };
            }

            object payload = request.RequestUri?.AbsolutePath.Contains("/history", StringComparison.OrdinalIgnoreCase) == true
                ? Array.Empty<AudiobookListenHistoryItemDto>()
                : new PlayerStateDto { Experience = PlayerExperienceModes.Audiobook, PlaybackRate = 1.25d };

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(payload),
            };
        }
    }

    private sealed class PlaybackPreferencesStub(UserPlaybackSettingsDto settings) : IUserPlaybackPreferencesAccessor
    {
        public Task<UserPlaybackSettingsDto?> GetAsync(CancellationToken ct = default) =>
            Task.FromResult<UserPlaybackSettingsDto?>(settings);

        public void UpdateCache(UserPlaybackSettingsDto next) { }

        public void Invalidate() { }
    }
}
