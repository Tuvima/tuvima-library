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
        Services.AddSingleton<ListenAudioDragService>();
        Services.AddSingleton(_ => new PlaybackSessionController(null!, null!));
    }

    private static MediaGroupingItemViewModel Track(string id, string title, string? number, bool unmatched = false) => new()
    {
        Id = id,
        EntityType = DetailEntityType.Work,
        Title = title,
        TrackNumber = number,
        IsOwned = true,
        IsUnmatched = unmatched,
    };

    private static MediaGroupingViewModel Album(params MediaGroupingItemViewModel[] items) => new() { Key = "tracks", Title = "OK Computer", Items = items };

    [Fact]
    public void ATrackAddedAnywayShowsTheTagInAnUnnumberedGroupAtTheEndOfTheList()
    {
        var cut = Render<AudioItemTable>(parameters => parameters
            .Add(component => component.SourceTitle, "OK Computer")
            .Add(component => component.Groups, [Album(
                Track("0000000a-0000-0000-0000-000000000001", "Unknown Song", null, unmatched: true),
                Track("0000000a-0000-0000-0000-000000000002", "Airbag", "1"),
                Track("0000000a-0000-0000-0000-000000000003", "Paranoid Android", "2"))]));

        var rows = cut.FindAll("tr[data-audio-row]");
        Assert.Equal(["0000000a-0000-0000-0000-000000000002", "0000000a-0000-0000-0000-000000000003", "0000000a-0000-0000-0000-000000000001"],
            rows.Select(row => row.GetAttribute("data-work-id")));
        Assert.Equal("Not matched", Assert.Single(cut.FindAll(".tl-not-matched-tag")).TextContent.Trim());
        Assert.Contains("Not matched", rows[2].TextContent, StringComparison.Ordinal);
        Assert.DoesNotContain("Not matched", rows[0].TextContent, StringComparison.Ordinal);

        var group = Assert.Single(cut.FindAll(".tl-audio-item-table__group-row"));
        Assert.Equal("Unnumbered", group.TextContent.Trim());
        Assert.Same(rows[1].NextElementSibling, group);
        Assert.Same(group.NextElementSibling, rows[2]);
    }

    [Fact]
    public void AListWithNoUnmatchedTracksHasNoUnnumberedGroupAndNoTag()
    {
        var cut = Render<AudioItemTable>(parameters => parameters
            .Add(component => component.SourceTitle, "OK Computer")
            .Add(component => component.Groups, [Album(Track("0000000a-0000-0000-0000-000000000002", "Airbag", "1"))]));

        Assert.Empty(cut.FindAll(".tl-audio-item-table__group-row"));
        Assert.Empty(cut.FindAll(".tl-not-matched-tag"));
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

        Assert.Equal("Not matched", cut.Find(".tl-not-matched-tag").TextContent.Trim());
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

        Assert.Empty(cut.FindAll(".tl-not-matched-tag"));
    }
}
