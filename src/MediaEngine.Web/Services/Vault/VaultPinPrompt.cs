using MediaEngine.Web.Components.Vault;
using MediaEngine.Web.Services.Ui;

namespace MediaEngine.Web.Services.Vault;

/// <summary>The plain sentences a PIN or Vault call that did not work is answered with.</summary>
public static class VaultMessages
{
    public const string WrongPin = "That PIN wasn't right. Try again.";
    public const string TooManyAttempts = "Too many tries. Wait a little and try again.";
    public const string Unreachable = "The Library couldn't be reached. Try again.";
    public const string NotAvailable = "That isn't available.";
    public const string ConfirmItsYou = "Confirm it's you to continue.";
    public const string SetProfilePinFirst = "Set your own profile PIN first (Settings > Profiles > your profile > Profile details).";

    /// <summary>The sentence for a call that did not succeed, or null when it did.</summary>
    public static string? For<T>(VaultCall<T> call) => call.Outcome switch
    {
        VaultOutcome.Ok => null,
        VaultOutcome.PinRequired => WrongPin,
        VaultOutcome.TooManyAttempts => TooManyAttempts,
        VaultOutcome.AdministratorPinNotSet => SetProfilePinFirst,
        VaultOutcome.Rejected => string.IsNullOrWhiteSpace(call.Message) ? "That couldn't be saved." : call.Message,
        VaultOutcome.ConfirmItsYou => ConfirmItsYou,
        VaultOutcome.NotAvailable => NotAvailable,
        _ => Unreachable,
    };
}

/// <summary>Opens the PIN pad dialog and answers whether a PIN was accepted.</summary>
public static class VaultPinPrompt
{
    /// <summary>
    /// Shows the number pad. <paramref name="verify"/> checks the PIN with the Engine and returns a plain message when it is not
    /// accepted (or null when it is); the dialog stays open for another try until it is accepted or cancelled.
    /// </summary>
    public static async Task<bool> AskAsync(
        IAppDialogService dialogs, string title, string? hint, string submitLabel, string padLabel, Func<string, Task<string?>> verify)
    {
        var parameters = new AppDialogParameters<VaultPinDialog>
        {
            { component => component.Title, title },
            { component => component.Hint, hint },
            { component => component.SubmitLabel, submitLabel },
            { component => component.PadLabel, padLabel },
            { component => component.Verify, verify },
        };
        var dialog = await dialogs.ShowAsync<VaultPinDialog>(string.Empty, parameters, new AppDialogOptions
        {
            MaxWidth = AppMaxWidth.Small,
            FullWidth = true,
            CloseButton = false,
            NoHeader = true,
            CloseOnEscapeKey = true,
            Sheet = AppDialogSheetMode.PhoneAndTablet,
        });
        return await dialog.Result is { Canceled: false };
    }
}
