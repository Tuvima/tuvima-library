using MediaEngine.Contracts.Playback;
using MediaEngine.Contracts.Details;
using MediaEngine.Web.Services.Integration;
using MediaEngine.Web.Services.Playback;
using MediaEngine.Web.Tests.Support;
using Microsoft.Extensions.DependencyInjection;

namespace MediaEngine.Web.Tests;

public sealed class PlaybackCommandSinkTests
{
    [Fact]
    public async Task DirectAndBroadcastAdaptersDispatchEquivalentCurrentIdentityEnvelopes()
    {
        var preferences = new Preferences(Guid.NewGuid());
        var playback = CreatePlayback(preferences);
        using var services = new ServiceCollection().AddSingleton<IUserPlaybackPreferencesAccessor>(preferences).BuildServiceProvider();
        var owner = new ListenPlaybackCommandOwner(services, playback);
        var observed = playback.CreateSnapshot();
        var commands = new List<PlaybackTransportCommand>();
        playback.TransportCommandRequested += command => { commands.Add(command); return Task.CompletedTask; };
        var direct = new DirectPlaybackCommandSink(owner);
        var broadcast = new BroadcastPlaybackCommandSink(owner.RecipientId, Guid.NewGuid(), new OwnerPlaybackCommandChannel(owner));
        foreach (var sink in new IPlaybackCommandSink[] { direct, broadcast })
        {
            var reply = await sink.SendAsync(observed, new() { Action = ListenPlaybackCommandActions.Seek, Value = 42 });
            Assert.Equal(AudiobookBookmarkOperationOutcomes.Success, reply?.Outcome);
        }
        Assert.Equal(2, commands.Count);
        Assert.All(commands, command => { Assert.Equal("seek", command.Action); Assert.Equal(42, command.Value); });
    }

    [Fact]
    public async Task ConcurrentDuplicateCommandIsAppliedOnceAndReturnsTheSameReply()
    {
        var preferences = new Preferences(Guid.NewGuid());
        var playback = CreatePlayback(preferences);
        using var services = new ServiceCollection().AddSingleton<IUserPlaybackPreferencesAccessor>(preferences).BuildServiceProvider();
        var owner = new ListenPlaybackCommandOwner(services, playback);
        var calls = 0;
        playback.TransportCommandRequested += _ => { calls++; return Task.CompletedTask; };
        var command = Envelope(owner, playback, ListenPlaybackCommandActions.TogglePlay);
        var replies = await Task.WhenAll(owner.HandleAsync(command), owner.HandleAsync(command));
        Assert.Equal(1, calls);
        Assert.Same(replies[0], replies[1]);
    }

