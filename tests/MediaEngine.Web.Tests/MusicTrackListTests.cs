using Bunit;
using MediaEngine.Contracts.Details;
using MediaEngine.Contracts.ProfileState;
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
/// The album page's track list on the one song table (spec music-table-and-reader-frame WP4): the album defaults, Show missing tracks,
/// a missing row with no actions, single click to play, the row tick box feeding the page's bulk bar, and the plain phone list.
/// </summary>
public sealed class MusicTrackListTests : AsyncBunitContext
{
    private static readonly Guid Album = Guid.Parse("a1000000-0000-0000-0000-000000000001");

    private readonly DeviceContextService _device = new(EngineApiClientStub.CreateDefault());
    private readonly PlaybackSessionController _playback;

    public MusicTrackListTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddLogging();
        Services.AddSingleton(_device);
        var api = EngineApiClientStub.Create(stub => stub.SetHandler(
            nameof(IEngineApiClient.GetProfileReactionsAsync),
            _ => Task.FromResult<IReadOnlyList<ProfileReactionDto>>([])));
        Services.AddSingleton<IEngineApiClient>(api);
        Services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        Services.AddScoped<ActiveProfileSessionService>();
        Services.AddScoped<UniverseStateContainer>();
        Services.AddScoped<UIOrchestratorService>();
        Services.AddSingleton(new MediaReactionService(api));
        Services.AddSingleton(new SavedItemService(api));
        Services.AddSingleton<ListenAudioDragService>();
        _playback = new PlaybackSessionController(null!, api);
        Services.AddSingleton(_playback);
    }

    private Task UseDeviceAsync(string formFactor) =>
        _device.InitialiseAsync(new DeviceContextSnapshot(formFactor == "phone" ? "mobile" : "web", formFactor, formFactor != "desktop", FinePointer: formFactor == "desktop", Hover: formFactor == "desktop"));

    private static MediaGroupingItemViewModel Track(
        int number,
        string title,
        string artist = "Radiohead",
        bool owned = true,
        bool unmatched = false,
        bool explicitLyrics = false) => new()
        {
            Id = $"0000000a-0000-0000-0000-{number:000000000000}",
            EntityType = DetailEntityType.Work,
            Title = title,
            TrackNumber = unmatched ? null : number.ToString(),
            Artist = artist,
            DurationSeconds = 180 + number,
            AssetId = $"0000000b-0000-0000-0000-{number:000000000000}",
            IsOwned = owned,
            IsUnmatched = unmatched,
            IsExplicit = explicitLyrics,
        };

    private static MediaGroupingViewModel Tracks(params MediaGroupingItemViewModel[] items) => new() { Key = "tracks", Title = "OK Computer", Items = items };

    private IRenderedComponent<MusicTrackList> RenderList(IReadOnlyList<MediaGroupingViewModel> groups, Action<ComponentParameterCollectionBuilder<MusicTrackList>>? more = null) =>
        Render<MusicTrackList>(parameters =>
        {
            parameters
                .Add(component => component.AlbumTitle, "OK Computer")
                .Add(component => component.AlbumId, Album.ToString("D"))
                .Add(component => component.ArtistName, "Radiohead")
                .Add(component => component.Groups, groups);
            more?.Invoke(parameters);
        });

    private static IReadOnlyList<string> Headers(IRenderedComponent<MusicTrackList> cut) =>
        [.. cut.FindAll(".song-table__head [role='columnheader']").Select(header => header.TextContent.Trim())];

    // -- Columns and Show missing tracks ----------------------------------------------------------

    [Fact]
    public async Task OnAnAlbumWithOneArtist_TheTableHasNumberSongStatusFavoriteAndTimeOnly()
    {
        await UseDeviceAsync("desktop");

        var cut = RenderList([Tracks(Track(1, "Airbag"), Track(2, "Paranoid Android"))]);

        Assert.Equal(["#", "Song", "Status", "Favorite", "Time", "Actions"], Headers(cut));
    }

    [Fact]
    public async Task OnACompilation_TheArtistColumnAppears()
    {
        await UseDeviceAsync("desktop");

        var cut = RenderList([Tracks(Track(1, "Airbag"), Track(2, "Other Song", artist: "Someone Else"))]);

        Assert.Equal(["#", "Song", "Artist", "Status", "Favorite", "Time", "Actions"], Headers(cut));
    }

    [Fact]
    public async Task ShowMissingTracks_SitsBesideTheTrackCount_AndAddsTheMissingRowsOnlyWhenOn()
    {
        await UseDeviceAsync("desktop");
        var cut = RenderList([Tracks(Track(1, "Airbag"), Track(2, "Paranoid Android", owned: false), Track(3, "Subterranean Homesick Alien"))]);

        Assert.Equal("2 of 3 tracks owned", cut.Find(".tl-music-tracks__count").TextContent.Trim());
        Assert.Equal(2, cut.FindAll(".song-table__row").Count);

        cut.Find(".tl-detail-track-toggle input").Change(true);

        Assert.Equal(3, cut.FindAll(".song-table__row").Count);
        Assert.Contains("Show missing tracks", cut.Find(".tl-detail-track-toggle").TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AMissingRow_IsDimmedWithAMissingChipAndHasNoActions()
    {
        await UseDeviceAsync("desktop");
        var cut = RenderList([Tracks(Track(1, "Airbag"), Track(2, "Paranoid Android", owned: false))]);
        cut.Find(".tl-detail-track-toggle input").Change(true);

        var rows = cut.FindAll(".song-table__row");
        var missing = rows[1];

        Assert.Contains("is-missing", missing.ClassList);
        Assert.Contains("Missing", missing.QuerySelector(".song-table__chip--missing")!.TextContent, StringComparison.Ordinal);
        Assert.Empty(missing.QuerySelectorAll("button"));
        Assert.NotEmpty(rows[0].QuerySelectorAll(".song-table__queue"));
        Assert.NotEmpty(rows[0].QuerySelectorAll("button[aria-pressed]"));
    }

    // -- Playing ------------------------------------------------------------------------------------

    [Fact]
    public async Task AClickOnATrack_PlaysTheOwnedTracksOfTheAlbumFromThatTrack()
    {
        await UseDeviceAsync("desktop");
        var cut = RenderList([Tracks(Track(1, "Airbag"), Track(2, "Paranoid Android", owned: false), Track(3, "Karma Police"), Track(4, "Lucky"))]);
        cut.Find(".tl-detail-track-toggle input").Change(true);

        cut.FindAll(".song-table__row")[2].Click();

        cut.WaitForAssertion(() => Assert.Equal(3, _playback.Queue.Count));
        Assert.Equal(["Airbag", "Karma Police", "Lucky"], _playback.Queue.Select(item => item.Title));
        Assert.Equal(1, _playback.CurrentIndex);
        Assert.All(_playback.Queue, item => Assert.Equal(Album, item.AlbumWorkId));
    }

    // -- Ticks and the page's bulk bar ---------------------------------------------------------------

    [Fact]
    public async Task TickingARow_OnAPageThePersonMayEdit_AddsItWithItsAlbumToThePagesSelection_AndShowsTheBar()
    {
        await UseDeviceAsync("desktop");
        var binding = new EditorBinding(_device) { HasPermission = true, EntityId = Album };
        var selection = new BulkEditSelection { MediaType = "Music" };
        var cut = RenderList([Tracks(Track(1, "Airbag"), Track(2, "Paranoid Android"))], parameters => parameters
            .AddCascadingValue(binding)
            .AddCascadingValue(selection));

        cut.Find(".song-table__row .song-table__cell--tick input").Change(true);

        var row = Assert.Single(selection.Rows);
        Assert.Equal("Airbag", row.Title);
        Assert.Equal(Album, row.ParentId);
        Assert.Equal("Music", row.MediaType);
        Assert.Same(binding, Services.GetRequiredService<EditorHost>().Binding);
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(".tl-batch__bar")));
        Assert.Contains("is-selected", cut.FindAll(".song-table__row")[0].ClassList);
    }

    [Fact]
    public async Task OnAPageThePersonMayNotEdit_NoTickBoxesAreDrawn()
    {
        await UseDeviceAsync("desktop");
        var binding = new EditorBinding(_device) { HasPermission = false, EntityId = Album };
        var cut = RenderList([Tracks(Track(1, "Airbag"))], parameters => parameters
            .AddCascadingValue(binding)
            .AddCascadingValue(new BulkEditSelection { MediaType = "Music" }));

        Assert.Empty(cut.FindAll(".song-table__cell--tick"));
    }

    // -- Phone ------------------------------------------------------------------------------------------

    [Fact]
    public async Task OnAPhone_TheTracksAreAPlainListWithNumberTitleExplicitBadgeAndMissingBeneath()
    {
        await UseDeviceAsync("phone");
        var cut = RenderList([Tracks(
            Track(1, "Airbag", explicitLyrics: true),
            Track(2, "Duet", artist: "Someone Else"),
            Track(3, "Karma Police", owned: false))]);
        cut.Find(".tl-detail-track-toggle input").Change(true);

        var rows = cut.FindAll(".song-table__prow");

        Assert.Equal(3, rows.Count);
        Assert.Empty(cut.FindAll(".song-table__art"));
        Assert.Equal("1", rows[0].QuerySelector(".song-table__pnumber")!.TextContent.Trim());
        Assert.Contains("Explicit", rows[0].QuerySelector(".song-table__ptitle")!.TextContent, StringComparison.Ordinal);
        Assert.Empty(rows[0].QuerySelectorAll(".song-table__psub"));
        Assert.Equal("Someone Else", rows[1].QuerySelector(".song-table__psub")!.TextContent.Trim());
        Assert.Equal("Missing", rows[2].QuerySelector(".song-table__psub")!.TextContent.Trim());
        Assert.Empty(rows[2].QuerySelectorAll(".song-table__pmenu"));
        Assert.NotEmpty(rows[0].QuerySelectorAll(".song-table__pmenu"));
        Assert.Empty(cut.FindAll(".song-table__cell--tick"));
    }

    // -- An audiobook keeps its own list --------------------------------------------------------------

    [Fact]
    public async Task AnAudiobook_StaysOnItsOwnChapterList()
    {
        await UseDeviceAsync("desktop");

        var cut = RenderList(
            [Tracks(new MediaGroupingItemViewModel { Id = "0000000d-0000-0000-0000-000000000001", EntityType = DetailEntityType.Audiobook, Title = "Chapter 1", TrackNumber = "1", IsOwned = true })],
            parameters => parameters.Add(component => component.IsAudiobook, true));

        Assert.Empty(cut.FindAll(".song-table"));
        Assert.Single(cut.FindAll(".tl-embedded-audio__row"));
    }
}
