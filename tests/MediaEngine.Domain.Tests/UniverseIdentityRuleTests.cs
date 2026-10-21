using MediaEngine.Domain.Constants;
using MediaEngine.Domain.Services;

namespace MediaEngine.Domain.Tests;

public sealed class UniverseIdentityRuleTests
{
    private static UniverseClaim Claim(string qid, string? label = null) => new(qid, label);

    [Fact]
    public void Choose_FictionalUniverseOutranksEveryOtherStep()
    {
        var choice = UniverseIdentityRule.Choose(new UniverseIdentityInput
        {
            OwnSeriesQid = "Q10",
            FictionalUniverses = [Claim("Q900", "Arrakis")],
            Franchises = [Claim("Q1", "Dune franchise")],
            Series = [Claim("Q10"), Claim("Q2", "Broader series")],
            BasedOn = [Claim("Q3", "Novel")],
        });

        Assert.Equal(new UniverseChoice("Q900", "Arrakis", NarrativeLevel.Universe, UniverseDecisionStep.FictionalUniverse), choice);
    }

    [Fact]
    public void Choose_FranchiseOutranksABroaderSeriesAndBasedOn()
    {
        var choice = UniverseIdentityRule.Choose(new UniverseIdentityInput
        {
            OwnSeriesQid = "Q10",
            Franchises = [Claim("Q800", "Dune")],
            Series = [Claim("Q2", "Broader series")],
            BasedOn = [Claim("Q3", "Novel")],
        });

        Assert.Equal(UniverseDecisionStep.Franchise, choice!.Step);
        Assert.Equal("Q800", choice.Qid);
        Assert.Equal(NarrativeLevel.Franchise, choice.Level);
    }

    [Fact]
    public void Choose_ASeriesOtherThanTheOwnOneOutranksBasedOn()
    {
        var choice = UniverseIdentityRule.Choose(new UniverseIdentityInput
        {
            OwnSeriesQid = "Q190214",
            Series = [Claim("Q190214", "The Lord of the Rings"), Claim("Q26214973", "Peter Jackson's Middle-earth film series")],
            BasedOn = [Claim("Q3", "Novel")],
        });

        Assert.Equal(new UniverseChoice("Q26214973", "Peter Jackson's Middle-earth film series", NarrativeLevel.Series, UniverseDecisionStep.BroaderSeries), choice);
    }

    [Fact]
    public void Choose_BasedOnDecidesWhenNothingBroaderExists()
    {
        var choice = UniverseIdentityRule.Choose(new UniverseIdentityInput
        {
            OwnSeriesQid = "Q18389644",
            Series = [Claim("Q18389644")],
            BasedOn = [Claim("Q19610143", "The Expanse")],
        });

        Assert.Equal(new UniverseChoice("Q19610143", "The Expanse", NarrativeLevel.Franchise, UniverseDecisionStep.BasedOn), choice);
    }

    [Fact]
    public void Choose_TheOwnSeriesDecidesWhenNothingElseIsNamed_AndTakesTheLabelWikidataGaveIt()
    {
        var choice = UniverseIdentityRule.Choose(new UniverseIdentityInput
        {
            OwnSeriesQid = "Q19610143",
            OwnSeriesLabel = "The Expanse books",
            Series = [Claim("Q19610143", "The Expanse")],
        });

        Assert.Equal(new UniverseChoice("Q19610143", "The Expanse", NarrativeLevel.Series, UniverseDecisionStep.OwnSeries), choice);
    }

    [Fact]
    public void Choose_TheOwnSeriesFallsBackToTheLocalNameWhenWikidataGaveNoLabel()
    {
        var choice = UniverseIdentityRule.Choose(new UniverseIdentityInput
        {
            OwnSeriesQid = "Q19610143",
            OwnSeriesLabel = "The Expanse books",
        });

        Assert.Equal("The Expanse books", choice!.Label);
    }

    [Fact]
    public void Choose_NamesNothingWhenThereIsNoSeriesAndNoRelationship()
    {
        Assert.Null(UniverseIdentityRule.Choose(new UniverseIdentityInput()));
    }

    [Fact]
    public void Choose_TheLowestNumericQidWinsWithinOneType()
    {
        var choice = UniverseIdentityRule.Choose(new UniverseIdentityInput
        {
            FictionalUniverses = [Claim("Q1000", "Later"), Claim("Q123", "Earlier"), Claim("Q20")],
        });

        Assert.Equal("Q20", choice!.Qid);

        var withoutTheLowest = UniverseIdentityRule.Choose(new UniverseIdentityInput
        {
            FictionalUniverses = [Claim("Q1000", "Later"), Claim("Q123", "Earlier")],
        });

        Assert.Equal("Q123", withoutTheLowest!.Qid);
        Assert.Equal("Earlier", withoutTheLowest.Label);
    }

