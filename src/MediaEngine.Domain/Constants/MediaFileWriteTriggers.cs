namespace MediaEngine.Domain.Constants;

/// <summary>
/// Why a row of the durable file-write queue (<c>media_file_write_intents</c>) was queued. Every trigger brings the file's tags up to date;
/// <see cref="LibraryMove"/> also asks for the file to be renamed and moved to fit its new place (Details overlay D19).
/// </summary>
public static class MediaFileWriteTriggers
{
    /// <summary>A person saved changes in the editor.</summary>
    public const string EditorCommit = "editor_commit";

    /// <summary>
    /// A person moved the item to another album, season, series or audiobook. The library change is already saved; the queue now
    /// renames and moves the file (when its folder is Managed and writable) and then brings its tags up to date. It survives a restart:
    /// a row that was being worked on when the Engine stopped is picked up again, and doing the work twice changes nothing.
    /// </summary>
    public const string LibraryMove = "library_move";

    /// <summary>
    /// Library filed the file on its own (a newly identified file in a Managed folder, or a file out of the Import folder). The move waits
    /// for the next run and is done with the tags (Library setup D62). On a row that has no tags waiting this is the whole job: the row
    /// is removed once the move is checked. It is not a tag-writing trigger, so the tag-writing switches do not apply to it.
    /// </summary>
    public const string Filing = "filing";

    /// <summary>Embedded artwork for a Music or Audiobooks file needs bringing up to date.</summary>
    public const string Artwork = "artwork";

    /// <summary>An administrator asked for a failed artwork embed to be tried again.</summary>
    public const string ArtworkRetry = "artwork_retry";

    /// <summary>The file was put back from the Recycle bin.</summary>
    public const string Restore = "restore";

    /// <summary>
    /// A View item became known with a profile that added it (a new file in a Managed folder, an upload, or a move into the shared library),
    /// so the profile's name is written into the file when the format can hold it (batched write-back D7).
    /// </summary>
    public const string ViewAddedBy = "view_added_by";

    /// <summary>
    /// The triggers of a write a person asked for with an explicit edit of a field (Editing Mode: the editor, a manual override, the item's
    /// own preferences or an applied canonical choice). Library writes nothing on its own into a file it could not match (Library setup
    /// D64), but a person's own edit is written as normal.
    /// </summary>
    public static IReadOnlyList<string> PersonsEdits { get; } = [EditorCommit, "manual_override", "item_preferences", "item_canonical_apply"];
}
