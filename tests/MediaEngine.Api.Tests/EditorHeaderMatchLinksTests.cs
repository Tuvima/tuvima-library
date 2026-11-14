using MediaEngine.Api.Services.EditSession;
using MediaEngine.Contracts.Editing;
using MediaEngine.Domain;
using MediaEngine.Domain.Configuration;

namespace MediaEngine.Api.Tests;

/// <summary>
/// The links the editor's header shows at its top right (universal editor v7 D2 to D4): the item's page at the provider it is matched to and at
/// Wikidata, each only when the source has a page, with the product name read from the provider config and never the config key.
/// </summary>
public sealed class EditorHeaderMatchLinksTests : IDisposable
{
    private readonly EditorHeaderTestWorld _world = new("Arrival");

    private static readonly IReadOnlyList<ProviderConfiguration> Providers =
    [
        new() { Name = "tmdb", DisplayName = "TMDB" },
        new() { Name = "wikidata_reconciliation", DisplayName = "Wikidata" },
        new()
        {
            Name = "apple_api",
            DisplayName = "Apple API",
            ProductName = "Apple Books",
            ProductNameByKind = new Dictionary<string, string> { ["album"] = "Apple Music", ["song"] = "Apple Music" },
            ProductAliases = ["apple_books", "apple_music"],
        },
    ];

    private EditorHeaderReadService Service() => _world.Header(providerNames: new EditorProviderNames(() => Providers));

    [Fact]
    public async Task AFilmMatchedToTmdbAndWikidata_GetsBothLinks_WithProductNames()
    {
        var film = _world.Work("Movies", "child", "Arrival");
        _world.Canonical(film, BridgeIdKeys.TmdbId, "329865");
        _world.Canonical(film, BridgeIdKeys.WikidataQid, "Q20872821");

        var header = await Service().GetHeaderAsync(film, null);

        Assert.NotNull(header);
        Assert.True(header.ProviderMatched);
        Assert.Collection(
            header.MatchLinks,
            link =>
            {
                Assert.Equal(EditorMatchLinkSources.Provider, link.Source);
                Assert.Equal("TMDB", link.Name);
                Assert.Equal("https://www.themoviedb.org/movie/329865", link.Url);
            },
            link =>
            {
                Assert.Equal(EditorMatchLinkSources.Wikidata, link.Source);
                Assert.Equal("Wikidata", link.Name);
                Assert.Equal("https://www.wikidata.org/wiki/Q20872821", link.Url);
            });
    }

    [Fact]
    public async Task ABookMatchedOnlyByIsbn_GetsNoProviderLink_BecauseAnIsbnHasNoPage()
    {
        var book = _world.Work("Books", "child", "Dune");
        _world.Canonical(book, BridgeIdKeys.Isbn13, "9780441172719");

        var header = await Service().GetHeaderAsync(book, null);

        Assert.NotNull(header);
        Assert.True(header.ProviderMatched);
        Assert.Empty(header.MatchLinks);
    }

