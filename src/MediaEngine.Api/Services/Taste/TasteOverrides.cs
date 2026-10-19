using System.Globalization;
using System.Text.RegularExpressions;
using MediaEngine.Contracts.Taste;
using MediaEngine.Domain.Models;

namespace MediaEngine.Api.Services.Taste;

/// <summary>
/// Applies a person's removals and additions on top of the taste the Library learned, and checks the values they type.
/// The learned profile is never changed: a removed value is hidden and an added value is shown as "Added by you".
/// </summary>
public static partial class TasteOverrides
{
    public const int MaxValueLength = 60;

    /// <summary>Maps the wire group (<c>genres</c>, <c>moods</c>, <c>eras</c>) to the stored group, or null when unknown.</summary>
    public static string? ToStoredGroup(string? group) => group?.Trim().ToLowerInvariant() switch
    {
        TasteValues.Genres => TasteGroups.Genre,
        TasteValues.Moods => TasteGroups.Mood,
        TasteValues.Eras => TasteGroups.Era,
        _ => null,
    };

    /// <summary>
    /// Trims, collapses spaces and lower-cases a typed value the same way the taste profiler writes its keys. Returns null when
    /// the value is empty, too long, holds control characters or, for an era, is not a decade such as <c>1990s</c>.
    /// </summary>
    public static string? Normalize(string storedGroup, string? value)
    {
        var text = value is null ? string.Empty : WhitespaceRun().Replace(value.Trim(), " ").ToLowerInvariant();
        if (text.Length == 0 || text.Length > MaxValueLength || text.Any(char.IsControl))
        {
            return null;
        }

        return storedGroup == TasteGroups.Era && !DecadePattern().IsMatch(text) ? null : text;
    }

    /// <summary>One group's items: learned values minus the removed ones, plus the typed ones, strongest first.</summary>
    public static List<TasteItemDto> Items(
        IReadOnlyDictionary<string, double>? learned, IEnumerable<TasteOverride> overrides, string storedGroup)
    {
        var removed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var added = new List<string>();
        foreach (var change in overrides.Where(change => change.Group == storedGroup))
        {
            if (change.Action == TasteOverrideActions.Removed)
            {
                removed.Add(change.Value);
            }
            else if (change.Action == TasteOverrideActions.Added)
            {
                added.Add(change.Value);
            }
        }

        var items = new List<TasteItemDto>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in (learned ?? new Dictionary<string, double>())
                     .Where(pair => pair.Value > 0 && !string.IsNullOrWhiteSpace(pair.Key))
                     .OrderByDescending(pair => pair.Value)
                     .ThenBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
        {
            var key = pair.Key.Trim();
            if (!removed.Contains(key) && seen.Add(key))
            {
                items.Add(new TasteItemDto(key, Math.Round(pair.Value, 4), TasteValues.SourceLearned));
            }
        }

        foreach (var value in added.Order(StringComparer.OrdinalIgnoreCase))
        {
            if (seen.Add(value))
            {
                items.Add(new TasteItemDto(value, null, TasteValues.SourceAdded));
            }
        }

        return items;
    }

    /// <summary>
    /// The learned profile with the person's removals and additions applied, for places that read it as numbers (the Overview's
    /// one-line summary). A typed value is given the strongest weight in its group so it can appear in the line.
    /// </summary>
    public static TasteProfile Apply(TasteProfile profile, IReadOnlyCollection<TasteOverride> overrides) => new()
    {
        UserId = profile.UserId,
        GenreDistribution = Weights(profile.GenreDistribution, overrides, TasteGroups.Genre),
        MoodPreferences = Weights(profile.MoodPreferences, overrides, TasteGroups.Mood),
        EraPreferences = Weights(profile.EraPreferences, overrides, TasteGroups.Era),
        MediaTypeMix = profile.MediaTypeMix,
        Summary = profile.Summary,
        LastUpdatedAt = profile.LastUpdatedAt,
    };

    private static Dictionary<string, double> Weights(
        IReadOnlyDictionary<string, double> learned, IReadOnlyCollection<TasteOverride> overrides, string storedGroup)
    {
        var top = learned.Count == 0 ? 1d : learned.Values.Max();
        var result = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in Items(learned, overrides, storedGroup))
        {
            result[item.Value] = item.Weight ?? top;
        }

        return result;
    }

    /// <summary>The decades offered by the Add control for eras, newest first.</summary>
    public static List<string> DecadeSuggestions(int currentYear)
    {
        var current = currentYear / 10 * 10;
        return Enumerable.Range(0, (current - 1900) / 10 + 1)
            .Select(step => (current - step * 10).ToString(CultureInfo.InvariantCulture) + "s")
            .ToList();
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRun();

    [GeneratedRegex(@"^(1[0-9]|20)\d0s$")]
    private static partial Regex DecadePattern();
}
