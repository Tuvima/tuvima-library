using MediaEngine.Web.Services.Theming;

namespace MediaEngine.Web.Services.Editing;

/// <summary>Who sees the Move entry, and for which editor target (details overlay spec D10, D12 and the Engine's own refusals).</summary>
public static class LibraryMoveRules
{
    // Whole containers are not moved (spec section 4): a show, season, album or series is only ever the place something moves into.
    private static readonly HashSet<string> ContainerScopes = new(StringComparer.OrdinalIgnoreCase)
    {
        "series", "season", "album", "show", "collection", "universe", "artist", "person", "franchise",
    };

    /// <summary>True for an editor target that is one title (a song, episode, book, comic issue, movie, video or audiobook part), never a container.</summary>
    public static bool IsMovableScope(string? scopeId) =>
        !string.IsNullOrWhiteSpace(scopeId) && !ContainerScopes.Contains(scopeId.Trim());

    /// <summary>
    /// The Move entry is shown for one title, in Editing Mode, on a device that may edit. Structural editing is desktop-only, so a tablet
    /// or phone never sees it (the Engine still decides who may move).
    /// </summary>
    public static bool CanShowMove(DeviceContextService? device, string? scopeId, Guid itemId) =>
        EditorDeviceRule.CanEdit(device) && IsMovableScope(scopeId) && itemId != Guid.Empty;

    /// <summary>
    /// True when a committed move removed the Work the editor was showing, so a page showing it must follow to the surviving Work. That is
    /// when the edited title itself was moved and the Engine named a different Work for it (it was merged into an entry's own title), or when
    /// a part of the edited audiobook joined another audiobook and the move left the edited one empty and removed (the removed places name it).
    /// A part joining another audiobook while the edited one still has other parts removes nothing: the edited Work stays.
    /// </summary>
    /// <param name="editedWorkId">The Work the editor was showing.</param>
    /// <param name="movedItemId">The item that moved: the edited Work itself, or one of its parts (an Edition).</param>
    /// <param name="resultEntityId">The Work the Engine says the item now lives in or as.</param>
    /// <param name="editedWorkTitles">What the edited Work is called (the editor's own title and the page's), to find it among the removed places.</param>
    /// <param name="removedContainers">The names of the places the move left empty and removed.</param>
    public static bool RemovedEditedWork(
        Guid editedWorkId,
        Guid movedItemId,
        Guid? resultEntityId,
        IEnumerable<string?> editedWorkTitles,
        IReadOnlyList<string>? removedContainers)
    {
        if (resultEntityId is not { } result || result == Guid.Empty || result == editedWorkId)
        {
            return false;
        }

        if (movedItemId == editedWorkId)
        {
            return true;
        }

        return removedContainers is { Count: > 0 }
            && editedWorkTitles
                .Where(title => !string.IsNullOrWhiteSpace(title))
                .Any(title => removedContainers.Any(name => string.Equals(name.Trim(), title!.Trim(), StringComparison.OrdinalIgnoreCase)));
    }
}
