using MediaEngine.Web.Components.Collections;
using MediaEngine.Web.Components.Shared;
using MediaEngine.Web.Services.Ui;

namespace MediaEngine.Web.Services.Editing;

public sealed class GalleryEditorLauncherService(IAppDialogService dialogService)
{
    public async Task<bool> OpenAsync(GalleryEditorLaunchRequest request)
    {
        var dialog = await dialogService.ShowAsync<GalleryEditorShell>(
            request.EditingGallery is null ? "New Gallery" : "Edit Gallery",
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
            return false;

        var result = await dialog.Result;
        return result is not null && !result.Canceled;
    }
}
