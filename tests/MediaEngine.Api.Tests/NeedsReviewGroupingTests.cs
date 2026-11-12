using MediaEngine.Api.Services.ReadServices;
using MediaEngine.Contracts.Review;
using MediaEngine.Domain.Constants;

namespace MediaEngine.Api.Tests;

/// <summary>The one rule that puts a Needs Review row in a group (Needs Review redesign D1).</summary>
public sealed class NeedsReviewGroupingTests
{
    [Theory]
    [InlineData(ReviewTrigger.MediaInspectionFailed)]
    [InlineData(ReviewTrigger.WritebackFailed)]
    [InlineData(ReviewTrigger.NameCollision)]
    [InlineData(ReviewTrigger.BatchEditFailed)]
    public void AFileProblemReason_IsAFileProblem_EvenWithASuggestion(string trigger)
    {
        Assert.Equal(NeedsReviewGroups.FileProblem, NeedsReviewGrouping.GroupFor(trigger, hasBestGuess: false));
        Assert.Equal(NeedsReviewGroups.FileProblem, NeedsReviewGrouping.GroupFor(trigger, hasBestGuess: true));
    }

    [Theory]
    [InlineData(ReviewTrigger.MovieMatchedAsTv)]
    [InlineData(ReviewTrigger.AmbiguousSeriesMembership)]
    [InlineData(ReviewTrigger.AmbiguousMediaType)]
    [InlineData(ReviewTrigger.RootWatchFolder)]
    [InlineData(ReviewTrigger.MetadataConflict)]
    public void AQuestionOnlyAPersonCanAnswer_IsAChoice_WhateverElseTheEngineKnows(string trigger)
    {
        Assert.Equal(NeedsReviewGroups.Choose, NeedsReviewGrouping.GroupFor(trigger, hasBestGuess: false));
        Assert.Equal(NeedsReviewGroups.Choose, NeedsReviewGrouping.GroupFor(trigger, hasBestGuess: true));
    }

    [Fact]
    public void AnAmbiguousRetailMatch_IsAChoiceWhenSeveralAnswersAreOffered_AndReadyWhenOnlyOneIs()
    {
        Assert.Equal(NeedsReviewGroups.Choose, NeedsReviewGrouping.GroupFor(ReviewTrigger.RetailMatchAmbiguous, hasBestGuess: false));
        Assert.Equal(NeedsReviewGroups.Ready, NeedsReviewGrouping.GroupFor(ReviewTrigger.RetailMatchAmbiguous, hasBestGuess: true));
    }

    [Theory]
    [InlineData(ReviewTrigger.LowConfidence)]
    [InlineData(ReviewTrigger.MultipleQidMatches)]
    [InlineData(ReviewTrigger.PlaceholderTitle)]
    public void AnyOtherReason_IsReadyWhenItHasABestGuess_AndNoMatchWhenItDoesNot(string trigger)
    {
        Assert.Equal(NeedsReviewGroups.Ready, NeedsReviewGrouping.GroupFor(trigger, hasBestGuess: true));
        Assert.Equal(NeedsReviewGroups.NoMatch, NeedsReviewGrouping.GroupFor(trigger, hasBestGuess: false));
    }

    [Theory]
    [InlineData(ReviewTrigger.RetailMatchFailed)]
    [InlineData(ReviewTrigger.StagedUnidentifiable)]
    [InlineData(ReviewTrigger.FromMerge)]
    [InlineData("SomeReasonAddedLater")]
    [InlineData(null)]
    public void NothingFound_AnUnknownReasonOrAFile_IsNoMatchFound(string? trigger)
    {
        Assert.Equal(NeedsReviewGroups.NoMatch, NeedsReviewGrouping.GroupFor(trigger, hasBestGuess: false));
    }

    [Fact]
    public void TheGroupsSortReadyChooseNoMatchFileProblem()
    {
        var sorted = new[] { NeedsReviewGroups.FileProblem, NeedsReviewGroups.NoMatch, NeedsReviewGroups.Ready, NeedsReviewGroups.Choose }
            .OrderBy(NeedsReviewGrouping.OrderOf);

        Assert.Equal([NeedsReviewGroups.Ready, NeedsReviewGroups.Choose, NeedsReviewGroups.NoMatch, NeedsReviewGroups.FileProblem], sorted);
    }

    [Fact]
    public void OnlyItemsThatLackJustTheirWikidataLink_AreHidden()
    {
        Assert.True(NeedsReviewGrouping.IsHidden(ReviewTrigger.MissingQid));
        Assert.True(NeedsReviewGrouping.IsHidden(ReviewTrigger.WikidataBridgeFailed));
        Assert.False(NeedsReviewGrouping.IsHidden(ReviewTrigger.MultipleQidMatches));
        Assert.False(NeedsReviewGrouping.IsHidden(ReviewTrigger.RetailMatchFailed));
        Assert.False(NeedsReviewGrouping.IsHidden(null));
    }
}
