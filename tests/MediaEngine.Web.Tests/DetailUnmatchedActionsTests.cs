using Bunit;
using MediaEngine.Contracts.Authentication;
using MediaEngine.Contracts.Details;
using MediaEngine.Contracts.Library;
using MediaEngine.Web.Components.Details;
using MediaEngine.Web.Components.Shared;
using MediaEngine.Web.Services.Editing;
using MediaEngine.Web.Services.Integration;
using MediaEngine.Web.Services.Playback;
using MediaEngine.Web.Services.Theming;
using MediaEngine.Web.Services.Ui;
using MediaEngine.Web.Tests.Support;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace MediaEngine.Web.Tests;

/// <summary>
/// The "..." menu's Unmatched actions (Library setup spec D34, D40): Retry now and Keep as is while a file is Unmatched and not kept,
/// Look up again once every file is kept. Each calls the Engine's Unmatched item route for the page's files it applies to. They are
/// administrator actions, not edits, so a device that cannot edit still shows them; the Engine sends them only to administrators.
/// </summary>
public sealed class DetailUnmatchedActionsTests : AsyncBunitContext
{
    private static readonly Guid MovieId = Guid.Parse("11111111-aaaa-bbbb-cccc-000000000002");
    private static readonly Guid OpenAsset = Guid.Parse("22222222-aaaa-bbbb-cccc-000000000001");
    private static readonly Guid KeptAsset = Guid.Parse("22222222-aaaa-bbbb-cccc-000000000002");

    private readonly DeviceContextService _device = new(EngineApiClientStub.CreateDefault());
    private readonly List<string> _calls = [];
    private int _detailLoads;
    private bool _failActions;

    public DetailUnmatchedActionsTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddLogging();
        Services.AddNativeUiServices();
        var api = EngineApiClientStub.Create(stub =>
        {
            stub.SetHandler(nameof(IEngineApiClient.RetryUnmatchedItemAsync), args => Act("retry", args));
            stub.SetHandler(nameof(IEngineApiClient.KeepUnmatchedItemAsync), args => Act("keep", args));
            stub.SetHandler(nameof(IEngineApiClient.LookUpUnmatchedItemAgainAsync), args => Act("look-up-again", args));
            stub.SetHandler(nameof(IEngineApiClient.GetDetailPageAsync), _ =>
            {
                _detailLoads++;
                return Task.FromResult<DetailPageViewModel?>(Model([], []));
            });
        });
        Services.AddSingleton<IEngineApiClient>(api);
        Services.AddSingleton(_device);
        Services.AddSingleton(new DashboardSessionAccessor());
        Services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        Services.AddScoped<ActiveProfileSessionService>();
        Services.AddScoped<UniverseStateContainer>();
        Services.AddScoped<UIOrchestratorService>();
        Services.AddScoped<MediaEditorLauncherService>();
        Services.AddScoped<CollectionEditorLauncherService>();
        Services.AddSingleton(new SavedItemService(api));
        Services.AddSingleton(new MediaReactionService(api));
        Services.AddSingleton<ListenAudioDragService>();
        Services.AddSingleton(_ => new PlaybackSessionController(null!, null!));
    }

    private Task<UnmatchedActionResultDto?> Act(string route, object?[]? args)
    {
        _calls.Add($"{route}:{args![0]}");
        return Task.FromResult<UnmatchedActionResultDto?>(_failActions ? null : new UnmatchedActionResultDto { Affected = 1 });
    }

    private Task UseDeviceAsync(string formFactor, bool fine = true, bool hover = true) =>
        _device.InitialiseAsync(new DeviceContextSnapshot("web", formFactor, false, FinePointer: fine, Hover: hover));

    private static DetailPageViewModel Model(IReadOnlyList<DetailAction> actions, IReadOnlyList<DetailUnmatchedAssetViewModel> assets) => new()
    {
        Id = MovieId.ToString("D"),
        EntityType = DetailEntityType.Movie,
        Title = "Dune: Part One",
        Tabs = [new DetailTab { Key = "overview", Label = "Overview" }],
        OverflowActions = [new DetailAction { Key = "edit", Label = "Edit", Icon = "edit" }, .. actions],
        UnmatchedAssets = assets,
    };

    private static readonly DetailAction[] NotKeptActions =
    [
        new() { Key = "unmatched-retry", Label = "Retry now", Icon = "refresh", IsAdminOnly = true },
        new() { Key = "unmatched-keep", Label = "Keep as is", Icon = "check", IsAdminOnly = true },
    ];

    private static readonly DetailAction[] KeptActions =
    [
        new() { Key = "unmatched-look-up-again", Label = "Look up again", Icon = "search", IsAdminOnly = true },
    ];

    private static DetailUnmatchedAssetViewModel Asset(Guid id, bool kept) => new() { AssetId = id, KeptAsIs = kept };

    private IRenderedComponent<DetailPage> RenderPage(DetailPageViewModel model) =>
        Render<DetailPage>(parameters => parameters.Add(page => page.Model, model));

    private static IReadOnlyList<string> OpenMenu(IRenderedComponent<DetailPage> cut)
    {
        cut.Find(".tl-detail-secondary-actions button[aria-label='More actions']").Click();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[role='menuitem']")));
        return [.. cut.FindAll("[role='menuitem']").Select(item => item.TextContent.Trim())];
    }

    private static void Choose(IRenderedComponent<DetailPage> cut, string label) =>
        cut.FindAll("[role='menuitem']").Single(item => item.TextContent.Trim() == label).Click();

    private IReadOnlyList<string> Toasts => [.. Services.GetRequiredService<AppToastService>().Toasts.Select(toast => toast.Message)];

    [Fact]
    public async Task AnAdministratorPageWithAnUnmatchedFileThatIsNotKept_OffersRetryNowAndKeepAsIs()
    {
        await UseDeviceAsync("desktop");
        var cut = RenderPage(Model(NotKeptActions, [Asset(OpenAsset, kept: false)]));

        var labels = OpenMenu(cut);

        Assert.Contains("Retry now", labels);
        Assert.Contains("Keep as is", labels);
        Assert.DoesNotContain("Look up again", labels);
    }

    [Fact]
    public async Task RetryNow_CallsTheEngineForEveryFileThatIsNotKeptOnly_ThenRefreshesThePageWithAToast()
    {
        await UseDeviceAsync("desktop");
        var cut = RenderPage(Model(NotKeptActions, [Asset(OpenAsset, kept: false), Asset(KeptAsset, kept: true)]));
        OpenMenu(cut);

        Choose(cut, "Retry now");

        cut.WaitForAssertion(() => Assert.Equal(1, _detailLoads));
        Assert.Equal([$"retry:{OpenAsset}"], _calls);
        Assert.Contains("Looking it up again now.", Toasts);
    }

    [Fact]
    public async Task KeepAsIs_AsksFirst_AndOnlyThenCallsTheEngine()
    {
        await UseDeviceAsync("desktop");
        var host = Render<AppDialogHost>();
        var cut = RenderPage(Model(NotKeptActions, [Asset(OpenAsset, kept: false)]));
        OpenMenu(cut);

        Choose(cut, "Keep as is");

        host.WaitForElement(".app-confirm");
        Assert.Contains("Stop trying to match this? It stays under Unmatched, marked Kept. You can look it up again later.", host.Markup);
        Assert.Empty(_calls);

        host.FindAll(".app-dialog-shell__footer button").Single(button => button.TextContent.Trim() == "Keep as is").Click();

        cut.WaitForAssertion(() => Assert.Equal(1, _detailLoads));
        Assert.Equal([$"keep:{OpenAsset}"], _calls);
        Assert.Contains("Kept as is.", Toasts);
    }

    [Fact]
    public async Task KeepAsIs_ChangesNothingWhenThePersonCancels()
    {
        await UseDeviceAsync("desktop");
        var host = Render<AppDialogHost>();
        var cut = RenderPage(Model(NotKeptActions, [Asset(OpenAsset, kept: false)]));
        OpenMenu(cut);
        Choose(cut, "Keep as is");
        host.WaitForElement(".app-confirm");

        host.FindAll(".app-dialog-shell__footer button").Single(button => button.TextContent.Trim() == "Cancel").Click();

        cut.WaitForAssertion(() => Assert.Empty(host.FindAll(".app-confirm")));
        Assert.Empty(_calls);
        Assert.Equal(0, _detailLoads);
    }

    [Fact]
    public async Task AnAdministratorPageWhoseUnmatchedFilesAreAllKept_OffersLookUpAgainAndCallsTheEngineForTheKeptFiles()
    {
        await UseDeviceAsync("desktop");
        var cut = RenderPage(Model(KeptActions, [Asset(KeptAsset, kept: true)]));

        var labels = OpenMenu(cut);
        Assert.Contains("Look up again", labels);
        Assert.DoesNotContain("Retry now", labels);
        Assert.DoesNotContain("Keep as is", labels);

        Choose(cut, "Look up again");

        cut.WaitForAssertion(() => Assert.Equal(1, _detailLoads));
        Assert.Equal([$"look-up-again:{KeptAsset}"], _calls);
    }

    [Fact]
    public async Task APageTheEngineSentNoUnmatchedEntriesFor_ShowsNone()
    {
        await UseDeviceAsync("desktop");
        var cut = RenderPage(Model([], []));

        var labels = OpenMenu(cut);

        Assert.DoesNotContain("Retry now", labels);
        Assert.DoesNotContain("Keep as is", labels);
        Assert.DoesNotContain("Look up again", labels);
    }

    [Fact]
    public async Task OnADeviceThatCannotEdit_TheUnmatchedEntriesStayWhileEditDisappears()
    {
        await UseDeviceAsync("phone", fine: false, hover: false);
        var cut = RenderPage(Model(NotKeptActions, [Asset(OpenAsset, kept: false)]));

        var labels = OpenMenu(cut);

        Assert.Contains("Retry now", labels);
        Assert.Contains("Keep as is", labels);
        Assert.DoesNotContain("Edit", labels);
    }

    [Fact]
    public async Task WhenTheEngineRefusesTheAction_ThePersonIsToldAndNothingIsClaimedDone()
    {
        await UseDeviceAsync("desktop");
        _failActions = true;
        var cut = RenderPage(Model(NotKeptActions, [Asset(OpenAsset, kept: false)]));
        OpenMenu(cut);

        Choose(cut, "Retry now");

        cut.WaitForAssertion(() => Assert.Contains("That could not be done. Try again in a moment.", Toasts));
        Assert.DoesNotContain("Looking it up again now.", Toasts);
    }
}
