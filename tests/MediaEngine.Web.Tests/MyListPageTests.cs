using System.Text.Json;
using Bunit;
using Bunit.TestDoubles;
using MediaEngine.Contracts.Display;
using MediaEngine.Contracts.ProfileState;
using MediaEngine.Domain.Enums;
using MediaEngine.Web.Components.Pages;
using MediaEngine.Web.Models.ViewDTOs;
using MediaEngine.Web.Services.Integration;
using MediaEngine.Web.Services.Navigation;
using MediaEngine.Web.Services.Playback;
using MediaEngine.Web.Services.Theming;
using MediaEngine.Web.Tests.Support;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace MediaEngine.Web.Tests;

/// <summary>My List, the one personal page: kind chips, sort, Select, Remove finished, Undo and the states in the spec's section 4.</summary>
public sealed class MyListPageTests : AsyncBunitContext
{
    private static readonly Guid OwnerId = Guid.Parse("00000000-0000-0000-0000-000000000001");
    private static readonly DateTimeOffset Now = new(2026, 11, 12, 12, 0, 0, TimeSpan.Zero);

    private readonly DeviceContextService _device;
    private readonly List<GridCall> _gridCalls = [];
    private readonly List<(string RememberKey, string Sort)> _sortSaves = [];
    private readonly List<IReadOnlyList<ProfileSavedItemKeyDto>> _removeCalls = [];
    private readonly List<IReadOnlyList<ProfileSavedItemDto>> _restoreCalls = [];
    private readonly List<Row> _rows = [];
    private readonly Dictionary<Guid, Row> _taken = [];
    private List<ProfileSavedItemDto> _finished = [];
    private List<ProfileViewModel> _profiles = [new(OwnerId, "Shy", "#C9922E", "Administrator", DateTimeOffset.UtcNow)];
    private bool _gridFails;
    private bool _removeFails;
    private bool _restoreFails;
    private IEngineApiClient _api = null!;

    private sealed record GridCall(string? Sort, string? Type, int Offset, int Limit);

    /// <summary>One saved title on the fake Engine: its card, its kind, and the date it was saved.</summary>
    private sealed record Row(Guid Id, ProfileEntityKind Kind, string Title, DateTimeOffset SavedAt, string Type, int Year = 2000);

    public MyListPageTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddLogging();
        Services.AddLocalization();
        Services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        Services.AddSupplyValueFromQueryProvider();

