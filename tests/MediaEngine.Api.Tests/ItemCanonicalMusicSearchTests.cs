using MediaEngine.Api.Endpoints;
using MediaEngine.Domain;

namespace MediaEngine.Api.Tests;

/// <summary>
/// Bug F50: a Match search for music says whether it looks for a track or an album, so MusicBrainz and Apple each run the search that
/// fits (and a typed query still reaches MusicBrainz, which needs more than a bare title).
/// </summary>
public sealed class ItemCanonicalMusicSearchTests
{
    private static ItemCanonicalEndpoints.CanonicalTargetPolicy Policy(string group) =>
        new("Music", group == "album" ? "container" : "work", group,
            [MetadataFieldConstants.Artist, MetadataFieldConstants.Album], [], [], [], [], true, true, true);

    [Fact]
    public void ATypedTrackQuery_BecomesATitleSearchForTracks()
    {
        var fields = ItemCanonicalEndpoints.BuildRetailSearchFields(Policy("track"), new Dictionary<string, string>(), "Karma Police");

        Assert.NotNull(fields);
        Assert.Equal("Karma Police", fields[MetadataFieldConstants.Title]);
        Assert.Equal("track", fields[ItemCanonicalEndpoints.MusicSearchScopeKey]);
    }

    [Fact]
    public void ATypedAlbumQuery_BecomesAnAlbumSearch()
    {
        var fields = ItemCanonicalEndpoints.BuildRetailSearchFields(Policy("album"), new Dictionary<string, string>(), "  Smile Empty Soul ");

        Assert.NotNull(fields);
        Assert.Equal("Smile Empty Soul", fields[MetadataFieldConstants.Album]);
        Assert.Equal("Smile Empty Soul", fields[MetadataFieldConstants.Title]);
        Assert.Equal("album", fields[ItemCanonicalEndpoints.MusicSearchScopeKey]);
    }

    [Fact]
    public void AnAlbumSearchFromTheItemsOwnDetails_KeepsItsFieldsAndSaysItIsForAlbums()
    {
        var draft = new Dictionary<string, string>
        {
            [MetadataFieldConstants.Album] = "GNX",
            [MetadataFieldConstants.Artist] = "Kendrick Lamar",
            [MetadataFieldConstants.TrackNumber] = "12",
        };

        var fields = ItemCanonicalEndpoints.BuildRetailSearchFields(Policy("album"), draft, null);

        Assert.NotNull(fields);
        Assert.Equal("GNX", fields[MetadataFieldConstants.Album]);
        Assert.Equal("Kendrick Lamar", fields[MetadataFieldConstants.Artist]);
        Assert.False(fields.ContainsKey(MetadataFieldConstants.TrackNumber));
        Assert.Equal("album", fields[ItemCanonicalEndpoints.MusicSearchScopeKey]);
    }

    [Fact]
    public void AnotherKindOfSearch_IsLeftAsItWas()
    {
        var movies = new ItemCanonicalEndpoints.CanonicalTargetPolicy("Movies", "work", "movie",
            [MetadataFieldConstants.Title], [], [], [], [], true, true, true);

        Assert.Null(ItemCanonicalEndpoints.BuildRetailSearchFields(movies, new Dictionary<string, string>(), "Dune"));
    }
}
