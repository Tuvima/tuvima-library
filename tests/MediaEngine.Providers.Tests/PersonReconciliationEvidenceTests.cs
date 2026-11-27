using MediaEngine.Providers.Services;
using Tuvima.Wikidata;

namespace MediaEngine.Providers.Tests;

public sealed class PersonReconciliationEvidenceTests
{
    [Theory]
    [InlineData("Narrator", "Q33999", "actor")]
    [InlineData("Performer", "Q177220", "singer")]
    [InlineData("Performer", "Q639669", "musician")]
    [InlineData("Author", "Q49757", "poet")]
    [InlineData("Producer", "Q3282637", "film producer")]
    public void RealQidEvidenceRequiresResolvedCompatibleOccupation(string role, string occupationQid, string label)
    {
        var candidate = Candidate("Q264699", [occupationQid]);
        Assert.False(PersonReconciliationService.HasCorroboratingIdentityEvidence(candidate, role, null));
        Assert.True(PersonReconciliationService.HasCorroboratingIdentityEvidence(candidate, role, null,
            new Dictionary<string, string?> { [occupationQid] = label }));
    }

    [Fact]
    public void ResolvedFootballerAndMissingLabelsStillRejectNamesake()
    {
        var candidate = Candidate("Q4761465", ["Q937857"]);
        Assert.False(PersonReconciliationService.HasCorroboratingIdentityEvidence(candidate, "Performer", "Part 01",
            new Dictionary<string, string?> { ["Q937857"] = "association football player" }));
        Assert.False(PersonReconciliationService.HasCorroboratingIdentityEvidence(candidate, "Performer", "Part 01",
            new Dictionary<string, string?> { ["Q937857"] = null }));
    }

    [Fact]
    public void NotableWorkQidMustResolveBeforeComparingTitleAndEmptyLabelNeverMatches()
    {
        var candidate = Candidate("Q1", [], notableWorks: ["Q2"]);
        Assert.True(PersonReconciliationService.HasCorroboratingIdentityEvidence(candidate, "Unknown", "Project Hail Mary",
            new Dictionary<string, string?> { ["Q2"] = "Project Hail Mary" }));
        Assert.False(PersonReconciliationService.HasCorroboratingIdentityEvidence(candidate, "Unknown", "Project Hail Mary",
            new Dictionary<string, string?> { ["Q2"] = "" }));
    }

    [Fact]
    public void PerformerRejectsExactNameFootballerWithoutWorkEvidence()
    {
        var candidate = Candidate("Q4761465", ["association football player"]);

        Assert.False(PersonReconciliationService.HasCorroboratingIdentityEvidence(
            candidate, "Performer", "Part 01"));
    }

    [Fact]
    public void AuthorAcceptsCompatibleWriterOccupation()
    {
        var candidate = Candidate("Q18590295", ["writer", "novelist"]);

        Assert.True(PersonReconciliationService.HasCorroboratingIdentityEvidence(
            candidate, "Author", "Project Hail Mary"));
    }

    [Fact]
    public void PerformerAcceptsMusicalGroupWithoutHumanOccupation()
    {
        var candidate = Candidate("Q189644", [], isGroup: true);

        Assert.True(PersonReconciliationService.HasCorroboratingIdentityEvidence(
            candidate, "Performer", "Get Lucky"));
    }

    [Fact]
    public void UnknownRoleRequiresSpecificWorkEvidence()
    {
        var unsupported = Candidate("Q1", []);
        var supported = Candidate("Q2", [], notableWorks: ["Project Hail Mary"]);

        Assert.False(PersonReconciliationService.HasCorroboratingIdentityEvidence(
            unsupported, "Unknown", "Part 01"));
        Assert.True(PersonReconciliationService.HasCorroboratingIdentityEvidence(
            supported, "Unknown", "Project Hail Mary"));
    }

    [Fact]
    public void NamesakeTheWorksSubjectPointsTo_IsFavouredOverTheChosenPerson()
    {
        var comedian = Namesake("Q5573861", "Australian comedian and actor");
        var player = Namesake("Q352159", "American basketball player (born 1988)");

        Assert.True(PersonReconciliationService.ContextFavoursAnotherNamesake(
            [comedian, player], "Q5573861", "Stephen Curry MasterClass Learn basketball shooting from the all-time great"));
        Assert.True(PersonReconciliationService.ContextFavoursAnotherNamesake(
            [comedian, player], "Q5573861", "Teaches basketballers how to shoot"));
    }

