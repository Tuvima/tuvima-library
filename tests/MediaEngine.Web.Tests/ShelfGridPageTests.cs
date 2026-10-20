using System.Text.Json;
using Bunit;
using Bunit.TestDoubles;
using MediaEngine.Contracts.Display;
using MediaEngine.Web.Components.Pages;
using MediaEngine.Web.Components.Shared;
using MediaEngine.Web.Services.Integration;
using MediaEngine.Web.Services.Theming;
using MediaEngine.Web.Services.Ui;
using MediaEngine.Web.Tests.Support;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace MediaEngine.Web.Tests;

/// <summary>The View all grid page: what it asks the Engine for, how sort and filters live in the address, and how it pages.</summary>
public sealed class ShelfGridPageTests : AsyncBunitContext
{
    private const string Route = "/all/watch/recently-added";
    private static readonly DateTimeOffset Now = new(2026, 10, 20, 12, 0, 0, TimeSpan.Zero);

    private readonly ToastSpy _toasts = new();
    private readonly DeviceContextService _device;
    private readonly List<GridCall> _gridCalls = [];
    private readonly List<(string RememberKey, string Sort)> _sortSaves = [];
    private Func<GridCall, DisplayGridPageDto?> _grid = _ => null;
    private bool _saveSucceeds = true;
    private int? _statusCode;
    private IEngineApiClient _api = null!;

    private sealed record GridCall(string Lane, string ShelfKey, string? Sort, string? Type, string? Genre, string? Format, string? Status, int Offset, int Limit);

    public ShelfGridPageTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddLogging();
        Services.AddLocalization();
        Services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        Services.AddSingleton<IAppToastService>(_toasts);
        Services.AddSupplyValueFromQueryProvider();

