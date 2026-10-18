using System.Security.Claims;
using Bunit;
using Bunit.TestDoubles;
using MediaEngine.Contracts.Details;
using MediaEngine.Contracts.Playback;
using MediaEngine.Contracts.Progress;
using MediaEngine.Web.Components.Details;
using MediaEngine.Web.Models.ViewDTOs;
using MediaEngine.Web.Services.Editing;
using MediaEngine.Web.Services.Integration;
using MediaEngine.Web.Services.Playback;
using MediaEngine.Web.Services.Theming;
using MediaEngine.Web.Tests.Support;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace MediaEngine.Web.Tests;

/// <summary>
/// The tab (and, on a show, the season) a person picks lives in the page address as <c>tab</c> and <c>season</c>,
/// written by replacing the current history entry (D19). Picking one never reloads the page from the Engine, a
/// reload or an address that names one opens it, and a personal action that refreshes the page keeps both.
/// </summary>
public sealed class DetailAddressStateTests : AsyncBunitContext
{
    private static readonly Guid Show = Guid.Parse("30000000-0000-0000-0000-000000000003");
    private static readonly Guid SeasonOneId = Guid.Parse("31000000-0000-0000-0000-000000000001");
    private static readonly Guid SeasonTwoId = Guid.Parse("32000000-0000-0000-0000-000000000002");

    private readonly Guid _profile = Guid.NewGuid();
    private readonly List<(DetailEntityType Type, Guid Id, string? Container)> _loads = [];
    private readonly List<(DetailEntityType Type, Guid Id, string? Container)> _refreshes = [];
    private TaskCompletionSource<DetailPageViewModel?>? _refreshGate;

