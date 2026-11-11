using MediaEngine.Web.Components.Listen;
using Microsoft.JSInterop;

namespace MediaEngine.Web.Services.Playback;

/// <summary>Where a profile's song table columns and widths are kept between visits (spec music-table-and-reader-frame D4).</summary>
public interface ISongTablePreferenceStore
{
    /// <summary>What the profile chose for a place; the place's defaults when nothing is stored or the store cannot be read.</summary>
    ValueTask<SongTableChoice> LoadAsync(Guid profileId, SongTablePlace place);

    /// <summary>Remembers a choice for a place. A choice equal to the defaults forgets the entry (Reset columns).</summary>
    ValueTask SaveAsync(Guid profileId, SongTablePlace place, SongTableChoice choice);
}

/// <summary>
/// Keeps the choice in this browser's local storage through <c>wwwroot/js/song-table-preferences.js</c>, one entry per profile and
/// place, written and read with <see cref="SongTablePreferences"/>. It is a display preference, so nothing goes to the Engine. When
/// storage is blocked the failure is logged and the defaults are used, so the table still works and simply forgets next time.
/// </summary>
public sealed class BrowserSongTablePreferenceStore(IJSRuntime js, ILogger<BrowserSongTablePreferenceStore> logger) : ISongTablePreferenceStore
{
    private const string Script = "./js/song-table-preferences.js";

    public async ValueTask<SongTableChoice> LoadAsync(Guid profileId, SongTablePlace place)
    {
        try
        {
            await using var module = await js.InvokeAsync<IJSObjectReference>("import", Script);
            var text = await module.InvokeAsync<string?>("load", StorageKey(profileId, place));
            var stored = new Dictionary<string, string>();
            if (!string.IsNullOrWhiteSpace(text))
            {
                stored[SongTablePreferences.KeyFor(place)] = text;
            }

            return SongTablePreferences.Read(stored, place);
        }
        catch (Exception exception) when (exception is JSException or InvalidOperationException)
        {
            logger.LogWarning(exception, "Could not read the song table columns for {Place}; using the defaults.", place);
            return SongTableChoice.Default;
        }
        catch (JSDisconnectedException exception)
        {
            logger.LogDebug(exception, "The browser went away before the song table columns for {Place} could be read.", place);
            return SongTableChoice.Default;
        }
    }

    public async ValueTask SaveAsync(Guid profileId, SongTablePlace place, SongTableChoice choice)
    {
        try
        {
            var stored = new Dictionary<string, string>();
            SongTablePreferences.Write(stored, place, choice);

            await using var module = await js.InvokeAsync<IJSObjectReference>("import", Script);
            if (stored.TryGetValue(SongTablePreferences.KeyFor(place), out var text))
            {
                await module.InvokeAsync<bool>("save", StorageKey(profileId, place), text);
            }
            else
            {
                await module.InvokeAsync<bool>("remove", StorageKey(profileId, place));
            }
        }
        catch (Exception exception) when (exception is JSException or InvalidOperationException)
        {
            logger.LogWarning(exception, "Could not save the song table columns for {Place}.", place);
        }
        catch (JSDisconnectedException exception)
        {
            logger.LogDebug(exception, "The browser went away before the song table columns for {Place} could be saved.", place);
        }
    }

    /// <summary>The local-storage key: one per profile and place, so two profiles on one browser never share columns.</summary>
    internal static string StorageKey(Guid profileId, SongTablePlace place)
        => $"tuvima.songTable.v1.{profileId:N}.{SongTablePreferences.KeyFor(place)}";
}
