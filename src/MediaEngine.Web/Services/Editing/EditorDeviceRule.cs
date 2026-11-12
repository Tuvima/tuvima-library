using MediaEngine.Web.Services.Integration;
using MediaEngine.Web.Services.Theming;

namespace MediaEngine.Web.Services.Editing;

/// <summary>
/// The device rules for editing. There are two, and every edit handler calls one of them first so hiding a button is
/// never the only protection. The Dashboard server and the browser snapshot feed the same inputs.
/// <list type="bullet">
/// <item><description><b>Item editing</b> (<see cref="CanEditItems(DeviceContextService?)"/>, <see cref="TryRefuseItems"/>): the editor, everything that opens it,
/// Delete and collection membership. Allowed on a desktop, tablet or phone, whatever the pointer; a TV, an unknown form factor and a missing
/// device context mean "no". The Engine's permission check still decides who may edit.</description></item>
/// <item><description><b>Desktop-only tools</b> (<see cref="CanEdit(DeviceContextService?)"/>, <see cref="TryRefuse"/>, <see cref="Message"/>): Settings configuration screens
/// and the Vault tools only. Allowed only on a desktop form factor whose primary pointer is fine and can hover; a phone, tablet, TV, native app
/// shell, unknown form factor and a browser that cannot report its pointer all mean "no".</description></item>
/// </list>
/// </summary>
public static class EditorDeviceRule
{
    /// <summary>The line for the desktop-only tools (Settings, the Vault tools).</summary>
    public const string Message = "Edit on a computer";

    /// <summary>The line for the item rule: only a TV or an unknown device is refused, and a phone or tablet works, so it never names a computer.</summary>
    public const string ItemMessage = "Editing is not available on this device";

    /// <summary>The line when the device may edit but the signed-in profile may not (see <see cref="DashboardSessionAccessor.CanManageMetadata"/>).</summary>
    public const string ProfileMessage = "Editing is not available for this profile";

    /// <summary>The pure item-editing rule: desktop, tablet and phone may edit; TV, an unknown or missing form factor and a television device class may not.</summary>
    public static bool CanEditItems(string? deviceClass, string? formFactor) =>
        !string.Equals(deviceClass, "television", StringComparison.OrdinalIgnoreCase)
        && (string.Equals(formFactor, "desktop", StringComparison.OrdinalIgnoreCase)
            || string.Equals(formFactor, "tablet", StringComparison.OrdinalIgnoreCase)
            || string.Equals(formFactor, "phone", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// True only when the current circuit's device may edit items. A missing device context never edits, and neither does one the browser has not
    /// reported yet (its form factor is still a guess).
    /// </summary>
    public static bool CanEditItems(DeviceContextService? device) =>
        device is { IsInitialised: true } && CanEditItems(device.DeviceClass, device.FormFactor);

    /// <summary>
    /// Whether a list offers Edit (for several items, or without a per-item Engine answer): the device may edit
    /// (<see cref="CanEditItems(DeviceContextService?)"/>) and the Engine says this profile may edit item details, or could once the
    /// administrator PIN is entered (<see cref="DashboardSessionAccessor.CanOfferMetadataEdit"/>). Unknown means no. This only decides
    /// what is shown: the handler behind the button calls <see cref="EnsureMayEditItemsAsync"/>, and the Engine still checks every save.
    /// </summary>
    public static bool CanEditItems(DeviceContextService? device, DashboardSessionAccessor? session) =>
        CanEditItems(device) && session?.CanOfferMetadataEdit == true;

    /// <summary>
    /// Says why this device or profile is never offered item editing, or null when it is (the device line wins when both apply). A locked
    /// administrator is offered it; <see cref="EnsureMayEditItemsAsync"/> asks for the PIN before anything opens.
    /// </summary>
    public static string? ItemRefusal(DeviceContextService? device, DashboardSessionAccessor? session)
    {
        if (TryRefuseItems(device))
        {
            return ItemMessage;
        }

        return session?.CanOfferMetadataEdit == true ? null : ProfileMessage;
    }

    /// <summary>
    /// The check every edit handler runs before it opens anything, so a hidden button is never the only protection. The device must be
    /// allowed and the Engine must say this profile may edit item details now. An administrator whose settings are locked is asked for the
    /// PIN first (the same prompt as the Universe pages and the collection builder) and passes only when the Engine then says yes;
    /// closing the prompt stops quietly (<see cref="ItemEditGate.Message"/> is null). Anyone else is refused with a line.
    /// </summary>
    public static async Task<ItemEditGate> EnsureMayEditItemsAsync(
        DeviceContextService? device,
        DashboardSessionAccessor? session,
        IAdministratorSurfaceAccessService? administratorAccess,
        CancellationToken ct = default)
    {
        if (TryRefuseItems(device))
        {
            return new(false, ItemMessage);
        }

        if (session is null)
        {
            return new(false, ProfileMessage);
        }

        if (session.CanManageMetadata)
        {
            return new(true, null);
        }

        if (!session.CanOfferMetadataEdit || administratorAccess is null)
        {
            return new(false, ProfileMessage);
        }

        return await administratorAccess.EnsureUnlockedAsync(ct) && session.CanManageMetadata
            ? new(true, null)
            : new(false, null);
    }

    /// <summary>
    /// For an item the Engine already answered for (its page or a list row offers Edit): an administrator whose settings are locked is
    /// asked for the PIN before the editor opens, and passes only when the Engine then says the profile may edit. Everyone else passes
    /// straight through, because the Engine's per-item answer already decided. False means stop quietly (the prompt was closed).
    /// </summary>
    public static async Task<bool> EnsureUnlockedForItemAsync(
        DashboardSessionAccessor? session,
        IAdministratorSurfaceAccessService? administratorAccess,
        CancellationToken ct = default)
    {
        if (session is null || session.CanManageMetadata || !session.CanOfferMetadataEdit)
        {
            return true;
        }

        return administratorAccess is not null && await administratorAccess.EnsureUnlockedAsync(ct) && session.CanManageMetadata;
    }

    /// <summary>
    /// Returns true when the current device may not edit items. Launchers and edit handlers call this before any other work so no editor
    /// surface ever opens; a handler that has room for a line shows <see cref="ItemMessage"/> in place (an alert), nothing is drawn over the page.
    /// </summary>
    public static bool TryRefuseItems(DeviceContextService? device) => !CanEditItems(device);

    /// <summary>The pure desktop-tools rule over the four signals the browser reports.</summary>
    public static bool CanEdit(string? deviceClass, string? formFactor, bool finePointer, bool canHover) =>
        string.Equals(deviceClass, "web", StringComparison.OrdinalIgnoreCase)
        && string.Equals(formFactor, "desktop", StringComparison.OrdinalIgnoreCase)
        && finePointer
        && canHover;

    /// <summary>True only when the current circuit's device may use a desktop-only tool. A missing device context never does.</summary>
    public static bool CanEdit(DeviceContextService? device) =>
        device is not null && CanEdit(device.DeviceClass, device.FormFactor, device.HasFinePointer, device.CanHover);

    /// <summary>
    /// Returns true when the current device may not use a desktop-only tool (Settings configuration, Vault tools). Those handlers call this first and
    /// show <see cref="Message"/> in place.
    /// </summary>
    public static bool TryRefuse(DeviceContextService? device) => !CanEdit(device);
}

/// <summary>The answer of <see cref="EditorDeviceRule.EnsureMayEditItemsAsync"/>: whether editing may open, and the line to show when it may not (null when the PIN prompt was simply closed).</summary>
public readonly record struct ItemEditGate(bool Allowed, string? Message);
