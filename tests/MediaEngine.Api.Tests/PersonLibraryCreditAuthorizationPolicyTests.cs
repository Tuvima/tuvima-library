using MediaEngine.Api.Services.Display;
using MediaEngine.Api.Services.ReadServices;
using MediaEngine.Contracts.Persons;

namespace MediaEngine.Api.Tests;

public sealed class PersonLibraryCreditAuthorizationPolicyTests
{
    [Fact]
    public void Filter_PreservesShowPosterWhenAuthorizedWorkHasEpisodeStill()
    {
        var episodeWorkId = Guid.NewGuid();
        var showPosterUrl = $"/stream/artwork/{Guid.NewGuid():D}";
        var credits = new List<PersonLibraryCreditDto>
        {
            new()
            {
                WorkId = episodeWorkId,
                CollectionId = Guid.NewGuid(),
                MediaType = "TV",
                Title = "Sample Show",
                CoverUrl = showPosterUrl,
                Role = "Actor",
            },
        };
        var visibleWorks = new List<DisplayWorkRow>
        {
            new()
            {
                WorkId = episodeWorkId,
                MediaType = "TV",
                CoverUrl = "/stream/episode/still.jpg",
            },
        };

        var result = Assert.Single(PersonLibraryCreditAuthorizationPolicy.Filter(credits, visibleWorks));

        Assert.Equal(showPosterUrl, result.CoverUrl);
        Assert.NotEqual(visibleWorks[0].CoverUrl, result.CoverUrl);
    }

    [Fact]
    public void Filter_UsesAuthorizedArtworkForOrdinaryWorksAndRemovesHiddenCredits()
    {
        var visibleWorkId = Guid.NewGuid();
        var hiddenWorkId = Guid.NewGuid();
        var credits = new List<PersonLibraryCreditDto>
        {
            new() { WorkId = visibleWorkId, MediaType = "Movie", Title = "Visible", CoverUrl = "/unscoped.jpg" },
            new() { WorkId = hiddenWorkId, MediaType = "Movie", Title = "Hidden", CoverUrl = "/hidden.jpg" },
        };
        var visibleWorks = new List<DisplayWorkRow>
        {
            new() { WorkId = visibleWorkId, MediaType = "Movie", CoverUrl = "/authorized.jpg" },
        };

        var result = Assert.Single(PersonLibraryCreditAuthorizationPolicy.Filter(credits, visibleWorks));

        Assert.Equal(visibleWorkId, result.WorkId);
        Assert.Equal("/authorized.jpg", result.CoverUrl);
    }

    [Fact]
    public void Filter_AuthorizesNormalizedAlbumCreditOnlyThroughItsCreditedVisibleTracks()
    {
        var albumId = Guid.NewGuid();
        var allowedTrackId = Guid.NewGuid();
        var deniedTrackId = Guid.NewGuid();
        var credit = new PersonLibraryCreditDto
        {
            WorkId = albumId,
            MediaType = "Music",
            Title = "Album",
            CoverUrl = "/unscoped-album-art.jpg",
            SourceWorkIds = [allowedTrackId, deniedTrackId],
        };
        var visibleWorks = new List<DisplayWorkRow>
        {
            new() { WorkId = allowedTrackId, RootWorkId = albumId, MediaType = "Music", CoverUrl = "/allowed-track-art.jpg" },
        };

        var result = Assert.Single(PersonLibraryCreditAuthorizationPolicy.Filter([credit], visibleWorks));

        Assert.Equal(albumId, result.WorkId);
        Assert.Equal("/allowed-track-art.jpg", result.CoverUrl);
        Assert.Empty(PersonLibraryCreditAuthorizationPolicy.Filter([credit], []));
    }
}
