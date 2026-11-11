using MediaEngine.Web.Components.Listen;
using Microsoft.JSInterop;

namespace MediaEngine.Web.Services.Playback;

/// <summary>Where a profile's phone Songs sort is kept between visits (spec music-table-and-reader-frame D21).</summary>
public interface ISongSortPreferenceStore
{
    /// <summary>What the profile chose; null when nothing is stored or the store cannot be read.</summary>
    ValueTask<SongSortState?> LoadAsync(Guid profileId);

    /// <summary>Remembers a sort; null forgets it.</summary>
    ValueTask SaveAsync(Guid profileId, SongSortState? state);
}

/// <summary>
/// Keeps the sort in this browser's local storage (the same script and approach as the remembered song columns), one entry per
/// profile. It is a display preference, so nothing goes to the Engine; when storage is blocked the failure is logged and the page's
/// default sort is used.
/// </summary>
public sealed class BrowserSongSortPreferenceStore(IJSRuntime js, ILogger<BrowserSongSortPreferenceStore> logger) : ISongSortPreferenceStore
{
    private const string Script = "./js/song-table-preferences.js";

    public async ValueTask<SongSortState?> LoadAsync(Guid profileId)
    {
        try
        {
            await using var module = await js.InvokeAsync<IJSObjectReference>("import", Script);
            return SongSortPreferences.Decode(await module.InvokeAsync<string?>("load", StorageKey(profileId)));
        }
        catch (Exception exception) when (exception is JSException or InvalidOperationException)
        {
            logger.LogWarning(exception, "Could not read the remembered Songs sort; using the default.");
            return null;
        }
        catch (JSDisconnectedException exception)
        {
            logger.LogDebug(exception, "The browser went away before the remembered Songs sort could be read.");
            return null;
        }
    }

    public async ValueTask SaveAsync(Guid profileId, SongSortState? state)
    {
        try
        {
            await using var module = await js.InvokeAsync<IJSObjectReference>("import", Script);
            if (SongSortPreferences.Encode(state) is { } text)
            {
                await module.InvokeAsync<bool>("save", StorageKey(profileId), text);
            }
            else
            {
                await module.InvokeAsync<bool>("remove", StorageKey(profileId));
            }
        }
        catch (Exception exception) when (exception is JSException or InvalidOperationException)
        {
            logger.LogWarning(exception, "Could not save the Songs sort.");
        }
        catch (JSDisconnectedException exception)
        {
            logger.LogDebug(exception, "The browser went away before the Songs sort could be saved.");
        }
    }

    /// <summary>The local-storage key: one per profile, so two profiles on one browser never share a sort.</summary>
    internal static string StorageKey(Guid profileId) => $"tuvima.songSort.v1.{profileId:N}";
}
