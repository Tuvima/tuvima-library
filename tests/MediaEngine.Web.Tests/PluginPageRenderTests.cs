using Bunit;
using MediaEngine.Contracts.Plugins;
using MediaEngine.Web.Components.Pages;
using MediaEngine.Web.Services.Integration;
using MediaEngine.Web.Services.Navigation;
using MediaEngine.Web.Services.Theming;
using MediaEngine.Web.Services.Ui;
using MediaEngine.Web.Tests.Support;
using Microsoft.Extensions.DependencyInjection;

namespace MediaEngine.Web.Tests;

/// <summary>Pages plugins add (WP16): drawn in the Library's own page types, and the guide timeline stays off phones.</summary>
public sealed class PluginPageRenderTests : AsyncBunitContext
{
    private PluginPageResponse? _page;
    private int _pageRequests;
    private PluginPageSubmitResponse? _submitResult = new() { Saved = true, RecordKey = "r" };
    private IReadOnlyDictionary<string, string>? _submitted;

    public PluginPageRenderTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddLogging();
        Services.AddLocalization();
        Services.AddNativeUiServices();
        Services.AddSingleton(_ => EngineApiClientStub.Create(stub =>
        {
            stub.SetHandler(nameof(IEngineApiClient.GetPluginPageAsync), _ =>
            {
                _pageRequests++;
                return Task.FromResult(_page);
            });
            stub.SetHandler(nameof(IEngineApiClient.SubmitPluginPageFormAsync), args =>
            {
                _submitted = new Dictionary<string, string>((IReadOnlyDictionary<string, string>)args![2]!);
                return Task.FromResult(_submitResult);
            });
        }));
        Services.AddScoped(provider => new DeviceContextService(provider.GetRequiredService<IEngineApiClient>()));
    }

    private IRenderedComponent<PluginPage> RenderPage(string key = "channels") =>
        Render<PluginPage>(parameters => parameters.Add(p => p.PluginId, "test.pages").Add(p => p.PageKey, key));

    private static PluginPageItemDto Item(string id, string title, string group = "", DateTimeOffset? start = null, DateTimeOffset? end = null) =>
        new() { Id = id, Title = title, Group = group, StartsAt = start, EndsAt = end };

    [Fact]
    public void ListPage_ShowsEachRowWithItsColumns()
    {
        var alpha = Item("a", "Alpha");
        alpha.Fields["number"] = "1";
        _page = new PluginPageResponse
        {
            PluginName = "Pages plugin",
            Title = "Channels",
            PageType = "list",
            PhoneSupported = true,
            Columns = [new PluginPageColumnDto { Key = "number", Label = "Number" }],
            Items = [alpha],
        };

        var cut = RenderPage();

        cut.WaitForAssertion(() => Assert.Equal("Channels", cut.Find(".plugin-page__title").TextContent));
        Assert.Contains("Pages plugin", cut.Find(".plugin-page__source").TextContent, StringComparison.Ordinal);
        Assert.Equal("Alpha", cut.Find(".plugin-page__row-title").TextContent);
        Assert.Contains("Number", cut.Find(".plugin-page__row-cell").TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public void GuidePage_PutsEachProgrammeInItsChannelRow()
    {
        var start = DateTimeOffset.UtcNow.AddMinutes(-10);
        _page = new PluginPageResponse
        {
            Title = "Guide",
            PageType = "guide",
            Items = [Item("1", "News at ten", "Channel 4", start, start.AddHours(1)), Item("2", "Film", "Channel 5", start, start.AddHours(2))],
        };

        var cut = RenderPage("guide");

        cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll(".plugin-page__guide-row").Count));
        Assert.Equal(["Channel 4", "Channel 5"], cut.FindAll(".plugin-page__guide-row .plugin-page__guide-channel").Select(e => e.TextContent));
        Assert.Equal(2, cut.FindAll(".plugin-page__guide-slot").Count);
    }

    [Fact]
    public async Task OnAPhone_AGuideSaysToUseALargerScreen()
    {
        await Services.GetRequiredService<DeviceContextService>().InitialiseAsync(new DeviceContextSnapshot("mobile", "phone", true));
        _page = new PluginPageResponse { Title = "Guide", PageType = "guide", PhoneSupported = false };

        var cut = RenderPage("guide");

        cut.WaitForAssertion(() => Assert.Contains("larger screen", cut.Markup, StringComparison.Ordinal));
        Assert.Empty(cut.FindAll(".plugin-page__guide"));
    }

    [Fact]
    public void APageThatIsNotThere_SaysSoPlainly()
    {
        _page = null;

        var cut = RenderPage("missing");

        cut.WaitForAssertion(() => Assert.Contains("isn't available", cut.Markup, StringComparison.Ordinal));
    }

    [Fact]
    public void FormPage_SendsTheTypedValues()
    {
        _page = new PluginPageResponse
        {
            Title = "Request",
            PageType = "form",
            PhoneSupported = true,
            SubmitLabel = "Send",
            FormFields = [new PluginPageFormFieldDto { Key = "title", Label = "Title", Kind = "text", Required = true }],
        };
        var cut = RenderPage("request");
        cut.WaitForAssertion(() => Assert.True(cut.FindAll("form.plugin-page__form").Count > 0, cut.Markup));

        cut.Find("input").Input("Dune");
        cut.Find("form").Submit();

        cut.WaitForAssertion(() => Assert.True(_submitted is not null, cut.Markup));
        Assert.Equal("Dune", _submitted!["title"]);
    }

    [Fact]
    public void Menu_ForAPhone_HidesPageTypesThatDoNotSuitIt()
    {
        PluginMenuEntryDto[] entries =
        [
            new() { PluginId = "p", PageKey = "list", Label = "List", PageType = "list", PhoneSupported = true },
            new() { PluginId = "p", PageKey = "guide", Label = "Guide", PageType = "guide", PhoneSupported = false },
        ];

        Assert.Equal(["List"], PluginPageRoute.ForDevice(entries, isPhone: true).Select(e => e.Label));
        Assert.Equal(["List", "Guide"], PluginPageRoute.ForDevice(entries, isPhone: false).Select(e => e.Label));
        Assert.Equal("/plugin/test.pages/my%20page", PluginPageRoute.Href("test.pages", "my page"));
    }
}
