using MediaEngine.Contracts.Ai;
using MediaEngine.Contracts.Approvals;
using MediaEngine.Web.Components.Settings;
using MediaEngine.Web.Services.Integration;
using MediaEngine.Web.Services.Ui;

namespace MediaEngine.Web.Services.Settings;

/// <summary>How saving the Local AI configuration ended once any approvals were asked about.</summary>
public enum AiSaveOutcome
{
    Saved,
    Failed,

    /// <summary>The person closed an approval dialog; nothing was saved and the switches are back to their saved values.</summary>
    Cancelled,
}

/// <summary>
/// The Dashboard half of the approval rule (Developer tools spec D-C). When the Engine answers an action with an
/// approval-required 409, this shows one dialog per preview, in order, in the Engine's own words. If every one is
/// confirmed the caller repeats the action carrying the hashes; if any is cancelled the caller stops and leaves everything
/// as it was.
/// </summary>
public sealed class ApprovalFlow(IAppDialogService dialogs)
{
    private const int MaxRounds = 3;

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

    /// <summary>
    /// Saves the Local AI configuration. A feature switched on that the person has not seen yet comes back as an approval
    /// request; each is confirmed and the save repeated with the hashes. When anything is cancelled, nothing is saved and
    /// every switch goes back to its saved value (the saved configuration is read again, so a switch the person changed but
    /// that needed no approval returns too).
    /// </summary>
    public async Task<AiSaveOutcome> SaveAiConfigAsync(UIOrchestratorService orchestrator, AiConfigDto config)
    {
        ArgumentNullException.ThrowIfNull(orchestrator);
        ArgumentNullException.ThrowIfNull(config);
        config.Approvals.Clear();
        try
        {
            for (var round = 0; round < MaxRounds; round++)
            {
                var result = await orchestrator.SaveAiConfigWithApprovalAsync(config);
                if (result.Approval is null)
                {
                    return result.Saved ? AiSaveOutcome.Saved : AiSaveOutcome.Failed;
                }

                var hashes = await ConfirmAsync(result.Approval.Approvals);
                if (hashes is null)
                {
                    await RestoreSavedSwitchesAsync(orchestrator, config, result.Approval.Approvals);
                    return AiSaveOutcome.Cancelled;
                }

                foreach (var (itemId, hash) in hashes)
                {
                    config.Approvals[itemId] = hash;
                }
            }

            return AiSaveOutcome.Failed;
        }
        finally
        {
            config.Approvals.Clear();
        }
    }

    private static async Task RestoreSavedSwitchesAsync(
        UIOrchestratorService orchestrator,
        AiConfigDto config,
        IReadOnlyList<ApprovalPreviewResponse> asked)
    {
        var saved = await orchestrator.GetAiConfigAsync();
        if (saved is not null)
        {
            config.Features.Clear();
            foreach (var (feature, enabled) in saved.Features)
            {
                config.Features[feature] = enabled;
            }

            return;
        }

        // The saved configuration could not be read, so at least put back the switches the Engine asked about: an approval
        // is only asked for when a feature was off.
        foreach (var preview in asked)
        {
            config.Features[preview.ItemId] = false;
        }
    }
}