    public DetailAddressStateTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddLogging();
        Services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        Services.AddSingleton<AuthenticationStateProvider>(new ProfileAuth(_profile));
        Services.AddSingleton(EngineApiClientStub.Create(stub =>
        {
            stub.SetHandler(nameof(IEngineApiClient.GetProfilesAsync), _ => Task.FromResult(new List<ProfileViewModel>
            {
                new(_profile, "One", "#aaa", "RestrictedProfile", DateTimeOffset.UtcNow),
            }));
            stub.SetHandler(nameof(IEngineApiClient.LoadDetailPageAsync), args =>
            {
                var loadedId = (Guid)args![1]!;
                _loads.Add(((DetailEntityType)args[0]!, loadedId, (string?)args[3]));
                return Task.FromResult(DetailPageLoadResult.Loaded(loadedId == Show
                    ? ShowPage("season-1")
                    : ShowPage("season-1", loadedId, "Other show")));
            });
            // A refresh that asks for a season gets the show opened on it; one that does not gets the default season.
            stub.SetHandler(nameof(IEngineApiClient.GetDetailPageAsync), args =>
            {
                var type = (DetailEntityType)args![0]!;
                var id = (Guid)args[1]!;
                _refreshes.Add((type, id, (string?)args[3]));
                if (_refreshGate is { } gate)
                    return gate.Task;
                return Task.FromResult<DetailPageViewModel?>(ShowPage(type == DetailEntityType.TvSeason && id == SeasonTwoId ? "season-2" : "season-1"));
            });
            stub.SetHandler(nameof(IEngineApiClient.GetPersonalStatusAsync), _ =>
                Task.FromResult<PersonalStatusInfo?>(new PersonalStatusInfo("r1", 4, 1, 1, false)));
            stub.SetHandler(nameof(IEngineApiClient.ChangePersonalStatusAsync), _ =>
                Task.FromResult(new PersonalStatusCommandResult(Guid.NewGuid(), 3, "r2")));
        }));
        Services.AddScoped<DashboardSessionAccessor>();
        Services.AddScoped<ActiveProfileSessionService>();
        Services.AddScoped<UniverseStateContainer>();
        Services.AddScoped<UIOrchestratorService>();
        Services.AddScoped<MediaEditorLauncherService>();
        Services.AddScoped<CollectionEditorLauncherService>();
        Services.AddSingleton(provider => new SavedItemService(provider.GetRequiredService<IEngineApiClient>()));
        Services.AddSingleton(provider => new MediaReactionService(provider.GetRequiredService<IEngineApiClient>()));
        Services.AddSingleton<IUserPlaybackPreferencesAccessor, NoPlaybackPreferences>();
        Services.AddSingleton(provider => new PlaybackSessionController(
            null!, null!, preferences: provider.GetRequiredService<IUserPlaybackPreferencesAccessor>()));
    }

    [Fact]
    public void PickingCastWritesTheTabToTheAddressByReplacingTheEntryAndDoesNotReloadThePage()
    {
        var cut = RenderShow();
        var navigation = Services.GetRequiredService<BunitNavigationManager>();
        Assert.Single(_loads);

        cut.FindAll("[role='tab']").Single(tab => tab.TextContent.Trim() == "Cast").Click();

        var written = navigation.History.First();
        Assert.Contains("tab=cast", written.Uri, StringComparison.Ordinal);
        Assert.True(written.Options.ReplaceHistoryEntry, "Picking a tab must replace the history entry, never add a Back step.");
        Assert.Single(navigation.History);
        Assert.Single(_loads);
        cut.WaitForAssertion(() => Assert.Contains("Cast", cut.Find("section.tl-detail-body").Id, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void AnAddressThatNamesTheCastTabOpensCast()
    {
        var cut = RenderShow(tab: "cast");

        Assert.Equal("cast", cut.Find("section.tl-detail-body").Id);
    }

    [Fact]
    public void AnUnknownOrHiddenTabFallsBackToTheDefaultTab()
    {
        var unknown = RenderShow(tab: "nonsense");
        var hidden = RenderShow(tab: "universe");

        Assert.Equal("overview", unknown.Find("section.tl-detail-body").Id);
        Assert.Equal("overview", hidden.Find("section.tl-detail-body").Id);
    }

    [Fact]
    public void AnAddressChangeThatOnlyNamesAnotherTabAppliesItWithoutReloadingThePage()
    {
        var cut = RenderShow();
        Assert.Equal("overview", cut.Find("section.tl-detail-body").Id);

        cut.Render(parameters => parameters.Add(c => c.Request, Request(tab: "cast")));

        cut.WaitForAssertion(() => Assert.Equal("cast", cut.Find("section.tl-detail-body").Id));
        Assert.Single(_loads);

        cut.Render(parameters => parameters.Add(c => c.Request, Request(tab: null)));

        cut.WaitForAssertion(() => Assert.Equal("overview", cut.Find("section.tl-detail-body").Id));
        Assert.Single(_loads);
    }

    [Fact]
    public void PickingASeasonWritesItToTheAddressByReplacingTheEntryAndDoesNotReloadThePage()
    {
        var cut = RenderShow();
        var navigation = Services.GetRequiredService<BunitNavigationManager>();

        cut.Find("button.tl-season-toggle").Click();
        cut.FindAll("button.tl-season-card").Single(card => card.QuerySelector("strong")!.TextContent.Trim() == "Season 2").Click();

        var written = navigation.History.First();
        Assert.Contains($"season={SeasonTwoId:D}", written.Uri, StringComparison.Ordinal);
        Assert.Contains($"/details/tvshow/{Show:D}", written.Uri, StringComparison.Ordinal);
        Assert.True(written.Options.ReplaceHistoryEntry);
        Assert.Single(navigation.History);
        Assert.Single(_loads);
        Assert.Equal("Season 2", cut.Find(".tl-season-toggle__label").TextContent.Trim());
    }

    [Fact]
    public void AddressChangesThatNameTheSameShowAndASeasonAreTheSamePageAsTheShow()
    {
        var show = DetailRouteRequest.ForUnified("tvshow", Show, null);
        var viaSeason = DetailRouteRequest.ForUnified("tvshow", Show, null, seasonId: SeasonTwoId, tab: "cast");

        Assert.Equal(show.LoadKey, viaSeason.LoadKey);
        Assert.Equal(SeasonTwoId, viaSeason.SelectedSeasonId);
        Assert.Equal("cast", viaSeason.RequestedTab);
        Assert.NotEqual(show.LoadKey, DetailRouteRequest.ForUnified("tvshow", Guid.NewGuid(), null).LoadKey);
    }

    [Fact]
    public async Task APersonalActionRefreshKeepsTheChosenSeasonAndTab()
    {
        var cut = RenderShow();
        cut.Find("button.tl-season-toggle").Click();
        cut.FindAll("button.tl-season-card").Single(card => card.QuerySelector("strong")!.TextContent.Trim() == "Season 2").Click();
        cut.FindAll("[role='tab']").Single(tab => tab.TextContent.Trim() == "Cast").Click();
        Assert.Equal("cast", cut.Find("section.tl-detail-body").Id);

        cut.Find(".tl-detail-secondary-actions button[aria-label='More actions']").Click();
        await cut.FindAll("[role='menuitem']").Single(item => item.TextContent.Contains("Mark as watched")).ClickAsync(new());

        cut.WaitForAssertion(() => Assert.NotEmpty(_refreshes));
        var refresh = Assert.Single(_refreshes);
        Assert.Equal((DetailEntityType.TvSeason, SeasonTwoId, Show.ToString("D")), refresh);
        Assert.Single(_loads);
        Assert.Equal("cast", cut.Find("section.tl-detail-body").Id);

        // Back on Overview the refreshed page is still on the chosen season, and the page did not fall back to the
        // payload it was first loaded with.
        cut.FindAll("[role='tab']").Single(tab => tab.TextContent.Trim() == "Overview").Click();
        Assert.Equal("Season 2", cut.Find(".tl-season-toggle__label").TextContent.Trim());
        Assert.Single(_loads);
    }

    [Fact]
    public async Task ARefreshThatLandsAfterNavigatingToAnotherItemDoesNotReplaceTheNewPage()
    {
        _refreshGate = new TaskCompletionSource<DetailPageViewModel?>();
        var cut = RenderShow();
        cut.Find(".tl-detail-secondary-actions button[aria-label='More actions']").Click();
        // The handler waits on the refresh, which waits on the gate: do not await the click itself.
        var click = cut.FindAll("[role='menuitem']").Single(item => item.TextContent.Contains("Mark as watched")).ClickAsync(new());
        cut.WaitForAssertion(() => Assert.NotEmpty(_refreshes));

        var other = Guid.NewGuid();
        cut.Render(parameters => parameters.Add(c => c.Request, DetailRouteRequest.ForUnified("tvshow", other, null)));
        cut.WaitForAssertion(() => Assert.Contains("Other show", cut.Markup));

        _refreshGate.SetResult(ShowPage("season-2"));
        await click;
        cut.Render(parameters => parameters.Add(c => c.Request, DetailRouteRequest.ForUnified("tvshow", other, null, tab: "cast")));

        Assert.Contains("Other show", cut.Markup);
        Assert.DoesNotContain("Northwatch", cut.Markup);
    }

    private IRenderedComponent<DetailRouteHost> RenderShow(string? tab = null)
    {
        var cut = Render<DetailRouteHost>(parameters => parameters.Add(c => c.Request, Request(tab)));
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("section.tl-detail-body")));
        return cut;
    }

    private static DetailRouteRequest Request(string? tab) => DetailRouteRequest.ForUnified("tvshow", Show, null, tab: tab);

    private static DetailPageViewModel ShowPage(string currentGroupKey, Guid? id = null, string title = "Northwatch")
    {
        var seasonOne = new[] { Episode("11", "season-1", "1", "First season opener"), Episode("12", "season-1", "2", "First season finale") };
        var seasonTwo = new[] { Episode("21", "season-2", "1", "Second season opener"), Episode("22", "season-2", "2", "Second season finale") };
        return new DetailPageViewModel
        {
            Id = (id ?? Show).ToString("D"),
            EntityType = DetailEntityType.TvShow,
            Title = title,
            Facts = new DetailFactsViewModel { MediaKind = "TV" },
            Tabs =
            [
                new DetailTab { Key = "overview", Label = "Overview" },
                new DetailTab { Key = "cast", Label = "Cast" },
                new DetailTab { Key = "details", Label = "Details" },
            ],
            ContributorGroups = [new CreditGroupViewModel { Title = "Cast", GroupType = CreditGroupType.Cast }],
            PrimaryModule = new DetailPrimaryModuleViewModel { Kind = DetailPrimaryModuleKind.Episodes, Title = "Episodes" },
            OverflowActions = [new DetailAction { Key = "status-complete", Label = "Mark as watched", Icon = "check" }],
            SequencePlacement = new SequencePlacementViewModel
            {
                ContainerId = Show.ToString("D"),
                SelectedContainerId = Show.ToString("D"),
                ContainerTitle = title,
                ContainerLabel = "Show",
                ItemLabel = "Episode",
                ItemPluralLabel = "Episodes",
                GroupLabel = "Season",
                CurrentGroupKey = currentGroupKey,
                CurrentItem = currentGroupKey == "season-2" ? seasonTwo[0] : seasonOne[0],
                Groups =
                [
                    Group("season-1", "Season 1", SeasonOneId, seasonOne),
                    Group("season-2", "Season 2", SeasonTwoId, seasonTwo),
                ],
            },
        };
    }

    private static SequenceGroupViewModel Group(string key, string title, Guid seasonId, SequenceItemViewModel[] items) => new()
    {
        Key = key,
        Title = title,
        EntityId = seasonId.ToString("D"),
        OwnedCount = items.Length,
        ItemCount = items.Length,
        TotalKnownItems = items.Length,
        Items = items,
    };

    private static SequenceItemViewModel Episode(string id, string groupKey, string number, string title) => new()
    {
        Id = $"4{id}000000-0000-0000-0000-000000000000",
        EntityType = DetailEntityType.TvEpisode,
        Title = title,
        GroupKey = groupKey,
        GroupTitle = groupKey == "season-1" ? "Season 1" : "Season 2",
        PositionLabel = number,
        PositionNumber = int.Parse(number),
        PositionSort = int.Parse(number),
        PositionText = $"E{number}",
        IsOwned = true,
        IsCurrent = groupKey == "season-2" ? id == "21" : id == "11",
    };

    private sealed class NoPlaybackPreferences : IUserPlaybackPreferencesAccessor
    {
        public Task<UserPlaybackSettingsDto?> GetAsync(CancellationToken ct = default) => Task.FromResult<UserPlaybackSettingsDto?>(null);
        public void UpdateCache(UserPlaybackSettingsDto settings) { }
        public void Invalidate() { }
    }

    private sealed class ProfileAuth(Guid profile) : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync() => Task.FromResult(new AuthenticationState(new ClaimsPrincipal(new ClaimsIdentity([new Claim("tuvima:active_profile_id", profile.ToString("D"))], "test"))));
    }
}
