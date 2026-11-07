using MediaEngine.Domain.Configuration;

namespace MediaEngine.Web.Services.Editing;

/// <summary>
/// Which kind of item (universal editor v5 kinds: movie, book, album ...) the Match tab searches for, read from the editor's media type and the
/// scope of the item being edited. The Engine decides the providers and boxes for the kind; the Dashboard only has to name it. A scope with
/// no provider to match (a Series, a course, a supporting file) has no kind.
/// </summary>
public static class EditorMatchKindRules
{
    /// <summary>The editor kind to search for, or null when no provider matches this scope.</summary>
    public static string? KindOf(string? mediaType, string? scopeId)
    {
        var scope = (scopeId ?? string.Empty).Trim().ToLowerInvariant();
        return (mediaType ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "movies" or "movie" => scope is "series" or "collection" ? null : EditorKinds.Movie,
            "books" or "book" => scope is "series" ? null : EditorKinds.Book,
            "audiobooks" or "audiobook" => scope is "series" ? null : EditorKinds.Audiobook,
            "comics" or "comic" => scope is "series" ? null : EditorKinds.Comic,
            "tv" => scope switch
            {
                "episode" => EditorKinds.TvEpisode,
                "season" => EditorKinds.TvSeason,
                "show" or "series" => EditorKinds.TvShow,
                _ => null,
            },
            "music" => scope switch
            {
                "album" => EditorKinds.Album,
                "track" or "song" => EditorKinds.Song,
                _ => null,
            },
            _ => null,
        };
    }
}
