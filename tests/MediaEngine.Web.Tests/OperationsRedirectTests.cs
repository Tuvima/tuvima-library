using Bunit;
using MediaEngine.Web.Components.Pages;
using MediaEngine.Web.Models.ViewDTOs;
using MediaEngine.Web.Tests.Support;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace MediaEngine.Web.Tests;

/// <summary>The old Operations addresses redirect into Settings; the activity ones land on the Events log of System > Logs.</summary>
public sealed class OperationsRedirectTests : AsyncBunitContext
{
    [Theory]
    [InlineData("/operations/activity")]
    [InlineData("/operations/timeline")]
    public void TheOldActivityAndTimelineAddresses_OpenTheActivityLog_WithNoFilter(string address)
    {
        var navigation = Services.GetRequiredService<NavigationManager>();
        navigation.NavigateTo(address);

        Render<Operations>();

        Assert.EndsWith("/settings/system/logs?tab=events", navigation.Uri, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("/operations/ingestion", "/settings/libraries")]
    [InlineData("/operations/recently-added", "/settings/library-activity")]
    public void TheOtherOldAddresses_StillRedirectAsBefore(string address, string expected)
    {
        var navigation = Services.GetRequiredService<NavigationManager>();
        navigation.NavigateTo(address);

        Render<Operations>();

        Assert.EndsWith(expected, navigation.Uri, StringComparison.Ordinal);
    }

    [Fact]
    public void ActivityLogRoute_IsBuiltInOnePlace_WithAnOptionalFilter()
    {
        Assert.Equal("/settings/system/logs?tab=events", SettingsNav.ActivityLogRoute());
        Assert.Equal("/settings/system/logs?tab=events", SettingsNav.ActivityLogRoute("  "));
        Assert.Equal("/settings/system/logs?tab=events&filter=added", SettingsNav.ActivityLogRoute("added"));
    }
}
