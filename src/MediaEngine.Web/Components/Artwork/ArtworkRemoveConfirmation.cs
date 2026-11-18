using MediaEngine.Web.Components.Shared;
using MediaEngine.Web.Services.Ui;
using Microsoft.AspNetCore.Components;

namespace MediaEngine.Web.Components.Artwork;

/// <summary>
/// The two artwork questions the editor asks before something is lost (details overlay spec D7 and D17): Remove artwork and
/// Restore automatic artwork. Both use the shared confirmation dialog, in the Artwork tool and in the Needs Review
/// modal alike. A pick, an upload or a URL applies at once; only these two ask first.
/// </summary>
public static class ArtworkRemoveConfirmation
{
    public const string RemoveLabel = "Remove";

    public const string KeepLabel = "Keep";

    public const string RestoreTitle = "Restore automatic artwork?";

    public const string RestoreMessage =
        "This removes the custom covers added to this shelf. The shelf then shows artwork built from the titles you own in it, in series order.";

    public const string RestoreLabel = "Restore automatic artwork";

    public const string KeepCustomLabel = "Keep custom covers";

    /// <summary>"Remove this cover?" for a slot called "Cover"; the slot's own name keeps the question about the right image.</summary>
    public static string RemoveTitle(string? slotLabel) => $"Remove this {Describe(slotLabel)}?";

    /// <summary>Asks whether to remove one artwork image, showing it. True only when the person chose Remove.</summary>
    public static async Task<bool> AskRemoveAsync(IAppDialogService dialogs, string? slotLabel, string? imageUrl, string message) =>
        await AskAsync(dialogs, RemoveTitle(slotLabel), message, RemoveLabel, KeepLabel, true, slotLabel, imageUrl) == AppConfirmResult.Primary;

    public const string DeleteLabel = "Delete";

    public const string HideLabel = "Hide";

    /// <summary>"Delete this poster?" (editor Artwork tab, v8 D4): for a picture Library downloaded or the person uploaded.</summary>
    public static string DeleteTitle(string? slotLabel) => $"Delete this {Describe(slotLabel)}?";

    /// <summary>"Hide this poster?" for a picture that lives in the person's own folder, which Library never deletes.</summary>
    public static string HideTitle(string? slotLabel) => $"Hide this {Describe(slotLabel)}?";

    /// <summary>Asks once before a downloaded or uploaded picture is deleted, showing it. True only when the person chose Delete.</summary>
    public static async Task<bool> AskDeleteAsync(IAppDialogService dialogs, string? slotLabel, string? imageUrl) =>
        await AskAsync(
            dialogs, DeleteTitle(slotLabel),
            "Library downloaded this picture or you uploaded it. It is deleted from this item now, without waiting for Save changes. The other pictures stay.",
            DeleteLabel, KeepLabel, true, slotLabel, imageUrl) == AppConfirmResult.Primary;

    /// <summary>Asks once before a picture from the person's own folder is hidden, showing it. True only when the person chose Hide.</summary>
    public static async Task<bool> AskHideAsync(IAppDialogService dialogs, string? slotLabel, string? imageUrl) =>
        await AskAsync(
            dialogs, HideTitle(slotLabel),
            "This picture is in your own folder, so Library never deletes its file. It is hidden from this item now, without waiting for Save changes. The other pictures stay.",
            HideLabel, KeepLabel, false, slotLabel, imageUrl) == AppConfirmResult.Primary;

    private static Task<AppConfirmResult> AskAsync(
        IAppDialogService dialogs, string title, string message, string primaryLabel, string secondaryLabel, bool destructive, string? slotLabel, string? imageUrl)
    {
        var label = Describe(slotLabel);
        RenderFragment? preview = string.IsNullOrWhiteSpace(imageUrl)
            ? null
            : builder =>
            {
                builder.OpenComponent<ArtworkRemovePreview>(0);
                builder.AddAttribute(1, nameof(ArtworkRemovePreview.ImageUrl), imageUrl);
                builder.AddAttribute(2, nameof(ArtworkRemovePreview.Label), label);
                builder.CloseComponent();
            };

        return AppConfirmDialog.ShowAsync(dialogs, title, message, primaryLabel, secondaryLabel, primaryDestructive: destructive, preview: preview);
    }

    /// <summary>Asks whether to drop a shelf's custom covers and go back to automatic artwork. True only when the person agreed.</summary>
    public static async Task<bool> AskRestoreAutomaticAsync(IAppDialogService dialogs) =>
        await AppConfirmDialog.ShowAsync(
            dialogs, RestoreTitle, RestoreMessage, RestoreLabel, KeepCustomLabel, primaryDestructive: true) == AppConfirmResult.Primary;

    private static string Describe(string? slotLabel) =>
        string.IsNullOrWhiteSpace(slotLabel) ? "artwork" : slotLabel.Trim().ToLowerInvariant();
}
