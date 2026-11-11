using MediaEngine.Contracts.Details;

namespace MediaEngine.Api.Services.Details.Internals;

/// <summary>
/// The tab keys each detail page type offers. No page offers a Universe, Related or Details tab: file and provider facts sit in "About this" on Overview.
/// </summary>
internal static class DetailTabCatalog
{
    public static IReadOnlyList<string> KeysFor(DetailEntityType entityType) => entityType switch
    {
        DetailEntityType.TvShow => ["overview", "cast"],
        DetailEntityType.TvSeason => ["overview", "cast"],
        DetailEntityType.Movie => ["overview", "cast"],
        DetailEntityType.MovieSeries => ["overview"],
        DetailEntityType.TvEpisode => ["overview", "cast"],
        DetailEntityType.Book => ["overview", "credits"],
        DetailEntityType.Audiobook => ["overview", "tracks"],
        DetailEntityType.BookSeries => ["overview"],
        DetailEntityType.Work => ["overview", "credits"],
        DetailEntityType.ComicIssue => ["overview", "credits"],
        DetailEntityType.ComicSeries => ["overview"],
        DetailEntityType.MusicAlbum => ["overview"],
        DetailEntityType.Person => ["overview"],
        DetailEntityType.Collection => ["overview"],
        DetailEntityType.Universe => ["overview"],
        _ => ["overview", "people"],
    };
}
