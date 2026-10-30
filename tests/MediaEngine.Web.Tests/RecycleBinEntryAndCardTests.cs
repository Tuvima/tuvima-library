using Bunit;
using MediaEngine.Contracts.Items;
using MediaEngine.Web.Components.Settings;
using MediaEngine.Web.Components.Shared;
using MediaEngine.Web.Services.Integration;
using MediaEngine.Web.Tests.Support;
using Microsoft.Extensions.DependencyInjection;

namespace MediaEngine.Web.Tests;

public sealed class RecycleBinEntryAndCardTests : AsyncBunitContext
{
    private RecycleBinSummaryDto? _summary;
    private readonly List<string?> _scopes = [];
    private readonly List<Guid> _restored = [];

    public RecycleBinEntryAndCardTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddSingleton(EngineApiClientStub.Create(stub =>
        {
            stub.SetHandler(nameof(IEngineApiClient.GetRecycleBinSummaryAsync), args =>
            {
                _scopes.Add((string?)args![0]);
                return Task.FromResult(_summary);
            });
            stub.SetHandler(nameof(IEngineApiClient.RestoreFromRecycleBinAsync), args =>
            {
                _restored.Add((Guid)args![0]!);
                return Task.FromResult(true);
            });
        }));
    }

    [Fact]
    public void TheEntry_ShowsCountAndSizeForThePagesTypeAndLinksToTheBinNarrowedToIt()
    {
        _summary = new RecycleBinSummaryDto { TotalItems = 38, TotalBytes = 24L * 1024 * 1024 * 1024 };

        var cut = Render<RecycleBinEntry>(parameters => parameters.Add(entry => entry.Tab, "movies"));

        cut.WaitForAssertion(() => Assert.Contains("Recycle bin · 38 items", cut.Markup, StringComparison.Ordinal));
        Assert.Equal("/recycle-bin?type=Movies", cut.Find("a.recycle-bin-entry").GetAttribute("href"));
        Assert.Equal("Movies", _scopes.Single());
    }

    [Fact]
    public void TheEntry_IsHiddenWhenTheBinHoldsNothingForThatType_OrTheTabHasNoBin()
    {
        _summary = new RecycleBinSummaryDto { TotalItems = 0 };
        var empty = Render<RecycleBinEntry>(parameters => parameters.Add(entry => entry.Tab, "movies"));
        var unrelated = Render<RecycleBinEntry>(parameters => parameters.Add(entry => entry.Tab, "playlists"));

        Assert.Empty(empty.FindAll("a"));
        Assert.Empty(unrelated.FindAll("a"));
        Assert.Single(_scopes);
    }

    [Fact]
    public void TheLeavingSoonCard_ToldAdministratorsWhatIsLeaving_AndKeepThemPutsEachBack()
    {
        var soon = new RecycleBinListItemDto { Id = Guid.NewGuid(), Title = "Soon", LeavesInDays = 2 };
        var later = new RecycleBinListItemDto { Id = Guid.NewGuid(), Title = "Later", LeavesInDays = 60 };
        _summary = new RecycleBinSummaryDto
        {
            CanManage = true,
            TotalItems = 2,
            Items = [soon, later],
            LeavingSoonItems = 1,
            LeavingSoonBytes = 2048,
            LeavingSoonDays = 2,
            LeavingSoonWindowDays = 7,
        };

        var cut = Render<RecycleBinLeavingSoonCard>();

        cut.WaitForAssertion(() => Assert.Contains("1 item", cut.Markup, StringComparison.Ordinal));
        Assert.Contains("in 2 days", cut.Markup, StringComparison.Ordinal);
        Assert.Equal("/recycle-bin", cut.Find("a.leaving-soon__review").GetAttribute("href"));

        cut.Find(".leaving-soon__actions button").Click();

        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll(".leaving-soon")));
        Assert.Equal([soon.Id], _restored);
    }

    [Fact]
    public void TheLeavingSoonCard_ShowsNothingToAProfileThatCannotManage_OrWhenNothingIsLeaving()
    {
        _summary = new RecycleBinSummaryDto { CanManage = false, TotalItems = 3, LeavingSoonItems = 3 };
        Assert.Empty(Render<RecycleBinLeavingSoonCard>().FindAll(".leaving-soon"));

        _summary = new RecycleBinSummaryDto { CanManage = true, TotalItems = 3 };
        Assert.Empty(Render<RecycleBinLeavingSoonCard>().FindAll(".leaving-soon"));
    }
}
