using MediaEngine.Contracts.Ai;
using MediaEngine.Contracts.Approvals;
using MediaEngine.Web.Components.Settings;
using MediaEngine.Web.Services.Integration;
using MediaEngine.Web.Services.Ui;

namespace MediaEngine.Web.Services.Settings;

/// <summary>
/// The Dashboard half of the approval rule (Developer tools spec D-C). When the Engine answers an action with an
/// approval-required 409, this shows one dialog per preview, in order, in the Engine's own words. If every one is
/// confirmed the caller repeats the action carrying the hashes; if any is cancelled the caller stops and leaves everything
/// as it was.
/// </summary>
public sealed class ApprovalFlow(IAppDialogService dialogs)
{
    private static readonly AppDialogOptions DialogOptions = new()
    {
        MaxWidth = AppMaxWidth.Small,
        FullWidth = true,
        CloseOnEscapeKey = true,
        BackdropClick = true,
        Sheet = AppDialogSheetMode.Phone,
    };

    /// <summary>The words on the dialog's heading for one preview.</summary>
    public static string Heading(ApprovalPreviewResponse preview)
    {
        ArgumentNullException.ThrowIfNull(preview);
        return preview.Kind switch
        {
            ApprovalKinds.ApplicationCredential => "Issue this key?",
            ApprovalKinds.Webhook => "Save this webhook?",
            ApprovalKinds.Application => "Change what these keys can do?",
            _ => $"Turn on {preview.Title}?",
        };
    }

    /// <summary>
    /// Shows each preview in order. Returns the confirmed statement hash for each item (keyed by the preview's item id), or
    /// null as soon as the person cancels any one of them.
    /// </summary>
    /// <param name="heading">Words for the dialog's heading when the usual one for the item does not fit (for example "Install X?").</param>
    /// <param name="confirmLabel">Words for the confirming button when the usual ones for the item do not fit (for example "Install").</param>
    public async Task<IReadOnlyDictionary<string, string>?> ConfirmAsync(
        IReadOnlyList<ApprovalPreviewResponse> previews,
        string? heading = null,
        string? confirmLabel = null)
    {
        ArgumentNullException.ThrowIfNull(previews);
        var hashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var preview in previews)
        {
            var parameters = new AppDialogParameters<ApprovalDialog>();
            parameters.Add(dialog => dialog.Preview, preview);
            if (!string.IsNullOrWhiteSpace(confirmLabel))
            {
                parameters.Add(dialog => dialog.ConfirmText, confirmLabel);
            }

            var reference = await dialogs.ShowAsync<ApprovalDialog>(heading ?? Heading(preview), parameters, DialogOptions);
            var result = await reference.Result;
            if (result is not { Canceled: false, Data: true })
            {
                return null;
            }

            hashes[preview.ItemId] = preview.StatementHash;
        }

        return hashes;
    }
}
