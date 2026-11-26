using Bunit;
using MediaEngine.Contracts.System;
using MediaEngine.Web.Components.Pages;
using MediaEngine.Web.Services.Navigation;
using MediaEngine.Web.Services.Theming;
using MediaEngine.Web.Shared;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace MediaEngine.Web.Tests;

/// <summary>
/// The phone's back bar names the page below it (profile-and-household D16): "My profile", "Household" and a person's name, not "Settings".
/// A page sets its name through the <see cref="PhoneBarTitle"/> the shell hands it; the shell falls back to the area's name.
/// </summary>
public sealed partial class UiShellRenderTests
{
    private sealed class BarTitleSetter : ComponentBase
    {
        [CascadingParameter] public PhoneBarTitle? Bar { get; set; }

        [Parameter] public string? Name { get; set; }

        protected override void OnAfterRender(bool firstRender) => Bar?.Set(Name);
    }

    private async Task<IRenderedComponent<MainLayout>> RenderSettingsLayoutAsync(string? pageName)
    {
        var cut = Render<MainLayout>(parameters => parameters.Add(layout => layout.Body, builder =>
        {
            builder.OpenComponent<BarTitleSetter>(0);
            builder.AddAttribute(1, nameof(BarTitleSetter.Name), pageName);
            builder.CloseComponent();
        }));
        cut.WaitForAssertion(() => Assert.Single(cut.FindAll(".tl-appbar")));
        await cut.InvokeAsync(() => Services.GetRequiredService<DeviceContextService>().SwitchDeviceAsync(PhoneDevice));
        await cut.InvokeAsync(() => Services.GetRequiredService<NavigationManager>().NavigateTo("/settings/household"));
        return cut;
    }

    [Fact]
    public async Task MainLayout_OnAPhone_TitlesTheSettingsBackBarWithThePageNameTheCurrentPageSets()
    {
        var cut = await RenderSettingsLayoutAsync("Mia");

        cut.WaitForAssertion(() => Assert.Equal("Mia", cut.Find(".layout-shell__back-title").TextContent.Trim()));
    }

    [Fact]
    public async Task MainLayout_OnAPhone_TitlesTheSettingsBackBarSettingsWhenThePageSetsNoName()
    {
        var cut = await RenderSettingsLayoutAsync(null);

        cut.WaitForAssertion(() => Assert.Equal("Settings", cut.Find(".layout-shell__back-title").TextContent.Trim()));
    }

    [Theory]
    [InlineData("overview", "My profile")]
    [InlineData("household", "Household")]
    public async Task SettingsPage_OnAPhone_NamesItselfInTheBackBarAndLeavesOutThePageSelector(string section, string title)
    {
        if (section == "household")
        {
            SignInAsHouseholdAdministrator();
        }

        await Services.GetRequiredService<DeviceContextService>().SwitchDeviceAsync(PhoneDevice);
        var bar = new PhoneBarTitle();
        Services.GetRequiredService<NavigationManager>().NavigateTo($"/settings/{section}");

        var cut = Render<Settings>(parameters => parameters.Add(page => page.Section, section).Add(page => page.BarTitle, bar));

        cut.WaitForAssertion(() => Assert.Equal(title, bar.Text));
        Assert.Empty(cut.FindAll("#settings-mobile-page"));
    }

    [Fact]
    public async Task SettingsPage_OnAPhone_KeepsThePageSelectorOnOtherPages()
    {
        UseDevelopmentTools(new DevelopmentToolsResponse(EnrichmentTester: true, DevelopmentData: true));
        await Services.GetRequiredService<DeviceContextService>().SwitchDeviceAsync(PhoneDevice);
        var bar = new PhoneBarTitle();
        Services.GetRequiredService<NavigationManager>().NavigateTo("/settings/developer");

        var cut = Render<Settings>(parameters => parameters.Add(page => page.Section, "developer").Add(page => page.BarTitle, bar));

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("#settings-mobile-page")));
        Assert.Null(bar.Text);
    }

    [Fact]
    public void PhoneBarTitle_RaisesChangedOnlyWhenTheNameChanges_AndTreatsBlankAsNoName()
    {
        var bar = new PhoneBarTitle();
        var raised = 0;
        bar.Changed += () => raised++;

        bar.Set("Mia");
        bar.Set("Mia");
        bar.Set("  ");

        Assert.Equal(2, raised);
        Assert.Null(bar.Text);
    }
}
