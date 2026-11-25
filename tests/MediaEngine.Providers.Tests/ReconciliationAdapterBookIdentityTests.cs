using System.Reflection;
using MediaEngine.Domain;
using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Enums;
using MediaEngine.Domain.Models;
using MediaEngine.Domain.Services;
using MediaEngine.Providers.Adapters;
using MediaEngine.Providers.Models;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaEngine.Providers.Tests;

/// <summary>Guards the book identity rules of the Wikidata step (wrong, foreign-language and edition matches).</summary>
public sealed class ReconciliationAdapterBookIdentityTests
{
    [Fact]
    public void SeriesLabelAsSeriesClaim_KeepsAnIssuesOwnTitle_AndNamesTheSeriesInstead()
    {
        IReadOnlyList<ProviderClaim> claims =
        [
            new ProviderClaim(MetadataFieldConstants.Title, "Batman", 0.98),
            new ProviderClaim(MetadataFieldConstants.Genre, "superhero", 0.9),
        ];

        var result = ReconciliationAdapter.SeriesLabelAsSeriesClaim(claims);

        Assert.DoesNotContain(result, c => c.Key == MetadataFieldConstants.Title);
        Assert.Equal("Batman", Assert.Single(result, c => c.Key == MetadataFieldConstants.Series).Value);
        Assert.Contains(result, c => c.Key == MetadataFieldConstants.Genre);
    }

    [Fact]
    public void SeriesLabelAsSeriesClaim_DoesNotAddASecondSeriesClaim_WhenOneExists()
    {
        IReadOnlyList<ProviderClaim> claims =
        [
            new ProviderClaim(MetadataFieldConstants.Series, "Batman (1940)", 0.95),
            new ProviderClaim(MetadataFieldConstants.Title, "Batman", 0.98),
        ];

        var result = ReconciliationAdapter.SeriesLabelAsSeriesClaim(claims);

        Assert.Equal("Batman (1940)", Assert.Single(result).Value);
    }

    [Theory]
    [InlineData("2009", "1989", MediaType.Books, true)]       // a newer edition of an older book is normal
    [InlineData("2011", "2008", MediaType.Books, true)]       // box set year, later than the book
    [InlineData("2019", "1965", MediaType.Audiobooks, true)]
    [InlineData("1987", "1989", MediaType.Books, true)]       // small slack for early printings
    [InlineData("1950", "1989", MediaType.Books, false)]      // an edition older than the book is wrong
    [InlineData("2010", "2001", MediaType.Movies, false)]     // other kinds keep the tight window
    [InlineData("2001", "2001", MediaType.Movies, true)]
    public void IsResolvedYearCompatible_ComparesEditionYearFairly(string hint, string resolved, MediaType type, bool expected)
    {
        var method = typeof(ReconciliationAdapter).GetMethod("IsResolvedYearCompatible", BindingFlags.Static | BindingFlags.NonPublic)!;
        IReadOnlyList<ProviderClaim> claims = [new ProviderClaim(MetadataFieldConstants.Year, resolved, 0.9)];

        Assert.Equal(expected, (bool)method.Invoke(null, [hint, claims, type])!);
    }

    [Fact]
    public void IsBareEditionRecord_OnlyTrueForEditionWithoutAWrittenWorkClass()
    {
        var method = typeof(ReconciliationAdapter).GetMethod("IsBareEditionRecord", BindingFlags.Static | BindingFlags.NonPublic)!;

        Assert.True((bool)method.Invoke(null, [new List<string> { "Q3331189" }])!);
        Assert.False((bool)method.Invoke(null, [new List<string> { "Q3331189", "Q8261" }])!);
        Assert.False((bool)method.Invoke(null, [new List<string> { "Q8261" }])!);
    }

    [Theory]
    [InlineData("The Hunger Games", "The Hunger Games", true)]
    [InlineData("The Hunger Games", "Mockingjay", false)]
    [InlineData("Pyramids", "The Red Pyramid", false)]
    [InlineData("Eric", "Saga of Erik the Red", false)]
    [InlineData("The Eye of the World", "L'oeil du monde", false)]
    [InlineData("The Eye of the World", "The Eye of the World", true)]
    [InlineData("", "Anything", true)]
    public void TitleAgreesWithFile_RejectsUnrelatedAndTranslatedNames(string fileTitle, string wikidataName, bool expected)
    {
        var adapter = new ReconciliationAdapter(
            new ReconciliationProviderConfig(),
            new HttpFactory(),
            NullLogger<ReconciliationAdapter>.Instance,
            new TokenFuzzy());
        var method = typeof(ReconciliationAdapter).GetMethod("TitleAgreesWithFile", BindingFlags.Instance | BindingFlags.NonPublic)!;
        IReadOnlyList<ProviderClaim> claims = [];

        Assert.Equal(expected, (bool)method.Invoke(adapter, [fileTitle, claims, wikidataName])!);
    }

    private sealed class HttpFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }

    /// <summary>Minimal token-set similarity: shared words over the smaller word set (subsets score 1).</summary>
    private sealed class TokenFuzzy : IFuzzyMatchingService
    {
        public double ComputeTokenSetRatio(string a, string b)
        {
            var left = Tokens(a);
            var right = Tokens(b);
            if (left.Count == 0 || right.Count == 0)
            {
                return 0;
            }

            return (double)left.Intersect(right).Count() / Math.Min(left.Count, right.Count);
        }

        public double ComputePartialRatio(string a, string b) => 0;

        public FieldMatchResult ScoreCandidate(LocalMetadata local, CandidateMetadata candidate) => new();

        private static HashSet<string> Tokens(string value) =>
            value.ToLowerInvariant()
                .Split([' ', '\'', '-'], StringSplitOptions.RemoveEmptyEntries)
                .ToHashSet();
    }
}
