using Bunit;
using MediaEngine.Contracts.Artwork;
using MediaEngine.Contracts.Editing;
using MediaEngine.Contracts.Metadata;
using MediaEngine.Contracts.Playback;
using MediaEngine.Web.Components.Editor;
using MediaEngine.Web.Components.Shared;
using MediaEngine.Web.Models.ViewDTOs;
using MediaEngine.Web.Services.Editing;
using MediaEngine.Web.Services.Integration;
using MediaEngine.Web.Services.Theming;
using MediaEngine.Web.Tests.Support;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace MediaEngine.Web.Tests;

/// <summary>
/// The editor's Details, Artwork, History and Bookmarks tabs on a phone (universal editor v6 section 4a, D21): one column with the lock at the right
/// of each field, the reason a field is locked a tap away, Quick Facts a closed section under the form, two pictures per row with photos and camera
/// as sources and the picker a full-screen step, a History list whose entries open on a tap, and 44 px targets throughout. A field is unlocked,
/// changed, reviewed and saved by touch alone. The desktop arrangement is unchanged.
/// </summary>
public sealed class EditorPhoneTabsTests : AsyncBunitContext
{
    private static readonly Guid MovieId = EditingModeTestData.MovieId;
    private static readonly Guid FirstPicture = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000001");

    private readonly DeviceContextService _device = new(EngineApiClientStub.CreateDefault());
    private readonly List<EditSessionRequest> _sent = [];
    private bool _historyFails;
    private bool _artworkFails;

