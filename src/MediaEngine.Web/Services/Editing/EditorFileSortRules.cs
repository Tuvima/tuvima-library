using MediaEngine.Contracts.Editing;
using MediaEngine.Domain.Models;

namespace MediaEngine.Web.Services.Editing;

/// <summary>Where, inside the file's own title, a person puts an Extra.</summary>
public enum ExtraPlaceMode
{
    /// <summary>The whole show, film or album.</summary>
    Whole,

    /// <summary>One season of a show (0 is Specials).</summary>
    Season,

    /// <summary>One episode of a show.</summary>
    Episode,

    /// <summary>One track of an album.</summary>
    Track,
}

/// <summary>
/// The wording and the small rules behind "What is this file?" (universal editor v4 D20): which places an Extra can be put in for each
/// kind of title, how a choice reads in Review changes, and how a guess from the file name pre-fills the picker. Pure: nothing here calls
/// the Engine or changes anything.
/// </summary>
public static class EditorFileSortRules
{
    /// <summary>What the first choice is called for each kind of title: the file is one of the title's own main files, and Match finds which.</summary>
    public static string MainFileNoun(string titleKind) => titleKind switch
    {
        EditorFileSortingTitleKinds.Show => "An episode",
        EditorFileSortingTitleKinds.Album => "A track",
        EditorFileSortingTitleKinds.Film => "A film",
        _ => "A book or main file",
    };

    /// <summary>The places the picker offers for an Extra of the file's own title, in the order shown.</summary>
    public static IReadOnlyList<ExtraPlaceMode> PlacesFor(string titleKind) => titleKind switch
    {
        EditorFileSortingTitleKinds.Show => [ExtraPlaceMode.Whole, ExtraPlaceMode.Season, ExtraPlaceMode.Episode],
        EditorFileSortingTitleKinds.Album => [ExtraPlaceMode.Whole, ExtraPlaceMode.Track],
        _ => [ExtraPlaceMode.Whole],
    };

    /// <summary>The words for one place ("The whole show", "One episode") for a kind of title.</summary>
    public static string PlaceLabel(string titleKind, ExtraPlaceMode mode) => mode switch
    {
        ExtraPlaceMode.Season => "A season",
        ExtraPlaceMode.Episode => "One episode",
        ExtraPlaceMode.Track => "One track",
        _ => titleKind switch
        {
            EditorFileSortingTitleKinds.Show => "The whole show",
            EditorFileSortingTitleKinds.Album => "The whole album",
            EditorFileSortingTitleKinds.Film => "The film",
            _ => "The whole title",
        },
    };

    /// <summary>The place a guess points at, kept to the places this kind of title offers.</summary>
    public static (ExtraPlaceMode Mode, int Season, int Episode, int Track) PlaceFromGuess(string titleKind, EditorFileGuessDto? guess)
    {
        var offered = PlacesFor(titleKind);
        if (guess is { Track: > 0 } && offered.Contains(ExtraPlaceMode.Track))
        {
            return (ExtraPlaceMode.Track, 1, 1, guess.Track.Value);
        }

        if (guess is { Season: not null } && guess.Episode is > 0 && offered.Contains(ExtraPlaceMode.Episode))
        {
            return (ExtraPlaceMode.Episode, guess.Season.Value, guess.Episode.Value, 1);
        }

        if (guess is { Season: not null } && offered.Contains(ExtraPlaceMode.Season))
        {
            return (ExtraPlaceMode.Season, guess.Season.Value, 1, 1);
        }

        return (ExtraPlaceMode.Whole, 1, 1, 1);
    }

    /// <summary>The choice for "An Extra" from what the picker holds. Null when the numbers are out of range.</summary>
    public static ExtraFileChoice? ExtraChoice(string extraKind, ExtraPlaceMode mode, int season, int episode, int track)
    {
        int? seasonValue = mode is ExtraPlaceMode.Season or ExtraPlaceMode.Episode ? season : null;
        int? episodeValue = mode == ExtraPlaceMode.Episode ? episode : null;
        int? trackValue = mode == ExtraPlaceMode.Track ? track : null;
        var token = new ExtraFileChoice(EditSessionExtraMove.ActionExtra, extraKind, seasonValue, episodeValue, trackValue, null);
        return EditSessionExtraMove.TryParseFile(EditSessionExtraMove.ToFileToken(token), out var valid) ? valid : null;
    }

    /// <summary>The choice for "Belongs to another title": an Extra of the title with this id.</summary>
    public static ExtraFileChoice? AttachChoice(string extraKind, Guid titleId)
    {
        var token = new ExtraFileChoice(EditSessionExtraMove.ActionAttach, extraKind, null, null, null, titleId);
        return EditSessionExtraMove.TryParseFile(EditSessionExtraMove.ToFileToken(token), out var valid) ? valid : null;
    }

    public static ExtraFileChoice IgnoreChoice { get; } = new(EditSessionExtraMove.ActionIgnore, null, null, null, null, null);

    /// <summary>
    /// How a choice reads to a person: "Trailer · Season 1", "Document, on Dune", "Ignored". <paramref name="attachedTitle"/> names the
    /// other title for an attached file.
    /// </summary>
    public static string Describe(ExtraFileChoice choice, string? attachedTitle)
    {
        ArgumentNullException.ThrowIfNull(choice);
        if (choice.Action == EditSessionExtraMove.ActionIgnore)
        {
            return "Ignored";
        }

        var kind = ExtraKinds.Label(choice.ExtraKind);
        if (choice.Action == EditSessionExtraMove.ActionAttach)
        {
            return string.IsNullOrWhiteSpace(attachedTitle) ? $"{kind}, on another title" : $"{kind}, on {attachedTitle}";
        }

        var place = ExtraPlaces.Label(choice.Season, choice.Episode, choice.Track);
        return place.Length == 0 ? kind : $"{kind} · {place}";
    }

    /// <summary>The kinds a file can be, as the Engine listed them, kept to ones this Dashboard knows.</summary>
    public static IReadOnlyList<string> KindsFrom(IEnumerable<string>? kinds) =>
        [.. (kinds ?? []).Where(ExtraKinds.IsKnown)];

    /// <summary>Which entity types of a Library search can be filed under: a title with a file of its own, not a person, a Series or a playlist.</summary>
    public static bool CanAttachTo(string entityType) =>
        entityType.ToLowerInvariant() is "tv-show" or "movie" or "album" or "audiobook" or "comic" or "book";
}
