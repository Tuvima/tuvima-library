using MediaEngine.Web.Components.Shared;
using MediaEngine.Web.Services.Editing;
using MediaEngine.Web.Services.Theming;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace MediaEngine.Web.Components.Collections;

// The phone arrangement (universal editor v6 section 4a, brought to the collection and playlist builder): a full-screen sheet with a one-line header, the tabs in
// one scrolling row, Delete collection in the header's "..." menu and a footer fixed above the on-screen keyboard. It is an arrangement only: what the builder
// does, when it asks before leaving and who may open it are unchanged.
public partial class CollectionEditorShell
{
    /// <summary>Draws the builder in the phone arrangement (true) or the desktop one (false) whatever the device says; left unset, the device's form factor decides.</summary>
    [Parameter] public bool? PhoneArrangement { get; set; }

    private DeviceContextService? _device;
    private EditorKeyboardFit? _keyboard;

    // A phone or a tablet form factor (under 1024 wide), the same rule as the editor window; the browser reports it before the first draw and again when it changes.
    // The sheet is a dialog, so the editor drawn in place on a page keeps its own arrangement.
    private bool Phone => !Inline && (PhoneArrangement ?? EditorPhoneArrangement.Applies(_device));

    private string WorkspaceClass =>
        "sme-shell collection-editor-workspace"
        + (IsCreateFlow ? " collection-editor-workspace--flow" : string.Empty)
        // The editor's phone class, so the shared keyboard script and safe-area rules find this sheet the same way they find the editor window.
        + (Phone ? " collection-editor-workspace--phone tl-editor-window--phone" : string.Empty);

    private int ActiveTabIndex => Math.Max(0, Array.FindIndex(VisibleTabs, tab => IsTabSelected(tab.Id)));

    // Delete collection leaves the rail for the header's "..." on a phone (v6 D22): the same condition, the same handler, the same question.
    private IReadOnlyList<AppRowMenuItem>? PhoneMenuItems =>
        Phone && EditingCollection is not null && CanDeleteCollection
            ?
            [
                new AppRowMenuItem
                {
                    Label = L["CollectionEditorShell_DeleteCollection"].Value,
                    Icon = AppMaterialIcons.Outlined.Delete,
                    Destructive = true,
                    Disabled = _saving,
                    OnClick = EventCallback.Factory.Create(this, DeleteCollectionAsync),
                },
            ]
            : null;

    private void ObserveDevice()
    {
        _device = ServiceProvider.GetService<DeviceContextService>();
        if (_device is not null)
        {
            _device.OnChanged += OnDeviceChanged;
        }
    }

    private void OnDeviceChanged() => _ = InvokeAsync(StateHasChanged);

    // The dialog is a full-screen sheet on a phone and the framed window elsewhere, and follows the device when it crosses a form-factor width (a tablet turned,
    // a window resized). The keyboard fit runs only while the phone arrangement is drawn.
    private async Task SyncPhoneAsync()
    {
        if (DialogContext is { } context && (context.Options.FullScreen == true) != Phone)
        {
            await context.SetOptionsAsync(context.Options with { FullScreen = Phone ? true : null });
        }

        _keyboard ??= new EditorKeyboardFit(JS, Logger);
        await _keyboard.SyncAsync(Phone);
    }

    public async ValueTask DisposeAsync()
    {
        if (_device is not null)
        {
            _device.OnChanged -= OnDeviceChanged;
        }

        _dialogCloseGuard?.Dispose();
        if (_keyboard is not null)
        {
            await _keyboard.DisposeAsync();
        }
    }
}