    [Fact]
    public void Choose_GivesTheSameAnswerWhateverOrderTheClaimsArriveIn()
    {
        UniverseClaim[] claims = [Claim("Q500"), Claim("Q40"), Claim("Q7000"), Claim("Q41")];

        var forward = UniverseIdentityRule.Choose(new UniverseIdentityInput { Franchises = claims });
        var backward = UniverseIdentityRule.Choose(new UniverseIdentityInput { Franchises = claims.Reverse().ToList() });

        Assert.Equal(forward, backward);
        Assert.Equal("Q40", forward!.Qid);
    }

    [Fact]
    public void Choose_SkipsBlankAndNonQidValues()
    {
        var choice = UniverseIdentityRule.Choose(new UniverseIdentityInput
        {
            FictionalUniverses = [Claim(""), Claim("   "), Claim("not-a-qid"), Claim("P1434"), Claim("Q 12"), Claim("Q77", "Real one")],
        });

        Assert.Equal("Q77", choice!.Qid);
    }

    [Fact]
    public void Choose_SkipsAFictionalUniverseThatIsAlsoOneOfTheSubjectsCharacters()
    {
        var choice = UniverseIdentityRule.Choose(new UniverseIdentityInput
        {
            OwnSeriesQid = "Q10",
            FictionalUniverses = [Claim("Q2695156", "Batman")],
            Franchises = [Claim("Q2695156", "Batman"), Claim("Q55", "DC franchise")],
            FictionalMemberQids = ["Q2695156"],
        });

        Assert.Equal(new UniverseChoice("Q55", "DC franchise", NarrativeLevel.Franchise, UniverseDecisionStep.Franchise), choice);
    }

    [Fact]
    public void Choose_NamesNothingWhenTheOnlyUniverseIsACharacter()
    {
        var choice = UniverseIdentityRule.Choose(new UniverseIdentityInput
        {
            FictionalUniverses = [Claim("Q2695156", "Batman")],
            FictionalMemberQids = ["q2695156"],
        });

        Assert.Null(choice);
    }

    [Fact]
    public async Task CanonicaliseAsync_KeepsTheChosenQidWhenTheLibraryKnowsNothingMore()
    {
        var choice = new UniverseChoice("Q10", "Dune", NarrativeLevel.Franchise, UniverseDecisionStep.Franchise);

        var identity = await UniverseIdentityRule.CanonicaliseAsync(choice, new FakeLookup());

        Assert.Equal(new UniverseIdentity("Q10", "Dune", NarrativeLevel.Franchise, UniverseDecisionStep.Franchise, UniverseCanonicalSource.Chosen, "Q10"), identity);
    }

    [Fact]
    public async Task CanonicaliseAsync_ASeriesShelfThatAlreadySitsInAUniverseWins()
    {
        var lookup = new FakeLookup();
        lookup.ShelfUniverse["Q19610143"] = "Q1000";
        lookup.Records["Q1000"] = new UniverseRecord("Q1000", "The Expanse universe", NarrativeLevel.Universe, null);
        var choice = new UniverseChoice("Q19610143", "The Expanse", NarrativeLevel.Franchise, UniverseDecisionStep.BasedOn);

        var identity = await UniverseIdentityRule.CanonicaliseAsync(choice, lookup);

        Assert.Equal("Q1000", identity.Qid);
        Assert.Equal("The Expanse universe", identity.Label);
        Assert.Equal(NarrativeLevel.Universe, identity.Level);
        Assert.Equal(UniverseCanonicalSource.SeriesShelf, identity.CanonicalSource);
        Assert.Equal("Q19610143", identity.ChosenQid);
        Assert.Equal(UniverseDecisionStep.BasedOn, identity.Step);
    }

    [Fact]
    public async Task CanonicaliseAsync_FollowsTheParentChainToItsTop()
    {
        var lookup = new FakeLookup();
        lookup.Records["Q3"] = new UniverseRecord("Q3", "Series", NarrativeLevel.Series, "Q2");
        lookup.Records["Q2"] = new UniverseRecord("Q2", "Franchise", NarrativeLevel.Franchise, "Q1");
        lookup.Records["Q1"] = new UniverseRecord("Q1", "Universe", NarrativeLevel.Universe, null);
        var choice = new UniverseChoice("Q3", "Series", NarrativeLevel.Series, UniverseDecisionStep.OwnSeries);

        var identity = await UniverseIdentityRule.CanonicaliseAsync(choice, lookup);

        Assert.Equal("Q1", identity.Qid);
        Assert.Equal("Universe", identity.Label);
        Assert.Equal(NarrativeLevel.Universe, identity.Level);
        Assert.Equal(UniverseCanonicalSource.ParentChain, identity.CanonicalSource);
    }

    [Fact]
    public async Task CanonicaliseAsync_AParentWithNoRecordOfItsOwnStillBecomesTheTop()
    {
        var lookup = new FakeLookup();
        lookup.Records["Q3"] = new UniverseRecord("Q3", "Series", NarrativeLevel.Series, "Q2");
        var choice = new UniverseChoice("Q3", "Series", NarrativeLevel.Series, UniverseDecisionStep.OwnSeries);

        var identity = await UniverseIdentityRule.CanonicaliseAsync(choice, lookup);

        Assert.Equal("Q2", identity.Qid);
        Assert.Equal("Q2", identity.Label);
    }

