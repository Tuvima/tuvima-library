using Bunit;
using MediaEngine.Contracts.Details;
using MediaEngine.Contracts.Metadata;
using MediaEngine.Web.Components.Details;
using MediaEngine.Web.Components.Shared;
using MediaEngine.Web.Services.Editing;
using MediaEngine.Web.Services.Integration;
using MediaEngine.Web.Services.Theming;
using MediaEngine.Web.Tests.Support;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;

namespace MediaEngine.Web.Tests;

/// <summary>
/// Editing Mode on the details page: the per-field pencils, how a field saves, cancels and fails, and the handlers that refuse
/// once the device stops qualifying. Everything renders only for a person who may edit on a desktop with a mouse. The banner,
/// the sections and leaving (Done, Esc, Back) have their own tests.
/// </summary>
public sealed class EditingModeTests : AsyncBunitContext
{
    private static readonly Guid MovieId = Guid.Parse("11111111-aaaa-bbbb-cccc-000000000001");

    private readonly DeviceContextService _device = new(EngineApiClientStub.CreateDefault());
    private readonly List<(Guid EntityId, Dictionary<string, string> Fields)> _saves = [];
    private bool _saveResult = true;
    private string? _lastError;

    public EditingModeTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddSingleton(_device);
        Render<AppPopoverHost>();
    }

    private Task UseDesktopAsync() =>
        _device.InitialiseAsync(new DeviceContextSnapshot("web", "desktop", false, FinePointer: true, Hover: true));

    private IEngineApiClient Api() => EngineApiClientStub.Create(stub =>
    {
        stub.SetHandler(nameof(IEngineApiClient.GetMediaEditorContextAsync), _ => Task.FromResult<MediaEditorContextDto?>(MovieContext()));
        stub.SetHandler(nameof(IEngineApiClient.GetItemEditorSuggestionsAsync), _ => Task.FromResult<IReadOnlyList<string>>(["Drama", "Documentary"]));
        stub.SetHandler(nameof(IEngineApiClient.SaveItemDisplayOverridesAsync), args =>
        {
            _saves.Add(((Guid)args![0]!, (Dictionary<string, string>)args[1]!));
            return Task.FromResult(_saveResult);
        });
        stub.SetHandler("get_LastError", _ => _lastError);
    });

    private async Task<EditingModeContext> StartedAsync(bool permission = true, bool turnOn = true)
    {
        var api = Api();
        var session = new ItemFieldEditSession(api, _device);
        var mode = new EditingModeContext(_device, session) { HasPermission = permission };
        mode.Loader = () => session.LoadAsync(MovieId, "Movies", "movie", null);
        if (turnOn)
        {
            Assert.True(await mode.EnterAsync());
        }

        return mode;
    }

    private IRenderedComponent<CascadingValue<EditingModeContext>> RenderWith(EditingModeContext mode, RenderFragment content) =>
        Render<CascadingValue<EditingModeContext>>(parameters => parameters
            .Add(component => component.Value, mode)
            .Add(component => component.ChildContent, content));

    private static RenderFragment Pencils() => builder =>
    {
        void Pencil(string[] keys, bool heading = false)
        {
            builder.OpenComponent<EditableFieldPencil>(0);
            if (heading)
            {
                builder.AddComponentParameter(1, nameof(EditableFieldPencil.Heading), true);
            }
            else
            {
                builder.AddComponentParameter(2, nameof(EditableFieldPencil.Keys), keys);
            }

            builder.CloseComponent();
        }

        Pencil([], heading: true);
        Pencil(["description", "episode_description", "issue_description"]);
        Pencil(["genre"]);
        Pencil(["rating"]);
        Pencil(["release_date", "year"]);
        Pencil(["custom_tags"]);
        Pencil(["director", "author"]);
    };

    // -- The pencils ---------------------------------------------------------------------------

    [Fact]
    public async Task NothingRendersForAFieldWhileEditingModeIsOff()
    {
        await UseDesktopAsync();
        var mode = await StartedAsync(turnOn: false);

        var cut = RenderWith(mode, Pencils());

        Assert.Equal(string.Empty, cut.Markup.Trim());
    }

    [Fact]
    public async Task EachEditableFieldGetsOnePencilAndProviderFactsGetALockAndAReason()
    {
        await UseDesktopAsync();
        var mode = await StartedAsync();

        var cut = RenderWith(mode, Pencils());

        var pencils = cut.FindAll(".tl-editing-pencil button").Select(button => button.GetAttribute("aria-label")).ToList();
        Assert.Equal(
            ["Edit title", "Edit synopsis", "Edit genres", "Edit rating", "Edit release date", "Edit tags"],
            pencils);

        var lock1 = Assert.Single(cut.FindAll(".tl-editing-lock"));
        Assert.Contains("Set by", lock1.GetAttribute("aria-label"));
        Assert.Contains("Change it in Match and Identity", lock1.GetAttribute("aria-label"));
        Assert.Equal("img", lock1.GetAttribute("role"));
        Assert.Equal("0", lock1.GetAttribute("tabindex"));
    }

    [Fact]
    public async Task APencilWhoseFieldShowsOtherTextThanThePageIsNotOffered()
    {
        await UseDesktopAsync();
        var mode = await StartedAsync();

        var cut = RenderWith(mode, builder =>
        {
            builder.OpenComponent<EditableFieldPencil>(0);
            builder.AddComponentParameter(1, nameof(EditableFieldPencil.Heading), true);
            builder.AddComponentParameter(2, nameof(EditableFieldPencil.ExpectedText), "A different show title");
            builder.CloseComponent();
        });

        Assert.Empty(cut.FindAll("button"));
    }

    [Fact]
    public async Task EnterSavesOneFieldThroughDisplayOverridesAndShowsSavedInline()
    {
        await UseDesktopAsync();
        var mode = await StartedAsync();
        var cut = RenderWith(mode, Pencils());

        cut.Find("button[aria-label='Edit title']").Click();
        var input = cut.Find("input[aria-label='Edit Title']");
        Assert.Equal("Dune: Part One", input.GetAttribute("value"));
        input.Input("Dune");
        input.KeyDown("Enter");

        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll("input[aria-label='Edit Title']")));
        var save = Assert.Single(_saves);
        Assert.Equal(MovieId, save.EntityId);
        Assert.Equal(new Dictionary<string, string> { ["title"] = "Dune" }, save.Fields);
        Assert.Contains("Saved", cut.Find(".tl-editing-pencil__saved").TextContent);
        Assert.Equal("status", cut.Find(".tl-editing-pencil__saved").GetAttribute("role"));
        Assert.False(mode.Fields.HasOpenDraft);
    }

    [Fact]
    public async Task TheCheckButtonSavesAndOnlyOneFieldIsOpenAtATime()
    {
        await UseDesktopAsync();
        var mode = await StartedAsync();
        var cut = RenderWith(mode, Pencils());

        cut.Find("button[aria-label='Edit rating']").Click();
        Assert.All(cut.FindAll(".tl-editing-pencil button"), button => Assert.True(button.HasAttribute("disabled")));
        cut.Find("input[aria-label='Edit Rating']").Input("9");
        cut.Find("button[aria-label='Save Rating']").Click();

        cut.WaitForAssertion(() => Assert.Single(_saves));
        Assert.Equal("9", _saves[0].Fields["rating"]);
    }

    [Fact]
    public async Task AFailedSaveKeepsTheTypedValueAndShowsTheErrorInline()
    {
        await UseDesktopAsync();
        _saveResult = false;
        _lastError = "HTTP 500: could not write";
        var mode = await StartedAsync();
        var cut = RenderWith(mode, Pencils());

        cut.Find("button[aria-label='Edit title']").Click();
        cut.Find("input[aria-label='Edit Title']").Input("Typed and kept");
        cut.Find("input[aria-label='Edit Title']").KeyDown("Enter");

        cut.WaitForAssertion(() => Assert.Contains("could not write", cut.Find(".tl-editing-field__error").TextContent));
        Assert.Equal("alert", cut.Find(".tl-editing-field__error").GetAttribute("role"));
        Assert.Equal("Typed and kept", cut.Find("input[aria-label='Edit Title']").GetAttribute("value"));
        Assert.True(mode.Fields.HasOpenDraft);
        Assert.Empty(cut.FindAll(".tl-editing-pencil__saved"));
    }

    [Fact]
    public async Task EscapeCancelsTheFieldWithoutSavingAndGivesTheFocusBackToThePencil()
    {
        await UseDesktopAsync();
        var mode = await StartedAsync();
        var cut = RenderWith(mode, Pencils());

        cut.Find("button[aria-label='Edit title']").Click();
        cut.Find("input[aria-label='Edit Title']").Input("Not saved");
        cut.Find("input[aria-label='Edit Title']").KeyDown("Escape");

        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll("input[aria-label='Edit Title']")));
        Assert.Empty(_saves);
        Assert.False(mode.Fields.HasOpenDraft);
        Assert.NotEmpty(cut.FindAll("button[aria-label='Edit title']"));
    }

    [Fact]
    public async Task GenresAndTagsUseTheTagEditorAndTagsAreNormalisedOnSave()
    {
        await UseDesktopAsync();
        var mode = await StartedAsync();
        var cut = RenderWith(mode, Pencils());

        cut.Find("button[aria-label='Edit tags']").Click();
        Assert.NotEmpty(cut.FindAll(".sme-tag-editor"));
        mode.Fields.UpdateDraft(" quiet, thoughtful ; quiet ");
        cut.Find("button[aria-label='Save Tags']").Click();

        cut.WaitForAssertion(() => Assert.Single(_saves));
        Assert.Equal(new Dictionary<string, string> { ["custom_tags"] = "quiet; thoughtful" }, _saves[0].Fields);
    }

    [Fact]
    public async Task EscapeInsideTheTagEditorCancelsTheField()
    {
        await UseDesktopAsync();
        var mode = await StartedAsync();
        var cut = RenderWith(mode, Pencils());

        cut.Find("button[aria-label='Edit genres']").Click();
        cut.Find(".tl-editing-field--tags").KeyDown(new KeyboardEventArgs { Key = "Escape" });

        cut.WaitForAssertion(() => Assert.False(mode.Fields.HasOpenDraft));
        Assert.Empty(_saves);
    }

    // -- Every handler refuses when the device cannot edit -------------------------------------

    [Fact]
    public async Task EveryHandlerRefusesOnceTheDeviceStopsQualifying()
    {
        await UseDesktopAsync();
        var mode = await StartedAsync();
        var field = mode.Fields.Find("title")!;
        Assert.True(mode.Fields.Begin(field, "hero"));
        mode.Fields.UpdateDraft("Typed on a desktop");

        // The window is dragged to tablet width: the mode ends by itself and nothing can be saved.
        await _device.SwitchDeviceAsync(new DeviceContextSnapshot("web", "tablet", true));

        Assert.False(mode.IsOn);
        Assert.False(mode.Fields.HasOpenDraft);
        Assert.False(await mode.Fields.SaveAsync());
        Assert.False(mode.Fields.Begin(field, "hero"));
        Assert.False(mode.OpenSection(EditingSection.Artwork));
        Assert.Empty(_saves);
    }

    [Fact]
    public async Task ASaveIsRefusedAtTheHandlerEvenIfTheModeWasLeftOpen()
    {
        await UseDesktopAsync();
        var mode = await StartedAsync();
        var field = mode.Fields.Find("title")!;
        Assert.True(mode.Fields.Begin(field, "hero"));
        mode.Fields.UpdateDraft("Typed");

        // Only the pointer changes and the context is not told: the save itself still checks the rule.
        var tabletOnly = new DeviceContextService(EngineApiClientStub.CreateDefault());
        await tabletOnly.InitialiseAsync(new DeviceContextSnapshot("web", "tablet", true));
        var guarded = new ItemFieldEditSession(Api(), tabletOnly);
        await guarded.LoadAsync(MovieId, "Movies", "movie", null);

        Assert.False(guarded.Begin(guarded.Find("title")!, "hero"));
        Assert.False(await guarded.SaveAsync());
        Assert.Empty(_saves);
    }

    // -- Fixture data --------------------------------------------------------------------------

    private static MediaEditorContextDto MovieContext() => EditingModeTestData.MovieContext();
}
