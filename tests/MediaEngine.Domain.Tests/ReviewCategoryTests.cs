using System.Reflection;
using MediaEngine.Domain.Constants;

namespace MediaEngine.Domain.Tests;

public sealed class ReviewCategoryTests
{
    private static string[] AllTriggers() =>
        typeof(ReviewTrigger)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(field => field.IsLiteral)
            .Select(field => (string)field.GetRawConstantValue()!)
            .ToArray();

    [Fact]
    public void ReviewTrigger_HasExactlyEighteenConstants()
    {
        Assert.Equal(18, AllTriggers().Length);
    }

    [Fact]
    public void EveryReviewTrigger_MapsToExactlyOneListedCategory()
    {
        var keys = ReviewCategory.All.Select(category => category.Key).ToArray();

        foreach (var trigger in AllTriggers())
        {
            var key = ReviewCategory.For(trigger);

            Assert.NotNull(key);
            Assert.Contains(key, keys);
        }
    }

    [Fact]
    public void EveryCategory_HasAtLeastOneTrigger_AndALabelAndExplanation()
    {
        var used = AllTriggers().Select(ReviewCategory.For).ToHashSet();

        foreach (var category in ReviewCategory.All)
        {
            Assert.Contains(category.Key, used);
            Assert.False(string.IsNullOrWhiteSpace(category.Label));
            Assert.False(string.IsNullOrWhiteSpace(category.Explanation));
        }

        Assert.Equal(ReviewCategory.All.Count, ReviewCategory.All.Select(category => category.Key).Distinct().Count());
    }

    [Theory]
    [InlineData(ReviewTrigger.NameCollision, ReviewCategory.Naming)]
    [InlineData(ReviewTrigger.FromMerge, ReviewCategory.Naming)]
    [InlineData(ReviewTrigger.MovieMatchedAsTv, ReviewCategory.Unmatched)]
    [InlineData(ReviewTrigger.WritebackFailed, ReviewCategory.ProviderFailures)]
    [InlineData(ReviewTrigger.MediaInspectionFailed, ReviewCategory.ProviderFailures)]
    public void For_PutsTheTriggerInItsCategory(string trigger, string expected)
    {
        Assert.Equal(expected, ReviewCategory.For(trigger));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("FutureTrigger")]
    public void For_UnknownOrMissingTrigger_ReturnsNull(string? trigger)
    {
        Assert.Null(ReviewCategory.For(trigger));
    }
}
