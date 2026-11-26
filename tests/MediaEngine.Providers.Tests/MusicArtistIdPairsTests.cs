using MediaEngine.Domain.Entities;
using MediaEngine.Providers.Helpers;

namespace MediaEngine.Providers.Tests;

public sealed class MusicArtistIdPairsTests
{
    private static readonly Guid MusicBrainz = Guid.NewGuid();
    private static readonly Guid Apple = Guid.NewGuid();
    private const string IdOne = "b10bbbfc-cf9e-42e0-be17-e2c3e1d2600d";
    private const string IdTwo = "a74b1b7f-71a5-4011-9441-d0b5e4122711";

    private static MetadataClaim Claim(Guid provider, string key, string value) => new()
    {
        ProviderId = provider,
        ClaimKey = key,
        ClaimValue = value,
        Confidence = 0.84,
        IsCurrent = true,
    };

    [Fact]
    public void Build_PairsEachArtistWithItsIdInTheSameOrder()
    {
        var pairs = MusicArtistIdPairs.Build([
            Claim(MusicBrainz, "artist", "Hans Zimmer; Lorne Balfe"),
            Claim(MusicBrainz, "artist_mbids", $"{IdOne}; {IdTwo}"),
        ]);

        Assert.Equal(IdOne, pairs["Hans Zimmer"]);
        Assert.Equal(IdTwo, pairs["lorne balfe"]);
    }

    [Fact]
    public void Build_IgnoresArtistNamesFromAnotherProvider()
    {
        var pairs = MusicArtistIdPairs.Build([
            Claim(Apple, "artist", "Someone Else"),
            Claim(MusicBrainz, "artist_mbids", IdOne),
        ]);

        Assert.Empty(pairs);
    }

    [Fact]
    public void Build_SkipsNamesWhenTheListsDifferInLength()
    {
        var pairs = MusicArtistIdPairs.Build([
            Claim(MusicBrainz, "artist", "A; B"),
            Claim(MusicBrainz, "artist_mbids", IdOne),
        ]);

        Assert.Empty(pairs);
    }
}
