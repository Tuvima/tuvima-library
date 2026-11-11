using Bunit;
using MediaEngine.Contracts.Editing;
using MediaEngine.Contracts.Metadata;
using MediaEngine.Web.Components.Details;
using MediaEngine.Web.Components.Editor;
using MediaEngine.Web.Components.Shared;
using MediaEngine.Web.Services.Editing;
using MediaEngine.Web.Services.Integration;
using MediaEngine.Web.Services.Theming;
using MediaEngine.Web.Tests.Support;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaEngine.Web.Tests;

/// <summary>
/// A Series is read-only and a Universe takes overrides only (universal editor v5 D7, screen H, acceptance A10): Edit on a Series shows its
/// name and ordered members locked, a notice that says why, no Artwork tab and Close instead of Save; the dropdown reaches a member, which
/// edits as any item does; a Universe's provider facts have no unlock while its description override does. The Engine is a hand-written stub.
/// </summary>
public sealed class SeriesReadOnlyEditorTests : AsyncBunitContext
{
    private static readonly Guid SeriesId = Guid.Parse("5e000000-0000-0000-0000-000000000001");
    private static readonly Guid BookOne = Guid.Parse("5e000000-0000-0000-0000-0000000000b1");
    private static readonly Guid BookTwo = Guid.Parse("5e000000-0000-0000-0000-0000000000b2");
    private static readonly Guid SubjectId = Guid.Parse("5e000000-0000-0000-0000-0000000000a1");

    private readonly DeviceContextService _device = new(EngineApiClientStub.CreateDefault());
    private EditorSubjectDto _subject = Grouping(EditorSubjectKinds.Series);

    public SeriesReadOnlyEditorTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddLogging();
        Services.AddSingleton(_device);
        Services.AddSingleton(EngineApiClientStub.Create(stub =>
        {
            stub.SetHandler(nameof(IEngineApiClient.GetEditorSubjectAsync), _ => Task.FromResult<EditorSubjectDto?>(_subject));
            stub.SetHandler(nameof(IEngineApiClient.GetItemEditorSuggestionsAsync), _ => Task.FromResult<IReadOnlyList<string>>([]));
            stub.SetHandler(nameof(IEngineApiClient.GetEditorNavLevelAsync), _ => Task.FromResult<EditorHeaderLevelDto?>(SeriesHeader().Level));
            stub.SetHandler(nameof(IEngineApiClient.GetMediaEditorContextAsync), args => Task.FromResult<MediaEditorContextDto?>(
                (Guid)args![0]! == SeriesId ? BooksContext(SeriesId, "Dune Novels", "series", SeriesHeader()) : BooksContext((Guid)args[0]!, "Dune", "book", BookHeader((Guid)args[0]!))));
        }));
        Render<AppPopoverHost>();
    }

    private sealed record Editor(EditingModeContext Mode, EditSessionState Session, EditSessionFlow Flow, ItemFieldEditSession Fields);

    private async Task<Editor> StartSubjectAsync(EditorSubjectDto subject)
    {
        _subject = subject;
        await _device.InitialiseAsync(new DeviceContextSnapshot("web", "desktop", false, FinePointer: true, Hover: true));
        var api = Services.GetRequiredService<IEngineApiClient>();
        var fields = new ItemFieldEditSession(api);
        var mode = new EditingModeContext(_device, fields) { HasPermission = true };
        mode.Loader = () => fields.LoadSubjectAsync(SubjectId);
        var session = new EditSessionState(new PendingEditList());
        var flow = new EditSessionFlow(session, mode, new EditSessionSaver(api), () => [new EditTrailItem(SubjectId, subject.Title, subject.Kind)], () => Task.FromResult(false));
        Assert.True(await mode.EnterAsync());
        return new Editor(mode, session, flow, fields);
    }

    private async Task<Editor> StartSeriesOfBooksAsync()
    {
        await _device.InitialiseAsync(new DeviceContextSnapshot("web", "desktop", false, FinePointer: true, Hover: true));
        var api = Services.GetRequiredService<IEngineApiClient>();
        var fields = new ItemFieldEditSession(api);
        var mode = new EditingModeContext(_device, fields) { HasPermission = true };
        mode.Loader = () => fields.LoadAsync(SeriesId, "Books", "series", null);
        var session = new EditSessionState(new PendingEditList());
        var flow = new EditSessionFlow(session, mode, new EditSessionSaver(api), () => [new EditTrailItem(SeriesId, "Dune Novels", "Series")], () => Task.FromResult(false))
        {
            OpenItem = async item =>
            {
                await fields.LoadAsync(item.EntityId, "Books", "book", null);
                return fields.Loaded && fields.EntityId == item.EntityId;
            },
        };
        Assert.True(await mode.EnterAsync());
        return new Editor(mode, session, flow, fields);
    }

    // The window as the pop-over draws it: the header's nav row, the tabs, the Details tool and the footer.
    private IRenderedComponent<CascadingValue<EditingModeContext>> RenderWindow(Editor editor, bool withHeader = false)
    {
        editor.Flow.SelectToolAsync(EditTool.Details).GetAwaiter().GetResult();
        return Render<CascadingValue<EditingModeContext>>(parameters => parameters
            .Add(host => host.Value, editor.Mode)
            .Add(host => host.ChildContent, builder =>
            {
                builder.OpenComponent<EditorPanelHost>(0);
                builder.AddComponentParameter(1, nameof(EditorPanelHost.Mode), editor.Mode);
                builder.AddComponentParameter(2, nameof(EditorPanelHost.Flow), editor.Flow);
                builder.AddComponentParameter(3, nameof(EditorPanelHost.ShowHistory), true);
                if (withHeader)
                {
                    builder.AddComponentParameter(4, nameof(EditorPanelHost.ItemHeader), (RenderFragment)(header =>
                    {
                        header.OpenComponent<EditorItemHeader>(0);
                        header.AddComponentParameter(1, nameof(EditorItemHeader.Flow), editor.Flow);
                        header.AddComponentParameter(2, nameof(EditorItemHeader.Fields), editor.Fields);
                        header.AddComponentParameter(3, nameof(EditorItemHeader.Family), new EditorFamilyState(Services.GetRequiredService<IEngineApiClient>(), NullLogger.Instance));
                        header.CloseComponent();
                    }));
                }

                builder.AddComponentParameter(5, nameof(EditorPanelHost.ChildContent), (RenderFragment<EditTool>)(tool => inner =>
                {
                    if (tool == EditTool.Details)
                    {
                        inner.OpenComponent<EditorDetailsPanel>(0);
                        inner.AddComponentParameter(1, nameof(EditorDetailsPanel.Mode), editor.Mode);
                        inner.AddComponentParameter(2, nameof(EditorDetailsPanel.Session), editor.Session);
                        inner.AddComponentParameter(3, nameof(EditorDetailsPanel.Flow), editor.Flow);
                        inner.CloseComponent();
                    }
                }));
                builder.CloseComponent();
            }));
    }

    private static IEnumerable<string> Tabs(IRenderedComponent<CascadingValue<EditingModeContext>> window) =>
        window.FindAll(".tl-editor-window__tabs [role='tab']").Select(tab => tab.TextContent.Trim());

    // -- A Series of the Library ----------------------------------------------------------------

    [Fact]
    public async Task EditOnASeries_ShowsItsNameLocked_WithNoWayToUnlockIt_AndTheNoticeWhy()
    {
        var editor = await StartSubjectAsync(Grouping(EditorSubjectKinds.Series));
        var window = RenderWindow(editor);

        window.WaitForElement(".tl-editor-details");

        Assert.Single(window.FindAll("[data-series-note]"));
        Assert.Contains("can't be edited", window.Find("[data-series-note]").TextContent, StringComparison.Ordinal);
        Assert.Single(window.FindAll(".tl-editor-field--locked[data-field-key='title']"));
        Assert.Empty(window.FindAll("[data-lock-state]"));
        Assert.Empty(window.FindAll(".tl-editor-details input, .tl-editor-details textarea"));
        Assert.Empty(window.FindAll("button.tl-editor-field__reset"));
    }

    [Fact]
    public async Task EditOnASeries_OffersDetailsAndHistory_WithNoArtworkTab()
    {
        var editor = await StartSubjectAsync(Grouping(EditorSubjectKinds.Series));

        var window = RenderWindow(editor);

        Assert.Equal(["Details", "History"], Tabs(window));
        Assert.False(editor.Fields.CanEditArtwork);
    }

    [Fact]
    public async Task EditOnASeries_ShowsOnlyClose_NeverSaveDiscardDeleteOrAnUnsavedCount()
    {
        var editor = await StartSubjectAsync(Grouping(EditorSubjectKinds.Series));

        var window = RenderWindow(editor);

        var footer = window.Find("footer.tl-editor-window__footer");
        Assert.Equal("true", footer.GetAttribute("data-read-only"));
        Assert.Equal(["Close"], footer.QuerySelectorAll("button").Select(button => button.TextContent.Trim()));
        Assert.Empty(window.FindAll("button.tl-editor-window__save"));
        Assert.Empty(window.FindAll(".tl-editor-window__footer-start"));
        Assert.DoesNotContain("unsaved", footer.TextContent, StringComparison.OrdinalIgnoreCase);
    }

    // -- A Series of books, reached from the header -----------------------------------------------

    [Fact]
    public async Task EditOnASeriesOfBooks_ListsItsBooksInOrder_Locked_WithCloseAndNoSave()
    {
        var editor = await StartSeriesOfBooksAsync();
        var window = RenderWindow(editor, withHeader: true);

        window.WaitForElement("[data-series-members]");

        Assert.True(editor.Fields.IsReadOnlySeries);
        Assert.Equal(["1 · Dune", "2 · Dune Messiah"], window.FindAll("[data-series-members] li").Select(line => line.TextContent.Trim()));
        Assert.Contains("Books in order", window.Find("[data-series-members]").GetAttribute("aria-label"), StringComparison.Ordinal);
        Assert.Equal(["Details", "History"], Tabs(window));
        Assert.Empty(window.FindAll("button.tl-editor-window__save"));
        Assert.Equal(["Close"], window.FindAll("footer.tl-editor-window__footer button").Select(button => button.TextContent.Trim()));
    }

    [Fact]
    public async Task PickingABookInTheSeriesDropdown_OpensTheBooksEditor_WhichIsEditable()
    {
        var editor = await StartSeriesOfBooksAsync();
        var window = RenderWindow(editor, withHeader: true);
        window.WaitForElement("#tl-hnav-item").Click();

        window.Find($"#tl-hnav-option-{BookOne:N}").Click();

        window.WaitForAssertion(() =>
        {
            Assert.Equal(BookOne, editor.Fields.EntityId);
            Assert.False(editor.Fields.IsReadOnlySeries);
            Assert.True(editor.Fields.CanEditFields);
            Assert.Empty(window.FindAll("[data-series-note]"));
            Assert.NotEmpty(window.FindAll("button.tl-editor-window__save"));
        });
    }

    // -- A Universe: mostly read-only -------------------------------------------------------------

    [Fact]
    public async Task AUniversesNameAndDescription_StartLocked_AndEachUnlocksAsAnOverride()
    {
        var editor = await StartSubjectAsync(Grouping(EditorSubjectKinds.Universe));
        var window = RenderWindow(editor);
        window.WaitForElement(".tl-editor-details");

        Assert.Equal("locked", window.LockStateOf("title"));
        Assert.Equal("locked", window.LockStateOf("description"));
        Assert.Empty(window.FindAll("[data-series-note]"));

        window.UnlockField("title");
        window.Find(".tl-editor-field[data-field-key='title'] input").Input("Arrakis");

        window.WaitForAssertion(() =>
        {
            Assert.True(editor.Session.Pending.TryGetField(SubjectId, "title", out var staged));
            Assert.Equal("Arrakis", staged);
        });
    }

    [Fact]
    public async Task ALibrarySeries_ListsItsMembersInOrder_AndPickingOneOpensThatMembersEditor()
    {
        var series = Grouping(EditorSubjectKinds.Series);
        series.Members.Add(new EditorSubjectMemberDto { Id = BookOne, Title = "Dune", Number = 1 });
        series.Members.Add(new EditorSubjectMemberDto { Id = BookTwo, Title = "Dune Messiah", Number = 2 });
        var editor = await StartSubjectAsync(series);
        var opened = new List<Guid>();
        editor.Flow.OpenItem = item =>
        {
            opened.Add(item.EntityId);
            return Task.FromResult(true);
        };
        var window = RenderWindow(editor);
        window.WaitForElement("[data-series-members]");

        Assert.Equal(["1 · Dune", "2 · Dune Messiah"], window.FindAll("[data-series-members] li").Select(line => line.TextContent.Trim()));
        window.FindAll("[data-series-members] li button")[1].Click();

        window.WaitForAssertion(() => Assert.Equal([BookTwo], opened));
    }

    [Fact]
    public async Task AUniversesFooter_KeepsSaveChanges_BecauseItsOverridesCanBeSaved()
    {
        var editor = await StartSubjectAsync(Grouping(EditorSubjectKinds.Universe));

        var window = RenderWindow(editor);

        Assert.NotEmpty(window.FindAll("button.tl-editor-window__save"));
        Assert.Empty(window.FindAll("footer[data-read-only]"));
    }

    // -- Data -------------------------------------------------------------------------------------

    private static EditorSubjectDto Grouping(string kind) => new()
    {
        Id = SubjectId,
        Kind = kind,
        Title = "Dune",
        ArtworkEntityKind = "Collection",
        ArtworkSlots = kind == EditorSubjectKinds.Series ? [] : ["CoverArt", "Background"],
        Fields =
        [
            new EditorSubjectFieldDto { Key = "title", Label = "Name", Value = "Dune", CanonicalValue = "Dune", SourceLabel = "Provider" },
            new EditorSubjectFieldDto { Key = "description", Label = "Description", InputKind = EditorSubjectInputKinds.TextArea, Value = "A desert world.", CanonicalValue = "A desert world.", SourceLabel = "Provider" },
        ],
    };

    private static EditorNavItemDto Book(Guid id, int number, string title) => new() { Id = id, Kind = "book", Number = number, Title = title };

    private static EditorHeaderDto SeriesHeader() => new()
    {
        Kind = EditorKindNames.Series,
        Root = new EditorHeaderRootDto { Id = SeriesId, Title = "Dune Novels", MediaType = "Books" },
        CurrentId = SeriesId,
        Label = "Series",
        LabelIsKind = true,
        ShowHome = true,
        HomeId = SeriesId,
        HomeIsCurrent = true,
        ShowItemDropdown = true,
        Level = new EditorHeaderLevelDto
        {
            LevelId = SeriesId,
            Total = 2,
            PageSize = 50,
            Items = [Book(BookOne, 1, "Dune"), Book(BookTwo, 2, "Dune Messiah")],
            SupportingFiles = new EditorHeaderSupportingFilesDto(),
        },
        Config = EditorHeaderTestData.Config(EditorKindNames.Series, "details", "history"),
    };

    private static EditorHeaderDto BookHeader(Guid id) => new()
    {
        Kind = EditorKindNames.Book,
        Root = new EditorHeaderRootDto { Id = SeriesId, Title = "Dune Novels", MediaType = "Books" },
        CurrentId = id,
        Label = "Dune Novels",
        ShowHome = true,
        HomeId = SeriesId,
        ShowItemDropdown = true,
        Level = SeriesHeader().Level,
        Config = EditorHeaderTestData.Config(EditorKindNames.Book, "details", "artwork", "match", "history"),
    };

    private static MediaEditorContextDto BooksContext(Guid id, string title, string scope, EditorHeaderDto header) => new()
    {
        LaunchEntityId = id,
        MediaType = "Books",
        InitialScope = scope,
        AvailableTabs = ["details", "artwork", "links", "history"],
        Header = header,
        Scopes =
        [
            new MediaEditorScopeDto
            {
                ScopeId = scope,
                Label = scope,
                FieldEntityId = id,
                DisplayTitle = title,
                CanEditFields = true,
                CanEditArtwork = true,
                ArtworkSlots = ["CoverArt"],
                FieldSnapshot = new MediaEditorScopeFieldSnapshotDto
                {
                    CanonicalFields = [new MediaEditorScopedFieldValueDto { Key = "title", Value = title, ProviderName = "openlibrary" }],
                    DisplayOverrides = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
                    AllowedOverrideKeys = ["title", "description"],
                },
            },
        ],
    };
}
