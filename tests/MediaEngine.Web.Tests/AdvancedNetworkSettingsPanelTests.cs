using Bunit;
using MediaEngine.Contracts.Settings;
using MediaEngine.Web.Components.Settings;
using MediaEngine.Web.Services.Integration;
using MediaEngine.Web.Services.Ui;
using MediaEngine.Web.Tests.Support;
using Microsoft.Extensions.DependencyInjection;

namespace MediaEngine.Web.Tests;

/// <summary>
/// Settings > Network > Advanced: a change that worked shows itself on the control, and one that did not is told once, in place,
/// as an alert (design system, Feedback after an action) and never as a pop-up notice.
/// </summary>
public sealed class AdvancedNetworkSettingsPanelTests : AsyncBunitContext
{
    private NetworkSettingsDto? _resetReplacement;
    private int _resets;

    public AdvancedNetworkSettingsPanelTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddLogging();
        Services.AddLocalization();
        Services.AddSingleton(EngineApiClientStub.Create(stub =>
        {
            stub.SetHandler(nameof(IEngineApiClient.ResetNetworkSettingsAsync), _ =>
            {
                _resets++;
                return Task.FromResult(_resetReplacement);
            });
        }));
    }

    [Fact]
    public void ReverseProxySave_AsksTheOwnerToSave_AndShowsNoNotice()
    {
        var saves = 0;
        var cut = Render<AdvancedNetworkSettingsPanel>(parameters => parameters
            .Add(panel => panel.Settings, new NetworkSettingsDto())
            .Add(panel => panel.SaveRequested, () => { saves++; }));

        cut.FindAll("button").First(button => button.TextContent.Trim() == "Save proxy trust").Click();

        Assert.Equal(1, saves);
        Assert.Empty(cut.FindAll("[data-network-failure]"));
        Assert.Empty(cut.FindAll("[data-reset-failure]"));
        Assert.Empty(Services.GetRequiredService<AppToastService>().Toasts);
    }

    [Fact]
    public void DirectSettingsSave_WithABadPort_ShowsAWarningInPlace_AndDoesNotSave()
    {
        var saves = 0;
        var cut = Render<AdvancedNetworkSettingsPanel>(parameters => parameters
            .Add(panel => panel.Settings, new NetworkSettingsDto())
            .Add(panel => panel.SaveRequested, () => { saves++; }));
        cut.FindAll("input[type='text'], input:not([type])")
            .First(input => input.GetAttribute("value") == "443")
            .Input("99999");

        cut.FindAll("button").First(button => button.TextContent.Trim() == "Save direct settings").Click();

        Assert.Equal(0, saves);
        Assert.NotEmpty(cut.Find("[data-network-failure]").TextContent.Trim());
        Assert.Empty(Services.GetRequiredService<AppToastService>().Toasts);

        // The next attempt with a good port clears the warning and saves.
        cut.FindAll("input[type='text'], input:not([type])")
            .First(input => input.GetAttribute("value") == "99999")
            .Input("443");
        cut.FindAll("button").First(button => button.TextContent.Trim() == "Save direct settings").Click();

        Assert.Equal(1, saves);
        Assert.Empty(cut.FindAll("[data-network-failure]"));
    }

    [Fact]
    public void Reset_WhenTheEngineRefuses_ShowsTheAlertInsideTheDialog_AndKeepsTheSettings()
    {
        var replaced = 0;
        var cut = Render<AdvancedNetworkSettingsPanel>(parameters => parameters
            .Add(panel => panel.Settings, new NetworkSettingsDto())
            .Add(panel => panel.SettingsReplaced, _ => { replaced++; }));

        OpenResetDialogAndConfirm(cut);

        cut.WaitForAssertion(() => Assert.Equal("alert", cut.Find("dialog [data-reset-failure]").GetAttribute("role")));
        Assert.Equal(1, _resets);
        Assert.Equal(0, replaced);
        Assert.Empty(Services.GetRequiredService<AppToastService>().Toasts);
    }

    [Fact]
    public void Reset_WhenItWorks_HandsTheNewSettingsToThePage_WithNoAlertAndNoNotice()
    {
        _resetReplacement = new NetworkSettingsDto();
        NetworkSettingsDto? received = null;
        var cut = Render<AdvancedNetworkSettingsPanel>(parameters => parameters
            .Add(panel => panel.Settings, new NetworkSettingsDto())
            .Add(panel => panel.SettingsReplaced, replacement => { received = replacement; }));

        OpenResetDialogAndConfirm(cut);

        cut.WaitForAssertion(() => Assert.Same(_resetReplacement, received));
        Assert.Empty(cut.FindAll("[data-reset-failure]"));
        Assert.Empty(Services.GetRequiredService<AppToastService>().Toasts);
    }

    // The row's button opens the dialog; the dialog's own button (the last one with that label) does the reset.
    private static void OpenResetDialogAndConfirm(IRenderedComponent<AdvancedNetworkSettingsPanel> cut)
    {
        cut.FindAll("button").First(button => button.TextContent.Trim() == "Reset Network Settings").Click();
        cut.FindAll("button").Last(button => button.TextContent.Trim() == "Reset Network Settings").Click();
    }
}