    [Fact]
    public async Task CanonicaliseAsync_StopsAtAParentCycle()
    {
        var lookup = new FakeLookup();
        lookup.Records["Q1"] = new UniverseRecord("Q1", "One", NarrativeLevel.Franchise, "Q2");
        lookup.Records["Q2"] = new UniverseRecord("Q2", "Two", NarrativeLevel.Franchise, "Q1");
        var choice = new UniverseChoice("Q1", "One", NarrativeLevel.Franchise, UniverseDecisionStep.Franchise);

        var identity = await UniverseIdentityRule.CanonicaliseAsync(choice, lookup);

        Assert.Equal("Q2", identity.Qid);
        Assert.True(lookup.RecordReads <= UniverseIdentityRule.MaxParentDepth + 1);
    }

    [Fact]
    public async Task CanonicaliseAsync_FollowsAtMostEightParents()
    {
        var lookup = new FakeLookup();
        for (var i = 1; i <= 20; i++)
        {
            lookup.Records[$"Q{i}"] = new UniverseRecord($"Q{i}", $"Node {i}", NarrativeLevel.Franchise, $"Q{i + 1}");
        }

        var choice = new UniverseChoice("Q1", "Node 1", NarrativeLevel.Franchise, UniverseDecisionStep.Franchise);

        var identity = await UniverseIdentityRule.CanonicaliseAsync(choice, lookup);

        Assert.Equal("Q9", identity.Qid);
    }

    [Fact]
    public void ShouldPresentUniverseShelf_NeedsAtLeastTwoSeriesShelves()
    {
        Assert.False(UniverseIdentityRule.ShouldPresentUniverseShelf("Q1", []));
        Assert.False(UniverseIdentityRule.ShouldPresentUniverseShelf("Q1", [new UniverseSeriesShelf("Q2", "Movies")]));
        Assert.True(UniverseIdentityRule.ShouldPresentUniverseShelf(
            "Q1",
            [new UniverseSeriesShelf("Q2", "Movies"), new UniverseSeriesShelf("Q3", "Books")]));
    }

    [Fact]
    public void ShouldPresentUniverseShelf_TheBookAndAudiobookSeriesOfOneQidDoNotMakeAUniverse()
    {
        Assert.False(UniverseIdentityRule.ShouldPresentUniverseShelf(
            "Q19610143",
            [new UniverseSeriesShelf("Q19610143", "Books"), new UniverseSeriesShelf("Q19610143", "Audiobooks")]));
    }

    [Fact]
    public void ShouldPresentUniverseShelf_ABookSeriesAndATvSeriesThatShareOneQidMakeAUniverse()
    {
        Assert.True(UniverseIdentityRule.ShouldPresentUniverseShelf(
            "Q19610143",
            [new UniverseSeriesShelf("Q19610143", "Books"), new UniverseSeriesShelf("Q19610143", "TV")]));
    }

    [Fact]
    public void ShouldPresentUniverseShelf_BookAndAudiobookSeriesInsideABroaderUniverseDoMakeOne()
    {
        Assert.True(UniverseIdentityRule.ShouldPresentUniverseShelf(
            "Q1000",
            [new UniverseSeriesShelf("Q19610143", "Books"), new UniverseSeriesShelf("Q19610143", "Audiobooks")]));
    }

    [Theory]
    [InlineData("Q42", "Q42")]
    [InlineData("q42", "Q42")]
    [InlineData("  Q42  ", "Q42")]
    [InlineData("", null)]
    [InlineData(null, null)]
    [InlineData("P42", null)]
    [InlineData("Q", null)]
    [InlineData("Q4 2", null)]
    [InlineData("http://www.wikidata.org/entity/Q42", null)]
    public void NormaliseQid_AcceptsQidsAndRejectsEverythingElse(string? value, string? expected)
    {
        Assert.Equal(expected, UniverseIdentityRule.NormaliseQid(value));
    }

    [Fact]
    public void CompareQids_OrdersByNumberNotByText()
    {
        Assert.True(UniverseIdentityRule.CompareQids("Q123", "Q1000") < 0);
        Assert.True(UniverseIdentityRule.CompareQids("Q1000", "Q123") > 0);
        Assert.Equal(0, UniverseIdentityRule.CompareQids("Q007", "Q7"));
        Assert.True(UniverseIdentityRule.CompareQids("Q999999999", "QUNIVERSE") < 0);
    }

    private sealed class FakeLookup : IUniverseCanonicalLookup
    {
        public Dictionary<string, string> ShelfUniverse { get; } = new(StringComparer.OrdinalIgnoreCase);

        public Dictionary<string, UniverseRecord> Records { get; } = new(StringComparer.OrdinalIgnoreCase);

        public int RecordReads { get; private set; }

        public Task<string?> FindShelfUniverseQidAsync(string seriesQid, CancellationToken ct) =>
            Task.FromResult(ShelfUniverse.GetValueOrDefault(seriesQid));

        public Task<UniverseRecord?> FindRecordAsync(string qid, CancellationToken ct)
        {
            RecordReads++;
            return Task.FromResult(Records.GetValueOrDefault(qid));
        }
    }
}
