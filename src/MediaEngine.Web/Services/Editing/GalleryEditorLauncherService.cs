using MediaEngine.Web.Services.Theming;
using MediaEngine.Web.Services.Ui;

namespace MediaEngine.Web.Services.Editing;

/// <summary>
/// Opens a gallery in the one View editor window (spec photos-documents-editor D4), the same window photos and documents use. The old modal gallery
/// editor shell is gone; this keeps the one call pages already make, so New gallery and Edit gallery work from every page that offered them.
/// </summary>
public sealed class GalleryEditorLauncherService(
    ViewEditorHost host,
    DeviceContextService? device = null,
    IAppToastService? toasts = null)
{
    /// <summary>Opens the editor; true only when the user applied a change.</summary>
    public async Task<bool> OpenAsync(GalleryEditorLaunchRequest request) =>
        await TryOpenAsync(request) == EditorLaunchResult.Applied;

    public async Task<EditorLaunchResult> TryOpenAsync(GalleryEditorLaunchRequest request)
    {
        // Editing is desktop-only (fine, hover-capable pointer): the editor never opens anywhere else.
        if (EditorDeviceRule.TryRefuse(device, toasts))
        {
            return EditorLaunchResult.NotAvailableOnThisDevice;
        }

        var opened = await host.OpenGalleryAsync(
            request.EditingGallery,
            request.EditingVaultGallery,
            newSmart: string.Equals(request.InitialMembershipMode, "Smart", StringComparison.OrdinalIgnoreCase),
            newVault: request.Vault && request.EditingVaultGallery is null);
        if (!opened)
        {
            return EditorLaunchResult.NotAvailable;
        }

        var closed = await host.WhenClosedAsync();
        return closed.Changed ? EditorLaunchResult.Applied : EditorLaunchResult.Dismissed;
    }
}
