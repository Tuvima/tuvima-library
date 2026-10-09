using MediaEngine.Web.Components.Settings;
using MediaEngine.Web.Components.Shared;
using MediaEngine.Web.Services.Ui;

namespace MediaEngine.Web.Services.Integration;

/// <summary>Opens the "Confirm it's you" dialog; the dialog itself talks to the Engine and closes once it is confirmed.</summary>
public sealed class DialogItsYouConfirmer(IAppDialogService dialogs) : IItsYouConfirmer
{
    public async Task<bool> ConfirmAsync(CancellationToken ct = default)
    {
        var dialog = await dialogs.ShowAsync<ConfirmItsYouDialog>("Confirm it's you",
            new AppDialogOptions { MaxWidth = AppMaxWidth.Small, FullWidth = true, CloseButton = true, CloseOnEscapeKey = true }).ConfigureAwait(false);
        using var cancellation = ct.Register(() => dialog.Close());
        var result = await dialog.Result.ConfigureAwait(false);
        return result is { Canceled: false };
    }
}
