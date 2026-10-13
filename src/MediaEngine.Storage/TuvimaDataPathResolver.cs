using System.Text.Json;
using MediaEngine.Domain.Services;

namespace MediaEngine.Storage;

/// <summary>
/// Resolves Tuvima's database path for every host process that needs to open
/// the library database. Keeping this in one place prevents recovery tooling
/// from silently targeting a different database than the Engine.
/// </summary>
public static class TuvimaDataPathResolver
{
    public static string ResolveDatabasePath(
        string configDirectory,
        string? environmentDatabasePath,
        string? environmentLibraryRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(configDirectory);

        if (!string.IsNullOrWhiteSpace(environmentDatabasePath))
        {
            return environmentDatabasePath;
        }

        string? libraryRoot = null;
        var coreJsonPath = Path.Combine(configDirectory, "core.json");
        if (File.Exists(coreJsonPath))
        {
            using var stream = File.OpenRead(coreJsonPath);
            using var document = JsonDocument.Parse(stream);
            if (document.RootElement.TryGetProperty("library_root", out var configuredLibraryRoot))
            {
                libraryRoot = TuvimaDataPaths.ExpandConfigToken(configuredLibraryRoot.GetString());
            }
        }

        if (!string.IsNullOrWhiteSpace(environmentLibraryRoot))
        {
            libraryRoot = environmentLibraryRoot;
        }

        return !string.IsNullOrWhiteSpace(libraryRoot)
            ? Path.Combine(libraryRoot, ".data", "database", "library.db")
            : Path.Combine(".data", "database", "library.db");
    }
}
