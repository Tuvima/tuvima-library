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
}
