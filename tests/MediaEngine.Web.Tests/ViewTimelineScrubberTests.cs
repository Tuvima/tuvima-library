using Bunit;
using MediaEngine.Contracts.LocalAssets;
using MediaEngine.Web.Components.Pages;

namespace MediaEngine.Web.Tests;

public sealed class ViewTimelineScrubberTests : AsyncBunitContext
{
    [Fact]
    public void OnlyActiveYearsPopulatedMonthsAppearAndMonthClickJumps()
    {
        ViewTimelineBucketDto? jumped = null;
        ViewTimelineBucketDto[] buckets = [Bucket(2026, 9), Bucket(2026, 3), Bucket(2026, 1, 0), Bucket(2025, 7)];
        var cut = Render<ViewTimelineScrubber>(p => p.Add(c => c.Buckets, buckets)
            .Add(c => c.ActiveYear, 2026).Add(c => c.ActiveMonth, 9)
            .Add(c => c.OnJump, value => jumped = value));
        Assert.Single(cut.FindAll(".view-timeline-scrubber__months"));
        Assert.Equal(2, cut.FindAll(".view-timeline-scrubber__months button").Count);
        cut.FindAll(".view-timeline-scrubber__months button")[1].Click();
        Assert.Equal(Bucket(2026, 3), jumped);
        cut.Render(p => p.Add(c => c.ActiveYear, 2025).Add(c => c.ActiveMonth, 7));
        Assert.Single(cut.FindAll(".view-timeline-scrubber__months button"));
        Assert.Equal("Months in 2025", cut.Find(".view-timeline-scrubber__months").GetAttribute("aria-label"));
        Assert.Equal("date", cut.Find(".view-timeline-scrubber__months button").GetAttribute("aria-current"));
    }

    private static ViewTimelineBucketDto Bucket(int year, int month, int count = 3)
        => new(year, month, count, new(year, month, 1, 0, 0, 0, TimeSpan.Zero), new(year, month, 2, 0, 0, 0, TimeSpan.Zero));

    [Fact]
    public void ResponsiveMonthsStayInTheRailAndUseTouchSizedHorizontalControls()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "MediaEngine.slnx")))
        {
            root = root.Parent;
        }
        var css = File.ReadAllText(Path.Combine(root!.FullName, "src/MediaEngine.Web/Components/Shared/AppTimelineNavigator.razor.css"));
        Assert.Contains("flex:none;flex-direction:column;align-items:stretch", css);
        Assert.Contains("max-width:calc(100vw - 2rem)", css);
        Assert.Contains("min-width:2.75rem;min-height:2.75rem", css);
        Assert.DoesNotContain("visibility:hidden", css);
    }
}
