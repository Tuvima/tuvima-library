using System.Text.Json;
using MediaEngine.Api.Services.Matching;
using MediaEngine.Contracts.Editing;
using MediaEngine.Domain.Configuration;

namespace MediaEngine.Api.Tests;

/// <summary>
/// The Match tab's verdict (universal editor v5, D8 and D34): Strong, Possible or Weak, with a fact the file cannot speak to "not checked".
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
    public void WhenTheTitleDiffers_TheVerdictIsWeak_EvenIfTheYearAgrees() =>
        Assert.Equal(EditorMatchVerdicts.Weak, _calculator.Verdict([
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

    private static readonly EditorMatchIdentifierDto[] FoundIsbn = [new() { Kind = EditorMatchIdentifierKinds.Isbn, Value = "978-0-441-17271-9" }];

    [Fact]
    public void AnAgreeingIdentifier_WithADifferingTitle_ReadsPossible_NotCheck() =>
        Assert.Equal(EditorMatchVerdicts.Possible, _calculator.Verdict([
            _calculator.Title("Dune", "Duna"),
            _calculator.Identifier(FoundIsbn, new Dictionary<string, string> { ["isbn"] = "9780441172719" }),
        ]));

    [Fact]
    public void ADifferingIdentifier_WithAnAgreeingTitle_StillReadsCheck() =>
        Assert.Equal(EditorMatchVerdicts.Weak, _calculator.Verdict([
            _calculator.Title("Dune", "Dune"),
            _calculator.Identifier(FoundIsbn, new Dictionary<string, string> { ["isbn"] = "9780000000000" }),
        ]));

    [Fact]
    public void AnAgreeingIdentifier_DoesNotHideADifferingNumber() =>
        Assert.Equal(EditorMatchVerdicts.Weak, _calculator.Verdict([
            _calculator.Identifier(FoundIsbn, new Dictionary<string, string> { ["isbn"] = "9780441172719" }),
            new MatchFactCheck(MatchFactNames.Number, EditorMatchEvidenceStates.Differs, "3", "5", "Number differs"),
        ]));

    [Theory]
    [InlineData(EditorMatchEvidenceStates.Agrees, 0.95, EditorMatchVerdicts.Strong)]
    [InlineData(EditorMatchEvidenceStates.Agrees, 0.7, EditorMatchVerdicts.Possible)]
    [InlineData(EditorMatchEvidenceStates.Agrees, null, EditorMatchVerdicts.Strong)]
    [InlineData(EditorMatchEvidenceStates.Differs, 0.2, EditorMatchVerdicts.Weak)]
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

    // ── The episode and song rows (v6 D9) ─────────────────────────────────────

    [Fact]
    public void AnEpisode_AtTheSameSeasonAndEpisode_Agrees_AndSaysBothInWords()
    {
        var slot = _calculator.SeasonAndEpisode(2, 3, 2, 3);

        Assert.Equal(EditorMatchEvidenceStates.Agrees, slot.State);
        Assert.Equal("Season 2, Episode 3", slot.Yours);
        Assert.Equal("Season 2, Episode 3", slot.Theirs);
    }

    [Theory]
    [InlineData(2, 3, 2, 4)]
    [InlineData(2, 3, 3, 3)]
    public void AnEpisode_AtAnotherSeasonOrEpisode_Differs(int yoursSeason, int yoursEpisode, int theirsSeason, int theirsEpisode) =>
        Assert.Equal(EditorMatchEvidenceStates.Differs, _calculator.SeasonAndEpisode(yoursSeason, yoursEpisode, theirsSeason, theirsEpisode).State);

    [Fact]
    public void AnEpisode_WhoseFileNamesNoSeason_IsJudgedOnTheEpisodeNumber_AndTheSeasonIsLeftOutOfWhatIsShown()
    {
        var slot = _calculator.SeasonAndEpisode(null, 3, 2, 3);

        Assert.Equal(EditorMatchEvidenceStates.Agrees, slot.State);
        Assert.Equal("Episode 3", slot.Yours);
        Assert.Equal("Season 2, Episode 3", slot.Theirs);
    }

    [Fact]
    public void AnEpisode_WhoseFileNamesNoEpisode_IsNotChecked_AndNeverCountsAgainstTheMatch()
    {
        var slot = _calculator.SeasonAndEpisode(2, null, 2, 3);

        Assert.Equal(EditorMatchEvidenceStates.NotChecked, slot.State);
        Assert.Equal(EditorMatchVerdicts.Strong, _calculator.Verdict([_calculator.Title("Static", "Static"), _calculator.Runtime(2820, 47), slot]));
    }

    [Theory]
    [InlineData("The Last Signal", "The Last Signal", EditorMatchEvidenceStates.Agrees)]
    [InlineData("Last Signal", "The Last Signal", EditorMatchEvidenceStates.Agrees)]
    [InlineData("The Last Signal", "Static Noise", EditorMatchEvidenceStates.Differs)]
    [InlineData(null, "The Last Signal", EditorMatchEvidenceStates.NotChecked)]
    [InlineData("The Last Signal", "", EditorMatchEvidenceStates.NotChecked)]
    public void TheShowOfAnEpisode_AndTheAlbumOfASong_AreComparedLikeATitle_AndAMissingSideIsNotChecked(string? yours, string theirs, string expected)
    {
        Assert.Equal(expected, _calculator.Named(MatchFactNames.Show, "Show", yours, theirs).State);
        Assert.Equal(expected, _calculator.Named(MatchFactNames.Album, "Album", yours, theirs).State);
    }

    [Fact]
    public void TheShippedFacts_ForAnEpisodeAndASong_AreTheOnesTheSpecNames()
    {
        var settings = new MatchVerdictSettings();
        var scoring = JsonSerializer.Deserialize<ScoringSettings>(File.ReadAllText(Path.Combine(FindRepoRoot(), "config", "scoring.json")),
            new JsonSerializerOptions { ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true })!;

        foreach (var shipped in new[] { settings, scoring.MatchVerdict })
        {
            Assert.Equal(["show", "number", "title", "runtime"], shipped.FactsFor("tv_episode"));
            Assert.Equal(["creator", "album", "title", "track_length", "identifier"], shipped.FactsFor("song"));
        }
    }

    [Theory]
    [InlineData("Percy Jackson: The Complete Series", true)]
    [InlineData("The Hobbit Collection", true)]
    [InlineData("Dune Box Set", true)]
    [InlineData("The Sea of Monsters", false)]
    [InlineData("Collections of Poetry", false)]
    public void ATitleIsACollection_WhenItCarriesABoxSetWordAsWholeWords(string title, bool expected) =>
        Assert.Equal(expected, _calculator.IsCollection(title, null));

    [Fact]
    public void AProviderThatSaysItIsACollection_IsOne_WhateverTheTitle() =>
        Assert.True(_calculator.IsCollection("Books I to III", new Dictionary<string, string> { ["is_collection"] = "true" }));

    [Fact]
    public void AnIdentifierNobodyCanCompare_StillShowsWhatEachSideCarries_AndIsNotChecked()
    {
        var onlyTheFile = _calculator.Identifier(
            [new EditorMatchIdentifierDto { Kind = EditorMatchIdentifierKinds.Isbn, Value = "1423103343" }], new Dictionary<string, string>());
        var onlyTheRecord = _calculator.Identifier([], new Dictionary<string, string> { ["isbn"] = "9781423103349" });

        Assert.Equal(EditorMatchEvidenceStates.NotChecked, onlyTheFile.State);
        Assert.Equal("1423103343", onlyTheFile.Yours);
        Assert.Null(onlyTheFile.Theirs);
        Assert.Equal(EditorMatchEvidenceStates.NotChecked, onlyTheRecord.State);
        Assert.Null(onlyTheRecord.Yours);
        Assert.Equal("9781423103349", onlyTheRecord.Theirs);
    }

    [Fact]
    public void AnIdentifierTheFileNames_IsNeverShownBesideARecordIdentifierOfAnotherKind()
    {
        var check = _calculator.Identifier(
            [new EditorMatchIdentifierDto { Kind = EditorMatchIdentifierKinds.Isrc, Value = "USRC17607839" }],
            new Dictionary<string, string> { ["musicbrainz_id"] = "b1a9c0e9-d987-4042-ae91-78d6a3267d69" });

        Assert.Equal(EditorMatchEvidenceStates.NotChecked, check.State);
        Assert.Equal("USRC17607839", check.Yours);
        Assert.Null(check.Theirs);
    }

    [Theory]
    [InlineData("The Mistborn Trilogy, Book 1", "1", false)]
    [InlineData("The Collection", null, true)]
    [InlineData("The Collection", "  ", true)]
    [InlineData("The Mistborn Trilogy", null, true)]
    public void ANumberedSeriesEntry_IsNotACollection_WhateverItsTitleSays(string title, string? seriesNumber, bool expected) =>
        Assert.Equal(expected, _calculator.IsCollection(title, null, seriesNumber));

    [Fact]
    public void AProviderThatSaysItIsACollection_IsOne_EvenWithASeriesNumber() =>
        Assert.True(_calculator.IsCollection("Books I to III", new Dictionary<string, string> { ["is_collection"] = "true" }, "1"));

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
