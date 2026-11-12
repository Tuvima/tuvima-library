using AngleSharp.Dom;
using Bunit;
using MediaEngine.Web.Components.Listen;
using MediaEngine.Web.Components.Shared;
using MediaEngine.Web.Services.Playback;
using MediaEngine.Web.Services.Theming;
using MediaEngine.Web.Tests.Support;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;

namespace MediaEngine.Web.Tests;

/// <summary>
/// The shared song table (spec music-table-and-reader-frame WP2): Songs, album page and playlist modes, the phone list, a missing
/// row, sort, the Columns menu, keyboard and drag reorder, and the touch path that has no hover-only controls.
/// </summary>
public sealed class SongTableTests : AsyncBunitContext
{
    private static readonly Guid Profile = Guid.Parse("00000000-0000-0000-0000-000000000001");

    private readonly DeviceContextService _device = new(EngineApiClientStub.CreateDefault());
    private readonly SpyPreferenceStore _store = new();

    public SongTableTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddLogging();
        Services.AddSingleton(_device);
        Services.AddSingleton(EngineApiClientStub.CreateDefault());
        Services.AddSingleton<ISongTablePreferenceStore>(_store);
    }

    private Task UseDeviceAsync(string formFactor, bool touch = false, bool fine = true, bool hover = true) =>
        _device.InitialiseAsync(new DeviceContextSnapshot(formFactor == "phone" ? "mobile" : "web", formFactor, touch, FinePointer: fine, Hover: hover));

    private static SongRow Row(
        string title,
        string? artist = "The Artist",
        string? album = "The Album",
        int? number = null,
        bool owned = true,
        bool unnumbered = false,
        DateTimeOffset? added = null,
        int plays = 0,
        string? genre = null,
        double seconds = 185,
        bool hasTargets = true,
        bool playlist = false,
        string? cover = null) => new()
        {
            Id = Guid.NewGuid().ToString("D"),
            Source = playlist ? SongRowSource.PlaylistItem : SongRowSource.Work,
            Title = title,
            Artist = artist,
            Album = album,
            Number = number,
            IsOwned = owned,
            IsUnnumbered = unnumbered,
            DateAdded = added ?? new DateTimeOffset(2026, 3, 4, 0, 0, 0, TimeSpan.Zero),
            Plays = plays,
            Genre = genre,
            DurationSeconds = seconds,
            CoverUrl = cover,
            WorkId = Guid.NewGuid(),
            PlaylistItemId = playlist ? Guid.NewGuid() : null,
            CollectionId = hasTargets ? Guid.NewGuid() : null,
            ArtistPersonId = hasTargets ? Guid.NewGuid() : null,
        };

    private static List<SongRow> ThreeSongs() =>
    [
        Row("Beta", artist: "Zed", added: new DateTimeOffset(2026, 1, 2, 0, 0, 0, TimeSpan.Zero), plays: 5),
        Row("Alpha", artist: "Mia", added: new DateTimeOffset(2026, 3, 9, 0, 0, 0, TimeSpan.Zero), plays: 9),
        Row("Gamma", artist: "Abe", added: new DateTimeOffset(2026, 2, 1, 0, 0, 0, TimeSpan.Zero), plays: 1),
    ];

    private EventCallback<T> Cb<T>(Action<T> action) => EventCallback.Factory.Create(this, action);

    private IRenderedComponent<SongTable> RenderTable(SongTablePlace place, IReadOnlyList<SongRow> rows, Action<ComponentParameterCollectionBuilder<SongTable>>? more = null) =>
        Render<SongTable>(parameters =>
        {
            parameters.Add(table => table.Place, place).Add(table => table.Rows, rows);
            more?.Invoke(parameters);
        });

    private static IReadOnlyList<string> Headers(IRenderedComponent<SongTable> cut) =>
        [.. cut.FindAll(".song-table__head [role='columnheader']").Select(header => header.TextContent.Trim())];

    private static IReadOnlyList<string> Titles(IRenderedComponent<SongTable> cut) =>
        [.. cut.FindAll(".song-table__title").Select(title => title.TextContent.Trim())];

    private static IElement RowElement(IRenderedComponent<SongTable> cut, SongRow row) => cut.Find($"[data-song-row-id='{row.Id}']");

    private static IElement ColumnsButton(IRenderedComponent<SongTable> cut) =>
        cut.Find(".song-table__head-cell--actions button[aria-haspopup='menu']");

    private static IReadOnlyList<string> OpenMenu(IRenderedComponent<SongTable> cut, IElement trigger)
    {
        trigger.Click();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[role='menuitem']")));
        return [.. cut.FindAll("[role='menuitem']").Select(item => item.TextContent.Trim())];
    }

    // -- The three modes ----------------------------------------------------------------------------

    [Fact]
    public async Task Songs_ShowTheOneColumnOrderWithFavoriteRightBeforeTime()
    {
        await UseDeviceAsync("desktop");

        var cut = RenderTable(SongTablePlace.Songs, ThreeSongs());

        Assert.Equal(["#", "Song", "Artist", "Album", "Date added", "Plays", "Favorite", "Time", "Actions"], Headers(cut));
        Assert.Equal(3, cut.FindAll(".song-table__row").Count);
        Assert.NotEmpty(cut.FindAll(".song-table__resizer"));
    }

    [Fact]
    public async Task AlbumPage_ShowsNumberSongStatusFavoriteTimeAndArtistOnlyWhenTheAlbumHasSeveral()
    {
        await UseDeviceAsync("desktop");
        var oneArtist = new List<SongRow> { Row("One", number: 1), Row("Two", number: 2) };
        var manyArtists = new List<SongRow> { Row("One", artist: "A", number: 1), Row("Two", artist: "B", number: 2) };

        var single = RenderTable(SongTablePlace.AlbumPage, oneArtist);
        Assert.Equal(["#", "Song", "Status", "Favorite", "Time", "Actions"], Headers(single));

        var compilation = RenderTable(SongTablePlace.AlbumPage, manyArtists);
        Assert.Equal(["#", "Song", "Artist", "Status", "Favorite", "Time", "Actions"], Headers(compilation));
    }

    [Fact]
    public async Task AlbumPage_KeepsTrackOrderAndPutsTheUnnumberedGroupLast()
    {
        await UseDeviceAsync("desktop");
        var rows = new List<SongRow>
        {
            Row("Loose", unnumbered: true),
            Row("Second", number: 2),
            Row("First", number: 1),
        };

        var cut = RenderTable(SongTablePlace.AlbumPage, rows);

        Assert.Equal(["First", "Second", "Loose"], Titles(cut));
        Assert.Equal("Unnumbered", cut.Find(".song-table__group").TextContent.Trim());
    }

    [Fact]
    public async Task Playlist_ShowsArtistAlbumFavoriteTimeInSavedOrderWithNoSortArrows()
    {
        await UseDeviceAsync("desktop");
        var rows = new List<SongRow> { Row("Zulu", playlist: true), Row("Alpha", playlist: true), Row("Mike", playlist: true) };

        var cut = RenderTable(SongTablePlace.Playlist, rows);

        Assert.Equal(["#", "Song", "Artist", "Album", "Favorite", "Time", "Actions"], Headers(cut));
        Assert.Equal(["Zulu", "Alpha", "Mike"], Titles(cut));
        Assert.Empty(cut.FindAll(".song-table__head-cell[aria-sort]"));
        Assert.Empty(cut.FindAll(".song-table__head-cell:not(.song-table__head-cell--actions) button"));
    }

    // -- Row content ------------------------------------------------------------------------------------

    [Fact]
    public async Task TheNowPlayingRowHasTheEqualizerAnAccentTitleAndNoNumber()
    {
        await UseDeviceAsync("desktop");
        var rows = ThreeSongs();

        var cut = RenderTable(SongTablePlace.Songs, rows, p => p.Add(t => t.CurrentRowId, rows[1].Id).Add(t => t.IsPlaying, true));

        var playing = RowElement(cut, rows[1]);
        Assert.Contains("is-playing", playing.ClassName);
        Assert.NotEmpty(playing.QuerySelectorAll(".playback-activity-mark"));
        Assert.Contains("is-current", playing.QuerySelector(".song-table__title")!.ClassName);
        Assert.Empty(playing.QuerySelectorAll(".song-table__position"));
        Assert.Empty(RowElement(cut, rows[0]).QuerySelectorAll(".playback-activity-mark"));
    }

    [Fact]
    public async Task TheHeartIsOutlineUntilSavedFilledOnceSavedAndSavesThroughTheHost()
    {
        await UseDeviceAsync("desktop");
        var rows = ThreeSongs();
        var toggled = new List<SongRow>();

        var cut = RenderTable(SongTablePlace.Songs, rows, p => p
            .Add(t => t.FavoriteIds, new HashSet<string> { rows[0].Id })
            .Add(t => t.OnToggleFavorite, Cb<SongRow>(toggled.Add)));

        Assert.Equal("true", RowElement(cut, rows[0]).QuerySelector(".song-table__cell--favorite button")!.GetAttribute("aria-pressed"));
        Assert.Equal("false", RowElement(cut, rows[1]).QuerySelector(".song-table__cell--favorite button")!.GetAttribute("aria-pressed"));

        RowElement(cut, rows[1]).QuerySelector(".song-table__cell--favorite button")!.Click();

        Assert.Equal([rows[1]], toggled);
    }

    [Fact]
    public async Task AMissingRowIsDimmedHasAMissingChipAndNoActions()
    {
        await UseDeviceAsync("desktop");
        var owned = Row("Owned", number: 1);
        var missing = Row("Missing song", number: 2, owned: false);
        var played = new List<SongPlayRequest>();

        var cut = RenderTable(SongTablePlace.AlbumPage, [owned, missing], p => p
            .Add(t => t.OnPlay, Cb<SongPlayRequest>(played.Add))
            .Add(t => t.OnQueue, Cb<SongRow>(_ => { }))
            .Add(t => t.ShowSelection, true)
            .Add(t => t.CanEdit, true));

        var row = RowElement(cut, missing);
        Assert.Contains("is-missing", row.ClassName);
        Assert.Equal("true", row.GetAttribute("aria-disabled"));
        Assert.Contains("Missing", row.QuerySelector(".song-table__chip--missing")!.TextContent);
        Assert.Empty(row.QuerySelectorAll("button"));
        Assert.Empty(row.QuerySelectorAll("input"));

        row.Click();
        Assert.Empty(played);

        RowElement(cut, owned).Click();
        Assert.Equal(owned, Assert.Single(played).Start);
    }

    [Fact]
    public async Task ClickingARowPlaysItWithTheOwnedRowsAsSorted()
    {
        await UseDeviceAsync("desktop");
        var rows = new List<SongRow> { Row("A", number: 1), Row("B", number: 2, owned: false), Row("C", number: 3) };
        SongPlayRequest? request = null;

        var cut = RenderTable(SongTablePlace.AlbumPage, rows, p => p.Add(t => t.OnPlay, Cb<SongPlayRequest>(r => request = r)));
        RowElement(cut, rows[2]).Click();

        Assert.NotNull(request);
        Assert.Equal(rows[2], request.Start);
        Assert.Equal(["A", "C"], request.Rows.Select(r => r.Title));
    }

    // -- Sort ------------------------------------------------------------------------------------------

    [Fact]
    public async Task SortStartsNewestFirstAndAHeaderClickSortsAscendingThenFlips()
    {
        await UseDeviceAsync("desktop");
        var sorts = new List<SongSortState>();

        var cut = RenderTable(SongTablePlace.Songs, ThreeSongs(), p => p.Add(t => t.OnSortChanged, Cb<SongSortState>(sorts.Add)));
        Assert.Equal(["Alpha", "Gamma", "Beta"], Titles(cut));

        cut.FindAll(".song-table__head-cell button").Single(button => button.TextContent.Trim() == "Artist").Click();
        Assert.Equal(["Gamma", "Alpha", "Beta"], Titles(cut));
        Assert.Equal("Artist", cut.Find(".song-table__head-cell[aria-sort='ascending']").TextContent.Trim());

        cut.FindAll(".song-table__head-cell button").Single(button => button.TextContent.Trim() == "Artist").Click();
        Assert.Equal(["Beta", "Alpha", "Gamma"], Titles(cut));
        Assert.Equal([new SongSortState(SongColumn.Artist, false), new SongSortState(SongColumn.Artist, true)], sorts);
    }

    [Fact]
    public async Task PlaysSortsHighestFirstOnTheFirstClick()
    {
        await UseDeviceAsync("desktop");

        var cut = RenderTable(SongTablePlace.Songs, ThreeSongs());
        cut.FindAll(".song-table__head-cell button").Single(button => button.TextContent.Trim() == "Plays").Click();

        Assert.Equal(["Alpha", "Beta", "Gamma"], Titles(cut));
    }

    [Fact]
    public async Task ASortChosenOutsideTheTableIsFollowed()
    {
        await UseDeviceAsync("desktop");
        var rows = ThreeSongs();

        var cut = RenderTable(SongTablePlace.Songs, rows, p => p.Add(t => t.Sort, new SongSortState(SongColumn.Song, false)));

        Assert.Equal(["Alpha", "Beta", "Gamma"], Titles(cut));
    }

    // -- Columns ---------------------------------------------------------------------------------------

    [Fact]
    public async Task TheColumnsMenuTicksAndUnticksColumnsAndResetPutsBackTheDefaults()
    {
        await UseDeviceAsync("desktop");

        var cut = RenderTable(SongTablePlace.Songs, ThreeSongs(), p => p.Add(t => t.ProfileId, Profile));
        ColumnsButton(cut).Click();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(".song-table__columns-menu")));

        SetColumn(cut, "Genre", true);
        SetColumn(cut, "Artist", false);
        Assert.Contains("Genre", Headers(cut));
        Assert.DoesNotContain("Artist", Headers(cut));
        Assert.Equal(2, _store.Saves.Count);

        cut.FindAll("[role='menuitem']").Single(item => item.TextContent.Trim() == "Reset columns").Click();

        // A menu action waits for the menu to close before it runs.
        cut.WaitForAssertion(() => Assert.Equal(["#", "Song", "Artist", "Album", "Date added", "Plays", "Favorite", "Time", "Actions"], Headers(cut)));
        Assert.True(_store.Saves[^1].Choice.IsDefault);
    }

    private static void SetColumn(IRenderedComponent<SongTable> cut, string label, bool on) =>
        cut.FindAll(".song-table__columns-menu label")
            .Single(row => row.TextContent.Trim() == label)
            .QuerySelector("input")!
            .Change(on);

    [Fact]
    public async Task ARememberedChoiceIsAppliedForTheProfileAndPlace()
    {
        await UseDeviceAsync("desktop");
        _store.Stored[(Profile, SongTablePlace.Songs)] = new SongTableChoice { Shown = new HashSet<SongColumn> { SongColumn.Genre }, Hidden = new HashSet<SongColumn> { SongColumn.Plays } };

        var cut = RenderTable(SongTablePlace.Songs, ThreeSongs(), p => p.Add(t => t.ProfileId, Profile));

        cut.WaitForAssertion(() => Assert.Equal(["#", "Song", "Artist", "Album", "Date added", "Genre", "Favorite", "Time", "Actions"], Headers(cut)));
    }

    [Fact]
    public async Task WithoutAProfileTheChoiceLastsForTheVisitAndIsNeverSaved()
    {
        await UseDeviceAsync("desktop");

        var cut = RenderTable(SongTablePlace.Songs, ThreeSongs());
        ColumnsButton(cut).Click();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(".song-table__columns-menu")));
        SetColumn(cut, "Plays", false);

        Assert.DoesNotContain("Plays", Headers(cut));
        Assert.Empty(_store.Saves);
    }

    [Fact]
    public async Task WhenADragEndsTheBrowserTellsTheTableTheFinalWidthAndItChangesTheLayoutAndSavesOnce()
    {
        await UseDeviceAsync("desktop");

        var cut = RenderTable(SongTablePlace.Songs, ThreeSongs(), p => p.Add(t => t.ProfileId, Profile));
        Assert.Contains("minmax(64px, 180px)", cut.Find("[style*='--song-table-columns']").GetAttribute("style"));
        Assert.Empty(_store.Saves);

        await cut.InvokeAsync(() => cut.Instance.ColumnResized("artist", 220));

        Assert.Contains(" 220px ", cut.Find("[style*='--song-table-columns']").GetAttribute("style"));
        var saved = Assert.Single(_store.Saves);
        Assert.Equal(220, saved.Choice.Widths[SongColumn.Artist]);
    }

    [Fact]
    public async Task AFinalWidthBelowTheMinimumIsKeptAtTheMinimum()
    {
        await UseDeviceAsync("desktop");

        var cut = RenderTable(SongTablePlace.Songs, ThreeSongs(), p => p.Add(t => t.ProfileId, Profile));
        await cut.InvokeAsync(() => cut.Instance.ColumnResized("artist", 10));

        Assert.Contains(" 64px ", cut.Find("[style*='--song-table-columns']").GetAttribute("style"));
        Assert.Equal(64, Assert.Single(_store.Saves).Choice.Widths[SongColumn.Artist]);
    }

    [Fact]
    public async Task AFinalWidthForAColumnThatCannotBeResizedOrDoesNotExistChangesNothing()
    {
        await UseDeviceAsync("desktop");

        var cut = RenderTable(SongTablePlace.Songs, ThreeSongs(), p => p.Add(t => t.ProfileId, Profile));
        await cut.InvokeAsync(() => cut.Instance.ColumnResized("tick", 300));
        await cut.InvokeAsync(() => cut.Instance.ColumnResized("nonsense", 300));

        Assert.Empty(_store.Saves);
    }

    [Fact]
    public async Task ADoubleClickPutsOneColumnBackToItsDefaultWidthAndSaves()
    {
        await UseDeviceAsync("desktop");
        _store.Stored[(Profile, SongTablePlace.Songs)] = new SongTableChoice
        {
            Widths = new Dictionary<SongColumn, int> { [SongColumn.Artist] = 260, [SongColumn.Album] = 300 },
        };

        var cut = RenderTable(SongTablePlace.Songs, ThreeSongs(), p => p.Add(t => t.ProfileId, Profile));
        cut.WaitForAssertion(() => Assert.Contains(" 260px ", cut.Find("[style*='--song-table-columns']").GetAttribute("style")));

        await cut.InvokeAsync(() => cut.Instance.ColumnResetWidth("artist"));

        var style = cut.Find("[style*='--song-table-columns']").GetAttribute("style");
        Assert.Contains("minmax(64px, 180px)", style);
        Assert.Contains(" 300px ", style);
        var saved = Assert.Single(_store.Saves);
        Assert.False(saved.Choice.Widths.ContainsKey(SongColumn.Artist));
        Assert.Equal(300, saved.Choice.Widths[SongColumn.Album]);
    }

    [Fact]
    public async Task TheSongColumnStaysFlexibleUntilItsWidthIsSetThenKeepsThatExactWidthUntilReset()
    {
        await UseDeviceAsync("desktop");
        var cut = RenderTable(SongTablePlace.Songs, ThreeSongs(), p => p.Add(t => t.ProfileId, Profile));
        string Columns() => cut.Find("[style*='--song-table-columns']").GetAttribute("style")!;

        Assert.Contains("minmax(360px, 1fr)", Columns());

        await cut.InvokeAsync(() => cut.Instance.ColumnResized("song", 300));
        Assert.DoesNotContain("1fr", Columns());
        Assert.Contains(" 300px ", Columns());

        await cut.InvokeAsync(() => cut.Instance.ColumnResized("song", 500));
        Assert.Contains(" 500px ", Columns());

        await cut.InvokeAsync(() => cut.Instance.ColumnResetWidth("song"));
        Assert.Contains("minmax(360px, 1fr)", Columns());
    }

    [Fact]
    public async Task EachResizableHeaderHasOneHandleNamingItsColumnAndLimitsAndTheTickColumnHasNone()
    {
        await UseDeviceAsync("desktop");

        var cut = RenderTable(SongTablePlace.Songs, ThreeSongs(), p => p
            .Add(t => t.ShowSelection, true)
            .Add(t => t.CanEdit, true));

        Assert.NotEmpty(cut.FindAll(".song-table__head-cell--tick"));
        var handles = cut.FindAll(".song-table__resizer");
        Assert.Equal(
            ["number", "song", "artist", "album", "dateAdded", "plays", "favorite", "time"],
            handles.Select(handle => handle.GetAttribute("data-column")));
        Assert.All(handles, handle => Assert.Equal(SongTableColumns.MaxWidth.ToString(System.Globalization.CultureInfo.InvariantCulture), handle.GetAttribute("data-max")));
        Assert.Equal("40", handles[0].GetAttribute("data-min"));
        Assert.Equal("64", cut.Find("[aria-label='Resize Artist column']").GetAttribute("data-min"));
        Assert.Empty(cut.FindAll(".song-table__head-cell--tick .song-table__resizer"));
    }

    [Fact]
    public async Task TheTableHandsItsRootAndItselfToTheBrowserScriptToWireTheDrag()
    {
        await UseDeviceAsync("desktop");
        var module = JSInterop.SetupModule("./js/song-table.js");
        module.Mode = JSRuntimeMode.Loose;

        var cut = RenderTable(SongTablePlace.Songs, ThreeSongs());

        cut.WaitForAssertion(() =>
        {
            var wiring = Assert.Single(module.Invocations["wireResize"]);
            Assert.IsType<DotNetObjectReference<SongTable>>(wiring.Arguments[1]);
        });
    }

    [Fact]
    public async Task AResizeHandleMovesByTheKeyboard()
    {
        await UseDeviceAsync("desktop");

        var cut = RenderTable(SongTablePlace.Songs, ThreeSongs(), p => p.Add(t => t.ProfileId, Profile));
        cut.Find("[aria-label='Resize Artist column']").KeyDown(new KeyboardEventArgs { Key = "ArrowRight" });

        Assert.Equal(188, Assert.Single(_store.Saves).Choice.Widths[SongColumn.Artist]);
    }

    // -- Menu ------------------------------------------------------------------------------------------

    [Fact]
    public async Task TheRowMenuHasOneOrderAndLeavesOutWhatDoesNotApply()
    {
        await UseDeviceAsync("desktop");
        var rows = new List<SongRow> { Row("One", playlist: true) };

        var cut = RenderTable(SongTablePlace.Playlist, rows, p => p
            .Add(t => t.OnPlayNext, Cb<SongRow>(_ => { }))
            .Add(t => t.OnQueue, Cb<SongRow>(_ => { }))
            .Add(t => t.OnRemoveFromPlaylist, Cb<SongRow>(_ => { }))
            .Add(t => t.CanEdit, true)
            .Add(t => t.CanDelete, true));

        var items = OpenMenu(cut, RowElement(cut, rows[0]).QuerySelector(".song-table__cell--actions button[aria-haspopup='menu']")!);

        Assert.Equal(["Play next", "Add to queue", "Go to album", "Go to artist", "Edit", "Remove from playlist", "Delete from library…"], items);
        Assert.Equal("Remove from playlist", cut.FindAll("[role='menuitem']")[5].TextContent.Trim());
        Assert.Contains("error", cut.FindAll("[role='menuitem']")[5].ClassName, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GoToAlbumAndGoToArtistShowDisabledWhenTheTargetIsUnknownAndTheAlbumPageLeavesGoToAlbumOut()
    {
        await UseDeviceAsync("desktop");
        var songs = new List<SongRow> { Row("Unknown", hasTargets: false) };

        var cut = RenderTable(SongTablePlace.Songs, songs);
        OpenMenu(cut, RowElement(cut, songs[0]).QuerySelector(".song-table__cell--actions button[aria-haspopup='menu']")!);

        Assert.True(cut.FindAll("[role='menuitem']").Single(item => item.TextContent.Trim() == "Go to album").HasAttribute("disabled"));
        Assert.True(cut.FindAll("[role='menuitem']").Single(item => item.TextContent.Trim() == "Go to artist").HasAttribute("disabled"));

        var albumTrack = new List<SongRow> { Row("Track", number: 1) };
        var album = RenderTable(SongTablePlace.AlbumPage, albumTrack);
        var items = OpenMenu(album, RowElement(album, albumTrack[0]).QuerySelector(".song-table__cell--actions button[aria-haspopup='menu']")!);
        Assert.DoesNotContain("Go to album", items);
        Assert.Contains("Go to artist", items);
    }

    [Fact]
    public async Task EditAndDeleteNeedHostPermissionAndADesktopThatMayEdit()
    {
        await UseDeviceAsync("desktop");
        var rows = new List<SongRow> { Row("One") };
        var edited = new List<SongRow>();

        var cut = RenderTable(SongTablePlace.Songs, rows, p => p.Add(t => t.CanEdit, true).Add(t => t.OnEdit, Cb<SongRow>(edited.Add)));
        var items = OpenMenu(cut, RowElement(cut, rows[0]).QuerySelector(".song-table__cell--actions button[aria-haspopup='menu']")!);
        Assert.Contains("Edit", items);
        Assert.DoesNotContain("Delete from library…", items);
        cut.FindAll("[role='menuitem']").Single(item => item.TextContent.Trim() == "Edit").Click();
        cut.WaitForAssertion(() => Assert.Equal(rows, edited));

        var noPermission = RenderTable(SongTablePlace.Songs, rows);
        var without = OpenMenu(noPermission, RowElement(noPermission, rows[0]).QuerySelector(".song-table__cell--actions button[aria-haspopup='menu']")!);
        Assert.DoesNotContain("Edit", without);
    }

    [Fact]
    public async Task ASongStillBeingIdentifiedHasItsEditDisabled()
    {
        await UseDeviceAsync("desktop");
        var rows = new List<SongRow> { Row("One") with { IsUpdatingDetails = true } };

        var cut = RenderTable(SongTablePlace.Songs, rows, p => p.Add(t => t.CanEdit, true));
        OpenMenu(cut, RowElement(cut, rows[0]).QuerySelector(".song-table__cell--actions button[aria-haspopup='menu']")!);

        Assert.True(cut.FindAll("[role='menuitem']").Single(item => item.TextContent.Trim() == "Edit").HasAttribute("disabled"));
    }

    // -- Ticks and the bulk slot -------------------------------------------------------------------------

    [Fact]
    public async Task TicksShowOnADesktopThatMayEditAndHandTheTickedRowsToTheBulkSlot()
    {
        await UseDeviceAsync("desktop");
        var rows = ThreeSongs();
        IReadOnlySet<string>? changed = null;

        var cut = RenderTable(SongTablePlace.Songs, rows, p => p
            .Add(t => t.ShowSelection, true)
            .Add(t => t.SelectedIdsChanged, Cb<IReadOnlySet<string>>(set => changed = set))
            .Add(t => t.BulkContent, ticked => builder => builder.AddMarkupContent(0, $"<p data-bulk>{string.Join(",", ticked.Select(r => r.Title))}</p>")));

        var boxes = cut.FindAll(".song-table__cell--tick input[type='checkbox']");
        Assert.Equal(3, boxes.Count);
        Assert.Equal("", cut.Find("[data-bulk]").TextContent);

        RowElement(cut, rows[1]).QuerySelector(".song-table__cell--tick input")!.Change(true);

        Assert.Equal([rows[1].Id], changed);
        Assert.Equal("Alpha", cut.Find("[data-bulk]").TextContent);
        Assert.Contains("Select", Headers(cut)[0]);
    }

    [Fact]
    public async Task TicksNeverShowOnAPhoneOrADeviceThatCannotEdit()
    {
        await UseDeviceAsync("phone", touch: true, fine: false, hover: false);
        var phone = RenderTable(SongTablePlace.Songs, ThreeSongs(), p => p.Add(t => t.ShowSelection, true).Add(t => t.CanEdit, true));
        Assert.Empty(phone.FindAll("input[type='checkbox']"));

        await UseDeviceAsync("tablet", touch: true, fine: false, hover: false);
        var tablet = RenderTable(SongTablePlace.Songs, ThreeSongs(), p => p.Add(t => t.ShowSelection, true).Add(t => t.CanEdit, true));
        Assert.Empty(tablet.FindAll("input[type='checkbox']"));
    }

    // -- Playlist order --------------------------------------------------------------------------------

    [Fact]
    public async Task AltDownAndAltUpMoveTheFocusedPlaylistRowOnePlace()
    {
        await UseDeviceAsync("desktop");
        var rows = new List<SongRow> { Row("A", playlist: true), Row("B", playlist: true), Row("C", playlist: true) };
        var moves = new List<SongReorderRequest>();

        var cut = RenderTable(SongTablePlace.Playlist, rows, p => p
            .Add(t => t.CanReorder, true)
            .Add(t => t.OnReorder, Cb<SongReorderRequest>(moves.Add)));

        RowElement(cut, rows[0]).KeyDown(new KeyboardEventArgs { Key = "ArrowDown", AltKey = true });
        RowElement(cut, rows[2]).KeyDown(new KeyboardEventArgs { Key = "ArrowUp", AltKey = true });
        RowElement(cut, rows[0]).KeyDown(new KeyboardEventArgs { Key = "ArrowUp", AltKey = true });
        RowElement(cut, rows[2]).KeyDown(new KeyboardEventArgs { Key = "ArrowDown", AltKey = true });

        Assert.Equal([new SongReorderRequest(rows[0], 0, 1), new SongReorderRequest(rows[2], 2, 1)], moves);
        Assert.Contains("position 2 of 3", cut.Find("[role='status']").TextContent);
    }

    [Fact]
    public async Task APlaylistThePersonMayNotEditDoesNotReorderAndOtherPlacesIgnoreAltArrows()
    {
        await UseDeviceAsync("desktop");
        var rows = new List<SongRow> { Row("A", playlist: true), Row("B", playlist: true) };
        var moves = new List<SongReorderRequest>();

        var readOnly = RenderTable(SongTablePlace.Playlist, rows, p => p.Add(t => t.OnReorder, Cb<SongReorderRequest>(moves.Add)));
        RowElement(readOnly, rows[0]).KeyDown(new KeyboardEventArgs { Key = "ArrowDown", AltKey = true });
        Assert.Empty(readOnly.FindAll("[draggable]"));

        var songs = RenderTable(SongTablePlace.Songs, rows, p => p.Add(t => t.CanReorder, true).Add(t => t.OnReorder, Cb<SongReorderRequest>(moves.Add)));
        RowElement(songs, rows[0]).KeyDown(new KeyboardEventArgs { Key = "ArrowDown", AltKey = true });

        Assert.Empty(moves);
    }

    [Fact]
    public async Task DraggingAPlaylistRowShowsAPurpleDropLineAndDroppingMovesItThere()
    {
        await UseDeviceAsync("desktop");
        var rows = new List<SongRow> { Row("A", playlist: true), Row("B", playlist: true), Row("C", playlist: true) };
        var moves = new List<SongReorderRequest>();

        var cut = RenderTable(SongTablePlace.Playlist, rows, p => p
            .Add(t => t.CanReorder, true)
            .Add(t => t.OnReorder, Cb<SongReorderRequest>(moves.Add)));
        Assert.Equal("true", RowElement(cut, rows[0]).GetAttribute("draggable"));
        Assert.NotEmpty(RowElement(cut, rows[0]).QuerySelectorAll(".song-table__grip"));

        RowElement(cut, rows[0]).DragStart();
        RowElement(cut, rows[2]).DragOver();
        Assert.Single(cut.FindAll(".song-table__drop-line"));
        RowElement(cut, rows[2]).Drop();

        Assert.Equal([new SongReorderRequest(rows[0], 0, 1)], moves);
        Assert.Empty(cut.FindAll(".song-table__drop-line"));
    }

    [Fact]
    public async Task DroppingOnTheEndZoneMovesTheRowToTheEnd()
    {
        await UseDeviceAsync("desktop");
        var rows = new List<SongRow> { Row("A", playlist: true), Row("B", playlist: true), Row("C", playlist: true) };
        var moves = new List<SongReorderRequest>();

        var cut = RenderTable(SongTablePlace.Playlist, rows, p => p
            .Add(t => t.CanReorder, true)
            .Add(t => t.OnReorder, Cb<SongReorderRequest>(moves.Add)));

        RowElement(cut, rows[0]).DragStart();
        cut.Find(".song-table__end-zone").DragOver();
        cut.Find(".song-table__end-zone").Drop();

        Assert.Equal([new SongReorderRequest(rows[0], 0, 2)], moves);
    }

    [Fact]
    public async Task APlaylistThatListsTheSameSongTwiceDrawsBothRows()
    {
        await UseDeviceAsync("desktop");
        var first = Row("Twice", playlist: true);
        var second = first with { PlaylistItemId = Guid.NewGuid() };

        var cut = RenderTable(SongTablePlace.Playlist, [first, second]);

        Assert.Equal(2, cut.FindAll(".song-table__row").Count);
    }

    // -- Touch and phone --------------------------------------------------------------------------------

    [Fact]
    public async Task OnAHoverlessTouchScreenThereAreNoResizeHandlesNoDragNoHoverQueueAndTheQueueIsInTheMenu()
    {
        await UseDeviceAsync("tablet", touch: true, fine: false, hover: false);
        var rows = new List<SongRow> { Row("A", playlist: true), Row("B", playlist: true) };

        var cut = RenderTable(SongTablePlace.Playlist, rows, p => p
            .Add(t => t.CanReorder, true)
            .Add(t => t.OnReorder, Cb<SongReorderRequest>(_ => { }))
            .Add(t => t.OnQueue, Cb<SongRow>(_ => { })));

        Assert.Empty(cut.FindAll(".song-table__resizer"));
        Assert.Empty(cut.FindAll("[draggable]"));
        Assert.Empty(cut.FindAll(".song-table__grip"));
        Assert.Empty(cut.FindAll(".song-table__queue"));
        Assert.Empty(cut.FindAll(".song-table__end-zone"));
        Assert.Equal(2, cut.FindAll(".song-table__row").Count);

        var items = OpenMenu(cut, RowElement(cut, rows[0]).QuerySelector(".song-table__cell--actions button[aria-haspopup='menu']")!);
        Assert.Contains("Add to queue", items);
    }

    [Fact]
    public async Task OnADesktopTheQueueButtonIsThereForHoverOrFocus()
    {
        await UseDeviceAsync("desktop");

        var cut = RenderTable(SongTablePlace.Songs, ThreeSongs(), p => p.Add(t => t.OnQueue, Cb<SongRow>(_ => { })));

        Assert.Equal(3, cut.FindAll(".song-table__queue button").Count);
    }

    [Fact]
    public async Task OnAPhoneEverySongListIsAPlainFlatListWithCoverTitleArtistAndMenu()
    {
        await UseDeviceAsync("phone", touch: true, fine: false, hover: false);
        var rows = new List<SongRow>
        {
            Row("One", artist: "Ada", cover: "/api/v1/artwork/a?w=200"),
            Row("Two", artist: "Bea", cover: "/api/v1/artwork/b?w=200"),
        };

        var cut = RenderTable(SongTablePlace.Songs, rows, p => p.Add(t => t.CurrentRowId, rows[0].Id));

        Assert.Empty(cut.FindAll(".song-table__head"));
        Assert.Empty(cut.FindAll(".song-table__resizer"));
        Assert.Empty(cut.FindAll("[draggable]"));
        Assert.Equal("list", cut.Find(".song-table--phone").GetAttribute("role"));
        Assert.Equal(2, cut.FindAll(".song-table__prow").Count);
        Assert.Equal(2, cut.FindAll(".song-table__art--phone").Count);

        var first = RowElement(cut, rows[0]);
        Assert.Equal("One", first.QuerySelector(".song-table__ptitle-text")!.TextContent.Trim());
        Assert.Equal("Ada", first.QuerySelector(".song-table__psub")!.TextContent.Trim());
        Assert.Contains("is-current", first.QuerySelector(".song-table__ptitle")!.ClassName);
        Assert.NotEmpty(first.QuerySelectorAll(".playback-activity-mark"));
        Assert.Empty(first.QuerySelectorAll(".song-table__cell--favorite"));
        Assert.Single(first.QuerySelectorAll("button[aria-haspopup]"));
    }

    [Fact]
    public async Task OnAPhoneATapPlaysAndTheHeartIsTheFirstItemOfTheSheet()
    {
        await UseDeviceAsync("phone", touch: true, fine: false, hover: false);
        var rows = ThreeSongs();
        SongPlayRequest? request = null;
        var toggled = new List<SongRow>();

        var cut = RenderTable(SongTablePlace.Songs, rows, p => p
            .Add(t => t.FavoriteIds, new HashSet<string> { rows[0].Id })
            .Add(t => t.OnPlay, Cb<SongPlayRequest>(r => request = r))
            .Add(t => t.OnQueue, Cb<SongRow>(_ => { }))
            .Add(t => t.OnToggleFavorite, Cb<SongRow>(toggled.Add)));

        RowElement(cut, rows[2]).Click();
        Assert.Equal(rows[2], request!.Start);

        var items = OpenMenu(cut, RowElement(cut, rows[0]).QuerySelector("button[aria-haspopup]")!);
        Assert.Equal("Remove from Favorites", items[0]);
        Assert.Equal("Add to queue", items[1]);

        cut.FindAll("[role='menuitem']")[0].Click();
        cut.WaitForAssertion(() => Assert.Equal([rows[0]], toggled));
    }

    [Fact]
    public async Task OnAPhonePressAndHoldPressesThatRowsMenuButtonAndNeverOpensAMissingRow()
    {
        await UseDeviceAsync("phone", touch: true, fine: false, hover: false);
        var rows = new List<SongRow> { Row("Held", number: 1), Row("Absent", number: 2, owned: false) };
        var module = JSInterop.SetupModule("./js/song-table.js");
        module.Mode = JSRuntimeMode.Loose;

        var cut = RenderTable(SongTablePlace.AlbumPage, rows);
        RowElement(cut, rows[1]).TriggerEvent("oncontextmenu", new MouseEventArgs());
        Assert.Empty(module.Invocations);

        RowElement(cut, rows[0]).TriggerEvent("oncontextmenu", new MouseEventArgs());

        cut.WaitForAssertion(() => Assert.Equal(rows[0].Id, Assert.Single(module.Invocations["openRowMenu"]).Arguments[0]));
    }

    [Fact]
    public async Task OnAPhoneAPlaylistSheetGetsMoveUpAndMoveDownLeftOutAtTheEnds()
    {
        await UseDeviceAsync("phone", touch: true, fine: false, hover: false);
        var rows = new List<SongRow> { Row("A", playlist: true), Row("B", playlist: true), Row("C", playlist: true) };
        var moves = new List<SongReorderRequest>();

        var cut = RenderTable(SongTablePlace.Playlist, rows, p => p
            .Add(t => t.CanReorder, true)
            .Add(t => t.OnReorder, Cb<SongReorderRequest>(moves.Add))
            .Add(t => t.OnRemoveFromPlaylist, Cb<SongRow>(_ => { })));

        var middle = OpenMenu(cut, RowElement(cut, rows[1]).QuerySelector("button[aria-haspopup]")!);
        Assert.Equal(["Go to album", "Go to artist", "Move up", "Move down", "Remove from playlist"], middle);
        cut.FindAll("[role='menuitem']").Single(item => item.TextContent.Trim() == "Move down").Click();
        cut.WaitForAssertion(() => Assert.Equal([new SongReorderRequest(rows[1], 1, 2)], moves));

        var firstRow = RenderTable(SongTablePlace.Playlist, rows, p => p
            .Add(t => t.CanReorder, true)
            .Add(t => t.OnReorder, Cb<SongReorderRequest>(_ => { })));
        var top = OpenMenu(firstRow, RowElement(firstRow, rows[0]).QuerySelector("button[aria-haspopup]")!);
        Assert.DoesNotContain("Move up", top);
        Assert.Contains("Move down", top);
    }

    [Fact]
    public async Task OnAPhoneTheAlbumPageShowsNumberTitleAndAnArtistOnlyWhenItDiffers()
    {
        await UseDeviceAsync("phone", touch: true, fine: false, hover: false);
        var rows = new List<SongRow>
        {
            Row("Same", artist: "Ada", number: 1, cover: "/api/v1/artwork/a?w=200"),
            Row("Also same", artist: "Ada", number: 2),
            Row("Guest", artist: "Bea", number: 3) with { IsExplicit = true },
            Row("Absent", artist: "Ada", number: 4, owned: false),
        };

        var cut = RenderTable(SongTablePlace.AlbumPage, rows, p => p.Add(t => t.AlbumArtist, "Ada"));

        Assert.Empty(cut.FindAll(".song-table__art"));
        Assert.Equal(["1", "2", "3", "4"], cut.FindAll(".song-table__pnumber").Select(n => n.TextContent.Trim()));
        Assert.Empty(RowElement(cut, rows[0]).QuerySelectorAll(".song-table__psub"));
        Assert.Equal("Bea", RowElement(cut, rows[2]).QuerySelector(".song-table__psub")!.TextContent.Trim());
        Assert.Equal("Explicit", RowElement(cut, rows[2]).QuerySelector(".song-table__chip")!.TextContent.Trim());
        Assert.Equal("Missing", RowElement(cut, rows[3]).QuerySelector(".song-table__psub")!.TextContent.Trim());
        Assert.Empty(RowElement(cut, rows[3]).QuerySelectorAll("button"));
    }

    // -- Notice, undo, loading and empty ------------------------------------------------------------------

    [Fact]
    public async Task ANoticeAndAnUndoShowInPlaceAboveTheTableAndTheNoticeCloses()
    {
        await UseDeviceAsync("desktop");
        var dismissed = 0;

        var cut = RenderTable(SongTablePlace.Songs, ThreeSongs(), p => p
            .Add(t => t.Notice, "Could not save that favorite.")
            .Add(t => t.OnNoticeDismissed, EventCallback.Factory.Create(this, () => dismissed++))
            .Add(t => t.Undo, new UndoOffer("Status updated.", () => Task.CompletedTask)));

        Assert.Equal("Could not save that favorite.", cut.Find("[data-song-table-notice]").TextContent.Trim());
        Assert.Contains("Status updated.", cut.Find("[data-song-table-undo]").TextContent);

        cut.Find("[data-song-table-notice] .tl-alert-close").Click();

        Assert.Empty(cut.FindAll("[data-song-table-notice]"));
        Assert.Equal(1, dismissed);
    }

    [Fact]
    public async Task WhileLoadingTheHeaderShowsWithSkeletonRows()
    {
        await UseDeviceAsync("desktop");

        var cut = RenderTable(SongTablePlace.Songs, [], p => p.Add(t => t.IsLoading, true));

        Assert.NotEmpty(cut.FindAll(".song-table__head"));
        Assert.Equal(8, cut.FindAll(".song-table__row--skeleton").Count);
    }

    [Fact]
    public async Task WhileLoadingOnAPhoneTheListShowsSkeletonRows()
    {
        await UseDeviceAsync("phone", touch: true, fine: false, hover: false);

        var cut = RenderTable(SongTablePlace.Songs, [], p => p.Add(t => t.IsLoading, true));

        Assert.Equal(8, cut.FindAll(".song-table__prow--skeleton").Count);
    }

    [Fact]
    public async Task WithNoSongsTheHostsEmptyLineShowsInsteadOfTheTable()
    {
        await UseDeviceAsync("desktop");

        var cut = RenderTable(SongTablePlace.AlbumPage, [], p => p
            .Add(t => t.EmptyContent, builder => builder.AddMarkupContent(0, "<p data-empty>No tracks match the current view.</p>")));

        Assert.Equal("No tracks match the current view.", cut.Find("[data-empty]").TextContent);
        Assert.Empty(cut.FindAll(".song-table"));
    }

    // -- Rules the markup must keep -----------------------------------------------------------------------

    [Fact]
    public void TheTableVirtualizesItsRowsAndDrawsNoInlineStylesOrRawButtons()
    {
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        var markup = File.ReadAllText(Path.Combine(root, "src", "MediaEngine.Web", "Components", "Listen", "SongTable.razor"));
        var css = File.ReadAllText(Path.Combine(root, "src", "MediaEngine.Web", "Components", "Listen", "SongTable.razor.css"));

        Assert.Contains("<Virtualize Items=\"@_shown\"", markup, StringComparison.Ordinal);
        Assert.Contains("@key=\"RowKey(row)\"", markup, StringComparison.Ordinal);
        Assert.Contains("role=\"separator\"", markup, StringComparison.Ordinal);
        Assert.DoesNotContain("::deep", css, StringComparison.Ordinal);
        Assert.DoesNotContain("!important", css, StringComparison.Ordinal);
    }

    private sealed class SpyPreferenceStore : ISongTablePreferenceStore
    {
        public Dictionary<(Guid Profile, SongTablePlace Place), SongTableChoice> Stored { get; } = [];

        public List<(Guid Profile, SongTablePlace Place, SongTableChoice Choice)> Saves { get; } = [];

        public ValueTask<SongTableChoice> LoadAsync(Guid profileId, SongTablePlace place) =>
            ValueTask.FromResult(Stored.TryGetValue((profileId, place), out var choice) ? choice : SongTableChoice.Default);

        public ValueTask SaveAsync(Guid profileId, SongTablePlace place, SongTableChoice choice)
        {
            Saves.Add((profileId, place, choice));
            Stored[(profileId, place)] = choice;
            return ValueTask.CompletedTask;
        }
    }
}
