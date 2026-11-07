using MediaEngine.Contracts.Playback;
using Microsoft.Extensions.Logging;

namespace MediaEngine.Web.Services.Playback;

/// <summary>A skip length chosen in Playback settings.</summary>
public sealed record PlaybackSkipChange(PlaybackSkipDirection Direction, int Seconds);

/// <summary>
/// What Playback settings saves on the profile (unified player v2 D14): the skip lengths and "Reset playback preferences". These are the same
/// per-profile preferences Settings, Playback held; the player is now their only home, and the running player takes a new length at once.
/// Speed is not saved here: it applies to what is playing and is the host's to change.
/// </summary>
public sealed class PlaybackSettingsActions
{
    private readonly PlayerPreferenceService _preferences;
    private readonly PlaybackSessionController _playback;
    private readonly ILogger<PlaybackSettingsActions>? _logger;

    public PlaybackSettingsActions(PlayerPreferenceService preferences, PlaybackSessionController playback, ILogger<PlaybackSettingsActions>? logger = null)
    {
        _preferences = preferences;
        _playback = playback;
        _logger = logger;
    }

    /// <summary>Saves one skip length for what is playing (video keeps its own pair, audio another). False when it is not a length on offer or could not be saved.</summary>
    public Task<bool> SetSkipLengthAsync(PlaybackSkipChange change, CancellationToken ct = default)
    {
        var video = _playback.IsVideoMode;
        return PlaybackSkipChoices.Options(video, change.Direction).Contains(change.Seconds)
            ? SaveAsync(PlaybackSkipChoices.Set(video, change.Direction, change.Seconds), ct)
            : Task.FromResult(false);
    }

    /// <summary>Puts the skip lengths of what is playing (video or audio) back to the app's defaults. False when they could not be saved.</summary>
    public Task<bool> ResetSkipLengthsAsync(CancellationToken ct = default) => SaveAsync(PlaybackSkipChoices.ResetLengths(_playback.IsVideoMode), ct);

    private async Task<bool> SaveAsync(Action<UserPlaybackSettingsDto> change, CancellationToken ct)
    {
        try
        {
            if (!await _preferences.UpdateAsync(change, ct))
            {
                return false;
            }

            var saved = await _preferences.GetAsync(ct);
            if (saved is not null)
            {
                var video = _playback.IsVideoMode;
                _playback.ApplySkipLengths(
                    PlaybackSkipChoices.Current(saved, video, PlaybackSkipDirection.Back),
                    PlaybackSkipChoices.Current(saved, video, PlaybackSkipDirection.Forward));
            }

            return true;
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException)
        {
            _logger?.LogWarning(ex, "The playback preference could not be saved.");
            return false;
        }
    }
}
