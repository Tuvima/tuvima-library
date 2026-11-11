using MediaEngine.Contracts.Playback;
using MediaEngine.Web.Services.Integration;

namespace MediaEngine.Web.Services.Playback;

/// <summary>
/// The player's own view of the profile's playback preferences (player controls v3): the chapter-marks and bookmark-marks switches,
/// the remembered bookmark filter and the subtitle styling. It reads through the shared preferences cache and saves only the one
/// field that changed, on top of the freshest copy, so a change made on another device in the meantime is kept.
/// </summary>
public sealed class PlayerPreferenceService
{
    private readonly IUserPlaybackPreferencesAccessor _accessor;
    private readonly UIOrchestratorService _orchestrator;
    private readonly SemaphoreSlim _saveGate = new(1, 1);

    public PlayerPreferenceService(IUserPlaybackPreferencesAccessor accessor, UIOrchestratorService orchestrator)
    {
        _accessor = accessor;
        _orchestrator = orchestrator;
    }

    /// <summary>Raised after a change was saved, so every open player redraws.</summary>
    public event Action? Changed;

    public Task<UserPlaybackSettingsDto?> GetAsync(CancellationToken ct = default) => _accessor.GetAsync(ct);

    /// <summary>
    /// Whether the progress bar shows chapter marks: the profile's choice, or the media default (off for video, on for audiobooks).
    /// </summary>
    public static bool ShowChapterMarks(UserPlaybackSettingsDto? settings, bool audiobook) =>
        settings?.General.ShowChapterMarks ?? audiobook;

    /// <summary>Saves one change. Returns false when the settings could not be read or saved (nothing is changed then).</summary>
    public async Task<bool> UpdateAsync(Action<UserPlaybackSettingsDto> change, CancellationToken ct = default)
    {
        await _saveGate.WaitAsync(ct);
        try
        {
            var fresh = await _orchestrator.GetPlaybackSettingsAsync(ct);
            if (fresh is null)
            {
                return false;
            }

            change(fresh);
            var saved = await _orchestrator.SavePlaybackSettingsAsync(fresh, ct);
            if (saved is null)
            {
                return false;
            }

            _accessor.UpdateCache(saved);
        }
        finally { _saveGate.Release(); }

        Changed?.Invoke();
        return true;
    }
}