    [Fact]
    public async Task StaleAndMissingIdentityOrLostProfileAuthorityNeverDispatch()
    {
        var preferences = new Preferences(Guid.NewGuid());
        var playback = CreatePlayback(preferences);
        using var services = new ServiceCollection().AddSingleton<IUserPlaybackPreferencesAccessor>(preferences).BuildServiceProvider();
        var owner = new ListenPlaybackCommandOwner(services, playback);
        var command = Envelope(owner, playback, ListenPlaybackCommandActions.ToggleMute);
        var calls = 0;
        playback.TransportCommandRequested += _ => { calls++; return Task.CompletedTask; };
        foreach (var invalid in new[]
        {
            command with { CommandId = Guid.NewGuid(), ExpectedPlaybackRequestVersion = command.ExpectedPlaybackRequestVersion - 1 },
            command with { CommandId = Guid.NewGuid(), WorkId = Guid.NewGuid() },
            command with { CommandId = Guid.NewGuid(), ExpectedAssetId = Guid.NewGuid() },
            command with { CommandId = Guid.NewGuid(), ExpectedPlaybackRequestVersion = null },
            command with { CommandId = Guid.NewGuid(), ProfileId = null },
        }) Assert.Equal(AudiobookBookmarkOperationOutcomes.DefiniteFailure, (await owner.HandleAsync(invalid))?.Outcome);
        preferences.ActiveProfileId = null;
        Assert.Equal(AudiobookBookmarkOperationOutcomes.DefiniteFailure,
            (await owner.HandleAsync(command with { CommandId = Guid.NewGuid(), ProfileId = null }))?.Outcome);
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task RemovalFindsTheIntendedDuplicateOccurrenceAfterAnotherRowIsRemoved()
    {
        var preferences = new Preferences(Guid.NewGuid());
        var playback = CreatePlayback(preferences);
        var duplicate = Song();
        await playback.AddQueueItemAsync(duplicate);
        await playback.AddQueueItemAsync(duplicate);
        var snapshot = playback.CreateSnapshot();
        Assert.NotEqual(snapshot.Queue[1].QueueEntryId, snapshot.Queue[2].QueueEntryId);
        using var services = new ServiceCollection().AddSingleton<IUserPlaybackPreferencesAccessor>(preferences).BuildServiceProvider();
        var owner = new ListenPlaybackCommandOwner(services, playback);
        var sink = new DirectPlaybackCommandSink(owner);
        playback.RemoveUpcomingAt(1);
        var reply = await sink.SendAsync(snapshot, new() { Action = ListenPlaybackCommandActions.RemoveUpcoming, Index = 2 });
        Assert.Equal(AudiobookBookmarkOperationOutcomes.Success, reply?.Outcome);
        Assert.Single(playback.Queue);
        Assert.Equal(snapshot.Queue[0].QueueEntryId, playback.CurrentItem?.QueueEntryId);
        var vanished = await sink.SendAsync(snapshot, new() { Action = ListenPlaybackCommandActions.RemoveUpcoming, Index = 1 });
        Assert.Equal(AudiobookBookmarkOperationOutcomes.DefiniteFailure, vanished?.Outcome);
    }

    [Fact]
    public async Task ClearKeepsCurrentAndRemovalCannotTargetCurrent()
    {
        var preferences = new Preferences(Guid.NewGuid());
        var playback = CreatePlayback(preferences);
        await playback.AddQueueItemAsync(Song());
        using var services = new ServiceCollection().AddSingleton<IUserPlaybackPreferencesAccessor>(preferences).BuildServiceProvider();
        var sink = new DirectPlaybackCommandSink(new ListenPlaybackCommandOwner(services, playback));
        var snapshot = playback.CreateSnapshot();
        Assert.Equal(AudiobookBookmarkOperationOutcomes.DefiniteFailure,
            (await sink.SendAsync(snapshot, new() { Action = ListenPlaybackCommandActions.RemoveUpcoming, Index = 0 }))?.Outcome);
        Assert.Equal(AudiobookBookmarkOperationOutcomes.Success,
            (await sink.SendAsync(snapshot, new() { Action = ListenPlaybackCommandActions.ClearUpcoming }))?.Outcome);
        Assert.Single(playback.Queue);
        Assert.Equal(snapshot.Queue[0].QueueEntryId, playback.CurrentItem?.QueueEntryId);
    }

    [Theory]
    [InlineData("Music")]
    [InlineData("Audiobooks")]
    [InlineData("Movie")]
    public void SharedPlayerCapabilitiesExcludeAllExitAndPresentationActions(string mediaType)
    {
        var snapshot = new ListenPlaybackSnapshot { Queue = [Song() with { MediaType = mediaType }], CurrentIndex = 0 };
        Assert.True(PlaybackCommandCapabilities.Supports(ListenPlaybackCommandActions.TogglePlay, snapshot));
        foreach (var action in new[] { ListenPlaybackCommandActions.ClosePlayer, ListenPlaybackCommandActions.PopupClosed,
            ListenPlaybackCommandActions.SetTab, ListenPlaybackCommandActions.TogglePanel, "collapse", "window-close", ListenPlaybackPresentationActions.NavigateIdentity, ListenPlaybackPresentationActions.RegisterPopup })
            Assert.False(PlaybackCommandCapabilities.Supports(action, snapshot));
        Assert.Equal(mediaType == "Audiobooks", PlaybackCommandCapabilities.Supports(ListenPlaybackCommandActions.SetSleepTimer, snapshot));
    }

    [Fact]
    public async Task BookmarkCleanupPreservesCapturedLeaseAfterSessionDismissal()
    {
        var channel = new RecordingChannel();
        var sink = new BroadcastPlaybackCommandSink(Guid.NewGuid(), Guid.NewGuid(), channel);
        var context = new ListenPlaybackCommandDto { Action = ListenPlaybackCommandActions.CloseBookmarkDialog,
            DialogId = Guid.NewGuid(), SessionLeaseId = Guid.NewGuid(), OwnerGeneration = 9,
            ProfileId = Guid.NewGuid(), WorkId = Guid.NewGuid(), ExpectedAssetId = Guid.NewGuid() };
        await sink.SendAsync(new ListenPlaybackSnapshot { IsDismissed = true, CurrentIndex = -1 }, context);
        var request = Assert.Single(channel.Commands);
        Assert.Equal(context.SessionLeaseId, request.SessionLeaseId);
        Assert.Equal(context.OwnerGeneration, request.OwnerGeneration);
        Assert.Equal(context.ExpectedAssetId, request.ExpectedAssetId);
        Assert.Equal(context.WorkId, request.WorkId);
        Assert.Equal(context.ProfileId, request.ProfileId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task VideoStartsPreserveRealNormalizedChaptersForFreshAndSuppliedManifests(bool supplied)
    {
        var manifest = new PlaybackManifestDto { DirectPlaySupported = true, DirectStreamUrl = "/stream/video", Chapters =
            [new() { Index = 2, Title = "Credits", StartSeconds = 90, EndSeconds = 100 },
             new() { Index = 1, Title = "First scene", StartSeconds = 0, EndSeconds = 90 }] };
        var api = EngineApiClientStub.Create(stub => stub.SetHandler(nameof(IEngineApiClient.GetPlaybackManifestAsync), _ => Task.FromResult<PlaybackManifestDto?>(manifest)));
        var playback = new PlaybackSessionController(null!, api);
        var video = Song() with { MediaType = "Movie", StreamUrl = null, Manifest = supplied ? manifest : null };
        await playback.PlayVideoAsync(video);
        Assert.Equal(new[] { "First scene", "Credits" }, playback.CurrentItem!.Chapters.Select(chapter => chapter.Title));
        Assert.Equal(new[] { 0d, 90d }, playback.CurrentItem.Chapters.Select(chapter => chapter.StartSeconds));
        Assert.Equal(2, playback.CreateSnapshot().Queue[0].Chapters.Count);
    }

    [Fact]
    public async Task SleepEnvelopeUsesBookRootWhileOrdinaryTransportUsesTheCurrentWork()
    {
        var channel = new RecordingChannel();
        var sink = new BroadcastPlaybackCommandSink(Guid.NewGuid(), Guid.NewGuid(), channel);
        var recording = Song() with { MediaType = "Audiobooks", AudiobookWorkId = Guid.NewGuid() };
        var snapshot = new ListenPlaybackSnapshot { Queue = [recording], CurrentIndex = 0, ProfileId = Guid.NewGuid() };
        await sink.SendAsync(snapshot, new() { Action = ListenPlaybackCommandActions.SetSleepTimer });
        await sink.SendAsync(snapshot, new() { Action = ListenPlaybackCommandActions.TogglePlay });
        Assert.Equal(recording.AudiobookWorkId, channel.Commands[0].WorkId);
        Assert.Equal(recording.WorkId, channel.Commands[1].WorkId);
        Assert.All(channel.Commands, command => Assert.Equal(recording.AssetId, command.ExpectedAssetId));
    }

    [Fact]
    public async Task HistoryReplayUsesTheOwnersRecordedIdentityRatherThanTheSendersProjection()
    {
        var preferences = new Preferences(Guid.NewGuid());
        var playback = CreatePlayback(preferences);
        var artistId = Guid.NewGuid();
        var recorded = Song() with { Title = "Recorded title", ArtistPersonId = artistId, AlbumWorkId = Guid.NewGuid() };
        playback.RestoreState(playback.CreateSnapshot() with { History = [recorded] });
        using var services = new ServiceCollection().AddSingleton<IUserPlaybackPreferencesAccessor>(preferences).BuildServiceProvider();
        var sink = new DirectPlaybackCommandSink(new ListenPlaybackCommandOwner(services, playback));
        var reply = await sink.SendAsync(playback.CreateSnapshot(), new() { Action = ListenPlaybackCommandActions.PlayHistory,
            QueueEntryId = recorded.QueueEntryId, QueueItem = new() { WorkId = recorded.WorkId, AssetId = recorded.AssetId, Title = "Untrusted title", MediaType = "Music" } });
        Assert.Equal(AudiobookBookmarkOperationOutcomes.Success, reply?.Outcome);
        Assert.Equal("Recorded title", playback.CurrentItem?.Title);
        Assert.Equal(artistId, playback.CurrentItem?.ArtistPersonId);
        Assert.Equal(recorded.AlbumWorkId, playback.CurrentItem?.AlbumWorkId);
    }

    [Fact]
    public async Task IdentityNavigationRecomputesTheAllowedRouteChecksAccessAndDeduplicatesReplies()
    {
        var preferences = new Preferences(Guid.NewGuid());
        var playback = CreatePlayback(preferences);
        var album = Guid.NewGuid();
        playback.RestoreState(playback.CreateSnapshot() with { Queue = [playback.CurrentItem! with { AlbumWorkId = album }] });
        var reads = 0;
        var api = EngineApiClientStub.Create(stub => stub.SetHandler(nameof(IEngineApiClient.GetDetailPageAsync), _ =>
        { reads++; return Task.FromResult<DetailPageViewModel?>(new() { Id = album.ToString("D"), EntityType = DetailEntityType.MusicAlbum }); }));
        using var services = new ServiceCollection().AddSingleton(new PlaybackIdentityNavigationOwner(playback, api, preferences)).BuildServiceProvider();
        var owner = new ListenPlaybackCommandOwner(services, playback);
        var navigations = new List<string>();
        owner.NavigateIdentityAsync = (_, route) => { navigations.Add(route); return Task.FromResult(true); };
        var command = Envelope(owner, playback, ListenPlaybackPresentationActions.NavigateIdentity) with { IdentityKind = "album", IdentityId = album };
        var results = await Task.WhenAll(owner.HandleAsync(command), owner.HandleAsync(command));
        Assert.True(results[0]?.BooleanResult);
        Assert.Same(results[0], results[1]);
        Assert.Single(navigations);
        Assert.Equal($"/details/musicalbum/{album:D}?context=listen", navigations[0]);
        Assert.Equal(1, reads);
        foreach (var invalid in new[] { command with { CommandId = Guid.NewGuid(), IdentityId = Guid.NewGuid() },
            command with { CommandId = Guid.NewGuid(), IdentityKind = "https://untrusted.example/" },
            command with { CommandId = Guid.NewGuid(), ExpectedPlaybackRequestVersion = 6 } })
            Assert.False((await owner.HandleAsync(invalid))?.BooleanResult);
        Assert.Equal(1, reads);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task IdentityAccessCompletionCannotNavigateAfterSessionOrProfileChanges(bool profileLoss)
    {
        var preferences = new Preferences(Guid.NewGuid());
        var playback = CreatePlayback(preferences);
        var album = Guid.NewGuid();
        playback.RestoreState(playback.CreateSnapshot() with { Queue = [playback.CurrentItem! with { AlbumWorkId = album }] });
        var pending = new TaskCompletionSource<DetailPageViewModel?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var api = EngineApiClientStub.Create(stub => stub.SetHandler(nameof(IEngineApiClient.GetDetailPageAsync), _ => pending.Task));
        using var services = new ServiceCollection().AddSingleton(new PlaybackIdentityNavigationOwner(playback, api, preferences)).BuildServiceProvider();
        var owner = new ListenPlaybackCommandOwner(services, playback);
        var navigated = false;
        owner.NavigateIdentityAsync = (_, _) => { navigated = true; return Task.FromResult(true); };
        var task = owner.HandleAsync(Envelope(owner, playback, ListenPlaybackPresentationActions.NavigateIdentity) with { IdentityKind = "album", IdentityId = album });
        if (profileLoss) preferences.ActiveProfileId = null;
        else playback.RestoreState(playback.CreateSnapshot() with { PlaybackRequestVersion = 8 });
        pending.SetResult(new() { Id = album.ToString("D"), EntityType = DetailEntityType.MusicAlbum });
        Assert.False((await task)?.BooleanResult);
        Assert.False(navigated);
    }

    [Fact]
    public async Task PassivePopupClosureRequiresCurrentWindowSenderAndGenerationAndNeverStopsAudio()
    {
        var preferences = new Preferences(Guid.NewGuid());
        var playback = CreatePlayback(preferences);
        using var services = new ServiceCollection().BuildServiceProvider();
        var owner = new ListenPlaybackCommandOwner(services, playback);
        var window = Guid.NewGuid();
        owner.ValidatePopupWindowAsync = id => Task.FromResult(id == window);
        var before = playback.CreateSnapshot();
        var transportCalls = 0;
        playback.TransportCommandRequested += _ => { transportCalls++; return Task.CompletedTask; };
        var registration = Envelope(owner, playback, ListenPlaybackPresentationActions.RegisterPopup) with { PopupWindowId = window, OwnerGeneration = 100 };
        Assert.True((await owner.HandleAsync(registration))?.BooleanResult);
        Assert.True(playback.IsPopupOpen);
        var next = registration with { CommandId = Guid.NewGuid(), SenderId = Guid.NewGuid(), OwnerGeneration = 101 };
        Assert.True((await owner.HandleAsync(next))?.BooleanResult);
        var obsoleteClose = registration with { CommandId = Guid.NewGuid(), Action = ListenPlaybackCommandActions.PopupClosed };
        Assert.False((await owner.HandleAsync(obsoleteClose))?.BooleanResult);
        Assert.True(playback.IsPopupOpen);
        Assert.False((await owner.HandleAsync(next with { CommandId = Guid.NewGuid(), PopupWindowId = Guid.NewGuid(), OwnerGeneration = 102 }))?.BooleanResult);
        var closed = next with { CommandId = Guid.NewGuid(), Action = ListenPlaybackCommandActions.PopupClosed };
        var reply = await owner.HandleAsync(closed);
        Assert.True(reply?.BooleanResult);
        Assert.Same(reply, await owner.HandleAsync(closed));
        Assert.False(playback.IsPopupOpen);
        Assert.Equal(before.Queue[0].AssetId, playback.CurrentItem?.AssetId);
        Assert.Equal(before.PlaybackRequestVersion, playback.PlaybackRequestVersion);
        Assert.False(playback.IsDismissed);
        Assert.Equal(0, transportCalls);
    }

    private static ListenQueueItem Song() => new() { WorkId = Guid.NewGuid(), AssetId = Guid.NewGuid(), MediaType = "Music", Title = "Song", StreamUrl = "stream://song" };
    private static PlaybackSessionController CreatePlayback(Preferences preferences)
    {
        var playback = new PlaybackSessionController(null!, null!, preferences: preferences);
        playback.RestoreState(new() { Queue = [Song()], CurrentIndex = 0, PlaybackRequestVersion = 7 });
        return playback;
    }
    private static ListenPlaybackCommandDto Envelope(ListenPlaybackCommandOwner owner, PlaybackSessionController playback, string action) => new()
    {
        CommandId = Guid.NewGuid(), SenderId = Guid.NewGuid(), RecipientId = owner.RecipientId,
        Action = action, ProfileId = playback.ActiveProfileId, WorkId = playback.CurrentItem!.WorkId,
        ExpectedAssetId = playback.CurrentItem.AssetId, ExpectedPlaybackRequestVersion = playback.PlaybackRequestVersion,
    };
    private sealed class Preferences(Guid profile) : IUserPlaybackPreferencesAccessor
    {
        public Guid? ActiveProfileId { get; set; } = profile;
        public Task<UserPlaybackSettingsDto?> GetAsync(CancellationToken ct = default) => Task.FromResult<UserPlaybackSettingsDto?>(null);
        public void UpdateCache(UserPlaybackSettingsDto settings) { }
        public void Invalidate() { }
    }
    private sealed class RecordingChannel : IListenPlaybackCommandChannel
    {
        public List<ListenPlaybackCommandDto> Commands { get; } = [];
        public Task<ListenPlaybackCommandReplyDto?> SendAsync(Guid ownerRecipientId, ListenPlaybackCommandDto command, CancellationToken ct = default)
        {
            Commands.Add(command);
            return Task.FromResult<ListenPlaybackCommandReplyDto?>(new() { CommandId = command.CommandId, RecipientId = command.SenderId });
        }
    }
}
