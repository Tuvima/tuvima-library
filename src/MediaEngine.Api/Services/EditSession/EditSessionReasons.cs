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

    public static string DuplicateField(string key) => $"'{key}' is changed twice for the same item.";
}
