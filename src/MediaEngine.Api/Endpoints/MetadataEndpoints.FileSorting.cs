using MediaEngine.Api.Http;
using MediaEngine.Api.Security;
using MediaEngine.Contracts.Editing;
using MediaEngine.Domain.Authorization;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Models;
using MediaEngine.Ingestion.Contracts;
using MediaEngine.Ingestion.Extras;

namespace MediaEngine.Api.Endpoints;

public static partial class MetadataEndpoints
{
    private const int MaxFolderFiles = 25;
    private const int UnmatchedLookupPage = 500;

    /// <summary>
    /// The read behind "What is this file?" in Match &amp; Identity (universal editor v4, D20): whether the item is a single file, the guess its
    /// name gives for an Extra's kind and place, and the other files in the same folder the Engine could not place, so they can be sorted in one
    /// pass. A read; nothing is written until Save changes.
    /// </summary>
    private static void MapFileSortingEndpoints(RouteGroupBuilder group)
    {
        group.MapGet("/{entityId:guid}/file-sorting", async (
            Guid entityId, ILibraryItemDeletionRepository works, ILibraryFolderResolver folders, IItemExtrasScanner scanner,
            IUnmatchedAssetRepository unmatched, CancellationToken ct) =>
        {
            var found = (await works.GetWorksAsync([entityId], ct)).FirstOrDefault();
            if (found is null)
            {
                return ApiErrors.NotFound("File sorting was not found.");
            }

            var dto = new EditorFileSortingDto { EntityId = entityId };
            if (found.Assets.Count != 1 || !File.Exists(found.Assets[0].FilePath))
            {
                return Results.Ok(dto);
            }

            var path = found.Assets[0].FilePath;
            var titleKind = TitleKindOf(found.MediaType);
            var extension = Path.GetExtension(path);
            var fileKind = ExtrasRule.KindOf(extension);
            var title = await scanner.ResolveTitleFolderAsync(path, ct);

            dto.CanSort = true;
            dto.FileName = Path.GetFileName(path);
            dto.TitleKind = titleKind;
            dto.TitleName = title is null ? null : Path.GetFileName(Path.TrimEndingDirectorySeparator(title.ItemFolder));
            dto.InTitleFolder = title is not null;
            dto.CanBeExtra = ExtrasRule.IsExtraKindExtension(extension);
            if (dto.CanBeExtra)
            {
                dto.ExtraKinds = [.. ExtraKinds.ForFile(fileKind)];
                dto.Guess = GuessFor(path, fileKind, titleKind);
            }

            // The other files in this folder that the Engine could not place, so one pass can sort them all.
            var libraryId = folders.ResolveSourceForPath(path)?.Library.Id;
            if (libraryId is not null)
            {
                var folder = Path.GetDirectoryName(Path.GetFullPath(path));
                var page = await unmatched.ListByLibraryAsync(libraryId, includeKept: true, 0, UnmatchedLookupPage, ct);
                dto.FolderFiles =
                [
                    .. page.Items
                        .Where(item => item.WorkId is { } id && id != entityId
                            && string.Equals(Path.GetDirectoryName(Path.GetFullPath(item.FilePath)), folder, StringComparison.OrdinalIgnoreCase))
                        .Take(MaxFolderFiles)
                        .Select(item =>
                        {
                            var siblingExtension = Path.GetExtension(item.FilePath);
                            var canBeExtra = ExtrasRule.IsExtraKindExtension(siblingExtension);
                            return new EditorFolderFileDto
                            {
                                EntityId = item.WorkId!.Value,
                                FileName = Path.GetFileName(item.FilePath),
                                CanBeExtra = canBeExtra,
                                Guess = canBeExtra ? GuessFor(item.FilePath, ExtrasRule.KindOf(siblingExtension), titleKind) : null,
                            };
                        }),
                ];
            }

            return Results.Ok(dto);
        }).WithName("GetEditorFileSorting")
          .Produces<EditorFileSortingDto>(StatusCodes.Status200OK)
          .ProducesProblem(StatusCodes.Status404NotFound)
          .RequireAdministratorOrApplication(ApplicationPermissionIds.MetadataRead)
          .RequireAnyCatalogueEntityAccess(ApplicationPermissionIds.MetadataRead);
    }

    private static string TitleKindOf(string mediaType)
    {
        if (mediaType.Equals("TV", StringComparison.OrdinalIgnoreCase))
        {
            return EditorFileSortingTitleKinds.Show;
        }

        if (mediaType.StartsWith("Movie", StringComparison.OrdinalIgnoreCase))
        {
            return EditorFileSortingTitleKinds.Film;
        }

        return mediaType.StartsWith("Music", StringComparison.OrdinalIgnoreCase)
            ? EditorFileSortingTitleKinds.Album
            : EditorFileSortingTitleKinds.Other;
    }

    // Only the parts of the guess that make sense for this kind of title are kept: a season or episode for a show, a track for an album.
    private static EditorFileGuessDto GuessFor(string path, string fileKind, string titleKind)
    {
        var parent = Path.GetFileName(Path.GetDirectoryName(Path.GetFullPath(path)) ?? string.Empty);
        var guess = ExtraNameGuesser.Guess(Path.GetFileName(path), fileKind, parent);
        return new EditorFileGuessDto
        {
            ExtraKind = guess.ExtraKind,
            Season = titleKind == EditorFileSortingTitleKinds.Show ? guess.Season : null,
            Episode = titleKind == EditorFileSortingTitleKinds.Show ? guess.Episode : null,
            Track = titleKind == EditorFileSortingTitleKinds.Album ? guess.Track : null,
        };
    }
}