        _api = EngineApiClientStub.Create(stub =>
        {
            stub.SetHandler(nameof(IEngineApiClient.GetProfilesAsync), _ => Task.FromResult(_profiles));
            stub.SetHandler(nameof(IEngineApiClient.GetDisplayGridAsync), args => Task.FromResult(ReadGrid(
                new GridCall((string?)args![2], (string?)args[3], (int)args[7]!, (int)args[8]!))));
            stub.SetHandler(nameof(IEngineApiClient.SetDisplayGridSortAsync), args =>
            {
                _sortSaves.Add(((string)args![0]!, (string)args[1]!));
                return Task.FromResult(true);
            });
            stub.SetHandler(nameof(IEngineApiClient.GetSavedItemsAsync), _ => Task.FromResult<IReadOnlyList<ProfileSavedItemDto>>(Saved()));
            stub.SetHandler(nameof(IEngineApiClient.GetFinishedSavedItemsAsync), _ => Task.FromResult<IReadOnlyList<ProfileSavedItemDto>>(_finished));
            stub.SetHandler(nameof(IEngineApiClient.RemoveSavedItemsAsync), args => Task.FromResult(Remove((IReadOnlyList<ProfileSavedItemKeyDto>)args![0]!)));
            stub.SetHandler(nameof(IEngineApiClient.RestoreSavedItemsAsync), args => Task.FromResult(Restore((IReadOnlyList<ProfileSavedItemDto>)args![0]!)));
            stub.SetHandler(nameof(IEngineApiClient.GetSavedItemAsync), args => Task.FromResult(Saved()
                .FirstOrDefault(item => item.EntityKind == (ProfileEntityKind)args![0]! && item.EntityId == (Guid)args[1]!)));
            stub.SetHandler(nameof(IEngineApiClient.RemoveSavedItemAsync), args =>
            {
                foreach (var row in _rows.Where(row => row.Kind == (ProfileEntityKind)args![0]! && row.Id == (Guid)args[1]!).ToList())
                {
                    _taken[row.Id] = row;
                    _rows.Remove(row);
                }

                return Task.FromResult(true);
            });
        });
        Services.AddSingleton(_api);
        Services.AddScoped<UniverseStateContainer>();
        Services.AddScoped<ActiveProfileSessionService>();
        Services.AddScoped<UIOrchestratorService>();
        Services.AddScoped<ViewWorkspaceService>();
        Services.AddScoped<SavedItemService>();
        _device = new DeviceContextService(_api);
        Services.AddSingleton(_device);
    }

    // -- the fake Engine ------------------------------------------------------------------------------------------------

    private static DisplayArtworkDto Art()
    {
        var fields = typeof(DisplayArtworkDto).GetProperties()
            .Where(property => property.Name.EndsWith("Url", StringComparison.Ordinal))
            .ToDictionary(property => property.Name, _ => "/art.jpg");
        return JsonSerializer.Deserialize<DisplayArtworkDto>(JsonSerializer.Serialize(fields))!;
    }

    private static Guid Id(int number) => Guid.Parse($"a0000000-0000-0000-0000-{number:D12}");

    private static DisplayCardDto Card(Row row) =>
        new(row.Id, row.Id, Guid.NewGuid(), null, row.Kind == ProfileEntityKind.Book ? "Book" : "Movie", "work", row.Title, null, [], Art(), "portrait", "default",
            "caption", "bottom", null, [], new(true, false, false, false), row.SavedAt) { SortYear = row.Year };

    private void Seed(int movies, int books = 0)
    {
        _rows.Clear();
        for (var index = 1; index <= movies; index++)
        {
            _rows.Add(new(Id(index), ProfileEntityKind.Movie, $"Film {index}", Now.AddDays(-index), DisplayGridValues.TypeMovie, 2000 + index));
        }

        for (var index = 1; index <= books; index++)
        {
            _rows.Add(new(Id(100 + index), ProfileEntityKind.Book, $"Novel {index}", Now.AddDays(-50 - index), DisplayGridValues.TypeBook, 1990 + index));
        }
    }

    private List<ProfileSavedItemDto> Saved() => [.. _rows.Select(row => new ProfileSavedItemDto(row.Kind, row.Id, row.SavedAt, null))];

    private DisplayGridLoadResult ReadGrid(GridCall call)
    {
        _gridCalls.Add(call);
        if (_gridFails)
        {
            return DisplayGridLoadResult.Failed(DisplayGridLoadFailure.Failed);
        }

        IEnumerable<Row> rows = _rows;
        if (call.Type is not null)
        {
            rows = rows.Where(row => row.Type == call.Type);
        }

        var sorted = (call.Sort switch
        {
            DisplayGridValues.SortTitle => rows.OrderBy(row => row.Title, StringComparer.OrdinalIgnoreCase),
            DisplayGridValues.SortYear => rows.OrderByDescending(row => row.Year),
            _ => rows.OrderByDescending(row => row.SavedAt),
        }).ToList();
        var page = sorted.Skip(call.Offset).Take(call.Limit).ToList();
        var kinds = _rows.GroupBy(row => row.Type).Select(group => (Value: group.Key, Label: group.Key == DisplayGridValues.TypeMovie ? "Movies" : "Books", Count: group.Count())).ToList();
        var group = new DisplayGridFilterGroupDto(DisplayGridValues.FilterType, "Media type",
            [.. kinds.Select(kind => new DisplayGridFilterOptionDto(kind.Value, kind.Label, kind.Count, kind.Value == call.Type))]);
        return DisplayGridLoadResult.Loaded(new("personal", "my-list", "My List", null, [.. page.Select(Card)], sorted.Count, call.Offset, call.Limit,
            call.Offset + page.Count < sorted.Count, null, call.Sort ?? DisplayGridValues.SortAdded, DisplayGridValues.SortAdded,
            DisplayGridValues.RememberMyList, [], kinds.Count >= 2 || call.Type is not null ? [group] : []));
    }

    private IReadOnlyList<ProfileSavedItemDto>? Remove(IReadOnlyList<ProfileSavedItemKeyDto> keys)
    {
        _removeCalls.Add(keys);
        if (_removeFails)
        {
            return null;
        }

        var removed = _rows
            .Where(row => keys.Any(key => key.EntityKind == row.Kind && key.EntityId == row.Id))
            .Select(row => new ProfileSavedItemDto(row.Kind, row.Id, row.SavedAt, null))
            .ToList();
        foreach (var row in _rows.Where(row => removed.Any(item => item.EntityKind == row.Kind && item.EntityId == row.Id)).ToList())
        {
            _taken[row.Id] = row;
            _rows.Remove(row);
        }

        return removed;
    }

    private IReadOnlyList<ProfileSavedItemDto>? Restore(IReadOnlyList<ProfileSavedItemDto> items)
    {
        _restoreCalls.Add(items);
        if (_restoreFails)
        {
            return null;
        }

        foreach (var item in items)
        {
            _rows.Add(_taken[item.EntityId] with { SavedAt = item.SavedAt });
        }

        return items;
    }

    // -- rendering ---------------------------------------------------------------------------------------------------------

    private Task UseAsync(string formFactor) =>
        _device.InitialiseAsync(new DeviceContextSnapshot(formFactor == "phone" ? "mobile" : "web", formFactor, formFactor != "desktop"));

    private BunitNavigationManager Nav => (BunitNavigationManager)Services.GetRequiredService<NavigationManager>();

    private async Task<IRenderedComponent<MyListPage>> RenderPageAsync(string url = "/my-list", string formFactor = "desktop")
    {
        await UseAsync(formFactor);
        Nav.NavigateTo(url);
        var cut = Render<RoutedPageHost<MyListPage>>().FindComponent<MyListPage>();
        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll(".my-list-skeleton")), TimeSpan.FromSeconds(15));
        return cut;
    }

    private static string[] Titles(IRenderedComponent<MyListPage> cut) =>
        cut.FindAll("article.media-tile").Select(tile => tile.GetAttribute("aria-label") ?? string.Empty).ToArray();

    private static string[] ChipTexts(IRenderedComponent<MyListPage> cut) =>
        cut.FindAll(".my-list-chip").Select(chip => string.Join(' ', chip.TextContent.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))).ToArray();

    private static string Count(IRenderedComponent<MyListPage> cut) => cut.Find("[data-my-list-count]").TextContent.Trim();

    private static void Press(IRenderedComponent<MyListPage> cut, string chipText) =>
        cut.FindAll(".my-list-chip").Single(chip => chip.TextContent.Trim().StartsWith(chipText, StringComparison.Ordinal)).Click();

    private static string UndoText(IRenderedComponent<MyListPage> cut) => cut.Find("[data-undo-note-line]").TextContent.Trim();

    // -- header ---------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task HeadingSaysMyListWithTheProfileNameAndFourLinks()
    {
        Seed(3);
        _profiles = [new(OwnerId, "Shy", "#C9922E", "Administrator", DateTimeOffset.UtcNow), new(Guid.NewGuid(), "Mina", "#7C4DFF", "RestrictedProfile", DateTimeOffset.UtcNow)];
        var cut = await RenderPageAsync();

        Assert.Equal("My List", cut.Find("h1").TextContent.Trim());
        Assert.Equal("Shy", cut.Find("[data-my-list-profile-name]").TextContent.Trim());
        var links = cut.FindAll("[data-my-list-link]").ToDictionary(link => link.GetAttribute("data-my-list-link")!, link => (link.TextContent.Trim(), link.GetAttribute("href")));
        Assert.Equal(["activity", "liked", "switch-profile", "settings"], links.Keys.ToArray());
        Assert.Equal(("Activity", SettingsNav.ActivityRoute), links["activity"]);
        Assert.Equal(("Liked", "/all/personal/liked"), links["liked"]);
        Assert.Equal("Switch profile", links["switch-profile"].Item1);
        Assert.Equal("Settings", links["settings"].Item1);
    }

    [Fact]
    public async Task ASingleProfileHasNoSwitchProfileLink()
    {
        Seed(2);
        var cut = await RenderPageAsync();

        Assert.Equal(["activity", "liked", "settings"], cut.FindAll("[data-my-list-link]").Select(link => link.GetAttribute("data-my-list-link")!).ToArray());
    }

    // -- kinds, count, sort -----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task ChipsComeFromTheEngineGroupWithCountsAndAllIsFirst()
    {
        Seed(3, books: 2);
        var cut = await RenderPageAsync();

        Assert.Equal(["All 5", "Movies 3", "Books 2"], ChipTexts(cut));
        Assert.Equal("5 titles", Count(cut));
        Assert.Equal(["Film 1", "Film 2", "Film 3", "Novel 1", "Novel 2"], Titles(cut));
    }

    [Fact]
    public async Task ASingleKindHasNoChipRow()
    {
        Seed(4);
        var cut = await RenderPageAsync();

        Assert.Empty(cut.FindAll("[data-my-list-chips]"));
        Assert.Equal("4 titles", Count(cut));
    }

    [Fact]
    public async Task OneTitleReadsInTheSingular()
    {
        Seed(1);
        var cut = await RenderPageAsync();

        Assert.Equal("1 title", Count(cut));
    }

    [Fact]
    public async Task PickingAChipAsksForThatKindAndTheCountLineNamesIt()
    {
        Seed(3, books: 1);
        var cut = await RenderPageAsync();

        Press(cut, "Movies");

        cut.WaitForAssertion(() => Assert.Equal("3 movies", Count(cut)));
        Assert.Contains("type=movie", Nav.Uri, StringComparison.Ordinal);
        Assert.Equal(DisplayGridValues.TypeMovie, _gridCalls.Last().Type);
        Assert.Equal(["Film 1", "Film 2", "Film 3"], Titles(cut));
        Assert.Equal(["All 4", "Movies 3", "Books 1"], ChipTexts(cut));
        Assert.Contains("is-on", cut.FindAll(".my-list-chip").Single(chip => chip.TextContent.Trim().StartsWith("Movies", StringComparison.Ordinal)).ClassList);

        Press(cut, "Books");
        cut.WaitForAssertion(() => Assert.Equal("1 book", Count(cut)));
    }

    [Fact]
    public async Task PressingAllShowsEverythingAgain()
    {
        Seed(2, books: 1);
        var cut = await RenderPageAsync("/my-list?type=book");
        Assert.Equal("1 book", Count(cut));

        Press(cut, "All");

        cut.WaitForAssertion(() => Assert.Equal("3 titles", Count(cut)));
        Assert.DoesNotContain("type=", Nav.Uri, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SortStaysInTheAddressAndAppliesInsideEveryChip()
    {
        Seed(3, books: 2);
        var cut = await RenderPageAsync();

        await cut.FindComponent<MediaEngine.Web.Components.Shared.AppSelect>().InvokeAsync(() =>
            cut.FindComponent<MediaEngine.Web.Components.Shared.AppSelect>().Instance.ValueChanged.InvokeAsync(DisplayGridValues.SortTitle));
        cut.WaitForAssertion(() => Assert.Equal("Film 1", Titles(cut)[0]));
        Assert.Contains((DisplayGridValues.RememberMyList, DisplayGridValues.SortTitle), _sortSaves);

        Press(cut, "Books");

        cut.WaitForAssertion(() => Assert.Equal("2 books", Count(cut)));
        Assert.Equal(DisplayGridValues.SortTitle, _gridCalls.Last().Sort);
        Assert.Equal(DisplayGridValues.TypeBook, _gridCalls.Last().Type);
        Assert.Contains("sort=title", Nav.Uri, StringComparison.Ordinal);
        Assert.Equal(["Novel 1", "Novel 2"], Titles(cut));
    }

    [Fact]
    public async Task SortOffersDateAddedTitleAndReleaseYearOnly()
    {
        Seed(3);
        var cut = await RenderPageAsync();

        var select = cut.FindComponent<MediaEngine.Web.Components.Shared.AppSelect>().Instance;

        Assert.Equal(["Date added", "Title", "Release year"], select.Options!.Select(option => option.Label).ToArray());
        Assert.Equal(DisplayGridValues.SortAdded, select.Value);
    }

    [Fact]
    public async Task AKindThatBecomesEmptyGoesBackToAll()
    {
        Seed(2, books: 1);
        var cut = await RenderPageAsync("/my-list?type=comic");

        cut.WaitForAssertion(() => Assert.Equal("3 titles", Count(cut)));
        Assert.DoesNotContain("type=", Nav.Uri, StringComparison.Ordinal);
    }

    // -- states ------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task AnEmptyListShowsTheMessageAndBrowseHomeAndNoChipsSortOrSelect()
    {
        Seed(0);
        var cut = await RenderPageAsync();

        Assert.Equal("Your list is empty", cut.Find(".my-list-empty .app-page-state__title, .my-list-empty h2, .my-list-empty h3").TextContent.Trim());
        Assert.Contains("Press + on any title to save it for later.", cut.Markup, StringComparison.Ordinal);
        var browse = cut.FindAll("a").Single(link => link.TextContent.Trim() == "Browse Home");
        Assert.Equal("/", browse.GetAttribute("href"));
        Assert.Empty(cut.FindAll("[data-my-list-chips]"));
        Assert.Empty(cut.FindAll("[data-my-list-sort]"));
        Assert.Empty(cut.FindAll("[data-my-list-select]"));
        Assert.Empty(cut.FindAll(".my-list-page-menu"));
        // The heading and the links stay.
        Assert.Equal("My List", cut.Find("h1").TextContent.Trim());
        Assert.NotEmpty(cut.FindAll("[data-my-list-link]"));
    }

    [Fact]
    public async Task WhenTheListCannotBeReadAnErrorWithTryAgainShowsAndTryingAgainLoadsIt()
    {
        Seed(2);
        _gridFails = true;
        var cut = await RenderPageAsync();

        Assert.Contains("My List could not load", cut.Markup, StringComparison.Ordinal);
        Assert.Empty(cut.FindAll("article.media-tile"));

        _gridFails = false;
        cut.FindAll("button").Single(button => button.TextContent.Trim() == "Try again").Click();

        cut.WaitForAssertion(() => Assert.Equal(["Film 1", "Film 2"], Titles(cut)));
    }

    [Fact]
    public async Task MoreTitlesLoadWhenLoadMoreIsPressed()
    {
        Seed(60);
        var cut = await RenderPageAsync();
        Assert.Equal(48, Titles(cut).Length);

        cut.FindAll("button").Single(button => button.TextContent.Trim() == "Load more").Click();

        cut.WaitForAssertion(() => Assert.Equal(60, Titles(cut).Length));
        Assert.Equal(48, _gridCalls.Last().Offset);
    }

    // -- removing and Undo ---------------------------------------------------------------------------------------------------

    private static void EnterSelect(IRenderedComponent<MyListPage> cut) => cut.Find("[data-my-list-select]").Click();

    private static void Tick(IRenderedComponent<MyListPage> cut, string title) =>
        cut.FindAll("[data-media-tile-pick] [role=checkbox]").Single(button => button.GetAttribute("aria-label") == title).Click();

    [Fact]
    public async Task SelectShowsTheBarWithTheCountAndRemoveStaysOffUntilSomethingIsTicked()
    {
        Seed(4);
        var cut = await RenderPageAsync();

        EnterSelect(cut);

        Assert.Empty(cut.FindAll("[data-my-list-chips]"));
        Assert.Equal("0 selected", cut.Find("[data-my-list-selected-count]").TextContent.Trim());
        Assert.True(cut.Find("[data-my-list-remove-selected]").HasAttribute("disabled"));
        // Covers are tick targets: no "..." while selecting.
        Assert.Empty(cut.FindAll("[data-media-tile-actions]"));

        Tick(cut, "Film 1");
        Tick(cut, "Film 3");

        Assert.Equal("2 selected", cut.Find("[data-my-list-selected-count]").TextContent.Trim());
        Assert.False(cut.Find("[data-my-list-remove-selected]").HasAttribute("disabled"));

        Tick(cut, "Film 3");
        Assert.Equal("1 selected", cut.Find("[data-my-list-selected-count]").TextContent.Trim());
    }

    [Fact]
    public async Task PressingACoverWhileSelectingTicksItAndDoesNotOpenIt()
    {
        Seed(2);
        var cut = await RenderPageAsync();
        var opened = Nav.Uri;
        EnterSelect(cut);

        Tick(cut, "Film 1");

        Assert.Equal(opened, Nav.Uri);
        Assert.Single(cut.FindAll("[data-media-tile-pick].is-picked"));
    }

    [Fact]
    public async Task RemovingTheTickedTitlesLeavesSelectAndShowsTheUndoLine()
    {
        Seed(4);
        var cut = await RenderPageAsync();
        EnterSelect(cut);
        Tick(cut, "Film 1");
        Tick(cut, "Film 2");

        cut.Find("[data-my-list-remove-selected]").Click();

        cut.WaitForAssertion(() => Assert.Equal(["Film 3", "Film 4"], Titles(cut)));
        Assert.Equal(2, _removeCalls.Single().Count);
        Assert.Empty(cut.FindAll("[data-my-list-selbar]"));
        Assert.Contains("2 titles removed from My List.", UndoText(cut), StringComparison.Ordinal);
        Assert.Equal("2 titles", Count(cut));
    }

    [Fact]
    public async Task UndoPutsTheTitlesBackWithTheirOriginalDatesAndTheSameOrder()
    {
        Seed(4);
        var originals = _rows.Where(row => row.Title is "Film 1" or "Film 2").Select(row => row.SavedAt).Order().ToList();
        var cut = await RenderPageAsync();
        EnterSelect(cut);
        Tick(cut, "Film 1");
        Tick(cut, "Film 2");
        cut.Find("[data-my-list-remove-selected]").Click();
        cut.WaitForAssertion(() => Assert.Equal(2, Titles(cut).Length));

        cut.Find("[data-undo-note-action]").Click();

        cut.WaitForAssertion(() => Assert.Equal(["Film 1", "Film 2", "Film 3", "Film 4"], Titles(cut)));
        Assert.Equal(originals, _restoreCalls.Single().Select(item => item.SavedAt).Order().ToList());
        Assert.Empty(cut.FindAll("[data-undo-note-line]"));
    }

    [Fact]
    public async Task OneRemovedTitleReadsRemovedFromMyList()
    {
        Seed(3);
        var cut = await RenderPageAsync();
        EnterSelect(cut);
        Tick(cut, "Film 2");

        cut.Find("[data-my-list-remove-selected]").Click();

        cut.WaitForAssertion(() => Assert.Equal("Removed from My List.", UndoText(cut).Replace("Undo", string.Empty).Trim()));
    }

    [Fact]
    public async Task ARemovalFromACoversOwnMenuOffersUndoForTheTitleThatLeft()
    {
        Seed(3);
        var cut = await RenderPageAsync();
        var saved = Services.GetRequiredService<SavedItemService>();

        // What the cover's "Remove from My List" row does.
        await cut.InvokeAsync(() => saved.ToggleAsync(ProfileEntityKind.Movie, Id(2)));

        cut.WaitForAssertion(() => Assert.Equal(["Film 1", "Film 3"], Titles(cut)));
        Assert.Contains("Removed from My List.", UndoText(cut), StringComparison.Ordinal);
        cut.Find("[data-undo-note-action]").Click();
        cut.WaitForAssertion(() => Assert.Equal(3, Titles(cut).Length));
        Assert.Equal(Id(2), _restoreCalls.Single().Single().EntityId);
    }

    [Fact]
    public async Task EscapeAndCancelBothLeaveSelect()
    {
        Seed(3);
        var cut = await RenderPageAsync();

        EnterSelect(cut);
        cut.Find("[data-my-list-page]").KeyDown(new KeyboardEventArgs { Key = "Escape" });
        Assert.Empty(cut.FindAll("[data-my-list-selbar]"));
        Assert.NotEmpty(cut.FindAll("[data-my-list-select]"));

        EnterSelect(cut);
        Tick(cut, "Film 1");
        cut.Find("[data-my-list-cancel-select]").Click();
        Assert.Empty(cut.FindAll("[data-my-list-selbar]"));
        Assert.Empty(_removeCalls);
    }

    [Fact]
    public async Task AFailedRemovalSaysMyListCouldNotBeChangedAndKeepsTheCovers()
    {
        Seed(3);
        _removeFails = true;
        var cut = await RenderPageAsync();
        EnterSelect(cut);
        Tick(cut, "Film 1");

        cut.Find("[data-my-list-remove-selected]").Click();

        cut.WaitForAssertion(() => Assert.Contains("My List could not be changed. Try again.", cut.Find("[data-my-list-change-failed]").TextContent, StringComparison.Ordinal));
        Assert.Equal(3, Titles(cut).Length);
        Assert.Empty(cut.FindAll("[data-undo-note-line]"));
    }

    [Fact]
    public async Task AFailedUndoSaysSoInPlace()
    {
        Seed(3);
        var cut = await RenderPageAsync();
        EnterSelect(cut);
        Tick(cut, "Film 1");
        cut.Find("[data-my-list-remove-selected]").Click();
        cut.WaitForAssertion(() => Assert.Single(cut.FindAll("[data-undo-note-line]")));
        _restoreFails = true;

        cut.Find("[data-undo-note-action]").Click();

        cut.WaitForAssertion(() => Assert.Contains("My List could not be changed. Try again.", cut.Find("[data-undo-note-error]").TextContent, StringComparison.Ordinal));
    }

    [Fact]
    public async Task APickedKindThatEmptiesByRemovalReturnsToAll()
    {
        Seed(2, books: 1);
        var cut = await RenderPageAsync("/my-list?type=book");
        EnterSelect(cut);
        Tick(cut, "Novel 1");

        cut.Find("[data-my-list-remove-selected]").Click();

        cut.WaitForAssertion(() => Assert.Equal("2 titles", Count(cut)));
        Assert.DoesNotContain("type=", Nav.Uri, StringComparison.Ordinal);
    }

    // -- the page menu ---------------------------------------------------------------------------------------------------------

    private static string[] MenuRows(IRenderedComponent<MyListPage> cut)
    {
        cut.Find(".my-list-page-menu .app-overflow-menu__trigger, button.my-list-page-menu").Click();
        return cut.FindAll(".app-menu-item").Select(item => item.TextContent.Trim()).ToArray();
    }

    [Fact]
    public async Task OnADesktopThePageMenuHasOneRowRemoveFinished()
    {
        Seed(3);
        _finished = [new(ProfileEntityKind.Movie, Id(1), Now, null)];
        var cut = await RenderPageAsync();

        Assert.Equal(["Remove finished"], MenuRows(cut));
    }

    [Fact]
    public async Task RemoveFinishedIsGrayAndSaysNothingFinishedYetWhenNothingIsFinished()
    {
        Seed(3);
        var cut = await RenderPageAsync();

        Assert.Equal(["Nothing finished yet"], MenuRows(cut));
        Assert.True(cut.Find(".app-menu-item").HasAttribute("disabled"));
    }

    [Fact]
    public async Task RemoveFinishedRemovesOnlyTheFinishedTitlesAndOffersUndo()
    {
        Seed(4);
        _finished = [new(ProfileEntityKind.Movie, Id(2), Now, null), new(ProfileEntityKind.Movie, Id(4), Now, null)];
        var cut = await RenderPageAsync();
        MenuRows(cut);

        cut.Find(".app-menu-item").Click();

        cut.WaitForAssertion(() => Assert.Equal(["Film 1", "Film 3"], Titles(cut)));
        Assert.Equal([Id(2), Id(4)], _removeCalls.Single().Select(key => key.EntityId).Order().ToArray());
        Assert.Contains("2 titles removed from My List.", UndoText(cut), StringComparison.Ordinal);
    }

    // -- phone -----------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task OnAPhoneTheHeaderHasSortAndTheMenuAndNoSelectButton()
    {
        Seed(3, books: 1);
        var cut = await RenderPageAsync(formFactor: "phone");

        Assert.NotEmpty(cut.FindAll(".my-list-phonebar"));
        Assert.Empty(cut.FindAll("[data-my-list-select]"));
        Assert.Empty(cut.FindAll("[data-my-list-sort]"));
        // Chips are there and scroll sideways.
        Assert.Equal(["All 4", "Movies 3", "Books 1"], ChipTexts(cut));
    }

    [Fact]
    public async Task OnAPhoneTheSortButtonOpensASheetWithTheThreeSortsAndATickOnTheCurrentOne()
    {
        Seed(3);
        var cut = await RenderPageAsync("/my-list?sort=title", "phone");

        cut.Find(".my-list-phonebar button[aria-label='Sort']").Click();

        var sheet = cut.Find("dialog");
        var rows = sheet.QuerySelectorAll(".app-menu-item");
        Assert.Equal(["Date added", "Title", "Release year"], rows.Select(row => row.TextContent.Trim()).ToArray());
        Assert.Equal(["false", "true", "false"], rows.Select(row => row.GetAttribute("aria-checked")!).ToArray());

        rows[2].Click();
        cut.WaitForAssertion(() => Assert.Contains("sort=year", Nav.Uri, StringComparison.Ordinal));
    }

    [Fact]
    public async Task OnAPhoneThePageMenuSheetOffersSelectAndRemoveFinishedAndSelectShowsTheBottomBar()
    {
        Seed(3);
        var cut = await RenderPageAsync(formFactor: "phone");

        cut.Find(".my-list-phonebar button[aria-label='More actions for My List']").Click();
        var rows = cut.Find("dialog").QuerySelectorAll(".app-menu-item");
        Assert.Equal(["Select", "Nothing finished yet"], rows.Select(row => row.TextContent.Trim()).ToArray());

        rows[0].Click();

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-my-list-selbar]")));
        Tick(cut, "Film 2");
        Assert.Equal("1 selected", cut.Find("[data-my-list-selected-count]").TextContent.Trim());
        cut.Find("[data-my-list-remove-selected]").Click();
        cut.WaitForAssertion(() => Assert.Equal(["Film 1", "Film 3"], Titles(cut)));
        Assert.Contains("Removed from My List.", UndoText(cut), StringComparison.Ordinal);
    }
}
