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

/// <summary>Putting the videos of a series in order by hand in Editing Mode.</summary>
public sealed class EditingVideoOrderTests : AsyncBunitContext
{
    private static readonly Guid SeriesId = Guid.Parse("11111111-aaaa-bbbb-cccc-000000000001");
    private static readonly Guid Parent = Guid.Parse("22222222-aaaa-bbbb-cccc-000000000001");
    private static readonly Guid[] Videos = [Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()];

    private readonly DeviceContextService _device = new(EngineApiClientStub.CreateDefault());
    private readonly List<(Guid Parent, Guid[] Ordered)> _saves = [];
    private int _videoCount = 3;
    private bool _canRename;
    private int _renames;
    private bool _renameSucceeds = true;

    public EditingVideoOrderTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddSingleton(_device);
    }

    private async Task<IRenderedComponent<CascadingValue<EditingModeContext>>> RenderAsync()
    {
        await _device.InitialiseAsync(new DeviceContextSnapshot("web", "desktop", false, FinePointer: true, Hover: true));
        var api = EngineApiClientStub.Create(stub =>
        {
            stub.SetHandler(nameof(IEngineApiClient.GetMediaEditorContextAsync), _ => Task.FromResult<MediaEditorContextDto?>(EditingModeTestData.MovieContext()));
            stub.SetHandler(nameof(IEngineApiClient.GetItemEditorSuggestionsAsync), _ => Task.FromResult<IReadOnlyList<string>>([]));
            stub.SetHandler(nameof(IEngineApiClient.GetVideoOrderAsync), _ => Task.FromResult<VideoOrderResponse?>(new VideoOrderResponse
            {
                Groups =
                [
                    new VideoOrderGroupDto
                    {
                        ParentId = Parent,
                        Title = "Course",
                        Entries = Videos.Take(_videoCount).Select((id, i) => new VideoOrderEntryDto { Id = id, Title = $"Video {i + 1}" }).ToList(),
                        CanRenameFiles = _canRename,
                    },
                ],
            }));
            stub.SetHandler(nameof(IEngineApiClient.RenameVideoFilesToOrderAsync), _ =>
            {
                _renames++;
                return Task.FromResult<VideoRenameResponse?>(_renameSucceeds ? new VideoRenameResponse { Renamed = 3, Skipped = 0 } : null);
            });
            stub.SetHandler(nameof(IEngineApiClient.SaveVideoOrderAsync), args =>
            {
                _saves.Add(((Guid)args![1]!, ((IReadOnlyList<Guid>)args[2]!).ToArray()));
                return Task.FromResult(true);
            });
        });
        Services.AddSingleton(api);
        var session = new ItemFieldEditSession(api, _device);
        var mode = new EditingModeContext(_device, session) { HasPermission = true };
        mode.Loader = () => session.LoadAsync(SeriesId, "Movies", "movie", null);
        Assert.True(await mode.EnterAsync());
        return Render<CascadingValue<EditingModeContext>>(parameters => parameters
            .Add(component => component.Value, mode)
            .Add(component => component.ChildContent, (RenderFragment)(builder => builder.OpenComponent<EditingVideoOrder>(0)) + (builder => builder.CloseComponent())));
    }

    [Fact]
    public async Task MovingAVideoDown_SavesTheWholeNewOrder()
    {
        var cut = await RenderAsync();

        cut.WaitForAssertion(() => Assert.Equal(3, cut.FindAll(".tl-editing-order__row").Count));
        cut.Find("button[aria-label='Move Video 1 down']").Click();

        cut.WaitForAssertion(() => Assert.Single(_saves));
        Assert.Equal(Parent, _saves[0].Parent);
        Assert.Equal([Videos[1], Videos[0], Videos[2]], _saves[0].Ordered);
        Assert.Equal("Video 2", cut.FindAll(".tl-editing-order__name")[0].TextContent);
    }

    [Fact]
    public async Task TheFirstVideoCannotMoveUp()
    {
        var cut = await RenderAsync();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(".tl-editing-order__row")));
        Assert.True(cut.Find("button[aria-label='Move Video 1 up']").HasAttribute("disabled"));
    }

    [Fact]
    public async Task ASingleVideoShowsNoList()
    {
        _videoCount = 1;
        var alone = await RenderAsync();
        alone.WaitForAssertion(() => Assert.Empty(alone.FindAll(".tl-editing-order__row")));
    }

    [Fact]
    public async Task InAnAsIsFolder_NoRenameButtonIsOffered()
    {
        var cut = await RenderAsync();

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(".tl-editing-order__row")));
        Assert.DoesNotContain("Rename files", cut.Markup);
    }

    [Fact]
    public async Task InAManagedFolder_RenamingAsksFirst_ThenRenamesOnce()
    {
        _canRename = true;
        var cut = await RenderAsync();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(".tl-editing-order__row")));

        cut.Find("button:contains('Rename files to match this order')").Click();
        Assert.Equal(0, _renames);
        Assert.Contains("cannot be undone", cut.Markup);
        cut.Find("button:contains('Rename files')").Click();

        cut.WaitForAssertion(() => Assert.Contains("Renamed 3 files.", cut.Find("[role='status']").TextContent));
        Assert.Equal(1, _renames);
    }

    [Fact]
    public async Task CancellingTheRenameQuestion_RenamesNothing()
    {
        _canRename = true;
        var cut = await RenderAsync();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(".tl-editing-order__row")));

        cut.Find("button:contains('Rename files to match this order')").Click();
        cut.Find("button:contains('Cancel')").Click();

        Assert.Equal(0, _renames);
        Assert.NotEmpty(cut.FindAll("button:contains('Rename files to match this order')"));
    }

    [Fact]
    public async Task ARefusedRename_SaysNothingWasChanged()
    {
        _canRename = true;
        _renameSucceeds = false;
        var cut = await RenderAsync();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(".tl-editing-order__row")));

        cut.Find("button:contains('Rename files to match this order')").Click();
        cut.Find("button:contains('Rename files')").Click();

        cut.WaitForAssertion(() => Assert.Contains("nothing was changed", cut.Find("[role='alert']").TextContent));
    }
}
