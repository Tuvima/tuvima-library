using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Services;

namespace MediaEngine.Domain.Tests;

// Global bookmarks spec D7 and D12: one place decides how close two bookmarks are, from the limits of their kind of position.
public sealed class BookmarkClosenessRulesTests
{
    private static readonly BookmarkClosenessConfiguration Defaults = new();

    [Theory]
    [InlineData(0, BookmarkCloseness.SameSpot)]
    [InlineData(3, BookmarkCloseness.SameSpot)]
    [InlineData(3.5, BookmarkCloseness.Close)]
    [InlineData(30, BookmarkCloseness.Close)]
    [InlineData(31, BookmarkCloseness.Far)]
    [InlineData(180, BookmarkCloseness.Far)]
    [InlineData(181, BookmarkCloseness.New)]
    public void ForTime_TheGapDecidesTheLabel_AtEachEdge(double gap, BookmarkCloseness expected) =>
        Assert.Equal(expected, BookmarkClosenessRules.Classify(Defaults.For(BookmarkPositionKind.Time), gap));

    [Theory]
    [InlineData(0, BookmarkCloseness.SameSpot)]
    [InlineData(1, BookmarkCloseness.Close)]
    [InlineData(2, BookmarkCloseness.Close)]
    [InlineData(3, BookmarkCloseness.Far)]
    [InlineData(10, BookmarkCloseness.Far)]
    [InlineData(11, BookmarkCloseness.New)]
    public void ForPages_TheSamePageIsTheSameSpot_AndTheGapIsInPages(double gap, BookmarkCloseness expected) =>
        Assert.Equal(expected, BookmarkClosenessRules.Classify(Defaults.For(BookmarkPositionKind.Page), gap));

    [Fact]
    public void AGapInEitherDirection_IsTheSameDistance() =>
        Assert.Equal(BookmarkCloseness.Close, BookmarkClosenessRules.Classify(Defaults.Time, -20));

    [Fact]
    public void ANonNumber_IsNew() =>
        Assert.Equal(BookmarkCloseness.New, BookmarkClosenessRules.Classify(Defaults.Time, double.NaN));

    [Theory]
    [InlineData(BookmarkCloseness.SameSpot, BookmarkRowChoice.KeepIncoming)]
    [InlineData(BookmarkCloseness.Close, BookmarkRowChoice.KeepYours)]
    [InlineData(BookmarkCloseness.Far, BookmarkRowChoice.KeepYours)]
    [InlineData(BookmarkCloseness.New, BookmarkRowChoice.KeepIncoming)]
    public void TheDefaultChoice_IsWhatApplyDoesWithNoClicking(BookmarkCloseness closeness, BookmarkRowChoice expected) =>
        Assert.Equal(expected, BookmarkClosenessRules.DefaultChoice(closeness));
}
