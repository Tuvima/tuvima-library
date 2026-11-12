using MediaEngine.Web.Resources;
using Microsoft.Extensions.Localization;

namespace MediaEngine.Web.Services.Editing;

/// <summary>
/// What the Engine's read of an item's current match says (universal editor v6 D10): whether the item is matched and, when it is, to which
/// provider. The Match tab raises it once the read has loaded, so the one-line header on the Match tab says exactly what the tab says.
/// </summary>
/// <param name="EntityId">The item the read is about.</param>
/// <param name="Matched">True when the Engine's read found a match that has not had its removal staged.</param>
/// <param name="ProviderName">The provider's name as shown ("TMDB", "Apple Books"); null when the item is not matched.</param>
public sealed record EditorMatchReadState(Guid EntityId, bool Matched, string? ProviderName);

/// <summary>The kind word of an item for "Match book": a plain, lower-case word taken from the editor's kind.</summary>
public static class EditorMatchKindWords
{
    public static string For(string? kind, IStringLocalizer<SharedStrings> localizer) => kind switch
    {
        "movie" => localizer["EditorMatchPanel_KindMovie"].Value,
        "book" => localizer["EditorMatchPanel_KindBook"].Value,
        "audiobook" => localizer["EditorMatchPanel_KindAudiobook"].Value,
        "comic" => localizer["EditorMatchPanel_KindComic"].Value,
        "tv_show" => localizer["EditorMatchPanel_KindShow"].Value,
        "tv_season" => localizer["EditorMatchPanel_KindSeason"].Value,
        "tv_episode" => localizer["EditorMatchPanel_KindEpisode"].Value,
        "album" => localizer["EditorMatchPanel_KindAlbum"].Value,
        "song" => localizer["EditorMatchPanel_KindSong"].Value,
        "video" => localizer["EditorMatchPanel_KindVideo"].Value,
        _ => localizer["EditorMatchPanel_KindItem"].Value,
    };
}
