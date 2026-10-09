namespace MediaEngine.Web.Services.Navigation;

public static class ListenNavigation
{
    public const string MusicHomeRoute = "/listen/music";
    public const string AlbumsRoute = MusicHomeRoute;
    public const string ArtistsRoute = "/listen/music?browse=artists";
    public const string SongsRoute = "/listen/music?browse=songs";
    public const string PlaylistsRoute = "/listen/music?browse=playlists";
    public const string TimelineRoute = "/listen/music?browse=timeline";
    public const string AudiobooksRoute = "/listen/audiobooks";

    public static string ResolveEntryRoute(string? savedMode, string? savedMusicRoute)
        => string.Equals(savedMode, "audiobooks", StringComparison.OrdinalIgnoreCase)
            ? AudiobooksRoute
            : MusicHomeRoute;

    public static string NormalizeMusicSurfaceRoute(string? route)
    {
        var normalizedRoute = NormalizeRoute(route);
        var path = normalizedRoute?.Split('?', 2)[0];
        if (string.IsNullOrWhiteSpace(path) || !path.StartsWith("/listen/music", StringComparison.OrdinalIgnoreCase))
        {
            return MusicHomeRoute;
        }

        if (string.Equals(path, MusicHomeRoute, StringComparison.OrdinalIgnoreCase))
        {
            var query = System.Web.HttpUtility.ParseQueryString(
                normalizedRoute?.StartsWith('/') != true
                && Uri.TryCreate(normalizedRoute, UriKind.Absolute, out var absolute)
                    ? absolute.Query
                    : normalizedRoute?.Contains('?') == true
                        ? normalizedRoute[(normalizedRoute.IndexOf('?'))..]
                        : string.Empty);
            return query["browse"]?.Trim().ToLowerInvariant() switch
            {
                "artists" => ArtistsRoute,
                "songs" => SongsRoute,
                "playlists" => PlaylistsRoute,
                "timeline" => TimelineRoute,
                _ => AlbumsRoute,
            };
        }

        if (path.StartsWith("/listen/music/artists/", StringComparison.OrdinalIgnoreCase))
        {
            return ArtistsRoute;
        }

        if (path.StartsWith("/listen/music/playlists/", StringComparison.OrdinalIgnoreCase))
        {
            return PlaylistsRoute;
        }

        return AlbumsRoute;
    }

    public static bool IsMusicRoute(string? route)
    {
        var path = NormalizeRoute(route);
        return !string.IsNullOrWhiteSpace(path)
               && path.StartsWith("/listen/music", StringComparison.OrdinalIgnoreCase);
    }

    private static string? NormalizeRoute(string? route)
    {
        if (string.IsNullOrWhiteSpace(route))
        {
            return null;
        }

        return !route.StartsWith('/') && Uri.TryCreate(route, UriKind.Absolute, out var absoluteRoute)
            ? absoluteRoute.PathAndQuery
            : route;
    }
}
