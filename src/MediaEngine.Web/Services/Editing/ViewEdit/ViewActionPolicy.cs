using MediaEngine.Domain.PersonalMedia;

namespace MediaEngine.Web.Services.Editing;

/// <summary>Whose photo it is, as far as what the person may do with it goes (spec view-section section 4).</summary>
public enum ViewItemOwnership
{
    /// <summary>The person's own photo (Mine, or one they added to the Shared Library).</summary>
    Own,

    /// <summary>A Shared Library photo someone else added: Favorite, add to one's own Gallery, Download only.</summary>
    SharedByOther,

    /// <summary>Another person's photo, seen read-only: Download only, and only when allowed.</summary>
    OtherPerson,
}

/// <summary>The actions one item can offer, on every surface at once: selection bar, tile menu and viewer.</summary>
[Flags]
public enum ViewActions
{
    None = 0,
    Favorite = 1,
    AddToGallery = 2,
    Download = 4,
    Edit = 8,
    Archive = 16,
    Restore = 32,
    Hide = 64,
    Trash = 128,
}

/// <summary>What the policy needs to know about one item and the screen it is on.</summary>
public readonly record struct ViewActionContext(
    ViewItemOwnership Ownership,
    bool CanEdit,
    bool FileMissing,
    bool CanTrash,
    bool IsArchived,
    bool IsTrashed,
    bool IsHidden,
    bool DownloadAllowed = true,
    bool DeviceMayEditItems = true);

/// <summary>
/// The one rule for which actions an item offers (spec view-section D5). The selection bar, the tile menu and the viewer all ask here, so they
/// never differ. Trash and Edit are offered on a phone as on a desktop (Edit wherever the device may edit items, which the caller passes as CanEdit); Trash is offered only where the device may
/// edit items (DeviceMayEditItems, <c>EditorDeviceRule.CanEditItems</c>: never a TV or an unknown device); an item in a read-only folder never goes to Trash; Restore replaces Archive and Trash for an archived or trashed item.
/// </summary>
public static class ViewActionPolicy
{
    public static ViewActions For(ViewActionContext c)
    {
        var actions = ViewActions.None;
        var live = !c.FileMissing;

        // Everyone who can see an item can save it to their own favorites or Galleries, and download it when allowed.
        if (c.Ownership != ViewItemOwnership.OtherPerson && live)
        {
            actions |= ViewActions.Favorite | ViewActions.AddToGallery;
        }

        if (live && c.DownloadAllowed)
        {
            actions |= ViewActions.Download;
        }

        if (c.Ownership != ViewItemOwnership.Own)
        {
            return actions;
        }

        // A trashed photo can only be brought back; an archived one can still be edited, and restored.
        if (c.IsTrashed)
        {
            return actions | ViewActions.Restore;
        }

        if (c.CanEdit && live)
        {
            actions |= ViewActions.Edit;
        }

        if (c.IsArchived)
        {
            return actions | ViewActions.Restore;
        }

        if (live)
        {
            actions |= ViewActions.Archive | (c.IsHidden ? ViewActions.None : ViewActions.Hide);
        }

        if (live && c.CanTrash && c.DeviceMayEditItems)
        {
            actions |= ViewActions.Trash;
        }

        return actions;
    }

    /// <summary>The ownership of an item, given the scope it was reached in (Mine, a person's page or the Shared Library).</summary>
    public static ViewItemOwnership OwnershipOf(ViewScopeKind itemScope, Guid? addedByProfileId, Guid? viewerProfileId) => itemScope switch
    {
        ViewScopeKind.Mine => ViewItemOwnership.Own,
        ViewScopeKind.Shared => addedByProfileId.HasValue && addedByProfileId == viewerProfileId ? ViewItemOwnership.Own : ViewItemOwnership.SharedByOther,
        _ => ViewItemOwnership.OtherPerson,
    };
}