    [Fact]
    public async Task AnAudiobookMatchedToAppleBooks_IsNamedByProductName_NeverByTheConfigKey()
    {
        var audiobook = _world.Work("Audiobooks", "child", "The Sea of Monsters");
        _world.Canonical(audiobook, BridgeIdKeys.AppleBooksId, "1234567");

        var header = await Service().GetHeaderAsync(audiobook, null);

        Assert.NotNull(header);
        var link = Assert.Single(header.MatchLinks);
        Assert.Equal("Apple Books", link.Name);
        Assert.DoesNotContain("apple_api", link.Name, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("https://books.apple.com/book/id1234567", link.Url);
    }

    [Fact]
    public async Task AFilmWithOnlyAWikidataId_GetsTheWikidataLinkAndIsNotProviderMatched()
    {
        var film = _world.Work("Movies", "child", "Arrival");
        _world.Canonical(film, BridgeIdKeys.WikidataQid, "Q20872821");

        var header = await Service().GetHeaderAsync(film, null);

        Assert.NotNull(header);
        Assert.False(header.ProviderMatched);
        var link = Assert.Single(header.MatchLinks);
        Assert.Equal(EditorMatchLinkSources.Wikidata, link.Source);
    }

    [Fact]
    public async Task AFilmWithNoMatchAtAll_GetsNoLinks()
    {
        var film = _world.Work("Movies", "child", "Arrival");

        var header = await Service().GetHeaderAsync(film, null);

        Assert.NotNull(header);
        Assert.False(header.ProviderMatched);
        Assert.Empty(header.MatchLinks);
    }

    [Fact]
    public async Task AMatchedFilm_WhenTheConfigHasNoProductName_FallsBackToTheNameTheMatchRuleCarries()
    {
        var film = _world.Work("Movies", "child", "Arrival");
        _world.Canonical(film, BridgeIdKeys.TmdbId, "329865");

        var header = await _world.Header(providerNames: new EditorProviderNames(() => [])).GetHeaderAsync(film, null);

        Assert.Equal("TMDB", Assert.Single(header!.MatchLinks).Name);
    }

    public void Dispose() => _world.Dispose();
}

/// <summary>The one lookup from a provider key to the name a person knows it by (universal editor v7 D4).</summary>
public sealed class EditorProviderNamesTests
{
    private static readonly IReadOnlyList<ProviderConfiguration> Providers =
    [
        new() { Name = "tvdb", DisplayName = "TheTVDB" },
        new()
        {
            Name = "apple_api",
            DisplayName = "Apple API",
            ProductName = "Apple Books",
            ProductNameByKind = new Dictionary<string, string> { ["album"] = "Apple Music" },
            ProductAliases = ["apple_books"],
        },
    ];

    [Fact]
    public void AProviderWithoutAProductName_IsNamedByItsDisplayName()
    {
        Assert.Equal("TheTVDB", EditorProviderNames.Resolve(Providers, "tvdb"));
    }

    [Fact]
    public void AProviderWithAProductName_IsNamedByItNotByItsDisplayNameOrKey()
    {
        Assert.Equal("Apple Books", EditorProviderNames.Resolve(Providers, "apple_api"));
    }

    [Fact]
    public void AnAliasOfAProvider_ResolvesToTheSameProductName()
    {
        Assert.Equal("Apple Books", EditorProviderNames.Resolve(Providers, "apple_books"));
    }

    [Fact]
    public void AProductNameForTheItemsKind_WinsOverTheDefaultProductName()
    {
        Assert.Equal("Apple Music", EditorProviderNames.Resolve(Providers, "apple_api", "album"));
        Assert.Equal("Apple Books", EditorProviderNames.Resolve(Providers, "apple_api", "audiobook"));
    }

    [Fact]
    public void AKeyNoProviderHas_HasNoName_SoTheCodeNameIsNeverReturned()
    {
        Assert.Null(EditorProviderNames.Resolve(Providers, "mystery_api"));
        Assert.Null(EditorProviderNames.Resolve(Providers, null));
    }

    [Fact]
    public void TheShippedAppleConfig_NamesAppleBooksAndAppleMusic_NeverAppleApi()
    {
        var json = File.ReadAllText(Path.Combine(RepoRoot(), "config", "providers", "apple_api.json"));
        var apple = System.Text.Json.JsonSerializer.Deserialize<ProviderConfiguration>(json)!;

        Assert.Equal("Apple Books", EditorProviderNames.Resolve([apple], "apple_api", "audiobook"));
        Assert.Equal("Apple Music", EditorProviderNames.Resolve([apple], "apple_api", "song"));
        Assert.Equal("Apple Books", EditorProviderNames.Resolve([apple], "apple_books"));
    }

    private static string RepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "MediaEngine.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("The repository root was not found.");
    }
}
