using MediaEngine.Domain.Services;

namespace MediaEngine.Domain.Tests;

/// <summary>Batch editing of library tags (details overlay spec D8): Add keeps what is there, Remove takes named tags away, Replace sets exactly the new ones.</summary>
public sealed class LibraryTagBatchChangeTests
{
    [Theory]
    [InlineData("Road Trip; Live", "Favourite", "add", "Road Trip; Live; Favourite")]
    [InlineData("Road Trip; Live", "live", "add", "Road Trip; Live")]
    [InlineData("Road Trip; Live", "live", "remove", "Road Trip")]
    [InlineData("Road Trip", "Not There", "remove", "Road Trip")]
    [InlineData("Road Trip; Live", "Favourite", "replace", "Favourite")]
    [InlineData(null, "Favourite", "add", "Favourite")]
    [InlineData("Road Trip", "Road Trip", "remove", "")]
    public void ApplyingAChange_GivesTheExpectedTags(string? existing, string change, string mode, string expected)
    {
        Assert.True(LibraryTagCatalog.TryApplyDisplayChange(existing, change, mode, out var result, out var error), error);
        Assert.Equal(expected, result);
    }

    [Fact]
    public void AnUnknownMode_IsRefused()
    {
        Assert.False(LibraryTagCatalog.TryApplyDisplayChange("A", "B", "swap", out _, out var error));
        Assert.Contains("swap", error);
    }
}
