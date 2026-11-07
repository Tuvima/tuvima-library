using System.Globalization;
using MediaEngine.Domain.Models;

namespace MediaEngine.Api.Services.Extras;

/// <summary>
/// How the supporting-files list reads a scope (universal editor v5 screen E, "Where"): which scopes a filter takes in, the words shown
/// for each, and the order they are listed in. A season takes in its episodes; a section takes in its lessons and the sections below it.
/// </summary>
internal static class SupportingFileScopes
{
    /// <summary>The words for the title itself (the browser's "Show level"); the Dashboard may say it more precisely for a kind of title.</summary>
    public const string TitleLevelLabel = "Title level";

    /// <summary>The words for no filter.</summary>
    public const string WholeTitleLabel = "Whole title";

    /// <summary>The level word on the wire for a scope.</summary>
    public static string LevelOf(ExtraScope scope) => scope.Level switch
    {
        ExtraScopeLevel.Season => "season",
        ExtraScopeLevel.Episode => "episode",
        ExtraScopeLevel.Folder => "folder",
        ExtraScopeLevel.File => "file",
        ExtraScopeLevel.Track => "track",
        _ => "root",
    };

    public static string LabelOf(ExtraScope scope)
    {
        if (scope.Level == ExtraScopeLevel.Root)
        {
            return TitleLevelLabel;
        }

        var label = scope.Label();
        return string.IsNullOrWhiteSpace(label) ? scope.Key : label;
    }

    /// <summary>True when a file that belongs to <paramref name="file"/> is selected by the Where filter <paramref name="filter"/>.</summary>
    public static bool Takes(ExtraScope filter, ExtraScope file)
    {
        switch (filter.Level)
        {
            case ExtraScopeLevel.Season:
                return file.Level is ExtraScopeLevel.Season or ExtraScopeLevel.Episode && file.Season == filter.Season;

            case ExtraScopeLevel.Folder:
                return file.Level is ExtraScopeLevel.Folder or ExtraScopeLevel.File
                    && !string.IsNullOrEmpty(file.Path)
                    && !string.IsNullOrEmpty(filter.Path)
                    && (string.Equals(file.Path, filter.Path, StringComparison.OrdinalIgnoreCase)
                        || file.Path.StartsWith(filter.Path + "/", StringComparison.OrdinalIgnoreCase));

            default:
                return file == filter;
        }
    }

    /// <summary>The scopes that sit above this one in the Where list (an episode's season, a lesson's sections), nearest last.</summary>
    public static IEnumerable<ExtraScope> Ancestors(ExtraScope scope)
    {
        switch (scope.Level)
        {
            case ExtraScopeLevel.Episode:
                yield return ExtraScope.ForSeason(scope.Season ?? 0);
                break;

            case ExtraScopeLevel.Folder or ExtraScopeLevel.File:
                var parts = (scope.Path ?? string.Empty).Split('/', StringSplitOptions.RemoveEmptyEntries);
                for (var length = 1; length < parts.Length; length++)
                {
                    yield return ExtraScope.ForFolder(string.Join('/', parts[..length]));
                }

                break;
        }
    }

    /// <summary>The key that puts scopes in list order: the title, then seasons with their episodes, then sections with their lessons, then tracks.</summary>
    public static string SortKey(ExtraScope scope)
    {
        string Number(int? value) => (value ?? 0).ToString("D6", CultureInfo.InvariantCulture);
        return scope.Level switch
        {
            ExtraScopeLevel.Season => $"1|{Number(scope.Season)}|000000|",
            ExtraScopeLevel.Episode => $"1|{Number(scope.Season)}|{Number(scope.Episode)}|",
            ExtraScopeLevel.Folder => $"2|000000|000000|{scope.Path?.ToLowerInvariant()}",
            ExtraScopeLevel.File => $"2|000000|000000|{scope.Path?.ToLowerInvariant()}",
            ExtraScopeLevel.Track => $"3|000000|{Number(scope.Episode)}|",
            _ => "0|000000|000000|",
        };
    }
}
