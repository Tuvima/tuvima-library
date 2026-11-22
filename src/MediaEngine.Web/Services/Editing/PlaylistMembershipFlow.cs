using MediaEngine.Web.Components.Details;
using MediaEngine.Web.Components.Shared;
using MediaEngine.Web.Resources;
using MediaEngine.Web.Services.Integration;
using MediaEngine.Web.Services.Theming;
using MediaEngine.Web.Services.Ui;
using Microsoft.Extensions.Localization;

namespace MediaEngine.Web.Services.Editing;

/// <summary>The line a list shows in place after Add to playlist (no toast), and how loudly.</summary>
public sealed record PlaylistFlowNotice(string Message, AppSeverity Severity)
{
    /// <summary>The same line for a surface that already hosts an <see cref="UndoOffer"/> line (a "..." menu that closes): no Undo button.</summary>
    public UndoOffer ToOffer() => new(Message, null, Severity);
}

/// <summary>
/// "Add to playlist" from every place that offers it (Listen music spec D9, D12, D15; Listen playlists spec WP2): the picker of hand-made
/// playlists (<see cref="PlaylistPickerDialog"/>), then the add through <see cref="PlaylistMembership"/>, then one plain sentence for the
/// caller to show in place. The selection bars, the song menu, the player bar menu and the page action all go through here, and
/// dragged songs use <see cref="Describe"/>, so every path gives the same sentence.
/// </summary>
public static class PlaylistMembershipFlow
{
    /// <summary>
    /// Runs the flow for ticked rows (songs, or albums that stand for their songs). Returns the sentence to show in place, or null when the
    /// person closed the list without choosing. Putting songs in a playlist is not editing, so any device that shows a song list may do it.
    /// </summary>
    public static async Task<PlaylistFlowNotice?> RunAsync(IServiceProvider services, IReadOnlyList<BulkEditRow> rows, string label)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(rows);
        var strings = services.GetRequiredService<IStringLocalizer<SharedStrings>>();
        if (BulkEditRules.TooManyAlbums(rows))
        {
            return new PlaylistFlowNotice(strings["PlaylistMembership_TooManyAlbums", BulkEditRules.MaxAlbumsPerAdd].Value, AppSeverity.Warning);
        }

        var resolved = await PlaylistMembership.ResolveAsync(services.GetRequiredService<IEngineApiClient>(), rows);
        var notice = await RunForSongsAsync(services, resolved.SongIds, label);

        // An album the Engine could not read is told, never dropped silently: the sentence says what went in, then what did not.
        if (notice is null || resolved.UnreadableAlbums == 0)
        {
            return notice;
        }

        var unreadable = resolved.UnreadableAlbums == 1
            ? strings["PlaylistMembership_OneAlbumUnreadable"].Value
            : strings["PlaylistMembership_ManyAlbumsUnreadable", resolved.UnreadableAlbums].Value;
        var told = notice.Message.EndsWith('.') ? notice.Message : notice.Message + ".";
        return new PlaylistFlowNotice(
            $"{told} {unreadable}",
            notice.Severity == AppSeverity.Error ? AppSeverity.Error : AppSeverity.Warning);
    }

    /// <summary>The same flow for songs already known by their ids (the song menu, the player menu, a page action, the phone's bottom bar). On a phone the picker is a bottom sheet.</summary>
    public static async Task<PlaylistFlowNotice?> RunForSongsAsync(IServiceProvider services, IReadOnlyList<Guid> songIds, string label)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(songIds);
        var strings = services.GetRequiredService<IStringLocalizer<SharedStrings>>();
        if (songIds.Count == 0)
        {
            return new PlaylistFlowNotice(strings["PlaylistMembership_NoSongs"].Value, AppSeverity.Warning);
        }

        var api = services.GetRequiredService<IEngineApiClient>();
        var phone = services.GetService<DeviceContextService>()?.IsPhone == true;
        var dialogs = services.GetRequiredService<IAppDialogService>();
        var dialog = await dialogs.ShowAsync<PlaylistPickerDialog>(
            label,
            new AppDialogParameters<PlaylistPickerDialog>
            {
                { d => d.WorkIds, songIds },
            },
            phone ? new AppDialogOptions { Sheet = AppDialogSheetMode.Phone } : null);
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

    /// <summary>
    /// The sentence for how an add went: "3 songs added to Road trip", "2 added, 1 already there", "Already in Road trip". When some songs
    /// failed it says what went in and what did not, and is a warning rather than a success.
    /// </summary>
    public static PlaylistFlowNotice Describe(PlaylistAddResult result, IStringLocalizer<SharedStrings> strings)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(strings);
        if (result.Added == 0 && result.AlreadyThere == 0)
        {
            // The Engine's own explanation goes after the sentence when it gave one, so "could not be created" says why.
            var why = EngineFailureText.Plain(result.Reason);
            var told = result.Creating
                ? why is null ? strings["PlaylistMembership_CreateFailed"].Value : strings["PlaylistMembership_CreateFailedWhy", why].Value
                : why is null ? strings["PlaylistMembership_AddFailed", result.PlaylistName].Value : strings["PlaylistMembership_AddFailedWhy", result.PlaylistName, why].Value;
            return new PlaylistFlowNotice(told, AppSeverity.Error);
        }

        if (result.Failed > 0)
        {
            var told = result switch
            {
                { Added: 0 } => strings["PlaylistMembership_AlreadyThereSomeFailed", result.AlreadyThere, result.Failed].Value,
                { AlreadyThere: 0 } => strings["PlaylistMembership_SomeFailed", result.Added, result.Failed].Value,
                _ => strings["PlaylistMembership_SomeAlreadyThereSomeFailed", result.Added, result.AlreadyThere, result.Failed].Value,
            };
            return new PlaylistFlowNotice(told, AppSeverity.Warning);
        }

        if (result.Added == 0)
        {
            return new PlaylistFlowNotice(strings["PlaylistMembership_AllAlreadyThere", result.PlaylistName].Value, AppSeverity.Info);
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
