using MediaEngine.Contracts.Ingestion;

namespace MediaEngine.Web.Components.Settings;

/// <summary>
/// The words for what a not-yet-Ready row is waiting for ("Waiting for matching and its show"). One helper builds them
/// so the Status column, its tooltip and accessible name, and the details drawer always say the same thing.
/// </summary>
public static class ReadinessWaitingText
{
    /// <summary>
    /// "Waiting for matching", "Waiting for matching and its show", "Waiting for its album and artwork"; null when
    /// there is nothing to wait for. "Its show" is used for TV and "its album" for Music; a placing reason on any other
    /// type has no wording and is left out.
    /// </summary>
    public static string? Text(string mediaType, IEnumerable<string>? waitingFor)
    {
        if (waitingFor is null)
        {
            return null;
        }

        var phrases = new List<string>(3);
        foreach (var key in waitingFor)
        {
            var phrase = Phrase(mediaType, key);
            if (phrase is not null && !phrases.Contains(phrase))
            {
                phrases.Add(phrase);
            }
        }

        return phrases.Count switch
        {
            0 => null,
            1 => $"Waiting for {phrases[0]}",
            _ => $"Waiting for {string.Join(", ", phrases.Take(phrases.Count - 1))} and {phrases[^1]}",
        };
    }

    /// <summary>
    /// The words for the row's status: the waiting text while the row is still being added or finishing and the Engine
    /// says what is missing, otherwise the Engine's own status label.
    /// </summary>
    public static string StatusFor(IngestionMediaGroupDto item) =>
        item.Availability is "adding" or "finishing"
            ? Text(item.MediaType, item.WaitingFor) ?? item.StatusLabel
            : item.StatusLabel;

    private static string? Phrase(string mediaType, string key) => key switch
    {
        IngestionWaitingForKeys.Matching => "matching",
        IngestionWaitingForKeys.Placing => mediaType switch
        {
            "TV" => "its show",
            "Music" => "its album",
            _ => null,
        },
        IngestionWaitingForKeys.Artwork => "artwork",
        _ => null,
    };
}
