using System.Reflection;
using MediaEngine.Domain;
using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Enums;
using MediaEngine.Domain.Models;
using MediaEngine.Domain.Services;
using MediaEngine.Providers.Adapters;
using MediaEngine.Providers.Contracts;
using MediaEngine.Providers.Models;
using MediaEngine.Providers.Services;
using MediaEngine.Storage;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaEngine.Providers.Tests;

public sealed class SearchServiceUniverseSearchTests
{
    [Fact]
    public async Task EditorPreview_ReportsProviderFailure()
    {
        var apple = new CapturingRetailProvider("apple_api") { FailSearch = true };
        var result = await new RetailMatchPreviewService(BuildSearchService(apple))
            .SearchAsync(new SearchRetailRequest("A book", "Books"));
        Assert.Empty(result.Candidates);
        Assert.Contains(result.ProviderErrors, error => error.Contains("apple_api"));
    }

    [Fact]
    public async Task EditorPreview_ReturnsInteractiveCandidatesWithoutAutomaticFetch()
    {
        var apple = new CapturingRetailProvider("apple_api");
        var preview = new RetailMatchPreviewService(BuildSearchService(apple));
        var result = await preview.SearchAsync(new SearchRetailRequest("Le Petit Prince", "Books"));
        Assert.NotEmpty(result.Candidates);
        Assert.Equal(1, apple.SearchCount);
        Assert.Equal(0, apple.FetchCount);
    }

    [Fact]
    public async Task SearchUniverse_ExactQid_DoesNotAppendCreatorHint()
    {
        var provider = new CapturingWikidataProvider();
        var service = BuildSearchService(provider);

        await service.SearchUniverseAsync(new SearchUniverseRequest(
            Query: "Q155653",
            MediaType: "Movies",
            MaxCandidates: 5,
            LocalAuthor: "Hayao Miyazaki"));

        Assert.NotNull(provider.LastRequest);
        Assert.Equal("Q155653", provider.LastRequest!.Title);
        Assert.Null(provider.LastRequest.Author);
    }

    [Fact]
    public async Task SearchUniverse_MovieSearch_DoesNotAppendCreatorHint()
    {
        var provider = new CapturingWikidataProvider();
        var service = BuildSearchService(provider);

        await service.SearchUniverseAsync(new SearchUniverseRequest(
            Query: "Spirited Away",
            MediaType: "Movies",
            MaxCandidates: 5,
            LocalAuthor: "Hayao Miyazaki"));

        Assert.NotNull(provider.LastRequest);
        Assert.Equal("Spirited Away", provider.LastRequest!.Title);
        Assert.Null(provider.LastRequest.Author);
    }

    [Fact]
    public async Task SearchUniverse_BookSearch_StillUsesCreatorHint()
    {
        var provider = new CapturingWikidataProvider();
        var service = BuildSearchService(provider);

        await service.SearchUniverseAsync(new SearchUniverseRequest(
            Query: "Dune",
            MediaType: "Books",
            MaxCandidates: 5,
            LocalAuthor: "Frank Herbert"));

        Assert.NotNull(provider.LastRequest);
        Assert.Equal("Dune Frank Herbert", provider.LastRequest!.Title);
        Assert.Equal("Frank Herbert", provider.LastRequest.Author);
    }

    [Fact]
    public async Task SearchUniverse_AudiobookKeepsNamedWikidataMetadataWithoutRetailArtworkLookup()
    {
        var wikidata = new AudiobookWikidataProvider();
        var retail = new CapturingRetailProvider("apple_api");
        var service = BuildSearchService(wikidata, retail);

        var result = await service.SearchUniverseAsync(new SearchUniverseRequest(
            Query: "Dungeon Crawler Carl",
            MediaType: "Audiobooks",
            MaxCandidates: 5,
            LocalTitle: "Dungeon Crawler Carl",
            LocalAuthor: "Matt Dinniman"));

        var candidate = Assert.Single(result.Candidates);
        Assert.Null(candidate.CoverUrl);
        Assert.Equal("Matt Dinniman", candidate.Author);
        Assert.Equal("Jeff Hays", candidate.MediaTypeMetadata!["narrator"]);
        Assert.Equal(0, retail.SearchCount);
    }

