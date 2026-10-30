using Bunit;
using MediaEngine.Contracts.Details;
using MediaEngine.Web.Components.Details;
using MediaEngine.Web.Services.Editing;
using MediaEngine.Web.Services.Integration;
using MediaEngine.Web.Services.Playback;
using MediaEngine.Web.Services.Theming;
using MediaEngine.Web.Tests.Support;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace MediaEngine.Web.Tests;

/// <summary>
/// A detail page that cannot load says why: a missing item, a refused account and an unreachable
/// Engine each have their own message, and only the last offers Retry.
/// </summary>
public sealed class DetailRouteHostErrorStateTests : AsyncBunitContext
{
    private readonly Queue<DetailPageLoadResult> _results = [];
    private int _loadCalls;

    public DetailRouteHostErrorStateTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddLogging();
        Services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        Services.AddSingleton(EngineApiClientStub.Create(stub =>
            stub.SetHandler(nameof(IEngineApiClient.LoadDetailPageAsync), _ =>
            {
                _loadCalls++;
                return Task.FromResult(_results.Dequeue());
            })));
        Services.AddScoped<ActiveProfileSessionService>();
        Services.AddScoped<UniverseStateContainer>();
        Services.AddScoped<UIOrchestratorService>();
        Services.AddScoped<CollectionEditorLauncherService>();
        Services.AddSingleton(provider => new SavedItemService(provider.GetRequiredService<IEngineApiClient>()));
        Services.AddSingleton(provider => new MediaReactionService(provider.GetRequiredService<IEngineApiClient>()));
        Services.AddSingleton(provider => new PlaybackSessionController(null!, null!));
    }

    [Fact]
    public void ANotFoundLoadShowsTheNotFoundMessageWithoutRetry()
    {
        var request = Request();
        _results.Enqueue(DetailPageLoadResult.Failed(DetailPageLoadFailure.NotFound));

        var cut = Render<DetailRouteHost>(p => p.Add(c => c.Request, request));

        cut.WaitForAssertion(() => Assert.Contains(request.NotFoundTitle, cut.Markup));
        Assert.Contains(request.NotFoundMessage, cut.Markup);
        Assert.NotEmpty(cut.FindAll(".app-page-state--unavailable"));
        Assert.Empty(cut.FindAll(".app-page-state button"));
    }

    [Fact]
    public void ANotAllowedLoadSaysTheAccountCannotOpenTheItemWithoutRetry()
    {
        var request = Request();
        _results.Enqueue(DetailPageLoadResult.Failed(DetailPageLoadFailure.NotAllowed));

        var cut = Render<DetailRouteHost>(p => p.Add(c => c.Request, request));

        cut.WaitForAssertion(() => Assert.Contains("You don't have access to this", cut.Markup));
        Assert.Contains("Ask a server administrator", cut.Markup);
        Assert.DoesNotContain(request.NotFoundTitle, cut.Markup);
        Assert.DoesNotContain("Can't reach the Engine", cut.Markup);
        Assert.Empty(cut.FindAll(".app-page-state button"));
    }

    [Fact]
    public void AnUnreachableEngineSaysSoAndOffersRetry()
    {
        var request = Request();
        _results.Enqueue(DetailPageLoadResult.Failed(DetailPageLoadFailure.Unreachable));

        var cut = Render<DetailRouteHost>(p => p.Add(c => c.Request, request));

        cut.WaitForAssertion(() => Assert.Contains("Can't reach the Engine", cut.Markup));
        Assert.Contains("Check that it's running", cut.Markup);
        Assert.DoesNotContain(request.NotFoundTitle, cut.Markup);
        Assert.Equal("Retry", Assert.Single(cut.FindAll(".app-page-state button")).TextContent.Trim());
    }

    [Fact]
    public void RetryLoadsAgainAndShowsThePageWhenTheEngineAnswers()
    {
        _results.Enqueue(DetailPageLoadResult.Failed(DetailPageLoadFailure.Unreachable));
        _results.Enqueue(DetailPageLoadResult.Loaded(new DetailPageViewModel
        {
            Id = Guid.NewGuid().ToString("D"),
            EntityType = DetailEntityType.Work,
            Title = "Recovered album",
            Facts = new DetailFactsViewModel { MediaKind = "Audiobook" },
            Tabs = [new DetailTab { Key = "overview", Label = "Overview" }],
        }));
        var cut = Render<DetailRouteHost>(p => p.Add(c => c.Request, Request()));
        cut.WaitForAssertion(() => Assert.Single(cut.FindAll(".app-page-state button")));
        Assert.Equal(1, _loadCalls);

        cut.Find(".app-page-state button").Click();

        cut.WaitForAssertion(() => Assert.Contains("Recovered album", cut.Markup));
        Assert.Equal(2, _loadCalls);
        Assert.Empty(cut.FindAll(".app-page-state"));
    }

    [Fact]
    public void RetryThatFailsAgainKeepsTheUnreachableStateAndTheRetryButton()
    {
        _results.Enqueue(DetailPageLoadResult.Failed(DetailPageLoadFailure.Unreachable));
        _results.Enqueue(DetailPageLoadResult.Failed(DetailPageLoadFailure.Unreachable));
        var cut = Render<DetailRouteHost>(p => p.Add(c => c.Request, Request()));
        cut.WaitForAssertion(() => Assert.Single(cut.FindAll(".app-page-state button")));

        cut.Find(".app-page-state button").Click();

        cut.WaitForAssertion(() => Assert.Equal(2, _loadCalls));
        cut.WaitForAssertion(() => Assert.Single(cut.FindAll(".app-page-state button")));
        Assert.Contains("Can't reach the Engine", cut.Markup);
    }

    private static DetailRouteRequest Request() => DetailRouteRequest.ForUnified("musicalbum", Guid.NewGuid(), null);
}
