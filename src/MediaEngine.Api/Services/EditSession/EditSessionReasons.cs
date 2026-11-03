namespace MediaEngine.Api.Services.EditSession;

/// <summary>The plain-English reasons an edit session gives. Kept in one place so the Review dialog and the tests agree.</summary>
public static class EditSessionReasons
{
    public const string UnknownKind = "This kind of change is not recognised.";
    public const string NotAvailableYet = "This kind of change cannot be saved yet.";
    public const string DuplicateId = "Two changes share the same id.";
    public const string NoTarget = "The item to change is missing.";
    public const string NoFields = "There is nothing to change.";
    public const string Gone = "This item is no longer in your library.";
    public const string SaveFailed = "It could not be saved. Try again.";
    public const string UndoneBecauseAnotherFailed = "Not saved, because another change in this save could not be.";
    public const string NotRunBecauseDataNotSaved = "Not run, because the details were not saved.";
    public const string NotRunBecauseCheckFailed = "Not saved, because another change failed its check.";

    public const string MatchUnreadable = "The match you chose could not be read. Pick it again.";
    public const string SessionUnavailable = "The editing session is unavailable. Sign in again and retry.";
    public const string NoDestination = "The album, season or series to move to is missing.";
    public const string NoFiles = "This item has no files to move.";
    public const string FilesStayPut = "The files do not need to move.";
    public const string NotAllowedToEdit = "You do not have permission to edit this item.";
    public const string SubjectUnavailable = "This cannot be opened for editing.";
    public const string BadPosition = "The track number is not valid. Use a whole number from 1 to 9999.";
    public const string NotInThatAlbum = "This song is no longer in that album.";

    public const string NotAVideo = "That is not a video.";
    public const string SeriesGone = "That series is no longer available. Pick it again.";
    public const string SeriesNeedsName = "Give the new series a name.";
    public const string SeriesNameTooLong = "The series name is too long.";
    public const string AlreadyAlone = "This video is not in a series.";
    public const string OrderChanged = "The videos of this series changed. Open the editor again to reorder them.";
    public const string NothingToOrder = "A series needs at least two videos to be put in order.";
    public const string RenameNotAllowed = "Library can only rename files in a Managed folder. Some of these videos are in a folder it only reads, or a file is missing.";
    public const string SeriesClash = "The series changed at the same moment. Open the editor again and retry.";

    public static string DuplicateField(string key) => $"'{key}' is changed twice for the same item.";
}
