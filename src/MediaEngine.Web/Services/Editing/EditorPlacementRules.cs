using MediaEngine.Contracts.Editing;
using MediaEngine.Domain;

namespace MediaEngine.Web.Services.Editing;

/// <summary>
/// The rules of "Move to another album, season or series" in the Match tool (universal editor v3, WP4 and spec 3a) that need no screen:
/// what the destination is called for each kind of item, and the "Not on this album" warning Review shows before Save when the item's
/// match does not name the place it is being moved to. The Engine still verifies the exact release track when it saves; this only makes
/// sure a person sees the mismatch first.
/// </summary>
public static class EditorPlacementRules
{
    /// <summary>A place as the library labels it ("OK Computer", "Season 2" under "Original Show").</summary>
    public sealed record Place(string Label, string? Parent);

    /// <summary>What the destination of a move is called for an item of this media type.</summary>
    public static string PlaceNoun(string mediaType) => mediaType.ToLowerInvariant() switch
    {
        "music" => "album",
        "tv" => "season",
        "books" or "audiobooks" or "comics" => "series",
        _ => "place",
    };

    /// <summary>The title of the "Move to ..." section: "Move to another season or show", "Move to another album", "Move to another series".</summary>
    public static string MoveHeading(string mediaType) => mediaType.ToLowerInvariant() switch
    {
        "tv" => "Move to another season or show",
        "music" => "Move to another album",
        "books" or "audiobooks" or "comics" => "Move to another series",
        _ => "Move to another place",
    };

    /// <summary>
    /// The warning for a move into <paramref name="destination"/>, or null when the item's staged match names that very place.
    /// "Not on this album" for a song, "Not in this series" for a book or issue, "Not in this season" for an episode. An item with no
    /// staged match has nothing that names the place, so it is flagged too: nothing says it belongs there.
    /// </summary>
    public static string? NotThereNote(string mediaType, Place destination, Place? stagedMatch)
    {
        var destinationLabel = destination.Parent is { Length: > 0 } parent && PlaceNoun(mediaType) == "season"
            ? $"{destination.Label} of {parent}"
            : destination.Label;
        var stagedMatchPlace = stagedMatch is null
            ? null
            : stagedMatch.Parent is { Length: > 0 } stagedParent && PlaceNoun(mediaType) == "season"
                ? $"{stagedMatch.Label} of {stagedParent}"
                : stagedMatch.Label;
        if (stagedMatch is not null && IsSame(stagedMatch, destination))
        {
            return null;
        }

        var noun = PlaceNoun(mediaType);
        var heading = noun == "album" ? "Not on this album" : $"Not in this {noun}";
        var what = noun == "album" ? "song" : noun == "series" ? "book" : "episode";
        return string.IsNullOrWhiteSpace(stagedMatchPlace)
            ? $"{heading}: nothing you have matched says this {what} belongs in {destinationLabel}. Check it is right before you save."
            : $"{heading}: this {what}'s match names {stagedMatchPlace.Trim()}, not {destinationLabel}. Check it is right before you save.";
    }

    private static bool IsSame(Place a, Place b) =>
        string.Equals(a.Label.Trim(), b.Label.Trim(), StringComparison.OrdinalIgnoreCase)
        && (a.Parent is null || b.Parent is null || string.Equals(a.Parent.Trim(), b.Parent.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>The album, season or series the item's staged match names, or null when no match is staged for it.</summary>
    public static Place? StagedMatchPlace(PendingEditList pending, Guid entityId, string mediaType)
    {
        ArgumentNullException.ThrowIfNull(pending);
        var change = pending.Changes.FirstOrDefault(c => c.Kind == EditSessionChangeKinds.Identity && c.EntityId == entityId);
        if (change is null || !EditSessionIdentityTarget.TryParse(change.TargetId, out var match) || match is null)
        {
            return null;
        }

        string? Field(string key) =>
            match.RequiredFields.TryGetValue(key, out var required) && !string.IsNullOrWhiteSpace(required) ? required.Trim()
            : match.SuggestedFields.TryGetValue(key, out var suggested) && !string.IsNullOrWhiteSpace(suggested) ? suggested.Trim()
            : null;

        if (string.Equals(mediaType, "TV", StringComparison.OrdinalIgnoreCase))
        {
            var show = Field(MetadataFieldConstants.ShowName);
            if (show is null)
            {
                return null;
            }

            return int.TryParse(Field(MetadataFieldConstants.SeasonNumber), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var season)
                ? new Place(season == 0 ? "Specials" : $"Season {season.ToString(System.Globalization.CultureInfo.InvariantCulture)}", show)
                : new Place(show, null);
        }

        var name = string.Equals(mediaType, "Music", StringComparison.OrdinalIgnoreCase)
            ? Field(MetadataFieldConstants.Album)
            : Field(MetadataFieldConstants.Series);
        return name is null ? null : new Place(name, null);
    }
}
