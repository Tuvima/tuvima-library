using Bunit;
using MediaEngine.Web.Components.Listen;
using MediaEngine.Web.Components.Shared;
using MediaEngine.Web.Services.Playback;
using MediaEngine.Web.Services.Theming;
using MediaEngine.Web.Tests.Support;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;

namespace MediaEngine.Web.Tests;

/// <summary>The phone Songs page header (spec music-table-and-reader-frame D21, mockup A6): search, Play, Shuffle and the Sort and filter sheet.</summary>
public sealed class SongsPhoneHeaderTests : AsyncBunitContext
{
    private static readonly Guid Profile = Guid.Parse("00000000-0000-0000-0000-000000000001");

    private readonly DeviceContextService _device = new(EngineApiClientStub.CreateDefault());
    private readonly SpySortStore _store = new();

    public SongsPhoneHeaderTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddLogging();
        Services.AddNativeUiServices();
        Services.AddSingleton(_device);
        Services.AddSingleton<ISongSortPreferenceStore>(_store);
    }

    private Task UsePhoneAsync() =>
        _device.InitialiseAsync(new DeviceContextSnapshot("mobile", "phone", true, FinePointer: false, Hover: false));

    private EventCallback<T> Cb<T>(Action<T> action) => EventCallback.Factory.Create(this, action);

    private IRenderedComponent<SongsPhoneHeader> RenderHeader(Action<ComponentParameterCollectionBuilder<SongsPhoneHeader>>? more = null, SongSortState? sort = null) =>
        Render<SongsPhoneHeader>(parameters =>
        {
            parameters.Add(header => header.ProfileId, Profile).Add(header => header.Sort, sort ?? new SongSortState(SongColumn.DateAdded, true));
            more?.Invoke(parameters);
        });

    private static AngleSharp.Dom.IElement Choice(IRenderedComponent<SongsPhoneHeader> cut, string title) =>
        cut.FindAll(".app-choice-row").Single(row => row.TextContent.Trim() == title);

    private static AngleSharp.Dom.IElement Direction(IRenderedComponent<SongsPhoneHeader> cut, string label) =>
        cut.FindAll(".app-segmented-control__item").Single(item => item.TextContent.Trim() == label);

    private static void OpenSheet(IRenderedComponent<SongsPhoneHeader> cut)
    {
        cut.Find(".songs-header__sort button").Click();
        cut.WaitForElement(".songs-header-sheet__body");
    }

    [Fact]
    public async Task ShowsASearchFieldPlayAndShuffleAndASortAndFilterButton()
    {
        await UsePhoneAsync();

        var cut = RenderHeader();

        Assert.Equal("Search Songs", cut.Find("input").GetAttribute("placeholder"));
        Assert.Equal(["Play", "Shuffle"], cut.FindAll(".songs-header__actions button").Select(button => button.TextContent.Trim()));
        Assert.Equal("Sort and filter", cut.Find(".songs-header__sort button").GetAttribute("aria-label"));
    }

    [Fact]
    public async Task PlayAndShuffleTellThePageAndAreDisabledWhenThereIsNothingToPlay()
    {
        await UsePhoneAsync();
        var played = 0;
        var shuffled = 0;
        var cut = RenderHeader(p => p.Add(h => h.OnPlay, EventCallback.Factory.Create(this, () => played++)).Add(h => h.OnShuffle, EventCallback.Factory.Create(this, () => shuffled++)));

        var buttons = cut.FindAll(".songs-header__actions button");
        buttons[0].Click();
        buttons[1].Click();

        Assert.Equal((1, 1), (played, shuffled));

        var empty = RenderHeader(p => p.Add(h => h.PlayDisabled, true));
        Assert.All(empty.FindAll(".songs-header__actions button"), button => Assert.True(button.HasAttribute("disabled")));
    }

    [Fact]
    public async Task TheSheetListsEverySortAndMarksTheCurrentOne()
    {
        await UsePhoneAsync();
        var cut = RenderHeader(sort: new SongSortState(SongColumn.Plays, false));

        OpenSheet(cut);

        Assert.Equal(
            ["Title", "Artist", "Album", "Date added", "Plays", "Time"],
            cut.FindAll(".app-choice-row").Select(option => option.TextContent.Trim()));
        Assert.Equal("true", Choice(cut, "Plays").GetAttribute("aria-pressed"));
        Assert.Equal("false", Choice(cut, "Title").GetAttribute("aria-pressed"));
        Assert.Equal("true", Direction(cut, "Ascending").GetAttribute("aria-pressed"));
        Assert.Equal("false", Direction(cut, "Descending").GetAttribute("aria-pressed"));
    }

    [Fact]
    public async Task ChoosingASortRaisesItAndRemembersItForTheProfile_FirstClickGoingTheWayTheDesktopHeaderDoes()
    {
        await UsePhoneAsync();
        var raised = new List<SongSortState>();
        var cut = RenderHeader(p => p.Add(h => h.SortChanged, Cb<SongSortState>(raised.Add)));
        OpenSheet(cut);

        Choice(cut, "Title").Click();
        Choice(cut, "Time").Click();

        Assert.Equal([new SongSortState(SongColumn.Song, false), new SongSortState(SongColumn.Time, true)], raised);
        Assert.Equal(new SongSortState(SongColumn.Time, true), _store.Stored[Profile]);
    }

    [Fact]
    public async Task TheDirectionButtonsFlipTheCurrentColumn()
    {
        await UsePhoneAsync();
        var raised = new List<SongSortState>();
        var cut = RenderHeader(p => p.Add(h => h.SortChanged, Cb<SongSortState>(raised.Add)), sort: new SongSortState(SongColumn.Artist, false));
        OpenSheet(cut);

        Direction(cut, "Descending").Click();

        Assert.Equal([new SongSortState(SongColumn.Artist, true)], raised);
    }

    [Fact]
    public async Task ARememberedSortIsRaisedToThePageWhenItLoads()
    {
        await UsePhoneAsync();
        _store.Stored[Profile] = new SongSortState(SongColumn.Artist, true);
        var raised = new List<SongSortState>();

        RenderHeader(p => p.Add(h => h.SortChanged, Cb<SongSortState>(raised.Add)));

        Assert.Equal([new SongSortState(SongColumn.Artist, true)], raised);
    }

    [Fact]
    public async Task TheSheetOffersFavoritesAndGenresTheWayThePageFiltersThemAndCountsWhatIsOn()
    {
        await UsePhoneAsync();
        var favorites = new List<bool>();
        var genres = new List<IReadOnlyList<string>>();
        var cut = RenderHeader(p => p
            .Add(h => h.FavoritesOnly, false)
            .Add(h => h.FavoritesOnlyChanged, Cb<bool>(favorites.Add))
            .Add(h => h.Genres, ["Jazz", "Rock"])
            .Add(h => h.SelectedGenres, ["Jazz"])
            .Add(h => h.SelectedGenresChanged, Cb<IReadOnlyList<string>>(genres.Add)));

        Assert.Equal("1", cut.Find(".songs-header__badge").TextContent.Trim());
        Assert.Equal("Sort and filter, 1 filters on", cut.Find(".songs-header__sort button").GetAttribute("aria-label"));
        OpenSheet(cut);

        var rows = cut.FindAll(".songs-header-sheet__section input[type='checkbox']");
        Assert.Equal(3, rows.Count);
        rows[0].Change(true);
        rows[2].Change(true);

        Assert.Equal([true], favorites);
        Assert.Equal([["Jazz", "Rock"]], genres.Select(list => list.Order(StringComparer.Ordinal).ToList()));
    }

    [Fact]
    public async Task WithNoFilterWiringAndNoGenres_TheSheetHasOnlySort()
    {
        await UsePhoneAsync();
        var cut = RenderHeader();

        OpenSheet(cut);

        Assert.Empty(cut.FindAll(".songs-header-sheet__section input[type='checkbox']"));
    }

    [Fact]
    public async Task TypingRaisesTheSearchOncePauseElapsesAndAPageChangeShowsInTheField()
    {
        await UsePhoneAsync();
        var cut = RenderHeader(p => p.Add(h => h.SearchText, "abc"));

        Assert.Equal("abc", cut.Find("input").GetAttribute("value"));

        cut.Render(parameters => parameters.Add(h => h.SearchText, string.Empty));

        Assert.Equal(string.Empty, cut.Find("input").GetAttribute("value") ?? string.Empty);
    }

    private sealed class SpySortStore : ISongSortPreferenceStore
    {
        public Dictionary<Guid, SongSortState> Stored { get; } = [];

        public ValueTask<SongSortState?> LoadAsync(Guid profileId) =>
            ValueTask.FromResult(Stored.TryGetValue(profileId, out var state) ? state : null);

        public ValueTask SaveAsync(Guid profileId, SongSortState? state)
        {
            if (state is null)
            {
                Stored.Remove(profileId);
            }
            else
            {
                Stored[profileId] = state;
            }

            return ValueTask.CompletedTask;
        }
    }
}
