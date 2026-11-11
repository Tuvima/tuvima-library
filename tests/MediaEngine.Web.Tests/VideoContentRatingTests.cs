using MediaEngine.Web.Models.ViewDTOs;
using MediaEngine.Web.Services.Playback;

namespace MediaEngine.Web.Tests;

public sealed class VideoContentRatingTests
{
    [Fact]
    public void Of_WithScoreAndContentRating_ReturnsTheContentRatingNotTheScore()
    {
        var detail = new LibraryItemDetailViewModel
        {
            Rating = "6.21",
            CanonicalValues =
            [
                new LibraryItemCanonicalValueDto { Key = "rating", Value = "6.21" },
                new LibraryItemCanonicalValueDto { Key = "content_rating", Value = " PG-13 " },
            ],
        };

        Assert.Equal("PG-13", VideoContentRating.Of(detail));
    }

    [Fact]
    public void Of_WithCertificationOnly_ReturnsTheCertification()
    {
        var detail = new LibraryItemDetailViewModel
        {
            CanonicalValues = [new LibraryItemCanonicalValueDto { Key = "certification", Value = "TV-MA" }],
        };

        Assert.Equal("TV-MA", VideoContentRating.Of(detail));
    }

    [Fact]
    public void Of_WithOnlyAScore_ReturnsNothingSoNoRatingCardShows()
    {
        var detail = new LibraryItemDetailViewModel { Rating = "6.21" };

        Assert.Null(VideoContentRating.Of(detail));
    }
}
