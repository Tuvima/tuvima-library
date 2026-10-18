using System.Reflection;
using MediaEngine.Api.Services;
using MediaEngine.Domain.Constants;

namespace MediaEngine.Api.Tests;

public sealed class IngestionReviewReasonsTests
{
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
    public void BuildReviewReasons_CountsEveryTriggerInItsSharedCategory(string trigger)
    {
        var reasons = IngestionOperationsStatusService.BuildReviewReasons(
            [new IngestionOperationsStatusService.ReviewCountRow { Trigger = trigger, Count = 3 }]);

        var expectedKey = ReviewCategory.For(trigger);
        Assert.Equal(3, reasons.Single(reason => reason.Key == expectedKey).Count);
        Assert.Equal(3, reasons.Sum(reason => reason.Count));
    }

    [Fact]
    public void BuildReviewReasons_ListsExactlyTheSharedCategories_WithNoDuplicatesOrMissingArtworkBucket()
    {
        var reasons = IngestionOperationsStatusService.BuildReviewReasons([]);

        Assert.Equal(ReviewCategory.All.Select(category => category.Key), reasons.Select(reason => reason.Key));
        Assert.DoesNotContain(reasons, reason => reason.Key is "duplicates" or "missing_artwork");
    }

    [Fact]
    public void BuildReviewReasons_SumsTriggersThatShareACategory_AndIgnoresUnknownTriggers()
    {
        var reasons = IngestionOperationsStatusService.BuildReviewReasons(
        [
            new() { Trigger = ReviewTrigger.RootWatchFolder, Count = 2 },
            new() { Trigger = ReviewTrigger.NameCollision, Count = 1 },
            new() { Trigger = ReviewTrigger.FromMerge, Count = 4 },
            new() { Trigger = "FutureTrigger", Count = 9 },
        ]);

        Assert.Equal(7, reasons.Single(reason => reason.Key == ReviewCategory.Naming).Count);
        Assert.Equal(7, reasons.Sum(reason => reason.Count));
    }
}
