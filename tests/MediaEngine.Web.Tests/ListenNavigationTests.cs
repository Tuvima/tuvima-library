using MediaEngine.Web.Services.Navigation;

namespace MediaEngine.Web.Tests;

public sealed class ListenNavigationTests
{
    [Theory]
    [InlineData(null, ListenNavigation.MusicHomeRoute)]
    [InlineData("", ListenNavigation.MusicHomeRoute)]
    [InlineData("/listen/music", ListenNavigation.MusicHomeRoute)]
    [InlineData("/listen/music/albums/11111111-1111-1111-1111-111111111111", ListenNavigation.AlbumsRoute)]
    [InlineData("/listen/music?browse=artists", ListenNavigation.ArtistsRoute)]
    [InlineData("/listen/music/artists/boygenius", ListenNavigation.ArtistsRoute)]
    [InlineData("/listen/music?browse=songs&track=11111111-1111-1111-1111-111111111111", ListenNavigation.SongsRoute)]
    [InlineData("/listen/music?browse=timeline", ListenNavigation.TimelineRoute)]
    [InlineData("/listen/music/playlists/system/all-music", ListenNavigation.PlaylistsRoute)]
    [InlineData("http://localhost:5016/listen/music/playlists/11111111-1111-1111-1111-111111111111", ListenNavigation.PlaylistsRoute)]
    [InlineData("/listen/playlists", ListenNavigation.PlaylistsRoute)]
    [InlineData("/listen/audiobooks", ListenNavigation.MusicHomeRoute)]
    public void NormalizeMusicSurfaceRoute_MapsRoutesBackToMusicSurfaces(string? route, string expected)
    {
        var actual = ListenNavigation.NormalizeMusicSurfaceRoute(route);

        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData("audiobooks", null, ListenNavigation.AudiobooksRoute)]
    [InlineData("audiobooks", "/listen/music?browse=songs", ListenNavigation.AudiobooksRoute)]
    [InlineData("music", null, ListenNavigation.MusicHomeRoute)]
    [InlineData("music", "/listen/music/artists/boygenius", ListenNavigation.MusicHomeRoute)]
    [InlineData(null, "/listen/music/playlists/system/all-music", ListenNavigation.MusicHomeRoute)]
    public void ResolveEntryRoute_UsesSavedModeAndNormalizedMusicSurface(string? savedMode, string? savedMusicRoute, string expected)
    {
        var actual = ListenNavigation.ResolveEntryRoute(savedMode, savedMusicRoute);

        Assert.Equal(expected, actual);
    }
}
