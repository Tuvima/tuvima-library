using MediaEngine.Domain.Constants;
using MediaEngine.Domain.Enums;
using MediaEngine.Domain.Services;

namespace MediaEngine.Domain.Tests;

public sealed class SeriesPlacementRuleTests
{
    private static readonly Guid BookSeriesShelf = Guid.Parse("00000000-0000-0000-0000-0000000000a1");
    private static readonly Guid AudiobookSeriesShelf = Guid.Parse("00000000-0000-0000-0000-0000000000a2");
    private static readonly Guid MemorysLegionShelf = Guid.Parse("00000000-0000-0000-0000-0000000000b1");
    private static readonly Guid OtherSeriesShelf = Guid.Parse("00000000-0000-0000-0000-0000000000c1");

    private static SeriesManifestMembership Series(Guid shelf, string qid, string mediaType, string scope = SeriesMembershipScopeNames.MainSequence)
        => new(shelf, qid, mediaType, scope, SeriesMembershipOrigin.WikidataSeries);

    private static SeriesManifestMembership ParentCollection(Guid shelf, string qid, string mediaType, params SeriesParentListing[] listedIn)
        => new(shelf, qid, mediaType, SeriesMembershipScopeNames.MainSequence, SeriesMembershipOrigin.WikidataParentCollection, listedIn);

    private static SeriesShelfIdentity RetailLabel(string mediaType, string label)
        => new(mediaType, label, null, $"local:{mediaType.ToLowerInvariant()}:{label.ToLowerInvariant()}");

    [Fact]
    public void ManifestThatListsTheWork_BeatsTheRetailIdentity_AndGivesTheManifestScope()
    {
        var decision = SeriesPlacementRule.Decide(new SeriesPlacementInput(
            nameof(MediaType.Books),
            [Series(BookSeriesShelf, "Q19610143", "Books", SeriesMembershipScopeNames.CollectedContent)],
            RetailLabel("Books", "Something else entirely")));

        Assert.Equal(SeriesPlacementKind.Placed, decision.Kind);
        Assert.Equal(SeriesPlacementBasis.WikidataManifest, decision.Basis);
        Assert.Equal(BookSeriesShelf, decision.ShelfCollectionId);
        Assert.Equal(SeriesMembershipScopeNames.CollectedContent, decision.Scope);
    }

    [Fact]
    public void WorkListedInAParentCollection_GoesToTheSeriesThatListsThatParent_AsCollectedContent()
    {
        // Gods of Risk: listed in Memory's Legion, which The Expanse lists. Memory's Legion never becomes its shelf.
        var decision = SeriesPlacementRule.Decide(new SeriesPlacementInput(
            nameof(MediaType.Books),
            [ParentCollection(MemorysLegionShelf, "QMemorysLegion", "Books", new SeriesParentListing("QExpanse", "Books", BookSeriesShelf))],
            RetailLabel("Books", "Memory's Legion")));

        Assert.Equal(SeriesPlacementKind.Placed, decision.Kind);
        Assert.Equal(SeriesPlacementBasis.ParentCollectionManifest, decision.Basis);
        Assert.Equal(BookSeriesShelf, decision.ShelfCollectionId);
        Assert.Equal(SeriesMembershipScopeNames.CollectedContent, decision.Scope);
    }

    [Fact]
    public void ParentCollectionNoSeriesLists_PlacesNothing_WhenThereIsNoRetailIdentity()
    {
        var decision = SeriesPlacementRule.Decide(new SeriesPlacementInput(
            nameof(MediaType.Books),
            [ParentCollection(MemorysLegionShelf, "QMemorysLegion", "Books")],
            Retail: null));

        Assert.Equal(SeriesPlacementKind.Unplaced, decision.Kind);
    }

    [Fact]
    public void ASeriesOfAnotherMediaType_IsNeverACandidate()
    {
        var decision = SeriesPlacementRule.Decide(new SeriesPlacementInput(
            nameof(MediaType.Audiobooks),
            [Series(BookSeriesShelf, "QExpanse", "Books")],
            Retail: null));

        Assert.Equal(SeriesPlacementKind.Unplaced, decision.Kind);
    }

