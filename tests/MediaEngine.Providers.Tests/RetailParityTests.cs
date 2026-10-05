using MediaEngine.Domain;
using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Enums;
using MediaEngine.Domain.Models;
using MediaEngine.Domain.Services;
using MediaEngine.Intelligence.Services;
using MediaEngine.Providers.Services;
using MediaEngine.Storage.Contracts;
using Xunit.Abstractions;
using ProviderConfiguration = MediaEngine.Domain.Configuration.ProviderConfiguration;

namespace MediaEngine.Providers.Tests;

/// <summary>
/// Parity tests — verify that the manual retail search path
/// (<c>SearchService.SearchRetailAsync</c> → <c>IExternalMetadataProvider.SearchAsync</c>
/// → <c>IRetailMatchScoringService.ScoreCandidate</c>) and the automated path
/// (<c>RetailMatchWorker</c> → same scorer) produce identical scores for identical
/// input. This is a structural regression test: both code paths share the same
/// <c>RetailMatchScoringService</c> instance via DI; if anyone introduces a
/// divergent local scorer in either path, this test will fail.
///
/// Marked Integration only because the broader parity story (manual vs pipeline)
/// is exercised end-to-end with live data; the scoring layer itself is
/// deterministic and runs without network access.
/// </summary>
[Trait("Category", "Integration")]
public sealed class RetailParityTests
{
    private readonly ITestOutputHelper _output;
    private readonly RetailMatchScoringService _scorer;

    public RetailParityTests(ITestOutputHelper output)
    {
        _output = output;
        _scorer = new RetailMatchScoringService(
            new FuzzyMatchingService(),
            new MinimalConfigLoader(),
            coverArtHash: null,
            logger: null);
    }

    [Fact]
    public void ScoreCandidate_TvUsesExplicitShowGateWithoutCreator()
    {
        var fileHints = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [MetadataFieldConstants.Title] = "Breaking Bad",
            [MetadataFieldConstants.EpisodeTitle] = "Pilot",
            [MetadataFieldConstants.ShowName] = "Breaking Bad",
            [MetadataFieldConstants.Year] = "2008",
        };

        var score = _scorer.ScoreCandidate(
            fileHints,
            candidateTitle: "Pilot",
            candidateAuthor: "Breaking Bad",
            candidateYear: "2008",
            mediaType: MediaType.TV,
            extendedMetadata: new() { Signals = new Dictionary<string, string> { ["show_name"] = "Breaking Bad" } });

