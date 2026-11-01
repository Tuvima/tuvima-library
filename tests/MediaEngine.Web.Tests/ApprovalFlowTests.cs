using Bunit;
using MediaEngine.Contracts.Ai;
using MediaEngine.Contracts.Approvals;
using MediaEngine.Contracts.Authentication;
using MediaEngine.Contracts.Plugins;
using MediaEngine.Contracts.Settings;
using MediaEngine.Web.Components.Settings;
using MediaEngine.Web.Components.Shared;
using MediaEngine.Web.Models.ViewDTOs;
using MediaEngine.Web.Services.Integration;
using MediaEngine.Web.Services.Navigation;
using MediaEngine.Web.Services.Theming;
using MediaEngine.Web.Tests.Support;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaEngine.Web.Tests;

/// <summary>
/// The Dashboard half of the approval rule: a switch-on action that comes back approval-required shows the Engine's words in
/// one dialog per item, repeats the action with the hashes when every one is confirmed, and changes nothing when any is
/// cancelled.
/// </summary>
public sealed class ApprovalFlowTests : AsyncBunitContext
{
    private static readonly Guid AccountId = Guid.Parse("00000000-0000-0000-0000-0000000000a1");
    private static readonly Guid ProfileId = Guid.Parse("00000000-0000-0000-0000-0000000000b1");

    private readonly DashboardSessionAccessor _session = new();
    private readonly IEngineApiClient _api;
    private readonly List<(string PluginId, bool Enabled, string? Hash)> _pluginCalls = [];
    private List<PluginSummaryResponse> _plugins = [];
    private AiUsageResponse _usage = new() { Days = 7 };
    private List<ApprovalRecordResponse> _approvals = [];
    private bool _pluginNeedsApproval = true;

