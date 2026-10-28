using MediaEngine.Contracts.Playback;
using MediaEngine.Web.Services.Integration;
using MediaEngine.Web.Services.Playback;
using MediaEngine.Web.Tests.Support;

namespace MediaEngine.Web.Tests;

public sealed class PlaybackLyricsLookupTests
{
    [Fact]
    public async Task WithNoLyricsAndNoSourceOn_ThePanelLearnsItShouldSayNoSourceIsOn()
    {
        var api = EngineApiClientStub.Create(stub =>
        {
            stub.SetHandler(nameof(IEngineApiClient.GetTextTracksAsync), _ => Task.FromResult<IReadOnlyList<TextTrackDto>>([]));
            stub.SetHandler(nameof(IEngineApiClient.GetTextTrackAlternativesAsync), _ => Task.FromResult<TextTrackAlternativesResponse?>(new() { status = "NoSourceOn" }));
        });
        using var presenter = new PlaybackLyricsPresenter(api);
        await presenter.EnsureAsync(Snapshot(), new Sink());
        await presenter.ProbeSourcesAsync();

        Assert.True(presenter.NoSourceOn);
        Assert.Empty(presenter.Lines);
    }

    [Fact]
    public async Task WithLyricsShowing_TheSourceCheckIsNeverMade()
    {
        var api = Api(Guid.NewGuid());
        using var presenter = new PlaybackLyricsPresenter(api);
        await presenter.EnsureAsync(Snapshot(), new Sink());
        await presenter.ProbeSourcesAsync();

        Assert.False(presenter.NoSourceOn);
        Assert.DoesNotContain(nameof(IEngineApiClient.GetTextTrackAlternativesAsync), ((EngineApiClientStub)(object)api).Calls);
    }

    [Fact]
    public async Task FindAnother_ListsWhatTheSourcesOffer_AndGoBackClosesIt()
    {
        var api = Api(Guid.NewGuid(), alternatives: [new() { provider = "lrclib", provider_name = "LRCLIB", source_id = "7", is_timed = true }]);
        using var presenter = new PlaybackLyricsPresenter(api);
        await presenter.EnsureAsync(Snapshot(), new Sink());

        await presenter.OpenAlternativesAsync();
        Assert.True(presenter.AlternativesOpen);
        Assert.Equal("7", Assert.Single(presenter.Alternatives!.items).source_id);

        presenter.CloseAlternatives();
        Assert.False(presenter.AlternativesOpen);
        Assert.Null(presenter.Alternatives);
    }

    [Fact]
    public async Task ChoosingAnotherResult_SendsIt_ReloadsTheLyrics_AndShowsTheEnginesMessage()
    {
        ChooseTextTrackAlternativeRequest? sent = null;
        var reads = 0;
        var api = EngineApiClientStub.Create(stub =>
        {
            stub.SetHandler(nameof(IEngineApiClient.GetTextTracksAsync), _ => { reads++; return Task.FromResult<IReadOnlyList<TextTrackDto>>([new() { Id = Guid.NewGuid(), Kind = "Lyrics", IsPreferred = true }]); });
            stub.SetHandler(nameof(IEngineApiClient.GetTextTrackContentAsync), _ => Task.FromResult<string?>(reads > 1 ? "New words" : "Old words"));
            stub.SetHandler(nameof(IEngineApiClient.ChooseTextTrackAlternativeAsync), args =>
            {
                sent = (ChooseTextTrackAlternativeRequest)args![1]!;
                return Task.FromResult<TextTrackChoiceResponse?>(new() { status = "Chosen", message = "Lyrics changed." });
            });
        });
        using var presenter = new PlaybackLyricsPresenter(api);
        await presenter.EnsureAsync(Snapshot(), new Sink());
        await presenter.OpenAlternativesAsync();

        await presenter.ChooseAsync(new() { provider = "lrclib", source_id = "7" });

        Assert.Equal(("lyrics", "lrclib", "7"), (sent!.kind, sent.provider, sent.source_id));
        Assert.Equal("New words", Assert.Single(presenter.Lines).Text);
        Assert.Equal("Lyrics changed.", presenter.Status);
        Assert.False(presenter.AlternativesOpen);
    }

