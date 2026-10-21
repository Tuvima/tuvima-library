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

/// <summary>Putting a standalone video into a series, and taking a video out of one, in Editing Mode.</summary>
public sealed class EditingVideoSeriesTests : AsyncBunitContext
{
    private static readonly Guid VideoId = Guid.Parse("11111111-aaaa-bbbb-cccc-000000000002");
    private static readonly Guid Course = Guid.Parse("22222222-aaaa-bbbb-cccc-000000000002");

    private readonly DeviceContextService _device = new(EngineApiClientStub.CreateDefault());
    private readonly List<(Guid? Series, string? Name)> _promotes = [];
    private int _removes;
    private bool _saveSucceeds = true;
    private VideoSeriesStateResponse _state = new()
    {
        Supported = true,
        InSeries = false,
        Targets = [new VideoSeriesTargetDto { Id = Course, Title = "Course", VideoCount = 4 }],
    };

    public EditingVideoSeriesTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddSingleton(_device);
    }

    private async Task<(IRenderedComponent<CascadingValue<EditingModeContext>> Cut, List<int> Changed)> RenderAsync(bool desktop = true)
    {
        await _device.InitialiseAsync(desktop
            ? new DeviceContextSnapshot("web", "desktop", false, FinePointer: true, Hover: true)
            : new DeviceContextSnapshot("mobile", "phone", true));
        var api = EngineApiClientStub.Create(stub =>
        {
            stub.SetHandler(nameof(IEngineApiClient.GetMediaEditorContextAsync), _ => Task.FromResult<MediaEditorContextDto?>(EditingModeTestData.MovieContext()));
            stub.SetHandler(nameof(IEngineApiClient.GetItemEditorSuggestionsAsync), _ => Task.FromResult<IReadOnlyList<string>>([]));
            stub.SetHandler(nameof(IEngineApiClient.GetVideoSeriesAsync), _ => Task.FromResult<VideoSeriesStateResponse?>(_state));
            stub.SetHandler(nameof(IEngineApiClient.PromoteVideoToSeriesAsync), args =>
            {
                _promotes.Add(((Guid?)args![1], (string?)args[2]));
                if (_saveSucceeds)
                {
                    _state = new VideoSeriesStateResponse { Supported = true, InSeries = true, SeriesTitle = "Course" };
                }

                return Task.FromResult(_saveSucceeds);
            });
            stub.SetHandler(nameof(IEngineApiClient.RemoveVideoFromSeriesAsync), _ =>
            {
                _removes++;
                _state = new VideoSeriesStateResponse { Supported = true, InSeries = false, Targets = [] };
                return Task.FromResult(true);
            });
        });
        Services.AddSingleton(api);
        var session = new ItemFieldEditSession(api, _device);
        var mode = new EditingModeContext(_device, session) { HasPermission = true };
        mode.Loader = () => session.LoadAsync(VideoId, "Videos", "video", null);
        Assert.True(await mode.EnterAsync());
        var changed = new List<int>();
        var cut = Render<CascadingValue<EditingModeContext>>(parameters => parameters
            .Add(component => component.Value, mode)
            .Add(component => component.ChildContent, (RenderFragment)(builder =>
            {
                builder.OpenComponent<EditingVideoSeries>(0);
                builder.AddAttribute(1, nameof(EditingVideoSeries.OnChanged), EventCallback.Factory.Create(this, () => changed.Add(1)));
                builder.CloseComponent();
            })));
        return (cut, changed);
    }

    [Fact]
    public async Task AVideoOnItsOwn_CanBePutInAnExistingSeries()
    {
        var (cut, changed) = await RenderAsync();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(".tl-editing-series")));

        Assert.True(cut.Find("button:contains('Put in series')").HasAttribute("disabled"));
        cut.Find("select").Change(Course.ToString("D"));
        cut.Find("button:contains('Put in series')").Click();

        cut.WaitForAssertion(() => Assert.Single(_promotes));
        Assert.Equal((Course, (string?)null), _promotes[0]);
        cut.WaitForAssertion(() => Assert.Contains("Take out of series", cut.Markup));
        Assert.Single(changed);
    }

    [Fact]
    public async Task AVideoOnItsOwn_CanStartANewSeriesByName()
    {
        var (cut, _) = await RenderAsync();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(".tl-editing-series")));

        cut.Find("select").Change("new");
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("input")));
        Assert.True(cut.Find("button:contains('Put in series')").HasAttribute("disabled"));
        cut.Find("input").Input("Road trip");
        cut.Find("button:contains('Put in series')").Click();

        cut.WaitForAssertion(() => Assert.Single(_promotes));
        Assert.Equal(((Guid?)null, (string?)"Road trip"), _promotes[0]);
    }

    [Fact]
    public async Task AVideoInASeries_CanBeTakenOut_AndTheNoteSaysNoFileMoves()
    {
        _state = new VideoSeriesStateResponse { Supported = true, InSeries = true, SeriesTitle = "Course" };
        var (cut, changed) = await RenderAsync();
        cut.WaitForAssertion(() => Assert.Contains("In the series", cut.Markup));

        Assert.Contains("not moved or renamed", cut.Markup);
        cut.Find("button:contains('Take out of series')").Click();

        cut.WaitForAssertion(() => Assert.Equal(1, _removes));
        cut.WaitForAssertion(() => Assert.Contains("stands on its own", cut.Markup));
        Assert.Single(changed);
    }

    [Fact]
    public async Task AFailedSave_SaysSoAndKeepsTheChoice()
    {
        _saveSucceeds = false;
        var (cut, changed) = await RenderAsync();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(".tl-editing-series")));

        cut.Find("select").Change(Course.ToString("D"));
        cut.Find("button:contains('Put in series')").Click();

        cut.WaitForAssertion(() => Assert.Contains("could not be saved", cut.Find("[role='alert']").TextContent));
        Assert.Empty(changed);
    }

    [Fact]
    public async Task ForSomethingThatIsNotAVideo_NothingIsDrawn()
    {
        _state = new VideoSeriesStateResponse { Supported = false };
        var (cut, _) = await RenderAsync();

        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll(".tl-editing-series")));
    }

    [Fact]
    public async Task OnAPhone_EditingNeverTurnsOn_SoNothingIsDrawn()
    {
        await _device.InitialiseAsync(new DeviceContextSnapshot("mobile", "phone", true));
        var api = EngineApiClientStub.CreateDefault();
        Services.AddSingleton(api);
        var mode = new EditingModeContext(_device, new ItemFieldEditSession(api, _device)) { HasPermission = true };

        Assert.False(await mode.EnterAsync());
        var cut = Render<CascadingValue<EditingModeContext>>(parameters => parameters
            .Add(component => component.Value, mode)
            .Add(component => component.ChildContent, (RenderFragment)(builder => builder.OpenComponent<EditingVideoSeries>(0)) + (builder => builder.CloseComponent())));

        Assert.Empty(cut.FindAll(".tl-editing-series"));
    }
}
