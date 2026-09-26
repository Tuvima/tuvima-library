using Bunit;
using MediaEngine.Contracts.LocalAssets;
using MediaEngine.Web.Components.View;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;

namespace MediaEngine.Web.Tests;

public sealed class PlacesHistogramTests : AsyncBunitContext
{
    public PlacesHistogramTests() => Services.AddMudServices();
    private static DateTimeOffset Date(int year, int month = 1) => new(year, month, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void SparseOlderBucketsRemainInteractiveBesideDenseRecentData()
    {
        ViewAtlasTimelineBucketDto[] buckets = [new(Date(2019), 1, 1, 0, Date(2019, 2)),
            new(Date(2024, 6), 2, 1, 1, Date(2024, 7)), new(Date(2026, 8), 0, 0, 0, Date(2026, 9)),
            new(Date(2026, 9), 2000, 1900, 100, Date(2026, 10))];
        var cut = Render<ViewPlacesTimeline>(p => p.Add(c => c.Buckets, buckets)
            .Add(c => c.EarliestAt, Date(2019)).Add(c => c.LatestAt, Date(2026, 10)));
        var bars = cut.FindAll(".places-timeline__histogram button");
        Assert.Equal(3, bars.Count);
        Assert.Contains("2019", bars[0].GetAttribute("aria-label"));
        bars[0].MouseEnter();
        Assert.Contains("2019", cut.Find("[role=status]").TextContent);
        Assert.Contains("1 items", cut.Find("[role=status]").TextContent);
        Assert.Equal("true", cut.FindAll(".places-timeline__controls button")[1].GetAttribute("aria-pressed"));
    }

    [Fact]
    public void ShortDomainsExposeMonthsAndLongDomainsUseSparseYears()
    {
        var scale = new PlacesTimeScale(Date(2024), Date(2026, 12).AddMonths(1).AddDays(-1));
        Assert.Equal(36, scale.Ticks().Count());
        Assert.Contains(scale.Ticks(), index => scale.DateAt(index) == Date(2025, 2));
        var longScale = new PlacesTimeScale(Date(1990), Date(2026));
        Assert.InRange(longScale.Ticks().Count(), 2, 6);
    }

    [Fact]
    public void ResponsiveBarAndLabelRulesPreserveVisibilityWithoutWideColumns()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "MediaEngine.slnx"))) root = root.Parent;
        var css = File.ReadAllText(Path.Combine(root!.FullName, "src/MediaEngine.Web/Components/View/ViewPlacesTimeline.razor.css"));
        Assert.Contains("width:clamp(2px,calc(100% - 2px),6px)", css);
        Assert.Contains("height:max(3px,var(--bar-height))", css);
        Assert.Contains("@container (min-width:480px)", css);
        Assert.Contains("@container (min-width:720px)", css);
        Assert.Contains("@container (min-width:1100px)", css);
        Assert.Contains("@container (max-width:450px)", css);
    }
}
