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

    /// <summary>
    /// Runs the flow. Returns the line the caller shows in place when the device cannot edit (<see cref="EditorDeviceRule.ItemMessage"/>), else null.
    /// </summary>
    public static async Task<string?> RunAsync(
        IServiceProvider services,
        IReadOnlyList<CollectionEditorSeedItem> items,
        string label,
        string? title)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(items);
        var dialogs = services.GetRequiredService<IAppDialogService>();
        var device = services.GetRequiredService<DeviceContextService>();
        if (items.Count == 0)
        {
            return null;
        }

        if (EditorDeviceRule.TryRefuseItems(device))
        {
            return EditorDeviceRule.ItemMessage;
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
            var profile = await services.GetRequiredService<UIOrchestratorService>().GetActiveProfileAsync();
            await services.GetRequiredService<CollectionEditorLauncherService>().OpenAsync(new CollectionEditorLaunchRequest
            {
                ActiveProfileId = profile?.Id,
                InitialMembershipMode = "Manual",
                InitialTitle = title,
                InitialItems = items,
            });
        }

        return null;
    }
}
