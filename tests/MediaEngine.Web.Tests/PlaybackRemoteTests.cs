using Bunit;
using MediaEngine.Contracts.Playback;
using MediaEngine.Web.Components.Shared;
using MediaEngine.Web.Services.Integration;
using MediaEngine.Web.Services.Playback;
using MediaEngine.Web.Tests.Support;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MediaEngine.Web.Tests;

/// <summary>
/// Play on another device (unified player v2 D28; acceptance A18): where the row is, why it is disabled, what the bar says while a receiver plays,
/// and that the Engine's cast link is asked for before the browser's picker opens. The script's own decisions are in playback-remote.test.mjs.
/// </summary>
public sealed class PlaybackRemoteTests : AsyncBunitContext
{
    private sealed class TestNavigation : NavigationManager
    {
        public TestNavigation(string uri) => Initialize(uri, uri);

        protected override void NavigateToCore(string uri, NavigationOptions options) { }
    }

    private sealed class LogCapture : ILoggerProvider
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public ILogger CreateLogger(string categoryName) => new Capture(Entries);

        public void Dispose() { }

        private sealed class Capture(List<(LogLevel, string)> entries) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
                entries.Add((logLevel, formatter(state, exception)));
        }
    }

    private static readonly CastSessionAttempt Refused = new(null, 403, "Forbidden");
    private static readonly CastSessionAttempt Ready = new(new CastSessionDto
    {
        Status = CastSessionStatuses.Ready,
        MediaPath = "/cast/v1/grant/media",
        ContentType = "video/mp4",
    }, 200, null);

    private readonly LogCapture _log = new();
    private readonly PlaybackTransientToolCoordinator _tools = new();
    private BunitJSModuleInterop _module = null!;
    private EngineApiClientStub _stub = null!;
    private PlaybackRemoteController _remote = null!;
    private PlaybackSessionController _playback = null!;

    public PlaybackRemoteTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        _loggers = LoggerFactory.Create(builder => builder.AddProvider(_log).SetMinimumLevel(LogLevel.Debug));
        Services.AddLogging();
        Services.AddSingleton(_tools);
    }

    private readonly ILoggerFactory _loggers;

    private static readonly Guid Asset = Guid.NewGuid();

    private static ListenQueueItem Item(string mediaType, bool vault = false, Guid? asset = null)
    {
        var id = asset ?? Asset;
        return new()
        {
            WorkId = Guid.NewGuid(),
            AssetId = id,
            MediaType = mediaType,
            Title = "The Glass Harbor",
            Subtitle = "A Band",
            Year = "2026",
            Manifest = new() { AssetId = id, IsVault = vault, MediaType = mediaType },
        };
    }

    // The script is replaced by a recorded module: support and the prompt's answer are what the test says they are.
    private PlaybackRemoteController Arrange(string support = "supported", CastSessionAttempt? attempt = null, string baseUri = "http://192.168.1.5:5016/",
        string promptOutcome = "prompted", bool probe = true)
    {
        var api = EngineApiClientStub.Create(stub => stub.SetHandler(nameof(IEngineApiClient.CreateCastSessionAsync), _ => Task.FromResult(attempt ?? Refused)));
        _stub = (EngineApiClientStub)(object)api;
        _module = JSInterop.SetupModule(PlaybackRemoteController.ModulePath);
        _module.Mode = JSRuntimeMode.Loose;
        _module.Setup<PlaybackRemoteDescription?>("attachTo", _ => true).SetResult(new(support, "unknown", "local", null));
        _module.Setup<PlaybackRemotePromptResult>("openPickerOn", _ => true).SetResult(new(promptOutcome));
        _module.Setup<bool>("stopOn", _ => true).SetResult(true);
        _module.Setup<bool>("followOn", _ => true).SetResult(true);
        _playback = new PlaybackSessionController(null!, api);
        var links = new PlaybackCastLinkService(api, new TestNavigation(baseUri), _loggers.CreateLogger<PlaybackCastLinkService>());
        _remote = new PlaybackRemoteController(JSInterop.JSRuntime, links, _playback, _loggers.CreateLogger<PlaybackRemoteController>());
        Services.AddSingleton(_remote);
        Render<AppPopoverHost>();
        return _remote;
    }

    private async Task<IRenderedComponent<PlaybackItemMenu>> OpenMenuAsync(PlaybackBarMedia media, ListenQueueItem? item)
    {
        var cut = Render<PlaybackItemMenu>(p => p.Add(m => m.Media, media).Add(m => m.Item, item));
        await cut.InvokeAsync(() => cut.FindComponent<PlaybackPanel>().Instance.OpenAsync(true));
        cut.WaitForElement("[data-playback-item-menu]");
        return cut;
    }

    private async Task<IRenderedComponent<PlaybackSettingsPanel>> OpenSettingsAsync(PlaybackBarMedia media, PlaybackManifestDto? manifest)
    {
        var cut = Render<PlaybackSettingsPanel>(p => p.Add(m => m.Media, media).Add(m => m.Manifest, manifest));
        await cut.InvokeAsync(() => cut.FindComponent<PlaybackPanel>().Instance.OpenAsync(true));
        cut.WaitForElement("[data-playback-settings]");
        return cut;
    }

    private List<JSRuntimeInvocation> Called(string identifier) => _module.Invocations.Where(call => call.Identifier == identifier).ToList();

    private static AngleSharp.Dom.IElement MenuRow(IRenderedComponent<PlaybackItemMenu> cut) => cut.Find("[data-playback-item-action='play-on-device']");

    private static AngleSharp.Dom.IElement SettingsRow(IRenderedComponent<PlaybackSettingsPanel> cut) => cut.Find("[data-psp-row='play-on-device']");

    // ---- Where the row is (A18) ----

    [Theory]
    [InlineData(PlaybackBarMedia.Music)]
    [InlineData(PlaybackBarMedia.Audiobook)]
    public async Task PlayOnAnotherDevice_IsInTheItemMenuForMusicAndAudiobooks(PlaybackBarMedia media)
    {
        Arrange();
        var cut = await OpenMenuAsync(media, Item(media.ToString()));

        var row = MenuRow(cut);
        Assert.Contains("Play on another device…", row.TextContent);
        Assert.False(row.HasAttribute("disabled"));
        Assert.NotNull(row.QuerySelector("svg"));
    }

    [Theory]
    [InlineData(PlaybackBarMedia.Tv)]
    [InlineData(PlaybackBarMedia.Movie)]
    public async Task PlayOnAnotherDevice_IsInPlaybackSettingsForTvAndMovies(PlaybackBarMedia media)
    {
        Arrange();
        var cut = await OpenSettingsAsync(media, new() { AssetId = Asset, MediaType = media.ToString() });

        var row = SettingsRow(cut);
        Assert.Contains("Play on another device…", row.TextContent);
        Assert.False(row.HasAttribute("disabled"));
    }

    [Theory]
    [InlineData(PlaybackBarMedia.Music)]
    [InlineData(PlaybackBarMedia.Audiobook)]
    public async Task PlaybackSettingsForMusicAndAudiobooks_HaveNoPlayOnAnotherDeviceRow(PlaybackBarMedia media)
    {
        Arrange();
        var cut = await OpenSettingsAsync(media, new() { AssetId = Asset });

        Assert.Empty(cut.FindAll("[data-psp-row='play-on-device']"));
    }

    [Theory]
    [InlineData(PlaybackBarMedia.Tv)]
    [InlineData(PlaybackBarMedia.Movie)]
    public async Task TheItemMenuForVideo_HasNoPlayOnAnotherDeviceRow(PlaybackBarMedia media)
    {
        Arrange();
        var cut = await OpenMenuAsync(media, Item(media == PlaybackBarMedia.Tv ? "TV" : "Movie"));

        Assert.Empty(cut.FindAll("[data-playback-item-action='play-on-device']"));
    }

    [Theory]
    [InlineData(PlaybackBarMedia.Music)]
    [InlineData(PlaybackBarMedia.Audiobook)]
    [InlineData(PlaybackBarMedia.Tv)]
    [InlineData(PlaybackBarMedia.Movie)]
    public void PlayOnAnotherDevice_IsNeverAToolOnTheBar(PlaybackBarMedia media)
    {
        var remote = Arrange();
        remote.OnRemoteStateChanged("remote", "Living room TV");

        var cut = Render<PlaybackShell>(p => p.Add(s => s.Media, media).Add(s => s.State, new PlaybackBarState(CanPrevious: true, CanNext: true)));

        Assert.DoesNotContain("another device", cut.Find("[data-playback-zone='tools']").InnerHtml, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(cut.FindAll("[data-playback-zone='tools'] [data-playback-item-action='play-on-device']"));
        Assert.DoesNotContain(PlaybackControlCatalog.BuildBarTools(media, new PlaybackBarState()), control => control.Key == PlaybackControlKey.Cast);
    }

    // ---- Why the row is disabled ----

    [Fact]
    public async Task TheRow_IsDisabledWithNotAvailableInThisBrowser_WhenTheBrowserHasNoRemotePlaybackApi()
    {
        Arrange(support: "unsupported");
        var cut = await OpenMenuAsync(PlaybackBarMedia.Music, Item("Music"));

        cut.WaitForAssertion(() => Assert.True(MenuRow(cut).HasAttribute("disabled")));
        Assert.Contains("Not available in this browser", MenuRow(cut).TextContent);
    }

    [Fact]
    public async Task TheRow_IsDisabledWhileNothingIsPlaying()
    {
        Arrange();
        var cut = await OpenMenuAsync(PlaybackBarMedia.Music, null);

        Assert.True(MenuRow(cut).HasAttribute("disabled"));
        Assert.Contains("Nothing is playing", MenuRow(cut).TextContent);
    }

    [Fact]
    public async Task TheRow_IsDisabledForAVaultItem_InTheMenuAndInPlaybackSettings()
    {
        Arrange();
        var menu = await OpenMenuAsync(PlaybackBarMedia.Audiobook, Item("Audiobook", vault: true));
        Assert.True(MenuRow(menu).HasAttribute("disabled"));
        Assert.Contains("Not available for Vault items", MenuRow(menu).TextContent);

        // Only one panel is open at a time, so the settings panel is opened after the menu has been checked.
        var settings = await OpenSettingsAsync(PlaybackBarMedia.Movie, new() { AssetId = Asset, IsVault = true });
        Assert.True(SettingsRow(settings).HasAttribute("disabled"));
        Assert.Contains("Not available for Vault items", SettingsRow(settings).TextContent);
    }

    [Fact]
    public async Task ActivatingAVaultItem_NeverAsksTheEngineForALinkOrOpensThePicker()
    {
        var remote = Arrange();
        var cut = await OpenSettingsAsync(PlaybackBarMedia.Movie, new() { AssetId = Asset, IsVault = true });

        await remote.ActivateAsync(PlaybackRemoteController.VideoElementId, Asset, isVault: true);

        Assert.DoesNotContain(nameof(IEngineApiClient.CreateCastSessionAsync), _stub.Calls);
        Assert.Empty(Called("openPickerOn"));
        Assert.True(SettingsRow(cut).HasAttribute("disabled"));
    }

    // ---- Asking the Engine first ----

    [Fact]
    public async Task ChoosingTheRow_AsksTheEngineForTheCastLinkThenOpensThePickerWithTheDashboardAddress()
    {
        var remote = Arrange(attempt: Ready);
        var cut = await OpenSettingsAsync(PlaybackBarMedia.Movie, new() { AssetId = Asset });

        await cut.InvokeAsync(() => SettingsRow(cut).Click());

        cut.WaitForAssertion(() => Assert.Single(Called("openPickerOn")));
        var prompt = Called("openPickerOn").Single();
        Assert.Equal([PlaybackRemoteController.VideoElementId, "http://192.168.1.5:5016/cast/v1/grant/media"], prompt.Arguments.Select(argument => argument?.ToString()));
        Assert.Contains(nameof(IEngineApiClient.CreateCastSessionAsync), _stub.Calls);
        Assert.False(remote.LastAttemptFailed);
    }

    [Fact]
    public async Task WhenTheEngineRefuses_ThePickerIsNotOpened_TheRowSaysCantPlayYet_AndTheReasonIsLoggedAtInformation()
    {
        var remote = Arrange(attempt: Refused);
        var cut = await OpenMenuAsync(PlaybackBarMedia.Music, Item("Music"));

        await cut.InvokeAsync(() => MenuRow(cut).Click());
        await cut.InvokeAsync(() => cut.FindComponent<PlaybackPanel>().Instance.OpenAsync(true));

        Assert.Empty(Called("openPickerOn"));
        Assert.True(remote.LastAttemptFailed);
        cut.WaitForAssertion(() => Assert.Contains("Can't play on another device yet", MenuRow(cut).TextContent));
        Assert.False(MenuRow(cut).HasAttribute("disabled"));
        Assert.Contains(_log.Entries, entry => entry.Level == LogLevel.Information && entry.Message.Contains("403", StringComparison.Ordinal));
    }

    [Fact]
    public async Task WhenTheDashboardIsOpenOnLocalhost_ThePickerIsNotOpened_BecauseAReceiverCannotReachIt()
    {
        var remote = Arrange(attempt: Ready, baseUri: "http://localhost:5016/");

        await remote.ActivateAsync(PlaybackRemoteController.AudioElementId, Asset, isVault: false);

        Assert.Empty(Called("openPickerOn"));
        Assert.True(remote.LastAttemptFailed);
        Assert.Contains(_log.Entries, entry => entry.Level == LogLevel.Information && entry.Message.Contains("cannot reach", StringComparison.Ordinal));
    }

    [Fact]
    public void TheCastLink_IsTheDashboardAddressForAReadySession_AndAReasonOtherwise()
    {
        var home = new Uri("http://192.168.1.5:5016/");

        Assert.Equal("http://192.168.1.5:5016/cast/v1/grant/media", PlaybackCastLinkService.Build(home, Ready).Url);
        Assert.Equal("video/mp4", PlaybackCastLinkService.Build(home, Ready).ContentType);
        Assert.False(PlaybackCastLinkService.Build(home, Refused).IsReady);
        Assert.Contains("403", PlaybackCastLinkService.Build(home, Refused).Reason);
        Assert.False(PlaybackCastLinkService.Build(home, new(new CastSessionDto { Status = CastSessionStatuses.Preparing, Message = "Getting this ready" }, 200, null)).IsReady);
        Assert.False(PlaybackCastLinkService.Build(home, new(new CastSessionDto { Status = CastSessionStatuses.Ready, MediaPath = "/stream/abc" }, 200, null)).IsReady);
        Assert.False(PlaybackCastLinkService.Build(new Uri("http://127.0.0.1:5016/"), Ready).IsReady);
    }

    [Fact]
    public void TheRowRules_PutTheVaultFirst_ThenStop_ThenTheBrowser_ThenTheItem()
    {
        var supported = PlaybackRemoteSupport.Supported;
        var local = PlaybackRemoteMode.Local;

        Assert.Equal(PlaybackRemoteRowState.Vault, PlaybackRemoteRules.Evaluate(PlaybackRemoteSupport.Unsupported, PlaybackRemoteMode.Remote, true, true, false));
        Assert.Equal(PlaybackRemoteRowState.Stop, PlaybackRemoteRules.Evaluate(supported, PlaybackRemoteMode.Remote, true, false, true));
        Assert.Equal(PlaybackRemoteRowState.NotInBrowser, PlaybackRemoteRules.Evaluate(PlaybackRemoteSupport.Unsupported, local, true, false, false));
        Assert.Equal(PlaybackRemoteRowState.NothingPlaying, PlaybackRemoteRules.Evaluate(supported, local, false, false, false));
        Assert.Equal(PlaybackRemoteRowState.CannotYet, PlaybackRemoteRules.Evaluate(supported, local, true, false, true));
        Assert.Equal(PlaybackRemoteRowState.Start, PlaybackRemoteRules.Evaluate(PlaybackRemoteSupport.Unknown, local, true, false, false));
    }

    // ---- While a receiver plays ----

    private IRenderedComponent<PlaybackShell> RenderShell(PlaybackBarMedia media = PlaybackBarMedia.Movie) => Render<PlaybackShell>(p => p
        .Add(s => s.Media, media)
        .Add(s => s.Identity, PlaybackBarIdentity.Build(media, Item(media == PlaybackBarMedia.Movie ? "Movie" : "Music")))
        .Add(s => s.State, new PlaybackBarState(CanPrevious: true, CanNext: true))
        .Add(s => s.Volume, 0.5)
        .Add(s => s.VolumeStep, 0.05));

    [Fact]
    public async Task WhileLocal_TheBarHasNoPlayingOnLine_AndVolumeIsThere()
    {
        Arrange();
        var cut = RenderShell();

        Assert.Empty(cut.FindAll("[data-playback-remote-line]"));
        Assert.NotEmpty(cut.FindAll("[data-playback-volume]"));
        await Task.CompletedTask;
    }

    [Fact]
    public async Task WhileAReceiverPlays_TheIdentityZoneAddsPlayingOnTheNamedDevice_VolumeHides_AndTransportStays()
    {
        var remote = Arrange();
        var cut = RenderShell();

        await cut.InvokeAsync(() => remote.OnRemoteStateChanged("remote", "Living room TV"));

        cut.WaitForAssertion(() => Assert.Equal("Playing on Living room TV", cut.Find("[data-playback-remote-line]").TextContent.Trim()));
        Assert.Empty(cut.FindAll("[data-playback-volume]"));
        Assert.Single(cut.FindAll("[data-playback-zone='transport'] button[aria-label='Play']"));
        Assert.Equal(["identity", "transport", "tools"], cut.Find(".playback-shell__row").Children.Select(child => child.GetAttribute("data-playback-zone")));
        Assert.Equal("The Glass Harbor", cut.Find(".playback-identity__title").TextContent.Trim());
    }

    [Fact]
    public async Task WhenTheBrowserDoesNotNameTheReceiver_TheBarSaysPlayingOnAnotherDevice()
    {
        var remote = Arrange();
        var cut = RenderShell(PlaybackBarMedia.Music);

        await cut.InvokeAsync(() => remote.OnRemoteStateChanged("remote", null));

        cut.WaitForAssertion(() => Assert.Equal("Playing on another device", cut.Find("[data-playback-remote-line]").TextContent.Trim()));
    }

    [Fact]
    public async Task WhenTheReceiverDisconnects_ThePlayingOnLineGoesAndVolumeComesBack()
    {
        var remote = Arrange();
        var cut = RenderShell();
        await cut.InvokeAsync(() => remote.OnRemoteStateChanged("remote", "Living room TV"));

        await cut.InvokeAsync(() => remote.OnRemoteStateChanged("local", null));

        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll("[data-playback-remote-line]")));
        Assert.NotEmpty(cut.FindAll("[data-playback-volume]"));
    }

    [Fact]
    public async Task WhileAReceiverPlays_TheRowReadsStopPlayingOnTheDevice_AndChoosingItReturnsPlaybackToTheBrowser()
    {
        var remote = Arrange();
        var cut = await OpenMenuAsync(PlaybackBarMedia.Music, Item("Music"));
        await cut.InvokeAsync(() => remote.OnRemoteStateChanged("remote", "Living room TV"));
        await cut.InvokeAsync(() => cut.FindComponent<PlaybackPanel>().Instance.OpenAsync(true));

        cut.WaitForAssertion(() => Assert.Contains("Stop playing on Living room TV", MenuRow(cut).TextContent));
        await cut.InvokeAsync(() => MenuRow(cut).Click());

        cut.WaitForAssertion(() => Assert.Single(Called("stopOn")));
        Assert.Equal(PlaybackRemoteController.AudioElementId, Called("stopOn").Single().Arguments[0]);
        Assert.Empty(Called("openPickerOn"));
    }

    [Fact]
    public async Task WithoutANamedDevice_TheStopRowReadsStopPlayingOnAnotherDevice()
    {
        var remote = Arrange();
        var cut = await OpenSettingsAsync(PlaybackBarMedia.Movie, new() { AssetId = Asset });

        await cut.InvokeAsync(() => remote.OnRemoteStateChanged("remote", null));

        cut.WaitForAssertion(() => Assert.Contains("Stop playing on another device", SettingsRow(cut).TextContent));
    }

    // ---- Closing the bar, and the queue moving on ----

    [Fact]
    public async Task EndingTheSession_StopsTheReceiverAndWaitsForTheBrowserToConfirm()
    {
        var remote = Arrange();
        await remote.ProbeAsync(PlaybackRemoteController.AudioElementId);
        await remote.OnRemoteStateChanged("remote", "Living room TV");

        var ending = remote.EndAsync();
        await Task.Delay(50);
        Assert.False(ending.IsCompleted, "the close waits for the disconnect");
        await remote.OnRemoteStateChanged("local", null);
        await ending;

        Assert.Single(Called("stopOn"));
        Assert.Equal(PlaybackRemoteMode.Local, remote.Mode);
    }

    [Fact]
    public async Task ClosingThePlayerWhileAReceiverPlays_EndsTheRemoteSession()
    {
        var remote = Arrange();
        await remote.ProbeAsync(PlaybackRemoteController.AudioElementId);
        _playback.RestoreState(new() { Queue = [Item("Music")], CurrentIndex = 0 });
        await remote.OnRemoteStateChanged("remote", "Living room TV");

        _playback.ClosePlayer();

        Retry.Until(() => Called("stopOn").Count > 0);
        Assert.Single(Called("stopOn"));
    }

    [Fact]
    public async Task WhenTheQueueMovesOnWhileAReceiverPlays_TheNextItemGetsAFreshCastLink()
    {
        var remote = Arrange(attempt: Ready);
        await remote.ProbeAsync(PlaybackRemoteController.AudioElementId);
        var first = Item("Music");
        var second = Item("Music", asset: Guid.NewGuid());
        _playback.RestoreState(new() { Queue = [first, second], CurrentIndex = 0 });
        await remote.ActivateAsync(PlaybackRemoteController.AudioElementId, first.AssetId, isVault: false);
        await remote.OnRemoteStateChanged("remote", "Living room TV");

        _playback.RestoreState(new() { Queue = [first, second], CurrentIndex = 1 });

        Retry.Until(() => Called("followOn").Count > 0);
        var follow = Called("followOn").Single();
        Assert.Equal("http://192.168.1.5:5016/cast/v1/grant/media", follow.Arguments[1]);
    }

    [Fact]
    public async Task APlayerWithoutARemoteController_DrawsNoPlayOnAnotherDeviceRow()
    {
        Render<AppPopoverHost>();
        var cut = Render<PlaybackSettingsPanel>(p => p.Add(m => m.Media, PlaybackBarMedia.Movie));
        await cut.InvokeAsync(() => cut.FindComponent<PlaybackPanel>().Instance.OpenAsync(true));
        cut.WaitForElement("[data-playback-settings]");

        Assert.Empty(cut.FindAll("[data-psp-row='play-on-device']"));
    }

    private static class Retry
    {
        public static void Until(Func<bool> condition)
        {
            for (var attempt = 0; attempt < 100 && !condition(); attempt++)
            {
                Thread.Sleep(50);
            }

            Assert.True(condition(), "The condition did not hold within five seconds.");
        }
    }
}
