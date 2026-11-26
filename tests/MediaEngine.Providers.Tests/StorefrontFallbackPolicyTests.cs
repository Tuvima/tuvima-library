using MediaEngine.Domain.Enums;
using MediaEngine.Providers.Adapters;

namespace MediaEngine.Providers.Tests;

public sealed class StorefrontFallbackPolicyTests
{
    [Theory]
    [InlineData(MediaType.Books, true)]
    [InlineData(MediaType.Audiobooks, true)]
    [InlineData(MediaType.Movies, true)]
    [InlineData(MediaType.TV, true)]
    [InlineData(MediaType.Music, false)]
    public void OnlySongs_SkipTheSecondStorefront(MediaType mediaType, bool expected) =>
        Assert.Equal(expected, ConfigDrivenAdapter.AllowsStorefrontFallback(mediaType));
}
