using MediaEngine.Web.Components.Collections;
using MediaEngine.Web.Components.Shared;
using MediaEngine.Web.Services.Theming;
using MediaEngine.Web.Services.Ui;

namespace MediaEngine.Web.Services.Editing;

public sealed class GalleryEditorLauncherService(
    IAppDialogService dialogService,
    DeviceContextService? device = null,
    IAppToastService? toasts = null)
{
    /// <summary>Opens the editor; true only when the user applied a change.</summary>
    public async Task<bool> OpenAsync(GalleryEditorLaunchRequest request) =>
        await TryOpenAsync(request) == EditorLaunchResult.Applied;

    public async Task<EditorLaunchResult> TryOpenAsync(GalleryEditorLaunchRequest request)
    {
        // Editing is desktop-only (fine, hover-capable pointer): the editor shell never opens anywhere else.
        if (EditorDeviceRule.TryRefuse(device, toasts))
        {
            return EditorLaunchResult.NotAvailableOnThisDevice;
        }

        var dialog = await dialogService.ShowAsync<GalleryEditorShell>(
            request.EditingGallery is null && request.EditingVaultGallery is null ? "New Gallery" : "Edit Gallery",
            new AppDialogParameters { { nameof(GalleryEditorShell.Request), request } },
            new AppDialogOptions
            {
                CloseButton = false,
                NoHeader = true,
                MaxWidth = AppMaxWidth.ExtraLarge,
                FullWidth = true,
                BackdropClick = false,
                CloseOnEscapeKey = true,
            });

        if (dialog is null)
        {
            return EditorLaunchResult.NotAvailable;
        }

        var result = await dialog.Result;
        return result is not null && !result.Canceled ? EditorLaunchResult.Applied : EditorLaunchResult.Dismissed;
    }
}
