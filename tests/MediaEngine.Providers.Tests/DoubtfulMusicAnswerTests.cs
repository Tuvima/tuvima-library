using MediaEngine.Domain;
using MediaEngine.Domain.Enums;
using MediaEngine.Providers.Models;
using MediaEngine.Providers.Workers;
using Xunit;

namespace MediaEngine.Providers.Tests;

public sealed class DoubtfulMusicAnswerTests
{
    private static ProviderClaim Claim(string key, string value) =>
        new(key, value, 0.9);

    [Fact]
    public void AmbiguousMusicAnswer_DoesNotKeepArtistNames()
    {
        var claims = new[] { Claim("artist", "Daniel Darc"), Claim("genre", "Rock") };
        var kept = RetailMatchWorker.DropPeopleFromDoubtfulMusicAnswer(MediaType.Music, "Ambiguous", claims);
        Assert.Single(kept);
        Assert.Equal("genre", kept[0].Key);
    }

    [Fact]
    public void AutoAcceptedMusicAnswer_KeepsEverything()
    {
        var claims = new[] { Claim("artist", "Glenn Gould"), Claim("genre", "Classical") };
        Assert.Equal(2, RetailMatchWorker.DropPeopleFromDoubtfulMusicAnswer(MediaType.Music, "AutoAccepted", claims).Count);
    }

    [Fact]
    public void AmbiguousBookAnswer_KeepsEverything()
    {
        var claims = new[] { Claim("author", "Andy Weir") };
        Assert.Single(RetailMatchWorker.DropPeopleFromDoubtfulMusicAnswer(MediaType.Books, "Ambiguous", claims));
    }
}
