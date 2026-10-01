using MediaEngine.Domain.Services;

namespace MediaEngine.Domain.Tests;

public sealed class LibraryTagCatalogTests
{
    [Fact]
    public void TryNormalizeDisplayValue_TrimsDeduplicatesAndSerializesTags()
    {
        var valid = LibraryTagCatalog.TryNormalizeDisplayValue(
            "  cozy, Mystery; cozy | comfort reads ",
            out var normalized,
            out var error);

        Assert.True(valid);
        Assert.Equal("cozy; Mystery; comfort reads", normalized);
        Assert.Null(error);
        Assert.Equal(["cozy", "Mystery", "comfort reads"], LibraryTagCatalog.ParseDisplayValue(normalized));
    }

    [Fact]
    public void TryNormalize_RejectsValuesOutsideSharedBounds()
    {
        Assert.False(LibraryTagCatalog.TryNormalize(
            Enumerable.Range(1, LibraryTagCatalog.MaximumTags + 1).Select(index => $"tag{index}"),
            out _, out _));
        Assert.False(LibraryTagCatalog.TryNormalize(
            [new string('x', LibraryTagCatalog.MaximumTagLength + 1)],
            out _, out _));
        Assert.False(LibraryTagCatalog.TryNormalize(["one;two"], out _, out _));
    }
}
