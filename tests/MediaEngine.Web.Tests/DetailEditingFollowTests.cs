using Bunit;
using MediaEngine.Contracts.Details;
using MediaEngine.Contracts.Metadata;
using MediaEngine.Web.Components.Details;
using MediaEngine.Web.Components.MediaEditor;
using MediaEngine.Web.Components.Shared;
using MediaEngine.Web.Services.Editing;
using MediaEngine.Web.Services.Integration;
using MediaEngine.Web.Services.Navigation;
using MediaEngine.Web.Services.Playback;
using MediaEngine.Web.Services.Theming;
using MediaEngine.Web.Services.Ui;
using MediaEngine.Web.Tests.Support;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MediaEngine.Web.Tests;

/// <summary>
/// Following the surviving item after a move (details overlay spec D9, D12): when a move removes the title a page shows, the address
/// follows the item and the page that loads there picks Editing Mode, the Match and Identity section and the move's result line back
/// up, so the follow does not end the edit. The Engine is a hand-written stub; nothing is moved anywhere.
/// </summary>
public sealed class DetailEditingFollowTests : AsyncBunitContext
{
    private static readonly Guid MovieId = EditingModeTestData.MovieId;
    private static readonly Guid SurvivorId = Guid.Parse("22222222-aaaa-bbbb-cccc-000000000002");
    private const string Line = "Moved Paranoid Android to OK Computer as Track 2.";

    private readonly DeviceContextService _device = new(EngineApiClientStub.CreateDefault());
    private readonly ManualClock _clock = new();

