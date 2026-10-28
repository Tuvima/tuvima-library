using Bunit;
using MediaEngine.Web.Components.Shared;
using MediaEngine.Web.Services.Theming;
using MediaEngine.Web.Services.Ui;
using MediaEngine.Web.Tests.Support;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace MediaEngine.Web.Tests;

public sealed class PhonePrimitivesTests : AsyncBunitContext
{
    private readonly DeviceContextService _device = new(EngineApiClientStub.CreateDefault());

    public PhonePrimitivesTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddSingleton(_device);
    }

    private Task UseAsync(string formFactor) =>
        _device.InitialiseAsync(new DeviceContextSnapshot(formFactor == "phone" ? "mobile" : "web", formFactor, formFactor != "desktop"));

    [Theory]
    [InlineData("phone", AppDialogSheetMode.Phone, true)]
    [InlineData("tablet", AppDialogSheetMode.Phone, false)]
    [InlineData("desktop", AppDialogSheetMode.Phone, false)]
    [InlineData("phone", AppDialogSheetMode.PhoneAndTablet, true)]
    [InlineData("tablet", AppDialogSheetMode.PhoneAndTablet, true)]
    [InlineData("desktop", AppDialogSheetMode.PhoneAndTablet, false)]
    [InlineData("phone", AppDialogSheetMode.Never, false)]
    public async Task DialogIsASheetOnlyForTheMatchingFormFactor(string formFactor, AppDialogSheetMode mode, bool expectSheet)
    {
        await UseAsync(formFactor);

        var frame = Render<AppDialogFrame>(parameters => parameters
            .Add(frame => frame.Title, "Pick one")
            .Add(frame => frame.DialogOptions, new AppDialogOptions { Sheet = mode })
            .AddChildContent("<p>body</p>"));

        Assert.Equal(expectSheet, frame.Find("dialog").ClassList.Contains("app-native-dialog--sheet"));
        Assert.Equal(expectSheet, frame.FindAll("[data-app-dialog-grip]").Count == 1);
    }

    [Fact]
    public async Task DialogClassFollowsALaterFormFactorChangeAndTellsTheModuleItIsASheet()
    {
        var module = JSInterop.SetupModule("./js/app-dialog.js");
        module.Mode = JSRuntimeMode.Loose;
        await UseAsync("desktop");
        var frame = Render<AppDialogFrame>(parameters => parameters
            .Add(frame => frame.DialogOptions, new AppDialogOptions { Sheet = AppDialogSheetMode.Phone }));
        Assert.DoesNotContain("app-native-dialog--sheet", frame.Find("dialog").ClassName);
        Assert.False((bool)module.Invocations.Last(i => i.Identifier == "update").Arguments.Last()!);

        await _device.SwitchDeviceAsync(new DeviceContextSnapshot("mobile", "phone", true));

        frame.WaitForAssertion(() => Assert.Contains("app-native-dialog--sheet", frame.Find("dialog").ClassName));
        frame.WaitForAssertion(() => Assert.True((bool)module.Invocations.Last(i => i.Identifier == "update").Arguments.Last()!));
    }

    [Fact]
    public void DialogWithoutADeviceContextStaysCentred()
    {
        // No DeviceContextService is registered for this context's service provider override.
        using var plain = new BunitContext();
        plain.JSInterop.Mode = JSRuntimeMode.Loose;
        plain.Services.AddNativeUiServices();

        var frame = plain.Render<AppDialogFrame>(parameters => parameters
            .Add(frame => frame.DialogOptions, new AppDialogOptions { Sheet = AppDialogSheetMode.PhoneAndTablet }));

        Assert.DoesNotContain("app-native-dialog--sheet", frame.Find("dialog").ClassName);
    }

    [Theory]
    [InlineData(AppTableCardsMode.Never, false, false)]
    [InlineData(AppTableCardsMode.Phone, true, false)]
    [InlineData(AppTableCardsMode.PhoneAndTablet, true, true)]
    public void TableAdvertisesCardsModeAndKeepsRoleAttributes(AppTableCardsMode mode, bool cards, bool tablet)
    {
        var table = Render<AppTable>(parameters => parameters
            .Add(table => table.CardsOn, mode)
            .AddChildContent("<thead><tr><th data-card-role=\"title\">Title</th><th data-card-role=\"value\">Size</th></tr></thead>"
                + "<tbody><tr><td data-card-role=\"title\">Dune</td><td data-card-role=\"value\">2 MB</td></tr></tbody>"));

        var shell = table.Find(".app-table-shell");
        Assert.Equal(cards, shell.ClassList.Contains("app-table-shell--cards"));
        Assert.Equal(cards, JSInterop.Invocations.Any(i => i.Identifier == "tuvimaTableCards.apply"));
        if (!cards)
        {
            Assert.DoesNotContain("cards", shell.ClassName);
        }
        Assert.Equal(tablet, shell.ClassList.Contains("app-table-shell--cards-tablet"));
        Assert.Equal(2, table.FindAll("th[data-card-role]").Count);
        Assert.Equal(2, table.FindAll("td[data-card-role]").Count);
        // The header row stays in the markup (screen readers keep the column names); CSS only hides it visually.
        Assert.Single(table.FindAll("thead"));
    }

    [Fact]
    public async Task RowMenuPutsOneDividerBeforeTheDestructiveItemsAtTheEnd()
    {
        await UseAsync("phone");
        var menu = Render<AppRowMenu>(parameters => parameters
            .Add(menu => menu.Items, new[]
            {
                new AppRowMenuItem { Label = "Open details" },
                new AppRowMenuItem { Label = "Remove", Destructive = true },
                new AppRowMenuItem { Label = "Delete", Destructive = true },
            }));

        menu.Find("button.app-row-menu__trigger").Click();

        Assert.Single(menu.FindAll("hr.app-menu-divider"));
        Assert.Equal("Remove", menu.Find("hr.app-menu-divider + [role='menuitem']").TextContent.Trim());
    }

    [Fact]
    public async Task RowMenuOpensASheetOnPhoneAndRunsTheAction()
    {
        await UseAsync("phone");
        var chosen = 0;
        var menu = Render<AppRowMenu>(parameters => parameters
            .Add(menu => menu.AriaLabel, "Actions for Dune")
            .Add(menu => menu.Items, new[]
            {
                new AppRowMenuItem { Label = "Open details", OnClick = EventCallback.Factory.Create(this, () => chosen++) },
                new AppRowMenuItem { Label = "Remove", Destructive = true },
            }));

        Assert.Empty(menu.FindAll("dialog"));
        Assert.Empty(menu.FindAll(".app-overflow-menu__popover"));
        var trigger = menu.Find("button.app-row-menu__trigger");
        Assert.Equal("Actions for Dune", trigger.GetAttribute("aria-label"));
        Assert.Equal("dialog", trigger.GetAttribute("aria-haspopup"));

        trigger.Click();

        var dialog = menu.Find("dialog");
        Assert.Contains("app-native-dialog--sheet", dialog.ClassName);
        var items = menu.FindAll("[role='menuitem']");
        Assert.Equal(2, items.Count);
        Assert.Contains("app-tone--error", items[1].ClassName);

        await items[0].ClickAsync(new());

        Assert.Equal(1, chosen);
        menu.WaitForAssertion(() => Assert.Empty(menu.FindAll("dialog")));
    }

    [Fact]
    public async Task TappingAChildContentItemInThePhoneSheetRunsItAndClosesTheSheet()
    {
        await UseAsync("phone");
        var chosen = 0;
        var menu = Render<AppRowMenu>(parameters => parameters
            .AddChildContent<AppMenuItem>(item => item
                .Add(item => item.Label, "Share")
                .Add(item => item.OnClick, () => { chosen++; })));
        menu.Find("button.app-row-menu__trigger").Click();

        await menu.Find("[role='menuitem']").ClickAsync(new());

        Assert.Equal(1, chosen);
        menu.WaitForAssertion(() => Assert.Empty(menu.FindAll("dialog")));
    }

    [Fact]
    public async Task RowMenuUsesThePopoverOnDesktop()
    {
        await UseAsync("desktop");
        var menu = Render<AppRowMenu>(parameters => parameters
            .Add(menu => menu.Items, new[] { new AppRowMenuItem { Label = "Open details" } }));

        Assert.Empty(menu.FindAll("dialog"));
        var trigger = menu.Find(".app-overflow-menu__trigger");
        Assert.Contains("app-row-menu__trigger", trigger.ClassName);
        Assert.Equal("menu", trigger.GetAttribute("aria-haspopup"));

        trigger.Click();

        Assert.Empty(menu.FindAll("dialog"));
        Assert.Single(menu.FindAll("[role='menuitem']"));
    }
}