    [Fact]
    public void ReconciliationTitleConstraints_DoNotApplyBookAuthorConstraintToMovieOrExactQid()
    {
        var method = typeof(ReconciliationAdapter).GetMethod(
            "BuildTitleSearchConstraints",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        var qidConstraints = InvokeBuildTitleSearchConstraints(method!, new ProviderLookupRequest
        {
            Title = "Q155653",
            MediaType = MediaType.Movies,
            Author = "Hayao Miyazaki",
        });
        Assert.Null(qidConstraints);

        var movieConstraints = InvokeBuildTitleSearchConstraints(method!, new ProviderLookupRequest
        {
            Title = "Spirited Away",
            MediaType = MediaType.Movies,
            Author = "Hayao Miyazaki",
        });
        Assert.Null(movieConstraints);

        var bookConstraints = InvokeBuildTitleSearchConstraints(method!, new ProviderLookupRequest
        {
            Title = "Dune",
            MediaType = MediaType.Books,
            Author = "Frank Herbert",
        });
        Assert.NotNull(bookConstraints);
        Assert.Equal("Frank Herbert", bookConstraints!["P50"]);
    }

    [Fact]
    public async Task SearchRetail_DoesNotQueryUnconfiguredProviders()
    {
        var unconfiguredProvider = new CapturingRetailProvider("test_unconfigured_provider");
        var service = BuildSearchService(unconfiguredProvider);

        var result = await service.SearchRetailAsync(new SearchRetailRequest(
            Query: "Le Petit Prince",
            MediaType: "Books",
            MaxCandidates: 5,
            LocalTitle: "Le Petit Prince",
            LocalAuthor: "Antoine de Saint-Exupery"));

        Assert.Empty(result.Candidates);
        Assert.Equal(0, unconfiguredProvider.SearchCount);
    }

    [Fact]
    public async Task SearchRetail_AutomaticPreview_UsesPipelineFetchAndDecisionPath()
    {
        var apple = new CapturingRetailProvider("apple_api");
        var service = BuildSearchService(apple);

        var result = await service.SearchRetailAutomaticAsync(new SearchRetailRequest(
            Query: "Dungeon Crawler Carl",
            MediaType: "Audiobooks",
            MaxCandidates: 5,
            LocalTitle: "stale enriched title",
            LocalAuthor: "stale enriched author",
            FileHints: new Dictionary<string, string>
            {
                ["title"] = "Dungeon Crawler Carl",
                ["author"] = "Matt Dinniman",
                ["narrator"] = "Jeff Hays",
            },
            SearchFields: new Dictionary<string, string>
            {
                ["title"] = "Dungeon Crawler Carl",
                ["author"] = "Matt Dinniman",
                ["narrator"] = "Jeff Hays",
            }));

        var candidate = Assert.Single(result.Candidates);
        Assert.Equal(1, apple.FetchCount);
        Assert.Equal(0, apple.SearchCount);
        Assert.Equal("Dungeon Crawler Carl", apple.LastFetchRequest!.Title);
        Assert.Equal("Jeff Hays", apple.LastFetchRequest.Narrator);
        Assert.Equal("AutoAccepted", candidate.ExtraFields["automatic_outcome"]);
    }

    private static SearchService BuildSearchService(params IExternalMetadataProvider[] providers)
    {
        var configLoader = new ConfigurationDirectoryLoader(Path.Combine(FindRepoRoot(), "config"));
        return new SearchService(
            providers,
            configLoader,
            new StubFuzzyMatchingService(),
            new StubRetailMatchScoringService(),
            NullLogger<SearchService>.Instance);
    }

    private static Dictionary<string, string>? InvokeBuildTitleSearchConstraints(
        MethodInfo method,
        ProviderLookupRequest request) =>
        (Dictionary<string, string>?)method.Invoke(null, [request]);

    private static string FindRepoRoot()
    {
        var dir = Path.GetDirectoryName(typeof(SearchServiceUniverseSearchTests).Assembly.Location);
        while (dir != null)
        {
            var gitMarker = Path.Combine(dir, ".git");
            if (Directory.Exists(gitMarker) || File.Exists(gitMarker))
            {
                return dir;
            }

            dir = Path.GetDirectoryName(dir);
        }

        throw new InvalidOperationException("Could not find repository root.");
    }

    private sealed class CapturingWikidataProvider : IExternalMetadataProvider
    {
        public string Name => "wikidata_reconciliation";
        public ProviderDomain Domain => ProviderDomain.Universal;
        public IReadOnlyList<string> CapabilityTags => ["wikidata"];
        public Guid ProviderId => Guid.Parse("b3000003-d000-4000-8000-000000000004");
        public ProviderLookupRequest? LastRequest { get; private set; }

        public bool CanHandle(MediaType mediaType) => true;

        public bool CanHandle(EntityType entityType) => true;

        public Task<IReadOnlyList<ProviderClaim>> FetchAsync(
            ProviderLookupRequest request,
            CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<ProviderClaim>>([]);

        public Task<IReadOnlyList<SearchResultItem>> SearchAsync(
            ProviderLookupRequest request,
            int limit = 25,
            CancellationToken ct = default)
        {
            LastRequest = request;
            return Task.FromResult<IReadOnlyList<SearchResultItem>>(
            [
                new SearchResultItem(
                    Title: "Spirited Away",
                    Author: null,
                    Description: "2001 anime film directed by Hayao Miyazaki",
                    Year: "2001",
                    ThumbnailUrl: null,
                    ProviderItemId: "Q155653",
                    Confidence: 0.99,
                    ProviderName: Name),
            ]);
        }
    }

    private sealed class CapturingRetailProvider(string name) : IExternalMetadataProvider
    {
        public string Name { get; } = name;
        public bool FailSearch { get; init; }
        public ProviderDomain Domain => ProviderDomain.Ebook;
        public IReadOnlyList<string> CapabilityTags => ["title", "cover"];
        public Guid ProviderId => WellKnownProviders.OpenLibrary;
        public int SearchCount { get; private set; }
        public int FetchCount { get; private set; }
        public ProviderLookupRequest? LastFetchRequest { get; private set; }

        public bool CanHandle(MediaType mediaType) => mediaType is MediaType.Books or MediaType.Audiobooks;

        public bool CanHandle(EntityType entityType) => entityType == EntityType.Work;

        public Task<IReadOnlyList<ProviderClaim>> FetchAsync(
            ProviderLookupRequest request,
            CancellationToken ct = default)
        {
            FetchCount++;
            LastFetchRequest = request;
            return Task.FromResult<IReadOnlyList<ProviderClaim>>(
            [
                new("provider_item_id", "1553350212", 1.0),
                new("title", "Dungeon Crawler Carl", 1.0),
                new("author", "Matt Dinniman", 1.0),
                new("narrator", "Jeff Hays", 1.0),
                new("year", "2020", 1.0),
            ]);
        }

        public Task<IReadOnlyList<SearchResultItem>> SearchAsync(
            ProviderLookupRequest request,
            int limit = 25,
            CancellationToken ct = default)
        {
            SearchCount++;
            if (FailSearch) throw new HttpRequestException("Unavailable");
            return Task.FromResult<IReadOnlyList<SearchResultItem>>(
            [
                new SearchResultItem(
                    Title: "Le Petit Prince",
                    Author: "Antoine de Saint-Exupery",
                    Description: "1943 novella",
                    Year: "1943",
                    ThumbnailUrl: null,
                    ProviderItemId: "OL45804W",
                    Confidence: 0.99,
                    ProviderName: Name),
            ]);
        }
    }

    private sealed class AudiobookWikidataProvider : IExternalMetadataProvider
    {
        public string Name => "wikidata_reconciliation";
        public ProviderDomain Domain => ProviderDomain.Universal;
        public IReadOnlyList<string> CapabilityTags => ["wikidata"];
        public Guid ProviderId => Guid.Parse("b3000003-d000-4000-8000-000000000004");

        public bool CanHandle(MediaType mediaType) => true;
        public bool CanHandle(EntityType entityType) => true;

        public Task<IReadOnlyList<ProviderClaim>> FetchAsync(ProviderLookupRequest request, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<ProviderClaim>>([]);

        public Task<IReadOnlyList<SearchResultItem>> SearchAsync(
            ProviderLookupRequest request,
            int limit = 25,
            CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<SearchResultItem>>(
            [
                new SearchResultItem(
                    Title: "Dungeon Crawler Carl",
                    Author: null,
                    Description: "2020 novel by Matt Dinniman",
                    Year: "2020",
                    ThumbnailUrl: "https://retail.example/should-not-be-used.jpg",
                    ProviderItemId: "Q136529136",
                    Confidence: 0.99,
                    ProviderName: Name,
                    ResultType: "audiobook_edition",
                    ExtraFields: new Dictionary<string, string>
                    {
                        ["author"] = "Matt Dinniman",
                        ["narrator"] = "Jeff Hays",
                    }),
            ]);
    }

    private sealed class StubFuzzyMatchingService : IFuzzyMatchingService
    {
        public double ComputeTokenSetRatio(string a, string b) => 1.0;
        public double ComputePartialRatio(string a, string b) => 1.0;
        public FieldMatchResult ScoreCandidate(LocalMetadata local, CandidateMetadata candidate) =>
            new() { TitleScore = 1.0, AuthorScore = 1.0, YearScore = 1.0, CompositeScore = 1.0 };
    }

    private sealed class StubRetailMatchScoringService : IRetailMatchScoringService
    {
        public FieldMatchScores ScoreCandidate(
            IReadOnlyDictionary<string, string> fileHints,
            string? candidateTitle,
            string? candidateAuthor,
            string? candidateYear,
            MediaType mediaType,
            MatchTierConfig? matchTiers = null,
            CandidateExtendedMetadata? extendedMetadata = null,
            double structuralBonus = 0.0) =>
            new()
            {
                TitleScore = 1.0,
                AuthorScore = 1.0,
                YearScore = 1.0,
                FormatScore = 1.0,
                CompositeScore = 1.0,
            };
    }
}
