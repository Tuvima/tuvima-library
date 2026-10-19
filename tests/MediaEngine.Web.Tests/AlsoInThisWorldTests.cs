using Bunit;
using MediaEngine.Contracts.Details;
using MediaEngine.Web.Components.Details;
using MediaEngine.Web.Services.Integration;
using MediaEngine.Web.Tests.Support;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace MediaEngine.Web.Tests;

/// <summary>
/// A Series shows its numbered run (decimals as the source gives them) and, apart from it, "Also in this world": the
/// supplementary, collected-content and broader-context members the Engine lists for the same Series. The panel uses the
/// Engine's order only.
/// </summary>
public sealed class AlsoInThisWorldTests : AsyncBunitContext
{
    private static readonly Guid Series = Guid.Parse("50000000-0000-0000-0000-000000000005");

    public AlsoInThisWorldTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddLogging();
        Services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        Services.AddSingleton(EngineApiClientStub.CreateDefault());
        Services.AddScoped<DashboardSessionAccessor>();
        Services.AddScoped<ActiveProfileSessionService>();
        Services.AddScoped<UniverseStateContainer>();
        Services.AddScoped<UIOrchestratorService>();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void TheAlsoRow_ListsTheEnginesAlsoMembers_ApartFromTheNumberedRun(bool detailSurface)
    {
        var cut = Render<SequencePlacementPanel>(p => p
            .Add(c => c.Placement, Placement())
            .Add(c => c.DetailSurface, detailSurface));

        Assert.Contains("Also in this world", cut.Markup);

        var rows = cut.FindAll(detailSurface ? ".tl-series-detail__strip" : ".tl-series-strip");
        Assert.Equal(2, rows.Count);
        var runText = rows[0].TextContent;
        var alsoText = rows[1].TextContent;

        Assert.Contains("Leviathan Wakes", runText);
        Assert.Contains("Half Moon", runText);
        Assert.Contains("Caliban's War", runText);
        Assert.DoesNotContain("The Churn", runText);
        Assert.DoesNotContain("Gods of Risk", runText);

        Assert.Contains("The Churn", alsoText);
        Assert.Contains("Gods of Risk", alsoText);
        Assert.DoesNotContain("Leviathan Wakes", alsoText);
    }

    [Fact]
    public void TheNumberedRun_ShowsDecimalsAsGiven_AndAMemberWithoutAPositionShowsNoMadeUpNumber()
    {
        var cut = Render<SequencePlacementPanel>(p => p
            .Add(c => c.Placement, Placement(withUnnumbered: true))
            .Add(c => c.DetailSurface, false));

        var nodes = cut.FindAll(".tl-series-strip")[0].QuerySelectorAll(".tl-series-item__node").Select(n => n.TextContent.Trim()).ToList();

        Assert.Equal(["1", "1.5", "2", "•"], nodes);
        Assert.DoesNotContain("?", nodes);
    }

    [Fact]
    public void Connectors_JoinMembersThatSitNextToEachOtherInTheEnginesOrder_IncludingDecimalNeighbours()
    {
        var cut = Render<SequencePlacementPanel>(p => p
            .Add(c => c.Placement, Placement())
            .Add(c => c.DetailSurface, false));

        // 1 -> 1.5 -> 2 are consecutive in the Engine's list, so both gaps are joined. A parsed-label rule (a step of
        // exactly one) would have joined neither.
        Assert.Equal(2, cut.FindAll(".tl-series-strip")[0].QuerySelectorAll(".tl-series-item__connector").Length);
        Assert.Empty(cut.FindAll(".tl-series-strip")[1].QuerySelectorAll(".tl-series-item__connector"));
    }

