using MediaEngine.Domain.Constants;
using MediaEngine.Domain.Enums;
using MediaEngine.Domain.Services;

namespace MediaEngine.Domain.Tests;

/// <summary>A person's locked Series choice comes before every automatic rule and is never ambiguous.</summary>
public sealed class SeriesPlacementLockTests
{
    private static readonly Guid ShelfA = Guid.Parse("00000000-0000-0000-0000-0000000000a1");
    private static readonly Guid ShelfB = Guid.Parse("00000000-0000-0000-0000-0000000000a2");
    private static readonly Guid ShelfC = Guid.Parse("00000000-0000-0000-0000-0000000000c3");

    private static SeriesManifestMembership MainSequence(Guid shelf, string qid)
        => new(shelf, qid, "Books", SeriesMembershipScopeNames.MainSequence, SeriesMembershipOrigin.WikidataSeries);

    [Fact]
    public void LockedToAShelf_ReturnsThatShelf_EvenWhenTwoOtherSeriesTieAtMainSequence()
    {
        var decision = SeriesPlacementRule.Decide(new SeriesPlacementInput(
            nameof(MediaType.Books),
            [MainSequence(ShelfA, "QA"), MainSequence(ShelfB, "QB")],
            Retail: null,
            Lock: new SeriesPlacementLock(ShelfC, "QC", "Books")));

        Assert.Equal(SeriesPlacementKind.Placed, decision.Kind);
        Assert.Equal(SeriesPlacementBasis.PersonChoice, decision.Basis);
        Assert.Equal(ShelfC, decision.ShelfCollectionId);
        Assert.Equal(SeriesMembershipScopeNames.MainSequence, decision.Scope);
    }

    [Fact]
    public void LockedToAShelfThatItsManifestListsAsSupplementary_KeepsThatScope()
    {
        var decision = SeriesPlacementRule.Decide(new SeriesPlacementInput(
            nameof(MediaType.Books),
            [
                MainSequence(ShelfA, "QA"),
                new SeriesManifestMembership(ShelfB, "QB", "Books", SeriesMembershipScopeNames.Supplementary, SeriesMembershipOrigin.WikidataSeries),
            ],
            Retail: null,
            Lock: new SeriesPlacementLock(ShelfB, "QB", "Books")));

        Assert.Equal(ShelfB, decision.ShelfCollectionId);
        Assert.Equal(SeriesMembershipScopeNames.Supplementary, decision.Scope);
    }

    [Fact]
    public void LockedToAShelfListedThroughAParentCollection_IsCollectedContent()
    {
        var decision = SeriesPlacementRule.Decide(new SeriesPlacementInput(
            nameof(MediaType.Books),
            [new SeriesManifestMembership(
                ShelfC, "QParent", "Books", SeriesMembershipScopeNames.MainSequence, SeriesMembershipOrigin.WikidataParentCollection,
                [new SeriesParentListing("QA", "Books", ShelfA)])],
            Retail: null,
            Lock: new SeriesPlacementLock(ShelfA, "QA", "Books")));

        Assert.Equal(ShelfA, decision.ShelfCollectionId);
        Assert.Equal(SeriesMembershipScopeNames.CollectedContent, decision.Scope);
    }

    [Fact]
    public void LockedToNoSeries_PlacesNothing_EvenWhenAManifestAndARetailIdentityNameASeries()
    {
        var decision = SeriesPlacementRule.Decide(new SeriesPlacementInput(
            nameof(MediaType.Books),
            [MainSequence(ShelfA, "QA")],
            new SeriesShelfIdentity("Books", "Dune", "QDune", null),
            Lock: SeriesPlacementLock.NoSeries));

        Assert.Equal(SeriesPlacementKind.Unplaced, decision.Kind);
        Assert.Equal(SeriesPlacementBasis.PersonChoice, decision.Basis);
        Assert.Null(decision.ShelfCollectionId);
    }

    [Fact]
    public void WithoutALock_TheRuleStillDecidesAutomatically()
    {
        var decision = SeriesPlacementRule.Decide(new SeriesPlacementInput(
            nameof(MediaType.Books),
            [MainSequence(ShelfA, "QA"), MainSequence(ShelfB, "QB")],
            Retail: null));

        Assert.Equal(SeriesPlacementKind.Ambiguous, decision.Kind);
    }

    [Theory]
    [InlineData("Books", true)]
    [InlineData("Audiobooks", true)]
    [InlineData("Comics", true)]
    [InlineData("Movies", true)]
    [InlineData("TV", false)]
    [InlineData("Music", false)]
    [InlineData(null, false)]
    public void OnlyMediaTypesWithSeriesShelves_UseSeriesShelves(string? mediaType, bool expected)
    {
        Assert.Equal(expected, SeriesPlacementRule.UsesSeriesShelves(mediaType));
    }
}
