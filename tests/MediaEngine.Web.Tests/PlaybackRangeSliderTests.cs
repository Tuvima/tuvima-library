using Bunit;
using MediaEngine.Web.Components.Shared;

namespace MediaEngine.Web.Tests;

public sealed class PlaybackRangeSliderTests : BunitContext
{
    [Fact]
    public void ReadOnlyBookProgressUsesSharedRailWithoutSeekInput()
    {
        var cut = Render<PlaybackRangeSlider>(parameters => parameters
            .Add(component => component.Min, 0)
            .Add(component => component.Max, 100)
            .Add(component => component.Value, 42)
            .Add(component => component.ReadOnly, true)
            .Add(component => component.ShowTicks, false)
            .Add(component => component.AriaLabel, "Book progress"));

        var progress = cut.Find("[role='progressbar']");
        Assert.Equal("Book progress", progress.GetAttribute("aria-label"));
        Assert.Equal("42", progress.GetAttribute("aria-valuenow"));
        Assert.Contains("42%", progress.GetAttribute("style"));
        Assert.Empty(cut.FindAll("input[type='range']"));
    }
}
