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
/// Editing Mode on the details page: the Edit / Done toggle, the per-field pencils, the dirty guard and the buttons that open
/// the drawer on its tabs. Everything renders only for a person who may edit on a desktop with a mouse.
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

    // -- The toggle ----------------------------------------------------------------------------

    [Fact]
    public async Task ToggleShowsEditThenDoneForAnEditorOnADesktopWithAMouse()
    {
        await UseDesktopAsync();
        var mode = await StartedAsync(turnOn: false);
        var cut = Render<EditingModeBar>(parameters => parameters.Add(component => component.Mode, mode));

        var button = cut.Find("button.tl-editing-toggle");
        Assert.Equal("Edit", button.TextContent.Trim());
        Assert.Equal("false", button.GetAttribute("aria-pressed"));

        button.Click();
        cut.WaitForAssertion(() => Assert.Equal("Done", cut.Find("button.tl-editing-toggle").TextContent.Trim()));
        Assert.Equal("true", cut.Find("button.tl-editing-toggle").GetAttribute("aria-pressed"));
        Assert.True(mode.IsOn);

        cut.Find("button.tl-editing-toggle").Click();
        cut.WaitForAssertion(() => Assert.Equal("Edit", cut.Find("button.tl-editing-toggle").TextContent.Trim()));
        Assert.False(mode.IsOn);
    }

    [Theory]
    [InlineData("phone")]
    [InlineData("tablet")]
    [InlineData("tv")]
    [InlineData("desktop-touch")]
    [InlineData("desktop-no-pointer")]
    public async Task ToggleRendersNothingOnADeviceThatCannotEdit(string device)
    {
        var snapshot = device switch
        {
            "phone" => new DeviceContextSnapshot("mobile", "phone", true),
            "tablet" => new DeviceContextSnapshot("web", "tablet", true),
            "tv" => new DeviceContextSnapshot("television", "tv", false, FinePointer: true, Hover: true),
            "desktop-touch" => new DeviceContextSnapshot("web", "desktop", true),
            _ => new DeviceContextSnapshot("web", "desktop", false),
        };
        await _device.InitialiseAsync(snapshot);
        var mode = await StartedAsync(permission: true, turnOn: false);

        var cut = Render<EditingModeBar>(parameters => parameters.Add(component => component.Mode, mode));

        Assert.Empty(cut.FindAll("button"));
        Assert.Equal(string.Empty, cut.Markup.Trim());
        Assert.False(await mode.EnterAsync());
    }

    [Fact]
    public async Task KidsAndOtherNonEditProfilesSeeNoControlEvenOnADesktop()
    {
        await UseDesktopAsync();
        var mode = await StartedAsync(permission: false, turnOn: false);

        var cut = Render<EditingModeBar>(parameters => parameters.Add(component => component.Mode, mode));

        Assert.Empty(cut.FindAll("button"));
        Assert.Equal(string.Empty, cut.Markup.Trim());
        Assert.False(await mode.EnterAsync());
        Assert.False(mode.IsOn);
    }

    [Fact]
    public async Task ABarThatHasUnavailableEditShowsItDisabledWithItsReason()
    {
        await UseDesktopAsync();
        var mode = await StartedAsync(turnOn: false);
        mode.UnavailableReason = "Updating details. Editing is available when ingestion finishes.";

        var cut = Render<EditingModeBar>(parameters => parameters.Add(component => component.Mode, mode));

        var button = cut.Find("button.tl-editing-toggle");
        Assert.True(button.HasAttribute("disabled"));
        Assert.Equal(mode.UnavailableReason, button.GetAttribute("title"));
    }

    [Fact]
    public async Task OnlyEditShowsWhileOffAndTheArtworkMatchAndMoreButtonsFollowWhenOn()
    {
        await UseDesktopAsync();
        var mode = await StartedAsync(turnOn: false);
        var cut = Render<EditingModeBar>(parameters => parameters
            .Add(component => component.Mode, mode)
            .Add(component => component.MoreActions, [new DetailAction { Key = "status-history", Label = "Viewing history", Icon = "history" }]));

        Assert.Single(cut.FindAll("button.tl-editing-toggle"));
        Assert.Empty(cut.FindAll(".tl-editing-bar__action"));

        await cut.InvokeAsync(() => mode.EnterAsync());

        cut.WaitForAssertion(() =>
        {
            Assert.Equal(["Manage Artwork", "Match and Identity"],
                cut.FindAll(".tl-editing-bar__action").Select(button => button.TextContent.Trim()));
            Assert.NotEmpty(cut.FindAll("button[aria-label='More actions']"));
        });
    }

    [Fact]
    public async Task AnItemWhoseFieldsCannotBeLoadedStillEntersTheModeAndSaysSoInline()
    {
        await UseDesktopAsync();
        var api = EngineApiClientStub.Create(stub =>
            stub.SetHandler(nameof(IEngineApiClient.GetMediaEditorContextAsync), _ => Task.FromResult<MediaEditorContextDto?>(null)));
        var session = new ItemFieldEditSession(api, _device);
        var mode = new EditingModeContext(_device, session) { HasPermission = true };
        mode.Loader = () => session.LoadAsync(MovieId, "Movies", "movie", null);
        var cut = Render<EditingModeBar>(parameters => parameters.Add(component => component.Mode, mode));

        cut.Find("button.tl-editing-toggle").Click();

        cut.WaitForAssertion(() => Assert.Contains("could not be loaded", cut.Find(".tl-editing-decision[role='alert']").TextContent));
        Assert.True(mode.IsOn);
        var pencils = RenderWith(mode, Pencils());
        Assert.Empty(pencils.FindAll(".tl-editing-pencil"));
    }

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
        Assert.False(mode.OpenDrawer(EditingDrawerTab.Details));
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

    // -- The drawer buttons --------------------------------------------------------------------

    [Fact]
    public async Task EachTopButtonOpensTheDrawerOnItsOwnTab()
    {
        await UseDesktopAsync();
        var mode = await StartedAsync();
        var cut = Render<EditingModeBar>(parameters => parameters.Add(component => component.Mode, mode));

        cut.FindAll(".tl-editing-bar__action").Single(button => button.TextContent.Contains("Manage Artwork")).Click();
        Assert.Equal(EditingDrawerTab.Artwork, mode.DrawerTab);
        Assert.Null(mode.DrawerArtworkSlot);

        mode.CloseDrawer();
        cut.FindAll(".tl-editing-bar__action").Single(button => button.TextContent.Contains("Match and Identity")).Click();
        Assert.Equal(EditingDrawerTab.Match, mode.DrawerTab);
    }

    [Fact]
    public async Task EachHeroArtButtonOpensTheDrawerOnArtworkForThatSlot()
    {
        await UseDesktopAsync();
        var mode = await StartedAsync();
        var cut = RenderWith(mode, builder =>
        {
            builder.OpenComponent<EditingHeroArtworkButtons>(0);
            builder.CloseComponent();
        });

        Assert.Equal(["Edit Background", "Edit Logo", "Edit Cover"],
            cut.FindAll(".tl-editing-art__button").Select(button => button.TextContent.Trim()));

        cut.FindAll(".tl-editing-art__button").Single(button => button.TextContent.Contains("Edit Logo")).Click();
        Assert.Equal(EditingDrawerTab.Artwork, mode.DrawerTab);
        Assert.Equal("Logo", mode.DrawerArtworkSlot);

        cut.FindAll(".tl-editing-art__button").Single(button => button.TextContent.Contains("Edit Cover")).Click();
        Assert.Equal("CoverArt", mode.DrawerArtworkSlot);
        Assert.Equal(2, mode.DrawerRequestVersion);
    }

    [Fact]
    public async Task HeroArtButtonsAreHiddenWhileTheModeIsOffAndTheDrawerRefusesWhileAFieldIsOpen()
    {
        await UseDesktopAsync();
        var mode = await StartedAsync(turnOn: false);
        var cut = RenderWith(mode, builder =>
        {
            builder.OpenComponent<EditingHeroArtworkButtons>(0);
            builder.CloseComponent();
        });
        Assert.Empty(cut.FindAll("button"));
        Assert.False(mode.OpenDrawer(EditingDrawerTab.Artwork, "Logo"));

        await mode.EnterAsync();
        Assert.True(mode.Fields.Begin(mode.Fields.Find("title")!, "hero"));
        Assert.False(mode.OpenDrawer(EditingDrawerTab.Details));
    }

    // -- The dirty guard -----------------------------------------------------------------------

    [Fact]
    public async Task DoneAsksFirstWhenAFieldHasUnsavedInputAndKeepEditingChangesNothing()
    {
        await UseDesktopAsync();
        var mode = await StartedAsync();
        var cut = Render<EditingModeBar>(parameters => parameters.Add(component => component.Mode, mode));
        Assert.True(mode.Fields.Begin(mode.Fields.Find("title")!, "hero"));
        mode.Fields.UpdateDraft("Typed but not saved");

        cut.Find("button.tl-editing-toggle").Click();

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(".tl-editing-decision[role='alert']")));
        Assert.True(mode.IsOn);
        Assert.Equal("Typed but not saved", mode.Fields.Draft);
        Assert.Contains("Keep editing", cut.Find(".tl-editing-decision").TextContent);

        cut.FindAll(".tl-editing-decision button").Single(button => button.TextContent.Contains("Keep editing")).Click();
        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll(".tl-editing-decision")));
        Assert.True(mode.IsOn);
        Assert.Equal("Typed but not saved", mode.Fields.Draft);
    }

    [Fact]
    public async Task DiscardAndFinishEndsEditingModeAndDropsTheTypedValue()
    {
        await UseDesktopAsync();
        var mode = await StartedAsync();
        var cut = Render<EditingModeBar>(parameters => parameters.Add(component => component.Mode, mode));
        Assert.True(mode.Fields.Begin(mode.Fields.Find("title")!, "hero"));
        mode.Fields.UpdateDraft("Typed but not saved");
        cut.Find("button.tl-editing-toggle").Click();

        cut.FindAll(".tl-editing-decision button").Single(button => button.TextContent.Contains("Discard and finish")).Click();

        cut.WaitForAssertion(() => Assert.False(mode.IsOn));
        Assert.False(mode.Fields.HasOpenDraft);
        Assert.Empty(_saves);
    }

    [Fact]
    public async Task SaveAndFinishSavesTheFieldThenEndsEditingMode()
    {
        await UseDesktopAsync();
        var mode = await StartedAsync();
        var cut = Render<EditingModeBar>(parameters => parameters.Add(component => component.Mode, mode));
        Assert.True(mode.Fields.Begin(mode.Fields.Find("title")!, "hero"));
        mode.Fields.UpdateDraft("Saved on the way out");
        cut.Find("button.tl-editing-toggle").Click();

        cut.FindAll(".tl-editing-decision button").Single(button => button.TextContent.Contains("Save and finish")).Click();

        cut.WaitForAssertion(() => Assert.False(mode.IsOn));
        Assert.Equal("Saved on the way out", Assert.Single(_saves).Fields["title"]);
    }

    [Fact]
    public async Task UnsavedDrawerInputAlsoBlocksDone()
    {
        await UseDesktopAsync();
        var mode = await StartedAsync();
        var cut = Render<EditingModeBar>(parameters => parameters.Add(component => component.Mode, mode));
        mode.SetDrawerDirty(true);

        cut.Find("button.tl-editing-toggle").Click();

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(".tl-editing-decision")));
        Assert.True(mode.IsOn);
        Assert.Contains("editor drawer", cut.Find(".tl-editing-decision").TextContent);
        Assert.DoesNotContain(cut.FindAll(".tl-editing-decision button"), button => button.TextContent.Contains("Save and finish"));
    }

    [Fact]
    public async Task LeavingThePageWithUnsavedInputIsInterceptedUntilTheUserChooses()
    {
        await UseDesktopAsync();
        var mode = await StartedAsync();
        var cut = Render<EditingModeBar>(parameters => parameters.Add(component => component.Mode, mode));
        var navigation = Services.GetRequiredService<NavigationManager>();
        var before = navigation.Uri;
        Assert.True(mode.Fields.Begin(mode.Fields.Find("title")!, "hero"));
        mode.Fields.UpdateDraft("Typed but not saved");

        navigation.NavigateTo("/watch/movies");

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(".tl-editing-decision")));
        Assert.Equal(before, navigation.Uri);
        Assert.Contains("Save changes before leaving?", cut.Find(".tl-editing-decision").TextContent);

        cut.FindAll(".tl-editing-decision button").Single(button => button.TextContent.Contains("Discard and leave")).Click();

        cut.WaitForAssertion(() => Assert.EndsWith("/watch/movies", navigation.Uri));
        Assert.False(mode.IsOn);
    }

    [Fact]
    public async Task LeavingWithNothingTypedIsNeverIntercepted()
    {
        await UseDesktopAsync();
        var mode = await StartedAsync();
        Render<EditingModeBar>(parameters => parameters.Add(component => component.Mode, mode));
        var navigation = Services.GetRequiredService<NavigationManager>();
        Assert.True(mode.Fields.Begin(mode.Fields.Find("title")!, "hero"));

        navigation.NavigateTo("/watch/movies");

        Assert.EndsWith("/watch/movies", navigation.Uri);
    }

    // -- Fixture data --------------------------------------------------------------------------

    private static MediaEditorContextDto MovieContext() => new()
    {
        LaunchEntityId = MovieId,
        MediaType = "Movies",
        InitialScope = "movie",
        AvailableTabs = ["details", "artwork", "links", "history"],
        Scopes =
        [
            new MediaEditorScopeDto
            {
                ScopeId = "movie",
                Label = "Movie",
                FieldEntityId = MovieId,
                DisplayTitle = "Dune: Part One",
                CanEditFields = true,
                CanEditArtwork = true,
                ArtworkSlots = ["CoverArt", "Background", "Logo"],
                FieldSnapshot = new MediaEditorScopeFieldSnapshotDto
                {
                    CanonicalFields =
                    [
                        Field("title", "Dune: Part One"),
                        Field("description", "A noble family becomes embroiled in a war for control of the galaxy's most valuable asset."),
                        Field("rating", "8.1"),
                        Field("release_date", "2021-10-22"),
                        Field("runtime", "155"),
                    ],
                    CanonicalArrays =
                    [
                        Array("genre", "Science Fiction", "Adventure"),
                        Array("director", "Denis Villeneuve"),
                    ],
                    DisplayOverrides = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["custom_tags"] = "epic" },
                    AllowedOverrideKeys = ["title", "description", "tagline", "genre", "custom_tags", "rating", "release_date", "runtime", "year"],
                },
            },
        ],
    };

    private static MediaEditorScopedFieldValueDto Field(string key, string value) =>
        new() { Key = key, Value = value, ProviderName = "tmdb" };

    private static MediaEditorScopedFieldArrayDto Array(string key, params string[] values) =>
        new()
        {
            Key = key,
            Entries = values.Select((value, index) => new MediaEditorScopedArrayEntryDto { Ordinal = index, Value = value }).ToList(),
        };
}
