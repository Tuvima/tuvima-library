using MediaEngine.Contracts.Editing;
using MediaEngine.Web.Services.Editing;

namespace MediaEngine.Web.Tests;

/// <summary>
/// The pure rules behind the editor's v3 leftovers (WP6, D7/D8): the Before value on every Review row and the
/// Artwork tool's levels.
/// </summary>
public sealed class EditorLeftoversTests
{
    private static readonly Guid Item = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000001");

    [Fact]
    public void PendingEditList_RemembersTheOldValueOfAStagedField()
    {
        var pending = new PendingEditList();

        pending.SetField(Item, "title", "New", before: "Old");

        Assert.Equal("Old", pending.Before(pending.Changes[0].Id + ":title"));
    }

    [Fact]
    public void PendingEditList_ForgetsTheOldValueWhenTheChangeIsDiscarded()
    {
        var pending = new PendingEditList();
        pending.SetField(Item, "title", "New", before: "Old");

        var rowId = pending.Changes[0].Id + ":title";
        pending.Discard();

        Assert.Null(pending.Before(rowId));
    }

    [Fact]
    public void ArtworkPickerRules_GroupByLevel_ListsThisItemThenSeasonsThenEpisodes()
    {
        var tabs = new[]
        {
            new ArtworkPickerTab("EpisodeStill", "Still", ArtworkPickerRules.LevelOf("EpisodeStill")),
            new ArtworkPickerTab("CoverArt", "Poster", ArtworkPickerRules.LevelOf("CoverArt")),
            new ArtworkPickerTab("SeasonPoster", "Season poster", ArtworkPickerRules.LevelOf("SeasonPoster")),
        };

        var groups = ArtworkPickerRules.GroupByLevel(tabs);

        Assert.Equal(
            [ArtworkPickerRules.ThisItemLevel, ArtworkPickerRules.SeasonsLevel, ArtworkPickerRules.EpisodesLevel],
            groups.Select(group => group.Level));
    }

    [Fact]
    public void ArtworkPickerRules_GroupByLevel_LeavesOutALevelWithNoTab()
    {
        var groups = ArtworkPickerRules.GroupByLevel([new ArtworkPickerTab("CoverArt", "Cover")]);

        Assert.Equal(ArtworkPickerRules.ThisItemLevel, Assert.Single(groups).Level);
    }

    [Fact]
    public void EditorHeaderRules_NameTheKindOfItem()
    {
        Assert.Equal("TV episode", EditorHeaderRules.KindLabel("TV", "episode"));
        Assert.Equal("TV show", EditorHeaderRules.KindLabel("TV", "series"));
        Assert.Equal("Film", EditorHeaderRules.KindLabel("Movies", "movie"));
        Assert.Equal("Album", EditorHeaderRules.KindLabel("Music", "album"));
    }

    [Fact]
    public void EditorHeaderRules_Links_ArePresentOnlyForRecordsTheEngineKnows()
    {
        var identity = new MediaEngine.Contracts.Metadata.MediaEditorIdentitySummaryDto { ProviderName = "TheTVDB", ProviderItemId = "355567", WikidataQid = "Q19853" };

        var links = EditorHeaderRules.Links(identity, "TV", "episode");

        Assert.Equal([("TheTVDB", "https://thetvdb.com/dataset/episode/355567"), ("Wikidata", "https://www.wikidata.org/wiki/Q19853")], links.Select(link => (link.Name, link.Url)));
        Assert.Empty(EditorHeaderRules.Links(null, "TV", "episode"));
        Assert.Empty(EditorHeaderRules.Links(new MediaEngine.Contracts.Metadata.MediaEditorIdentitySummaryDto { ProviderName = "TheTVDB", ProviderItemId = "x/../y" }, "TV", "series"));
    }

    [Fact]
    public void TheOldFamilyTreeAndRailStayGone_TheHeaderNavRowReplacedThem()
    {
        var root = AppContext.BaseDirectory;
        while (root is not null && !File.Exists(Path.Combine(root, "MediaEngine.slnx")))
        {
            root = Path.GetDirectoryName(root);
        }

        Assert.NotNull(root);
        var details = Path.Combine(root!, "src", "MediaEngine.Web", "Components", "Editor");
        Assert.False(File.Exists(Path.Combine(details, "EditorFamilyTree.razor")));
        Assert.False(File.Exists(Path.Combine(details, "EditorFamilyTree.razor.css")));
        Assert.False(File.Exists(Path.Combine(details, "EditorFamilyRail.razor")));
        var components = Path.Combine(root!, "src", "MediaEngine.Web", "Components");
        Assert.Empty(Directory.GetFiles(components, "EditorFamilyTree.razor*", SearchOption.AllDirectories));
        Assert.Empty(Directory.GetFiles(components, "EditorFamilyRail.razor*", SearchOption.AllDirectories));
        Assert.True(File.Exists(Path.Combine(details, "EditorHeaderNav.razor")));
    }
}
