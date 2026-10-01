using MediaEngine.Contracts.Artwork;

namespace MediaEngine.Web.Components.MediaEditor;

public static class MediaEditorEffectiveCoverPresentation
{
    public static bool SupportsOwnedFileCover(string? mediaType) => mediaType is
        "Books" or "Comic" or "Comics" or "Audiobooks" or "Movies" or "Music";

    public static string OwnerLabel(EffectiveArtworkSelection cover) =>
        cover.SourceEntityType.Equals("Edition", StringComparison.OrdinalIgnoreCase)
            ? "Edition cover for this file"
            : cover.IsInherited
                ? $"Inherited from {cover.SourceEntityType}"
                : $"{cover.SourceEntityType} cover";
}
