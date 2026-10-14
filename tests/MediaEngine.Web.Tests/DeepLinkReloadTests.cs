using Bunit;
using MediaEngine.Contracts.Playback;
using MediaEngine.Web.Components.Shared;
using MediaEngine.Web.Components.Watch;
using MediaEngine.Web.Services.Integration;
using MediaEngine.Web.Services.Playback;
using MediaEngine.Web.Tests.Support;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;

namespace MediaEngine.Web.Tests;

/// <summary>
/// A full reload of a deep link mounts the shell and its popovers while their scripts are still loading. A
/// component can be replaced or removed before the browser answers; it must then stop quietly instead of failing
/// the whole screen (the error boundary shows "Screen failed to load").
/// </summary>
public sealed class DeepLinkReloadTests : AsyncBunitContext
{
    private const string PopoverModule = "./js/app-popover.js";

    public DeepLinkReloadTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddLogging();
        Services.AddNativeUiServices();
    }

    [Fact]
    public void PopoverTalksToTheBrowserOnlyAfterItsFirstRenderAndDoesItOnce()
    {
        var module = JSInterop.SetupModule(PopoverModule);

        Render<AppPopover>(parameters => parameters.Add(p => p.Open, false));

        var update = Assert.Single(module.Invocations, i => i.Identifier == "update");
        Assert.False((bool)update.Arguments[4]!);
    }

    [Fact]
    public async Task PopoverRemovedWhileItsScriptIsLoadingNeverAsksTheBrowserToPositionIt()
    {
        var module = JSInterop.SetupModule(PopoverModule);
        var gate = new GatedJsRuntime(JSInterop.JSRuntime, (identifier, args) => identifier == "import" && args?.FirstOrDefault() as string == PopoverModule);
        Services.AddSingleton<IJSRuntime>(gate);

        var host = Render<Toggle<AppPopover>>(parameters => parameters.Add(p => p.Show, true));
        await gate.Reached.WaitAsync(TimeSpan.FromSeconds(5));

        host.Render(parameters => parameters.Add(p => p.Show, false));
        gate.Release();
        await WaitForAsync(() => module.Invocations.Any(i => i.Identifier == "detach"));

        Assert.DoesNotContain(module.Invocations, i => i.Identifier == "update");
    }

    [Fact]
    public async Task VideoHostRemovedBeforeItsFirstRenderFinishesRegistersNothingWithTheBrowser()
    {
        var gate = new GatedJsRuntime(JSInterop.JSRuntime, (identifier, _) => identifier == "listenPlayback.canPictureInPicture");
        Services.AddSingleton<IJSRuntime>(gate);
        AddVideoHostServices();

        var host = Render<Toggle<VideoPlaybackHost>>(parameters => parameters.Add(p => p.Show, true));
        await gate.Reached.WaitAsync(TimeSpan.FromSeconds(5));

        host.Render(parameters => parameters.Add(p => p.Show, false));
        gate.Release();
        await Task.Delay(100);

        Assert.DoesNotContain(JSInterop.Invocations, i => i.Identifier == "listenPlayback.registerAudioStateObserver");
        Assert.DoesNotContain(JSInterop.Invocations, i => i.Identifier == "import");
    }

    [Fact]
    public async Task VideoHostRemovedWhileItsScriptsAreLoadingReleasesThemInsteadOfAttaching()
    {
        var chrome = JSInterop.SetupModule("./js/playback-chrome.js");
        JSInterop.SetupModule("./js/video-presentation.js");
        var gate = new GatedJsRuntime(JSInterop.JSRuntime, (identifier, args) => identifier == "import" && args?.FirstOrDefault() as string == "./js/playback-chrome.js");
        Services.AddSingleton<IJSRuntime>(gate);
        AddVideoHostServices();

        var host = Render<Toggle<VideoPlaybackHost>>(parameters => parameters.Add(p => p.Show, true));
        await gate.Reached.WaitAsync(TimeSpan.FromSeconds(5));

        host.Render(parameters => parameters.Add(p => p.Show, false));
        gate.Release();
        await WaitForAsync(() => chrome.Invocations.Any(i => i.Identifier == "detach"));

        Assert.DoesNotContain(chrome.Invocations, i => i.Identifier is "attach" or "update");
    }

    [Fact]
    public void VideoHostRendersAndDisposesImmediatelyWithoutThrowing()
    {
        AddVideoHostServices();

        var host = Render<Toggle<VideoPlaybackHost>>(parameters => parameters.Add(p => p.Show, true));
        host.Render(parameters => parameters.Add(p => p.Show, false));
        host.Render(parameters => parameters.Add(p => p.Show, true));
        host.Render(parameters => parameters.Add(p => p.Show, false));

        Assert.Empty(host.FindAll("video"));
    }

    private void AddVideoHostServices()
    {
        var api = EngineApiClientStub.CreateDefault();
        var playback = new PlaybackSessionController(null!, api, preferences: new Preferences());
        Services.AddSingleton(api);
        Services.AddSingleton(playback);
        Services.AddSingleton(new VideoPresentationResolver(api, null!, playback));
        Services.AddSingleton(new PlaybackTransientToolCoordinator(playback));
        Services.AddSingleton(new UniverseStateContainer());
        Services.AddSingleton<ShellActivityState>();
        Render<AppPopoverHost>();
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (true)
        {
            try
            {
                if (condition())
                {
                    return;
                }
            }
            catch (InvalidOperationException)
            {
                // The recorded calls were being added to while they were read; look again.
            }

            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("The expected browser call did not happen.");
            }

            await Task.Delay(10);
        }
    }

    private sealed class Preferences : IUserPlaybackPreferencesAccessor
    {
        public Guid? ActiveProfileId { get; } = Guid.NewGuid();
        public Task<UserPlaybackSettingsDto?> GetAsync(CancellationToken ct = default) => Task.FromResult<UserPlaybackSettingsDto?>(null);
        public void UpdateCache(UserPlaybackSettingsDto settings) { }
        public void Invalidate() { }
    }

    private sealed class Toggle<TComponent> : ComponentBase where TComponent : IComponent
    {
        [Parameter] public bool Show { get; set; }

        protected override void BuildRenderTree(RenderTreeBuilder builder)
        {
            if (!Show)
            {
                return;
            }

            builder.OpenComponent<TComponent>(0);
            builder.CloseComponent();
        }
    }
}
