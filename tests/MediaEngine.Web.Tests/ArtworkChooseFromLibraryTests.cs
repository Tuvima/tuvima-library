using Bunit;
using MediaEngine.Contracts.Artwork;
using MediaEngine.Contracts.Editing;
using MediaEngine.Contracts.Metadata;
using MediaEngine.Web.Components.Details;
using MediaEngine.Web.Components.Shared;
using MediaEngine.Web.Services.Editing;
using MediaEngine.Web.Services.Integration;
using MediaEngine.Web.Services.Theming;
using MediaEngine.Web.Tests.Support;
using Microsoft.Extensions.DependencyInjection;

namespace MediaEngine.Web.Tests;

/// <summary>
/// "Choose from Library" in the Artwork tab (universal editor v4.1 WP3): offered for every kind (a film, an episode, a Universe), it opens the
/// Library's own picture picker above the editor window, and what is chosen is picked in the tool like an upload: nothing is staged until
/// "Add version", and nothing is saved until Save changes.
/// </summary>
public sealed class ArtworkChooseFromLibraryTests : AsyncBunitContext
{
    private static readonly Guid SavedPicture = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000001");
    private static readonly Guid LibraryPicture = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000002");
    private static readonly Guid EpisodeId = Guid.Parse("22222222-aaaa-bbbb-cccc-000000000002");
    private static readonly Guid UniverseSubjectId = Guid.Parse("33333333-aaaa-bbbb-cccc-000000000003");

    private readonly DeviceContextService _device = new(EngineApiClientStub.CreateDefault());
    private readonly List<ArtworkAssetQuery> _queries = [];
    private readonly List<(string EntityType, Guid EntityId, ArtworkLinkRequest Request)> _links = [];
    private ArtworkEntityWorkspaceDto _workspace = default!;
    private IRenderedComponent<AppDialogHost> _dialogs = default!;
    private MediaEditorContextDto? _context;
    private EditorSubjectDto? _subject;
    private Guid _entityId;
    private string _entityKind = "Work";
    private string _slot = "CoverArt";

    public ArtworkChooseFromLibraryTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddLogging();
        Services.AddSingleton(_device);
        Services.AddSingleton(EngineApiClientStub.Create(stub =>
        {
            stub.SetHandler(nameof(IEngineApiClient.GetMediaEditorContextAsync), _ => Task.FromResult(_context));
            stub.SetHandler(nameof(IEngineApiClient.GetEditorSubjectAsync), _ => Task.FromResult(_subject));
            stub.SetHandler(nameof(IEngineApiClient.GetItemEditorSuggestionsAsync), _ => Task.FromResult<IReadOnlyList<string>>([]));
            stub.SetHandler(nameof(IEngineApiClient.GetEntityArtworkAsync), _ => Task.FromResult<ArtworkEntityWorkspaceDto?>(_workspace));
            stub.SetHandler(nameof(IEngineApiClient.GetArtworkAssetsAsync), args =>
            {
                _queries.Add((ArtworkAssetQuery)args![0]!);
                return Task.FromResult<ArtworkAssetPageDto?>(new ArtworkAssetPageDto(
                    [new ArtworkAssetDto(LibraryPicture, $"/art/{LibraryPicture:N}/large", $"/art/{LibraryPicture:N}/small", 1000, 1500, "Portrait", "tmdb", null, [])], 0, 48, 1));
            });
            stub.SetHandler(nameof(IEngineApiClient.LinkArtworkAssetAsync), args =>
            {
                _links.Add(((string)args![0]!, (Guid)args[1]!, (ArtworkLinkRequest)args[2]!));
                _workspace = Workspace(_entityId, _entityKind, _slot, SavedPicture, LibraryPicture);
                return Task.FromResult<ArtworkEntityWorkspaceDto?>(_workspace);
            });
        }));
        Render<AppPopoverHost>();
        _dialogs = Render<AppDialogHost>();
    }

    private static ArtworkEntityWorkspaceDto Workspace(Guid entityId, string kind, string slot, params Guid[] pictures) =>
        new(entityId, kind, pictures.Select((asset, index) => new ArtworkEntityVariantDto(
            Guid.NewGuid(), asset, ArtworkPickerRules.RoleFor(slot)!, null, slot, index == 0 && asset == SavedPicture, false,
            $"/art/{asset:N}/large", $"/art/{asset:N}/small", 1000, 1500, "Portrait", "tmdb", null)
        {
            FileName = "picture.jpg",
            ContentType = "image/jpeg",
        }).ToList());

    private static MediaEditorContextDto EpisodeContext() => new()
    {
        LaunchEntityId = EpisodeId,
        MediaType = "TV",
        InitialScope = "episode",
        AvailableTabs = ["details", "artwork"],
        Scopes =
        [
            new MediaEditorScopeDto
            {
                ScopeId = "episode",
                Label = "Episode",
                FieldEntityId = EpisodeId,
                DisplayTitle = "Static",
                CanEditFields = true,
                CanEditArtwork = true,
                ArtworkSlots = ["EpisodeStill"],
                FieldSnapshot = new MediaEditorScopeFieldSnapshotDto(),
            },
        ],
    };

    private static EditorSubjectDto UniverseSubject() => new()
    {
        Id = UniverseSubjectId,
        Kind = EditorSubjectKinds.Universe,
        Title = "Dune",
        ArtworkEntityKind = "Universe",
        ArtworkSlots = ["CoverArt", "Background"],
        Fields = [new EditorSubjectFieldDto { Key = "title", Label = "Name", Value = "Dune" }],
    };

    private async Task<(EditSessionState Session, IRenderedComponent<EditorArtworkTool> Cut)> OpenAsync(
        Guid entityId, string entityKind, string slot, EditTrailItem item, Func<ItemFieldEditSession, Task> load, MediaEditorContextDto? context = null, EditorSubjectDto? subject = null)
    {
        await _device.InitialiseAsync(new DeviceContextSnapshot("web", "desktop", false, FinePointer: true, Hover: true));
        (_entityId, _entityKind, _slot, _context, _subject) = (entityId, entityKind, slot, context, subject);
        _workspace = Workspace(entityId, entityKind, slot, SavedPicture);
        var api = Services.GetRequiredService<IEngineApiClient>();

        var fields = new ItemFieldEditSession(api);
        var mode = new EditingModeContext(_device, fields) { HasPermission = true };
        mode.Loader = () => load(fields);
        var session = new EditSessionState(new PendingEditList());
        var flow = new EditSessionFlow(session, mode, new EditSessionSaver(api), () => [item], () => Task.FromResult(true));
        Assert.True(await mode.EnterAsync());
        var preview = new ArtworkEditPreview(session, fields);
        var picker = new ArtworkPickerSession(api, session, fields, preview);
        await flow.SelectToolAsync(EditTool.Artwork);
        var cut = Render<EditorArtworkTool>(parameters => parameters
            .Add(tool => tool.Mode, mode)
            .Add(tool => tool.Picker, picker));
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(".tl-art-picker__version")));
        return (session, cut);
    }

    private Task<(EditSessionState Session, IRenderedComponent<EditorArtworkTool> Cut)> OpenFilmAsync() =>
        OpenAsync(
            EditingModeTestData.MovieId, "Work", "CoverArt", new EditTrailItem(EditingModeTestData.MovieId, "Dune: Part One", "Movie"),
            fields => fields.LoadAsync(EditingModeTestData.MovieId, "Movies", "movie", null),
            context: EditingModeTestData.MovieContext());

    private Task<(EditSessionState Session, IRenderedComponent<EditorArtworkTool> Cut)> OpenEpisodeAsync() =>
        OpenAsync(
            EpisodeId, "Work", "EpisodeStill", new EditTrailItem(EpisodeId, "Static", EditTrailKinds.Episode),
            fields => fields.LoadAsync(EpisodeId, "TV", "episode", null),
            context: EpisodeContext());

    private Task<(EditSessionState Session, IRenderedComponent<EditorArtworkTool> Cut)> OpenUniverseAsync() =>
        OpenAsync(
            UniverseSubjectId, "Universe", "CoverArt", new EditTrailItem(UniverseSubjectId, "Dune", EditTrailKinds.Universe),
            fields => fields.LoadSubjectAsync(UniverseSubjectId),
            subject: UniverseSubject());

    [Fact]
    public async Task AFilm_OffersChooseFromLibrary_InTheActionRow_AndOpensTheLibraryPicker()
    {
        var (_, cut) = await OpenFilmAsync();

        var button = cut.Find(".tl-art-picker__actions button.tl-art-picker__library");
        Assert.Equal("Choose from Library", button.TextContent.Trim());
        Assert.False(button.HasAttribute("disabled"));
        button.Click();

        _dialogs.WaitForElement(".artwork-picker-host");
        var query = Assert.Single(_queries);
        Assert.Equal(EditingModeTestData.MovieId, query.TargetEntityId);
        Assert.Equal("Work", query.TargetEntityType);
        Assert.Equal("Primary", query.TargetRole);
        Assert.Equal("CoverArt", query.TargetSourceAssetType);
        Assert.Equal(ArtworkPickerScope.Recommended, query.PickerScope);
    }

    [Fact]
    public async Task AnEpisode_OffersChooseFromLibrary_ForItsStill()
    {
        var (_, cut) = await OpenEpisodeAsync();

        cut.Find("button.tl-art-picker__library").Click();

        _dialogs.WaitForElement(".artwork-picker-host");
        var query = Assert.Single(_queries);
        Assert.Equal(EpisodeId, query.TargetEntityId);
        Assert.Equal("EpisodeStill", query.TargetSourceAssetType);
    }

    [Fact]
    public async Task AUniverse_OffersChooseFromLibrary_ForItsPictures()
    {
        var (_, cut) = await OpenUniverseAsync();

        // A Universe has no provider pictures to refresh, but it can still reuse what is already in the Library.
        Assert.Empty(cut.FindAll("button.tl-art-picker__refresh"));
        cut.Find("button.tl-art-picker__library").Click();

        _dialogs.WaitForElement(".artwork-picker-host");
        var query = Assert.Single(_queries);
        Assert.Equal(UniverseSubjectId, query.TargetEntityId);
        Assert.Equal("Universe", query.TargetEntityType);
        Assert.Equal(ArtworkPickerScope.Recommended, query.PickerScope);
    }

    [Fact]
    public async Task TheLibraryPicker_IsADialogOfItsOwn_NotPartOfTheEditorsMarkup()
    {
        var (_, cut) = await OpenFilmAsync();

        cut.Find("button.tl-art-picker__library").Click();

        _dialogs.WaitForElement(".artwork-picker-host");
        // It is drawn by the dialog host (a modal dialog opened after the editor's, so it sits in front of it), not inside the tool.
        Assert.Empty(cut.FindAll(".artwork-picker-host"));
        Assert.Single(_dialogs.FindAll("dialog.tl-dialog"));
    }

    [Fact]
    public async Task AChosenLibraryPicture_IsPickedLikeAnUpload_AndOnlyStagedByAddVersion()
    {
        var (session, cut) = await OpenFilmAsync();
        cut.Find("button.tl-art-picker__library").Click();
        _dialogs.WaitForElement(".artwork-picker-host");
        _dialogs.WaitForElement("button[aria-label='Select Library artwork']").Click();

        _dialogs.FindAll(".app-dialog-shell__footer button").Last().Click();

        // The picture is linked to the item as an unchosen version (the same as an upload), and picked in the tool.
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(".tl-art-picker__selected")));
        var link = Assert.Single(_links);
        Assert.Equal(EditingModeTestData.MovieId, link.EntityId);
        Assert.Equal(LibraryPicture, link.Request.ArtworkAssetId);
        Assert.False(link.Request.Preferred);
        Assert.Equal("CoverArt", link.Request.SourceAssetType);
        Assert.True(session.Pending.IsEmpty);
        Assert.Empty(_dialogs.FindAll(".artwork-picker-host"));

        cut.Find("button.tl-art-picker__use").Click();

        cut.WaitForAssertion(() => Assert.Equal(LibraryPicture.ToString("D"), Assert.Single(session.Pending.Changes).TargetId));
    }

    [Fact]
    public async Task ClosingTheLibraryPickerWithoutAChoice_GoesBackToThePreview_AndChangesNothing()
    {
        var (session, cut) = await OpenFilmAsync();
        cut.Find("button.tl-art-picker__library").Click();
        _dialogs.WaitForElement(".artwork-picker-host");

        _dialogs.FindAll(".app-dialog-shell__footer button").First().Click();

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(".tl-art-picker__preview")));
        Assert.Empty(cut.FindAll(".tl-art-picker__add-panel"));
        Assert.Empty(_links);
        Assert.True(session.Pending.IsEmpty);
    }
}
