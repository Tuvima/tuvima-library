using MediaEngine.Web.Components.Shared;
using MediaEngine.Web.Services.Ui;
using Microsoft.AspNetCore.Components;

namespace MediaEngine.Web.Components.Artwork;

/// <summary>
/// The two artwork questions Editing Mode asks before something is lost (details overlay spec D7 and D17): Remove artwork and
/// Restore automatic artwork. Both use the shared confirmation dialog, in the Editing Mode section and in the Needs Review
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
    public static async Task<bool> AskRemoveAsync(IAppDialogService dialogs, string? slotLabel, string? imageUrl, string message)
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

        var answer = await AppConfirmDialog.ShowAsync(
            dialogs, RemoveTitle(slotLabel), message, RemoveLabel, KeepLabel, primaryDestructive: true, preview: preview);
        return answer == AppConfirmResult.Primary;
    }

    /// <summary>Asks whether to drop a shelf's custom covers and go back to automatic artwork. True only when the person agreed.</summary>
    public static async Task<bool> AskRestoreAutomaticAsync(IAppDialogService dialogs) =>
        await AppConfirmDialog.ShowAsync(
            dialogs, RestoreTitle, RestoreMessage, RestoreLabel, KeepCustomLabel, primaryDestructive: true) == AppConfirmResult.Primary;

    private static string Describe(string? slotLabel) =>
        string.IsNullOrWhiteSpace(slotLabel) ? "artwork" : slotLabel.Trim().ToLowerInvariant();
}
