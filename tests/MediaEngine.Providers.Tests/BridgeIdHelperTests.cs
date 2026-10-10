using MediaEngine.Domain;
using MediaEngine.Providers.Helpers;

namespace MediaEngine.Providers.Tests;

/// <summary>
/// Tests for <see cref="BridgeIdHelper"/> bridge-key recognition.
/// </summary>
public sealed class BridgeIdHelperTests
{
    [Theory]
    [InlineData(BridgeIdKeys.Isbn, true)]
    [InlineData(BridgeIdKeys.Isbn13, true)]
    [InlineData(BridgeIdKeys.Isbn10, true)]
    [InlineData(BridgeIdKeys.Asin, true)]
    [InlineData(BridgeIdKeys.AppleBooksId, true)]
    [InlineData(BridgeIdKeys.TmdbId, true)]
    [InlineData(BridgeIdKeys.TmdbEpisodeId, true)]
    [InlineData(BridgeIdKeys.TvdbId, true)]
    [InlineData(BridgeIdKeys.ImdbId, true)]
    [InlineData(BridgeIdKeys.AudibleId, true)]
    [InlineData(BridgeIdKeys.GoodreadsId, true)]
    [InlineData(BridgeIdKeys.MusicBrainzId, true)]
    [InlineData(BridgeIdKeys.ComicVineId, true)]
    [InlineData("gcd_id", true)]
    [InlineData("apple_music_id", true)]
    public void IsBridgeId_KnownKeys_ReturnsTrue(string key, bool expected)
    {
        Assert.Equal(expected, BridgeIdHelper.IsBridgeId(key));
    }

    [Theory]
    [InlineData("title")]
    [InlineData("author")]
    [InlineData("year")]
    [InlineData("description")]
    [InlineData("cover_url")]
    [InlineData("rating")]
    [InlineData("genre")]
    [InlineData("director")]
    [InlineData("")]
    [InlineData("random_field")]
    public void IsBridgeId_NonBridgeKeys_ReturnsFalse(string key)
    {
        Assert.False(BridgeIdHelper.IsBridgeId(key));
    }
}
