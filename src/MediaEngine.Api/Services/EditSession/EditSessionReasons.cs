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
    public const string BadPosition = "The track number is not valid. Use a whole number from 1 to 9999.";
    public const string NotInThatAlbum = "This song is no longer in that album.";

    public static string DuplicateField(string key) => $"'{key}' is changed twice for the same item.";
}
