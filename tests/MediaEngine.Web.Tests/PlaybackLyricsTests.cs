using System.Text.Json;
using MediaEngine.Contracts.Playback;
using MediaEngine.Web.Services.Integration;
using MediaEngine.Web.Services.Playback;
using MediaEngine.Web.Tests.Support;
using Microsoft.Extensions.DependencyInjection;

namespace MediaEngine.Web.Tests;

public sealed class PlaybackLyricsTests
{
    [Theory]
    [InlineData("[offset:+250]",9.75)]
    [InlineData("[offset:-500]",10.5)]
    [InlineData("[offset:100]\n[offset:250]",9.75)]
    public void OffsetAppliesToEveryLeadingTimestampAndMetadataIsNotRendered(string metadata,double expected)
    {
        var lines=PlaybackLyricsParser.Parse($"{metadata}\n[00:10][00:12.50]Repeat\nPlain text");
        Assert.Equal(3,lines.Count); Assert.Equal(expected,lines[0].StartSeconds);
        Assert.Equal(expected+2.5,lines[1].StartSeconds); Assert.Null(lines[2].StartSeconds);
    }

    [Fact]
    public void ParserKeepsStaticTextAndOnlyValidLeadingTimestampsAreSeekable()
    {
        var lines = PlaybackLyricsParser.Parse("[ar:Artist]\n[00:12.5][01:02.025]Repeated line\n[00:99]Invalid seconds\nAn ordinary line\ntext [00:12] inline tag");
        Assert.Equal(5, lines.Count);
        Assert.Equal(12.5, lines[0].StartSeconds);
        Assert.Equal(62.025, lines[1].StartSeconds);
        Assert.Equal("Repeated line", lines[1].Text);
        Assert.All(lines.Skip(2), line => Assert.Null(line.StartSeconds));
        Assert.Equal("[00:99]Invalid seconds", lines[2].Text);
        Assert.Equal("An ordinary line", lines[3].Text);
    }

    [Fact]
    public async Task OldProfileContentCannotReplaceCurrentLyricsOrClearCurrentLoading()
    {
        var track = Guid.NewGuid();
        var first = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var second = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var api = Api(track, content: _ => ++calls == 1 ? first.Task : second.Task);
        using var presenter = new PlaybackLyricsPresenter(api);
        var snapshot = Snapshot();
        var sink = new Sink();
        var oldRead = presenter.EnsureAsync(snapshot, sink);
        Assert.True(presenter.Loading);
        var newRead = presenter.EnsureAsync(snapshot with { ProfileId = Guid.NewGuid() }, sink);
        first.SetResult("Old profile lyrics");
        await oldRead;
        Assert.True(presenter.Loading);
        Assert.Empty(presenter.Lines);
        second.SetResult("[00:01.2]Current profile lyrics");
        await newRead;
        Assert.False(presenter.Loading);
        Assert.Equal("Current profile lyrics", Assert.Single(presenter.Lines).Text);
    }

    [Fact]
    public async Task LostProfileAuthorityClearsVisibleVersionsAndRejectsOldTrackListReplies()
    {
        var pending = new TaskCompletionSource<IReadOnlyList<TextTrackDto>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var api = EngineApiClientStub.Create(stub => stub.SetHandler(nameof(IEngineApiClient.GetTextTracksAsync), _ => pending.Task));
        using var presenter = new PlaybackLyricsPresenter(api);
        var snapshot = Snapshot(); var sink = new Sink();
        var read = presenter.EnsureAsync(snapshot, sink);
        presenter.Observe(snapshot with { ProfileId = null }, sink);
        pending.SetResult([new() { Id = Guid.NewGuid(), Kind = "Lyrics" }]);
        await read;
        Assert.Empty(presenter.Tracks); Assert.Empty(presenter.Lines);
        Assert.False(presenter.Loading); Assert.Null(presenter.SelectedTrackId);
    }

