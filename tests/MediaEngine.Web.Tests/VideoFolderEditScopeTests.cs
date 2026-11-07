using MediaEngine.Contracts.Items;
using MediaEngine.Contracts.Metadata;
using MediaEngine.Web.Services.Editing;
using MediaEngine.Web.Services.Integration;
using MediaEngine.Web.Tests.Support;

namespace MediaEngine.Web.Tests;

/// <summary>
/// The Dashboard's side of editing a folder of videos (universal editor v5, WP1, bug 21): the scope asked for is the folder's own (Series,
/// Section, Video), an item whose scope is not found is still opened as itself, and the folder's words and rules apply.
/// </summary>
public sealed class VideoFolderEditScopeTests
{
    private static readonly Guid SeriesId = Guid.Parse("d0000000-0000-0000-0000-000000000001");
    private static readonly Guid SectionId = Guid.Parse("d0000000-0000-0000-0000-000000000011");
    private static readonly Guid VideoId = Guid.Parse("d0000000-0000-0000-0000-000000000101");

    private static MediaEditorScopeDto Scope(string scopeId, Guid id, string title, int order) => new()
    {
        ScopeId = scopeId,
        Label = scopeId,
        Order = order,
        FieldEntityId = id,
        DisplayTitle = title,
        CanEditFields = true,
        CanEditArtwork = true,
    };

    private static ItemFieldEditSession SessionAnswering(MediaEditorContextDto context) =>
        new(EngineApiClientStub.Create(stub =>
        {
            stub.SetHandler(nameof(IEngineApiClient.GetMediaEditorContextAsync), _ => Task.FromResult<MediaEditorContextDto?>(context));
            stub.SetHandler(nameof(IEngineApiClient.GetItemExplicitAsync), _ => Task.FromResult<ItemExplicitResponse?>(null));
            stub.SetHandler(nameof(IEngineApiClient.GetItemEditorSuggestionsAsync), _ => Task.FromResult<IReadOnlyList<string>>([]));
        }));

    private static MediaEditorContextDto VideoInASection() => new()
    {
        LaunchEntityId = VideoId,
        MediaType = "Videos",
        InitialScope = "video",
        Scopes =
        [
            Scope("series", SeriesId, "Steph Curry MasterClass", 0),
            Scope("section", SectionId, "Ball Handling", 1),
            Scope("video", VideoId, "Warm Up", 2),
        ],
    };

    [Theory]
    [InlineData("Show", "series")]
    [InlineData("Season", "section")]
    [InlineData("Episode", "video")]
    public void InAFolderOfVideos_TheTrailsPlacesAskForTheFoldersOwnScopes(string kind, string scope)
    {
        Assert.Equal(scope, EditTrailKinds.ScopeOf(kind, "Videos"));
    }

    [Theory]
    [InlineData("Show", "series")]
    [InlineData("Season", "season")]
    [InlineData("Episode", "episode")]
    [InlineData("Album", "album")]
    [InlineData("Track", "track")]
    public void ForEveryOtherLibrary_TheTrailsPlacesAskForTheScopesTheyAlwaysDid(string kind, string scope)
    {
        Assert.Equal(scope, EditTrailKinds.ScopeOf(kind, "TV"));
        Assert.Equal(scope, EditTrailKinds.ScopeOf(kind));
    }

    [Fact]
    public async Task AVideoOpenedWithoutAScope_IsReadyOnTheVideoItself()
    {
        var fields = SessionAnswering(VideoInASection());

        await fields.LoadAsync(VideoId, "Videos", null, null);

        Assert.True(fields.Loaded);
        Assert.Equal(VideoId, fields.EntityId);
        Assert.Equal("video", fields.ScopeId);
    }

    [Fact]
    public async Task AVideoOpenedWithATvScopeName_IsStillOpenedAsItself_NotAsItsSeries()
    {
        var fields = SessionAnswering(VideoInASection());

        await fields.LoadAsync(VideoId, "TV", "episode", null);

        Assert.True(fields.Loaded);
        Assert.Equal(VideoId, fields.EntityId);
        Assert.Equal("video", fields.ScopeId);
    }

    [Fact]
    public async Task ASectionOpenedWithATvScopeName_IsOpenedAsTheSection()
    {
        var fields = SessionAnswering(new MediaEditorContextDto
        {
            LaunchEntityId = SectionId,
            MediaType = "Videos",
            InitialScope = "section",
            Scopes = [Scope("series", SeriesId, "Steph Curry MasterClass", 0), Scope("section", SectionId, "Ball Handling", 1)],
        });

        await fields.LoadAsync(SectionId, "TV", "season", null);

        Assert.Equal(SectionId, fields.EntityId);
        Assert.Equal("section", fields.ScopeId);
    }

    [Fact]
    public async Task TheEnginesChainOfAVideo_NamesTheSeriesSectionAndVideoInTheTrailsPlaces()
    {
        var fields = SessionAnswering(VideoInASection());

        await fields.LoadAsync(VideoId, "Videos", null, null);

        Assert.Equal([SeriesId, SectionId, VideoId], fields.ScopeChain.Select(step => step.EntityId));
        Assert.Equal([EditTrailKinds.Show, EditTrailKinds.Season, EditTrailKinds.Episode], fields.ScopeChain.Select(step => step.Kind));
    }

    [Theory]
    [InlineData("series", false)]
    [InlineData("section", false)]
    [InlineData("video", true)]
    public void AVideoCanBeMatched_ButTheFolderAndItsSectionsCannot(string scope, bool canMatch)
    {
        Assert.Equal(canMatch, MediaEditorMatchRule.CanMatch("Videos", scope));
    }

    [Theory]
    [InlineData("series", "Video folder")]
    [InlineData("section", "Section")]
    [InlineData("video", "Video")]
    public void TheEditorsKindLine_UsesTheFoldersOwnWords(string scope, string expected)
    {
        Assert.Equal(expected, EditorHeaderRules.KindLabel("Videos", scope));
    }
}
