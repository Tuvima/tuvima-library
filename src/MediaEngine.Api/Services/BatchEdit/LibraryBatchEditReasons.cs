namespace MediaEngine.Api.Services.BatchEdit;

/// <summary>The plain-English reasons a batch edit gives when it leaves an item out, shared by the access check and the service.</summary>
public static class LibraryBatchEditReasons
{
    public const string NotAllowed = "You are not allowed to edit this item.";

    public const string Updating = "This item is still being updated. Try again when it finishes.";

    /// <summary>The id belongs to an edition, collection or person: it exists, but a batch cannot change it.</summary>
    public const string NotATitle = "Not a library title that can be batch edited.";

    /// <summary>The id matches nothing a batch can change. A deleted title and a photo (which lives outside the library) look the same from here.</summary>
    public const string Gone = "This item is no longer in your library, or it is not a title that can be batch edited.";

    public static string EmptiedList(string label) =>
        $"{label} can't be emptied here; the provider's {label.ToLowerInvariant()} would come back.";
}
