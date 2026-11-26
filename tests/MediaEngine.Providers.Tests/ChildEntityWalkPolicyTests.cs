using MediaEngine.Domain.Enums;
using MediaEngine.Providers.Adapters;
using Xunit;

namespace MediaEngine.Providers.Tests;

public sealed class ChildEntityWalkPolicyTests
{
    [Theory]
    [InlineData(MediaType.TV, true)]
    [InlineData(MediaType.Comics, true)]
    [InlineData(MediaType.Music, false)]
    [InlineData(MediaType.Movies, false)]
    public void WalksChildEntities_SkipsMusicBecauseWikidataOnlyNeedsArtistAndAlbum(MediaType mediaType, bool expected)
    {
        Assert.Equal(expected, ReconciliationAdapter.WalksChildEntities(mediaType));
    }
}
