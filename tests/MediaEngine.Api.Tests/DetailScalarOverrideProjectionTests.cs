using MediaEngine.Api.Services.Details.Internals;
using MediaEngine.Domain.Models;

namespace MediaEngine.Api.Tests;

public sealed class DetailScalarOverrideProjectionTests
{
    [Fact]
    public void ApplyDetailScalarOverrides_ProjectsPresentationScalarsWithoutChangingCanonicalIdentityOrStructure()
    {
        var original = new LibraryItemDetail
        {
            EntityId = Guid.NewGuid(),
            Title = "Canonical title",
            Year = "2001",
            ReleaseDate = "2001-01-02",
            Runtime = "100",
            Rating = "7.0",
            Language = "en",
            Genre = "Drama",
        };
        IReadOnlyDictionary<string, string> canonical = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["title"] = "Canonical title",
            ["series_position"] = "3",
            ["episode_count"] = "12",
            ["custom_tags"] = "canonical tag",
        };
        IReadOnlyDictionary<string, string> overrides = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["year"] = "2024",
            ["air_date"] = "2024-09-13",
            ["runtime"] = "143",
            ["rating"] = "8.5",
            ["content_rating"] = "PG-13",
            ["language"] = "fr",
            ["genre"] = "Mystery|Drama",
            ["custom_tags"] = "library tag; shared favorite",
        };

        var result = DetailCompositionOrchestrator.ApplyDetailScalarOverrides(original, canonical, overrides);

        Assert.Equal("Canonical title", result.Detail.Title);
        Assert.Equal("2024", result.Detail.Year);
        Assert.Equal("2024-09-13", result.Detail.ReleaseDate);
        Assert.Equal("143", result.Detail.Runtime);
        Assert.Equal("8.5", result.Detail.Rating);
        Assert.Equal("fr", result.Detail.Language);
        Assert.Equal("Mystery|Drama", result.Detail.Genre);
        Assert.Equal("PG-13", result.Values["content_rating"]);
        Assert.Equal("3", result.Values["series_position"]);
        Assert.Equal("12", result.Values["episode_count"]);
        Assert.Equal("library tag; shared favorite", result.Values["custom_tags"]);
        Assert.Equal("Canonical title", result.Values["title"]);
        Assert.Equal("2001", original.Year);
        Assert.Equal("100", original.Runtime);
    }

    [Fact]
    public void ApplyDetailScalarOverrides_UsesCanonicalFactsWhenNoOverrideExists()
    {
        var original = new LibraryItemDetail { Year = "2001", Runtime = "100", Genre = "Drama" };

        var result = DetailCompositionOrchestrator.ApplyDetailScalarOverrides(
            original,
            new Dictionary<string, string> { ["custom_tags"] = "canonical tag" },
            new Dictionary<string, string>());

        Assert.Equal("2001", result.Detail.Year);
        Assert.Equal("100", result.Detail.Runtime);
        Assert.Equal("Drama", result.Detail.Genre);
        Assert.Equal("canonical tag", result.Values["custom_tags"]);

        var cleared = DetailCompositionOrchestrator.ApplyDetailScalarOverrides(
            original,
            new Dictionary<string, string> { ["custom_tags"] = "canonical tag" },
            new Dictionary<string, string> { ["custom_tags"] = string.Empty });
        Assert.Equal(string.Empty, cleared.Values["custom_tags"]);
    }
}
