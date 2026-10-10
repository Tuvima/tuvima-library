using Bunit;
using MediaEngine.Web.Components.Cinematic;
using MediaEngine.Web.Components.Pages;
using MediaEngine.Web.Components.Settings;
using MediaEngine.Web.Components.Shared;
using MediaEngine.Web.Models.ViewDTOs;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace MediaEngine.Web.Tests;

public sealed class SharedUiPrimitiveTests : AsyncBunitContext
{
    public SharedUiPrimitiveTests()
    {
        Services.AddNativeUiServices();
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    [Fact]
    public async Task PlaybackSelectAvoidsReattachingOnUnrelatedRenderAndDetachesWhenAppearanceChanges()
    {
        Render<AppPopoverHost>();
        var module = JSInterop.SetupModule("./js/app-select-playback.js");
        module.Mode = JSRuntimeMode.Loose;
        var cut = Render<AppSelect>(parameters => parameters
            .Add(component => component.Appearance, "playback-flat")
            .Add(component => component.AriaLabel, "Sleep timer")
            .Add(component => component.Value, "off")
            .Add(component => component.Options, new[] { new AppSelectOption("off", "Off") }));
        cut.WaitForAssertion(() => Assert.Single(module.Invocations, call => call.Identifier == "attach"));
        cut.Render(parameters => parameters.Add(component => component.HelpText, "Changed unrelated help text"));
        await cut.InvokeAsync(() => Task.CompletedTask);
        Assert.Single(module.Invocations, call => call.Identifier == "attach");
        cut.Render(parameters => parameters.Add(component => component.AriaLabel, "New sleep label"));
        cut.WaitForAssertion(() => Assert.Equal(2, module.Invocations.Count(call => call.Identifier == "attach")));
        cut.Render(parameters => parameters.Add(component => component.Appearance, "standard"));
        cut.WaitForAssertion(() => Assert.Single(module.Invocations, call => call.Identifier == "detach"));
    }

    [Fact]
    public void SurfaceTabs_ExposeOnlyTheCurrentTabAsSelected()
    {
        var cut = Render<SurfaceTabBar>(parameters => parameters
            .Add(component => component.Items, new List<SurfaceTabItem>
            {
                new("users", "Users"), new("authentication", "Authentication")
            })
            .Add(component => component.ActiveKey, "authentication"));
        Assert.Equal("Authentication", Assert.Single(cut.FindAll("[role=tab][aria-selected=true]")).TextContent.Trim());
        Assert.Equal("false", cut.FindAll("[role=tab]")[0].GetAttribute("aria-selected"));
        cut.Render(parameters => parameters.Add(component => component.ActiveKey, "users"));
        Assert.Equal("Users", Assert.Single(cut.FindAll("[role=tab][aria-selected=true]")).TextContent.Trim());
    }

    [Theory]
    [InlineData(AppPageStateKind.Loading, "Loading")]
    [InlineData(AppPageStateKind.Empty, "Nothing here")]
    [InlineData(AppPageStateKind.Error, "Could not load")]
    [InlineData(AppPageStateKind.Unavailable, "Unavailable")]
    public void AppPageState_RendersExpectedStateClass(AppPageStateKind kind, string title)
    {
        var cut = Render<AppPageState>(parameters => parameters
            .Add(component => component.Kind, kind)
            .Add(component => component.Title, title)
            .Add(component => component.Message, "State message"));

        Assert.Single(cut.FindAll($".app-page-state--{kind.ToString().ToLowerInvariant()}"));
        Assert.Contains(title, cut.Markup);
        Assert.Contains("State message", cut.Markup);
        Assert.Equal(kind == AppPageStateKind.Error ? "alert" : "status", cut.Find(".app-page-state").GetAttribute("role"));
    }

    [Theory]
    [InlineData(AppUiTone.Neutral, "app-status-badge--neutral")]
    [InlineData(AppUiTone.Success, "app-status-badge--success")]
    [InlineData(AppUiTone.Warning, "app-status-badge--warning")]
    [InlineData(AppUiTone.Error, "app-status-badge--error")]
    public void AppStatusBadge_MapsToneToClass(AppUiTone tone, string expectedClass)
    {
        var cut = Render<AppStatusBadge>(parameters => parameters
            .Add(component => component.Text, "Status")
            .Add(component => component.Tone, tone));

        Assert.Single(cut.FindAll($".{expectedClass}"));
        Assert.Single(cut.FindAll(".app-chip"));
        Assert.Single(cut.FindAll($".app-tone--{tone.ToString().ToLowerInvariant()}"));
        Assert.Contains("Status", cut.Markup);
    }

    [Fact]
    public void AppPanel_CapturesElevationAndLiveRegionAttributesTogether()
    {
        var cut = Render(builder =>
        {
            builder.OpenComponent<AppPanel>(0);
            builder.AddAttribute(1, "Elevation", 0);
            builder.AddAttribute(2, "aria-live", "polite");
            builder.AddAttribute(3, nameof(AppPanel.ChildContent), (RenderFragment)(contentBuilder =>
                contentBuilder.AddContent(0, "Current activity")));
            builder.CloseComponent();
        });

        var panel = cut.Find("section");
        Assert.Equal("0", panel.GetAttribute("Elevation"));
        Assert.Equal("polite", panel.GetAttribute("aria-live"));
        Assert.Contains("Current activity", panel.TextContent);
    }

    [Fact]
    public void AppCheckbox_UsesSharedToneAndSupportsTwoWayValueChanges()
    {
        var value = false;
        var cut = Render<AppCheckbox>(parameters => parameters
            .Add(component => component.Label, "Select row")
            .Add(component => component.Value, value)
            .Add(component => component.ValueChanged, EventCallback.Factory.Create<bool>(this, next => value = next))
            .Add(component => component.Tone, AppUiTone.Warning));

        Assert.Single(cut.FindAll(".app-checkbox"));
        Assert.Single(cut.FindAll(".app-tone--warning"));
        Assert.Contains("Select row", cut.Markup);
        Assert.False(cut.Find("input[type='checkbox']").HasAttribute("checked"));
        Assert.Equal("true", cut.Find("svg").GetAttribute("aria-hidden"));
        Assert.Equal("false", cut.Find("svg").GetAttribute("focusable"));
        var uncheckedGlyph = cut.Find("svg").InnerHtml;

        cut.Find("input[type='checkbox']").Change(true);
        Assert.True(value);
        cut.Render(parameters => parameters.Add(component => component.Value, value));
        Assert.True(cut.Find("input[type='checkbox']").HasAttribute("checked"));
        Assert.NotEqual(uncheckedGlyph, cut.Find("svg").InnerHtml);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void AppCheckbox_DisabledOrReadOnlyDoesNotChangeControlledValue(bool disabled, bool readOnly)
    {
        var changes = 0;
        var cut = Render<AppCheckbox>(parameters => parameters
            .Add(component => component.Label, "Select row")
            .Add(component => component.Value, true)
            .Add(component => component.Disabled, disabled)
            .Add(component => component.ReadOnly, readOnly)
            .Add(component => component.ValueChanged, EventCallback.Factory.Create<bool>(this, _ => changes++)));

        var input = cut.Find("input[type='checkbox']");
        Assert.Equal(disabled, input.HasAttribute("disabled"));
        Assert.Equal(readOnly.ToString().ToLowerInvariant(), input.GetAttribute("aria-readonly"));
        input.Change(false);
        Assert.Equal(0, changes);
        Assert.True(cut.Instance.Value);
    }

    [Fact]
    public void AppCheckboxRow_KeepsOneLabeledNativeInputAndDecorativeGlyph()
    {
        var value = false;
        var cut = Render<AppCheckboxRow>(parameters => parameters
            .Add(component => component.Label, "Include artwork")
            .Add(component => component.Value, value)
            .Add(component => component.ValueChanged, EventCallback.Factory.Create<bool>(this, next => value = next)));

        var label = cut.Find("label");
        Assert.Contains("Include artwork", label.TextContent);
        Assert.Single(label.QuerySelectorAll("input[type='checkbox']"));
        Assert.Equal("true", cut.Find("svg").GetAttribute("aria-hidden"));
        cut.Find("input[type='checkbox']").Change(true);
        Assert.True(value);
        cut.Render(parameters => parameters.Add(component => component.Value, value));
        Assert.True(cut.Find("input[type='checkbox']").HasAttribute("checked"));
    }

    [Fact]
    public void AppButton_MapsToneSizeVariantAndClickCallback()
    {
        var clicked = false;

        var cut = Render<AppButton>(parameters => parameters
            .Add(component => component.Label, "Save")
            .Add(component => component.Tone, AppUiTone.Primary)
            .Add(component => component.Size, AppControlSize.Compact)
            .Add(component => component.ButtonStyle, AppButtonStyle.Filled)
            .Add(component => component.StartIcon, AppMaterialIcons.Filled.Save)
            .Add(component => component.OnClick, EventCallback.Factory.Create(this, () => clicked = true)));

        Assert.Single(cut.FindAll(".app-button"));
        Assert.Single(cut.FindAll(".app-control--compact"));
        Assert.Single(cut.FindAll(".app-tone--primary"));
        Assert.Single(cut.FindAll(".app-button--filled"));
        Assert.Contains("Save", cut.Markup);

        cut.Find("button").Click();
        Assert.True(clicked);
    }

    [Theory]
    [InlineData("AppSize.Small", "compact")]
    [InlineData("AppControlSize.Compact", "compact")]
    [InlineData("small", "compact")]
    [InlineData("AppSize.Medium", "normal")]
    [InlineData("AppControlSize.Normal", "normal")]
    [InlineData("AppSize.Large", "large")]
    [InlineData("AppControlSize.Large", "large")]
    public void ButtonsAcceptLiteralEnumSizeNamesFromRazorObjectParameters(string size, string expected)
    {
        var button = Render<AppButton>(parameters => parameters
            .Add(component => component.Size, (object)size)
            .Add(component => component.Label, "Save"));
        var icon = Render<AppIconButton>(parameters => parameters
            .Add(component => component.Size, (object)size)
            .Add(component => component.Icon, AppMaterialIcons.Outlined.Save)
            .Add(component => component.AriaLabel, "Save"));
        Assert.Single(button.FindAll($".app-control--{expected}"));
        Assert.Single(icon.FindAll($".app-control--{expected}"));
    }

    [Fact]
    public void AppButton_MergesUserLoadingAndUnmatchedAttributesWithoutMudButtonCollision()
    {
        var cut = Render<AppButton>(parameters => parameters
            .Add(component => component.Label, "Save")
            .Add(component => component.Loading, true)
            .Add(component => component.UserAttributes, new Dictionary<string, object>
            {
                ["aria-expanded"] = "false",
            })
            .AddUnmatched("data-editor-action", "save"));

        var button = cut.Find("button");
        Assert.Equal("true", button.GetAttribute("aria-busy"));
        Assert.Equal("false", button.GetAttribute("aria-expanded"));
        Assert.Equal("save", button.GetAttribute("data-editor-action"));
        Assert.True(button.HasAttribute("disabled"));
    }

    [Fact]
    public void AppTextField_RendersLabelHelpTextAndSizeClass()
    {
        var cut = Render<AppTextField>(parameters => parameters
            .Add(component => component.Label, "Provider Name")
            .Add(component => component.Value, "TMDB")
            .Add(component => component.HelpText, "Shown below the field.")
            .Add(component => component.Size, AppControlSize.Large));

        Assert.Single(cut.FindAll(".app-field"));
        Assert.Single(cut.FindAll(".app-control--large"));
        Assert.Contains("Provider Name", cut.Markup);
        Assert.Contains("Shown below the field.", cut.Markup);
    }

    [Fact]
    public void AppSelect_RendersSharedFieldAndOptions()
    {
        var cut = Render(builder =>
        {
            builder.OpenComponent<AppPopoverHost>(0);
            builder.CloseComponent();
            builder.OpenComponent<AppSelect>(1);
            builder.AddAttribute(2, nameof(AppSelect.Label), "Region");
            builder.AddAttribute(3, nameof(AppSelect.Value), "localized");
            builder.AddAttribute(4, nameof(AppSelect.Options), new[]
            {
                new AppSelectOption("source", "Source defaults"),
                new AppSelectOption("localized", "Localized metadata"),
            });
            builder.AddAttribute(5, nameof(AppSelect.Size), AppControlSize.Normal);
            builder.CloseComponent();
        });

        Assert.Single(cut.FindAll(".app-field"));
        Assert.Single(cut.FindAll(".app-control--normal"));
        Assert.Contains("Region", cut.Markup);
        Assert.Contains("Localized metadata", cut.Markup);
    }

    [Fact]
    public void AppSwitchRow_RendersLabelDescriptionAndDisabledState()
    {
        var cut = Render<AppSwitchRow>(parameters => parameters
            .Add(component => component.Label, "Status")
            .Add(component => component.Description, "Provider is enabled.")
            .Add(component => component.Value, true)
            .Add(component => component.Disabled, true));

        Assert.Single(cut.FindAll(".app-switch-row"));
        Assert.Contains("Status", cut.Markup);
        Assert.Contains("Provider is enabled.", cut.Markup);
        Assert.Contains("disabled", cut.Markup, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("Status", cut.Find("input[type='checkbox']").GetAttribute("aria-label"));
    }

    [Fact]
    public void AppSegmentedControl_UsesPressedButtonsForFilterSemantics()
    {
        var cut = Render<AppSegmentedControl>(parameters => parameters
            .Add(component => component.AriaLabel, "Library area")
            .Add(component => component.Value, "read")
            .Add(component => component.Options,
            [
                new AppSelectOption("all", "All"),
                new AppSelectOption("read", "Read"),
            ]));

        Assert.Equal("group", cut.Find(".app-segmented-control").GetAttribute("role"));
        Assert.Equal("true", cut.FindAll("button")[1].GetAttribute("aria-pressed"));
        Assert.Null(cut.FindAll("button")[1].GetAttribute("aria-selected"));
    }

    [Fact]
    public void SettingsSubsectionNav_RendersCanonicalLinksAndActiveState()
    {
        Services.GetRequiredService<NavigationManager>().NavigateTo("/settings/metadata/ingestion-flow");

        var cut = Render<SettingsSubsectionNav>(parameters => parameters
            .Add(component => component.Section, SettingsSection.Providers)
            .Add(component => component.ActiveSubsection, "ingestion-flow")
            .Add(component => component.AriaLabel, "Metadata sections"));

        Assert.Equal(2, cut.FindAll("a.settings-subsection-nav__item").Count);
        var active = cut.Find("a[href='/settings/metadata/ingestion-flow']");
        Assert.Equal("page", active.GetAttribute("aria-current"));
        Assert.Null(active.GetAttribute("aria-selected"));
        Assert.Null(active.GetAttribute("role"));
        Assert.Contains("is-active", active.ClassList);
    }

    [Fact]
    public void SettingsAdvancedLinkRow_RendersOneSemanticDestination()
    {
        var cut = Render<SettingsAdvancedLinkRow>(parameters => parameters
            .Add(component => component.Title, "Variant storage")
            .Add(component => component.Description, "Manage prepared media storage.")
            .Add(component => component.Href, "/settings/delivery/storage")
            .Add(component => component.ActionLabel, "Manage"));

        var link = cut.Find("a.settings-advanced-link-row__action");
        Assert.Equal("/settings/delivery/storage", link.GetAttribute("href"));
        Assert.Contains("Variant storage", cut.Markup);
        Assert.Contains("Manage", link.TextContent);
    }

    [Fact]
    public void AppProviderLogo_UsesSharedSizingAndFallback()
    {
        var cut = Render<AppProviderLogo>(parameters => parameters
            .Add(component => component.FallbackText, "TM")
            .Add(component => component.AccentColor, "#22C55E")
            .Add(component => component.Size, AppControlSize.Large));

        Assert.Single(cut.FindAll(".app-provider-logo"));
        Assert.Single(cut.FindAll(".app-control--large"));
        Assert.Single(cut.FindAll(".app-provider-logo--fallback"));
        Assert.Contains("TM", cut.Markup);
    }

    [Fact]
    public void AppProviderLogo_UsesTransparentImageTreatmentForProviderAssets()
    {
        var cut = Render<AppProviderLogo>(parameters => parameters
            .Add(component => component.ImageUrl, "images/providers/tmdb.svg")
            .Add(component => component.AltText, "TMDB")
            .Add(component => component.Size, AppControlSize.Normal));

        Assert.Single(cut.FindAll(".app-provider-logo--image"));
        Assert.Single(cut.FindAll("img[src='images/providers/tmdb.svg']"));
        Assert.Empty(cut.FindAll(".app-provider-logo__fallback"));
    }

}
