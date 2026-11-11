using MediaEngine.Api.Services.Plugins.Setup;

namespace MediaEngine.Api.Tests;

/// <summary>The Connection row says "key ending 3f2a" (plugins screens v6 D61); a short key shows nothing, so four characters never give a key away.</summary>
public sealed class PluginKeyEndingTests
{
    [Theory]
    [InlineData("abcdef0123453f2a", "3f2a")]
    [InlineData("  abcdef0123453f2a  ", "3f2a")]
    public void ALongKey_ShowsItsLastFourCharacters(string key, string expected) =>
        Assert.Equal(expected, PluginPageModelService.EndingOf(key));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("short-key")]
    public void ANoKeyOrAShortOne_ShowsNothing(string? key) =>
        Assert.Null(PluginPageModelService.EndingOf(key));
}
