using MediaEngine.Domain.Configuration;

namespace MediaEngine.Api.Services.Settings;

/// <summary>Keeps the optional import folder separate from every library folder.</summary>
internal static class ImportFolderValidation
{
    internal static string? Validate(LibrariesConfiguration proposed)
    {
        var location = ImportFolderRules.Find(proposed);
        if (location is null)
        {
            return null;
        }

        if (!Path.IsPathFullyQualified(location.Path))
        {
            return "The import folder must be an absolute path on the server.";
        }

        if (!location.AllowWrite)
        {
            return "The import folder must be writable so Library can move imported files out of it.";
        }

        var import = Normalize(location.Path);
        foreach (var library in proposed.Libraries)
        {
            foreach (var source in library.Sources.Where(source => Path.IsPathFullyQualified(source.Path)))
            {
                var folder = Normalize(source.Path);
                if (Overlaps(import, folder))
                {
                    return $"The import folder can't overlap the {library.Name} folder {source.Path}. Choose a separate folder.";
                }
            }
        }

        foreach (var other in proposed.StorageLocations.Where(other =>
                     !string.Equals(other.Id, ImportFolderRules.LocationId, StringComparison.OrdinalIgnoreCase)
                     && Path.IsPathFullyQualified(other.Path)))
        {
            if (Overlaps(import, Normalize(other.Path)))
            {
                return $"The import folder can't overlap {other.Label}. Choose a separate folder.";
            }
        }

        return null;
    }

    private static string Normalize(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    private static bool Overlaps(string left, string right) => Within(left, right) || Within(right, left);

    private static bool Within(string parent, string child)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        // A drive root or "/" already ends with a separator.
        var prefix = Path.EndsInDirectorySeparator(parent) ? parent : parent + Path.DirectorySeparatorChar;
        return string.Equals(parent, child, comparison)
            || child.StartsWith(prefix, comparison)
            || child.StartsWith(parent + Path.AltDirectorySeparatorChar, comparison);
    }
}
