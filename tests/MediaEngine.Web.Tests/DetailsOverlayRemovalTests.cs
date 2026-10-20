using Bunit;
using MediaEngine.Contracts.Details;
using MediaEngine.Contracts.Realtime;
using MediaEngine.Web.Components.Details;
using MediaEngine.Web.Services.Editing;
using MediaEngine.Web.Services.Integration;
using MediaEngine.Web.Services.Navigation;
using MediaEngine.Web.Services.Playback;
using MediaEngine.Web.Services.Theming;
using MediaEngine.Web.Services.Ui;
using MediaEngine.Web.Tests.Support;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using RouteData = Microsoft.AspNetCore.Components.RouteData;

namespace MediaEngine.Web.Tests;

/// <summary>
/// When the open title is deleted (here or in another window), "leave the page" means dismissing the details overlay
/// entirely, back to the page under it, through the same path as the close button (spec D1, Delete from library).
/// </summary>
public sealed class DetailsOverlayRemovalTests : AsyncBunitContext
{
    private static readonly Guid MovieId = Guid.Parse("11111111-aaaa-bbbb-cccc-000000000001");

    private readonly List<int> _steps = [];
    private readonly NavigationManager _nav;

    public DetailsOverlayRemovalTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddLogging();
        var api = EngineApiClientStub.CreateDefault();
        Services.AddSingleton<IEngineApiClient>(api);
        Services.AddSingleton(new DeviceContextService(api));
        Services.AddSingleton(new DashboardSessionAccessor());
        Services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        Services.AddScoped<ActiveProfileSessionService>();
        Services.AddScoped<UniverseStateContainer>();
        Services.AddScoped<UIOrchestratorService>();
        Services.AddScoped<MediaEditorLauncherService>();
        Services.AddScoped<CollectionEditorLauncherService>();
        Services.AddSingleton(new SavedItemService(api));
        Services.AddSingleton(new MediaReactionService(api));
        Services.AddSingleton<ListenAudioDragService>();
        Services.AddSingleton(_ => new PlaybackSessionController(null!, null!));
        Services.AddSingleton<IDetailsOverlayHistory>(new StepRecorder(_steps));
        Services.AddScoped<DetailsOverlayState>();
        _nav = Services.GetRequiredService<NavigationManager>();
    }

    private static DetailPageViewModel Model() => new()
    {
        Id = MovieId.ToString("D"),
        EntityType = DetailEntityType.Movie,
        Title = "Dune: Part One",
        Tabs = [new DetailTab { Key = "overview", Label = "Overview" }],
    };

    private IRenderedComponent<DetailPage> RenderInOpenOverlay()
    {
        _nav.NavigateTo("/watch");
        var state = Services.GetRequiredService<DetailsOverlayState>();
        state.RememberPage("/watch", new RouteData(typeof(BlankPage), new Dictionary<string, object?>()));
        state.Attach();
        _nav.NavigateTo($"/details/movie/{MovieId}");
        Assert.True(state.IsOpen);
        return Render<DetailPage>(parameters => parameters
            .Add(page => page.Model, Model())
            .AddCascadingValue(state));
    }

    [Fact]
    public async Task WhenTheOpenTitleIsDeletedElsewhere_TheOverlayIsDismissedEntirelyAndNoPageIsNavigatedTo()
    {
        var cut = RenderInOpenOverlay();
        var toasts = Services.GetRequiredService<AppToastService>();

        await cut.InvokeAsync(() => Services.GetRequiredService<UIOrchestratorService>().RaiseLibraryItemsRemoved(new LibraryItemsRemovedEvent([MovieId], [])));

        cut.WaitForAssertion(() => Assert.Equal([1], _steps));
        Assert.Equal(["This item is no longer in your library."], toasts.Toasts.Select(toast => toast.Message));
        Assert.DoesNotContain(JSInterop.Invocations, invocation => invocation.Identifier == "detailOrigin.back");
    }

    [Fact]
    public async Task ARefusingDismissGuard_DoesNotStopTheOverlayLeavingATitleThatNoLongerExists()
    {
        var cut = RenderInOpenOverlay();
        using var registration = Services.GetRequiredService<DetailsOverlayState>().RegisterDismissGuard(new RefusingGuard());

        await cut.InvokeAsync(() => Services.GetRequiredService<UIOrchestratorService>().RaiseLibraryItemsRemoved(new LibraryItemsRemovedEvent([MovieId], [])));

        cut.WaitForAssertion(() => Assert.Equal([1], _steps));
    }

    [Fact]
    public async Task WhenAnotherTitleIsDeleted_TheOverlayStaysOpen()
    {
        var cut = RenderInOpenOverlay();

        await cut.InvokeAsync(() => Services.GetRequiredService<UIOrchestratorService>().RaiseLibraryItemsRemoved(new LibraryItemsRemovedEvent([Guid.NewGuid()], [])));

        Assert.Empty(_steps);
        Assert.True(Services.GetRequiredService<DetailsOverlayState>().IsOpen);
    }

    private sealed class BlankPage : ComponentBase
    {
    }

    private sealed class StepRecorder(List<int> steps) : IDetailsOverlayHistory
    {
        public ValueTask<bool> GoBackAsync(int count)
        {
            steps.Add(count);
            return ValueTask.FromResult(true);
        }
    }

    private sealed class RefusingGuard : IDetailsOverlayDismissGuard
    {
        public ValueTask<bool> CanDismissAsync() => ValueTask.FromResult(false);
    }
}
