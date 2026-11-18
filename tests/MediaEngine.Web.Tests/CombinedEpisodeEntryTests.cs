using Bunit;
using MediaEngine.Contracts.Details;
using MediaEngine.Contracts.Display;
using MediaEngine.Contracts.Editing;
using MediaEngine.Web.Components.Details;
using MediaEngine.Web.Services.Editing;
using MediaEngine.Web.Services.Integration;
using MediaEngine.Web.Services.MediaTiles;
using MediaEngine.Web.Tests.Support;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace MediaEngine.Web.Tests;

/// <summary>
/// A file named S01E19-20 is one file for two episodes (spec review-mockups-2026-11-18, D9): one entry, "E19-20: Title A + Title B",
/// with episodes and files counted apart in the lists and in the editor's dropdowns.
/// </summary>
public sealed class CombinedEpisodeEntryTests : AsyncBunitContext
{
    private static readonly Guid Show = Guid.Parse("50000000-0000-0000-0000-000000000005");

    public CombinedEpisodeEntryTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddLogging();
        Services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        Services.AddSingleton(EngineApiClientStub.CreateDefault());
        Services.AddScoped<DashboardSessionAccessor>();
        Services.AddScoped<ActiveProfileSessionService>();
        Services.AddScoped<UniverseStateContainer>();
        Services.AddScoped<UIOrchestratorService>();
    }

    [Fact]
    public void TheEpisodeList_ShowsADoubleFileAsOneEntryWithBothTitles()
    {
        var cut = RenderSeason();

        Assert.Contains("E19-20: Title A + Title B", cut.Markup);
        Assert.DoesNotContain("Same file as", cut.Markup);
    }

    [Fact]
    public void TheEpisodeTiles_GiveAMergedEntryNoSameFileChip()
    {
        var cut = RenderSeason(detailSurface: false);

        Assert.Contains("Title A + Title B", cut.Markup);
        Assert.DoesNotContain("Same file as", cut.Markup);
    }

    [Fact]
    public void TheEpisodeList_CountsEpisodesAndFilesApart()
    {
        var cut = RenderSeason();

        Assert.Contains("3 episodes owned · 2 files", cut.Markup);
    }

    [Fact]
    public void AnEpisodeWhoseSiblingIsOffScreen_StillShowsTheSameFileChip()
    {
        var cut = RenderSeason(withLoneSibling: true, detailSurface: false);

        Assert.Contains("Same file as Episode 5", cut.Markup);
    }

    [Fact]
    public void TheEpisodeTile_OfADoubleFile_ReadsAsTheEpisodeRangeOverBothTitles()
    {
        var tile = SequenceEpisodeTileAdapter.FromItem(Combined());

        Assert.Equal("S1 E19-20", tile.Subtitle);
        Assert.Equal("Title A + Title B", tile.Title);
    }

    [Fact]
    public void TheEpisodeTile_OfASingleEpisode_KeepsItsSeasonAndEpisode()
    {
        var tile = SequenceEpisodeTileAdapter.FromItem(Episode("e1", 1, "Pilot"));

        Assert.Equal("S1 E1", tile.Subtitle);
    }

    [Fact]
    public void TheSeasonDropdown_SaysTwentyEpisodesInNineteenFiles()
    {
        var entry = new EditorHeaderLevelEntryDto { Id = Guid.NewGuid(), Title = "Season 1", ItemCount = 20, FileCount = 19, SupportingFileCount = 2 };

        Assert.Equal("20 episodes · 19 files · 2 supporting files", EditorNavText.EntryLine(entry, "episode"));
    }

    [Fact]
    public void TheSeasonDropdown_WithOneFilePerEpisode_SaysEpisodesOnly()
    {
        var entry = new EditorHeaderLevelEntryDto { Id = Guid.NewGuid(), Title = "Season 1", ItemCount = 20, FileCount = null };

        Assert.Equal("20 episodes", EditorNavText.EntryLine(entry, "episode"));
    }

    [Fact]
    public void TheItemDropdown_PagesByEpisodesNotRows_SoAMergedRowNeverShiftsTheNextPage()
    {
        var rows = new List<EditorNavItemDto>
        {
            new() { Id = Guid.NewGuid(), Kind = "episode", Code = "S01E01", Title = "Pilot" },
            new() { Id = Guid.NewGuid(), Kind = "episode", Code = "S01E02-03", Title = "Title A + Title B", EpisodeCount = 2 },
        };

        Assert.Equal(3, EditorNavText.Covered(rows));
    }

    private IRenderedComponent<SequencePlacementPanel> RenderSeason(bool withLoneSibling = false, bool detailSurface = true)
    {
        var combined = Combined();
        var items = new List<SequenceItemViewModel> { Episode("e1", 18, "Eighteen"), combined };
        if (withLoneSibling)
        {
            items.Insert(0, new SequenceItemViewModel
            {
                Id = "40000000-0000-0000-0000-0000000000a5",
                EntityType = DetailEntityType.TvEpisode,
                Title = "Five",
                GroupKey = "season-1",
                GroupTitle = "Season 1",
                PositionLabel = "5",
                PositionNumber = 5,
                PositionSort = 5,
                PositionText = "E5",
                IsOwned = true,
                CoveredWith = [new SequenceCoverageLinkViewModel { Id = "40000000-0000-0000-0000-0000000000a6", PositionLabel = "5" }],
                EpisodeContext = Context(5, "Five"),
            });
        }

        var group = new SequenceGroupViewModel
        {
            Key = "season-1",
            Title = "Season 1",
            EntityId = Guid.Parse("51000000-0000-0000-0000-000000000001").ToString("D"),
            OwnedCount = 20,
            FileCount = 19,
            ItemCount = items.Count,
            TotalKnownItems = 20,
            Items = items,
        };
        var placement = new SequencePlacementViewModel
        {
            ContainerId = Show.ToString("D"),
            SelectedContainerId = Show.ToString("D"),
            ContainerTitle = "Dr Horrible",
            ContainerLabel = "Show",
            ItemLabel = "Episode",
            ItemPluralLabel = "Episodes",
            GroupLabel = "Season",
            CurrentGroupKey = "season-1",
            CurrentItem = items[0],
            Groups = [group],
        };
        return Render<SequencePlacementPanel>(p => p.Add(c => c.Placement, placement).Add(c => c.DetailSurface, detailSurface));
    }

    // One file, two episodes: the entry keeps the first episode's id and says it stands for both.
    private static SequenceItemViewModel Combined() => new()
    {
        Id = "40000000-0000-0000-0000-000000000019",
        EntityType = DetailEntityType.TvEpisode,
        Title = "Title A + Title B",
        GroupKey = "season-1",
        GroupTitle = "Season 1",
        PositionLabel = "19-20",
        PositionNumber = 19,
        PositionSort = 19,
        PositionText = "E19-20",
        IsOwned = true,
        EpisodeCount = 2,
        CoveredWith = [new SequenceCoverageLinkViewModel { Id = "40000000-0000-0000-0000-000000000020", PositionLabel = "20", Title = "Title B" }],
        EpisodeContext = Context(19, "Title A"),
    };

    private static SequenceItemViewModel Episode(string id, int number, string title) => new()
    {
        Id = $"40000000-0000-0000-0000-{number:000000000000}",
        EntityType = DetailEntityType.TvEpisode,
        Title = title,
        GroupKey = "season-1",
        GroupTitle = "Season 1",
        PositionLabel = number.ToString(),
        PositionNumber = number,
        PositionSort = number,
        PositionText = $"E{number}",
        IsOwned = true,
        IsCurrent = number == 18,
        EpisodeContext = Context(number, title),
    };

    private static DisplayEpisodeContextDto Context(int number, string title) => new(
        Show, Guid.NewGuid(), Guid.NewGuid(), "Dr Horrible", title, 1, number, DisplayContinuationState.Unstarted, null, null);
}
