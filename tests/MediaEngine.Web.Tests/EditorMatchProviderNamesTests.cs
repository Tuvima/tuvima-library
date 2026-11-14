using MediaEngine.Contracts.Editing;
using MediaEngine.Contracts.Matching;
using MediaEngine.Web.Services.Editing;

namespace MediaEngine.Web.Tests;

/// <summary>
/// A staged match is worded with the source's product name ("Apple Books", "TheTVDB"), never its key (universal editor v7 D4):
/// the Match tab's staged note and Review's After line both read this label.
/// </summary>
public sealed class EditorMatchProviderNamesTests
{
    private static readonly IReadOnlyList<EditorMatchProviderDto> Providers =
    [
        new() { Name = "apple_api", DisplayName = "Apple Books" },
        new() { Name = "tvdb", DisplayName = "TheTVDB" },
        new() { Name = "musicbrainz", DisplayName = "MusicBrainz" },
    ];

    private static ItemCanonicalRetailCandidateDto Candidate(string providerName, string title = "Dune") => new()
    {
        ProviderId = providerName,
        ProviderName = providerName,
        ProviderItemId = "1",
        Title = title,
    };

    [Fact]
    public void AnAppleResult_IsNamedAppleBooks_NotByItsKey()
    {
        var facts = EditorMatchRules.FactsOf(Candidate("apple_api"), Providers);

        Assert.Equal("Apple Books", facts.Provider);
        Assert.Equal("Dune (Apple Books)", EditorMatchRules.StagedLabel(facts));
    }

    [Fact]
    public void ATvdbResult_IsNamedTheTvdb_NotByItsKey()
    {
        var facts = EditorMatchRules.FactsOf(Candidate("tvdb", "Severance"), Providers);

        Assert.Equal("Severance (TheTVDB)", EditorMatchRules.StagedLabel(facts));
    }

    [Theory]
    [InlineData("mystery_api")]
    [InlineData("")]
    public void AResultWhoseSourceTheListDoesNotName_ShowsTheTitleAlone_NeverTheKey(string key)
    {
        var facts = EditorMatchRules.FactsOf(Candidate(key), Providers);

        Assert.Null(facts.Provider);
        Assert.Equal("Dune", EditorMatchRules.StagedLabel(facts));
    }

    [Fact]
    public void WithNoProviderList_TheLabelIsTheTitleAlone()
    {
        Assert.Equal("Dune", EditorMatchRules.StagedLabel(EditorMatchRules.FactsOf(Candidate("apple_api"))));
    }

    [Fact]
    public void TheSourceIsFoundByTheCandidatesProviderId_WhenItsNameIsNotInTheList()
    {
        var candidate = Candidate("apple_books");
        candidate.ProviderId = "apple_api";

        Assert.Equal("Apple Books", EditorMatchRules.ProductNameOf(candidate, Providers));
    }
}
