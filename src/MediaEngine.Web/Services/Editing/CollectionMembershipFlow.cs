using MediaEngine.Web.Components.Details;
using MediaEngine.Web.Services.Integration;
using MediaEngine.Web.Services.Theming;
using MediaEngine.Web.Services.Ui;

namespace MediaEngine.Web.Services.Editing;

/// <summary>
/// "Add to collection" from an item's "..." menu or from ticked rows (Collections and lists spec 4a, D19): the list of collections the
/// person may edit, and "New collection with this title", which opens the New collection flow already holding the chosen titles. It is
/// the one place both entry points go through, so they cannot drift apart.
/// </summary>
public static class CollectionMembershipFlow
{
    /// <summary>What the list dialog closes with when the person chose "New collection with this title".</summary>
    public const string NewCollectionChoice = "new-collection";

    public static async Task RunAsync(
        IServiceProvider services,
        IReadOnlyList<CollectionEditorSeedItem> items,
        string label,
        string? title)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(items);
        var dialogs = services.GetRequiredService<IAppDialogService>();
        var toasts = services.GetRequiredService<IAppToastService>();
        var device = services.GetRequiredService<DeviceContextService>();
        var orchestrator = services.GetRequiredService<UIOrchestratorService>();
        if (items.Count == 0 || EditorDeviceRule.TryRefuse(device, toasts))
        {
            return;
        }

        var dialog = await dialogs.ShowAsync<MediaMembershipDialog>(label, new AppDialogParameters<MediaMembershipDialog>
        {
            { d => d.Playlist, false },
            { d => d.WorkIds, items.Select(item => item.WorkId).Distinct().ToList() },
            { d => d.OfferNewCollection, true },
        });
        var result = await dialog.Result;
        if (result is { Canceled: false, Data: string choice } && choice == NewCollectionChoice)
        {
            var profile = await orchestrator.GetActiveProfileAsync();
            await services.GetRequiredService<CollectionEditorLauncherService>().OpenAsync(new CollectionEditorLaunchRequest
            {
                ActiveProfileId = profile?.Id,
                InitialMembershipMode = "Manual",
                InitialTitle = title,
                InitialItems = items,
            });
        }
    }
}
