using MediaEngine.Providers.Services;
using Xunit;

namespace MediaEngine.Providers.Tests;

public sealed class PersonPlaceholderNameTests
{
    [Theory]
    [InlineData("Various Artists")]
    [InlineData("  various artists ")]
    [InlineData("Unknown Artist")]
    [InlineData("Original Soundtrack")]
    public void StandInNames_AreNotSearchedForAsPeople(string name)
    {
        Assert.True(PersonReconciliationService.IsPlaceholderName(name));
    }

    [Theory]
    [InlineData("Neil Gaiman")]
    [InlineData("The Various")]
    [InlineData("")]
    [InlineData(null)]
    public void RealNamesAndEmptyValues_AreSearchedAsUsual(string? name)
    {
        Assert.False(PersonReconciliationService.IsPlaceholderName(name));
    }
}
