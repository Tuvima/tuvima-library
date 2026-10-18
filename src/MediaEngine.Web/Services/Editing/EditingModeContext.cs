using MediaEngine.Web.Services.Theming;

namespace MediaEngine.Web.Services.Editing;

/// <summary>The three destinations of the Editing Mode drawer. History is read-only and is not in the drawer.</summary>
public enum EditingDrawerTab
{
    Details,
    Artwork,
    Match,
}

/// <summary>
/// The state of Editing Mode on one detail page: whether it is on, which drawer tab is open (and which artwork slot
/// it was opened for), and whether anything typed is still unsaved. It lives on the page and ends when the page is
/// left or the user chooses Done; a deep link never starts in Editing Mode.
/// </summary>
/// <remarks>
/// Every way in (turning the mode on, opening the drawer, opening a field) re-checks <see cref="EditorDeviceRule"/>,
/// so a hidden button is never the only protection, and a device that stops qualifying (for example a window resized
/// to tablet width) leaves the mode on its own.
/// </remarks>
public sealed class EditingModeContext : IDisposable
{
    private readonly DeviceContextService _device;

    public EditingModeContext(DeviceContextService device, ItemFieldEditSession fields)
    {
        _device = device;
        Fields = fields;
        _device.OnChanged += OnDeviceChanged;
        Fields.Changed += RaiseChanged;
    }

    public ItemFieldEditSession Fields { get; }

    /// <summary>Whether the person has edit permission for this item (the Engine's own answer).</summary>
    public bool HasPermission { get; set; }

    /// <summary>Why Edit is unavailable right now, for the disabled button's tooltip (for example ingestion still running).</summary>
    public string? UnavailableReason { get; set; }

    /// <summary>Permission and a device that is allowed to edit. The toggle renders only when this is true.</summary>
    public bool CanEdit => HasPermission && EditorDeviceRule.CanEdit(_device);

    public bool IsOn { get; private set; }

    public bool DrawerOpen => DrawerTab is not null;

    public EditingDrawerTab? DrawerTab { get; private set; }

    /// <summary>The artwork asset type the drawer was opened for (CoverArt, Background, Logo), if any.</summary>
    public string? DrawerArtworkSlot { get; private set; }

    /// <summary>Changes on every drawer request so reopening the same tab and slot still re-applies it.</summary>
    public int DrawerRequestVersion { get; private set; }

    /// <summary>Set by the drawer while the editor inside it has unsaved input.</summary>
    public bool DrawerDirty { get; private set; }

    /// <summary>A field typed into on the page that has not been saved.</summary>
    public bool HasUnsavedField => Fields.IsDirty;

    public bool HasUnsavedInput => Fields.IsDirty || DrawerDirty;

    public event Action? Changed;

    /// <summary>Loads the item's editable fields the first time Editing Mode turns on (set by the page).</summary>
    public Func<Task>? Loader { get; set; }

    /// <summary>Turns Editing Mode on. Refused (and false) when the person or the device may not edit.</summary>
    public async Task<bool> EnterAsync()
    {
        if (!CanEdit)
        {
            return false;
        }

        IsOn = true;
        RaiseChanged();
        if (!Fields.Loaded && !Fields.Loading && Loader is not null)
        {
            await Loader();
        }

        return true;
    }

    /// <summary>True while the page is asking whether to discard unsaved input before Done.</summary>
    public bool LeavePromptRequested { get; private set; }

    /// <summary>
    /// The Edit / Done switch (also the details More entry): turns the mode on, or ends it. Ending it with unsaved
    /// input does not discard anything; it raises <see cref="LeavePromptRequested"/> so the page asks first.
    /// </summary>
    public async Task ToggleAsync()
    {
        if (!IsOn)
        {
            await EnterAsync();
            return;
        }

        if (HasUnsavedInput)
        {
            LeavePromptRequested = true;
            RaiseChanged();
            return;
        }

        Leave();
    }

    /// <summary>The person chose to keep editing.</summary>
    public void DismissLeavePrompt()
    {
        if (!LeavePromptRequested)
        {
            return;
        }

        LeavePromptRequested = false;
        RaiseChanged();
    }

    /// <summary>
    /// Ends Editing Mode. The caller has already asked about unsaved input; whatever is still open is dropped.
    /// </summary>
    public void Leave()
    {
        LeavePromptRequested = false;
        IsOn = false;
        DrawerTab = null;
        DrawerArtworkSlot = null;
        DrawerDirty = false;
        Fields.Discard();
        RaiseChanged();
    }

    /// <summary>Opens the drawer on a tab (and artwork slot). Refused while a field on the page is mid-edit.</summary>
    public bool OpenDrawer(EditingDrawerTab tab, string? artworkSlot = null)
    {
        if (!IsOn || !CanEdit || Fields.HasOpenDraft)
        {
            return false;
        }

        DrawerTab = tab;
        DrawerArtworkSlot = tab == EditingDrawerTab.Artwork ? artworkSlot : null;
        DrawerRequestVersion++;
        RaiseChanged();
        return true;
    }

    /// <summary>Records the tab the editor inside the drawer moved to, so the header names it. Does not reopen or reload anything.</summary>
    public void FollowDrawerTab(EditingDrawerTab tab)
    {
        if (DrawerTab is null || DrawerTab == tab)
        {
            return;
        }

        DrawerTab = tab;
        RaiseChanged();
    }

    /// <summary>Closes the drawer. The drawer has already guarded its own unsaved input.</summary>
    public void CloseDrawer()
    {
        if (DrawerTab is null)
        {
            return;
        }

        DrawerTab = null;
        DrawerArtworkSlot = null;
        DrawerDirty = false;
        RaiseChanged();
    }

    /// <summary>Reports whether the editor inside the drawer has unsaved input.</summary>
    public void SetDrawerDirty(bool dirty)
    {
        if (DrawerDirty == dirty)
        {
            return;
        }

        DrawerDirty = dirty;
        RaiseChanged();
    }

    public void Dispose()
    {
        _device.OnChanged -= OnDeviceChanged;
        Fields.Changed -= RaiseChanged;
    }

    private void OnDeviceChanged()
    {
        if (IsOn && !EditorDeviceRule.CanEdit(_device))
        {
            Leave();
            return;
        }

        RaiseChanged();
    }

    private void RaiseChanged() => Changed?.Invoke();
}
