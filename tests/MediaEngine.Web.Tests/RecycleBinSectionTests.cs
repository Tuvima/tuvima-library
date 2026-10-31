using Bunit;
using MediaEngine.Contracts.Items;
using MediaEngine.Web.Components.Settings;
using MediaEngine.Web.Services.Integration;
using MediaEngine.Web.Tests.Support;
using Microsoft.Extensions.DependencyInjection;

namespace MediaEngine.Web.Tests;

public sealed class RecycleBinSectionTests : AsyncBunitContext
{
    private RecycleBinSummaryDto _summary = new() { CanManage = true };
    private readonly List<Guid> _restored = [];
    private readonly List<Guid> _asked = [];
    private readonly List<string?> _scopes = [];

    public RecycleBinSectionTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddSingleton(EngineApiClientStub.Create(stub =>
        {
            stub.SetHandler(nameof(IEngineApiClient.GetRecycleBinSummaryAsync), args =>
            {
                _scopes.Add((string?)args![0]);
                return Task.FromResult<RecycleBinSummaryDto?>(_summary);
            });
            stub.SetHandler(nameof(IEngineApiClient.RestoreFromRecycleBinAsync), args =>
            {
                var id = (Guid)args![0]!;
                _restored.Add(id);
                _summary = new RecycleBinSummaryDto { CanManage = true };
                return Task.FromResult(true);
            });
            stub.SetHandler(nameof(IEngineApiClient.AskToKeepFromRecycleBinAsync), args =>
            {
                _asked.Add((Guid)args![0]!);
                return Task.FromResult(true);
            });
        }));
    }

    private static RecycleBinListItemDto Item(string title, int? leavesIn = 90) => new()
    {
        Id = Guid.NewGuid(),
        Title = title,
        MediaType = "Movies",
        SizeBytes = 2048,
        DeletedAt = DateTimeOffset.UtcNow,
        DeletedBy = "Shy",
        LeavesInDays = leavesIn,
    };

    [Fact]
    public void WhenTheBinIsEmpty_SaysSo_AndOffersNoEmptyButton()
    {
        var cut = Render<RecycleBinSection>();

        cut.WaitForAssertion(() => Assert.Contains("Nothing in the recycle bin.", cut.Markup, StringComparison.Ordinal));
        Assert.DoesNotContain("Empty recycle bin", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void AnAdministratorSeesWhoDeletedAndWhenItLeaves_AndPuttingOneBackClearsTheList()
    {
        var item = Item("My trip", leavesIn: 3);
        _summary = new RecycleBinSummaryDto { CanManage = true, TotalItems = 1, TotalBytes = 2048, Items = [item] };
        var cut = Render<RecycleBinSection>();
        cut.WaitForAssertion(() => Assert.Contains("My trip", cut.Markup, StringComparison.Ordinal));
        Assert.Contains("by Shy", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("leaves in 3 days", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("Delete now", cut.Markup, StringComparison.Ordinal);

        cut.FindAll(".recycle-bin__row button").First(button => button.TextContent.Contains("Put back")).Click();

        cut.WaitForAssertion(() => Assert.Contains("Nothing in the recycle bin.", cut.Markup, StringComparison.Ordinal));
        Assert.Equal([item.Id], _restored);
    }

    [Fact]
    public void AdministratorsAreWarnedAboutItemsLeavingSoon()
    {
        _summary = new RecycleBinSummaryDto
        {
            CanManage = true,
            TotalItems = 2,
            TotalBytes = 4096,
            Items = [Item("a", 1), Item("b", 5)],
            LeavingSoonItems = 2,
            LeavingSoonBytes = 4096,
            LeavingSoonDays = 1,
        };

        var cut = Render<RecycleBinSection>();

        cut.WaitForAssertion(() => Assert.Contains("will be deleted for good tomorrow", cut.Markup, StringComparison.Ordinal));
    }

    [Fact]
    public void AnotherProfileCanAskToKeep_ButCannotRestoreOrEmpty_AndSeesOnlyOneLineForTheRest()
    {
        var item = Item("Family movie");
        _summary = new RecycleBinSummaryDto
        {
            CanManage = false,
            TotalItems = 4,
            TotalBytes = 10_000,
            Items = [item],
            OtherItems = 3,
            OtherBytes = 8000,
        };
        var cut = Render<RecycleBinSection>();
        cut.WaitForAssertion(() => Assert.Contains("Family movie", cut.Markup, StringComparison.Ordinal));
        Assert.DoesNotContain("Put back", cut.Markup, StringComparison.Ordinal);
        Assert.DoesNotContain("Empty recycle bin", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("Other media: 3 items", cut.Markup, StringComparison.Ordinal);

        cut.FindAll(".recycle-bin__row button").Single(button => button.TextContent.Contains("Ask to keep")).Click();

        cut.WaitForAssertion(() => Assert.Contains("Asked.", cut.Markup, StringComparison.Ordinal));
        Assert.Equal([item.Id], _asked);
    }

    [Fact]
    public void OpenedFromABrowsePage_ItIsNarrowedToThatType_AndShowAllWidensItInPlace()
    {
        _summary = new RecycleBinSummaryDto { CanManage = true, TotalItems = 1, Items = [Item("Dune")] };

        var cut = Render<RecycleBinSection>(parameters => parameters.Add(section => section.MediaType, "Movies"));

        cut.WaitForAssertion(() => Assert.Contains("Showing: Movies", cut.Markup, StringComparison.Ordinal));
        Assert.Equal("Movies", _scopes.Last());

        cut.FindAll(".recycle-bin__filter button").Single().Click();

        cut.WaitForAssertion(() => Assert.Contains("Showing: All media", cut.Markup, StringComparison.Ordinal));
        Assert.Null(_scopes.Last());
    }
}
