using MediaEngine.Api.Services.Details.Internals;
using MediaEngine.Domain.Configuration;

namespace MediaEngine.Api.Tests;

/// <summary>The Details panel names a source by its product ("Apple Books"), never by the Engine's connection name ("Apple API").</summary>
public sealed class ProviderSourceLinkNameTests
{
    private static ProviderConfiguration Apple() => new()
    {
        Name = "apple_api",
        DisplayName = "Apple API",
        ProductName = "Apple Books",
        ProductNameByKind = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["album"] = "Apple Music" },
        UiMetadata = new ProviderUiMetadata
        {
            ExternalLinks = new Dictionary<string, ProviderExternalLinkConfiguration>
            {
                ["apple_books_id"] = new() { Label = "", UrlTemplate = "https://books.apple.com/book/id{value}" },
            },
        },
    };

    [Fact]
    public void Resolve_NamesTheProductAndNotTheConnection()
    {
        var links = new ProviderSourceLinkResolver([Apple()]).Resolve(
            new Dictionary<string, string> { ["apple_books_id"] = "123" }, "Audiobooks");

        var link = Assert.Single(links);
        Assert.Equal("Apple Books", link.SourceName);
        Assert.Equal("View on Apple Books", link.Label);
    }
}