    public ApprovalFlowTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddLogging();
        Services.AddLocalization();
        _api = EngineApiClientStub.Create(stub =>
        {
            stub.SetHandler(nameof(IEngineApiClient.GetPluginsAsync), _ =>
                Task.FromResult<IReadOnlyList<PluginSummaryResponse>>(_plugins.ToList()));
            stub.SetHandler(nameof(IEngineApiClient.GetAiUsageAsync), _ => Task.FromResult<AiUsageResponse?>(_usage));
            stub.SetHandler(nameof(IEngineApiClient.SetPluginEnabledAsync), args =>
            {
                var hash = (string?)args![2];
                _pluginCalls.Add(((string)args[0]!, (bool)args[1]!, hash));
                return Task.FromResult(_pluginNeedsApproval && hash is null && (bool)args[1]!
                    ? PluginActionResult.ApprovalRequired(new ApprovalRequiredResponse { Approvals = [Preview("plugin", "a.plugin", "Alpha", "plugin-hash")] })
                    : PluginActionResult.Success);
            });
            stub.SetHandler(nameof(IEngineApiClient.GetApprovalsAsync), _ =>
                Task.FromResult<IReadOnlyList<ApprovalRecordResponse>>(_approvals.ToList()));
        });
        Services.AddSingleton(_api);
        Services.AddSingleton(_session);
        Services.AddSingleton(new DeviceContextService(_api));
        Services.AddSingleton(serviceProvider => new UIOrchestratorService(
            _api,
            new UniverseStateContainer(),
            new ActiveProfileSessionService(
                serviceProvider.GetRequiredService<Microsoft.JSInterop.IJSRuntime>(), _api,
                authenticationStateProvider: new SessionOnlyAuthentication(), dashboardSession: _session),
            new ConfigurationBuilder().Build(),
            NullLogger<UIOrchestratorService>.Instance,
            dashboardSession: _session));
        Services.AddSingleton<SettingsAttentionState>();
        SignInAdministrator();
    }

    // ── Plugins ──────────────────────────────────────────────────────────────

    [Fact]
    public void PluginSwitch_ShowsTheEnginesWords_AndCancelLeavesItOffWithNoSecondRequest()
    {
        _plugins = [Plugin(enabled: false)];
        var dialogs = Render<AppDialogHost>();
        var cut = Render<PluginSettingsTab>();
        cut.WaitForAssertion(() => Assert.Single(cut.FindAll("[data-state-card=plugin]")));

        cut.Find("[data-state-card=plugin] input[role=switch]").Change(true);

        dialogs.WaitForAssertion(() => Assert.Contains("Turn on Alpha?", dialogs.Markup));
        Assert.Contains("Reads your media files", dialogs.Find("[data-testid=approval-can-do]").TextContent, StringComparison.Ordinal);
        Assert.Contains("Yes, to the internet", dialogs.Find("[data-testid=approval-sends-data]").TextContent, StringComparison.Ordinal);
        Assert.Contains("Plugins run inside the server without a sandbox", dialogs.Find("[data-testid=approval-notes]").TextContent, StringComparison.Ordinal);
        dialogs.FindAll("button").Single(button => button.TextContent.Trim() == "Cancel").Click();

        dialogs.WaitForAssertion(() => Assert.DoesNotContain("Turn on Alpha?", dialogs.Markup));
        Assert.Single(_pluginCalls);
        Assert.Equal("off", cut.Find("[data-state-card=plugin]").GetAttribute("data-health"));
        Assert.False(cut.Find("[data-state-card=plugin] input[role=switch]").HasAttribute("checked"));
    }

    [Fact]
    public void PluginSwitch_TurnOn_RepeatsTheRequestCarryingTheHash_AndTheCardTurnsOn()
    {
        _plugins = [Plugin(enabled: false)];
        var dialogs = Render<AppDialogHost>();
        var cut = Render<PluginSettingsTab>();
        cut.WaitForAssertion(() => Assert.Single(cut.FindAll("[data-state-card=plugin]")));
        cut.Find("[data-state-card=plugin] input[role=switch]").Change(true);
        dialogs.WaitForAssertion(() => Assert.Contains("Turn on Alpha?", dialogs.Markup));

        dialogs.FindAll("button").Single(button => button.TextContent.Trim() == "Turn on").Click();

        cut.WaitForAssertion(() => Assert.NotEqual("off", cut.Find("[data-state-card=plugin]").GetAttribute("data-health")));
        Assert.Equal(2, _pluginCalls.Count);
        Assert.Null(_pluginCalls[0].Hash);
        Assert.Equal("plugin-hash", _pluginCalls[1].Hash);
        Assert.All(_pluginCalls, call => Assert.True(call.Enabled));
    }

    [Fact]
    public void PluginSwitch_WhenTheEngineNeedsNoApproval_TurnsOnWithoutADialog()
    {
        _pluginNeedsApproval = false;
        _plugins = [Plugin(enabled: false)];
        var dialogs = Render<AppDialogHost>();
        var cut = Render<PluginSettingsTab>();
        cut.WaitForAssertion(() => Assert.Single(cut.FindAll("[data-state-card=plugin]")));

        cut.Find("[data-state-card=plugin] input[role=switch]").Change(true);

        cut.WaitForAssertion(() => Assert.NotEqual("off", cut.Find("[data-state-card=plugin]").GetAttribute("data-health")));
        Assert.Single(_pluginCalls);
        Assert.DoesNotContain("Turn on Alpha?", dialogs.Markup);
    }

    [Fact]
    public void PluginSwitchOff_NeedsNoApproval()
    {
        _plugins = [Plugin(enabled: true)];
        var dialogs = Render<AppDialogHost>();
        var cut = Render<PluginSettingsTab>();
        cut.WaitForAssertion(() => Assert.Single(cut.FindAll("[data-state-card=plugin]")));

        cut.Find("[data-state-card=plugin] input[role=switch]").Change(false);

        cut.WaitForAssertion(() => Assert.Equal("off", cut.Find("[data-state-card=plugin]").GetAttribute("data-health")));
        Assert.Single(_pluginCalls);
        Assert.DoesNotContain("approval-dialog", dialogs.Markup);
    }

    [Fact]
    public void PluginCard_ShowsTheEnginesWordsNotAMapOfItsOwn()
    {
        _plugins = [Plugin(enabled: false, allowedTo: ["Needs FFmpeg", "Engine-written line"], sendsDataTo: null)];

        var cut = Render<PluginSettingsTab>();

        cut.WaitForAssertion(() => Assert.Single(cut.FindAll("[data-state-card=plugin]")));
        var card = cut.Find("[data-state-card=plugin]");
        // The permission lines left the tile (v4 D30); they are on the plugin's own page.
        Assert.DoesNotContain("Needs FFmpeg", card.TextContent, StringComparison.Ordinal);
        Assert.DoesNotContain("Reads your media files", card.TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public void PluginDetails_ShowWhoTurnedItOnAndWhen()
    {
        _plugins = [Plugin(enabled: true)];
        _approvals =
        [
            new ApprovalRecordResponse
            {
                ApprovedByName = "Avery",
                ApprovedAt = new DateTimeOffset(2026, 10, 19, 9, 30, 0, TimeSpan.Zero),
                StatementHash = "plugin-hash",
            },
        ];

        var cut = Render<PluginSettingsTab>(parameters => parameters.Add(page => page.DetailPluginId, "a.plugin"));

        cut.WaitForAssertion(() =>
        {
            var line = cut.Find("[data-testid=plugin-approval]").TextContent;
            Assert.Contains("Turned on by Avery on", line, StringComparison.Ordinal);
            Assert.Contains("2026", line, StringComparison.Ordinal);
        });
    }

    // ── Local AI ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task LocalAiStatus_PointsAtThePluginsPage_AndHasNoFeatureListOrSwitchesOfItsOwn()
    {
        var cut = await RenderLocalAiAsync();

        var text = cut.Markup;
        Assert.Contains("AI features are plugins", text, StringComparison.Ordinal);
        Assert.Contains(cut.FindAll("a"), link => link.GetAttribute("href") == "/settings/plugins");
        Assert.DoesNotContain("Save features", text, StringComparison.Ordinal);
        Assert.Empty(cut.FindAll("input[role=switch]"));
        Assert.Empty(cut.FindAll(".local-ai-settings__summary-card"));
    }

    [Fact]
    public void LocalAiUsage_ListsPluginsAgainstTheirAllowance_WithALinkToTheirPluginCard()
    {
        _usage = new AiUsageResponse
        {
            Days = 7,
            Since = DateTimeOffset.UtcNow.AddDays(-7),
            Plugins =
            [
                new AiPluginBudgetStandingDto { PluginId = "tuvima.ai-vibe-tags", PluginName = "Vibe tags", RequestsThisHour = 130, RequestsPerHour = 120, OverBudget = true },
                new AiPluginBudgetStandingDto { PluginId = "tuvima.ai-smart-labelling", PluginName = "Smart labelling", RequestsThisHour = 3, RequestsPerHour = 120 },
            ],
            Rows =
            [
                new AiUsageRowDto { CallerKind = "plugin", CallerId = "tuvima.ai-vibe-tags", CallerName = "Vibe tags", Ability = "Classify", Requests = 12, Succeeded = 12, BusySeconds = 4.5, LastUsedAt = DateTimeOffset.UtcNow },
            ],
        };

        // Status lists the plugins with this hour's counts and no table.
        var status = Render<LocalAiSettingsTab>(parameters => parameters.Add(p => p.Subsection, "status"));

        status.WaitForAssertion(() => Assert.Contains("Vibe tags", status.Markup, StringComparison.Ordinal));
        Assert.Contains("Plugins using Local AI", status.Markup, StringComparison.Ordinal);
        Assert.Contains("Smart labelling", status.Markup, StringComparison.Ordinal);
        Assert.Contains("130 of 120 requests this hour", status.Markup, StringComparison.Ordinal);
        Assert.Contains("Needs attention", status.Markup, StringComparison.Ordinal);
        Assert.Contains(status.FindAll("a"), link => link.GetAttribute("href") == "/settings/plugins?plugin=tuvima.ai-vibe-tags");
        Assert.Empty(status.FindAll("table"));

        // Advanced holds the detailed usage table and not the plugin list.
        var advanced = Render<LocalAiSettingsTab>(parameters => parameters.Add(p => p.Subsection, "advanced"));

        advanced.WaitForAssertion(() => Assert.Contains("Classify", advanced.Find("table").TextContent, StringComparison.Ordinal));
        Assert.DoesNotContain("Plugins using Local AI", advanced.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void LocalAiBudget_ShowsTheDefaultPluginAllowance_AndSavesWhatTheAdministratorChanges()
    {
        var cut = Render<LocalAiSettingsTab>(parameters => parameters.Add(p => p.Subsection, "advanced"));

        cut.WaitForAssertion(() => Assert.Contains("Plugin allowance", cut.Markup, StringComparison.Ordinal));
        Assert.Contains("Requests per hour", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("Busy seconds per hour", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("Longest wait", cut.Markup, StringComparison.Ordinal);
        Assert.Contains(cut.FindAll("button"), button => button.TextContent.Trim() == "Save allowance");
    }

    [Fact]
    public void APluginThatNeedsApprovalAgain_ShowsNeedsAttentionWithTheReason_AndTurnOnAsksForApproval()
    {
        _plugins = [Plugin(enabled: false) with { NeedsApproval = true }];
        var dialogs = Render<AppDialogHost>();
        var cut = Render<PluginSettingsTab>();
        cut.WaitForAssertion(() => Assert.Single(cut.FindAll("[data-state-card=plugin]")));

        var card = cut.Find("[data-state-card=plugin]");
        Assert.Equal("needsattention", card.GetAttribute("data-health"));
        Assert.Contains("Needs approval: its permissions changed", card.InnerHtml, StringComparison.Ordinal);
        Assert.False(cut.Find("[data-state-card=plugin] input[role=switch]").HasAttribute("checked"));

        cut.Find("[data-state-card=plugin] input[role=switch]").Change(true);

        dialogs.WaitForAssertion(() => Assert.Contains("Turn on Alpha?", dialogs.Markup));
    }

    // ── The dialog itself ────────────────────────────────────────────────────

    [Theory]
    [InlineData("plugin", "Turn on")]
    [InlineData("application_credential", "Issue key")]
    [InlineData("webhook", "Save")]
    [InlineData("application", "Allow")]
    public void TheDialog_ShowsTheEnginesWordsAndTheRightButtonForEachKind(string kind, string confirm)
    {
        var preview = new ApprovalPreviewResponse
        {
            Kind = kind,
            ItemId = "x",
            Title = "Thing",
            CanDo = ["First ability", "Second ability"],
            SendsDataOut = false,
            Notes = ["A note"],
            StatementHash = "h",
        };

        var cut = Render<ApprovalDialog>(parameters => parameters
            .Add(dialog => dialog.Preview, preview)
            .AddCascadingValue<MediaEngine.Web.Services.Ui.IAppDialogContext>(new NoOpDialogContext()));

        Assert.Equal("Thing", cut.Find("[data-testid=approval-title]").TextContent);
        Assert.Equal(["First ability", "Second ability"], cut.FindAll("[data-testid=approval-can-do] li").Select(li => li.TextContent).ToArray());
        Assert.Contains("No", cut.Find("[data-testid=approval-sends-data]").TextContent, StringComparison.Ordinal);
        Assert.Equal("A note", cut.Find("[data-testid=approval-notes] li").TextContent);
        Assert.Equal(["Cancel", confirm], cut.FindAll("footer button").Select(button => button.TextContent.Trim()).ToArray());
    }

    [Fact]
    public void TheDialog_SaysWhereDataGoes_WhenItLeavesTheMachine()
    {
        var preview = new ApprovalPreviewResponse { Kind = "webhook", Title = "T", SendsDataOut = true, SendsDataTo = "hooks.example.com", StatementHash = "h" };

        var cut = Render<ApprovalDialog>(parameters => parameters
            .Add(dialog => dialog.Preview, preview)
            .AddCascadingValue<MediaEngine.Web.Services.Ui.IAppDialogContext>(new NoOpDialogContext()));

        Assert.Contains("Yes, to hooks.example.com", cut.Find("[data-testid=approval-sends-data]").TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public void TheDialog_OnAPhone_IsPresentedAsASheet()
    {
        var options = typeof(MediaEngine.Web.Services.Settings.ApprovalFlow)
            .GetField("DialogOptions", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(null) as MediaEngine.Web.Services.Ui.AppDialogOptions;

        Assert.Equal(AppDialogSheetMode.Phone, options!.Sheet);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private async Task<IRenderedComponent<LocalAiSettingsTab>> RenderLocalAiAsync()
    {
        await Services.GetRequiredService<SettingsAttentionState>().StartAsync();
        var cut = Render<LocalAiSettingsTab>();
        cut.WaitForAssertion(() => Assert.Contains("AI features are plugins", cut.Markup));
        return cut;
    }

    private static PluginSummaryResponse AiPlugin(string id, string name, bool enabled) =>
        Plugin(enabled) with { Id = id, Name = name, Description = $"{name} runs on this machine." };

    private static ApprovalPreviewResponse Preview(string kind, string itemId, string title, string hash) => new()
    {
        Kind = kind,
        ItemId = itemId,
        Title = title,
        KindLabel = kind == "plugin" ? "Plugin" : "Local AI feature",
        CanDo = ["Reads your media files"],
        SendsDataOut = kind == "plugin",
        SendsDataTo = kind == "plugin" ? "the internet" : null,
        Notes = kind == "plugin" ? ["Plugins run inside the server without a sandbox. Only turn on plugins you trust."] : [],
        StatementHash = hash,
    };

    private static PluginSummaryResponse Plugin(bool enabled, List<string>? allowedTo = null, string? sendsDataTo = "the internet") => new()
    {
        Id = "a.plugin",
        Name = "Alpha",
        Description = "Alpha does something useful.",
        Enabled = enabled,
        TrustLevel = "official",
        Permissions = ["media.read", "network.http"],
        AllowedTo = allowedTo ?? ["Reads your media files", "Connects to the internet"],
        SendsDataTo = sendsDataTo,
    };

    private void SignInAdministrator()
    {
        AccountProfileGrantDto[] grants =
        [
            new(AccountId, ProfileId, "Test User", null, true, true, true,
                new ProfileAdminProtectionResponse(false, "None", null, 1), 1, DateTimeOffset.UtcNow),
        ];
        _session.Set("token", AccountId, ProfileId, Guid.NewGuid(),
            new DashboardAuthorityResponse(AccountId, ProfileId, true, true, 1, 1, true, true, null, 1, grants, ["settings.administration"], []));
    }

    private sealed class NoOpDialogContext : MediaEngine.Web.Services.Ui.IAppDialogContext
    {
        public Guid Id { get; } = Guid.NewGuid();

        public MediaEngine.Web.Services.Ui.AppDialogOptions Options { get; } = new();

        public string? PresentationClass => null;

        public void Close(MediaEngine.Web.Services.Ui.AppDialogResult? result = null)
        {
        }

        public void Cancel()
        {
        }

        public Task CloseAsync(MediaEngine.Web.Services.Ui.AppDialogResult? result = null) => Task.CompletedTask;

        public Task CancelAsync() => Task.CompletedTask;

        public Task SetOptionsAsync(MediaEngine.Web.Services.Ui.AppDialogOptions options) => Task.CompletedTask;

        public Task SetPresentationClassAsync(string? value) => Task.CompletedTask;

        public void SetCloseGuard(Func<Task<bool>>? guard)
        {
        }

        public IDisposable RegisterCloseGuard(Func<Task<bool>> guard) => new NoOpDisposable();

        private sealed class NoOpDisposable : IDisposable
        {
            public void Dispose()
            {
            }
        }
    }

    /// <summary>The active person comes from the Dashboard session; the cookie principal adds nothing.</summary>
    private sealed class SessionOnlyAuthentication : Microsoft.AspNetCore.Components.Authorization.AuthenticationStateProvider
    {
        public override Task<Microsoft.AspNetCore.Components.Authorization.AuthenticationState> GetAuthenticationStateAsync() =>
            Task.FromResult(new Microsoft.AspNetCore.Components.Authorization.AuthenticationState(
                new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity())));
    }
}