    [Fact]
    public async Task ExplicitNonPreferredVersionSurvivesRefreshAndPreferenceWritesStaySeparate()
    {
        var preferred = Guid.NewGuid(); var alternate = Guid.NewGuid(); var saved = new List<Guid>();
        IReadOnlyList<TextTrackDto> tracks = [new() { Id = preferred, Kind = "Lyrics", IsPreferred = true }, new() { Id = alternate, Kind = "Lyrics" }];
        var api = EngineApiClientStub.Create(stub =>
        {
            stub.SetHandler(nameof(IEngineApiClient.GetTextTracksAsync), _ => Task.FromResult(tracks));
            stub.SetHandler(nameof(IEngineApiClient.GetTextTrackContentAsync), args => Task.FromResult<string?>(args![1]!.ToString()));
            stub.SetHandler(nameof(IEngineApiClient.SetPreferredTextTrackAsync), args => { saved.Add((Guid)args![1]!); return Task.FromResult(true); });
            stub.SetHandler(nameof(IEngineApiClient.RefreshTextTracksAsync), _ => Task.FromResult<RefreshTextTracksResponse?>(new() { message = "Search finished." }));
        });
        using var presenter = new PlaybackLyricsPresenter(api);
        var sink = new Sink();
        await presenter.EnsureAsync(Snapshot(), sink);
        await presenter.SelectAsync(alternate);
        Assert.Empty(saved);
        await presenter.RefreshAsync();
        Assert.Equal(alternate, presenter.SelectedTrackId);
        Assert.Equal(alternate.ToString(), Assert.Single(presenter.Lines).Text);
        await presenter.PreferAsync();
        Assert.Equal(alternate, Assert.Single(saved));
        Assert.Equal(alternate, presenter.SelectedTrackId);
    }

    [Fact]
    public async Task LateSelectedVersionCannotReplaceNewChoice()
    {
        var a = Guid.NewGuid(); var b = Guid.NewGuid();
        var old = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        IReadOnlyList<TextTrackDto> tracks = [new() { Id = a, Kind = "Lyrics", IsPreferred = true }, new() { Id = b, Kind = "Lyrics" }];
        var api = EngineApiClientStub.Create(stub =>
        {
            stub.SetHandler(nameof(IEngineApiClient.GetTextTracksAsync), _ => Task.FromResult(tracks));
            stub.SetHandler(nameof(IEngineApiClient.GetTextTrackContentAsync), args => (Guid)args![1]! == a ? old.Task : Task.FromResult<string?>("Current selected lyrics"));
        });
        using var presenter = new PlaybackLyricsPresenter(api);
        var oldRead = presenter.EnsureAsync(Snapshot(), new Sink());
        await presenter.SelectAsync(b);
        old.SetResult("Old selected lyrics"); await oldRead;
        Assert.Equal(b, presenter.SelectedTrackId);
        Assert.False(presenter.Loading);
        Assert.Equal("Current selected lyrics", Assert.Single(presenter.Lines).Text);
    }

    [Fact]
    public async Task OwnerRechecksSessionAfterAllowedTrackReadAndDeduplicatesSelection()
    {
        var preferences = new Preferences(Guid.NewGuid());
        var playback = new PlaybackSessionController(null!, null!, preferences: preferences);
        playback.RestoreState(Snapshot());
        var track = Guid.NewGuid(); var reads = 0;
        var pending = new TaskCompletionSource<IReadOnlyList<TextTrackDto>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var api = EngineApiClientStub.Create(stub => stub.SetHandler(nameof(IEngineApiClient.GetTextTracksAsync), _ => { reads++; return pending.Task; }));
        using var services = new ServiceCollection().AddSingleton(new PlaybackLyricsSelectionOwner(playback, api)).BuildServiceProvider();
        var owner = new ListenPlaybackCommandOwner(services, playback);
        var snapshot = playback.CreateSnapshot();
        var command = new ListenPlaybackCommandDto { Action = ListenPlaybackPresentationActions.SelectLyrics,
            CommandId = Guid.NewGuid(), SenderId = Guid.NewGuid(), RecipientId = owner.RecipientId,
            ProfileId = snapshot.ProfileId, WorkId = snapshot.Queue[0].WorkId, ExpectedAssetId = snapshot.Queue[0].AssetId,
            ExpectedPlaybackRequestVersion = snapshot.PlaybackRequestVersion, LyricTrackId = track };
        var first = owner.HandleAsync(command); var duplicate = owner.HandleAsync(command);
        preferences.ActiveProfileId = Guid.NewGuid();
        pending.SetResult([new() { Id = track, Kind = "Lyrics" }]);
        Assert.False((await first)?.BooleanResult);
        Assert.Same(await first, await duplicate);
        Assert.Equal(1, reads);
        Assert.Null(playback.CreateSnapshot().LyricsSelection);
    }

