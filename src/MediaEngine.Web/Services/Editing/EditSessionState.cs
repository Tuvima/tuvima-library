namespace MediaEngine.Web.Services.Editing;

/// <summary>The three tools of the universal editor (spec D1).</summary>
public enum EditTool
{
    Details,
    Artwork,
    Match,
}

/// <summary>What the page behind the editor is showing: the normal page, or the labelled temporary view used while picking artwork (spec D9).</summary>
public enum EditViewContext
{
    Normal,
    TemporaryArtwork,
}

/// <summary>One step of the item trail ("Le Continental > Season 1 > Episode 1").</summary>
public sealed record EditTrailItem(Guid EntityId, string Title, string Kind);

/// <summary>
/// The state of one edit session on one page, as four separate pieces on purpose (spec WP2): the active item, the active tool,
/// the view context and the pending changes. Switching item or tool never touches the pending list; only
/// <see cref="Cancel"/> and <see cref="End"/> clear it. It lives on the page, and a deep link never starts one.
/// </summary>
public sealed class EditSessionState
{
    public EditSessionState(PendingEditList pending)
    {
        Pending = pending;
        Pending.Changed += RaiseChanged;
    }

    public PendingEditList Pending { get; }

    public event Action? Changed;

    public bool IsActive { get; private set; }

    /// <summary>The item being edited now.</summary>
    public EditTrailItem? ActiveItem { get; private set; }

    /// <summary>Where the active item sits, outermost first, ending with the active item. Empty when nothing is active.</summary>
    public IReadOnlyList<EditTrailItem> Trail { get; private set; } = [];

    /// <summary>The open tool, or null while the panel is closed (spec "Normal": bar on top, panel closed until a tool is chosen).</summary>
    public EditTool? ActiveTool { get; private set; }

    public EditViewContext ViewContext { get; private set; }

    public bool HasPendingChanges => !Pending.IsEmpty;

    /// <summary>Starts editing at the end of <paramref name="trail"/>. Starting again while active only moves to the new item.</summary>
    public void Begin(IReadOnlyList<EditTrailItem> trail)
    {
        ArgumentNullException.ThrowIfNull(trail);
        if (trail.Count == 0)
        {
            throw new ArgumentException("An edit session needs at least the item being edited.", nameof(trail));
        }

        IsActive = true;
        Trail = trail;
        ActiveItem = trail[^1];
        RaiseChanged();
    }

    /// <summary>Moves to another item (show, season, episode; album, track). Pending changes on every item are kept.</summary>
    public void SwitchItem(IReadOnlyList<EditTrailItem> trail)
    {
        if (!IsActive)
        {
            throw new InvalidOperationException("No edit session is active.");
        }

        ArgumentNullException.ThrowIfNull(trail);
        if (trail.Count == 0)
        {
            throw new ArgumentException("The trail must end with the item being edited.", nameof(trail));
        }

        Trail = trail;
        ActiveItem = trail[^1];
        // The temporary artwork view belongs to the item it was opened for.
        ViewContext = EditViewContext.Normal;
        RaiseChanged();
    }

    /// <summary>Opens a tool, or closes the panel when null. Closing the panel keeps edit mode and the pending list (spec D22).</summary>
    public void SetTool(EditTool? tool)
    {
        if (!IsActive)
        {
            throw new InvalidOperationException("No edit session is active.");
        }

        ActiveTool = tool;
        if (tool != EditTool.Artwork)
        {
            ViewContext = EditViewContext.Normal;
        }

        RaiseChanged();
    }

    public void SetViewContext(EditViewContext context)
    {
        if (!IsActive)
        {
            throw new InvalidOperationException("No edit session is active.");
        }

        ViewContext = context;
        RaiseChanged();
    }

    /// <summary>Cancel: leaves edit mode and throws the pending changes away. Callers ask "Unsaved changes" first when <see cref="HasPendingChanges"/>.</summary>
    public void Cancel()
    {
        Pending.Discard();
        End();
    }

    /// <summary>Leaves edit mode after a successful save (the pending list was already applied).</summary>
    public void End()
    {
        IsActive = false;
        ActiveItem = null;
        Trail = [];
        ActiveTool = null;
        ViewContext = EditViewContext.Normal;
        RaiseChanged();
    }

    private void RaiseChanged() => Changed?.Invoke();
}
