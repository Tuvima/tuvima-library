using System.Text.Json;
using Bunit;
using MediaEngine.Contracts.Plugins;
using MediaEngine.Web.Components.Settings;
using MediaEngine.Web.Components.Shared;
using MediaEngine.Web.Services.Integration;
using MediaEngine.Web.Tests.Support;
using Microsoft.Extensions.DependencyInjection;

namespace MediaEngine.Web.Tests;

/// <summary>
/// The one generator that draws every plugin page from the Engine's page model (Plugins and Integrations screens v5 D46 to D52, D59):
/// each control type, repeating groups with their row actions, the highlighted card, a plugin that declares no test, and the activity
/// timeline. No test names a real plugin; the page model is the whole input.
/// </summary>
public sealed class PluginPageGeneratorTests : AsyncBunitContext
{
    private readonly List<string> _removed = [];
    private readonly List<string> _tested = [];
    private readonly List<string> _keysSaved = [];
    private PluginSetupPageResponse? _page;

    public PluginPageGeneratorTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddLogging();
        Services.AddLocalization();
        var api = EngineApiClientStub.Create(stub =>
        {
            stub.SetHandler(nameof(IEngineApiClient.GetPluginSetupPageAsync), _ => Task.FromResult(_page));
            stub.SetHandler(nameof(IEngineApiClient.RemovePluginSetupItemAsync), args =>
            {
                _removed.Add((string)args![2]!);
                return Task.FromResult<PluginSetupChangeResult?>(new PluginSetupChangeResult { Ok = true, Message = "Removed." });
            });
            stub.SetHandler(nameof(IEngineApiClient.TestPluginSetupItemAsync), args =>
            {
                _tested.Add((string)args![2]!);
                return Task.FromResult<PluginSetupChangeResult?>(new PluginSetupChangeResult { Ok = true, Message = "Connected." });
            });
            stub.SetHandler(nameof(IEngineApiClient.SavePluginSetupItemSecretAsync), args =>
            {
                _keysSaved.Add((string)args![2]!);
                return Task.FromResult<PluginSetupChangeResult?>(new PluginSetupChangeResult { Ok = true, Message = "Key saved." });
            });
        });
        Services.AddSingleton(api);
    }

    // ------------------------------------------------------------------------------------------------ Settings controls

    [Fact]
    public void EveryControlType_DrawsItsOwnField()
    {
        var card = SettingsCard(
            Control("toggle", "switch_on", "Switch on", value: true),
            Control("choice", "mode", "Mode", value: "fast", choices: [("fast", "Fast"), ("slow", "Slow")]),
            Control("number", "retries", "Retries", value: 3, integer: true, minimum: 1, maximum: 10),
            Control("number", "limit", "Limit", value: 500, integer: true, minimum: 1, maximum: 5000),
            Control("number", "ratio", "Ratio", value: 0.5),
            Control("choices", "kinds", "Kinds", value: new[] { "a" }, choices: [("a", "Alpha"), ("b", "Beta")]),
            Control("url", "address", "Address", value: "https://example.test"),
            Control("path", "folder", "Folder", value: "/media"),
            Control("text", "label", "Label", value: "Hello"),
            Control("secret", "token", "Token", scope: "profile"));

        var cut = RenderCard(card);

        foreach (var type in new[] { "toggle", "choice", "number", "choices", "url", "path", "text", "secret" })
        {
            Assert.NotEmpty(cut.FindAll($"[data-control={type}]"));
        }

        Assert.NotNull(cut.Find("[data-setting=switch_on] input[role=switch]"));
        Assert.NotNull(cut.Find("[data-setting=retries] input[type=range]"));
        Assert.Empty(cut.FindAll("[data-setting=limit] input[type=range]"));
        Assert.Equal(2, cut.FindAll("[data-setting=kinds] input[type=checkbox]").Count);
        Assert.Equal("url", cut.Find("[data-setting=address] input").GetAttribute("type"));
        Assert.Contains("Open Accounts", cut.Find("[data-setting=token]").TextContent);
    }

    [Fact]
    public void GroupTitles_AreShownAndAdvancedControlsStayCollapsed()
    {
        var advanced = Control("number", "timeout", "Timeout", value: 30, integer: true);
        advanced.Advanced = true;
        var card = SettingsCard(Control("toggle", "on", "On", value: true, group: "Lookups"), advanced);

        var cut = RenderCard(card);

        Assert.Equal("Lookups", cut.Find("[data-testid=plugin-settings-group] h4").TextContent);
        Assert.Empty(cut.FindAll("[data-setting=timeout]"));
        Assert.Contains("Advanced settings", cut.Markup);
    }

    [Fact]
    public void AControlWithShowWhen_AppearsOnlyWhileTheOtherSettingHasThatValue()
    {
        var dependent = Control("text", "details", "Details", value: "x");
        dependent.ShowWhen = new PluginSetupShowWhenDto { Setting = "switch_on", Value = JsonSerializer.SerializeToElement(true) };
        var cut = RenderCard(SettingsCard(Control("toggle", "switch_on", "Switch on", value: false), dependent));

        Assert.Empty(cut.FindAll("[data-setting=details]"));

        cut.Find("[data-setting=switch_on] input[role=switch]").Change(true);

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-setting=details]")));
    }

    [Fact]
    public void AReadOnlyControl_AndOneThatNeedsAMissingTool_AreGreyedWithTheReason()
    {
        var needsTool = Control("toggle", "burn", "Burn in", value: false);
        needsTool.Requires = new PluginSetupRequirementDto { Kind = "tool", Name = "ffmpeg", Reason = "Needs ffmpeg, which is not installed." };
        var locked = Control("text", "fixed", "Fixed", value: "x");
        locked.ReadOnly = true;
        var card = SettingsCard(needsTool, locked);

        var cut = RenderCard(card, new PluginSetupContext(ToolMissing: true, LocalAiBlocked: false, KeyMissing: false));

        Assert.Contains("Needs ffmpeg, which is not installed.", cut.Find("[data-setting=burn]").TextContent);
        Assert.Contains("plugin-setting--disabled", cut.Find("[data-setting=burn]").ClassName);
        Assert.Contains("plugin-setting--disabled", cut.Find("[data-setting=fixed]").ClassName);
    }

    [Fact]
    public void SaveChanges_StaysOffUntilSomethingIsChanged()
    {
        var cut = RenderCard(SettingsCard(Control("toggle", "switch_on", "Switch on", value: false)));
        Assert.True(cut.Find("[data-testid=plugin-settings-save]").HasAttribute("disabled"));

        cut.Find("[data-setting=switch_on] input[role=switch]").Change(true);

        cut.WaitForAssertion(() => Assert.False(cut.Find("[data-testid=plugin-settings-save]").HasAttribute("disabled")));
    }

    // ------------------------------------------------------------------------------------------------ Group lists

    [Fact]
    public async Task AGroupList_DrawsItsRows_AndALockedRowHasALockAndCannotBeRemoved()
    {
        var cut = RenderCard(SettingsCard(Servers()));

        Assert.Equal(3, cut.FindAll("[data-testid=plugin-group-row]").Count);
        var locked = cut.Find("[data-item=built-in]");
        Assert.NotEmpty(locked.QuerySelectorAll("[data-testid=plugin-group-lock]"));
        Assert.Empty(cut.Find("[data-item=mine]").QuerySelectorAll("[data-testid=plugin-group-lock]"));
        Assert.Contains("Add server", cut.Find("[data-testid=plugin-group-add]").TextContent);

        await cut.ClickAsync("[data-item=built-in] .app-row-menu__trigger");
        Assert.DoesNotContain(cut.FindAll("[role=menuitem]"), item => item.TextContent.Contains("Remove", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ARowWithoutAKey_OffersAddKey_AndTheConnectDialogTestsBeforeItSaves()
    {
        var dialogs = Render<AppDialogHost>();
        var cut = RenderCard(SettingsCard(Servers()));

        var row = cut.Find("[data-item=nokey]");
        Assert.NotNull(row.QuerySelector("[data-testid=plugin-group-key-missing]"));
        var addKey = row.QuerySelector("[data-testid=plugin-group-add-key]")!;
        Assert.Contains("Add key", addKey.TextContent);
        Assert.True(row.QuerySelector("[data-testid=plugin-group-test]")!.HasAttribute("disabled"));

        await cut.ClickAsync("[data-item=nokey] [data-testid=plugin-group-add-key]");
        dialogs.WaitForElement("input[type=password]").Input("secret-value");
        Assert.True(dialogs.Find("[data-testid=plugin-connect-save]").HasAttribute("disabled"));
        await dialogs.ClickAsync("[data-testid=plugin-connect-test]");
        dialogs.WaitForAssertion(() => Assert.False(dialogs.Find("[data-testid=plugin-connect-save]").HasAttribute("disabled")));
        await dialogs.ClickAsync("[data-testid=plugin-connect-save]");

        cut.WaitForAssertion(() => Assert.Equal(["nokey"], _keysSaved));
    }

    [Fact]
    public async Task RowActions_TestAndRemoveGoToTheEnginesGenericItemActions()
    {
        var dialogs = Render<AppDialogHost>();
        var cut = RenderCard(SettingsCard(Servers()));

        cut.Find("[data-item=mine] [data-testid=plugin-group-test]").Click();
        cut.WaitForAssertion(() => Assert.Contains("Connected.", cut.Find("[data-testid=plugin-group-message]").TextContent));
        await cut.ClickAsync("[data-item=mine] .app-row-menu__trigger");
        cut.FindAll("[role=menuitem]").Single(item => item.TextContent.Trim() == "Remove").Click();
        dialogs.WaitForElement(".app-confirm");
        dialogs.FindAll(".app-dialog-shell__footer button").Single(button => button.TextContent.Trim() == "Remove").Click();

        cut.WaitForAssertion(() => Assert.Equal(["mine"], _removed));
        Assert.Equal(["mine"], _tested);
    }

    // ------------------------------------------------------------------------------------------------ The page

    [Fact]
    public void TheCardThePageIsOpenedFor_IsHighlightedOnlyWhenAsked()
    {
        _page = Page(new PluginSetupCardDto { Key = "access", Title = "Access", NeedsAttention = true }, new PluginSetupCardDto { Key = "privacy", Title = "Privacy" });

        var asked = RenderPage(attention: true);
        asked.WaitForAssertion(() => Assert.Equal(2, asked.FindAll("[data-card]").Count));
        Assert.Contains("plugin-card--attention", asked.Find("#plugin-setup-card-access").ClassName);
        Assert.Equal("true", asked.Find("#plugin-setup-card-access").GetAttribute("data-highlighted"));
        Assert.DoesNotContain("plugin-card--attention", asked.Find("#plugin-setup-card-privacy").ClassName);

        var plain = RenderPage(attention: false);
        plain.WaitForAssertion(() => Assert.Equal(2, plain.FindAll("[data-card]").Count));
        Assert.DoesNotContain("plugin-card--attention", plain.Find("#plugin-setup-card-access").ClassName);
    }

    [Fact]
    public void APluginThatDeclaresNoTest_ShowsNoTestButton()
    {
        _page = Page(new PluginSetupCardDto { Key = "access", Title = "Access" });

        var cut = RenderPage();

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-card=access]")));
        Assert.Empty(cut.FindAll("[data-testid=plugin-tests]"));
        Assert.DoesNotContain(cut.FindAll("button"), button => button.TextContent.Contains("Test connection", StringComparison.Ordinal));
    }

    [Fact]
    public void APluginThatDeclaresATest_ShowsExactlyThatButton()
    {
        var access = new PluginSetupCardDto { Key = "access", Title = "Access" };
        access.Actions.Add(new PluginSetupActionDto { Kind = "test_connection", Label = "Test connection" });
        _page = Page(access);

        var cut = RenderPage();

        cut.WaitForAssertion(() => Assert.Single(cut.FindAll("[data-testid=plugin-test-test_connection]")));
        Assert.Single(cut.FindAll("[data-testid=plugin-tests] button"));
    }

    [Fact]
    public void AnEngineThatDoesNotAnswer_ShowsTheUnavailableStateInsteadOfAPage()
    {
        _page = null;

        var cut = RenderPage();

        cut.WaitForAssertion(() => Assert.Contains("This plugin's page is unavailable", cut.Markup));
        Assert.Empty(cut.FindAll("[data-card]"));
    }

    [Fact]
    public void ThePrivacyCard_SaysWhatLeavesTheMachineInTheEnginesWords()
    {
        _page = Page(new PluginSetupCardDto { Key = "privacy", Title = "Privacy", DataLeaving = "The song title goes to a lyrics site." });

        var cut = RenderPage();

        cut.WaitForAssertion(() => Assert.Contains("The song title goes to a lyrics site.", cut.Find("[data-testid=plugin-leaves]").TextContent));
    }

    // ------------------------------------------------------------------------------------------------ Connection card and shared frame (v6 D60, D61, D69)

    [Fact]
    public void TheConnectionCard_SaysNotConnected_AndOffersConnect_WhenNoKeyIsSet()
    {
        _page = Page(AccessCard(configured: false));

        var cut = RenderPage();

        cut.WaitForAssertion(() => Assert.Equal("notconnected", cut.Find("[data-testid=plugin-credential]").GetAttribute("data-state")));
        Assert.Equal("Not connected", cut.Find("[data-testid=plugin-connection-state]").TextContent);
        Assert.NotEmpty(cut.FindAll("[data-testid=plugin-connect]"));
    }

    [Fact]
    public void TheConnectionCard_SaysConnected_WithNoConnectButton_WhenTheKeyIsSet()
    {
        _page = Page(AccessCard(configured: true));

        var cut = RenderPage();

        cut.WaitForAssertion(() => Assert.Equal("connected", cut.Find("[data-testid=plugin-credential]").GetAttribute("data-state")));
        Assert.Equal("Connected", cut.Find("[data-testid=plugin-connection-state]").TextContent);
        Assert.Empty(cut.FindAll("[data-testid=plugin-connect]"));
    }

    [Fact]
    public void ThePluginPageAndTheIntegrationPage_BothUseTheSharedFrame_AndNeitherDrawsAnAboutCard()
    {
        var root = AppContext.BaseDirectory;
        while (root is not null && !File.Exists(Path.Combine(root, "MediaEngine.slnx")))
        {
            root = Path.GetDirectoryName(root);
        }

        Assert.NotNull(root);
        foreach (var file in new[] { "PluginSettingsTab.razor", "ManagedAccessApplications.razor" })
        {
            var source = File.ReadAllText(Path.Combine(root!, "src", "MediaEngine.Web", "Components", "Settings", file));
            Assert.Contains("<AppDetailPageFrame", source, StringComparison.Ordinal);
            Assert.DoesNotContain("plugin-about-card", source, StringComparison.Ordinal);
        }
    }

    private static PluginSetupCardDto AccessCard(bool configured) => new()
    {
        Key = "access",
        Title = "Access",
        Credentials = [new PluginSetupCredentialDto { Name = "api_key", Label = "API key", Configured = configured }],
    };

    // ------------------------------------------------------------------------------------------------ Activity

    [Fact]
    public void TheActivityTimeline_SaysNothingHasGoneWrong_WhenThereIsNothingToShow()
    {
        var cut = Render<PluginActivityTimeline>(parameters => parameters.Add(timeline => timeline.Activity, new PluginActivityResponse()));

        Assert.Contains("Nothing has gone wrong.", cut.Find("[data-testid=plugin-activity-empty]").TextContent);
        Assert.Empty(cut.FindAll("[data-testid=plugin-activity-routine]"));
    }

    [Fact]
    public void TheActivityTimeline_ListsFailuresFirstNewestFirst_AndFoldsRoutineEntriesAway()
    {
        var now = DateTimeOffset.UtcNow;
        var activity = new PluginActivityResponse
        {
            Entries =
            [
                Entry(1, "run", "Ran fine", now.AddMinutes(-1)),
                Entry(2, "failure", "Older failure", now.AddHours(-3)),
                Entry(3, "failure", "Newest failure", now.AddMinutes(-5)),
            ],
        };

        var cut = Render<PluginActivityTimeline>(parameters => parameters.Add(timeline => timeline.Activity, activity));

        var failures = cut.FindAll("[data-testid=plugin-activity-failures] li").Select(item => item.QuerySelector("strong")!.TextContent).ToList();
        Assert.Equal(["Newest failure", "Older failure"], failures);
        Assert.Empty(cut.FindAll("[data-testid=plugin-activity-empty]"));
        Assert.Contains("1 routine entry", cut.Markup);
    }

    // ------------------------------------------------------------------------------------------------ Helpers

    private IRenderedComponent<PluginSettingsCard> RenderCard(PluginSetupCardDto card, PluginSetupContext? context = null) =>
        Render<PluginSettingsCard>(parameters => parameters
            .Add(component => component.PluginId, "example")
            .Add(component => component.Card, card)
            .Add(component => component.Enabled, true)
            .Add(component => component.Context, context ?? new PluginSetupContext(false, false, false)));

    private IRenderedComponent<PluginSetupPage> RenderPage(bool attention = false) =>
        Render<PluginSetupPage>(parameters => parameters
            .Add(component => component.Plugin, new PluginSummaryResponse { Id = "example", Name = "Example", Enabled = true })
            .Add(component => component.Attention, attention));

    private static PluginSetupPageResponse Page(params PluginSetupCardDto[] cards) =>
        new() { PluginId = "example", PluginName = "Example", Cards = [.. cards] };

    private static PluginSetupCardDto SettingsCard(params PluginSetupControlDto[] controls) =>
        new() { Key = "settings", Title = "Settings", Controls = [.. controls] };

    private static PluginActivityEntryDto Entry(long id, string kind, string summary, DateTimeOffset at) =>
        new() { Id = id, Kind = kind, Summary = summary, OccurredAt = at };

    private static PluginSetupControlDto Control(
        string type,
        string key,
        string title,
        object? value = null,
        bool integer = false,
        double? minimum = null,
        double? maximum = null,
        string scope = "household",
        string group = "",
        (string Value, string Label)[]? choices = null) => new()
        {
            Control = type,
            Key = key,
            Title = title,
            Group = group,
            Integer = integer,
            Minimum = minimum,
            Maximum = maximum,
            Scope = scope,
            Value = value is null ? null : JsonSerializer.SerializeToElement(value),
            Choices = [.. (choices ?? []).Select(choice => new PluginSetupChoiceDto { Value = choice.Value, Label = choice.Label })],
        };

    private static PluginSetupControlDto Servers()
    {
        var control = Control("group_list", "servers", "Servers");
        control.GroupList = new PluginSetupGroupListDto
        {
            ItemTitle = "Server",
            AddLabel = "Add server",
            RowActions = ["test_connection", "remove"],
            Fields =
            [
                Control("text", "name", "Name"),
                Control("secret", "api_key", "Key"),
            ],
            Items =
            [
                new PluginSetupGroupItemDto { Id = "built-in", Title = "Built in", State = "ok", Locked = true },
                new PluginSetupGroupItemDto { Id = "mine", Title = "Mine", State = "ok" },
                new PluginSetupGroupItemDto { Id = "nokey", Title = "No key yet", KeyMissing = true },
            ],
        };
        return control;
    }
}
