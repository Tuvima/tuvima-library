using Bunit;
using MediaEngine.Contracts.Items;
using MediaEngine.Contracts.Metadata;
using MediaEngine.Web.Components.Details;
using MediaEngine.Web.Components.Shared;
using MediaEngine.Web.Services.Editing;
using MediaEngine.Web.Services.Integration;
using MediaEngine.Web.Services.Theming;
using MediaEngine.Web.Tests.Support;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace MediaEngine.Web.Tests;

/// <summary>The Explicit switch in Editing Mode: drawn only for videos, saves straight away, never hidden-by-accident.</summary>
public sealed class EditingExplicitMarkTests : AsyncBunitContext
{
    private static readonly Guid VideoId = Guid.Parse("11111111-aaaa-bbbb-cccc-000000000001");

    private readonly DeviceContextService _device = new(EngineApiClientStub.CreateDefault());
    private readonly List<(Guid Id, bool Value)> _saves = [];
    private bool _supported = true;

    public EditingExplicitMarkTests()
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
            stub.SetHandler(nameof(IEngineApiClient.GetItemExplicitAsync), _ =>
                Task.FromResult<ItemExplicitResponse?>(new ItemExplicitResponse { EntityId = VideoId, Supported = _supported }));
            stub.SetHandler(nameof(IEngineApiClient.SaveItemExplicitAsync), args =>
            {
                _saves.Add(((Guid)args![0]!, (bool)args[1]!));
                return Task.FromResult(true);
            });
        });
        Services.AddSingleton(api);
        var session = new ItemFieldEditSession(api, _device);
        var mode = new EditingModeContext(_device, session) { HasPermission = true };
        mode.Loader = () => session.LoadAsync(VideoId, "Movies", "movie", null);
        Assert.True(await mode.EnterAsync());
        return Render<CascadingValue<EditingModeContext>>(parameters => parameters
            .Add(component => component.Value, mode)
            .Add(component => component.ChildContent, (RenderFragment)(builder => builder.OpenComponent<EditingExplicitMark>(0)) + (builder => builder.CloseComponent())));
    }

    [Fact]
    public async Task ForAVideo_TheSwitchSavesStraightAway()
    {
        var cut = await RenderAsync();

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("input[role=switch]")));
        cut.Find("input[role=switch]").Change(true);

        cut.WaitForAssertion(() => Assert.Equal([(VideoId, true)], _saves));
    }

    [Fact]
    public async Task ForAnythingElse_NothingIsDrawn()
    {
        _supported = false;

        var cut = await RenderAsync();

        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll("input[role=switch]")));
        Assert.Empty(_saves);
    }
}
