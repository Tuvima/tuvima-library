using System.Globalization;
using System.Text.Json;
using MediaEngine.Contracts.Review;

namespace MediaEngine.Web.Services.Editing;

/// <summary>
/// Reads the TMDB TV suggestion the Engine stores in <c>candidates_json</c> for review items whose
/// trigger is <c>MovieMatchedAsTv</c> ("Found as a TV title"), and words it for the user.
/// </summary>
public static class MovieTvSuggestionReader
{
    public const string Trigger = "MovieMatchedAsTv";

    /// <summary>The suggestion carried by the review item, or <c>null</c> when it is missing or unreadable.</summary>
    public static MovieTvSuggestionDto? Read(string? candidatesJson)
    {
        if (string.IsNullOrWhiteSpace(candidatesJson))
        {
            return null;
        }

        try
        {
            var suggestion = JsonSerializer.Deserialize<List<MovieTvSuggestionDto>>(candidatesJson)?.FirstOrDefault();
            return suggestion is not null && !string.IsNullOrWhiteSpace(suggestion.Name) ? suggestion : null;
        }
        catch (JsonException)
        {
            // Unreadable candidates behave like no suggestion; the row then falls back to the normal Review action.
            return null;
        }
    }

    /// <summary>
    /// For example <c>TMDB lists this as the miniseries 'Dr. Horrible's Sing-Along Blog' (2008, 3 parts). Move it to TV?</c>
    /// </summary>
    public static string Describe(MovieTvSuggestionDto suggestion)
    {
        var kind = string.IsNullOrWhiteSpace(suggestion.Type) ? "TV series" : suggestion.Type.Trim().ToLowerInvariant();
        var facts = new List<string>();
        if (suggestion.FirstAirYear is { } year)
        {
            facts.Add(year.ToString(CultureInfo.InvariantCulture));
        }

        if (suggestion.Episodes > 0)
        {
            facts.Add(suggestion.Episodes == 1 ? "1 part" : $"{suggestion.Episodes} parts");
        }

        var detail = facts.Count > 0 ? $" ({string.Join(", ", facts)})" : string.Empty;
        return $"TMDB lists this as the {kind} '{suggestion.Name}'{detail}. Move it to TV?";
    }
}