    [Fact]
    public async Task ChoosingTheResultAlreadyInUse_DoesNothing()
    {
        var api = Api(Guid.NewGuid());
        using var presenter = new PlaybackLyricsPresenter(api);
        await presenter.EnsureAsync(Snapshot(), new Sink());

        await presenter.ChooseAsync(new() { provider = "lrclib", source_id = "7", is_current = true });

        Assert.DoesNotContain(nameof(IEngineApiClient.ChooseTextTrackAlternativeAsync), ((EngineApiClientStub)(object)api).Calls);
    }

    [Fact]
    public async Task ThisOneIsWrong_MarksTheShowingTrack_AndReportsAFailureInPlainWords()
    {
        var track = Guid.NewGuid(); Guid? marked = null;
        var api = EngineApiClientStub.Create(stub =>
        {
            stub.SetHandler(nameof(IEngineApiClient.GetTextTracksAsync), _ => Task.FromResult<IReadOnlyList<TextTrackDto>>([new() { Id = track, Kind = "Lyrics", IsPreferred = true }]));
            stub.SetHandler(nameof(IEngineApiClient.GetTextTrackContentAsync), _ => Task.FromResult<string?>("Words"));
            stub.SetHandler(nameof(IEngineApiClient.MarkTextTrackWrongAsync), args => { marked = (Guid)args![1]!; return Task.FromResult<TextTrackChoiceResponse?>(null); });
        });
        using var presenter = new PlaybackLyricsPresenter(api);
        await presenter.EnsureAsync(Snapshot(), new Sink());

        await presenter.MarkWrongAsync();

        Assert.Equal(track, marked);
        Assert.Equal("Could not mark those lyrics as wrong.", presenter.Status);
    }

    [Fact]
    public async Task ThePlayersActions_AreLoadedOnce_AndOffersFindsTheKind()
    {
        var calls = 0;
        var api = EngineApiClientStub.Create(stub =>
        {
            stub.SetHandler(nameof(IEngineApiClient.GetTextTracksAsync), _ => Task.FromResult<IReadOnlyList<TextTrackDto>>([]));
            stub.SetHandler(nameof(IEngineApiClient.GetPlayerPluginActionsAsync), _ =>
            {
                calls++;
                return Task.FromResult<IReadOnlyList<PlayerPluginActionDto>>([new() { kind = "find_lyrics", slot = "player_core" }]);
            });
        });
        using var presenter = new PlaybackLyricsPresenter(api);
        await presenter.EnsureAsync(Snapshot(), new Sink());

        await presenter.EnsureActionsAsync();
        await presenter.EnsureActionsAsync();

        Assert.Equal(1, calls);
        Assert.True(presenter.Offers("find_lyrics"));
        Assert.False(presenter.Offers("rescan_skip_markers"));
    }

    private static IEngineApiClient Api(Guid track, IReadOnlyList<TextTrackAlternativeDto>? alternatives = null) => EngineApiClientStub.Create(stub =>
    {
        stub.SetHandler(nameof(IEngineApiClient.GetTextTracksAsync), _ => Task.FromResult<IReadOnlyList<TextTrackDto>>([new() { Id = track, Kind = "Lyrics", IsPreferred = true }]));
        stub.SetHandler(nameof(IEngineApiClient.GetTextTrackContentAsync), _ => Task.FromResult<string?>("Static lyrics"));
        stub.SetHandler(nameof(IEngineApiClient.GetTextTrackAlternativesAsync), _ =>
            Task.FromResult<TextTrackAlternativesResponse?>(new() { status = "Found", items = alternatives ?? [] }));
    });

    private static ListenPlaybackSnapshot Snapshot() => new()
    {
        ProfileId = Guid.NewGuid(),
        CurrentIndex = 0,
        PlaybackRequestVersion = 9,
        Queue = [new() { WorkId = Guid.NewGuid(), AssetId = Guid.NewGuid(), MediaType = "Music", Title = "Song", StreamUrl = "stream://song" }]
    };

    private sealed class Sink : IPlaybackCommandSink
    {
        public bool Supports(string action, ListenPlaybackSnapshot snapshot) => true;
        public Task<ListenPlaybackCommandReplyDto?> SendAsync(ListenPlaybackSnapshot snapshot, ListenPlaybackCommandDto command, CancellationToken ct = default) => Task.FromResult<ListenPlaybackCommandReplyDto?>(new());
    }
}
