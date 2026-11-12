using System.Globalization;
using MediaEngine.Domain.Constants;
using MediaEngine.Domain.Models;
using MediaEngine.Web.Resources;
using MediaEngine.Web.Services.Editing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;

namespace MediaEngine.Web.Tests;

public sealed class NeedsReviewReasonTextTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 12, 15, 30, 0, TimeSpan.Zero);

    private static T WithEnglish<T>(Func<IStringLocalizer<SharedStrings>, T> action)
    {
        var previous = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = new CultureInfo("en-US");
            using var provider = new ServiceCollection().AddLogging().AddLocalization().BuildServiceProvider();
            return action(provider.GetRequiredService<IStringLocalizer<SharedStrings>>());
        }
        finally
        {
            CultureInfo.CurrentUICulture = previous;
        }
    }

    [Theory]
    [InlineData("Movies", "More than one film fits this file. Which one is it?")]
    [InlineData("TV", "More than one episode fits this file. Which one is it?")]
    [InlineData("Books", "More than one book fits this file. Which one is it?")]
    [InlineData("Audiobooks", "More than one audiobook fits this file. Which one is it?")]
    [InlineData("Comics", "More than one comic fits this file. Which one is it?")]
    [InlineData("Music", "More than one song fits this file. Which one is it?")]
    [InlineData(null, "More than one item fits this file. Which one is it?")]
    public void ASentenceWithAGap_IsFilledWithTheMediaWord(string? mediaType, string expected) =>
        Assert.Equal(expected, WithEnglish(L => NeedsReviewReasonText.ForTrigger(L, ReviewTrigger.RetailMatchAmbiguous, mediaType, null)));

    [Fact]
    public void ASentenceWithoutAGap_IsReturnedAsWritten() =>
        Assert.Equal("Close match, but Library is not sure.",
            WithEnglish(L => NeedsReviewReasonText.ForTrigger(L, ReviewTrigger.LowConfidence, "Movies", null)));

    [Fact]
    public void ABatchEditThatFailed_ShowsItsOwnDetailWhenThereIsOne() =>
        Assert.Equal("Genre was left empty.",
            WithEnglish(L => NeedsReviewReasonText.ForTrigger(L, ReviewTrigger.BatchEditFailed, "Books", "Genre was left empty.")));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ABatchEditThatFailed_WithoutDetail_ShowsTheSentence(string? detail) =>
        Assert.Equal("A change to several items did not apply to this one. Nothing was changed on it.",
            WithEnglish(L => NeedsReviewReasonText.ForTrigger(L, ReviewTrigger.BatchEditFailed, "Books", detail)));

    [Theory]
    [InlineData("SomethingNew")]
    [InlineData(null)]
    public void AnUnknownTrigger_GetsTheFallbackSentence(string? trigger) =>
        Assert.Equal("Library could not work this out by itself.",
            WithEnglish(L => NeedsReviewReasonText.ForTrigger(L, trigger, "Movies", null)));

    [Fact]
    public void AnUnmatchedFileReason_GetsItsSentence()
    {
        Assert.Equal("Library searched and found nothing close enough.",
            WithEnglish(L => NeedsReviewReasonText.ForUnmatchedReason(L, UnmatchedReasons.NoMatch)));
        Assert.Equal("This video has no YouTube ID in its name, so Library cannot look it up.",
            WithEnglish(L => NeedsReviewReasonText.ForUnmatchedReason(L, UnmatchedReasons.NoYoutubeId)));
    }

    [Fact]
    public void AnUnknownUnmatchedFileReason_GetsTheFallbackSentence() =>
        Assert.Equal("Library could not work this out by itself.",
            WithEnglish(L => NeedsReviewReasonText.ForUnmatchedReason(L, "something_new")));

    [Fact]
    public void ARetryLaterToday_SaysTriesAgainToday() =>
        Assert.Equal("Tries again today", WithEnglish(L => NeedsReviewReasonText.RetryText(L, Now.AddHours(3), Now)));

    [Fact]
    public void ARetryThatIsOverdue_SaysTriesAgainToday() =>
        Assert.Equal("Tries again today", WithEnglish(L => NeedsReviewReasonText.RetryText(L, Now.AddDays(-2), Now)));

    [Fact]
    public void ARetryOnTheNextDay_SaysTriesAgainTomorrow() =>
        Assert.Equal("Tries again tomorrow", WithEnglish(L => NeedsReviewReasonText.RetryText(L, Now.AddDays(1), Now)));

    [Fact]
    public void ARetryInSeveralDays_SaysHowManyDays() =>
        Assert.Equal("Tries again in 5 days", WithEnglish(L => NeedsReviewReasonText.RetryText(L, Now.AddDays(5), Now)));

    [Fact]
    public void ANoRetry_SaysItWillNotTryAgainByItself() =>
        Assert.Equal("Will not try again by itself", WithEnglish(L => NeedsReviewReasonText.RetryText(L, null, Now)));
}
