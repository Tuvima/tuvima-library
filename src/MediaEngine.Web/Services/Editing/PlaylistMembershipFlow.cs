using MediaEngine.Web.Components.Details;
using MediaEngine.Web.Components.Shared;
using MediaEngine.Web.Resources;
using MediaEngine.Web.Services.Integration;
using MediaEngine.Web.Services.Theming;
using MediaEngine.Web.Services.Ui;
using Microsoft.Extensions.Localization;

namespace MediaEngine.Web.Services.Editing;

/// <summary>The line a list shows in place after Add to playlist (no toast), and how loudly.</summary>
public sealed record PlaylistFlowNotice(string Message, AppSeverity Severity);

/// <summary>
/// "Add to playlist" from ticked songs or albums (Listen music spec D9, D15): the list of hand-made playlists with "New playlist", then
/// the add, then one plain sentence for the caller to show in place. The selection bar goes through here, so every list that shows the bar
/// behaves the same.
/// </summary>
public static class PlaylistMembershipFlow
{
    /// <summary>
    /// Runs the flow. Returns the sentence to show in place, or null when the person closed the list without choosing. Off a desktop it
    /// returns the device message and opens nothing.
    /// </summary>
    public static async Task<PlaylistFlowNotice?> RunAsync(IServiceProvider services, IReadOnlyList<BulkEditRow> rows, string label)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(rows);
        var strings = services.GetRequiredService<IStringLocalizer<SharedStrings>>();
        var device = services.GetRequiredService<DeviceContextService>();
        if (EditorDeviceRule.TryRefuse(device))
        {
            return new PlaylistFlowNotice(EditorDeviceRule.Message, AppSeverity.Info);
        }

        var api = services.GetRequiredService<IEngineApiClient>();
        var songIds = await PlaylistMembership.ResolveSongIdsAsync(api, rows);
        if (songIds.Count == 0)
        {
            return new PlaylistFlowNotice(strings["PlaylistMembership_NoSongs"].Value, AppSeverity.Warning);
        }

        var dialogs = services.GetRequiredService<IAppDialogService>();
        var dialog = await dialogs.ShowAsync<MediaMembershipDialog>(label, new AppDialogParameters<MediaMembershipDialog>
        {
            { d => d.Playlist, true },
            { d => d.ChooseOnly, true },
            { d => d.OfferNewPlaylist, true },
            { d => d.WorkIds, songIds },
        });
        var result = await dialog.Result;
        if (result is not { Canceled: false, Data: PlaylistChoice choice })
        {
            return null;
        }

        var profile = await services.GetRequiredService<UIOrchestratorService>().GetActiveProfileAsync();
        var outcome = choice.PlaylistId is { } playlistId
            ? await PlaylistMembership.AddAsync(api, playlistId, choice.Name, songIds, profile?.Id)
            : await PlaylistMembership.CreateAsync(api, choice.Name, songIds, profile?.Id);
        return Describe(outcome, strings);
    }

    /// <summary>The sentence for how an add went: "3 songs added to Road trip", "2 added, 1 already there".</summary>
    public static PlaylistFlowNotice Describe(PlaylistAddResult result, IStringLocalizer<SharedStrings> strings)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(strings);
        if (result.Added == 0 && result.AlreadyThere == 0)
        {
            return new PlaylistFlowNotice(
                result.Created ? strings["PlaylistMembership_CreateFailed"].Value : strings["PlaylistMembership_AddFailed", result.PlaylistName].Value,
                AppSeverity.Error);
        }

        if (result.Added == 0)
        {
            return new PlaylistFlowNotice(strings["PlaylistMembership_AllAlreadyThere", result.PlaylistName].Value, AppSeverity.Info);
        }

        if (result.Failed > 0)
        {
            return new PlaylistFlowNotice(strings["PlaylistMembership_SomeFailed", result.Added, result.Failed].Value, AppSeverity.Warning);
        }

        if (result.AlreadyThere > 0)
        {
            return new PlaylistFlowNotice(strings["PlaylistMembership_SomeAlreadyThere", result.Added, result.AlreadyThere].Value, AppSeverity.Success);
        }

        return new PlaylistFlowNotice(
            result.Added == 1
                ? strings["PlaylistMembership_OneAdded", result.PlaylistName].Value
                : strings["PlaylistMembership_ManyAdded", result.Added, result.PlaylistName].Value,
            AppSeverity.Success);
    }
}
