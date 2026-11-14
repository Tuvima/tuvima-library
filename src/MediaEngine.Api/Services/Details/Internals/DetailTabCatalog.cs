using MediaEngine.Contracts.Details;

namespace MediaEngine.Api.Services.Details.Internals;

/// <summary>
/// The tab keys each detail page type offers. No page offers a Universe, Related or Details tab: file facts sit in "Your copy" and credits in the Credits tab (details-page-bottom D1).
/// </summary>
internal static class DetailTabCatalog
{
    public static IReadOnlyList<string> KeysFor(DetailEntityType entityType) => entityType switch
    {
        DetailEntityType.TvShow => ["overview", "credits"],
        DetailEntityType.TvSeason => ["overview", "credits"],
        DetailEntityType.Movie => ["overview", "credits"],
        DetailEntityType.MovieSeries => ["overview"],
        DetailEntityType.TvEpisode => ["overview", "credits"],
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
