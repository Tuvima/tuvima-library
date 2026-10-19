using Bunit;
using MediaEngine.Contracts.Ingestion;
using MediaEngine.Web.Components.Settings;
using MediaEngine.Web.Components.Shared;
using MediaEngine.Web.Services.Integration;
using MediaEngine.Web.Tests.Support;
using Microsoft.Extensions.DependencyInjection;

namespace MediaEngine.Web.Tests;

/// <summary>A file waiting for a media tool is shown as waiting (never failed), with one notice that links to the tools list.</summary>
public sealed class IngestionWaitingForToolsTests : AsyncBunitContext
{
    private const string HourglassPathStart = "M6,2l0.01,6L10,12";

    public IngestionWaitingForToolsTests()
    {
        Services.AddNativeUiServices();
        Services.AddSingleton(EngineApiClientStub.Create(_ => { }));
        JSInterop.Mode = JSRuntimeMode.Loose;
        Render<AppPopoverHost>();
    }

    [Fact]
    public void ListRow_ForAFileWaitingForTools_ShowsTheLabelAndAnHourglass()
    {
        var cut = Render<IngestionMediaCard>(parameters => parameters
            .Add(component => component.DisplayMode, "list")
            .Add(component => component.Item, Waiting()));

        var status = cut.Find(".ingestion-media-list-row__status");
        Assert.Contains("Waiting for media tools", status.TextContent, StringComparison.Ordinal);
        Assert.Contains(HourglassPathStart, status.InnerHtml, StringComparison.Ordinal);
    }

    [Fact]
    public void Drawer_ForAFileWaitingForTools_ShowsTheLabelWithAnHourglass()
    {
        var cut = Render<IngestionMediaDrawer>(parameters => parameters
            .Add(component => component.Item, Waiting()));

        var status = cut.Find(".ingestion-drawer__progress > span.is-waiting_for_tools");
        Assert.Contains("Waiting for media tools", status.TextContent, StringComparison.Ordinal);
        Assert.Contains(HourglassPathStart, status.InnerHtml, StringComparison.Ordinal);
    }

    [Fact]
    public void Notice_WhenAnyVisibleItemIsWaiting_LinksToTheToolsPage()
    {
        var cut = Render<MediaToolsWaitingNotice>(parameters => parameters
            .Add(component => component.Items, [Ready(), Waiting()]));

        Assert.Contains("Some files are waiting for media tools.", cut.Markup, StringComparison.Ordinal);
        Assert.Equal("/settings/tools", cut.Find("a").GetAttribute("href"));
        Assert.Contains("Tools and requirements", cut.Find("a").TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public void Notice_WhenNothingIsWaiting_ShowsNothing()
    {
        var cut = Render<MediaToolsWaitingNotice>(parameters => parameters
            .Add(component => component.Items, [Ready()]));

        Assert.Empty(cut.Markup.Trim());
    }

    private static IngestionMediaGroupDto Waiting() => new()
    {
        Title = "Dune",
        MediaType = "Movies",
        Availability = "waiting_for_tools",
        StatusLabel = "Waiting for media tools",
        FileCount = 1,
    };

    private static IngestionMediaGroupDto Ready() => new()
    {
        Title = "The Quiet Mind",
        MediaType = "Books",
        Availability = "ready",
        StatusLabel = "Ready",
        FileCount = 1,
    };
}
