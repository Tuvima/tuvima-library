using System.Text.Json;
using MediaEngine.Api.Services.Matching;
using MediaEngine.Contracts.Editing;
using MediaEngine.Domain.Configuration;

namespace MediaEngine.Api.Tests;

/// <summary>
/// The Match tab's verdict (universal editor v5, D8 and D34): Strong, Possible or Check, with a fact the file cannot speak to "not checked".
/// The bands and tolerances come from <c>match_verdict</c> in <c>config/scoring.json</c>.
/// </summary>
public sealed class EditorMatchVerdictCalculatorTests
{
    private readonly EditorMatchVerdictCalculator _calculator = new(new MatchVerdictSettings());

    [Fact]
    public void WhenTheTitleAndYearAgreeAndNothingDiffers_TheVerdictIsStrong() =>
        Assert.Equal(EditorMatchVerdicts.Strong, _calculator.Verdict([
            _calculator.Title("The Matrix", "The Matrix"),
            _calculator.Year("1999", "1999"),
            _calculator.Runtime(null, 136),
        ]));

    [Fact]
    public void WhenTheTitleDiffers_TheVerdictIsCheck_EvenIfTheYearAgrees() =>
        Assert.Equal(EditorMatchVerdicts.Check, _calculator.Verdict([
            _calculator.Title("The Matrix", "Matrix Revolutions"),
            _calculator.Year("1999", "1999"),
        ]));

    [Fact]
    public void WhenOnlyTheYearDiffers_TheVerdictIsPossible() =>
        Assert.Equal(EditorMatchVerdicts.Possible, _calculator.Verdict([
            _calculator.Title("Dune", "Dune"),
            _calculator.Year("1984", "2021"),
        ]));

    [Fact]
    public void WhenOnlyOneFactCouldBeChecked_TheVerdictIsPossibleNotStrong() =>
        Assert.Equal(EditorMatchVerdicts.Possible, _calculator.Verdict([
            _calculator.Title("Dune", "Dune"),
            _calculator.Year(null, "2021"),
        ]));

    [Fact]
    public void AFactTheFileCannotSpeakTo_IsNotChecked()
    {
        var year = _calculator.Year(null, "1999");

        Assert.Equal(EditorMatchEvidenceStates.NotChecked, year.State);
        Assert.False(year.IsChecked);
        Assert.Equal("Year not checked", year.Text);
    }

    [Theory]
    [InlineData(8160d, 136d, EditorMatchEvidenceStates.Agrees)]
    [InlineData(8160d, 137d, EditorMatchEvidenceStates.Agrees)]
    [InlineData(8160d, 134.5d, EditorMatchEvidenceStates.Agrees)]
    [InlineData(8160d, 120d, EditorMatchEvidenceStates.Differs)]
    [InlineData(null, 136d, EditorMatchEvidenceStates.NotChecked)]
    [InlineData(8160d, null, EditorMatchEvidenceStates.NotChecked)]
    public void AMeasuredLength_IsConsistentWithARoundedProviderRuntime_WithinTheTolerance(double? seconds, double? minutes, string state) =>
        Assert.Equal(state, _calculator.Runtime(seconds, minutes).State);

    [Fact]
    public void ATrackLength_IsJudgedInSeconds()
    {
        Assert.Equal(EditorMatchEvidenceStates.Agrees, _calculator.TrackLength(254.2, 255).State);
        Assert.Equal(EditorMatchEvidenceStates.Differs, _calculator.TrackLength(254, 270).State);
    }

    [Theory]
    [InlineData("Frank Herbert", "Herbert, Frank", true)]
    [InlineData("Frank Herbert", "Brian Herbert", false)]
    public void ANameIsTheSameInAnyOrder(string a, string b, bool same) =>
        Assert.Equal(same, EditorMatchVerdictCalculator.SameName(a, b));

    [Theory]
    [InlineData("The Matrix", "Matrix", true)]
    [InlineData("Fast & Furious", "Fast and Furious", true)]
    [InlineData("Dune", "Dune Messiah", false)]
    public void ATitleIsTheSameIgnoringArticlesAndPunctuation(string a, string b, bool same) =>
        Assert.Equal(same, EditorMatchVerdictCalculator.SameTitle(a, b));

    [Fact]
    public void AnIdentifierTheRecordAlsoCarries_Agrees_AndADifferentOneDiffers()
    {
        var found = new[] { new EditorMatchIdentifierDto { Kind = EditorMatchIdentifierKinds.Isbn, Value = "978-0-441-17271-9" } };

        Assert.Equal(
            EditorMatchEvidenceStates.Agrees,
            _calculator.Identifier(found, new Dictionary<string, string> { ["isbn"] = "9780441172719" }).State);
        Assert.Equal(
            EditorMatchEvidenceStates.Differs,
            _calculator.Identifier(found, new Dictionary<string, string> { ["isbn"] = "9780000000000" }).State);
        Assert.Equal(
            EditorMatchEvidenceStates.NotChecked,
            _calculator.Identifier(found, new Dictionary<string, string>()).State);
    }

    [Theory]
    [InlineData(EditorMatchEvidenceStates.Agrees, 0.95, EditorMatchVerdicts.Strong)]
    [InlineData(EditorMatchEvidenceStates.Agrees, 0.7, EditorMatchVerdicts.Possible)]
    [InlineData(EditorMatchEvidenceStates.Agrees, null, EditorMatchVerdicts.Strong)]
    [InlineData(EditorMatchEvidenceStates.Differs, 0.2, EditorMatchVerdicts.Check)]
    [InlineData(EditorMatchEvidenceStates.NotChecked, null, null)]
    public void AWhyRow_ReadsStrongPossibleOrCheck_FromItsFactAndItsStoredConfidence(string state, double? score, string? expected) =>
        Assert.Equal(expected, _calculator.RowVerdict(state, score));

    [Fact]
    public void TheShippedScoringFile_CarriesTheVerdictBandsAndTheFactsOfEveryKind()
    {
        var path = Path.Combine(FindRepoRoot(), "config", "scoring.json");
        var scoring = JsonSerializer.Deserialize<ScoringSettings>(File.ReadAllText(path), new JsonSerializerOptions { ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true })!;

        Assert.Equal(0.85, scoring.MatchVerdict.StrongMinScore);
        Assert.Equal(0.60, scoring.MatchVerdict.PossibleMinScore);
        Assert.Equal(2, scoring.MatchVerdict.RuntimeToleranceMinutes);
        foreach (var kind in new[] { "movie", "tv_episode", "album", "song", "book", "audiobook", "comic", "video" })
        {
            Assert.NotEmpty(scoring.MatchVerdict.FactsFor(kind));
        }
    }

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "MediaEngine.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("Could not locate repository root.");
    }
}
