using MediaEngine.Web.Services.Navigation;
using MediaEngine.Web.Services.Theming;

namespace MediaEngine.Web.Services.Editing;

/// <summary>
/// The sections the Editing Mode banner can open below the hero, one at a time. Details is only used by the Universe
/// editor (one workspace with its own categories); an item's own fields are edited in place with pencils.
/// </summary>
public enum EditingSection
{
    Details,
    Artwork,
    Match,
    History,
}

/// <summary>The person's answer to "Save or discard?". Closing the dialog (Esc or the close button) is <see cref="Cancel"/>.</summary>
public enum SaveOrDiscardChoice
{
    Save,
    Discard,
    Cancel,
}

/// <summary>
/// What the open section (the editor hosted below the hero) lets the page do with unsaved input. The section slot
/// implements it so the page can ask "Save or discard?" without knowing anything about the editor inside.
/// </summary>
public interface IEditingSectionHost
{
    /// <summary>Saves what the section holds. Returns true when nothing unsaved is left (a failed save returns false and keeps the input).</summary>
    Task<bool> SaveAsync();

    /// <summary>Drops what the section holds.</summary>
    Task DiscardAsync();
}

/// <summary>
/// The state of Editing Mode on one detail page: whether it is on, which section is open below the hero (and which
/// artwork slot it was opened for), and whether anything typed is still unsaved. It lives on the page and ends when the
/// page is left or the user chooses Done; a deep link never starts in Editing Mode.
/// </summary>
/// <remarks>
/// <para>
/// Every way in (turning the mode on, opening a section, opening a field) re-checks <see cref="EditorDeviceRule"/>,
/// so a hidden button is never the only protection, and a device that stops qualifying (for example a window resized
/// to tablet width) leaves the mode on its own.
/// </para>
/// <para>
/// Leaving is where unsaved input is asked about (details overlay spec D9): Done, Esc and Back all go through
/// <see cref="DoneAsync"/> and its "Save or discard?" question, which the page supplies as <see cref="AskSaveOrDiscard"/>.
/// With no question wired, unsaved input is never dropped silently: the answer is to stay.
/// </para>
/// </remarks>
public sealed class EditingModeContext : IDetailsOverlayDismissGuard, IDisposable
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

    /// <summary>Permission and a device that is allowed to edit. The banner renders only while the mode is on and this is true.</summary>
    public bool CanEdit => HasPermission && EditorDeviceRule.CanEdit(_device);

    public bool IsOn { get; private set; }

    public bool SectionOpen => OpenSectionKind is not null;

    /// <summary>The section open below the hero, or null when none is.</summary>
    public EditingSection? OpenSectionKind { get; private set; }

    /// <summary>The artwork asset type the section was opened for (CoverArt, Background, Logo), if any.</summary>
    public string? OpenSectionArtworkSlot { get; private set; }

    /// <summary>Changes on every section request so reopening the same section and slot still re-applies it.</summary>
    public int SectionRequestVersion { get; private set; }

    /// <summary>Set by the section slot while the editor inside it has unsaved input.</summary>
    public bool SectionDirty { get; private set; }

    /// <summary>A field typed into on the page that has not been saved.</summary>
    public bool HasUnsavedField => Fields.IsDirty;

    public bool HasUnsavedInput => Fields.IsDirty || SectionDirty;

    /// <summary>The open section's handle for saving and discarding, set by the section slot while it is showing.</summary>
    public IEditingSectionHost? SectionHost { get; set; }

    /// <summary>How the page asks "Save or discard?". Null means nothing can ask, so unsaved input keeps the person where they are.</summary>
    public Func<Task<SaveOrDiscardChoice>>? AskSaveOrDiscard { get; set; }

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

    /// <summary>
    /// Ends Editing Mode. The caller has already asked about unsaved input; whatever is still open is dropped.
    /// </summary>
    public void Leave()
    {
        IsOn = false;
        OpenSectionKind = null;
        OpenSectionArtworkSlot = null;
        SectionDirty = false;
        Fields.Discard();
        RaiseChanged();
    }

    /// <summary>
    /// True while Done, Esc, Back or a section close is working through "Save or discard?" and its save. A second one of those
    /// arriving meanwhile is refused (and Done is disabled), so nothing asks twice and a Discard can never run in the middle of a save.
    /// </summary>
    public bool IsSettling { get; private set; }

    /// <summary>
    /// Done: ends Editing Mode. With unsaved input (a field, or the open section) it asks "Save or discard?" first. Save
    /// saves and then leaves (a failed save stays in the mode with the input and its error showing); Discard drops the input
    /// and leaves; Cancel keeps everything as it was. Returns true when the mode ended.
    /// </summary>
    public async Task<bool> DoneAsync()
    {
        if (!IsOn)
        {
            return true;
        }

        if (Fields.Saving)
        {
            return false;
        }

        return await SettleAsync(DoneCoreAsync, whenBusy: false);
    }

    private async Task<bool> DoneCoreAsync()
    {
        if (HasUnsavedInput)
        {
            switch (await AskAsync())
            {
                case SaveOrDiscardChoice.Save:
                    if (!await SaveUnsavedAsync())
                    {
                        return false;
                    }

                    break;
                case SaveOrDiscardChoice.Discard:
                    break;
                default:
                    return false;
            }
        }

        Leave();
        return true;
    }

    /// <summary>The overlay is closing (close, Esc, backdrop or Back): leaving Editing Mode first goes through <see cref="DoneAsync"/>.</summary>
    public async ValueTask<bool> CanDismissAsync() => await DoneAsync();

    /// <summary>
    /// Esc (and a backdrop click or the sheet's grip) from outside a field, in order: an open field closes, asking
    /// "Save or discard?" first when it holds typed input (Esc pressed inside the focused field is that field's own and cancels
    /// it); otherwise an open section closes, asking first when it holds unsaved input. Returns true when the press was used,
    /// false when nothing was open (the overlay then dismisses, which leaves the mode through <see cref="DoneAsync"/>).
    /// </summary>
    public async ValueTask<bool> HandleEscapeAsync()
    {
        if (!IsOn)
        {
            return false;
        }

        if (!Fields.HasOpenDraft && !SectionOpen)
        {
            return false;
        }

        if (IsSettling || Fields.Saving)
        {
            // Something is already being settled or saved: the press is spent, and the overlay must not close under it.
            return true;
        }

        await SettleAsync(HandleEscapeCoreAsync, whenBusy: true);
        return true;
    }

    private async Task<bool> HandleEscapeCoreAsync()
    {
        if (Fields.HasOpenDraft)
        {
            if (!Fields.IsDirty)
            {
                Fields.Cancel();
                return true;
            }

            switch (await AskAsync())
            {
                case SaveOrDiscardChoice.Save:
                    // A failed save leaves the field open with its typed value and its error showing.
                    await Fields.SaveAsync();
                    break;
                case SaveOrDiscardChoice.Discard:
                    Fields.Cancel();
                    break;
            }

            return true;
        }

        await CloseSectionCoreAsync();
        return true;
    }

    /// <summary>
    /// Closes the open section. With unsaved input in it, asks "Save or discard?" first (Save saves and closes, a failed
    /// save keeps it open; Discard drops the input; Cancel keeps it open). Returns true when no section is open any more.
    /// </summary>
    public async Task<bool> CloseSectionAsync()
    {
        if (!SectionOpen)
        {
            return true;
        }

        return await SettleAsync(CloseSectionCoreAsync, whenBusy: false);
    }

    private async Task<bool> CloseSectionCoreAsync()
    {
        if (!SectionOpen)
        {
            return true;
        }

        if (SectionDirty)
        {
            switch (await AskAsync())
            {
                case SaveOrDiscardChoice.Save:
                    if (SectionHost is null || !await SectionHost.SaveAsync())
                    {
                        return false;
                    }

                    break;
                case SaveOrDiscardChoice.Discard:
                    if (SectionHost is not null)
                    {
                        await SectionHost.DiscardAsync();
                    }

                    break;
                default:
                    return false;
            }
        }

        CloseSection();
        return true;
    }

    private async Task<T> SettleAsync<T>(Func<Task<T>> work, T whenBusy)
    {
        if (IsSettling)
        {
            return whenBusy;
        }

        IsSettling = true;
        RaiseChanged();
        try
        {
            return await work();
        }
        finally
        {
            IsSettling = false;
            RaiseChanged();
        }
    }

    /// <summary>
    /// A banner button: closes the section when it is the one already open (the button pressed again), otherwise opens it.
    /// Returns whether the section is open afterwards.
    /// </summary>
    public async Task<bool> ToggleSectionAsync(EditingSection section)
    {
        if (OpenSectionKind == section)
        {
            return !await CloseSectionAsync();
        }

        return await ShowSectionAsync(section);
    }

    /// <summary>
    /// A hero art chip, or a banner button swapping sections: opens the section, first settling the unsaved input of a
    /// different open section ("Save or discard?"). Asking again for the open section only re-applies the artwork slot.
    /// Refused while a field is mid-edit. Returns whether the section is open afterwards.
    /// </summary>
    public async Task<bool> ShowSectionAsync(EditingSection section, string? artworkSlot = null)
    {
        if (SectionOpen && OpenSectionKind != section && !await CloseSectionAsync())
        {
            return false;
        }

        return OpenSection(section, artworkSlot);
    }

    /// <summary>Opens a section (and artwork slot). Refused while a field on the page is mid-edit.</summary>
    public bool OpenSection(EditingSection section, string? artworkSlot = null)
    {
        if (!IsOn || !CanEdit || Fields.HasOpenDraft)
        {
            return false;
        }

        OpenSectionKind = section;
        OpenSectionArtworkSlot = section == EditingSection.Artwork ? artworkSlot : null;
        SectionRequestVersion++;
        RaiseChanged();
        return true;
    }

    /// <summary>Closes the section. The caller has already settled its unsaved input.</summary>
    public void CloseSection()
    {
        if (OpenSectionKind is null)
        {
            return;
        }

        OpenSectionKind = null;
        OpenSectionArtworkSlot = null;
        SectionDirty = false;
        RaiseChanged();
    }

    /// <summary>Reports whether the editor inside the open section has unsaved input.</summary>
    public void SetSectionDirty(bool dirty)
    {
        if (SectionDirty == dirty)
        {
            return;
        }

        SectionDirty = dirty;
        RaiseChanged();
    }

    public void Dispose()
    {
        _device.OnChanged -= OnDeviceChanged;
        Fields.Changed -= RaiseChanged;
    }

    private async Task<SaveOrDiscardChoice> AskAsync() =>
        AskSaveOrDiscard is { } ask ? await ask() : SaveOrDiscardChoice.Cancel;

    // Saves the field first, then the section. Either one failing stops the leave: the input and its error stay on screen.
    private async Task<bool> SaveUnsavedAsync()
    {
        if (Fields.IsDirty && !await Fields.SaveAsync())
        {
            return false;
        }

        if (SectionDirty && (SectionHost is null || !await SectionHost.SaveAsync()))
        {
            return false;
        }

        return true;
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
