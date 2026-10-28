using MediaEngine.Domain.Enums;
using MediaEngine.Ingestion.Models;

namespace MediaEngine.Ingestion.Contracts;

/// <summary>
/// Everything the pipeline needs to know about the optional import folder: where it is, which media type a file's
/// folder implies, and the one managed folder each type is filed into. Answers come from the current configuration
/// on every call, so setting the folder, or marking a different New media folder, applies without a restart.
/// </summary>
public interface IImportFolderService
{
    /// <summary>The import folder as configured now, or <see langword="null"/> when none is set.</summary>
    string? GetImportPath();

    /// <summary>True when <paramref name="path"/> is the import folder or anywhere inside it, including Tuvima's own working folder.</summary>
    bool IsInImportFolder(string path);

    /// <summary>True when <paramref name="path"/> is inside the working folder (<c>.data</c>) the import folder keeps for Tuvima.</summary>
    bool IsWorkingPath(string path);

    /// <summary>
    /// True when <paramref name="path"/> is inside the Photos or Personal videos section of the import folder. Those belong to the personal
    /// photo and video filer; the catalogue pipeline must not read, identify or file anything in them.
    /// </summary>
    bool IsViewSectionPath(string path) => false;

    /// <summary>
    /// The media type implied by the per-type folder (<c>Movies</c>, <c>TV</c>, <c>Music</c>, <c>Books</c>,
    /// <c>Audiobooks</c>, <c>Comics</c>) a file sits in, or <see langword="null"/> for a file at the root or anywhere else.
    /// </summary>
    MediaType? ResolveTypePrior(string path);

    /// <summary>The per-type folder that holds <paramref name="path"/>, or <see langword="null"/> for a file at the root.</summary>
    string? ResolveTypeFolder(string path);

    /// <summary>The catalogued library that holds a media type, whether or not it has a managed folder yet, or <see langword="null"/>.</summary>
    LibraryFolderEntry? ResolveLibraryForType(MediaType mediaType);

    /// <summary>
    /// Every catalogued library that holds a media type, in settings order. The Vault's entry is also a Videos library, so a caller
    /// that attributes a file to "the" library of a type picks the first one that is not a Vault library.
    /// </summary>
    IReadOnlyList<LibraryFolderEntry> ResolveLibrariesForType(MediaType mediaType) =>
        ResolveLibraryForType(mediaType) is { } library ? [library] : [];

    /// <summary>
    /// The single managed folder marked New media for a type, checked right now for being reachable, writable and
    /// roomy enough for <paramref name="requiredBytes"/>. As-is folders are never returned, and there is no fallback.
    /// </summary>
    ImportDestination ResolveDestination(MediaType mediaType, long requiredBytes = 0);

    /// <summary>What to do with an exact copy of a file already held (<c>keep_one</c> or <c>keep_both</c>), read from the current configuration.</summary>
    string DuplicatePolicy { get; }

    /// <summary>
    /// The duplicate rule that applies to an import copy whose exact twin is already held at <paramref name="heldCopyPath"/>:
    /// the rule of the folder (or library) holding that copy when it sets one, otherwise <see cref="DuplicatePolicy"/>.
    /// </summary>
    string ResolveDuplicatePolicy(string heldCopyPath);

    /// <summary>
    /// The mutation policy for the import folder itself, so moving a file out of it, and removing an exact duplicate
    /// copy of it, go through the same source-policy gate as every other change to files. <see langword="null"/> when
    /// no import folder is set.
    /// </summary>
    FileSourceMutationPolicy? CreateImportPolicy(bool allowDelete = false);

    /// <summary>
    /// Creates the seven per-type folders inside the import folder if they are missing. Existing folders and files are left
    /// alone. The import folder itself is created only when <paramref name="createImportFolder"/> is true, so a drive
    /// that is offline is never replaced by an empty local folder at startup.
    /// </summary>
    IReadOnlyList<string> EnsureSubfolders(bool createImportFolder = false);

    /// <summary>
    /// Removes folders that a download program left empty, walking up from <paramref name="startFolder"/>. It never
    /// removes the import folder, a per-type folder or the working folder, and never leaves the import folder.
    /// </summary>
    void RemoveEmptyFolders(string startFolder);
}
