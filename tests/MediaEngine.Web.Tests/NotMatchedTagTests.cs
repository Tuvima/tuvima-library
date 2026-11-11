using Bunit;
using MediaEngine.Contracts.Details;
using MediaEngine.Contracts.ProfileState;
using MediaEngine.Web.Components.Details;
using MediaEngine.Web.Services.Integration;
using MediaEngine.Web.Services.Playback;
using MediaEngine.Web.Tests.Support;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace MediaEngine.Web.Tests;

/// <summary>
/// A title added to an album, season, series or audiobook with "Add anyway" has no number: lists show it with a quiet "Not matched"
/// tag, in an "Unnumbered" group at the end (details overlay spec D13).
/// </summary>
public sealed class NotMatchedTagTests : AsyncBunitContext
{
    public NotMatchedTagTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddLogging();
        Services.AddNativeUiServices();
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
        Services.AddSingleton(_ => new PlaybackSessionController(null!, null!));
    }

    private static MediaGroupingItemViewModel Track(string id, string title, string? number, bool unmatched = false, bool owned = true, DetailEntityType kind = DetailEntityType.Work) => new()
    {
        Id = id,
        EntityType = kind,
        Title = title,
        TrackNumber = number,
        IsOwned = owned,
        IsUnmatched = unmatched,
    };

    private static MediaGroupingViewModel Album(params MediaGroupingItemViewModel[] items) => new() { Key = "tracks", Title = "OK Computer", Items = items };

    [Fact]
    public void ATrackAddedAnywayShowsTheTagInAnUnnumberedGroupAtTheEndOfTheList()
    {
        var cut = Render<MusicTrackList>(parameters => parameters
            .Add(component => component.AlbumTitle, "OK Computer")
            .Add(component => component.Groups, [Album(
                Track("0000000a-0000-0000-0000-000000000001", "Unknown Song", null, unmatched: true),
                Track("0000000a-0000-0000-0000-000000000002", "Airbag", "1"),
                Track("0000000a-0000-0000-0000-000000000003", "Paranoid Android", "2"))]));

        var rows = cut.FindAll(".song-table__row");
        Assert.Equal(["0000000a-0000-0000-0000-000000000002", "0000000a-0000-0000-0000-000000000003", "0000000a-0000-0000-0000-000000000001"],
            rows.Select(row => row.GetAttribute("data-song-row-id")));
        Assert.Equal("Not matched", Assert.Single(cut.FindAll(".app-chip")).TextContent.Trim());
        Assert.Contains("Not matched", rows[2].TextContent, StringComparison.Ordinal);
        Assert.DoesNotContain("Not matched", rows[0].TextContent, StringComparison.Ordinal);

        var group = Assert.Single(cut.FindAll(".song-table__group"));
        Assert.Equal("Unnumbered", group.TextContent.Trim());
        Assert.Same(rows[1].NextElementSibling, group);
        Assert.Same(group.NextElementSibling, rows[2]);
        Assert.Equal("–", rows[2].QuerySelector(".song-table__position")!.TextContent.Trim());
    }

    [Fact]
    public void TheAlbumTrackListGivesNumberAndSongTheirOwnCellsOnEveryRowAndMissingRowsHaveNoActions()
    {
        // A 12-track album with two tracks not owned: "Show missing tracks" on keeps every row aligned.
        var tracks = Enumerable.Range(1, 12)
            .Select(number => Track($"0000000c-0000-0000-0000-{number:000000000000}", $"Track {number}", number.ToString(), owned: number is not (3 or 11)))
            .ToArray();
        var cut = Render<MusicTrackList>(parameters => parameters
            .Add(component => component.AlbumTitle, "OK Computer")
            .Add(component => component.Groups, [Album(tracks)]));
        cut.Find(".tl-detail-track-toggle input").Change(true);

        var rows = cut.FindAll(".song-table__row");
        Assert.Equal(12, rows.Count);
        foreach (var row in rows)
        {
            Assert.Single(row.QuerySelectorAll(".song-table__cell--number"));
            Assert.Single(row.QuerySelectorAll(".song-table__cell--song"));
            Assert.Matches(@"^\d+$", row.QuerySelector(".song-table__cell--number")!.TextContent.Trim());
        }

        var missing = rows.Where(row => row.ClassList.Contains("is-missing")).ToList();
        Assert.Equal(2, missing.Count);
        Assert.All(missing, row =>
        {
            Assert.Empty(row.QuerySelectorAll("[data-listen-row-play]"));
            Assert.Empty(row.QuerySelectorAll("button"));
            Assert.Contains("Missing", row.QuerySelector(".song-table__chip--missing")!.TextContent, StringComparison.Ordinal);
        });
        Assert.Equal("10", rows[9].QuerySelector(".song-table__cell--number")!.TextContent.Trim());
        Assert.Contains("Track 10", rows[9].QuerySelector(".song-table__cell--song")!.TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public void AListWithNoUnmatchedTracksHasNoUnnumberedGroupAndNoTag()
    {
        var cut = Render<MusicTrackList>(parameters => parameters
            .Add(component => component.AlbumTitle, "OK Computer")
            .Add(component => component.Groups, [Album(Track("0000000a-0000-0000-0000-000000000002", "Airbag", "1"))]));

        Assert.Empty(cut.FindAll(".song-table__group"));
        Assert.Empty(cut.FindAll(".app-chip"));
    }

    [Fact]
    public void AnAudiobookChapterAddedAnywayShowsTheTagInAnUnnumberedGroupAtTheEndOfTheList()
    {
        var cut = Render<MusicTrackList>(parameters => parameters
            .Add(component => component.AlbumTitle, "Dune")
            .Add(component => component.IsAudiobook, true)
            .Add(component => component.Groups, [Album(
                Track("0000000d-0000-0000-0000-000000000001", "Interlude", null, unmatched: true, kind: DetailEntityType.Audiobook),
                Track("0000000d-0000-0000-0000-000000000002", "Chapter 1", "1", kind: DetailEntityType.Audiobook),
                Track("0000000d-0000-0000-0000-000000000003", "Chapter 2", "2", kind: DetailEntityType.Audiobook))]));

        var rows = cut.FindAll(".tl-embedded-audio__row");
        Assert.Equal(["0000000d-0000-0000-0000-000000000002", "0000000d-0000-0000-0000-000000000003", "0000000d-0000-0000-0000-000000000001"],
            rows.Select(row => row.GetAttribute("data-work-id")));
        Assert.Equal("Not matched", Assert.Single(cut.FindAll(".app-chip")).TextContent.Trim());
        Assert.Equal("Unnumbered", Assert.Single(cut.FindAll(".tl-audio-item-table__group-heading")).TextContent.Trim());
        Assert.Empty(cut.FindAll(".song-table"));
    }

    [Fact]
    public void AnEpisodeOrSeriesItemAddedAnywayShowsTheTag()
    {
        var cut = Render<SequenceEntryContent>(parameters => parameters
            .Add(component => component.Item, new SequenceItemViewModel
            {
                Id = "0000000b-0000-0000-0000-000000000001",
                Title = "A special",
                IsOwned = true,
                IsUnmatched = true,
            })
            .Add(component => component.Title, "A special")
            .Add(component => component.PositionLabel, "•"));

        Assert.Equal("Not matched", cut.Find(".app-chip").TextContent.Trim());
    }

    [Fact]
    public void AnItemThatIsMatchedHasNoTag()
    {
        var cut = Render<SequenceEntryContent>(parameters => parameters
            .Add(component => component.Item, new SequenceItemViewModel
            {
                Id = "0000000b-0000-0000-0000-000000000002",
                Title = "Pilot",
                IsOwned = true,
            })
            .Add(component => component.Title, "Pilot")
            .Add(component => component.PositionLabel, "1"));

        Assert.Empty(cut.FindAll(".app-chip"));
    }
}
