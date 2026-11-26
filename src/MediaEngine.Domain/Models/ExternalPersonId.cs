namespace MediaEngine.Domain.Models;

/// <summary>
/// An ID a provider gave for a person, together with the Wikidata property that holds the same kind of ID
/// (P434 for a MusicBrainz artist, P4985 for a TMDB person), so the person can be found by that exact ID instead of by name.
/// </summary>
public sealed record ExternalPersonId(string WikidataPropertyId, string Value)
{
    public static ExternalPersonId MusicBrainzArtist(string id) => new("P434", id);

    public static ExternalPersonId TmdbPerson(int id) => new("P4985", id.ToString(System.Globalization.CultureInfo.InvariantCulture));
}
