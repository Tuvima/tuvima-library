using MediaEngine.Contracts.Universe;
using MediaEngine.Web.Components.MediaEditor;
using MediaEngine.Web.Components.Shared;
using MediaEngine.Web.Services.Integration;
using MediaEngine.Web.Services.Theming;
using MediaEngine.Web.Services.Ui;

namespace MediaEngine.Web.Services.Editing;

public sealed class MediaEditorLauncherService
{
    private readonly IAppDialogService _dialogService;
    private readonly AdministratorSurfaceAccessService? _administratorAccess;
    private readonly DeviceContextService? _device;
    private readonly IAppToastService? _toasts;

    public MediaEditorLauncherService(
        IAppDialogService dialogService,
        AdministratorSurfaceAccessService? administratorAccess = null,
        DeviceContextService? device = null,
        IAppToastService? toasts = null)
    {
        _dialogService = dialogService;
        _administratorAccess = administratorAccess;
        _device = device;
        _toasts = toasts;
    }

    /// <summary>Opens the editor; true only when the user applied a change.</summary>
    public async Task<bool> OpenAsync(MediaEditorLaunchRequest request) =>
        await TryOpenAsync(request) == EditorLaunchResult.Applied;

    public async Task<EditorLaunchResult> TryOpenAsync(MediaEditorLaunchRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        // Editing is desktop-only (fine, hover-capable pointer): the editor shell never opens anywhere else.
        if (EditorDeviceRule.TryRefuse(_device, _toasts))
        {
            return EditorLaunchResult.NotAvailableOnThisDevice;
        }

        var sharedTarget = request.SharedEntityTarget;
        if (sharedTarget is null && request.EntityIds.Count == 0)
        {
            return EditorLaunchResult.NotAvailable;
        }

        if (sharedTarget is not null
            && (request.Mode != SharedMediaEditorMode.Normal || !IsValidSharedTarget(sharedTarget)))
        {
            return EditorLaunchResult.NotAvailable;
        }

        if (_administratorAccess is not null && !await _administratorAccess.EnsureUnlockedAsync())
        {
            return EditorLaunchResult.NotAvailable;
        }

        // This editor takes exactly one item. Several items are edited together from the selection bar (the "Edit N items" panel),
        // so a launch with several items must never silently choose a representative or fan a change out across several works.
        if (request.Mode == SharedMediaEditorMode.Batch || request.EntityIds.Count > 1)
        {
            return EditorLaunchResult.NotAvailable;
        }

        if (string.Equals(request.LaunchEntityKind, "Person", StringComparison.OrdinalIgnoreCase))
        {
            var personDialog = await _dialogService.ShowAsync<PersonEditorDialog>(
                "Edit Person",
                new AppDialogParameters { { nameof(PersonEditorDialog.Request), request } },
                new AppDialogOptions
                {
                    CloseButton = false,
                    NoHeader = true,
                    MaxWidth = AppMaxWidth.ExtraLarge,
                    FullWidth = true,
                    BackdropClick = false,
                    CloseOnEscapeKey = true,
                });
            if (personDialog is null)
            {
                return EditorLaunchResult.NotAvailable;
            }

            var personResult = await personDialog.Result;
            return personResult is not null && !personResult.Canceled ? EditorLaunchResult.Applied : EditorLaunchResult.Dismissed;
        }

        var dialog = await _dialogService.ShowAsync<SharedMediaEditorShell>(
            "Edit Item",
            new AppDialogParameters
            {
                { nameof(SharedMediaEditorShell.Request), request },
            },
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

    private static bool IsValidSharedTarget(SharedEntityEditorTargetDto target)
    {
        if (string.IsNullOrWhiteSpace(target.UniverseQid))
        {
            return false;
        }

        if (string.Equals(target.Kind, SharedEntityEditorTargetKinds.Universe, StringComparison.OrdinalIgnoreCase))
        {
            return !target.FictionalEntityId.HasValue
                && string.Equals(target.Qid, target.UniverseQid, StringComparison.OrdinalIgnoreCase);
        }

        return string.Equals(target.Kind, SharedEntityEditorTargetKinds.FictionalEntity, StringComparison.OrdinalIgnoreCase)
            && target.FictionalEntityId.HasValue
            && !string.IsNullOrWhiteSpace(target.Qid);
    }
}
