using Bunit;
using MediaEngine.Contracts.Details;
using MediaEngine.Web.Components.Details;
using MediaEngine.Web.Services.Theming;
using MediaEngine.Web.Tests.Support;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace MediaEngine.Web.Tests;

/// <summary>
/// The details "..." menu (details overlay spec D2, D5, D10; editor v6 D19): the Edit entry per device and permission (desktop, tablet and
/// phone; never a TV), the order with Delete last and red, and the reason a disabled Edit gives.
/// </summary>
public sealed class OverflowActionMenuTests : AsyncBunitContext
{
    private const string EditLabel = "Edit this show";
    private readonly DeviceContextService _device = new(EngineApiClientStub.CreateDefault());

    public OverflowActionMenuTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddSingleton(_device);
    }

    private Task UseAsync(string device) => _device.InitialiseAsync(device switch
    {
        "desktop" => new DeviceContextSnapshot("web", "desktop", false, FinePointer: true, Hover: true),
        "desktop-no-pointer" => new DeviceContextSnapshot("web", "desktop", false),
        "tablet" => new DeviceContextSnapshot("web", "tablet", true),
        "phone" => new DeviceContextSnapshot("mobile", "phone", true),
        "tv" => new DeviceContextSnapshot("television", "tv", false, FinePointer: true, Hover: true),
        _ => new DeviceContextSnapshot("web", "mystery", false),
    });

    private static DetailAction Action(string key, string label, string icon = "info") => new() { Key = key, Label = label, Icon = icon };

    private static DetailAction Edit(bool disabled = false, string? reason = null) =>
        new() { Key = "edit", Label = EditLabel, Icon = "edit", IsDisabled = disabled, Tooltip = reason };

    private static readonly DetailAction History = Action("status-history", "Viewing history", "history");

    private IRenderedComponent<OverflowActionMenu> RenderMenu(params DetailAction[] actions) =>
        Render<OverflowActionMenu>(parameters => parameters
            .Add(component => component.Actions, actions)
            .Add(component => component.ButtonClass, "hero-icon-circle"));

    private static IReadOnlyList<string> Labels(IRenderedComponent<OverflowActionMenu> menu) =>
        menu.FindAll("[role='menuitem']").Select(item => item.TextContent.Trim()).ToList();

    [Fact]
    public async Task DesktopThatCanEditShowsTheEditEntryWhenTheEngineOfferedIt()
    {
        await UseAsync("desktop");
        var menu = RenderMenu(History, Edit());

        menu.Find("button.hero-icon-circle").Click();

        Assert.Contains(EditLabel, Labels(menu));
    }

    [Fact]
    public async Task DesktopDoesNotShowEditOrTheLineWhenTheEngineDidNotOfferEdit()
    {
        await UseAsync("desktop");
        var menu = RenderMenu(History);

        menu.Find("button.hero-icon-circle").Click();

        Assert.Equal(["Viewing history"], Labels(menu));
    }

    [Fact]
    public async Task DesktopWithNothingToOfferDrawsNoMenu()
    {
        await UseAsync("desktop");

        Assert.Empty(RenderMenu().FindAll("button"));
    }

    // Editing works on a tablet and a phone (editor v6 D19): the Edit entry is there, with no "Edit on a computer" line.
    [Theory]
    [InlineData("tablet")]
    [InlineData("desktop-no-pointer")]
    public async Task DevicesWithoutAMouseShowTheEditEntry_AndNoQuietLine(string device)
    {
        await UseAsync(device);
        var menu = RenderMenu(History, Edit());

        menu.Find("button.hero-icon-circle").Click();

        Assert.Equal(["Viewing history", EditLabel], Labels(menu));
        Assert.DoesNotContain("Edit on a computer", menu.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TabletDoesNotShowEditToAPersonWhoWasNotOfferedEdit()
    {
        await UseAsync("tablet");
        var menu = RenderMenu(History);

        menu.Find("button.hero-icon-circle").Click();

        Assert.Equal(["Viewing history"], Labels(menu));
    }

    [Theory]
    [InlineData("tablet")]
    [InlineData("phone")]
    public async Task TabletAndPhoneMenuThatHoldsOnlyEditIsDrawn(string device)
    {
        await UseAsync(device);

        var menu = RenderMenu(Edit());
        menu.Find("button.hero-icon-circle").Click();

        Assert.Equal([EditLabel], Labels(menu));
    }

    [Fact]
    public async Task PhoneSheetHasTheEditEntry_AndNoQuietLine()
    {
        await UseAsync("phone");
        var menu = RenderMenu(History, Edit());

        menu.Find("button.hero-icon-circle").Click();

        Assert.Contains("app-native-dialog--sheet", menu.Find("dialog").ClassName);
        Assert.Equal(["Viewing history", EditLabel], Labels(menu));
    }

    [Fact]
    public async Task TvMenuThatHeldOnlyEditIsNotDrawn()
    {
        await UseAsync("tv");

        var menu = RenderMenu(Edit());

        Assert.Empty(menu.FindAll("button"));
        Assert.DoesNotContain("Edit on a computer", menu.Markup, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("tv")]
    [InlineData("unknown")]
    public async Task TvAndUnknownDevicesGetNeitherTheEntryNorTheLine(string device)
    {
        await UseAsync(device);
        var menu = RenderMenu(History, Edit());

        menu.Find("button.hero-icon-circle").Click();

        Assert.Equal(["Viewing history"], Labels(menu));
        Assert.DoesNotContain("Edit on a computer", menu.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DesktopMenuOpensBesideItsButtonAlignedToTheButtonsLeftEdge()
    {
        await UseAsync("desktop");
        var menu = RenderMenu(History);

        menu.Find("button.hero-icon-circle").Click();

        Assert.NotEmpty(menu.FindAll(".app-overflow-menu__popover--left"));
    }

    [Fact]
    public async Task DeleteIsAlwaysTheLastEntryInRedWithADividerBeforeIt()
    {
        await UseAsync("desktop");
        DetailAction delete = new() { Key = "delete", Label = "Delete this show", Icon = "delete", IsDestructive = true };
        var menu = RenderMenu(delete, Edit(), Action("add-playlist", "Add to playlist", "playlist_add"), Action("status-complete", "Mark as watched", "check"), History);

        menu.Find("button.hero-icon-circle").Click();

        Assert.Equal(["Mark as watched", "Viewing history", "Add to playlist", EditLabel, "Delete this show"], Labels(menu));
        var last = menu.FindAll("[role='menuitem']").Last();
        Assert.Contains("app-tone--error", last.ClassName);
        Assert.Single(menu.FindAll("[role='menuitem'].app-tone--error"));
        Assert.Contains("app-menu-divider", last.PreviousElementSibling?.ClassName);
    }

    [Fact]
    public async Task DeleteByKeyIsRedAndLastEvenWithoutTheDestructiveFlag()
    {
        await UseAsync("desktop");
        var menu = RenderMenu(Action("delete", "Delete", "delete"), Edit());

        menu.Find("button.hero-icon-circle").Click();

        Assert.Equal([EditLabel, "Delete"], Labels(menu));
        Assert.Contains("app-tone--error", menu.FindAll("[role='menuitem']").Last().ClassName);
    }

    [Theory]
    [InlineData("tablet")]
    [InlineData("phone")]
    public async Task OnATabletOrPhoneDeleteIsStillTheLastEntryInRed(string device)
    {
        await UseAsync(device);
        DetailAction delete = new() { Key = "delete", Label = "Delete", Icon = "delete", IsDestructive = true };
        var menu = RenderMenu(History, Edit(), delete);

        menu.Find("button.hero-icon-circle").Click();

        Assert.Equal(["Viewing history", EditLabel, "Delete"], Labels(menu));
        Assert.Contains("app-tone--error", menu.FindAll("[role='menuitem']").Last().ClassName);
    }

    [Fact]
    public async Task DisabledEditShowsItsReasonAsAQuietSecondLineAndCannotBeChosen()
    {
        await UseAsync("desktop");
        const string reason = "Updating details. Editing is available when ingestion finishes.";
        DetailAction? chosen = null;
        var menu = Render<OverflowActionMenu>(parameters => parameters
            .Add(component => component.Actions, [Edit(disabled: true, reason: reason)])
            .Add(component => component.ButtonClass, "hero-icon-circle")
            .Add(component => component.OnActionSelected, EventCallback.Factory.Create<DetailAction>(this, action => chosen = action)));

        menu.Find("button.hero-icon-circle").Click();

        var item = Assert.Single(menu.FindAll("[role='menuitem']"));
        Assert.True(item.HasAttribute("disabled"));
        Assert.Contains(EditLabel, item.TextContent);
        Assert.Equal(reason, item.QuerySelector(".tl-detail-menu-item__reason")?.TextContent.Trim());
        Assert.Null(chosen);
    }

    [Fact]
    public async Task ChoosingTheEditEntryHandsTheEditActionToThePage()
    {
        await UseAsync("desktop");
        DetailAction? chosen = null;
        var menu = Render<OverflowActionMenu>(parameters => parameters
            .Add(component => component.Actions, [History, Edit()])
            .Add(component => component.ButtonClass, "hero-icon-circle")
            .Add(component => component.OnActionSelected, EventCallback.Factory.Create<DetailAction>(this, action => chosen = action)));

        menu.Find("button.hero-icon-circle").Click();
        await menu.FindAll("[role='menuitem']").Single(item => item.TextContent.Contains(EditLabel)).ClickAsync(new());

        Assert.Equal("edit", chosen?.Key);
    }

    private static readonly DetailAction IdentifyAction = new() { Key = "identify", Label = "Identify", Icon = "search" };
    private static readonly DetailAction ThumbnailAction = new() { Key = "change-thumbnail", Label = "Change thumbnail", Icon = "image" };

    [Fact]
    public async Task DesktopThatCanEditShowsIdentifyAndChangeThumbnailBetweenEditAndNothingElse()
    {
        await UseAsync("desktop");
        var menu = RenderMenu(History, IdentifyAction, ThumbnailAction, Edit());

        menu.Find("button.hero-icon-circle").Click();

        Assert.Equal(["Viewing history", EditLabel, "Identify", "Change thumbnail"], Labels(menu));
    }

    [Theory]
    [InlineData("tv")]
    [InlineData("unknown")]
    public async Task WhereTheDeviceCannotEdit_IdentifyAndChangeThumbnailDisappearWithEdit(string device)
    {
        await UseAsync(device);
        var menu = RenderMenu(History, Edit(), IdentifyAction, ThumbnailAction);

        menu.Find("button.hero-icon-circle").Click();

        Assert.DoesNotContain("Identify", Labels(menu));
        Assert.DoesNotContain("Change thumbnail", Labels(menu));
    }

    [Theory]
    [InlineData("tablet")]
    [InlineData("phone")]
    public async Task OnATabletOrPhone_IdentifyAndChangeThumbnailStayWithEdit(string device)
    {
        await UseAsync(device);
        var menu = RenderMenu(History, Edit(), IdentifyAction, ThumbnailAction);

        menu.Find("button.hero-icon-circle").Click();

        Assert.Equal(["Viewing history", EditLabel, "Identify", "Change thumbnail"], Labels(menu));
    }
}
