using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Services;
using MediaEngine.Storage.Contracts;

namespace MediaEngine.Api.Services.Display;

public sealed class DisplayLaneGroupPolicy
{
    private static readonly IReadOnlyDictionary<string, DisplayLaneGroupShelfPolicy> Defaults =
        new Dictionary<string, DisplayLaneGroupShelfPolicy>(StringComparer.OrdinalIgnoreCase)
        {
            ["watch"] = new(
                Key: "series",
                Title: "Series",
                Subtitle: "Movies dynamically aligned into series from your library metadata",
                SeeAllRoute: "/watch/movies?grouping=series",
                Enabled: true,
                MinimumSeriesItems: 2),
            ["read"] = new(
                Key: "series-and-reading-lists",
                Title: "Series & Reading Lists",
                Subtitle: "Book series, comic runs, and grouped reading",
                SeeAllRoute: "/read/books?grouping=series",
                Enabled: true,
                MinimumSeriesItems: 2),
        };

    private readonly IConfigurationLoader? _configLoader;

    public DisplayLaneGroupPolicy(IConfigurationLoader? configLoader = null)
    {
        _configLoader = configLoader;
    }

    public DisplayLaneGroupShelfPolicy GetShelf(string lane)
    {
        var normalized = DisplayMediaRules.NormalizeLane(lane) ?? lane;
        var fallback = Defaults.TryGetValue(normalized, out var defaultPolicy)
            ? defaultPolicy
            : new DisplayLaneGroupShelfPolicy($"{normalized}-groups", "Groups", "Grouped titles from your library", $"/{normalized}", true, 2);

        var settings = LoadPreferences().LaneGroupDisplay.GetValueOrDefault(normalized);
        if (settings is null)
        {
            return fallback;
        }

        return new DisplayLaneGroupShelfPolicy(
            Key: StringHelpers.FirstNonBlank(settings.ShelfKey, fallback.Key)!,
            Title: StringHelpers.FirstNonBlank(settings.Title, fallback.Title)!,
            Subtitle: StringHelpers.FirstNonBlank(settings.Subtitle, fallback.Subtitle)!,
            SeeAllRoute: StringHelpers.FirstNonBlank(settings.SeeAllRoute, fallback.SeeAllRoute),
            Enabled: settings.Enabled ?? fallback.Enabled,
            MinimumSeriesItems: Math.Max(1, settings.MinimumSeriesItems ?? fallback.MinimumSeriesItems));
    }

    private LibraryPreferencesSettings LoadPreferences()
    {
        if (_configLoader is null)
        {
            return new LibraryPreferencesSettings();
        }

        try
        {
            return _configLoader.LoadLibraryPreferences()
                   ?? new LibraryPreferencesSettings();
        }
        catch
        {
            // Display grouping defaults are safe to use if an operator is editing UI preferences live.
            return new LibraryPreferencesSettings();
        }
    }

}

public sealed record DisplayLaneGroupShelfPolicy(
    string Key,
    string Title,
    string Subtitle,
    string? SeeAllRoute,
    bool Enabled,
    int MinimumSeriesItems);
