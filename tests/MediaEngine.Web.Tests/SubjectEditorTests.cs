using Bunit;
using MediaEngine.Contracts.Editing;
using MediaEngine.Web.Components.Details;
using MediaEngine.Web.Components.Shared;
using MediaEngine.Web.Services.Editing;
using MediaEngine.Web.Services.Integration;
using MediaEngine.Web.Services.Theming;
using MediaEngine.Web.Tests.Support;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace MediaEngine.Web.Tests;

/// <summary>
/// The universal editor on a person, a Series, a curated collection and a Universe (spec D17, D17a): which tools the bar offers for each
/// (Artwork only where there are pictures; never Match here), the Details fields and read-only facts, staging instead of saving, the
/// wording of the way back to the canonical value, and the rows Review changes shows for each. The Engine is a hand-written stub.
/// </summary>
public sealed class SubjectEditorTests : AsyncBunitContext
{
    private static readonly Guid SubjectId = Guid.Parse("00000000-0000-0000-0000-0000000000a1");

    private readonly DeviceContextService _device = new(EngineApiClientStub.CreateDefault());
    private readonly List<EditSessionRequest> _requests = [];
    private readonly IRenderedComponent<AppDialogHost> _dialogs;
    private EditorSubjectDto _subject = Person();

    public SubjectEditorTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddLogging();
        Services.AddSingleton(_device);
        Services.AddSingleton(EngineApiClientStub.Create(stub =>
        {
            stub.SetHandler(nameof(IEngineApiClient.GetEditorSubjectAsync), _ => Task.FromResult<EditorSubjectDto?>(_subject));
            stub.SetHandler(nameof(IEngineApiClient.GetItemEditorSuggestionsAsync), _ => Task.FromResult<IReadOnlyList<string>>([]));
            stub.SetHandler(nameof(IEngineApiClient.RunEditSessionAsync), args =>
            {
                var request = (EditSessionRequest)args![0]!;
                _requests.Add(request);
                return Task.FromResult(LibraryBatchEditOutcome<EditSessionResult>.Ok(new EditSessionResult
                {
                    PreviewOnly = request.PreviewOnly,
                    Valid = true,
                    DataSaved = !request.PreviewOnly,
                    Changes = request.Changes.Select(change => new EditSessionChangeResult
                    {
                        Id = change.Id,
                        Kind = change.Kind,
                        EntityId = change.EntityId,
                        Status = EditSessionChangeStatuses.Ok,
                    }).ToList(),
                }));
            });
        }));
        Render<AppPopoverHost>();
        _dialogs = Render<AppDialogHost>();
    }

    private sealed record Editor(EditingModeContext Mode, EditSessionState Session, EditSessionFlow Flow);

    private async Task<Editor> StartAsync(EditorSubjectDto subject)
    {
        _subject = subject;
        await _device.InitialiseAsync(new DeviceContextSnapshot("web", "desktop", false, FinePointer: true, Hover: true));
        var api = Services.GetRequiredService<IEngineApiClient>();
        var fields = new ItemFieldEditSession(api, _device);
        var mode = new EditingModeContext(_device, fields) { HasPermission = true };
        mode.Loader = () => fields.LoadSubjectAsync(SubjectId);
        var session = new EditSessionState(new PendingEditList());
        var flow = new EditSessionFlow(
            session,
            mode,
            new EditSessionSaver(api),
            () => [new EditTrailItem(SubjectId, subject.Title, subject.Kind)],
            () => Task.FromResult(false));
        Assert.True(await mode.EnterAsync());
        return new Editor(mode, session, flow);
    }

    private IRenderedComponent<EditingBar> RenderBar(Editor editor) =>
        Render<EditingBar>(parameters => parameters
            .Add(bar => bar.Mode, editor.Mode)
            .Add(bar => bar.Flow, editor.Flow));

    private IRenderedComponent<CascadingValue<EditingModeContext>> RenderPanel(Editor editor) =>
        Render<CascadingValue<EditingModeContext>>(parameters => parameters
            .Add(host => host.Value, editor.Mode)
            .Add(host => host.ChildContent, builder =>
            {
                builder.OpenComponent<EditorPanelHost>(0);
                builder.AddComponentParameter(1, nameof(EditorPanelHost.Mode), editor.Mode);
                builder.AddComponentParameter(2, nameof(EditorPanelHost.Flow), editor.Flow);
                builder.AddComponentParameter(3, nameof(EditorPanelHost.ChildContent), (RenderFragment<EditTool>)(tool => inner =>
                {
                    if (tool == EditTool.Details)
                    {
                        inner.OpenComponent<EditorDetailsPanel>(0);
                        inner.AddComponentParameter(1, nameof(EditorDetailsPanel.Mode), editor.Mode);
                        inner.AddComponentParameter(2, nameof(EditorDetailsPanel.Session), editor.Session);
                        inner.CloseComponent();
                    }
                }));
                builder.CloseComponent();
            }));

    // -- Which tools each kind offers -----------------------------------------------------------

    [Fact]
    public async Task APerson_OffersDetailsAndArtwork_ButNoMatch()
    {
        var editor = await StartAsync(Person());

        var bar = RenderBar(editor);

        Assert.Equal(["Details", "Artwork"], bar.FindAll(".tl-editing-bar__tool").Select(button => button.TextContent.Trim()));
        Assert.False(editor.Mode.Fields.CanMatch);
        Assert.False(editor.Mode.Fields.ProvidersAvailable);
        Assert.Equal(EditorSubjectKinds.Person, editor.Mode.Fields.SubjectKind);

    }

    [Theory]
    [InlineData(EditorSubjectKinds.Series)]
    [InlineData(EditorSubjectKinds.Collection)]
    [InlineData(EditorSubjectKinds.Universe)]
    public async Task ASeriesCollectionOrUniverse_OffersDetailsAndArtwork_ButNoMatch(string kind)
    {
        var editor = await StartAsync(Grouping(kind));

        var bar = RenderBar(editor);

        Assert.Equal(["Details", "Artwork"], bar.FindAll(".tl-editing-bar__tool").Select(button => button.TextContent.Trim()));
        Assert.False(editor.Mode.Fields.CanMatch);
    }

    [Fact]
    public async Task ASubjectWithNoPictureSlots_HasNoArtworkTool()
    {
        var editor = await StartAsync(new EditorSubjectDto
        {
            Id = SubjectId,
            Kind = EditorSubjectKinds.Collection,
            Title = "Road trip",
            ArtworkSlots = [],
            Fields = [new EditorSubjectFieldDto { Key = "title", Label = "Name", Value = "Road trip" }],
        });

        var bar = RenderBar(editor);

        Assert.Equal(["Details"], bar.FindAll(".tl-editing-bar__tool").Select(button => button.TextContent.Trim()));
    }

    [Theory]
    [InlineData(EditorSubjectKinds.Series, "Cover")]
    [InlineData(EditorSubjectKinds.Collection, "Cover")]
    [InlineData(EditorSubjectKinds.Universe, "Cover")]
    [InlineData(EditorSubjectKinds.Person, "Photo")]
    public void ThePictureTabsAreNamedForWhatTheyAre(string kind, string label)
    {
        var slot = kind == EditorSubjectKinds.Person ? "Headshot" : "CoverArt";

        Assert.Equal(label, Assert.Single(ArtworkPickerRules.TabsFor([slot], kind)).Label);
    }

    // -- Details --------------------------------------------------------------------------------

    [Fact]
    public async Task APersonsDetails_ListTheEditableFields_AndTheProviderFactsReadOnly()
    {
        var editor = await StartAsync(Person());
        var cut = RenderPanel(editor);
        await cut.InvokeAsync(() => editor.Flow.SelectToolAsync(EditTool.Details));

        cut.WaitForElement(".tl-editor-details");

        Assert.Single(cut.FindAll(".tl-editor-field[data-field-key='name'] input"));
        Assert.Single(cut.FindAll(".tl-editor-field[data-field-key='biography'] textarea"));
        var fact = cut.Find(".tl-editor-field--locked[data-field-key='occupation']");
        Assert.Contains("Actor", fact.TextContent);
        Assert.Empty(fact.QuerySelectorAll("input, textarea"));
    }

    [Fact]
    public async Task TypingIntoAField_StagesIt_AndSavesNothing()
    {
        var editor = await StartAsync(Person());
        var cut = RenderPanel(editor);
        await cut.InvokeAsync(() => editor.Flow.SelectToolAsync(EditTool.Details));

        cut.WaitForElement(".tl-editor-field[data-field-key='name'] input").Input("Timothy Chalamet");

        cut.WaitForAssertion(() =>
        {
            Assert.True(editor.Session.Pending.TryGetField(SubjectId, "name", out var staged));
            Assert.Equal("Timothy Chalamet", staged);
        });
        Assert.Empty(_requests);
    }

    [Fact]
    public async Task ASeriesOverride_OffersTheProvidersValueBack()
    {
        var editor = await StartAsync(Grouping(EditorSubjectKinds.Series, overridden: true));
        var cut = RenderPanel(editor);
        await cut.InvokeAsync(() => editor.Flow.SelectToolAsync(EditTool.Details));
        var title = cut.WaitForElement(".tl-editor-field[data-field-key='title']");

        Assert.NotNull(title.QuerySelector(".tl-editor-field__dot"));
        title.QuerySelectorAll("button").Single(button => button.TextContent.Contains("Reset to provider value")).Click();

        cut.WaitForAssertion(() =>
        {
            Assert.True(editor.Session.Pending.TryGetField(SubjectId, "title", out var staged));
            Assert.True(string.IsNullOrEmpty(staged));
            Assert.Contains("provider's value", cut.Find(".tl-editor-field[data-field-key='title'] .tl-editor-field__note").TextContent);
        });
    }

    [Fact]
    public async Task AUniverseOverride_OffersTheCanonicalValueBack()
    {
        var editor = await StartAsync(Grouping(EditorSubjectKinds.Universe, overridden: true));
        var cut = RenderPanel(editor);
        await cut.InvokeAsync(() => editor.Flow.SelectToolAsync(EditTool.Details));
        var title = cut.WaitForElement(".tl-editor-field[data-field-key='title']");

        title.QuerySelectorAll("button").Single(button => button.TextContent.Contains("Reset to canonical value")).Click();

        cut.WaitForAssertion(() => Assert.Contains("canonical value", cut.Find(".tl-editor-field[data-field-key='title'] .tl-editor-field__note").TextContent));
    }

    // -- Review changes and saving --------------------------------------------------------------

    [Theory]
    [InlineData(EditorSubjectKinds.Person, "name", "Name", "Timothée Chalamet", "Timothy Chalamet")]
    [InlineData(EditorSubjectKinds.Series, "title", "Name", "Dune Novels", "Dune books")]
    [InlineData(EditorSubjectKinds.Collection, "title", "Name", "Road trip", "Road trip 2")]
    [InlineData(EditorSubjectKinds.Universe, "title", "Name", "Dune", "Arrakis")]
    public async Task ReviewChanges_ShowsOneRowForTheStagedField_AndSaveSendsItForTheSubject(
        string kind, string key, string label, string before, string after)
    {
        var editor = await StartAsync(kind == EditorSubjectKinds.Person ? Person() : Grouping(kind, title: before));
        editor.Session.Pending.SetField(SubjectId, key, after);
        var bar = RenderBar(editor);

        bar.Find("button.tl-editing-bar__save").Click();

        _dialogs.WaitForElement(".review-changes");
        _dialogs.WaitForAssertion(() =>
        {
            var row = Assert.Single(_dialogs.FindAll(".review-changes__row"));
            Assert.Contains(label, row.TextContent);
            Assert.Contains(before, row.TextContent);
            Assert.Contains(after, row.TextContent);
        });
        _dialogs.WaitForAssertion(() => Assert.False(_dialogs.Find(".app-dialog-shell__footer button:last-child").HasAttribute("disabled")));
        _dialogs.Find(".app-dialog-shell__footer button:last-child").Click();

        bar.WaitForAssertion(() => Assert.Contains(_requests, request => !request.PreviewOnly));
        var saved = _requests.Last(request => !request.PreviewOnly).Changes.Single();
        Assert.Equal(SubjectId, saved.EntityId);
        Assert.Equal(EditSessionChangeKinds.Details, saved.Kind);
        var field = Assert.Single(saved.Fields);
        Assert.Equal(key, field.Key);
        Assert.Equal(after, field.Value);
    }

    // -- Subjects -------------------------------------------------------------------------------

    private static EditorSubjectDto Person() => new()
    {
        Id = SubjectId,
        Kind = EditorSubjectKinds.Person,
        Title = "Timothée Chalamet",
        ArtworkEntityKind = "Person",
        ArtworkSlots = ["Headshot"],
        Fields =
        [
            new EditorSubjectFieldDto { Key = "name", Label = "Name", Value = "Timothée Chalamet" },
            new EditorSubjectFieldDto { Key = "biography", Label = "Biography", InputKind = EditorSubjectInputKinds.TextArea, Value = "An actor." },
        ],
        Facts = [new EditorSubjectFactDto { Label = "Occupation", Value = "Actor", SourceLabel = "Wikidata" }],
    };

    private static EditorSubjectDto Grouping(string kind, bool overridden = false, string title = "Dune") => new()
    {
        Id = SubjectId,
        Kind = kind,
        Title = title,
        ArtworkEntityKind = "Collection",
        ArtworkSlots = ["CoverArt", "Background"],
        Fields =
        [
            new EditorSubjectFieldDto { Key = "title", Label = "Name", Value = title, CanonicalValue = "Dune", IsOverridden = overridden, SourceLabel = "Provider" },
            new EditorSubjectFieldDto { Key = "description", Label = "Description", InputKind = EditorSubjectInputKinds.TextArea, Value = "A desert world." },
        ],
    };
}
