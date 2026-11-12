using Bunit;
using MediaEngine.Contracts.Playback;
using MediaEngine.Web.Components.Listen;
using MediaEngine.Web.Components.Player;
using MediaEngine.Web.Components.Shared;
using MediaEngine.Web.Services.Integration;
using MediaEngine.Web.Services.Playback;
using MediaEngine.Web.Tests.Support;
using Microsoft.Extensions.DependencyInjection;

namespace MediaEngine.Web.Tests;

/// <summary>
/// The Lyrics panel after "Find lyrics" (spec music-player-and-listen-fixes D3): "Looking for lyrics…" until the queued lookup has finished, then the
/// lyrics, or "No lyrics found" with both buttons, or the "could not be looked up right now" line with Try again. The empty state is never drawn while
/// the lookup is still running. The pauses between looks are held by a spy so nothing sleeps.
/// </summary>
public sealed class PlaybackLyricsLookingPanelTests : AsyncBunitContext
{
    private readonly Guid _track = Guid.NewGuid();
    private readonly Guid _operation = Guid.NewGuid();
    private readonly Queue<string> _statuses = new();
    private readonly LyricsWaitSpy _wait = new();
    private IReadOnlyList<TextTrackDto> _tracks = [];
    private int _trackReads, _statusReads;

    public PlaybackLyricsLookingPanelTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        JSInterop.SetupModule("./js/playback-lyrics.js");
        JSInterop.SetupModule("./js/playback-popover.js");
        Services.AddLogging();
        Services.AddLocalization();
        Services.AddNativeUiServices();
    }

    private async Task<IRenderedComponent<PlaybackLyricsPanel>> OpenAsync()
    {
        var api = EngineApiClientStub.Create(stub =>
        {
            stub.SetHandler(nameof(IEngineApiClient.GetTextTracksAsync), _ => { _trackReads++; return Task.FromResult(_tracks); });
            stub.SetHandler(nameof(IEngineApiClient.GetTextTrackContentAsync), _ => Task.FromResult<string?>("[00:01.00]The city settles into blue"));
            stub.SetHandler(nameof(IEngineApiClient.GetPlayerPluginActionsAsync), _ =>
                Task.FromResult<IReadOnlyList<PlayerPluginActionDto>>([new() { kind = "find_lyrics", slot = "player_core" }]));
            stub.SetHandler(nameof(IEngineApiClient.QueueTextTrackRefreshAsync), _ =>
                Task.FromResult<RefreshTextTracksResponse?>(new() { operation_id = _operation, status = "queued" }));
            stub.SetHandler(nameof(IEngineApiClient.GetTextTrackRefreshStatusAsync), _ =>
            {
                _statusReads++;
                var status = _statuses.Count > 1 ? _statuses.Dequeue() : _statuses.Peek();
                return Task.FromResult<RefreshTextTracksResponse?>(new() { operation_id = _operation, status = status });
            });
        });
        Services.AddSingleton(new PlaybackTransientToolCoordinator());
        Services.AddSingleton(new PlaybackSessionController(null!, null!));
        Services.AddSingleton(new PlaybackLyricsPresenter(api, null, _wait.Wait));
        Render<AppPopoverHost>();
        var cut = Render<PlaybackLyricsPanel>(parameters => parameters
            .Add(p => p.Snapshot, new ListenPlaybackSnapshot
            {
                ProfileId = Guid.NewGuid(),
                CurrentIndex = 0,
                PlaybackRequestVersion = 9,
                DurationSeconds = 60,
                Queue = [new() { WorkId = Guid.NewGuid(), AssetId = Guid.NewGuid(), MediaType = "Music", Title = "Stay a Little Longer", StreamUrl = "stream://song" }],
            })
            .Add(p => p.Commands, new Sink()));
        await cut.InvokeAsync(() => cut.Instance.OpenAsync());
        cut.WaitForElement("[data-playback-lyrics]");
        cut.WaitForAssertion(() => Assert.Equal("unavailable", View(cut)));
        return cut;
    }

    private static string View(IRenderedComponent<PlaybackLyricsPanel> cut) => cut.Find("[data-playback-lyrics]").GetAttribute("data-lyrics-view")!;

    private static Task ClickAsync(IRenderedComponent<PlaybackLyricsPanel> cut, string label) =>
        cut.InvokeAsync(() => cut.FindAll("button").First(button => button.TextContent.Trim() == label).ClickAsync(new()));

    private async Task ReleaseAndWaitForStatusAsync(int reads)
    {
        await _wait.ReleaseNextAsync();
        for (var attempt = 0; attempt < 500 && _statusReads < reads; attempt++)
        {
            await Task.Delay(10);
        }
        Assert.Equal(reads, _statusReads);
    }

    [Fact]
    public async Task WhileTheQueuedLookupIsQueuedOrRunning_ThePanelSaysLookingAndNeverNoLyricsFound()
    {
        _statuses.Enqueue("queued"); _statuses.Enqueue("running"); _statuses.Enqueue("succeeded");
        var cut = await OpenAsync();

        var find = ClickAsync(cut, "Find lyrics");
        cut.WaitForAssertion(() => Assert.Equal("loading", View(cut)));
        Assert.Equal("Looking for lyrics…", cut.Find("[data-lyrics-state=loading]").TextContent.Trim());

        await ReleaseAndWaitForStatusAsync(1);
        Assert.Equal("loading", View(cut));
        Assert.DoesNotContain("No lyrics found", cut.Markup);
        Assert.Empty(cut.FindAll("[data-lyrics-state=unavailable]"));

        await ReleaseAndWaitForStatusAsync(2);
        Assert.Equal("loading", View(cut));
        Assert.DoesNotContain("No lyrics found", cut.Markup);
        Assert.Equal(1, _trackReads);

        _tracks = [new() { Id = _track, Kind = "Lyrics", Provider = "lrclib", IsPreferred = true }];
        await _wait.ReleaseNextAsync();
        await find;

        cut.WaitForAssertion(() => Assert.Equal("synced", View(cut)));
        Assert.Equal(2, _trackReads);
        Assert.Equal("The city settles into blue", cut.Find("button.playback-lyrics__line").TextContent.Trim());
        Assert.DoesNotContain("No lyrics found", cut.Markup);
    }

    [Fact]
    public async Task WhenTheLookupFindsNothing_ThePanelSaysNoLyricsFoundWithFindLyricsAndAddALyricsFile()
    {
        _statuses.Enqueue("no_result");
        var cut = await OpenAsync();

        var find = ClickAsync(cut, "Find lyrics");
        await _wait.ReleaseNextAsync();
        await find;

        cut.WaitForAssertion(() => Assert.Equal("unavailable", View(cut)));
        Assert.Equal("No lyrics found", cut.Find("[data-lyrics-state=unavailable] .playback-lyrics__message").TextContent.Trim());
        Assert.Equal(["Find lyrics", "Add a lyrics file"], cut.FindAll("[data-lyrics-state=unavailable] button").Select(button => button.TextContent.Trim()));
        Assert.Empty(cut.FindAll("[data-lyrics-state=unavailable] button[disabled]"));
        Assert.Equal(2, _trackReads);
    }

    [Fact]
    public async Task WhenTheLookupCouldNotBeMade_ThePanelSaysSoWithTryAgain_AndTryAgainLooksAgain()
    {
        _statuses.Enqueue("failed_retryable");
        var cut = await OpenAsync();

        var find = ClickAsync(cut, "Find lyrics");
        await _wait.ReleaseNextAsync();
        await find;

        cut.WaitForAssertion(() => Assert.Equal("unreachable", View(cut)));
        Assert.Equal("Lyrics could not be looked up right now", cut.Find("[data-lyrics-state=unreachable] .playback-lyrics__message").TextContent.Trim());
        Assert.Equal(["Try again"], cut.FindAll("[data-lyrics-state=unreachable] button").Select(button => button.TextContent.Trim()));
        Assert.Equal(1, _trackReads);

        _statuses.Clear(); _statuses.Enqueue("no_result");
        var again = ClickAsync(cut, "Try again");
        cut.WaitForAssertion(() => Assert.Equal("loading", View(cut)));
        await _wait.ReleaseNextAsync();
        await again;

        cut.WaitForAssertion(() => Assert.Equal("unavailable", View(cut)));
        Assert.Equal("No lyrics found", cut.Find("[data-lyrics-state=unavailable] .playback-lyrics__message").TextContent.Trim());
    }

    [Fact]
    public async Task WhenTheLookupIsStillGoingAfterThirtySeconds_ThePanelSaysStillLookingAndFindLyricsWaits()
    {
        _statuses.Enqueue("running");
        var cut = await OpenAsync();

        var find = ClickAsync(cut, "Find lyrics");
        for (var look = 1; look <= 16; look++)
        {
            await ReleaseAndWaitForStatusAsync(look);
        }

        cut.WaitForAssertion(() => Assert.Equal("Still looking. Lyrics will appear when they are found.",
            cut.Find("[data-lyrics-state=unavailable] .playback-lyrics__message").TextContent.Trim()));
        Assert.Contains(cut.FindAll("[data-lyrics-state=unavailable] button"), button => button.TextContent.Trim() == "Find lyrics" && button.HasAttribute("disabled"));
        Assert.DoesNotContain("No lyrics found", cut.Markup);

        _tracks = [new() { Id = _track, Kind = "Lyrics", Provider = "lrclib", IsPreferred = true }];
        await _wait.ReleaseNextAsync();
        await find;
        cut.WaitForAssertion(() => Assert.Equal("synced", View(cut)));
    }

    private sealed class Sink : IPlaybackCommandSink
    {
        public bool Supports(string action, ListenPlaybackSnapshot snapshot) => true;
        public Task<ListenPlaybackCommandReplyDto?> SendAsync(ListenPlaybackSnapshot snapshot, ListenPlaybackCommandDto command, CancellationToken ct = default) =>
            Task.FromResult<ListenPlaybackCommandReplyDto?>(new());
    }
}
