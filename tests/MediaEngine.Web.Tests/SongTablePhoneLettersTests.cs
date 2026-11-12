using Bunit;
using MediaEngine.Web.Components.Listen;
using MediaEngine.Web.Services.Theming;
using MediaEngine.Web.Tests.Support;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;

namespace MediaEngine.Web.Tests;

/// <summary>The phone Songs list's letter headings and A to Z jump strip (spec music-table-and-reader-frame D21).</summary>
public sealed class SongTablePhoneLettersTests : AsyncBunitContext
{
    private readonly DeviceContextService _device = new(EngineApiClientStub.CreateDefault());

    public SongTablePhoneLettersTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddLogging();
        Services.AddSingleton(_device);
        Services.AddSingleton(EngineApiClientStub.CreateDefault());
    }

    private Task UseDeviceAsync(string formFactor, bool touch = false) =>
        _device.InitialiseAsync(new DeviceContextSnapshot(formFactor == "phone" ? "mobile" : "web", formFactor, touch, FinePointer: !touch, Hover: !touch));

    private static SongRow Row(string title, string artist) => new()
    {
        Id = Guid.NewGuid().ToString("D"),
        Source = SongRowSource.Work,
        Title = title,
        Artist = artist,
        DurationSeconds = 180,
        WorkId = Guid.NewGuid(),
    };

    private static List<SongRow> Songs() =>
    [
        Row("Africa", "Toto"),
        Row("Aerials", "System Of A Down"),
        Row("Bohemian Rhapsody", "Queen"),
        Row("99 Problems", "Jay-Z"),
        Row("Zombie", "The Cranberries"),
    ];

    private IRenderedComponent<SongTable> RenderTable(IReadOnlyList<SongRow> rows, SongSortState? sort, bool letters = true, SongTablePlace place = SongTablePlace.Songs) =>
        Render<SongTable>(parameters =>
        {
            parameters.Add(table => table.Place, place).Add(table => table.Rows, rows).Add(table => table.PhoneLetterIndex, letters);
            if (sort is not null)
            {
                parameters.Add(table => table.Sort, sort);
            }
        });

    [Fact]
    public async Task SortedByTitleOnAPhone_TheListGroupsUnderLetterHeadingsWithOneStripLetterPerHeading()
    {
        await UseDeviceAsync("phone", touch: true);

        var cut = RenderTable(Songs(), new SongSortState(SongColumn.Song, false));

        Assert.Equal(["#", "A", "B", "Z"], cut.FindAll(".song-table__letter").Select(heading => heading.TextContent.Trim()));
        Assert.Equal(5, cut.FindAll(".song-table__prow").Count);
        var strip = cut.FindAll(".app-letter-strip__letter");
        Assert.Equal(27, strip.Count);
        Assert.Equal(["#", "A", "B", "Z"], strip.Where(letter => !letter.HasAttribute("disabled")).Select(letter => letter.TextContent.Trim()).Order(StringComparer.Ordinal));
        Assert.Equal("Letter index", cut.Find(".app-letter-strip").GetAttribute("aria-label"));
    }

    [Fact]
    public async Task SortedByArtist_HeadingsFollowTheArtistsFirstLetter()
    {
        await UseDeviceAsync("phone", touch: true);

        var cut = RenderTable(Songs(), new SongSortState(SongColumn.Artist, false));

        Assert.Equal(["J", "Q", "S", "T"], cut.FindAll(".song-table__letter").Select(heading => heading.TextContent.Trim()));
    }

    [Fact]
    public async Task SortedByTitleDescending_HeadingsRunBackwards()
    {
        await UseDeviceAsync("phone", touch: true);

        var cut = RenderTable(Songs(), new SongSortState(SongColumn.Song, true));

        Assert.Equal(["Z", "B", "A", "#"], cut.FindAll(".song-table__letter").Select(heading => heading.TextContent.Trim()));
    }

    [Fact]
    public async Task SortedByDateAdded_ThereAreNoHeadingsAndNoStrip()
    {
        await UseDeviceAsync("phone", touch: true);

        var cut = RenderTable(Songs(), new SongSortState(SongColumn.DateAdded, true));

        Assert.Empty(cut.FindAll(".song-table__letter"));
        Assert.Empty(cut.FindAll(".song-table__strip"));
    }

    [Fact]
    public async Task WhenThePageDoesNotAskForLettersOrOnADesktop_ThereAreNoneEvenSortedByTitle()
    {
        await UseDeviceAsync("phone", touch: true);
        Assert.Empty(RenderTable(Songs(), new SongSortState(SongColumn.Song, false), letters: false).FindAll(".song-table__strip"));

        await UseDeviceAsync("desktop");
        var desktop = RenderTable(Songs(), new SongSortState(SongColumn.Song, false));
        Assert.Empty(desktop.FindAll(".song-table__letter"));
        Assert.Empty(desktop.FindAll(".song-table__strip"));
    }

    [Fact]
    public async Task AnAlbumsOwnPageNeverGetsLetters()
    {
        await UseDeviceAsync("phone", touch: true);

        var cut = RenderTable(Songs(), new SongSortState(SongColumn.Song, false), place: SongTablePlace.AlbumPage);

        Assert.Empty(cut.FindAll(".song-table__strip"));
    }

    [Fact]
    public async Task TouchingALetter_JumpsToItsHeadingByItsPlaceInTheList()
    {
        await UseDeviceAsync("phone", touch: true);
        var module = JSInterop.SetupModule("./js/song-table.js");
        module.Mode = JSRuntimeMode.Loose;
        var cut = RenderTable(Songs(), new SongSortState(SongColumn.Song, false));

        // Items in order: # heading, 99 Problems, A heading, Aerials, Africa, B heading, Bohemian Rhapsody, Z heading, Zombie.
        cut.FindAll(".app-letter-strip__letter").Single(letter => letter.TextContent.Trim() == "B").Click();

        cut.WaitForAssertion(() =>
        {
            var call = Assert.Single(module.Invocations["scrollToIndex"]);
            Assert.Equal(5, call.Arguments[1]);
            Assert.Equal(64, call.Arguments[2]);
        });
    }

    [Fact]
    public async Task ALetterWithNoSongsIsDimmedAndDoesNothing()
    {
        await UseDeviceAsync("phone", touch: true);
        var module = JSInterop.SetupModule("./js/song-table.js");
        module.Mode = JSRuntimeMode.Loose;
        var cut = RenderTable(Songs(), new SongSortState(SongColumn.Song, false));

        var empty = cut.FindAll(".app-letter-strip__letter").Single(letter => letter.TextContent.Trim() == "M");
        Assert.True(empty.HasAttribute("disabled"));
        Assert.Contains("is-empty", empty.ClassList);
    }
}