    [Fact]
    public async Task DirectAndPopupChoiceUseSameOwnerProjectionAndRejectUnauthorizedIds()
    {
        var preferences = new Preferences(Guid.NewGuid());
        var playback = new PlaybackSessionController(null!, null!, preferences: preferences);
        playback.RestoreState(Snapshot()); var track = Guid.NewGuid();
        using var services = new ServiceCollection().AddSingleton(new PlaybackLyricsSelectionOwner(playback, Api(track))).BuildServiceProvider();
        var owner = new ListenPlaybackCommandOwner(services, playback);
        var direct = new DirectPlaybackCommandSink(owner);
        var remote = new BroadcastPlaybackCommandSink(owner.RecipientId, Guid.NewGuid(), new OwnerPlaybackCommandChannel(owner));
        var transportCalls = 0; playback.TransportCommandRequested += _ => { transportCalls++; return Task.CompletedTask; };
        foreach (var sink in new IPlaybackLyricsSelectionSink[] { direct, remote })
        {
            Assert.True((await sink.SelectLyricsAsync(playback.CreateSnapshot(), track))?.BooleanResult);
            Assert.Equal(new PlaybackLyricsSelectionProjection(owner.RecipientId, PlaybackLyricsIdentity.From(playback.CreateSnapshot())!, track), playback.CreateSnapshot().LyricsSelection);
        }
        var snapshot = playback.CreateSnapshot();
        var json = JsonSerializer.Serialize(snapshot, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var roundTrip = JsonSerializer.Deserialize<ListenPlaybackSnapshot>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.Contains("\"lyrics_selection\"", json);
        Assert.Equal(snapshot.LyricsSelection, roundTrip!.LyricsSelection);
        using var wire = JsonDocument.Parse(json);
        var projection = wire.RootElement.GetProperty("lyrics_selection");
        Assert.Equal(owner.RecipientId, projection.GetProperty("ownerId").GetGuid());
        Assert.Equal(snapshot.ProfileId, projection.GetProperty("identity").GetProperty("profileId").GetGuid());
        Assert.Equal(snapshot.PlaybackRequestVersion, projection.GetProperty("identity").GetProperty("requestVersion").GetInt64());
        Assert.Equal(track, projection.GetProperty("trackId").GetGuid());
        Assert.False(direct.Supports(ListenPlaybackPresentationActions.SelectLyrics, snapshot));
        Assert.False((await remote.SelectLyricsAsync(snapshot, Guid.NewGuid()))?.BooleanResult);
        Assert.Null(playback.CreateSnapshot().LyricsSelection);
        Assert.Equal(0, transportCalls);
    }

    private static IEngineApiClient Api(Guid track, Func<object?[]?, Task<string?>>? content = null) => EngineApiClientStub.Create(stub =>
    {
        stub.SetHandler(nameof(IEngineApiClient.GetTextTracksAsync), _ => Task.FromResult<IReadOnlyList<TextTrackDto>>([new() { Id = track, Kind = "Lyrics", IsPreferred = true }]));
        stub.SetHandler(nameof(IEngineApiClient.GetTextTrackContentAsync), args => content?.Invoke(args) ?? Task.FromResult<string?>("Static lyrics"));
    });
    private static ListenPlaybackSnapshot Snapshot() => new() { ProfileId = Guid.NewGuid(), CurrentIndex = 0, PlaybackRequestVersion = 9,
        Queue = [new() { WorkId = Guid.NewGuid(), AssetId = Guid.NewGuid(), MediaType = "Music", Title = "Song", StreamUrl = "stream://song" }] };
    private sealed class Sink : IPlaybackCommandSink, IPlaybackLyricsSelectionSink
    {
        public bool Supports(string action, ListenPlaybackSnapshot snapshot) => true;
        public Task<ListenPlaybackCommandReplyDto?> SendAsync(ListenPlaybackSnapshot snapshot, ListenPlaybackCommandDto command, CancellationToken ct = default) => Task.FromResult<ListenPlaybackCommandReplyDto?>(new());
        public Task<ListenPlaybackCommandReplyDto?> SelectLyricsAsync(ListenPlaybackSnapshot snapshot, Guid trackId, CancellationToken ct = default) => Task.FromResult<ListenPlaybackCommandReplyDto?>(new() { BooleanResult = true });
    }
    private sealed class Preferences(Guid profile) : IUserPlaybackPreferencesAccessor
    {
        public Guid? ActiveProfileId { get; set; } = profile;
        public Task<UserPlaybackSettingsDto?> GetAsync(CancellationToken ct = default) => Task.FromResult<UserPlaybackSettingsDto?>(null);
        public void UpdateCache(UserPlaybackSettingsDto settings) { }
        public void Invalidate() { }
    }
}
