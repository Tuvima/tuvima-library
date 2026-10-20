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
}
