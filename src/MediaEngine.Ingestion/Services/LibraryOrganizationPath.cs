using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Enums;
using MediaEngine.Ingestion.Contracts;
using MediaEngine.Ingestion.Models;

namespace MediaEngine.Ingestion.Services;

/// <summary>
/// Applies the library's naming policy to ingestion and managed promotion. A naming template names the path inside a
/// type's own folder (<c>{Author}/{Title} ({Year})/...</c> inside <c>library/Books</c>), never the category folder itself,
/// so every route that files a file joins the template onto that folder.
/// </summary>
public static class LibraryOrganizationPath
{
    /// <summary>The category of a file whose type is unknown. Such a file is never filed by a template.</summary>
    public const string UnknownCategory = "Other";

    public static string Calculate(IFileOrganizer organizer, IngestionCandidate candidate,
        LibraryFolderEntry? library, string standardTemplate)
    {
        if (library?.OrganizationMode == LibraryOrganizationModes.KeepOriginalNames)
        {
            var source = library.Sources.Where(source =>
                    PathSafety.IsContainedBy(Path.GetFullPath(candidate.Path), Path.GetFullPath(source.Path)))
                .OrderByDescending(source => source.Path.Length).FirstOrDefault();
            if (source is null)
            {
                return Path.GetFileName(candidate.Path);
            }

            var root = source.Path;
            var staging = Path.Combine(root, ".data", "staging");
            if (PathSafety.IsContainedBy(Path.GetFullPath(candidate.Path), Path.GetFullPath(staging)))
            {
                root = staging;
            }

            return Path.GetRelativePath(root, candidate.Path);
        }

        var template = library?.OrganizationMode == LibraryOrganizationModes.Custom
            ? library.CustomOrganizationTemplate ?? throw new InvalidOperationException("A custom naming template is required.")
            : standardTemplate;
        return organizer.CalculatePath(candidate, template);
    }

    /// <summary>The category folder a type is filed under (Books, TV, ...), or <see cref="UnknownCategory"/>.</summary>
    public static string Category(MediaType? mediaType) => mediaType switch
    {
        MediaType.Books => "Books",
        MediaType.Comics => "Comics",
        MediaType.Movies => "Movies",
        MediaType.TV => "TV",
        MediaType.Audiobooks => "Audiobooks",
        MediaType.Music => "Music",
        MediaType.Videos => "Videos",
        _ => UnknownCategory,
    };

    /// <summary>
    /// The category the organisation gate checks: the type's category when a template names the file, or null when the
    /// library keeps original names and no template applies.
    /// </summary>
    public static string? FilingCategory(LibraryFolderEntry? library, MediaType? mediaType) =>
        library?.OrganizationMode == LibraryOrganizationModes.KeepOriginalNames ? null : Category(mediaType);

    /// <summary>
    /// The folder a template is joined onto when a file is filed into its library: the library's New media folder, or,
    /// for a file with no library, the type's category folder inside the library folder.
    /// </summary>
    public static string TypeFolder(LibraryFolderEntry? library, string libraryFolder, MediaType? mediaType) =>
        !string.IsNullOrWhiteSpace(library?.LibraryRoot)
            ? library.LibraryRoot
            : Path.Combine(libraryFolder, Category(mediaType));
}
