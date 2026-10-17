using MediaEngine.Contracts.Details;

namespace MediaEngine.Api.Services.Details.Internals;

/// <summary>
/// The tab keys each detail page type offers. The Universe and Related tabs are hidden
/// everywhere (every page, every device) behind the single switch
/// <see cref="DetailTabVisibility.ShowUniverseAndRelatedTabs"/>; flipping it restores them.
/// </summary>
internal static class DetailTabCatalog
{
    public static IReadOnlyList<string> KeysFor(
        DetailEntityType entityType,
        bool hasUniverse,
        bool includeUniverseAndRelated)
    {
        return BaseKeysFor(entityType, hasUniverse)
            .Where(key => DetailTabVisibility.IsTabAllowed(key, includeUniverseAndRelated))
            .ToArray();
    }

    private static string[] BaseKeysFor(DetailEntityType entityType, bool hasUniverse) => entityType switch
    {
        DetailEntityType.TvShow => hasUniverse ? ["overview", "cast", "universe", "related", "details"] : ["overview", "cast", "related", "details"],
        DetailEntityType.TvSeason when hasUniverse => ["overview", "cast", "universe", "related", "details"],
        DetailEntityType.TvSeason => ["overview", "cast", "related", "details"],
        DetailEntityType.Movie when hasUniverse => ["overview", "cast", "universe", "related", "details"],
        DetailEntityType.Movie => ["overview", "cast", "related", "details"],
        DetailEntityType.MovieSeries when hasUniverse => ["overview", "universe", "related", "details"],
        DetailEntityType.MovieSeries => ["overview", "related", "details"],
        DetailEntityType.TvEpisode when hasUniverse => ["overview", "cast", "characters", "universe", "related", "details"],
        DetailEntityType.TvEpisode => ["overview", "cast", "characters", "related", "details"],
        DetailEntityType.Book when hasUniverse => ["overview", "credits", "universe", "related", "details"],
        DetailEntityType.Book => ["overview", "credits", "related", "details"],
        DetailEntityType.Audiobook => ["overview", "tracks", "details"],
        DetailEntityType.BookSeries when hasUniverse => ["overview", "universe", "related", "details"],
        DetailEntityType.BookSeries => ["overview", "related", "details"],
        DetailEntityType.Work when hasUniverse => ["overview", "credits", "universe", "related", "details"],
        DetailEntityType.Work => ["overview", "credits", "related", "details"],
        DetailEntityType.ComicIssue when hasUniverse => ["overview", "credits", "universe", "related", "details"],
        DetailEntityType.ComicIssue => ["overview", "credits", "related", "details"],
        DetailEntityType.ComicSeries when hasUniverse => ["overview", "universe", "related", "details"],
        DetailEntityType.ComicSeries => ["overview", "related", "details"],
        DetailEntityType.MusicAlbum => ["overview", "details"],
        DetailEntityType.Person => ["overview"],
        DetailEntityType.Collection => ["overview", "details"],
        DetailEntityType.Character when hasUniverse => ["overview", "portrayals", "relationships", "universe", "details"],
        DetailEntityType.Character => ["overview", "portrayals", "relationships", "details"],
        DetailEntityType.Universe => ["overview", "characters", "people", "relationships", "details"],
        _ when hasUniverse => ["overview", "people", "characters", "universe", "details"],
        _ => ["overview", "people", "characters", "details"],
    };
}