        _api = EngineApiClientStub.Create(stub =>
        {
            stub.SetHandler(nameof(IEngineApiClient.GetDisplayGridAsync), args =>
            {
                var call = new GridCall((string)args![0]!, (string)args[1]!, (string?)args[2], (string?)args[3], (string?)args[4],
                    (string?)args[5], (string?)args[6], (int)args[7]!, (int)args[8]!);
                _gridCalls.Add(call);
                return Task.FromResult(_grid(call));
            });
            stub.SetHandler(nameof(IEngineApiClient.SetDisplayGridSortAsync), args =>
            {
                _sortSaves.Add(((string)args![0]!, (string)args[1]!));
                return Task.FromResult(_saveSucceeds);
            });
            stub.SetHandler("get_LastStatusCode", _ => _statusCode);
        });
        Services.AddSingleton(_api);
        _device = new DeviceContextService(_api);
        Services.AddSingleton(_device);
    }

    private static DisplayArtworkDto Art()
    {
        var fields = typeof(DisplayArtworkDto).GetProperties()
            .Where(property => property.Name.EndsWith("Url", StringComparison.Ordinal))
            .ToDictionary(property => property.Name, _ => "/art.jpg");
        return JsonSerializer.Deserialize<DisplayArtworkDto>(JsonSerializer.Serialize(fields))!;
    }

    private static Guid Id(int number) => Guid.Parse($"a0000000-0000-0000-0000-{number:D12}");

    private static DisplayCardDto Card(int number) =>
        new(Id(number), Id(number), Guid.NewGuid(), null, "Movie", "work", $"Film {number}", null, [], Art(), "portrait", "default",
            "caption", "bottom", null, [], new(true, false, false, false), Now.AddDays(-number));

    private static readonly IReadOnlyList<DisplayGridSortOptionDto> SortOptions =
    [
        new(DisplayGridValues.SortAdded, "Recently added"),
        new(DisplayGridValues.SortTitle, "Title"),
        new(DisplayGridValues.SortYear, "Year"),
    ];

    private static DisplayGridFilterGroupDto GenreGroup(string? selected = null) =>
        new(DisplayGridValues.FilterGenre, "Genre",
        [
            new("Drama", "Drama", 7, selected == "Drama"),
            new("Comedy", "Comedy", 3, selected == "Comedy"),
        ]);

    private static DisplayGridPageDto Grid(
        IReadOnlyList<DisplayCardDto> cards,
        int total,
        int offset = 0,
        bool hasMore = false,
        string sort = DisplayGridValues.SortAdded,
        string? rememberKey = DisplayGridValues.RememberWatch,
        IReadOnlyList<DisplayGridFilterGroupDto>? filters = null,
        string defaultSort = DisplayGridValues.SortAdded) =>
        new("watch", "recently-added", "Recently added", null, cards, total, offset, 48, hasMore, null, sort, defaultSort,
            rememberKey, SortOptions, filters ?? [GenreGroup()]);

    private Task UseAsync(string formFactor) =>
        _device.InitialiseAsync(new DeviceContextSnapshot(formFactor == "phone" ? "mobile" : "web", formFactor, formFactor != "desktop"));

    private BunitNavigationManager Nav => (BunitNavigationManager)Services.GetRequiredService<NavigationManager>();

    private IRenderedComponent<ShelfGridPage> RenderPage(string url = Route)
    {
        Nav.NavigateTo(url);
        var cut = Render<ShelfGridPage>(parameters => parameters
            .Add(page => page.Lane, "watch")
            .Add(page => page.ShelfKey, "recently-added"));
        return cut;
    }

    private static string[] Titles(IRenderedComponent<ShelfGridPage> cut) =>
        cut.FindAll("article.media-tile").Select(tile => tile.GetAttribute("aria-label") ?? string.Empty).ToArray();

    private static IRenderedComponent<AppSelect> Select(IRenderedComponent<ShelfGridPage> cut, string label) =>
        cut.FindComponents<AppSelect>().First(select => select.Instance.Label == label);

    [Fact]
    public async Task ShowsTheTitleTheCountAndOneTilePerCard()
    {
        await UseAsync("desktop");
        _grid = _ => Grid([Card(1), Card(2), Card(3)], total: 312, hasMore: true);

        var cut = RenderPage();

        cut.WaitForAssertion(() => Assert.Equal(["Film 1", "Film 2", "Film 3"], Titles(cut)));
        Assert.Equal("Recently added", cut.Find("h1").TextContent.Trim());
        var count = cut.Find("[data-shelf-grid-count]");
        Assert.Equal("312 titles", count.TextContent.Trim());
        Assert.Equal("status", count.GetAttribute("role"));
        Assert.Equal("polite", count.GetAttribute("aria-live"));
        Assert.DoesNotContain("is-landscape", cut.Find("article.media-tile").ClassName);
    }

    [Fact]
    public async Task OneTitleIsCountedInTheSingular()
    {
        await UseAsync("desktop");
        _grid = _ => Grid([Card(1)], total: 1);

        var cut = RenderPage();

        cut.WaitForAssertion(() => Assert.Equal("1 title", cut.Find("[data-shelf-grid-count]").TextContent.Trim()));
    }

    [Fact]
    public async Task ReadsSortAndFiltersFromTheAddressAndPassesThemToTheEngine()
    {
        await UseAsync("desktop");
        _grid = _ => Grid([Card(1)], total: 1, sort: DisplayGridValues.SortTitle, filters: [GenreGroup("Drama")]);

        var cut = RenderPage($"{Route}?sort=title&type=movie&genre=Drama&format=4k&status=in-progress");

        cut.WaitForAssertion(() => Assert.Single(Titles(cut)));
        var call = Assert.Single(_gridCalls);
        Assert.Equal(new GridCall("watch", "recently-added", "title", "movie", "Drama", "4k", "in-progress", 0, DisplayGridValues.DefaultPageSize), call);
    }

    [Fact]
    public async Task WithoutASortInTheAddressTheEngineIsAskedWithoutOneAndTheAppliedSortIsNotWrittenBack()
    {
        await UseAsync("desktop");
        _grid = _ => Grid([Card(1)], total: 1, sort: DisplayGridValues.SortYear);

        var cut = RenderPage();

        cut.WaitForAssertion(() => Assert.Single(Titles(cut)));
        Assert.Null(Assert.Single(_gridCalls).Sort);
        Assert.DoesNotContain("sort=", Nav.Uri);
        Assert.Equal(DisplayGridValues.SortYear, Select(cut, "Sort by").Instance.Value);
    }

    [Fact]
    public async Task ChangingTheSortSavesItWhenTheGridRemembersItAndReloadsFromTheTop()
    {
        await UseAsync("desktop");
        _grid = call => Grid([Card(1)], total: 1, sort: call.Sort ?? DisplayGridValues.SortAdded);
        var cut = RenderPage();
        cut.WaitForAssertion(() => Assert.Single(Titles(cut)));

        await cut.InvokeAsync(() => Select(cut, "Sort by").Instance.ValueChanged.InvokeAsync(DisplayGridValues.SortTitle));

        Assert.Equal([(DisplayGridValues.RememberWatch, DisplayGridValues.SortTitle)], _sortSaves);
        Assert.Contains("sort=title", Nav.Uri);
        Assert.True(Nav.History.First().Options.ReplaceHistoryEntry);
        cut.WaitForAssertion(() =>
        {
            Assert.Equal(2, _gridCalls.Count);
            Assert.Equal(new GridCall("watch", "recently-added", "title", null, null, null, null, 0, DisplayGridValues.DefaultPageSize), _gridCalls[1]);
        });
        Assert.Empty(_toasts.Messages);
    }

    [Fact]
    public async Task ChangingTheSortOfAGridThatDoesNotRememberItSavesNothing()
    {
        await UseAsync("desktop");
        _grid = call => Grid([Card(1)], total: 1, sort: call.Sort ?? DisplayGridValues.SortPlayed, rememberKey: null, defaultSort: DisplayGridValues.SortPlayed);
        var cut = RenderPage();
        cut.WaitForAssertion(() => Assert.Single(Titles(cut)));

        await cut.InvokeAsync(() => Select(cut, "Sort by").Instance.ValueChanged.InvokeAsync(DisplayGridValues.SortTitle));

        Assert.Empty(_sortSaves);
        Assert.Contains("sort=title", Nav.Uri);
        cut.WaitForAssertion(() => Assert.Equal(DisplayGridValues.SortTitle, _gridCalls[^1].Sort));
    }

    [Fact]
    public async Task AFailedSaveKeepsTheNewSortForThisVisitAndSaysSoOnce()
    {
        await UseAsync("desktop");
        _saveSucceeds = false;
        _grid = call => Grid([Card(1)], total: 1, sort: call.Sort ?? DisplayGridValues.SortAdded);
        var cut = RenderPage();
        cut.WaitForAssertion(() => Assert.Single(Titles(cut)));

        await cut.InvokeAsync(() => Select(cut, "Sort by").Instance.ValueChanged.InvokeAsync(DisplayGridValues.SortYear));

        Assert.Equal("Couldn't save this sort for next time", Assert.Single(_toasts.Messages));
        Assert.Contains("sort=year", Nav.Uri);
        cut.WaitForAssertion(() => Assert.Equal(DisplayGridValues.SortYear, _gridCalls[^1].Sort));
    }

    [Fact]
    public async Task ChoosingAFilterReplacesTheAddressAndReloadsFromTheTopKeepingTheSort()
    {
        await UseAsync("desktop");
        _grid = call => Grid([Card(1)], total: 1, sort: call.Sort ?? DisplayGridValues.SortAdded, filters: [GenreGroup(call.Genre)]);
        var cut = RenderPage($"{Route}?sort=title");
        cut.WaitForAssertion(() => Assert.Single(Titles(cut)));

        await cut.InvokeAsync(() => Select(cut, "Genre").Instance.ValueChanged.InvokeAsync("Drama"));

        Assert.Contains("genre=Drama", Nav.Uri);
        Assert.Contains("sort=title", Nav.Uri);
        Assert.True(Nav.History.First().Options.ReplaceHistoryEntry);
        cut.WaitForAssertion(() =>
        {
            Assert.Equal(2, _gridCalls.Count);
            Assert.Equal(new GridCall("watch", "recently-added", "title", null, "Drama", null, null, 0, DisplayGridValues.DefaultPageSize), _gridCalls[1]);
        });
        Assert.Contains("Drama", cut.Find(".app-active-filter-summary").TextContent);
    }

    [Fact]
    public async Task FilterChoicesListEveryOptionWithItsCountAfterAnAnyChoice()
    {
        await UseAsync("desktop");
        _grid = _ => Grid([Card(1)], total: 1);
        var cut = RenderPage();
        cut.WaitForAssertion(() => Assert.Single(Titles(cut)));

        var options = Select(cut, "Genre").Instance.Options!;

        Assert.Equal(["Any", "Drama (7)", "Comedy (3)"], options.Select(option => option.Label));
    }

    [Fact]
    public async Task LoadMoreAppendsTheNextPageSkipsRepeatsAndGoesAwayWhenThereIsNoMore()
    {
        await UseAsync("desktop");
        _grid = call => call.Offset == 0
            ? Grid([Card(1), Card(2)], total: 3, hasMore: true)
            : Grid([Card(2), Card(3)], total: 3, offset: 2, hasMore: false);
        var cut = RenderPage();
        cut.WaitForAssertion(() => Assert.Equal(2, Titles(cut).Length));

        cut.FindAll("button").First(button => button.TextContent.Contains("Load more", StringComparison.Ordinal)).Click();

        cut.WaitForAssertion(() => Assert.Equal(["Film 1", "Film 2", "Film 3"], Titles(cut)));
        Assert.Equal(2, _gridCalls[1].Offset);
        Assert.DoesNotContain(cut.FindAll("button"), button => button.TextContent.Contains("Load more", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AFailedLoadMoreKeepsTheTilesAndOffersTryAgain()
    {
        await UseAsync("desktop");
        var failNext = true;
        _grid = call =>
        {
            if (call.Offset == 0)
            {
                return Grid([Card(1), Card(2)], total: 3, hasMore: true);
            }

            if (failNext)
            {
                failNext = false;
                return null;
            }

            return Grid([Card(3)], total: 3, offset: 2);
        };
        var cut = RenderPage();
        cut.WaitForAssertion(() => Assert.Equal(2, Titles(cut).Length));

        cut.FindAll("button").First(button => button.TextContent.Contains("Load more", StringComparison.Ordinal)).Click();

        cut.WaitForAssertion(() => Assert.Contains("Couldn't load more titles.", cut.Markup));
        Assert.Equal(2, Titles(cut).Length);
        cut.FindAll("button").First(button => button.TextContent.Contains("Try again", StringComparison.Ordinal)).Click();
        cut.WaitForAssertion(() => Assert.Equal(3, Titles(cut).Length));
    }

    [Fact]
    public async Task AFailedFirstLoadShowsRetryAndRetryLoadsTheList()
    {
        await UseAsync("desktop");
        var fail = true;
        _grid = _ => fail ? null : Grid([Card(1)], total: 1);
        var cut = RenderPage();

        cut.WaitForAssertion(() => Assert.Contains("This list could not load", cut.Markup));
        fail = false;
        cut.Find("button.app-error-state__retry").Click();

        cut.WaitForAssertion(() => Assert.Single(Titles(cut)));
        Assert.DoesNotContain("This list could not load", cut.Markup);
    }

    [Fact]
    public async Task ANotFoundGridShowsTheUnavailableStateWithALinkHome()
    {
        await UseAsync("desktop");
        _statusCode = 404;
        _grid = _ => null;

        var cut = RenderPage();

        cut.WaitForAssertion(() => Assert.Contains("This list isn't available", cut.Markup));
        Assert.DoesNotContain("could not load", cut.Markup);
        Assert.Equal("/", cut.Find(".app-page-state a").GetAttribute("href"));
    }

    [Fact]
    public async Task NothingMatchingTheFiltersOffersClearFiltersWhichKeepsTheSort()
    {
        await UseAsync("desktop");
        _grid = call => call.Genre is null
            ? Grid([Card(1)], total: 1, sort: DisplayGridValues.SortTitle)
            : Grid([], total: 0, sort: DisplayGridValues.SortTitle, filters: [GenreGroup(call.Genre)]);
        var cut = RenderPage($"{Route}?sort=title&genre=Drama");

        cut.WaitForAssertion(() => Assert.Contains("Nothing matches these filters", cut.Markup));
        cut.FindAll("button").First(button => button.TextContent.Contains("Clear filters", StringComparison.Ordinal)).Click();

        Assert.DoesNotContain("genre=", Nav.Uri);
        Assert.Contains("sort=title", Nav.Uri);
        cut.WaitForAssertion(() => Assert.Single(Titles(cut)));
    }

    [Fact]
    public async Task AnEmptyListWithoutFiltersSaysNothingIsHereYet()
    {
        await UseAsync("desktop");
        _grid = _ => Grid([], total: 0);

        var cut = RenderPage();

        cut.WaitForAssertion(() => Assert.Contains("Nothing here yet", cut.Markup));
        Assert.DoesNotContain("Clear filters", cut.Markup);
    }

    [Fact]
    public async Task ThePhoneFormFactorShowsOneFiltersButtonInsteadOfInlineControls()
    {
        await UseAsync("phone");
        _grid = _ => Grid([Card(1)], total: 1, filters: [GenreGroup("Drama")]);

        var cut = RenderPage($"{Route}?genre=Drama");

        cut.WaitForAssertion(() => Assert.Single(Titles(cut)));
        var trigger = cut.Find("button.browse-filter-sheet__trigger");
        Assert.Contains("Filters (1)", trigger.TextContent);
        Assert.Empty(cut.FindComponents<AppSelect>());
        trigger.Click();
        Assert.Contains(cut.FindComponents<AppSelect>(), select => select.Instance.Label == "Sort by");
        Assert.Contains("Show 1 title", cut.Find("button.browse-filter-sheet__apply").TextContent);
    }

    [Fact]
    public async Task TheScrollTriggerIsStartedWhileThereIsMoreAndReleasedWhenTheListEnds()
    {
        await UseAsync("desktop");
        var module = JSInterop.SetupModule("./js/load-more-observer.js");
        module.Mode = JSRuntimeMode.Loose;
        _grid = call => call.Offset == 0
            ? Grid([Card(1)], total: 2, hasMore: true)
            : Grid([Card(2)], total: 2, offset: 1);
        var cut = RenderPage();
        cut.WaitForAssertion(() => Assert.Single(module.Invocations["observe"]));

        cut.FindAll("button").First(button => button.TextContent.Contains("Load more", StringComparison.Ordinal)).Click();

        cut.WaitForAssertion(() => Assert.Single(module.Invocations["disconnect"]));
        Assert.Single(module.Invocations["observe"]);
    }

    [Fact]
    public async Task AContinueGridDrawsItsTilesLikeTheContinueShelves()
    {
        await UseAsync("desktop");
        var started = new DisplayCardDto(Id(9), Id(9), Guid.NewGuid(), null, "Movie", "work", "Half Seen", null, [], Art(), "portrait", "default",
            "caption", "bottom", new DisplayProgressDto(50, "50%", Now, null), [], new(true, false, false, false), Now.AddDays(-1))
        { ContinuationState = DisplayContinuationState.InProgress };
        _grid = _ => new("watch", "continue", "Continue watching", null, [started], 1, 0, 48, false, null,
            DisplayGridValues.SortPlayed, DisplayGridValues.SortPlayed, null, SortOptions, []);
        Nav.NavigateTo("/all/watch/continue");

        var cut = Render<ShelfGridPage>(parameters => parameters
            .Add(page => page.Lane, "watch")
            .Add(page => page.ShelfKey, "continue"));

        cut.WaitForAssertion(() => Assert.Contains("is-landscape", cut.Find("article.media-tile").ClassName));
    }

    private sealed class ToastSpy : IAppToastService
    {
        public List<string> Messages { get; } = [];

        public AppToast Add(string message, AppSeverity severity = AppSeverity.Normal, Action<AppToastOptions>? configure = null)
        {
            Messages.Add(message);
            return new AppToast(message, severity, new AppToastOptions());
        }

        public void Remove(AppToast toast)
        {
        }

        public void Clear() => Messages.Clear();
    }
}
