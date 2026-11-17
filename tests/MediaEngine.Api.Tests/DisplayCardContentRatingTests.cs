using MediaEngine.Api.Services.Display;

namespace MediaEngine.Api.Tests;

public sealed class DisplayCardContentRatingTests
{
    [Fact]
    public void AJourneyCard_CarriesTheContentRatingTheMovieHas()
    {
        var card = new DisplayCardBuilder().FromJourney(
            new DisplayJourneyRow { WorkId = Guid.NewGuid(), AssetId = Guid.NewGuid(), MediaType = "Movies", Title = "Heat", ProgressPct = 40, ContentRating = " R " },
            "continue");

        Assert.Equal("R", card.ContentRating);
    }

    [Fact]
    public void AWorkCard_WithNoContentRating_CarriesNullNotABlank()
    {
        var card = new DisplayCardBuilder().FromWork(
            new DisplayWorkRow { WorkId = Guid.NewGuid(), AssetId = Guid.NewGuid(), MediaType = "Books", Title = "Dune", ContentRating = "  " },
            "continue",
            progress: null);

        Assert.Null(card.ContentRating);
    }
}