    public EditorPhoneTabsTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddLogging();
        Services.AddSingleton(_device);
        Services.AddSingleton(EngineApiClientStub.Create(stub =>
        {
            stub.SetHandler(nameof(IEngineApiClient.GetMediaEditorContextAsync), _ => Task.FromResult<MediaEditorContextDto?>(EditingModeTestData.MovieContext()));
            stub.SetHandler(nameof(IEngineApiClient.GetItemEditorSuggestionsAsync), _ => Task.FromResult<IReadOnlyList<string>>([]));
            stub.SetHandler(nameof(IEngineApiClient.GetEditorSubjectAsync), _ => Task.FromResult<EditorSubjectDto?>(Series()));
            stub.SetHandler(nameof(IEngineApiClient.RunEditSessionAsync), args =>
            {
                var request = (EditSessionRequest)args![0]!;
                _sent.Add(request);
                return Task.FromResult(LibraryBatchEditOutcome<EditSessionResult>.Ok(Ok(request)));
            });
            stub.SetHandler(nameof(IEngineApiClient.GetEditorBookmarksAsync), _ => Task.FromResult<EditorBookmarksDto?>(Bookmarks()));
            stub.SetHandler(nameof(IEngineApiClient.GetEntityArtworkAsync), _ => Task.FromResult(_artworkFails ? null : Workspace()));
            stub.SetHandler(nameof(IEngineApiClient.GetItemHistoryWithStatusAsync), _ => Task.FromResult((new List<LibraryItemHistoryDto>
            {
                new() { Id = "1", EntityId = MovieId, OccurredAt = DateTimeOffset.Now.AddHours(-1), EventType = "MetadataEdited", Label = "Title edited", Detail = "Dune to Dune: Part One", Category = "metadata", ActorLabel = "Shy" },
            }, _historyFails ? "boom" : null)));
        }));
        Services.AddSingleton(new DashboardSessionAccessor());
        Services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        Render<AppPopoverHost>();
    }

    private static EditorSubjectDto Series() => new()
    {
        Id = MovieId,
        Kind = EditorSubjectKinds.Series,
        Title = "Dune",
        ArtworkEntityKind = "Collection",
        ArtworkSlots = [],
        Fields =
        [
            new EditorSubjectFieldDto { Key = "title", Label = "Name", Value = "Dune", CanonicalValue = "Dune", SourceLabel = "Provider" },
        ],
    };

    private static EditorBookmarkRowDto BookmarkRow(string name, double seconds, bool inEveryone) => new()
    {
        Id = Guid.NewGuid(),
        AssetId = MovieId,
        PositionSeconds = seconds,
        Name = name,
        OwnerName = "Mira",
        InEveryone = inEveryone,
        FrameUrl = $"/api/v1/player/bookmarks/{Guid.NewGuid():D}/frame?v=1",
        CreatedAt = new DateTimeOffset(2026, 11, 1, 12, 0, 0, TimeSpan.Zero),
    };

    private static EditorBookmarksDto Bookmarks() => new()
    {
        WorkId = MovieId,
        State = EditorBookmarkStates.Pooled,
        AssetId = MovieId,
        HasTimes = true,
        CanExchange = true,
        HasFrames = true,
        RunningTimeSeconds = 9000,
        Everyone = [BookmarkRow("Opening", 724, true)],
        FromProfiles = [BookmarkRow("Opening", 724, true), BookmarkRow("The twist", 3100, false)],
    };

    private static EditSessionResult Ok(EditSessionRequest request) => new()
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
    };

    private static ArtworkEntityWorkspaceDto Workspace() =>
        new(MovieId, "Work",
        [
            new ArtworkEntityVariantDto(Guid.NewGuid(), FirstPicture, "Primary", null, "CoverArt", true, false,
                $"/art/{FirstPicture:N}/large", $"/art/{FirstPicture:N}/small", 1000, 1500, "Portrait", "tmdb", null)
            {
                FileName = "poster.jpg",
                FileSizeBytes = 2_400_000,
                ContentType = "image/jpeg",
            },
        ]);

    private sealed record Editor(EditingModeContext Mode, EditSessionState Session, EditSessionFlow Flow, ItemFieldEditSession Fields);

    private async Task<Editor> StartAsync(bool subject = false)
    {
        await _device.InitialiseAsync(new DeviceContextSnapshot("web", "desktop", false, FinePointer: true, Hover: true));
        var api = Services.GetRequiredService<IEngineApiClient>();
        var fields = new ItemFieldEditSession(api);
        var mode = new EditingModeContext(_device, fields) { HasPermission = true };
        mode.Loader = subject ? () => fields.LoadSubjectAsync(MovieId) : () => fields.LoadAsync(MovieId, "Movies", "movie", null);
        var session = new EditSessionState(new PendingEditList());
        var flow = new EditSessionFlow(session, mode, new EditSessionSaver(api), () => [new EditTrailItem(MovieId, "Dune: Part One", subject ? "Series" : "Movie")], () => Task.FromResult(false));
        Assert.True(await mode.EnterAsync());
        await flow.SelectToolAsync(EditTool.Details);
        return new Editor(mode, session, flow, fields);
    }

    private IRenderedComponent<CascadingValue<EditingModeContext>> RenderDetails(Editor editor, bool phone) =>
        Render<CascadingValue<EditingModeContext>>(parameters => parameters
            .Add(host => host.Value, editor.Mode)
            .Add(host => host.ChildContent, builder =>
            {
                builder.OpenComponent<EditorPanelHost>(0);
                builder.AddComponentParameter(1, nameof(EditorPanelHost.Mode), editor.Mode);
                builder.AddComponentParameter(2, nameof(EditorPanelHost.Flow), editor.Flow);
                builder.AddComponentParameter(3, nameof(EditorPanelHost.Phone), phone);
                builder.AddComponentParameter(4, nameof(EditorPanelHost.ShowHistory), true);
                builder.AddComponentParameter(5, nameof(EditorPanelHost.ChildContent), (RenderFragment<EditTool>)(tool => inner =>
                {
                    inner.OpenComponent<EditorDetailsPanel>(0);
                    inner.AddComponentParameter(1, nameof(EditorDetailsPanel.Mode), editor.Mode);
                    inner.AddComponentParameter(2, nameof(EditorDetailsPanel.Session), editor.Session);
                    inner.AddComponentParameter(3, nameof(EditorDetailsPanel.Flow), editor.Flow);
                    inner.AddComponentParameter(4, nameof(EditorDetailsPanel.Phone), phone);
                    inner.CloseComponent();
                }));
                builder.CloseComponent();
            }));

    // -- Details --------------------------------------------------------------------------------------

    [Fact]
    public async Task OnAPhone_AFieldIsUnlockedChangedReviewedAndSavedByTouchAlone()
    {
        var editor = await StartAsync();
        var cut = RenderDetails(editor, phone: true);
        var field = cut.WaitForElement(".tl-editor-field[data-field-key='title']");
        Assert.Equal("true", field.GetAttribute("data-locked"));

        // Tap the lock, change the value, tap Save changes, read the Review step, tap Save.
        field.QuerySelector("button.tl-editor-field__lock-toggle")!.Click();
        cut.WaitForAssertion(() => Assert.Equal("false", cut.Find(".tl-editor-field[data-field-key='title']").GetAttribute("data-locked")));
        cut.Find("input[aria-label='Title']").Input("Dune 2");
        cut.WaitForAssertion(() => Assert.True(editor.Session.HasPendingChanges));
        Assert.DoesNotContain(_sent, request => !request.PreviewOnly);

        cut.Find("button.tl-editor-window__save").Click();
        cut.WaitForElement(".review-changes--phone");
        cut.WaitForAssertion(() => Assert.False(cut.Find(".review-changes__footer button:last-child").HasAttribute("disabled")));
        cut.Find(".review-changes__footer button:last-child").Click();

        cut.WaitForAssertion(() =>
        {
            var saved = Assert.Single(_sent, request => !request.PreviewOnly);
            var change = Assert.Single(saved.Changes);
            Assert.Equal("title", Assert.Single(change.Fields).Key);
            Assert.Equal("Dune 2", change.Fields[0].Value);
        });
    }

    [Fact]
    public async Task OnAPhone_TheDetailsAreOneColumnWithTheLockAtTheRightOfEachField()
    {
        var editor = await StartAsync();

        var cut = RenderDetails(editor, phone: true);

        var layout = cut.WaitForElement(".tl-editor-details-layout");
        Assert.Contains("tl-editor-details-layout--phone", layout.ClassList);
        Assert.Contains("tl-editor-touch", layout.ClassList);
        Assert.Equal("true", layout.GetAttribute("data-phone"));
        Assert.All(cut.FindAll(".tl-editor-details .app-form-row"), row => Assert.Contains("app-form-row--stack", row.ClassList));
        Assert.NotEmpty(cut.FindAll(".tl-editor-field button.tl-editor-field__lock-toggle"));
    }

    [Fact]
    public async Task OnADesktop_TheDetailsAreUnchanged()
    {
        var editor = await StartAsync();

        var cut = RenderDetails(editor, phone: false);

        var layout = cut.WaitForElement(".tl-editor-details-layout");
        Assert.DoesNotContain("tl-editor-details-layout--phone", layout.ClassList);
        Assert.DoesNotContain("tl-editor-touch", layout.ClassList);
        Assert.False(layout.HasAttribute("data-phone"));
        Assert.Empty(cut.FindAll("[data-quick-facts-toggle]"));
        Assert.Empty(cut.FindAll(".tl-tap-reveal"));
    }

    [Fact]
    public async Task OnAPhone_QuickFactsIsASectionUnderTheFormThatIsClosedUntilItIsTapped()
    {
        var editor = await StartAsync();
        var cut = RenderDetails(editor, phone: true);

        var toggle = cut.WaitForElement("[data-quick-facts-toggle]");

        Assert.Equal("false", toggle.GetAttribute("aria-expanded"));
        Assert.Empty(cut.FindAll(".tl-quick-facts__row"));
        toggle.Click();
        cut.WaitForAssertion(() => Assert.Equal("true", cut.Find("[data-quick-facts-toggle]").GetAttribute("aria-expanded")));
        Assert.NotEmpty(cut.FindAll(".tl-quick-facts__row"));
    }

    [Fact]
    public async Task OnADesktop_QuickFactsIsOpenWithNoToggle()
    {
        var editor = await StartAsync();

        var cut = RenderDetails(editor, phone: false);

        cut.WaitForElement(".tl-quick-facts");
        Assert.Empty(cut.FindAll("[data-quick-facts-toggle]"));
        Assert.NotEmpty(cut.FindAll(".tl-quick-facts__row"));
    }

    [Fact]
    public async Task OnAPhone_TheReasonAFieldIsLockedIsOneTapAwayInTheAppsPopOut()
    {
        var editor = await StartAsync(subject: true);
        var cut = RenderDetails(editor, phone: true);

        var trigger = cut.WaitForElement(".tl-editor-field--locked[data-field-key='title'] button.tl-tap-reveal__trigger");
        Assert.Equal("false", trigger.GetAttribute("aria-expanded"));
        Assert.Empty(cut.FindAll("[data-testid='tap-reveal-text']"));
        Assert.DoesNotContain("title=", trigger.OuterHtml, StringComparison.Ordinal);

        trigger.Click();

        cut.WaitForAssertion(() => Assert.False(string.IsNullOrWhiteSpace(cut.Find("[data-testid='tap-reveal-text']").TextContent)));
        Assert.Equal("true", cut.Find(".tl-editor-field--locked[data-field-key='title'] button.tl-tap-reveal__trigger").GetAttribute("aria-expanded"));
    }

    [Fact]
    public async Task OnADesktop_TheReasonAFieldIsLockedStaysATooltipAndShowsNoTapButton()
    {
        var editor = await StartAsync(subject: true);

        var cut = RenderDetails(editor, phone: false);

        cut.WaitForElement(".tl-editor-field--locked[data-field-key='title']");
        Assert.Empty(cut.FindAll("button.tl-tap-reveal__trigger"));
    }

    // -- Artwork --------------------------------------------------------------------------------------

    private async Task<IRenderedComponent<EditorArtworkTool>> RenderArtworkAsync(bool phone)
    {
        var editor = await StartAsync();
        var api = Services.GetRequiredService<IEngineApiClient>();
        var preview = new ArtworkEditPreview(editor.Session, editor.Fields);
        var picker = new ArtworkPickerSession(api, editor.Session, editor.Fields, preview);
        await editor.Flow.SelectToolAsync(EditTool.Artwork);
        return Render<EditorArtworkTool>(parameters => parameters
            .Add(tool => tool.Mode, editor.Mode)
            .Add(tool => tool.Picker, picker)
            .Add(tool => tool.Phone, phone));
    }

    [Fact]
    public async Task OnAPhone_ArtworkIsMarkedForTwoPicturesPerRow_AndRefreshOpensAFullScreenStepWithAWayBack()
    {
        var cut = await RenderArtworkAsync(phone: true);
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(".tl-art-picker__version")));
        var root = cut.Find(".tl-art-picker");
        Assert.Contains("tl-art-picker--phone", root.ClassList);
        Assert.Contains("tl-editor-touch", root.ClassList);

        cut.Find("button.tl-art-picker__refresh").Click();

        var step = cut.WaitForElement(".tl-art-picker__add-panel--step");
        Assert.Equal("true", step.GetAttribute("data-artwork-step"));
        cut.Find(".tl-art-picker__step-bar .tl-art-picker__back").Click();
        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll(".tl-art-picker__add-panel")));
    }

    [Fact]
    public async Task OnAPhone_UploadOffersThePhonesPhotosAndItsCameraThroughTheSameUpload()
    {
        var cut = await RenderArtworkAsync(phone: true);
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(".tl-art-picker__version")));
        cut.Find("button.tl-art-picker__refresh").Click();
        cut.WaitForElement(".tl-art-picker__sources");

        cut.FindAll(".tl-art-picker__sources .app-segmented-control__item").Single(item => item.TextContent.Trim() == "Upload").Click();

        cut.WaitForAssertion(() => Assert.NotNull(cut.Find("[data-artwork-photos]")));
        var inputs = cut.FindAll(".tl-art-picker__phone-sources input[type='file']");
        Assert.Equal(2, inputs.Count);
        Assert.All(inputs, input => Assert.Equal("image/jpeg,image/png", input.GetAttribute("accept")));
        Assert.Single(inputs, input => input.GetAttribute("capture") == "environment");
    }

    [Fact]
    public async Task OnADesktop_ArtworkIsUnchanged_WithNoPhoneStepOrCameraSource()
    {
        var cut = await RenderArtworkAsync(phone: false);
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(".tl-art-picker__version")));
        Assert.DoesNotContain("tl-art-picker--phone", cut.Find(".tl-art-picker").ClassList);

        cut.Find("button.tl-art-picker__refresh").Click();

        cut.WaitForElement(".tl-art-picker__add-panel");
        Assert.Empty(cut.FindAll(".tl-art-picker__add-panel--step"));
        Assert.Empty(cut.FindAll("[data-artwork-camera]"));
    }

    [Fact]
    public async Task OnAPhone_WhenTheEngineCannotBeReached_ArtworkSaysSoAndOffersTryAgain()
    {
        _artworkFails = true;

        var cut = await RenderArtworkAsync(phone: true);

        cut.WaitForElement("[data-artwork-retry]");
        _artworkFails = false;
        cut.Find("[data-artwork-retry]").Click();
        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll("[data-artwork-retry]")));
    }

    // -- History --------------------------------------------------------------------------------------

    [Fact]
    public void OnAPhone_HistoryIsAListWhoseEntriesOpenTheirDetailsOnATap()
    {
        var cut = Render<EditorHistoryTool>(p => p.Add(x => x.EntityId, MovieId).Add(x => x.Phone, true));

        var entry = cut.WaitForElement("[data-history-entry]");
        Assert.Equal("false", entry.GetAttribute("aria-expanded"));
        Assert.Empty(cut.FindAll("[data-history-details]"));
        entry.Click();

        cut.WaitForAssertion(() => Assert.Contains("Dune to Dune: Part One", cut.Find("[data-history-details]").TextContent, StringComparison.Ordinal));
        Assert.Equal("true", cut.Find("[data-history-entry]").GetAttribute("aria-expanded"));
        entry = cut.Find("[data-history-entry]");
        entry.Click();
        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll("[data-history-details]")));
    }

    [Fact]
    public void OnADesktop_HistoryIsUnchanged()
    {
        var cut = Render<EditorHistoryTool>(p => p.Add(x => x.EntityId, MovieId));

        cut.WaitForAssertion(() => Assert.Single(cut.FindAll(".tl-history__row")));
        Assert.Empty(cut.FindAll("[data-history-entry]"));
        Assert.DoesNotContain("tl-history--phone", cut.Find(".tl-history").ClassList);
    }

    [Fact]
    public void OnAPhone_WhenHistoryCannotBeRead_ItSaysSoAndOffersTryAgain()
    {
        _historyFails = true;
        var cut = Render<EditorHistoryTool>(p => p.Add(x => x.EntityId, MovieId).Add(x => x.Phone, true));

        cut.WaitForElement("[data-history-retry]");
        Assert.Contains("could not be read", cut.Find("[role='alert']").TextContent, StringComparison.Ordinal);
        _historyFails = false;
        cut.Find("[data-history-retry]").Click();

        cut.WaitForAssertion(() => Assert.Single(cut.FindAll("[data-history-entry]")));
    }

    // -- Bookmarks ------------------------------------------------------------------------------------

    private async Task<IRenderedComponent<EditorBookmarksPanel>> RenderBookmarksAsync(bool phone)
    {
        var editor = await StartAsync();
        editor.Session.Begin([new EditTrailItem(MovieId, "Dune: Part One", "Movie")]);
        return Render<EditorBookmarksPanel>(parameters => parameters
            .Add(panel => panel.Flow, editor.Flow)
            .Add(panel => panel.Phone, phone));
    }

    [Fact]
    public async Task OnAPhone_BookmarksAreRowsWithTheFrameTheTimeAndTheNameAndTheirActionsInTheMenu()
    {
        var cut = await RenderBookmarksAsync(phone: true);

        cut.WaitForState(() => cut.FindAll("li.tl-bookmarks__row").Count == 3);
        var root = cut.Find(".tl-bookmarks");
        Assert.Contains("tl-bookmarks--phone", root.ClassList);
        Assert.Contains("tl-editor-touch", root.ClassList);
        Assert.Equal("true", root.GetAttribute("data-phone"));
        var row = cut.FindAll("ul.tl-bookmarks__list")[0].QuerySelector("li.tl-bookmarks__row")!;
        Assert.NotNull(row.QuerySelector(".tl-bookmarks__frame"));
        Assert.Contains("12:04", row.TextContent, StringComparison.Ordinal);
        Assert.Contains("Opening", row.TextContent, StringComparison.Ordinal);
        Assert.NotNull(row.QuerySelector(".app-row-menu__trigger"));
    }

    [Fact]
    public async Task OnADesktop_BookmarksAreUnchanged()
    {
        var cut = await RenderBookmarksAsync(phone: false);

        cut.WaitForState(() => cut.FindAll("li.tl-bookmarks__row").Count == 3);
        var root = cut.Find(".tl-bookmarks");
        Assert.DoesNotContain("tl-bookmarks--phone", root.ClassList);
        Assert.DoesNotContain("tl-editor-touch", root.ClassList);
        Assert.False(root.HasAttribute("data-phone"));
    }
}
