using MediaEngine.Contracts.Details;

namespace MediaEngine.Api.Services.Details.Internals;

/// <summary>
/// The tab keys each detail page type offers. No page offers a Universe or Related tab.
/// </summary>
internal static class DetailTabCatalog
{
    public static IReadOnlyList<string> KeysFor(DetailEntityType entityType) => entityType switch
    {
        DetailEntityType.TvShow => ["overview", "cast", "details"],
        DetailEntityType.TvSeason => ["overview", "cast", "details"],
        DetailEntityType.Movie => ["overview", "cast", "details"],
        DetailEntityType.MovieSeries => ["overview", "details"],
        DetailEntityType.TvEpisode => ["overview", "cast", "details"],
        DetailEntityType.Book => ["overview", "credits", "details"],
        DetailEntityType.Audiobook => ["overview", "tracks", "details"],
        DetailEntityType.BookSeries => ["overview", "details"],
        DetailEntityType.Work => ["overview", "credits", "details"],
        DetailEntityType.ComicIssue => ["overview", "credits", "details"],
        DetailEntityType.ComicSeries => ["overview", "details"],
        DetailEntityType.MusicAlbum => ["overview", "details"],
        DetailEntityType.Person => ["overview"],
        DetailEntityType.Collection => ["overview", "details"],
        DetailEntityType.Universe => ["overview", "details"],
        _ => ["overview", "people", "details"],
    };
}
