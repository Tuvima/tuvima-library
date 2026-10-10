using System.Reflection;
using System.Text.Json;
using Bunit;
using MediaEngine.Contracts.Metadata;
using MediaEngine.Contracts.Review;
using MediaEngine.Web.Components.MediaEditor;
using MediaEngine.Web.Components.Shared;
using MediaEngine.Web.Services.Editing;
using MediaEngine.Web.Services.Integration;
using MediaEngine.Web.Services.Ui;
using MediaEngine.Web.Tests.Support;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MediaEngine.Web.Tests;

/// <summary>The review editor puts the "Found as a TV title" suggestion at the top of Match &amp; Identity.</summary>
public sealed class MovieAsTvEditorTests : AsyncBunitContext
{
    private static readonly Guid EntityId = Guid.Parse("22222222-2222-4222-8222-222222222222");
    private static readonly Guid ReviewId = Guid.Parse("11111111-1111-4111-8111-111111111111");

    public MovieAsTvEditorTests()
    {
        Services.AddNativeUiServices();
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    private void RegisterShellServices(out List<Guid> moved)
    {
        var movedIds = new List<Guid>();
        moved = movedIds;
        var api = EngineApiClientStub.Create(stub =>
            stub.SetHandler(nameof(IEngineApiClient.MoveReviewItemToTvAsync), args =>
            {
                movedIds.Add((Guid)args![0]!);
                return Task.FromResult<ReviewMoveToTvResponse?>(new ReviewMoveToTvResponse(true, ReviewId, EntityId, "Dr. Horrible's Sing-Along Blog", "tv-library"));
            }));
        Services.AddSingleton(api);
        Services.AddSingleton(serviceProvider => new UIOrchestratorService(
            api,
            new UniverseStateContainer(),
            new ActiveProfileSessionService(serviceProvider.GetRequiredService<Microsoft.JSInterop.IJSRuntime>(), api),
            new ConfigurationBuilder().Build(),
            serviceProvider.GetRequiredService<ILogger<UIOrchestratorService>>()));
        Services.AddSingleton(_ => new ProviderCatalogueService(api, new MemoryCache(new MemoryCacheOptions())));
    }

    private static MediaEditorLaunchRequest Request(string trigger) => new()
    {
        EntityIds = [EntityId],
        LaunchEntityId = EntityId,
        LaunchEntityKind = "MediaAsset",
        Mode = SharedMediaEditorMode.Review,
        InitialTab = "links",
        ReviewItemId = ReviewId,
        ReviewTrigger = trigger,
        ReviewCandidatesJson = JsonSerializer.Serialize(new[]
        {
            new MovieTvSuggestionDto { TmdbTvId = "5739", Name = "Dr. Horrible's Sing-Along Blog", FirstAirYear = 2008, Type = "Miniseries", Episodes = 3 },
        }),
        MediaType = "Movies",
        HeaderTitle = "Dr. Horrible's Sing-Along Blog",
    };

    [Fact]
    public void MatchTab_ForTheTrigger_ShowsTheCardWithoutSearchAgain_AndMoveToTvRunsTheAction()
    {
        RegisterShellServices(out var moved);
        Render<AppPopoverHost>();

        var cut = Render<MatchShell>(p => p.Add(c => c.Request, Request("MovieMatchedAsTv")));

        cut.WaitForAssertion(() => Assert.Single(cut.FindAll(".sme-movie-tv-suggestion .movie-as-tv-card")));
        Assert.Equal(
            ["Move to TV", "Keep as unmatched film"],
            cut.FindAll(".movie-as-tv-card__actions button").Select(button => button.TextContent.Trim()).ToArray());

        cut.FindAll(".movie-as-tv-card__actions button")[0].Click();
        cut.WaitForAssertion(() => Assert.Equal([ReviewId], moved));
    }

    [Fact]
    public void MatchTab_ForOtherTriggers_DoesNotShowTheCard()
    {
        RegisterShellServices(out _);
        Render<AppPopoverHost>();

        var cut = Render<MatchShell>(p => p.Add(c => c.Request, Request("RetailMatchFailed")));

        Assert.Empty(cut.FindAll(".movie-as-tv-card"));
    }

    private sealed class MatchShell : SharedMediaEditorShell
    {
        protected override Task OnInitializedAsync()
        {
            SetField("_editorContext", new MediaEditorContextDto
            {
                LaunchEntityId = EntityId,
                MediaType = "Movies",
                InitialScope = "movie",
                AvailableTabs = ["details", "artwork", "links", "history"],
                Scopes =
                [
                    new MediaEditorScopeDto
                    {
                        ScopeId = "movie",
                        Label = "Movie",
                        Order = 0,
                        FieldEntityId = EntityId,
                        DisplayTitle = "Dr. Horrible's Sing-Along Blog",
                        CanEditFields = true,
                        AvailableTabs = ["details", "artwork", "links", "history"],
                        FieldSnapshot = new MediaEditorScopeFieldSnapshotDto(),
                    },
                ],
            });
            SetField("_activeScopeId", "movie");
            SetField("_loading", false);
            ((MediaEditorTabState)typeof(SharedMediaEditorShell)
                .GetField("_tabState", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(this)!).Activate("links");
            return Task.CompletedTask;
        }

        private void SetField(string name, object value) =>
            typeof(SharedMediaEditorShell).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(this, value);
    }
}
