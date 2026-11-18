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
    public void NamesakePickedByTheWorksSubject()
    {
        var comedian = Namesake("Q5573861", "Australian comedian and actor");
        var player = Namesake("Q352159", "American basketball player (born 1988)");

        var pick = PersonReconciliationService.PickNamesakeByContext(
            [comedian, player], "Stephen Curry MasterClass Learn basketball shooting from the all-time great");

        Assert.Equal("Q352159", pick?.Id);
    }

    [Fact]
    public void NamesakeNotPickedWhenTheWorkSaysNothingOrFitsBoth()
    {
        var comedian = Namesake("Q5573861", "Australian comedian and actor");
        var player = Namesake("Q352159", "American basketball player (born 1988)");

        Assert.Null(PersonReconciliationService.PickNamesakeByContext([comedian, player], "Part 01"));
        Assert.Null(PersonReconciliationService.PickNamesakeByContext(
            [comedian, player], "A comedian turned basketball coach"));
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
