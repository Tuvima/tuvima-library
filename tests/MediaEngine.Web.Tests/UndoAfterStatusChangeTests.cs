using Bunit;
using MediaEngine.Contracts.Details;
using MediaEngine.Contracts.Progress;
using MediaEngine.Web.Components.Details;
using MediaEngine.Web.Components.Shared;
using MediaEngine.Web.Services.Integration;
using MediaEngine.Web.Services.Playback;
using MediaEngine.Web.Services.Theming;
using MediaEngine.Web.Services.Ui;
using MediaEngine.Web.Tests.Support;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace MediaEngine.Web.Tests;

/// <summary>
/// A personal status change (listened, watched, finished) says so in place, with Undo, for a few seconds. There is no pop-up: the line belongs
/// to the surface that hosts the control (the detail page, the song table, the player).
/// </summary>
public sealed class UndoAfterStatusChangeTests : AsyncBunitContext
{
    private static readonly Guid MovieId = Guid.Parse("11111111-aaaa-bbbb-cccc-000000000009");
    private readonly Guid _commandId = Guid.NewGuid();
    private readonly List<Guid> _undone = [];
    private bool _failUndo;
    private int _completed;
    private int _detailLoads;
    private readonly DeviceContextService _device = new(EngineApiClientStub.CreateDefault());

    public UndoAfterStatusChangeTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddLogging();
        Services.AddNativeUiServices();
        var api = EngineApiClientStub.Create(stub =>
        {
            stub.SetHandler(nameof(IEngineApiClient.GetPersonalStatusAsync), _ =>
                Task.FromResult<PersonalStatusInfo?>(new PersonalStatusInfo("r" + _completed, 4, 1, _completed, false)));
            stub.SetHandler(nameof(IEngineApiClient.ChangePersonalStatusAsync), _ =>
            {
                _completed = 4;
                return Task.FromResult(new PersonalStatusCommandResult(_commandId, 3, "r4"));
            });
            stub.SetHandler(nameof(IEngineApiClient.UndoPersonalStatusAsync), args =>
            {
                if (_failUndo)
                {
                    throw new InvalidOperationException("That change can no longer be undone.");
                }

                _undone.Add((Guid)args![0]!);
                _completed = 0;
                return Task.FromResult(new PersonalStatusCommandResult(_commandId, 3, "r5"));
            });
            stub.SetHandler(nameof(IEngineApiClient.GetDetailPageAsync), _ =>
            {
                _detailLoads++;
                return Task.FromResult<DetailPageViewModel?>(Model());
            });
        });
        Services.AddSingleton<IEngineApiClient>(api);
        Services.AddSingleton(_device);
        Services.AddSingleton(new DashboardSessionAccessor());
        Services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        Services.AddScoped<ActiveProfileSessionService>();
        Services.AddScoped<UniverseStateContainer>();
        Services.AddScoped<UIOrchestratorService>();
        Services.AddScoped<MediaEngine.Web.Services.Editing.CollectionEditorLauncherService>();
        Services.AddSingleton(new SavedItemService(api));
        Services.AddSingleton(new MediaReactionService(api));
        Services.AddSingleton<ListenAudioDragService>();
        Services.AddSingleton(_ => new PlaybackSessionController(null!, null!));
    }

    private static DetailPageViewModel Model() => new()
    {
        Id = MovieId.ToString("D"),
        EntityType = DetailEntityType.Movie,
        Title = "Dune: Part One",
        Tabs = [new DetailTab { Key = "overview", Label = "Overview" }],
        OverflowActions = [new DetailAction { Key = DetailActionKeys.StatusComplete, Label = "Mark as watched", Icon = "check" }],
    };

    private IRenderedComponent<DetailPage> RenderPage()
    {
        var cut = Render<DetailPage>(parameters => parameters.Add(page => page.Model, Model()));
        cut.Find(".tl-detail-secondary-actions button[aria-label='More actions']").Click();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[role='menuitem']")));
        cut.FindAll("[role='menuitem']").Single(item => item.TextContent.Trim() == "Mark as watched").Click();
        return cut;
    }

    [Fact]
    public async Task ADetailPageStatusChange_SaysSoInPlaceWithUndo_AndUndoCallsTheSameEngineUndoThenRefreshes()
    {
        await _device.InitialiseAsync(new DeviceContextSnapshot("web", "desktop", false, FinePointer: true, Hover: true));
        var cut = RenderPage();

        cut.WaitForAssertion(() => Assert.Contains("Status updated for 3 owned item(s).", cut.Find("[data-detail-undo]").TextContent));
        var loadsBeforeUndo = _detailLoads;

        cut.Find("[data-detail-undo] [data-undo-note-action]").Click();

        cut.WaitForAssertion(() => Assert.Equal([_commandId], _undone));
        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll("[data-detail-undo] [data-undo-note-line]")));
        Assert.True(_detailLoads > loadsBeforeUndo);
    }

    [Fact]
    public async Task TheStatusMenuOfASongRow_HandsTheUndoUpAfterAChange_AndUndoReachesTheEngine()
    {
        var offers = new List<UndoOffer>();
        var cut = Render<PersonalStatusMenuItems>(parameters => parameters
            .Add(menu => menu.WorkId, Guid.NewGuid())
            .Add(menu => menu.OnChanged, EventCallback.Factory.Create<UndoOffer>(this, offers.Add)));
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[role='menuitem']")));

        cut.FindAll("[role='menuitem']").Single(item => item.TextContent.Trim() == "Mark as listened").Click();

        cut.WaitForAssertion(() => Assert.Single(offers));
        Assert.Equal("Listening status updated.", offers[0].Message);
        await cut.InvokeAsync(offers[0].UndoAsync);
        Assert.Equal([_commandId], _undone);
    }

    [Fact]
    public async Task ThePlayerItemMenu_HandsTheUndoUpAfterMarkingDone_AndUndoReachesTheEngine()
    {
        Services.AddSingleton(new PlaybackTransientToolCoordinator());
        Render<AppPopoverHost>();
        var offers = new List<UndoOffer>();
        var item = new ListenQueueItem { WorkId = Guid.NewGuid(), AssetId = Guid.NewGuid(), Title = "Track" };
        var cut = Render<PlaybackItemMenu>(parameters => parameters
            .Add(menu => menu.Media, PlaybackBarMedia.Music)
            .Add(menu => menu.Item, item)
            .Add(menu => menu.OnStatusChanged, EventCallback.Factory.Create<UndoOffer>(this, offers.Add)));
        await cut.InvokeAsync(() => cut.FindComponent<PlaybackPanel>().Instance.OpenAsync(true));
        cut.WaitForElement("[data-playback-item-menu]");

        cut.FindAll("[data-playback-item-menu] [role='menuitem']").Single(row => row.TextContent.Trim() == "Mark as listened").Click();

        cut.WaitForAssertion(() => Assert.Single(offers));
        Assert.Equal("Status updated.", offers[0].Message);
        await cut.InvokeAsync(offers[0].UndoAsync);
        Assert.Equal([_commandId], _undone);
    }

    [Fact]
    public void TheUndoNote_ShowsTheMessageWithUndo_AndPressingUndoRunsItClearsTheLineAndTellsTheHost()
    {
        var ran = 0;
        var cleared = 0;
        var cut = Render<AppUndoNote>(p => p
            .Add(n => n.Offer, new UndoOffer("Status updated.", () => { ran++; return Task.CompletedTask; }))
            .Add(n => n.Delay, TimeSpan.FromSeconds(30))
            .Add(n => n.OnCleared, EventCallback.Factory.Create(this, () => { cleared++; })));

        Assert.Equal("status", cut.Find("[data-undo-note-line]").GetAttribute("role"));
        Assert.Contains("Status updated.", cut.Find("[data-undo-note-line]").TextContent);
        Assert.Equal("Undo", cut.Find("[data-undo-note-action]").TextContent.Trim());

        cut.Find("[data-undo-note-action]").Click();

        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll("[data-undo-note-line]")));
        Assert.Equal(1, ran);
        Assert.Equal(1, cleared);
    }

    [Fact]
    public void TheUndoNote_GoesAwayByItselfAfterTheDelay()
    {
        var cut = Render<AppUndoNote>(p => p
            .Add(n => n.Offer, new UndoOffer("Status updated.", () => Task.CompletedTask))
            .Add(n => n.Delay, TimeSpan.FromMilliseconds(60)));
        Assert.Single(cut.FindAll("[data-undo-note-line]"));

        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll("[data-undo-note-line]")));
    }

    [Fact]
    public void TheUndoNote_ANewerOfferReplacesTheOlderOne_AndNoOfferClearsTheLine()
    {
        var cut = Render<AppUndoNote>(p => p
            .Add(n => n.Offer, new UndoOffer("First.", () => Task.CompletedTask)).Add(n => n.Delay, TimeSpan.FromSeconds(30)));

        cut.Render(p => p.Add(n => n.Offer, new UndoOffer("Second.", () => Task.CompletedTask)).Add(n => n.Delay, TimeSpan.FromSeconds(30)));
        Assert.Contains("Second.", cut.Find("[data-undo-note-line]").TextContent);
        Assert.DoesNotContain("First.", cut.Markup);

        cut.Render(p => p.Add(n => n.Offer, (UndoOffer?)null).Add(n => n.Delay, TimeSpan.FromSeconds(30)));
        Assert.Empty(cut.FindAll("[data-undo-note-line]"));
    }

    [Fact]
    public void TheUndoNote_WhenUndoFails_SaysWhyInPlaceAsAnAlert()
    {
        _failUndo = true;
        var cut = Render<AppUndoNote>(p => p
            .Add(n => n.Offer, new UndoOffer("Status updated.", () => Task.FromException(new InvalidOperationException("That change can no longer be undone."))))
            .Add(n => n.Delay, TimeSpan.FromSeconds(30)));

        cut.Find("[data-undo-note-action]").Click();

        cut.WaitForAssertion(() => Assert.Contains("That change can no longer be undone.", cut.Find("[data-undo-note-error]").TextContent));
        Assert.Equal("alert", cut.Find("[data-undo-note-error]").GetAttribute("role"));
    }
}
