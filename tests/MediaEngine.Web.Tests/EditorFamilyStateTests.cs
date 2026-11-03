using MediaEngine.Contracts.Metadata;
using MediaEngine.Web.Services.Editing;
using MediaEngine.Web.Services.Integration;
using MediaEngine.Web.Tests.Support;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaEngine.Web.Tests;

/// <summary>
/// What the editor's family tree knows (universal editor v3 WP2, D3): this show, album or series and nothing else, built from the Engine's
/// structure-only navigator and paged owned-children reads. The Engine is a hand-written stub; nothing is written anywhere.
/// </summary>
public sealed class EditorFamilyStateTests
{
    private static readonly Guid ShowId = EditTrailTestData.ShowId;
    private static readonly Guid SeasonOne = EditTrailTestData.SeasonOneId;
    private static readonly Guid SeasonTwo = EditTrailTestData.SeasonTwoId;
    private readonly List<string> _asked = [];

    private EditorFamilyState Family(Action<EngineApiClientStub>? more = null)
    {
        var api = EngineApiClientStub.Create(stub =>
        {
            EditTrailTestData.RegisterFamily(stub, _asked);
            more?.Invoke(stub);
        });
        return new EditorFamilyState(api, NullLogger.Instance);
    }

    private static string[] Titles(EditorFamilyState family) =>
        family.Rows().Where(row => row.Type == FamilyRowType.Item).Select(row => row.Title).ToArray();

    [Fact]
    public async Task AnEpisode_GivesItsShowsFamily_WithSeasonsClosedUntilTheirParentsAreNamed()
    {
        var family = Family();

        await family.EnsureAsync(EditTrailTestData.EpisodeTwoId);

        Assert.Equal(EditorFamilyKind.Show, family.Kind);
        Assert.Equal("This show", family.Heading);
        Assert.Equal(["Le Continental", "Season 1", "Season 2"], Titles(family));
        // Only the shape and the root's own files were read; no season's files until one is opened.
        Assert.Equal([$"navigator:{EditTrailTestData.EpisodeTwoId:N}", $"children:{ShowId:N}:page1"], _asked);
    }

    [Fact]
    public async Task KnownParents_OpenWithoutAClick_SoTheOpenedItemIsInView()
    {
        var family = Family();

        await family.EnsureAsync(EditTrailTestData.EpisodeTwoId, [ShowId, SeasonOne]);

        Assert.Equal(["Le Continental", "Season 1", "Episode 1", "Episode 2", "Season 2"], Titles(family));
        var episodeOne = family.Rows().Single(row => row.Title == "Episode 1");
        var episodeTwo = family.Rows().Single(row => row.Title == "Episode 2");
        Assert.True(episodeOne.Matched);
        Assert.Null(episodeOne.Reason);
        Assert.False(episodeTwo.Matched);
        Assert.Contains("No show, season or episode", episodeTwo.Reason);
    }

    [Fact]
    public async Task TheTrailToAnEpisode_IsShowThenSeasonThenEpisode_ForSwitchingToIt()
    {
        var family = Family();
        await family.EnsureAsync(EditTrailTestData.EpisodeTwoId, [ShowId, SeasonOne]);

        var trail = family.TrailTo(EditTrailTestData.EpisodeTwoId);

        Assert.Equal([ShowId, SeasonOne, EditTrailTestData.EpisodeTwoId], trail!.Select(step => step.EntityId));
        Assert.Equal([EditTrailKinds.Show, EditTrailKinds.Season, EditTrailKinds.Episode], trail!.Select(step => step.Kind));
        Assert.Null(family.TrailTo(Guid.NewGuid()));
    }

    [Fact]
    public async Task OpeningASeason_ReadsItsFilesOnce_AndClosingItKeepsThem()
    {
        var family = Family();
        await family.EnsureAsync(ShowId);

        await family.ToggleAsync(SeasonTwo);
        Assert.Contains("Episode 3", Titles(family));
        await family.ToggleAsync(SeasonTwo);
        Assert.DoesNotContain("Episode 3", Titles(family));
        await family.ToggleAsync(SeasonTwo);

        Assert.Contains("Episode 3", Titles(family));
        Assert.Equal(1, _asked.Count(read => read == $"children:{SeasonTwo:N}:page1"));
    }

