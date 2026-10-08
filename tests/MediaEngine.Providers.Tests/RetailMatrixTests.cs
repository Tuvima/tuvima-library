using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Enums;
using MediaEngine.Domain.Models;
using MediaEngine.Intelligence.Services;
using MediaEngine.Providers.Services;
using MediaEngine.Storage;
namespace MediaEngine.Providers.Tests;

public sealed class RetailMatrixTests
{
    private static RetailMatchScoringService Scorer()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "config", "pipelines.json")))
        {
            directory = directory.Parent;
        }
        return new(new FuzzyMatchingService(), new ConfigurationDirectoryLoader(Path.Combine(directory!.FullName, "config")));
    }
    [Theory]
    [InlineData(MediaType.Books, "default")]
    [InlineData(MediaType.Audiobooks, "default")]
    [InlineData(MediaType.Movies, "default")]
    [InlineData(MediaType.TV, "series")]
    [InlineData(MediaType.TV, "episode")]
    [InlineData(MediaType.Music, "track")]
    [InlineData(MediaType.Music, "album")]
    [InlineData(MediaType.Comics, "issue")]
    public void ExactIdentityScoresHighlyInEveryScope(MediaType type, string scope)
    {
        var hints = Hints();
        var score = Scorer().ScoreCandidate(hints, "Example", "Creator", "2020", type,
            extendedMetadata: new() { Scope = scope, Kind = type, Series = "Example", DurationSeconds = 180, IssueNumber = "2", Signals = hints });
        Assert.InRange(score.CompositeScore, .95, 1);
        Assert.Empty(score.AutoAcceptBlockReasons);
        Assert.DoesNotContain(score.FieldScores, row => row.Key == "genre");
        Assert.Equal(1, score.FieldScores.Where(row => row.Role == "weighted").Sum(row => row.Weight), 6);
    }
    [Fact]
    public void MovieMissingDirectorHasNoIdentityPenalty()
    {
        var score = Scorer().ScoreCandidate(new Dictionary<string, string> { { "title", "Example" }, { "year", "2020" }, { "director", "Known director" } }, "Example", null, "2020", MediaType.Movies);
        Assert.Equal(1, score.CompositeScore);
        Assert.DoesNotContain(score.FieldScores, row => row.Role == "weighted" && row.Key == "author");
    }
    [Theory]
    [InlineData(MediaType.Books)]
    [InlineData(MediaType.Audiobooks)]
    [InlineData(MediaType.Music)]
    public void MissingRequiredCreatorCannotBeRescuedByExactIdentifier(MediaType type)
    {
        var score = Scorer().ScoreCandidate(Hints(), "Example", null, "2020", type,
            extendedMetadata: new() { Signals = Hints() }, structuralBonus: .35);
        Assert.InRange(score.CompositeScore, 0, .65);
        Assert.Contains("required_author_missing", score.AutoAcceptBlockReasons);
        Assert.Contains(score.FieldScores, row => row.Key == "author" && row.Missing && row.Verdict == "required_missing");
    }
    [Fact]
    public void FailedKindGateSurvivesAllBonuses()
    {
        var score = Scorer().ScoreCandidate(Hints(), "Example", "Creator", "2020", MediaType.Books,
            extendedMetadata: new() { Kind = MediaType.Movies, Signals = Hints(), Publisher = "Publisher", PageCount = 100 }, structuralBonus: .35);
        Assert.InRange(score.CompositeScore, 0, .50);
        Assert.Contains("gate_format_failed", score.AutoAcceptBlockReasons);
    }
    [Fact]
    public void WrongShowCannotAutoAcceptExactEpisodeNumbers()
    {
        var remote = Hints(); remote["show_name"] = "Different show";
        var score = Scorer().ScoreCandidate(Hints(), "Example", null, "2020", MediaType.TV,
            extendedMetadata: new() { Scope = "episode", Signals = remote, Series = "Different show" });
        Assert.InRange(score.CompositeScore, 0, .50);
        Assert.Contains("gate_show_title_failed", score.AutoAcceptBlockReasons);
    }
    [Fact]
    public void ShowAndEpisodeNumbersAreEnoughWhenEpisodeTitleIsAbsent()
    {
        var hints = Hints(); hints.Remove("episode_title"); hints.Remove("year");
        var score = Scorer().ScoreCandidate(hints, null, null, null, MediaType.TV,
            extendedMetadata: new() { Scope = "episode", Signals = hints, Series = "Example" });
        Assert.Equal(1, score.CompositeScore);
        Assert.Empty(score.AutoAcceptBlockReasons);
    }
    [Fact]
    public void TitleOnlyRemainsReviewAndGenreNeverChangesTheScore()
    {
        var hints = new Dictionary<string, string> { { "title", "Example" } };
        var first = Scorer().ScoreCandidate(hints, "Example", null, null, MediaType.Movies);
        hints["genre"] = "18";
        var second = Scorer().ScoreCandidate(hints, "Example", null, null, MediaType.Movies,
            extendedMetadata: new() { Genres = ["Rock"] });
        Assert.Equal(first.CompositeScore, second.CompositeScore);
        Assert.InRange(first.CompositeScore, 0, .65);
        Assert.Contains("insufficient_identity_evidence", first.AutoAcceptBlockReasons);
    }
    [Fact]
    public void FractionalComicIssueNumbersDoNotMatchTheirIntegerPrefix()
    {
        var hints = Hints(); hints["issue_number"] = "2.5";
        var score = Scorer().ScoreCandidate(hints, "Example", null, "2020", MediaType.Comics,
            extendedMetadata: new() { Series = "Example", IssueNumber = "2.7" });
        Assert.Equal(0, score.FieldScores.Single(row => row.Key == "issue" && row.Role == "weighted").Score);
        Assert.True(score.CompositeScore < .90);
    }
    [Fact]
    public void MinuteSecondClockDurationMatchesWithoutTreatingMinutesAsHours()
    {
        var hints = Hints(); hints.Remove("duration_seconds"); hints["duration"] = "3:00";
        var score = Scorer().ScoreCandidate(hints, "Example", "Creator", "2020", MediaType.Music,
            extendedMetadata: new() { Scope = "track", DurationSeconds = 180, Signals = Hints() });
        Assert.Equal(1, score.FieldScores.Single(row => row.Key == "duration" && row.Role == "weighted").Score);
        Assert.DoesNotContain(score.FieldScores, row => row.Key == "duration" && row.Role == "penalty");
    }

    [Fact]
    public void AlreadyComputedCoverEvidenceDoesNotRequireAnotherProviderOrHashCall()
    {
        var score = Scorer().ScoreCandidate(Hints(), "Example", "Creator", "2020", MediaType.Books,
            extendedMetadata: new() { CoverArtSimilarity = .95 });
        Assert.Contains(score.FieldScores, row => row.Key == "cover" && row.Role == "bonus" && row.Contribution > 0);
    }

    [Theory]
    [InlineData(MediaType.TV, "episode")]
    [InlineData(MediaType.Comics, "issue")]
    public void ExplicitWrongOrdinalCannotBeRescuedByAnIdentifierOrCover(MediaType type, string scope)
    {
        var hints = Hints(); var remote = Hints(); remote["episode_number"] = "9";
        var score = Scorer().ScoreCandidate(hints, "Example", "Creator", "2020", type,
            extendedMetadata: new() { Scope = scope, Signals = remote, Series = "Example", IssueNumber = "9", CoverArtSimilarity = 1 }, structuralBonus: .35);
        Assert.InRange(score.CompositeScore, 0, .5);
        Assert.Contains("structural_identity_contradiction", score.AutoAcceptBlockReasons);
    }

    [Fact]
    public void ProviderKindSignalCannotHideWrongMediaOrWrongMusicScope()
    {
        var wrongBook = Scorer().ScoreCandidate(Hints(), "Example", "Creator", "2020", MediaType.Books,
            extendedMetadata: new() { Signals = new Dictionary<string, string> { { "kind", "feature-movie" } } }, structuralBonus: .35);
        var wrongTrack = Scorer().ScoreCandidate(Hints(), "Example", "Creator", "2020", MediaType.Music,
            extendedMetadata: new() { Scope = "track", Signals = new Dictionary<string, string> { { "kind", "album" } } }, structuralBonus: .35);
        Assert.InRange(wrongBook.CompositeScore, 0, .5); Assert.InRange(wrongTrack.CompositeScore, 0, .5);
    }
    [Fact]
    public void PlaceholderTitleKeepsItsOriginalRejectionEvenWithOtherEvidenceAndBonuses()
    {
        var hints = Hints(); hints["title"] = "Unknown";
        var score = Scorer().ScoreCandidate(hints, "Unknown", "Creator", "2020", MediaType.Books,
            extendedMetadata: new() { Kind = MediaType.Books, CoverArtSimilarity = 1 }, structuralBonus: .35);
        Assert.Equal(0, score.CompositeScore); Assert.Contains("placeholder_title", score.AutoAcceptBlockReasons);
    }

    private static Dictionary<string, string> Hints() => new(StringComparer.OrdinalIgnoreCase)
    {
        ["title"] = "Example",
        ["episode_title"] = "Example",
        ["show_name"] = "Example",
        ["series"] = "Example",
        ["author"] = "Creator",
        ["artist"] = "Creator",
        ["album_artist"] = "Creator",
        ["year"] = "2020",
        ["album"] = "Example",
        ["duration_seconds"] = "180",
        ["season_number"] = "1",
        ["episode_number"] = "2",
        ["issue_number"] = "2",
        ["track_count"] = "10",
        ["publisher"] = "Publisher",
        ["page_count"] = "100",
    };
}
