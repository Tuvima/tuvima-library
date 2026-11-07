using System.Text.Json;
using MediaEngine.Contracts.Editing;
using MediaEngine.Contracts.Playback;

namespace MediaEngine.Contracts.Tests;

/// <summary>The wire shapes of the editor's Bookmarks tab (universal editor v5 D37, WP15): the staged change, the two lists and the show overview.</summary>
public sealed class EditorBookmarkContractTests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    [Fact]
    public void TheBookmarksChangeKind_IsKnown_AndIsSavedWithTheRestOfTheSession()
    {
        Assert.Equal("bookmarks", EditSessionChangeKinds.Bookmarks);
        Assert.True(EditSessionChangeKinds.IsKnown("bookmarks"));
        Assert.True(EditSessionChangeKinds.IsDataChange(EditSessionChangeKinds.Bookmarks));
        Assert.False(EditSessionChangeKinds.IsFileChange(EditSessionChangeKinds.Bookmarks));
    }

    [Fact]
    public void AStagedChange_CarriesItsOperationsInOrder_ByTheirWireNames()
    {
        var title = Guid.NewGuid();
        var bookmark = Guid.NewGuid();
        var change = new EditSessionChange
        {
            Id = "b1",
            Kind = EditSessionChangeKinds.Bookmarks,
            EntityId = title,
            Bookmarks = new EditorBookmarkChangeDto
            {
                Ops =
                [
                    new EditorBookmarkOp { Id = "1", Kind = EditorBookmarkOpKinds.Add, BookmarkId = bookmark },
                    new EditorBookmarkOp { Id = "2", Kind = EditorBookmarkOpKinds.Rename, BookmarkId = bookmark, Name = "Opening" },
                    new EditorBookmarkOp { Id = "3", Kind = EditorBookmarkOpKinds.ChangeTime, BookmarkId = bookmark, PositionSeconds = 724 },
                    new EditorBookmarkOp
                    {
                        Id = "4",
                        Kind = EditorBookmarkOpKinds.Import,
                        Items = [new EditorBookmarkImportItem { PositionSeconds = 60, Name = "Titles", ReplacesBookmarkId = bookmark }],
                    },
                    new EditorBookmarkOp { Id = "5", Kind = EditorBookmarkOpKinds.StartOver },
                ],
            },
        };

        var json = JsonSerializer.Serialize(change, Web);
        var back = JsonSerializer.Deserialize<EditSessionChange>(json, Web)!;

        Assert.Contains("\"kind\":\"bookmarks\"", json, StringComparison.Ordinal);
        Assert.Contains("\"bookmarks\":{\"ops\":[{\"id\":\"1\",\"kind\":\"add\"", json, StringComparison.Ordinal);
        Assert.Equal(["add", "rename", "change_time", "import", "start_over"], back.Bookmarks!.Ops.Select(op => op.Kind));
        Assert.Equal(724, back.Bookmarks.Ops[2].PositionSeconds);
        Assert.Equal(bookmark, Assert.Single(back.Bookmarks.Ops[3].Items).ReplacesBookmarkId);
        Assert.DoesNotContain("\"bookmarks\":null", JsonSerializer.Serialize(new EditSessionChange { Id = "d", Kind = EditSessionChangeKinds.Details, EntityId = title }), StringComparison.Ordinal);
    }

    [Fact]
    public void EveryOperationKind_IsKnown_AndAnythingElseIsNot()
    {
        Assert.All(EditorBookmarkOpKinds.All, kind => Assert.True(EditorBookmarkOpKinds.IsKnown(kind)));
        Assert.Equal(6, EditorBookmarkOpKinds.All.Count);
        Assert.False(EditorBookmarkOpKinds.IsKnown("promote"));
        Assert.False(EditorBookmarkOpKinds.IsKnown(null));
    }

    [Fact]
    public void ATitlesTabRoundTrips_WithBothListsAndItsState()
    {
        var tab = new EditorBookmarksDto
        {
            WorkId = Guid.NewGuid(),
            State = EditorBookmarkStates.Definitive,
            Everyone = [new EditorBookmarkRowDto { Id = Guid.NewGuid(), Name = "Opening", PositionSeconds = 724, OwnerName = "Ada", FrameUrl = "/api/v1/player/bookmarks/x/frame?v=1", InEveryone = true }],
            FromProfiles = [new EditorBookmarkRowDto { Id = Guid.NewGuid(), PositionSeconds = 3000, OwnerName = "Ben", IsYours = false }],
            HasTimes = true,
            HasFrames = true,
            RunningTimeSeconds = 7200,
        };

        var json = JsonSerializer.Serialize(tab, Web);
        var back = JsonSerializer.Deserialize<EditorBookmarksDto>(json, Web)!;

        Assert.Contains("\"state\":\"definitive\"", json, StringComparison.Ordinal);
        Assert.Contains("\"fromProfiles\":[", json, StringComparison.Ordinal);
        Assert.Equal("Opening", Assert.Single(back.Everyone).Name);
        Assert.True(Assert.Single(back.Everyone).InEveryone);
        Assert.Equal("Ben", Assert.Single(back.FromProfiles).OwnerName);
        Assert.Equal(7200, back.RunningTimeSeconds);
    }

    [Fact]
    public void ANewTab_IsPooled_AndEmpty()
    {
        var tab = new EditorBookmarksDto();

        Assert.Equal(EditorBookmarkStates.Pooled, tab.State);
        Assert.Empty(tab.Everyone);
        Assert.Empty(tab.FromProfiles);
        Assert.False(tab.HasFrames);
    }

    [Fact]
    public void TheShowOverview_IsGroupedBySeason_AndTheShowFileCompareIsKeyedBySeasonAndEpisode()
    {
        var overview = new EditorBookmarkOverviewDto
        {
            RootTitle = "The Show",
            EveryoneTotal = 3,
            Groups = [new EditorBookmarkOverviewGroupDto { Title = "Season 1", Number = 1, BookmarkCount = 3, Rows = [new EditorBookmarkOverviewRowDto { Code = "S1 E2", Title = "Second", EveryoneCount = 3 }] }],
        };
        var compare = new EditorBookmarkShowCompareDto
        {
            Name = "A friend's set",
            UnmatchedEpisodes = 1,
            Episodes = [new EditorBookmarkShowCompareEpisodeDto { SeasonNumber = 1, EpisodeNumber = 2, WorkId = Guid.NewGuid(), NewCount = 2 }],
        };

        var back = JsonSerializer.Deserialize<EditorBookmarkOverviewDto>(JsonSerializer.Serialize(overview, Web), Web)!;
        var compareJson = JsonSerializer.Serialize(compare, Web);

        Assert.Equal("S1 E2", Assert.Single(Assert.Single(back.Groups).Rows).Code);
        Assert.Contains("\"seasonNumber\":1,\"episodeNumber\":2", compareJson, StringComparison.Ordinal);
        Assert.Equal(1, JsonSerializer.Deserialize<EditorBookmarkShowCompareDto>(compareJson, Web)!.UnmatchedEpisodes);
    }

    [Fact]
    public void TheLimits_AreSharedByTheEngineAndTheDashboard()
    {
        Assert.Equal(500, EditorBookmarkLimits.MaximumOps);
        Assert.Equal(BookmarkPackLimits.MaximumBookmarks, EditorBookmarkLimits.MaximumImportItems);
        Assert.True(EditorBookmarkLimits.MaximumShowFileBytes > BookmarkPackLimits.MaximumFileBytes);
    }
}