    [Fact]
    public void BookAndAudiobookSeriesWithTheSameQid_EachWorkGoesToItsOwnMediaTypesSeries()
    {
        SeriesManifestMembership[] memberships =
        [
            Series(BookSeriesShelf, "QExpanse", "Books"),
            Series(AudiobookSeriesShelf, "QExpanse", "Audiobooks"),
        ];

        var book = SeriesPlacementRule.Decide(new SeriesPlacementInput(nameof(MediaType.Books), memberships, null));
        var audiobook = SeriesPlacementRule.Decide(new SeriesPlacementInput(nameof(MediaType.Audiobooks), memberships, null));

        Assert.Equal(BookSeriesShelf, book.ShelfCollectionId);
        Assert.Equal(AudiobookSeriesShelf, audiobook.ShelfCollectionId);
    }

    [Fact]
    public void TwoSeriesBothListingTheWorkAsMainSequence_AreAmbiguous_AndNothingIsPlaced()
    {
        var decision = SeriesPlacementRule.Decide(new SeriesPlacementInput(
            nameof(MediaType.Books),
            [Series(BookSeriesShelf, "QOne", "Books"), Series(OtherSeriesShelf, "QTwo", "Books")],
            RetailLabel("Books", "QOne")));

        Assert.Equal(SeriesPlacementKind.Ambiguous, decision.Kind);
        Assert.Null(decision.ShelfCollectionId);
        Assert.Equal(["QOne", "QTwo"], decision.Candidates.Select(c => c.SeriesQid).ToArray());
    }

    [Fact]
    public void ATieBelowMainSequence_IsNotAmbiguous_TheSeriesTheWorkClaimsWins()
    {
        var decision = SeriesPlacementRule.Decide(new SeriesPlacementInput(
            nameof(MediaType.Books),
            [
                Series(OtherSeriesShelf, "QTwo", "Books", SeriesMembershipScopeNames.Supplementary),
                Series(BookSeriesShelf, "QOne", "Books", SeriesMembershipScopeNames.Supplementary),
            ],
            null,
            ClaimedSeriesQid: "qtwo"));

        Assert.Equal(SeriesPlacementKind.Placed, decision.Kind);
        Assert.Equal(OtherSeriesShelf, decision.ShelfCollectionId);
    }

    [Fact]
    public void ATieBelowMainSequence_WithNoClaim_PicksTheLowestQid_WhateverTheOrder()
    {
        SeriesManifestMembership[] memberships =
        [
            Series(OtherSeriesShelf, "QB", "Books", SeriesMembershipScopeNames.CollectedContent),
            Series(BookSeriesShelf, "QA", "Books", SeriesMembershipScopeNames.CollectedContent),
        ];

        var forward = SeriesPlacementRule.Decide(new SeriesPlacementInput(nameof(MediaType.Books), memberships, null));
        var reversed = SeriesPlacementRule.Decide(new SeriesPlacementInput(nameof(MediaType.Books), memberships.Reverse().ToArray(), null));

        Assert.Equal(BookSeriesShelf, forward.ShelfCollectionId);
        Assert.Equal(BookSeriesShelf, reversed.ShelfCollectionId);
        Assert.Equal(SeriesPlacementKind.Placed, forward.Kind);
    }

    [Fact]
    public void ATieAtMainSequence_StaysAmbiguous_EvenWhenOneSeriesIsClaimed()
    {
        var decision = SeriesPlacementRule.Decide(new SeriesPlacementInput(
            nameof(MediaType.Books),
            [Series(BookSeriesShelf, "QOne", "Books"), Series(OtherSeriesShelf, "QTwo", "Books")],
            null,
            ClaimedSeriesQid: "QOne"));

        Assert.Equal(SeriesPlacementKind.Ambiguous, decision.Kind);
        Assert.Contains("main sequence", decision.Reason);
    }