    [Fact]
    public async Task AnAlbum_GivesItsTracks()
    {
        var family = Family();

        await family.EnsureAsync(EditTrailTestData.TrackTwoId);

        Assert.Equal(EditorFamilyKind.Album, family.Kind);
        Assert.Equal("This album", family.Heading);
        Assert.Equal(["OK Computer", "Airbag", "Paranoid Android"], Titles(family));
        Assert.All(family.Rows().Where(row => row.Title is "Airbag" or "Paranoid Android"), row => Assert.Equal(EditTrailKinds.Track, row.Kind));
    }

    [Fact]
    public async Task APersonACollectionOrAnUnknownItem_HasNoFamily_AndTheColumnStaysOut()
    {
        var family = Family();

        await family.EnsureAsync(Guid.NewGuid());

        Assert.Equal(EditorFamilyKind.None, family.Kind);
        Assert.False(family.Visible);
        Assert.Empty(family.Rows());
    }

    [Theory]
    [InlineData("Movies", false)]
    [InlineData("Books", true)]
    public async Task AFilmOrAStandaloneBook_HasNoFamily(string mediaType, bool rootIsAWorkNotAParent)
    {
        var id = Guid.NewGuid();
        var family = Family(stub => stub.SetHandler(nameof(IEngineApiClient.GetMediaEditorNavigatorAsync), _ =>
            Task.FromResult<MediaEditorNavigatorDto?>(new MediaEditorNavigatorDto
            {
                Enabled = true,
                MediaType = mediaType,
                ContainerEntityId = id,
                Nodes = [new MediaEditorNavigatorNodeDto { EntityId = id, NodeId = id, NodeKind = "book", Title = "Dune", IsRoot = true, IsLeaf = rootIsAWorkNotAParent }],
            })));

        await family.EnsureAsync(id);

        Assert.Equal(EditorFamilyKind.None, family.Kind);
        Assert.False(family.Visible);
    }

    [Fact]
    public async Task ABookSeries_ListsItsBooks_AndShowsAnAudiobookAsAVariantOfTheSameBook()
    {
        var series = Guid.NewGuid();
        var book = Guid.NewGuid();
        var family = Family(stub =>
        {
            stub.SetHandler(nameof(IEngineApiClient.GetMediaEditorNavigatorAsync), _ =>
                Task.FromResult<MediaEditorNavigatorDto?>(new MediaEditorNavigatorDto
                {
                    Enabled = true,
                    MediaType = "Books",
                    ContainerEntityId = series,
                    Nodes = [new MediaEditorNavigatorNodeDto { EntityId = series, NodeId = series, NodeKind = "series", Title = "Dune Novels", IsRoot = true }],
                }));
            stub.SetHandler(nameof(IEngineApiClient.GetMediaEditorOwnedChildrenAsync), _ =>
                Task.FromResult<MediaEditorOwnedChildSearchDto?>(new MediaEditorOwnedChildSearchDto
                {
                    TotalCount = 2,
                    Items =
                    [
                        new MediaEditorOwnedChildDto { WorkId = book, AssetId = Guid.NewGuid(), Title = "Dune", MatchState = "matched", SourceFilePath = "/books/dune.epub" },
                        new MediaEditorOwnedChildDto { WorkId = book, AssetId = Guid.NewGuid(), Title = "Dune", MatchState = "matched", SourceFilePath = "/audio/dune.m4b" },
                    ],
                }));
        });

        await family.EnsureAsync(book);

        Assert.Equal(EditorFamilyKind.Series, family.Kind);
        Assert.Equal("This series", family.Heading);
        var row = Assert.Single(family.Rows(), candidate => candidate.EntityId == book);
        Assert.Equal("Also as audiobook", row.Variant);
    }

    [Fact]
    public async Task FilesWithNoEpisode_SitUnderTheirOwnHeading_AtTheShow()
    {
        var stray = Guid.NewGuid();
        var family = Family(stub => stub.SetHandler(nameof(IEngineApiClient.GetMediaEditorOwnedChildrenAsync), args =>
        {
            var parent = (Guid)args![0]!;
            return Task.FromResult<MediaEditorOwnedChildSearchDto?>(new MediaEditorOwnedChildSearchDto
            {
                TotalCount = parent == ShowId ? 1 : 0,
                Items = parent == ShowId
                    ? [new MediaEditorOwnedChildDto { WorkId = stray, AssetId = Guid.NewGuid(), Title = "random clip.mkv", MatchState = "unmatched", MatchReason = "The file name has no season and episode number." }]
                    : [],
            });
        }));

        await family.EnsureAsync(ShowId);

        var rows = family.Rows().ToList();
        var heading = rows.FindIndex(row => row.Type == FamilyRowType.Heading);
        Assert.True(heading > 0);
        Assert.Equal("Files with no episode", rows[heading].Title);
        Assert.Equal(stray, rows[heading + 1].EntityId);
        Assert.False(rows[heading + 1].Matched);
    }