    [Fact]
    public void AMember_OfAlsoInThisWorld_CanBeTheCurrentPage_WithoutBeingNumbered()
    {
        var placement = Placement(currentIsAlso: true);
        var cut = Render<SequencePlacementPanel>(p => p
            .Add(c => c.Placement, placement)
            .Add(c => c.DetailSurface, false));

        var current = Assert.Single(cut.FindAll(".tl-series-item.is-current"));
        Assert.Contains("The Churn", current.TextContent);
        Assert.Contains("The Churn", cut.FindAll(".tl-series-strip")[1].TextContent);
        Assert.DoesNotContain("Also in this world", string.Join(' ', cut.FindAll(".tl-series-strip")[0].QuerySelectorAll(".tl-series-item").Select(i => i.TextContent)));
    }

    [Fact]
    public void WithoutAnyAlsoMembers_NoAlsoRowIsShown()
    {
        var plain = Placement();
        var withoutAlso = new SequencePlacementViewModel
        {
            ContainerId = plain.ContainerId,
            SelectedContainerId = plain.SelectedContainerId,
            ContainerTitle = plain.ContainerTitle,
            ContainerLabel = plain.ContainerLabel,
            ItemLabel = plain.ItemLabel,
            ItemPluralLabel = plain.ItemPluralLabel,
            CurrentItem = plain.CurrentItem,
            OrderedItems = plain.OrderedItems,
            Groups = plain.Groups,
        };

        var cut = Render<SequencePlacementPanel>(p => p
            .Add(c => c.Placement, withoutAlso)
            .Add(c => c.DetailSurface, false));

        Assert.DoesNotContain("Also in this world", cut.Markup);
        Assert.Single(cut.FindAll(".tl-series-strip"));
    }

    private static SequencePlacementViewModel Placement(bool withUnnumbered = false, bool currentIsAlso = false)
    {
        var run = new List<SequenceItemViewModel>
        {
            Book("1", "Leviathan Wakes", "1", 1, current: !currentIsAlso),
            Book("2", "Half Moon", "1.5", 1.5),
            Book("3", "Caliban's War", "2", 2),
        };
        if (withUnnumbered)
        {
            run.Add(Book("4", "Unnumbered Extra", null, null));
        }

        var also = new List<SequenceItemViewModel>
        {
            Also("5", "The Churn", "0.2", 0.2, SeriesScope.CollectedContent, current: currentIsAlso),
            Also("6", "Gods of Risk", "2.5", 2.5, SeriesScope.CollectedContent),
        };

        return new SequencePlacementViewModel
        {
            ContainerId = Series.ToString("D"),
            SelectedContainerId = Series.ToString("D"),
            ContainerTitle = "The Expanse",
            ContainerLabel = "Series",
            ItemLabel = "Book",
            ItemPluralLabel = "Books",
            CurrentItem = currentIsAlso ? also[0] : run[0],
            OrderedItems = run,
            AlsoInThisWorld = also,
            Groups =
            [
                new SequenceGroupViewModel
                {
                    Key = "main-sequence",
                    Title = "Main Series",
                    OwnedCount = run.Count,
                    TotalKnownItems = run.Count,
                    Items = run,
                },
            ],
        };
    }

    private static class SeriesScope
    {
        public const string CollectedContent = "CollectedContent";
    }

    private static SequenceItemViewModel Book(string id, string title, string? label, double? sort, bool current = false) => new()
    {
        Id = $"6{id}000000-0000-0000-0000-000000000000",
        EntityType = DetailEntityType.Book,
        Title = title,
        GroupKey = "main-sequence",
        GroupTitle = "Main Series",
        PositionLabel = label,
        PositionNumber = sort is { } whole && whole % 1 == 0 ? (int)whole : null,
        PositionSort = sort,
        IsOwned = true,
        IsCurrent = current,
    };

    private static SequenceItemViewModel Also(string id, string title, string label, double sort, string scope, bool current = false) => new()
    {
        Id = $"7{id}000000-0000-0000-0000-000000000000",
        EntityType = DetailEntityType.Book,
        Title = title,
        GroupKey = "collected-content",
        GroupTitle = "Collected Content",
        MembershipScope = scope,
        PositionLabel = label,
        PositionSort = sort,
        IsOwned = true,
        IsCurrent = current,
    };
}