    [Fact]
    public void MainSequenceMembership_BeatsASupplementaryOneInAnotherSeries()
    {
        var decision = SeriesPlacementRule.Decide(new SeriesPlacementInput(
            nameof(MediaType.Books),
            [
                Series(OtherSeriesShelf, "QOther", "Books", SeriesMembershipScopeNames.Supplementary),
                Series(BookSeriesShelf, "QOne", "Books"),
            ],
            null));

        Assert.Equal(SeriesPlacementKind.Placed, decision.Kind);
        Assert.Equal(BookSeriesShelf, decision.ShelfCollectionId);
    }

    [Fact]
    public void TheSameSeriesReachedTwice_CountsOnce_AtItsStrongestScope()
    {
        var decision = SeriesPlacementRule.Decide(new SeriesPlacementInput(
            nameof(MediaType.Books),
            [
                Series(BookSeriesShelf, "QExpanse", "Books", SeriesMembershipScopeNames.CollectedContent),
                Series(BookSeriesShelf, "QExpanse", "Books", SeriesMembershipScopeNames.MainSequence),
            ],
            null));

        Assert.Equal(SeriesPlacementKind.Placed, decision.Kind);
        Assert.Equal(SeriesMembershipScopeNames.MainSequence, decision.Scope);
    }

    [Fact]
    public void NoWikidataManifest_FallsBackToTheProviderManifestThenTheRetailIdentity_AsMainSequence()
    {
        var provider = SeriesPlacementRule.Decide(new SeriesPlacementInput(
            nameof(MediaType.Comics),
            [new SeriesManifestMembership(BookSeriesShelf, "comicvine:volume:1", "Comics", SeriesMembershipScopeNames.MainSequence, SeriesMembershipOrigin.ProviderSequence)],
            RetailLabel("Comics", "Saga")));
        var retail = SeriesPlacementRule.Decide(new SeriesPlacementInput(nameof(MediaType.Comics), [], RetailLabel("Comics", "Saga")));

        Assert.Equal(SeriesPlacementBasis.ProviderManifest, provider.Basis);
        Assert.Equal(BookSeriesShelf, provider.ShelfCollectionId);
        Assert.Equal(SeriesPlacementBasis.RetailIdentity, retail.Basis);
        Assert.Null(retail.ShelfCollectionId);
        Assert.Equal(SeriesMembershipScopeNames.MainSequence, retail.Scope);
    }

    [Fact]
    public void TheDecision_DoesNotDependOnTheOrderTheEvidenceArrivesIn()
    {
        SeriesManifestMembership[] memberships =
        [
            ParentCollection(MemorysLegionShelf, "QMemorysLegion", "Books", new SeriesParentListing("QExpanse", "Books", BookSeriesShelf)),
            Series(BookSeriesShelf, "QExpanse", "Books", SeriesMembershipScopeNames.CollectedContent),
            Series(OtherSeriesShelf, "QOther", "Books", SeriesMembershipScopeNames.BroaderContext),
        ];
        var retail = RetailLabel("Books", "The Expanse");

        var forward = SeriesPlacementRule.Decide(new SeriesPlacementInput(nameof(MediaType.Books), memberships, retail));
        var reversed = SeriesPlacementRule.Decide(new SeriesPlacementInput(nameof(MediaType.Books), memberships.Reverse().ToArray(), retail));

        Assert.Equal(forward, reversed with { Candidates = forward.Candidates });
        Assert.Equal(BookSeriesShelf, forward.ShelfCollectionId);
    }

    [Fact]
    public void SeriesIdentityKey_IncludesTheMediaType_SoTwoMediaTypesAreTwoSeries()
    {
        var book = new SeriesShelfIdentity("Books", "The Expanse", "Q19610143", null);
        var audiobook = new SeriesShelfIdentity("Audiobooks", "The Expanse", "q19610143", null);

        Assert.NotEqual(book.Key, audiobook.Key);
        Assert.Equal(book.Key, new SeriesShelfIdentity("books", "x", "Q19610143", "tvdb:tv:1").Key);
    }
}
