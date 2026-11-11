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

    [Fact]
    public async Task AnEpisode_GivesItsShowsFamily_WithSeasonsClosedUntilTheirParentsAreNamed()
    {
        var family = Family();

        await family.EnsureAsync(EditTrailTestData.EpisodeTwoId);

        Assert.Equal(EditorFamilyKind.Show, family.Kind);
        Assert.True(family.Visible);
        // Only the shape and the root's own files were read; no season's files until one is opened.
        Assert.Equal([$"navigator:{EditTrailTestData.EpisodeTwoId:N}", $"children:{ShowId:N}:page1"], _asked);
    }

    [Fact]
    public async Task KnownParents_OpenWithoutAClick_SoTheOpenedItemIsKnownToTheTree()
    {
        var family = Family();

        await family.EnsureAsync(EditTrailTestData.EpisodeTwoId, [ShowId, SeasonOne]);

        Assert.Equal(SeasonOne, family.ParentOf(EditTrailTestData.EpisodeTwoId));
        Assert.True(family.IsMatched(EditTrailTestData.EpisodeTwoId) == false);
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
    public async Task AnAlbum_GivesItsTracks()
    {
        var family = Family();

        await family.EnsureAsync(EditTrailTestData.TrackTwoId);

        Assert.Equal(EditorFamilyKind.Album, family.Kind);
        Assert.Equal(EditTrailKinds.Track, family.TrailTo(EditTrailTestData.TrackTwoId)!.Last().Kind);
    }

    [Fact]
    public async Task APersonACollectionOrAnUnknownItem_HasNoFamily_AndTheColumnStaysOut()
    {
        var family = Family();

        await family.EnsureAsync(Guid.NewGuid());

        Assert.Equal(EditorFamilyKind.None, family.Kind);
        Assert.False(family.Visible);
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
    public async Task Reset_ForgetsTheFamily_SoTheNextItemStartsClean()
    {
        var family = Family();
        await family.EnsureAsync(ShowId, [SeasonOne]);

        family.Reset();

        Assert.Equal(EditorFamilyKind.None, family.Kind);
        Assert.Null(family.TrailTo(SeasonOne));
    }
}
