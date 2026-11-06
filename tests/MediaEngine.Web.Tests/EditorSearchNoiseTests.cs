using MediaEngine.Web.Services.Editing;

namespace MediaEngine.Web.Tests;

public sealed class EditorSearchNoiseTests
{
    [Fact]
    public void ReleaseNoiseIsLeftOutAndRemembered()
    {
        var result = EditorSearchNoise.Clean("Blade.Runner.2049.2160p.UHD.BluRay.x265-GROUP [YTS]");

        Assert.Equal("Blade Runner 2049 GROUP", result.Query.Replace("-", " ").Replace("  ", " ").Trim());
        Assert.Contains("2160p", result.LeftOut);
        Assert.Contains("x265", result.LeftOut);
        Assert.Contains("[YTS]", result.LeftOut);
        Assert.True(result.HasLeftOut);
    }

    [Fact]
    public void ACleanTitleIsUntouched()
    {
        var result = EditorSearchNoise.Clean("The Expanse");

        Assert.Equal("The Expanse", result.Query);
        Assert.False(result.HasLeftOut);
    }

    [Fact]
    public void ASearchIsNeverStrippedToNothing()
    {
        var result = EditorSearchNoise.Clean("1080p");

        Assert.Equal("1080p", result.Query);
        Assert.Empty(result.LeftOut);
    }
}
