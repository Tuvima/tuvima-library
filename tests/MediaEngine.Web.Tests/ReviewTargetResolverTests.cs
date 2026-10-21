using MediaEngine.Web.Services.Editing;

namespace MediaEngine.Web.Tests;

public sealed class ReviewTargetResolverTests
{
    [Fact]
    public void Resolve_AudiobookQidGap_TargetsNarratorIdentity()
    {
        var target = ReviewTargetResolver.Resolve("Audiobooks", "QidNoMatch");

        Assert.Equal("links", target.InitialTab);
        Assert.Equal("narrator", target.CanonicalTargetGroup);
        Assert.Equal("narrator", target.FocusField);
        Assert.Equal(MediaEditorIdentityIntent.FixWikidataMatch, target.Intent);
    }

    [Fact]
    public void Resolve_TvReview_TargetsShowEpisodeIdentity()
    {
        var target = ReviewTargetResolver.Resolve("TV", "RetailMatchFailed");

        Assert.Equal("links", target.InitialTab);
        Assert.Equal("show_episode", target.CanonicalTargetGroup);
        Assert.Equal("show_name", target.FocusField);
        Assert.Equal(MediaEditorIdentityIntent.FixRetailMatch, target.Intent);
    }

    [Fact]
    public void Resolve_MusicReview_TargetsRecordingRatherThanAlbum()
    {
        var target = ReviewTargetResolver.Resolve("Music", "RetailMatchAmbiguous");

        Assert.Equal("links", target.InitialTab);
        Assert.Equal("track", target.CanonicalTargetGroup);
        Assert.Equal("title", target.FocusField);
        Assert.Contains("track and album", target.Summary, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("RetailMatchFailed", MediaEditorIdentityIntent.FixRetailMatch, "Find match")]
    [InlineData("PlaceholderTitle", MediaEditorIdentityIntent.FixRetailMatch, "Find match")]
    [InlineData("RetailMatchAmbiguous", MediaEditorIdentityIntent.ConfirmRetailMatch, "Confirm Retail Match")]
    [InlineData("WikidataBridgeFailed", MediaEditorIdentityIntent.FixWikidataMatch, "Search Wikidata")]
    [InlineData("MissingQid", MediaEditorIdentityIntent.FixWikidataMatch, "Search Wikidata")]
    [InlineData("MultipleQidMatches", MediaEditorIdentityIntent.FixWikidataMatch, "Search Wikidata")]
    [InlineData("AmbiguousMediaType", MediaEditorIdentityIntent.ReviewClassification, "Review classification")]
    public void Resolve_Trigger_MapsIntentAndPrimaryAction(string trigger, MediaEditorIdentityIntent intent, string label)
    {
        var target = ReviewTargetResolver.Resolve("Comics", trigger);

        Assert.Equal(intent, target.Intent);
        Assert.Equal(label, target.PrimaryActionLabel);
        if (intent == MediaEditorIdentityIntent.ReviewClassification)
        {
            Assert.Contains("does not change the media type", target.Summary, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Theory]
    [InlineData("Books")]
    [InlineData("Audiobooks")]
    [InlineData("Comics")]
    [InlineData("Movies")]
    public void Resolve_AmbiguousSeriesMembership_OpensMatchingWithTheSeriesChoice(string mediaType)
    {
        var target = ReviewTargetResolver.Resolve(mediaType, "AmbiguousSeriesMembership");

        Assert.Equal("links", target.InitialTab);
        Assert.Equal("Choose Series", target.PrimaryActionLabel);
        Assert.Contains("Series", target.Summary, StringComparison.Ordinal);
    }
}
