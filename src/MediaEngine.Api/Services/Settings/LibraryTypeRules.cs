using MediaEngine.Domain.Configuration;

namespace MediaEngine.Api.Services.Settings;

/// <summary>
/// Each media type has exactly one catalogued library; extra folders are added to that library instead.
/// Duplicates that already exist in the saved configuration are tolerated so older setups can still be edited and tidied.
/// </summary>
internal static class LibraryTypeRules
{
    /// <summary>Returns an error when <paramref name="proposed"/> introduces a second catalogued library for a media type.</summary>
    internal static string? ValidateOneLibraryPerType(LibrariesConfiguration current, LibrariesConfiguration proposed)
    {
        var existing = Duplicates(current);
        foreach (var (type, names) in Duplicates(proposed))
        {
            if (!existing.ContainsKey(type))
            {
                return $"{type} already has a library ({string.Join(", ", names)}). Add folders to it instead of creating another {type} library.";
            }
        }

        return null;
    }

    private static Dictionary<string, List<string>> Duplicates(LibrariesConfiguration configuration) => configuration.Libraries
        .Where(library => string.Equals(library.Kind, LibraryKinds.Catalogued, StringComparison.OrdinalIgnoreCase))
        .SelectMany(library => library.MediaTypes
            .Select(Normalize)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(type => (type, name: string.IsNullOrWhiteSpace(library.Name) ? library.Id : library.Name)))
        .GroupBy(entry => entry.type, StringComparer.OrdinalIgnoreCase)
        .Where(group => group.Count() > 1)
        .ToDictionary(group => group.Key, group => group.Select(entry => entry.name).ToList(), StringComparer.OrdinalIgnoreCase);

    private static string Normalize(string mediaType) => string.Equals(mediaType, "TVShows", StringComparison.OrdinalIgnoreCase) ? "TV" : mediaType;
}