    [Fact]
    public async Task ALongSeason_PagesInFiftyAtATime_UntilTheEnd()
    {
        var pages = new List<int>();
        var family = Family(stub => stub.SetHandler(nameof(IEngineApiClient.GetMediaEditorOwnedChildrenAsync), args =>
        {
            var parent = (Guid)args![0]!;
            var page = (int)args[2]!;
            if (parent != SeasonOne)
            {
                return Task.FromResult<MediaEditorOwnedChildSearchDto?>(new MediaEditorOwnedChildSearchDto());
            }

            pages.Add(page);
            var items = Enumerable.Range((page - 1) * 50 + 1, Math.Min(50, 120 - (page - 1) * 50))
                .Select(n => new MediaEditorOwnedChildDto { WorkId = Guid.NewGuid(), AssetId = Guid.NewGuid(), Title = $"Episode {n}", MatchedNumber = n.ToString(), MatchState = "matched" })
                .ToList();
            return Task.FromResult<MediaEditorOwnedChildSearchDto?>(new MediaEditorOwnedChildSearchDto { TotalCount = 120, Page = page, Items = items });
        }));
        await family.EnsureAsync(ShowId);

        await family.ToggleAsync(SeasonOne);
        Assert.Equal(50, family.Rows().Count(row => row.Title.StartsWith("Episode ", StringComparison.Ordinal)));
        var more = Assert.Single(family.Rows(), row => row.Type == FamilyRowType.More);
        Assert.Equal("Show more (70 left)", more.Title);

        await family.LoadMoreAsync(SeasonOne);
        await family.LoadMoreAsync(SeasonOne);

        Assert.Equal(120, family.Rows().Count(row => row.Title.StartsWith("Episode ", StringComparison.Ordinal)));
        Assert.DoesNotContain(family.Rows(), row => row.Type == FamilyRowType.More);
        Assert.Equal([1, 2, 3], pages);
    }

    [Fact]
    public async Task WhileTheShapeIsBeingRead_RowSkeletonsShow_AndNothingElseWaits()
    {
        var gate = new TaskCompletionSource<MediaEditorNavigatorDto?>();
        var family = Family(stub => stub.SetHandler(nameof(IEngineApiClient.GetMediaEditorNavigatorAsync), _ => gate.Task));

        var reading = family.EnsureAsync(ShowId);

        Assert.True(family.Loading);
        Assert.True(family.Visible);
        Assert.All(family.Rows(), row => Assert.Equal(FamilyRowType.Skeleton, row.Type));
        Assert.NotEmpty(family.Rows());
        gate.SetResult(null);
        await reading;
        Assert.False(family.Visible);
    }

    [Fact]
    public async Task Reset_ForgetsTheFamily_SoTheNextItemStartsClean()
    {
        var family = Family();
        await family.EnsureAsync(ShowId, [SeasonOne]);

        family.Reset();

        Assert.Equal(EditorFamilyKind.None, family.Kind);
        Assert.Empty(family.Rows());
        Assert.Null(family.TrailTo(SeasonOne));
    }

    [Fact]
    public async Task AFilesPageThatCannotBeRead_OffersTryAgain_AndRetryReadsItAgain()
    {
        var fail = true;
        var family = Family(stub => stub.SetHandler(nameof(IEngineApiClient.GetMediaEditorOwnedChildrenAsync), args =>
        {
            var parent = (Guid)args![0]!;
            return parent == SeasonOne && fail
                ? Task.FromResult<MediaEditorOwnedChildSearchDto?>(null)
                : Task.FromResult<MediaEditorOwnedChildSearchDto?>(new MediaEditorOwnedChildSearchDto
                {
                    TotalCount = parent == SeasonOne ? 1 : 0,
                    Items = parent == SeasonOne ? [new MediaEditorOwnedChildDto { WorkId = Guid.NewGuid(), AssetId = Guid.NewGuid(), Title = "Episode 1", MatchState = "matched" }] : [],
                });
        }));
        await family.EnsureAsync(ShowId);

        await family.ToggleAsync(SeasonOne);
        var note = Assert.Single(family.Rows(), row => row.Type == FamilyRowType.Note);
        Assert.Equal("Retry", note.Kind);

        fail = false;
        await family.RetryAsync(SeasonOne);

        Assert.Contains("Episode 1", Titles(family));
    }
}
