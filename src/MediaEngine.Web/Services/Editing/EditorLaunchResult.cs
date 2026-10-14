using MediaEngine.Web.Components.Shared;
using MediaEngine.Web.Services.Theming;
using MediaEngine.Web.Services.Ui;

namespace MediaEngine.Web.Services.Editing;

/// <summary>Outcome of asking a launcher to open an editor dialog.</summary>
public enum EditorLaunchResult
{
    /// <summary>The editor opened and the user applied a change.</summary>
    Applied,

    /// <summary>The editor opened and was closed without a change.</summary>
    Dismissed,

    /// <summary>The request was invalid, locked, or the dialog could not open.</summary>
    NotAvailable,

    /// <summary>Phones are consumption devices; the editor never opens there.</summary>
    NotAvailableOnPhone,
}

/// <summary>Shared phone rule for every editor launcher: no editing on phones.</summary>
public static class EditorPhoneRule
{
    public const string Message = "Edit on a computer or tablet";

    /// <summary>
    /// Returns true (and shows the standard toast) when the current device is a phone.
    /// Launchers call this before any other work so the editor shell never opens.
    /// </summary>
    internal static bool TryRefuse(DeviceContextService? device, IAppToastService? toasts)
    {
        if (device is null || !device.IsPhone)
        {
            return false;
        }

        toasts?.Add(Message, AppSeverity.Info);
        return true;
    }
}