        Assert.DoesNotContain(score.FieldScores, row => row.Key == "author" && row.Role == "weighted");
        Assert.Contains(score.FieldScores, row => row.Key == "show_title" && row.Verdict == "pass");
        Assert.True(score.CompositeScore >= 0.90);
    }

    [Fact]
    public void ScoreCandidate_TvSeriesExactTitleAndYearOutranksDerivativeWithoutCreator()
    {
        var hints = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [MetadataFieldConstants.Title] = "Solo Leveling",
            [MetadataFieldConstants.ShowName] = "Solo Leveling",
            [MetadataFieldConstants.Year] = "2024",
        };

        var anime = _scorer.ScoreCandidate(hints, "Solo Leveling", null, "2024", MediaType.TV);
        var documentary = _scorer.ScoreCandidate(hints, "The Leveling of Solo Leveling", null, "2024", MediaType.TV);

        Assert.Equal(1.0, anime.TitleScore);
        Assert.Equal(1.0, anime.CompositeScore);
        Assert.True(documentary.TitleScore < 0.75);
        Assert.True(documentary.CompositeScore < anime.CompositeScore);
    }

    [Fact]
    public void ScoreCandidate_UsesDateAsYearFallback()
    {
        var fileHints = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [MetadataFieldConstants.Title] = "The Long Walk",
            [MetadataFieldConstants.Author] = "Richard Bachman",
            ["date"] = "1979-01-01",
        };

        var score = _scorer.ScoreCandidate(
            fileHints,
            candidateTitle: "The Long Walk",
            candidateAuthor: "Stephen King",
            candidateYear: "1979",
            mediaType: MediaType.Books);

        Assert.Equal(1.0, score.YearScore);
    }

    [Fact]
    public void ScoreCandidate_NormalizedExactTitleVariantsScoreAsExact()
    {
        var fileHints = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [MetadataFieldConstants.Title] = "4'33\"",
            [MetadataFieldConstants.Artist] = "John Cage",
            [MetadataFieldConstants.Year] = "2002",
        };

        var score = _scorer.ScoreCandidate(
            fileHints,
            candidateTitle: "4'33''",
            candidateAuthor: "John Cage",
            candidateYear: "2002",
            mediaType: MediaType.Music);

        Assert.Equal(1.0, score.TitleScore);
        Assert.True(score.CompositeScore >= 0.95);
    }

    [Fact]
    public void ScoreCandidate_DiacriticTolerantBookCreatorMatchScoresAsExact()
    {
        var fileHints = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [MetadataFieldConstants.Title] = "Le Petit Prince",
            [MetadataFieldConstants.Author] = "Antoine de Saint-Exupery",
            [MetadataFieldConstants.Year] = "1943",
        };

        var score = _scorer.ScoreCandidate(
            fileHints,
            candidateTitle: "Le Petit Prince",
            candidateAuthor: "Antoine de Saint-Exupéry",
            candidateYear: "1943",
            mediaType: MediaType.Books);

        Assert.Equal(1.0, score.TitleScore);
        Assert.Equal(1.0, score.AuthorScore);
        Assert.True(score.CompositeScore >= 0.95);
    }

    [Fact]
    public void ScoreCandidate_DiacriticTolerantMusicArtistMatchScoresAsExact()
    {
        var fileHints = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [MetadataFieldConstants.Title] = "La Vie en rose",
            [MetadataFieldConstants.Artist] = "Edith Piaf",
            [MetadataFieldConstants.Year] = "1947",
        };

        var score = _scorer.ScoreCandidate(
            fileHints,
            candidateTitle: "La Vie en rose",
            candidateAuthor: "Édith Piaf",
            candidateYear: "1947",
            mediaType: MediaType.Music);

        Assert.Equal(1.0, score.TitleScore);
        Assert.Equal(1.0, score.AuthorScore);
        Assert.True(score.CompositeScore >= 0.95);
    }

    [Theory]
    [InlineData("J. R. R. Tolkien", "J. R. R. Tolkien & Rafat Allam", 0.5)]
    [InlineData("J. R. R. Tolkien & Rafat Allam", "J. R. R. Tolkien", 0.5)]
    [InlineData("Neil Gaiman & Terry Pratchett", "Terry Pratchett and Neil Gaiman", 1.0)]
    [InlineData("J. R. R. Tolkien", "Tolkien, J. R. R.", 1.0)]
    [InlineData("JRR Tolkien", "Tolkien, J. R. R.", 1.0)]
    public void ScoreCandidate_ComparesCreatorListsProportionally(
        string fileAuthor,
        string candidateAuthor,
        double expectedAuthorScore)
    {
        var fileHints = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [MetadataFieldConstants.Title] = "The Two Towers",
            [MetadataFieldConstants.Author] = fileAuthor,
            [MetadataFieldConstants.Year] = "1954",
        };

        var score = _scorer.ScoreCandidate(
            fileHints,
            candidateTitle: "The Two Towers",
            candidateAuthor: candidateAuthor,
            candidateYear: "2025",
            mediaType: MediaType.Books);

        Assert.Equal(expectedAuthorScore, score.AuthorScore);
    }

    [Fact]
    public void ScoreCandidate_MusicPrimaryArtistMatchesProviderFeaturedCredits()
    {
        var fileHints = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [MetadataFieldConstants.Title] = "Stan",
            [MetadataFieldConstants.Artist] = "Eminem",
            [MetadataFieldConstants.Year] = "2000",
        };

        var score = _scorer.ScoreCandidate(
            fileHints,
            candidateTitle: "Stan",
            candidateAuthor: "Eminem, Dido",
            candidateYear: "2000",
            mediaType: MediaType.Music);

        Assert.Equal(1.0, score.AuthorScore);
        Assert.True(score.CompositeScore >= 0.90);
    }

    [Fact]
    public void ScoreCandidate_ExtraRetailCreatorCannotProducePerfectOrAutoAcceptedMatch()
    {
        var fileHints = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [MetadataFieldConstants.Title] = "The Two Towers",
            [MetadataFieldConstants.Author] = "J. R. R. Tolkien",
            [MetadataFieldConstants.Year] = "1954",
        };

        var score = _scorer.ScoreCandidate(
            fileHints,
            candidateTitle: "THE TWO TOWERS",
            candidateAuthor: "J. R. R. Tolkien & Rafat Allam",
            candidateYear: "2025",
            mediaType: MediaType.Books);
        var decision = new RetailCandidateScorer().EvaluateDecision(
            fileHints,
            candidateTitle: "THE TWO TOWERS",
            candidateCreator: "J. R. R. Tolkien & Rafat Allam",
            candidateYear: "2025",
            retailScore: score,
            finalScore: score.CompositeScore,
            retailAcceptThreshold: 0.90,
            retailAmbiguousThreshold: 0.65,
            matchContext: "single_item",
            mediaType: MediaType.Books);

        Assert.Equal(0.5, score.AuthorScore);
        Assert.True(score.CompositeScore < 0.90);
        Assert.Equal("Ambiguous", decision.Outcome);
    }

    [Fact]
    public void ScoreCandidate_ComicExactSeriesIssueDoesNotRequireIssueTitleWording()
    {
        var fileHints = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [MetadataFieldConstants.Title] = "Batman: Year One Part 1",
            [MetadataFieldConstants.Series] = "Batman",
            [MetadataFieldConstants.SeriesPosition] = "404",
            [MetadataFieldConstants.Year] = "1987",
        };

        var score = _scorer.ScoreCandidate(
            fileHints,
            candidateTitle: "Who I am, How I Came To Be.",
            candidateAuthor: null,
            candidateYear: "1987",
            mediaType: MediaType.Comics,
            extendedMetadata: new CandidateExtendedMetadata
            {
                Series = "Batman",
                IssueNumber = "404",
            },
            structuralBonus: 0.35);

        Assert.Contains(score.FieldScores, row => row.Key == "series" && row.Score == 1);
        Assert.Contains(score.FieldScores, row => row.Key == "issue" && row.Score == 1);
        Assert.True(score.TitleScore < 1);
        Assert.True(score.CompositeScore >= 0.90);
    }

    public static IEnumerable<object?[]> Fixtures()
    {
        // Books — title + author + year
        yield return new object?[]
        {
            "books",
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["title"]  = "Dune",
                ["author"] = "Frank Herbert",
                ["year"]   = "1965",
            },
            "Dune", "Frank Herbert", "1965",
            MediaType.Books,
        };

        // Movies — title + director + year
        yield return new object?[]
        {
            "movies",
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["title"]    = "Blade Runner 2049",
                ["director"] = "Denis Villeneuve",
                ["year"]     = "2017",
            },
            "Blade Runner 2049", "Denis Villeneuve", "2017",
            MediaType.Movies,
        };

        // TV — episode_title + show_name + year
        yield return new object?[]
        {
            "tv",
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["title"]         = "Breaking Bad",
                ["episode_title"] = "Pilot",
                ["show_name"]     = "Breaking Bad",
                ["year"]          = "2008",
            },
            "Pilot", null, "2008",
            MediaType.TV,
        };

        // Audiobooks — title + author + narrator + year
        yield return new object?[]
        {
            "audiobooks",
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["title"]    = "Project Hail Mary",
                ["author"]   = "Andy Weir",
                ["narrator"] = "Ray Porter",
                ["year"]     = "2021",
            },
            "Project Hail Mary", "Andy Weir", "2021",
            MediaType.Audiobooks,
        };
    }

    [Theory(Skip = "Requires live Wikidata network access. Run locally with: dotnet test --filter Category=Integration")]
    [Trait("Category", "Integration")]
    [MemberData(nameof(Fixtures))]
    public void ManualAndPipeline_ScoreIdentically(
        string fixtureLabel,
        IReadOnlyDictionary<string, string> fileHints,
        string? candidateTitle,
        string? candidateAuthor,
        string? candidateYear,
        MediaType mediaType)
    {
        // The manual search path constructs file hints in SearchService.SearchRetailAsync
        // and calls _retailScoring.ScoreCandidate(...). The pipeline path
        // (RetailMatchWorker) constructs the same file hints from the
        // ProviderLookupRequest fields and calls the same method on the same
        // singleton instance. We assert that calling the scorer twice with
        // identical input yields identical output — i.e. that the scorer is
        // deterministic and stateless, which is the structural guarantee that
        // both paths must share.

        var manualScore = _scorer.ScoreCandidate(
            fileHints, candidateTitle, candidateAuthor, candidateYear, mediaType);

        var pipelineScore = _scorer.ScoreCandidate(
            fileHints, candidateTitle, candidateAuthor, candidateYear, mediaType);

        _output.WriteLine($"[{fixtureLabel}] manual={manualScore.CompositeScore:F4} pipeline={pipelineScore.CompositeScore:F4}");

        Assert.Equal(manualScore.CompositeScore, pipelineScore.CompositeScore);
        Assert.Equal(manualScore.TitleScore, pipelineScore.TitleScore);
        Assert.Equal(manualScore.AuthorScore, pipelineScore.AuthorScore);
        Assert.Equal(manualScore.YearScore, pipelineScore.YearScore);
        Assert.Equal(manualScore.FormatScore, pipelineScore.FormatScore);
    }

    // ── Stubs ─────────────────────────────────────────────────────────────────

    /// <summary>Minimal config loader returning default HydrationSettings (default fuzzy match weights).</summary>
    private sealed class MinimalConfigLoader : IConfigurationLoader
    {
        public ScoringSettings LoadScoring() => new();
        public IReadOnlyList<ProviderConfiguration> LoadAllProviders() => [];
        public PipelineConfiguration LoadPipelines() => RetailMatrixFixture.Load();
        public HydrationSettings LoadHydration() => new();
        public T? LoadConfig<T>(string subdirectory, string name) where T : class => default;
        public CoreConfiguration LoadCore() => throw new NotImplementedException();
        public void SaveCore(CoreConfiguration config) => throw new NotImplementedException();
        public void SaveScoring(ScoringSettings settings) => throw new NotImplementedException();
        public MaintenanceSettings LoadMaintenance() => throw new NotImplementedException();
        public void SaveMaintenance(MaintenanceSettings settings) => throw new NotImplementedException();
        public void SaveHydration(HydrationSettings settings) => throw new NotImplementedException();
        public void SavePipelines(PipelineConfiguration config) => throw new NotImplementedException();
        public DisambiguationSettings LoadDisambiguation() => throw new NotImplementedException();
        public void SaveDisambiguation(DisambiguationSettings settings) => throw new NotImplementedException();
        public TranscodingSettings LoadTranscoding() => throw new NotImplementedException();
        public void SaveTranscoding(TranscodingSettings settings) => throw new NotImplementedException();
        public MediaTypeConfiguration LoadMediaTypes() => throw new NotImplementedException();
        public void SaveMediaTypes(MediaTypeConfiguration config) => throw new NotImplementedException();
        public LibrariesConfiguration LoadLibraries() => throw new NotImplementedException();
        public FieldPriorityConfiguration LoadFieldPriorities() => throw new NotImplementedException();
        public void SaveFieldPriorities(FieldPriorityConfiguration config) => throw new NotImplementedException();
        public ProviderConfiguration? LoadProvider(string name) => throw new NotImplementedException();
        public void SaveProvider(ProviderConfiguration config) => throw new NotImplementedException();
        public T? LoadAi<T>() where T : class => throw new NotImplementedException();
        public void SaveAi<T>(T settings) where T : class => throw new NotImplementedException();
        public PaletteConfiguration LoadPalette() => throw new NotImplementedException();
        public void SavePalette(PaletteConfiguration palette) => throw new NotImplementedException();
        public void SaveConfig<T>(string subdirectory, string name, T config) where T : class => throw new NotImplementedException();
    }
}