    public DetailEditingFollowTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddLogging();
        Services.AddNativeUiServices();
        var api = EngineApiClientStub.Create(stub =>
            stub.SetHandler(nameof(IEngineApiClient.GetMediaEditorContextAsync), _ => Task.FromResult<MediaEditorContextDto?>(EditingModeTestData.MovieContext())));
        Services.AddSingleton<IEngineApiClient>(api);
        Services.AddSingleton<IDetailsOverlayHistory>(new NoHistory());
        Services.AddSingleton<TimeProvider>(_clock);
        Services.AddScoped<DetailsOverlayState>();
        Services.AddSingleton(_device);
        Services.AddSingleton(new DashboardSessionAccessor());
        Services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        Services.AddScoped<ActiveProfileSessionService>();
        Services.AddScoped<UniverseStateContainer>();
        Services.AddScoped<UIOrchestratorService>();
        Services.AddScoped<CollectionEditorLauncherService>();
        Services.AddSingleton(new SavedItemService(api));
        Services.AddSingleton(new MediaReactionService(api));
        Services.AddSingleton<ListenAudioDragService>();
        Services.AddSingleton(_ => new PlaybackSessionController(null!, null!));
        Services.AddSingleton(_ => new ProviderCatalogueService(api, new MemoryCache(new MemoryCacheOptions())));
        Render<AppPopoverHost>();
    }

    private DetailsOverlayState State => Services.GetRequiredService<DetailsOverlayState>();

    private NavigationManager Nav => Services.GetRequiredService<NavigationManager>();

    private static DetailPageViewModel Model(Guid id) => new()
    {
        Id = id.ToString("D"),
        EntityType = DetailEntityType.Movie,
        Title = "Dune: Part One",
        Tabs = [new DetailTab { Key = "overview", Label = "Overview" }],
        OverflowActions = [new DetailAction { Key = "edit", Label = "Edit", Icon = "edit" }],
    };

    private Task UseDeviceAsync(string formFactor) => formFactor == "desktop"
        ? _device.InitialiseAsync(new DeviceContextSnapshot("web", "desktop", false, FinePointer: true, Hover: true))
        : _device.InitialiseAsync(new DeviceContextSnapshot("web", formFactor, true));

    private IRenderedComponent<DetailPage> RenderPage(Guid id) =>
        Render<DetailPage>(parameters => parameters.Add(page => page.Model, Model(id)));

    private static void StartEditing(IRenderedComponent<DetailPage> cut)
    {
        cut.Find("button[aria-label='More actions']").Click();
        cut.FindAll("[role='menuitem']").Single(item => item.TextContent.Trim() == "Edit").Click();
        cut.WaitForAssertion(() => Assert.Single(cut.FindAll(".tl-editing-bar")));

        // The move is made from Match, which the editor hosts in its right-hand panel.
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(".tl-editing-bar__tool")));
        cut.FindAll(".tl-editing-bar__tool").Single(tool => tool.TextContent.Trim() == "Match").Click();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(".tl-editor-panel[data-tool='match']")));
    }

    [Fact]
    public async Task ADeviceThatNoLongerQualifies_OpensTheSurvivingPageToReadWithoutEditing()
    {
        await UseDeviceAsync("desktop");
        State.OfferEditResume(new DetailsEditResume(SurvivorId, EditingSection.Match, Line));
        await UseDeviceAsync("tablet");

        var page = RenderPage(SurvivorId);

        Assert.Empty(page.FindAll(".tl-editing-bar"));
        Assert.False(State.TryTakeEditResume(SurvivorId, out _));
    }

    [Fact]
    public async Task APageForAnotherItem_DoesNotPickTheNoteUpAndTheNoteIsDropped()
    {
        await UseDeviceAsync("desktop");
        State.OfferEditResume(new DetailsEditResume(SurvivorId, EditingSection.Match, Line));

        var page = RenderPage(MovieId);

        Assert.Empty(page.FindAll(".tl-editing-bar"));
        Assert.False(State.TryTakeEditResume(SurvivorId, out _));
    }

    // -- the one-shot note ------------------------------------------------------------------------

    [Fact]
    public void TheNote_IsTakenOnceForItsItemAndThenGone()
    {
        State.OfferEditResume(new DetailsEditResume(SurvivorId, EditingSection.Match, Line));

        Assert.True(State.TryTakeEditResume(SurvivorId, out var resume));
        Assert.Equal(EditingSection.Match, resume.Section);
        Assert.Equal(Line, resume.ResultLine);
        Assert.False(State.TryTakeEditResume(SurvivorId, out _));
    }

    [Fact]
    public void TheNote_LapsesWhenNoPageTakesItSoon_SoALaterOpeningOfTheItemStaysInReadMode()
    {
        State.OfferEditResume(new DetailsEditResume(SurvivorId, EditingSection.Match, Line));
        _clock.Advance(DetailsOverlayState.EditResumeLifetime + TimeSpan.FromSeconds(1));

        Assert.False(State.TryTakeEditResume(SurvivorId, out _));
    }

    [Fact]
    public void ANewNote_ReplacesTheOldOne_AndWithdrawingRemovesIt()
    {
        State.OfferEditResume(new DetailsEditResume(MovieId, EditingSection.Match, "old"));
        State.OfferEditResume(new DetailsEditResume(SurvivorId, EditingSection.Match, "new"));

        Assert.False(State.TryTakeEditResume(MovieId, out _));

        State.OfferEditResume(new DetailsEditResume(SurvivorId, EditingSection.Match, "again"));
        State.WithdrawEditResume();

        Assert.False(State.TryTakeEditResume(SurvivorId, out _));
    }

    // -- when to follow ----------------------------------------------------------------------------

    private static readonly Guid PartOfA = Guid.Parse("aa000000-0000-0000-0000-00000000000a");
    private static readonly Guid AudiobookA = Guid.Parse("a0000000-0000-0000-0000-00000000000a");
    private static readonly Guid AudiobookB = Guid.Parse("b0000000-0000-0000-0000-00000000000b");

    [Fact]
    public void ATitleMergedIntoAnEntrysOwnTitle_RemovedItsWork()
    {
        var removed = LibraryMoveRules.RemovedEditedWork(MovieId, MovieId, SurvivorId, ["Paranoid Android"], []);

        Assert.True(removed);
    }

    [Fact]
    public void ATitleThatStayedItsOwn_RemovedNothing()
    {
        var removed = LibraryMoveRules.RemovedEditedWork(MovieId, MovieId, MovieId, ["Paranoid Android"], []);

        Assert.False(removed);
    }

    [Fact]
    public void ANoAnswerFromTheEngine_RemovedNothing()
    {
        Assert.False(LibraryMoveRules.RemovedEditedWork(MovieId, MovieId, null, ["x"], []));
        Assert.False(LibraryMoveRules.RemovedEditedWork(MovieId, MovieId, Guid.Empty, ["x"], []));
    }

    [Fact]
    public void AnAudiobookPartJoiningAnotherWhileTheSourceStillHasOtherParts_RemovedNothingSoThePageStays()
    {
        var removed = LibraryMoveRules.RemovedEditedWork(AudiobookA, PartOfA, AudiobookB, ["Audiobook A", "Audiobook A (long title)"], []);

        Assert.False(removed);
    }

    [Fact]
    public void AnAudiobookPartJoiningAnotherWhoseLastPartItWas_RemovedTheAudiobookSoThePageFollows()
    {
        var removed = LibraryMoveRules.RemovedEditedWork(AudiobookA, PartOfA, AudiobookB, [null, "audiobook a "], ["Audiobook A"]);

        Assert.True(removed);
    }

    [Fact]
    public void AnAudiobookPartMove_WithOtherPlacesRemoved_StillLeavesTheSourceAlone()
    {
        var removed = LibraryMoveRules.RemovedEditedWork(AudiobookA, PartOfA, AudiobookB, ["Audiobook A"], ["Season 2", "My road trip"]);

        Assert.False(removed);
    }

    private sealed class NoHistory : IDetailsOverlayHistory
    {
        public ValueTask<bool> GoBackAsync(int steps) => ValueTask.FromResult(true);
    }

    private sealed class ManualClock : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }
}