    [Fact]
    public void ChosenPersonStands_WhenTheWorkSaysNothing_FitsTheChosenPerson_OrFitsBoth()
    {
        var comedian = Namesake("Q5573861", "Australian comedian and actor");
        var player = Namesake("Q352159", "American basketball player (born 1988)");

        Assert.False(PersonReconciliationService.ContextFavoursAnotherNamesake([comedian, player], "Q5573861", "Part 01"));
        Assert.False(PersonReconciliationService.ContextFavoursAnotherNamesake([comedian, player], "Q352159", "Learn basketball shooting"));
        Assert.False(PersonReconciliationService.ContextFavoursAnotherNamesake(
            [Namesake("Q1", "basketball coach"), player], "Q1", "Learn basketball shooting"));
        Assert.False(PersonReconciliationService.ContextFavoursAnotherNamesake([comedian], "Q5573861", "basketball"));
    }

    [Fact]
    public void SameNamePeople_TheOneTheWorksTextFitsIsChosen_AndNoFitLeavesThemUnmatched()
    {
        var comedian = Namesake("Q5573861", "Australian comedian and actor");
        var player = Namesake("Q352159", "American basketball player (born 1988)");

        var switched = PersonReconciliationService.ChooseNamesakeFromContext([comedian, player], "MasterClass NBA champion teaches shooting", "Q5573861");
        Assert.Equal(PersonReconciliationService.NamesakeOutcome.Switch, switched.Outcome);
        Assert.Equal("Q352159", switched.Person!.Id);
        Assert.Equal(PersonReconciliationService.NamesakeOutcome.Keep,
            PersonReconciliationService.ChooseNamesakeFromContext([comedian, player], "Learn basketball", "Q352159").Outcome);
        Assert.Equal(PersonReconciliationService.NamesakeOutcome.Unmatched,
            PersonReconciliationService.ChooseNamesakeFromContext([comedian, player], "Part 01", "Q5573861").Outcome);
        Assert.Equal(PersonReconciliationService.NamesakeOutcome.Keep,
            PersonReconciliationService.ChooseNamesakeFromContext([comedian], "Part 01", "Q5573861").Outcome);
    }

    [Theory]
    [InlineData("American banker", "Actor", false)]
    [InlineData("Scottish researcher", "Actor", false)]
    [InlineData("British politician", "Director", false)]
    [InlineData("English actor", "Actor", true)]
    [InlineData("American film director", "Director", true)]
    [InlineData("Chinese poet", "Actor", false)]
    [InlineData("Australian television executive", "Actor", false)]
    [InlineData("English film and television actor", "Actor", true)]
    [InlineData(null, "Actor", false)]
    [InlineData("American banker", "Cinematographer", true)]
    [InlineData("American banker", null, true)]
    public void ANamesakeIsOnlyPickedWhenTheirDescriptionFitsTheCreditedJob(string? description, string? role, bool expected)
    {
        Assert.Equal(expected, PersonReconciliationService.DescriptionFitsRole(description, role));
    }

    [Fact]
    public void ABankerSharingWordsWithTheWorksText_IsNotChosenAsTheActor()
    {
        var banker = Namesake("Q55609145", "American banker in New York");
        var actor = Namesake("Q1", "American television actor");

        var choice = PersonReconciliationService.ChooseNamesakeFromContext(
            [banker, actor], "Band of Brothers New York banker miniseries", "Q1", "Actor");

        Assert.NotEqual("Q55609145", choice.Person?.Id);
    }

    private static ReconciliationResult Namesake(string qid, string description) => new()
    {
        Id = qid,
        Name = "Stephen Curry",
        Description = description,
        Score = 91,
    };

    private static PersonSearchResult Candidate(
        string qid,
        IReadOnlyList<string> occupations,
        bool isGroup = false,
        IReadOnlyList<string>? notableWorks = null) => new()
        {
            Found = true,
            Qid = qid,
            CanonicalName = "Same Name",
            Score = 1,
            IsGroup = isGroup,
            Occupations = occupations,
            NotableWorks = notableWorks ?? [],
        };
}
