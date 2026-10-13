using MediaEngine.Contracts.Settings;

namespace MediaEngine.Web.Models.ViewDTOs;

public static class SettingsContractExtensions
{
    public static IReadOnlyList<string> GetEffectiveSourcePaths(this LibraryFolderDto library) =>
        library.Sources
            .Select(source => source.Path)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>The label people see for a folder: its Label, else the folder name, else (a drive root) the full path.</summary>
    public static string DisplayName(this LibrarySourceDto source)
    {
        if (!string.IsNullOrWhiteSpace(source.Notes))
        {
            return source.Notes;
        }

        var name = Path.GetFileName(source.Path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        return string.IsNullOrWhiteSpace(name) ? source.Path : name;
    }

    public static LibrarySourceDto? GetPrimaryDestination(this LibraryFolderDto library) =>
        string.IsNullOrWhiteSpace(library.PrimaryDestinationSourceId)
            ? null
            : library.Sources.FirstOrDefault(source => string.Equals(
                source.Id,
                library.PrimaryDestinationSourceId,
                StringComparison.OrdinalIgnoreCase));
}
