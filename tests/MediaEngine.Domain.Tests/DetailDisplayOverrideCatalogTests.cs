using MediaEngine.Domain.Services;

namespace MediaEngine.Domain.Tests;

public sealed class DetailDisplayOverrideCatalogTests
{
    [Fact]
    public void Catalog_ContainsOnlyBoundedPresentationFields()
    {
        Assert.Contains("title", DetailDisplayOverrideCatalog.AllowedKeys);
        Assert.Contains("air_date", DetailDisplayOverrideCatalog.AllowedKeys);
        Assert.Contains("runtime", DetailDisplayOverrideCatalog.AllowedKeys);
        Assert.Contains("custom_tags", DetailDisplayOverrideCatalog.AllowedKeys);
        Assert.DoesNotContain("author", DetailDisplayOverrideCatalog.AllowedKeys);
        Assert.DoesNotContain("series_position", DetailDisplayOverrideCatalog.AllowedKeys);
        Assert.DoesNotContain("wikidata_qid", DetailDisplayOverrideCatalog.AllowedKeys);
        Assert.DoesNotContain("episode_count", DetailDisplayOverrideCatalog.AllowedKeys);
    }

    [Theory]
    [InlineData("year", "2026", true, "2026")]
    [InlineData("air_date", "2026-03-09", true, "2026-03-09")]
    [InlineData("runtime", "142", true, "142")]
    [InlineData("rating", "8.25", true, "8.25")]
    [InlineData("year", "twenty", false, "twenty")]
    [InlineData("air_date", "2026-15-40", false, "2026-15-40")]
    [InlineData("runtime", "-1", false, "-1")]
    [InlineData("rating", "11", false, "11")]
    [InlineData("author", "A Person", false, "A Person")]
    public void TryValidateValue_EnforcesScalarValueShape(
        string key,
        string value,
        bool expectedValid,
        string expectedNormalized)
    {
        var valid = DetailDisplayOverrideCatalog.TryValidateValue(key, value, out var normalized, out _);

        Assert.Equal(expectedValid, valid);
        Assert.Equal(expectedNormalized, normalized);
    }

    [Fact]
    public void TryValidateValue_AllowsBlankValueForRevert()
    {
        var valid = DetailDisplayOverrideCatalog.TryValidateValue("title", "   ", out var normalized, out var error);

        Assert.True(valid);
        Assert.Equal(string.Empty, normalized);
        Assert.Null(error);
    }

    [Fact]
    public void TryValidateValue_NormalizesAndBoundsCustomTags()
    {
        var valid = DetailDisplayOverrideCatalog.TryValidateValue(
            "custom_tags",
            "  cozy, Mystery; cozy | comfort reads ",
            out var normalized,
            out var error);

        Assert.True(valid);
        Assert.Equal("cozy; Mystery; comfort reads", normalized);
        Assert.Null(error);

        var tooMany = string.Join(';', Enumerable.Range(1, LibraryTagCatalog.MaximumTags + 1).Select(index => $"tag{index}"));
        Assert.False(DetailDisplayOverrideCatalog.TryValidateValue("custom_tags", tooMany, out _, out _));
        Assert.False(DetailDisplayOverrideCatalog.TryValidateValue("custom_tags", new string('x', LibraryTagCatalog.MaximumTagLength + 1), out _, out _));
    }

    [Fact]
    public void ApplyChanges_SavesValidValueAndBlankValueRemovesItForRevert()
    {
        var current = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["title"] = "Temporary title",
            ["description"] = "Keep this",
            ["custom_tags"] = "local tag",
        };

        var updated = DetailDisplayOverrideCatalog.ApplyChanges(current,
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["title"] = "Revised title",
                ["description"] = "   ",
                ["custom_tags"] = "   ",
            });

        Assert.Equal("Revised title", current["title"]);
        Assert.False(current.ContainsKey("description"));
        Assert.True(current.ContainsKey("custom_tags"));
        Assert.Equal(string.Empty, current["custom_tags"]);
        Assert.Equal(3, updated.Count);
        Assert.Contains("title", updated);
        Assert.Contains("description", updated);
        Assert.Contains("custom_tags", updated);
    }

    [Fact]
    public void ApplyChanges_RejectsUnsupportedOrMalformedValues()
    {
        var current = new Dictionary<string, string>();

        Assert.Throws<ArgumentException>(() => DetailDisplayOverrideCatalog.ApplyChanges(current,
            new Dictionary<string, string> { ["series_position"] = "4" }));
        Assert.Throws<ArgumentException>(() => DetailDisplayOverrideCatalog.ApplyChanges(current,
            new Dictionary<string, string> { ["year"] = "next year" }));
        Assert.Empty(current);
    }

    [Fact]
    public void ApplyChanges_CanonicalizesOverrideKeysForJsonPathReads()
    {
        var current = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Custom_Tags"] = "old tag",
        };

        DetailDisplayOverrideCatalog.ApplyChanges(current,
            new Dictionary<string, string> { ["CUSTOM_TAGS"] = "new tag" });

        var item = Assert.Single(current);
        Assert.Equal("custom_tags", item.Key);
        Assert.Equal("new tag", item.Value);
    }
}
