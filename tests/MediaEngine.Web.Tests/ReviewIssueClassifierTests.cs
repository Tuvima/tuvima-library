using System.Reflection;
using MediaEngine.Domain.Constants;
using MediaEngine.Web.Services.Editing;

namespace MediaEngine.Web.Tests;

public sealed class ReviewIssueClassifierTests
{
    [Theory]
    [InlineData("MissingQid", ReviewIssueBucket.QuickFix)]
    [InlineData("RetailMatchAmbiguous", ReviewIssueBucket.ManualReview)]
    [InlineData("AmbiguousMediaType", ReviewIssueBucket.ManualReview)]
    [InlineData("WritebackFailed", ReviewIssueBucket.HighPriority)]
    [InlineData("StagedUnidentifiable", ReviewIssueBucket.HighPriority)]
    public void Classify_UsesStableTriggerBuckets(string trigger, ReviewIssueBucket expected)
    {
        Assert.Equal(expected, ReviewIssueClassifier.Classify(trigger).Bucket);
    }

    [Fact]
    public void Classify_UnknownTrigger_RemainsVisibleForManualReview()
    {
        var presentation = ReviewIssueClassifier.Classify("FutureTrigger");

        Assert.Equal(ReviewIssueBucket.ManualReview, presentation.Bucket);
        Assert.False(string.IsNullOrWhiteSpace(presentation.Explanation));
    }

    [Theory]
    [InlineData("RetailMatchFailed", "No reliable match")]
    [InlineData("RetailMatchAmbiguous", "Match needs confirmation")]
    [InlineData("MissingQid", "No canonical identity found")]
    [InlineData("MultipleQidMatches", "Canonical identity needs confirmation")]
    public void Classify_UsesPipelineStageSpecificLabels(string trigger, string expectedLabel)
    {
        Assert.Equal(expectedLabel, ReviewIssueClassifier.Classify(trigger).Label);
    }

    public static TheoryData<string> AllTriggers()
    {
        var data = new TheoryData<string>();
        foreach (var trigger in typeof(ReviewTrigger)
                     .GetFields(BindingFlags.Public | BindingFlags.Static)
                     .Where(field => field.IsLiteral)
                     .Select(field => (string)field.GetRawConstantValue()!))
        {
            data.Add(trigger);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(AllTriggers))]
    public void Classify_CarriesTheSharedReviewCategoryForEveryTrigger(string trigger)
    {
        var category = ReviewIssueClassifier.Classify(trigger).Category;

        Assert.NotNull(category);
        Assert.Equal(ReviewCategory.For(trigger), category);
    }

    [Fact]
    public void Classify_UnknownTrigger_HasNoSharedCategory()
    {
        Assert.Null(ReviewIssueClassifier.Classify("FutureTrigger").Category);
        Assert.Null(ReviewIssueClassifier.Classify(null).Category);
    }
}
