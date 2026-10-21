using Bunit;
using MediaEngine.Contracts.Items;
using MediaEngine.Web.Components.Settings;
using MediaEngine.Web.Services.Integration;
using MediaEngine.Web.Tests.Support;
using Microsoft.Extensions.DependencyInjection;

namespace MediaEngine.Web.Tests;

public sealed class RecycleBinSectionTests : AsyncBunitContext
{
    private readonly List<RecycleBinItemDto> _items = [];
    private readonly List<Guid> _restored = [];

    public RecycleBinSectionTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddSingleton(EngineApiClientStub.Create(stub =>
        {
            stub.SetHandler(nameof(IEngineApiClient.GetRecycleBinAsync), _ =>
                Task.FromResult<IReadOnlyList<RecycleBinItemDto>?>(_items.ToList()));
            stub.SetHandler(nameof(IEngineApiClient.RestoreFromRecycleBinAsync), args =>
            {
                var id = (Guid)args![0]!;
                _restored.Add(id);
                _items.RemoveAll(item => item.Id == id);
                return Task.FromResult(true);
            });
        }));
    }

    [Fact]
    public void WhenTheBinIsEmpty_SaysSo_AndOffersNoEmptyButton()
    {
        var cut = Render<RecycleBinSection>();

        cut.WaitForAssertion(() => Assert.Contains("Nothing in the recycle bin.", cut.Markup, StringComparison.Ordinal));
        Assert.DoesNotContain("Empty recycle bin", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void EachFileIsListed_AndPuttingOneBackRemovesItFromTheList()
    {
        var item = new RecycleBinItemDto { Id = Guid.NewGuid(), FileName = "a.mp4", Title = "My trip", SizeBytes = 2048, DeletedAt = DateTimeOffset.UtcNow };
        _items.Add(item);
        var cut = Render<RecycleBinSection>();
        cut.WaitForAssertion(() => Assert.Contains("My trip", cut.Markup, StringComparison.Ordinal));

        cut.Find(".recycle-bin__row button").Click();

        cut.WaitForAssertion(() => Assert.Contains("Nothing in the recycle bin.", cut.Markup, StringComparison.Ordinal));
        Assert.Equal([item.Id], _restored);
    }
}
