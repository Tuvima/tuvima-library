using Bunit;
using MediaEngine.Contracts.Items;
using MediaEngine.Contracts.Metadata;
using MediaEngine.Web.Components.Details;
using MediaEngine.Web.Services.Editing;
using MediaEngine.Web.Services.Integration;
using MediaEngine.Web.Services.Theming;
using MediaEngine.Web.Tests.Support;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace MediaEngine.Web.Tests;

/// <summary>Identify for a normal video: paste a YouTube link in Match and Identity.</summary>
public sealed class EditingVideoIdentifyTests : AsyncBunitContext
{
    private static readonly Guid VideoId = Guid.Parse("11111111-aaaa-bbbb-cccc-000000000003");

    private readonly DeviceContextService _device = new(EngineApiClientStub.CreateDefault());
    private readonly List<string> _links = [];
    private VideoIdentifyStateResponse _state = new() { Supported = true, Matched = false, LookupEnabled = true };
    private VideoIdentifyResponse? _answer = new() { Title = "The Real Title" };

    public EditingVideoIdentifyTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddSingleton(_device);
    }

    private async Task<(IRenderedComponent<EditingVideoIdentify> Cut, List<int> Matched)> RenderAsync()
    {
        await _device.InitialiseAsync(new DeviceContextSnapshot("web", "desktop", false, FinePointer: true, Hover: true));
        var api = EngineApiClientStub.Create(stub =>
        {
            stub.SetHandler(nameof(IEngineApiClient.GetMediaEditorContextAsync), _ => Task.FromResult<MediaEditorContextDto?>(EditingModeTestData.MovieContext()));
            stub.SetHandler(nameof(IEngineApiClient.GetItemEditorSuggestionsAsync), _ => Task.FromResult<IReadOnlyList<string>>([]));
            stub.SetHandler(nameof(IEngineApiClient.GetVideoIdentifyStateAsync), _ => Task.FromResult<VideoIdentifyStateResponse?>(_state));
            stub.SetHandler(nameof(IEngineApiClient.IdentifyVideoFromLinkAsync), args =>
            {
                _links.Add((string)args![1]!);
                return Task.FromResult(_answer);
            });
        });
        Services.AddSingleton(api);
        var session = new ItemFieldEditSession(api, _device);
        var mode = new EditingModeContext(_device, session) { HasPermission = true };
        mode.Loader = () => session.LoadAsync(VideoId, "Videos", "video", null);
        Assert.True(await mode.EnterAsync());
        var matched = new List<int>();
        var cut = Render<EditingVideoIdentify>(parameters => parameters
            .Add(component => component.Mode, mode)
            .Add(component => component.OnMatched, EventCallback.Factory.Create(this, () => matched.Add(1))));
        return (cut, matched);
    }

    [Fact]
    public async Task APastedLink_IsSentOnce_AndTheMatchIsShown()
    {
        var (cut, matched) = await RenderAsync();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("input")));

        Assert.True(cut.Find("button:contains('Identify')").HasAttribute("disabled"));
        cut.Find("input").Input("  https://youtu.be/dQw4w9WgXcQ ");
        cut.Find("button:contains('Identify')").Click();

        cut.WaitForAssertion(() => Assert.Contains("Matched: The Real Title.", cut.Markup));
        Assert.Equal(["https://youtu.be/dQw4w9WgXcQ"], _links);
        Assert.Single(matched);
    }

    [Fact]
    public async Task WhenNothingMatches_ItSaysSo_AndOffersTheLinkAgain()
    {
        _answer = null;
        var (cut, matched) = await RenderAsync();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("input")));

        cut.Find("input").Input("dQw4w9WgXcQ");
        cut.Find("button:contains('Identify')").Click();

        cut.WaitForAssertion(() => Assert.Contains("No match was found", cut.Find("[role='alert']").TextContent));
        Assert.NotEmpty(cut.FindAll("input"));
        Assert.Empty(matched);
    }

    [Fact]
    public async Task WhenTheLibraryHasTheLookupOff_ThereIsNothingToPaste()
    {
        _state = new VideoIdentifyStateResponse { Supported = true, Matched = false, LookupEnabled = false };
        var (cut, _) = await RenderAsync();

        cut.WaitForAssertion(() => Assert.Contains("switched off for this library", cut.Markup));
        Assert.Empty(cut.FindAll("input"));
    }

    [Fact]
    public async Task AVideoThatIsAlreadyMatched_ShowsNoLinkBox()
    {
        _state = new VideoIdentifyStateResponse { Supported = true, Matched = true, LookupEnabled = true };
        var (cut, _) = await RenderAsync();

        cut.WaitForAssertion(() => Assert.Contains("This video is matched.", cut.Markup));
        Assert.Empty(cut.FindAll("input"));
    }

    [Fact]
    public async Task ForSomethingThatIsNotAVideo_NothingIsDrawn()
    {
        _state = new VideoIdentifyStateResponse { Supported = false };
        var (cut, _) = await RenderAsync();

        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll(".tl-editing-identify")));
    }
}
