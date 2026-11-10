using MediaEngine.Storage.Playback;

namespace MediaEngine.Storage.Tests;

/// <summary>Test reads of the bookmark table through the editor's repository, which lists every profile's rows on a title.</summary>
internal static class BookmarkReads
{
    /// <summary>The timed bookmarks the profile made on the title (its own rows only), limited to the given files when any.</summary>
    public static async Task<IReadOnlyList<EditorBookmarkRow>> OwnAsync(
        this EditorBookmarkRepository editor, Guid profileId, Guid workId, IReadOnlySet<Guid>? allowed = null) =>
        [.. (await editor.GetByWorksAsync([workId], allowed)).Where(row => row.ProfileId == profileId)];

    /// <summary>Every profile's bookmarks on the title, limited to the given files when any.</summary>
    public static Task<IReadOnlyList<EditorBookmarkRow>> AllAsync(
        this EditorBookmarkRepository editor, Guid workId, IReadOnlySet<Guid>? allowed = null) =>
        editor.GetByWorksAsync([workId], allowed);
}
