using System.Collections.Concurrent;
using MediaEngine.Web.Services.Integration;

namespace MediaEngine.Web.Services.Playback;

/// <summary>
/// Reads and writes the song table's remembered texts on the profile (<c>Listening.SongTableColumns</c>), so the columns, widths and
/// phone Songs sort follow the person across devices (spec music-table-and-reader-frame D4, D21). Reads go through the shared
/// preferences cache. A write takes the freshest copy of the profile's settings, changes only the one key and saves it, so another
/// choice made elsewhere in the meantime is kept. When the profile cannot be reached the failure is logged and the choice is kept in
/// memory for this session, so the table still remembers until the page is closed.
/// </summary>
public sealed class SongTableProfileSettings(
    IUserPlaybackPreferencesAccessor accessor,
    UIOrchestratorService orchestrator,
    ILogger<SongTableProfileSettings> logger)
{
    private readonly SemaphoreSlim _saveGate = new(1, 1);
    private readonly ConcurrentDictionary<(Guid Profile, string Key), string?> _unsaved = new();

    /// <summary>The text stored under a key for a profile; null when nothing is stored or the profile's settings cannot be read.</summary>
    public async ValueTask<string?> LoadAsync(Guid profileId, string key)
    {
        if (_unsaved.TryGetValue((profileId, key), out var pending))
        {
            return pending;
        }

        try
        {
            var settings = await accessor.GetAsync();
            return settings is not null
                && settings.ProfileId == profileId
                && settings.Listening?.SongTableColumns is { } columns
                && columns.TryGetValue(key, out var text)
                    ? text
                    : null;
        }
        catch (Exception exception) when (exception is HttpRequestException or InvalidOperationException or OperationCanceledException)
        {
            logger.LogWarning(exception, "Could not read the remembered song table choice {Key}; using the default.", key);
            return null;
        }
    }

    /// <summary>Remembers a text under a key for a profile; null forgets it.</summary>
    public async ValueTask SaveAsync(Guid profileId, string key, string? text)
    {
        await _saveGate.WaitAsync();
        try
        {
            var fresh = await orchestrator.GetPlaybackSettingsAsync();
            if (fresh is null || fresh.ProfileId != profileId)
            {
                logger.LogWarning("The song table choice {Key} could not be saved: the profile's settings were unavailable.", key);
                _unsaved[(profileId, key)] = text;
                return;
            }

            fresh.Listening.SongTableColumns ??= [];
            if (text is null)
            {
                fresh.Listening.SongTableColumns.Remove(key);
            }
            else
            {
                fresh.Listening.SongTableColumns[key] = text;
            }

            var saved = await orchestrator.SavePlaybackSettingsAsync(fresh);
            if (saved is null)
            {
                logger.LogWarning("The song table choice {Key} could not be saved to the profile.", key);
                _unsaved[(profileId, key)] = text;
                return;
            }

            _unsaved.TryRemove((profileId, key), out _);
            accessor.UpdateCache(saved);
        }
        catch (Exception exception) when (exception is HttpRequestException or InvalidOperationException or OperationCanceledException)
        {
            logger.LogWarning(exception, "The song table choice {Key} could not be saved to the profile.", key);
            _unsaved[(profileId, key)] = text;
        }
        finally
        {
            _saveGate.Release();
        }
    }
}
