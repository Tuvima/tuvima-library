using MediaEngine.Contracts.Display;
using MediaEngine.Web.Services.Integration;

namespace MediaEngine.Web.Services.MediaTiles;

/// <summary>
/// The Engine's list of libraries the signed-in profile may browse (one browse tab each), held for the circuit so moving between Watch, Listen
/// and Read does not read it again every time. A read is reused for about a minute and only for the profile it was read for; a different
/// active profile always reads again, so a tab can never show for a library the active profile cannot see. The list itself always comes from
/// the Engine, which applies the profile's grants. (No event announces that the Libraries settings changed, so a change shows within a minute.)
/// </summary>
public sealed class BrowseLibrariesState(IEngineApiClient api, ActiveProfileSessionService profiles, TimeProvider? clock = null)
{
    /// <summary>How long a read is reused.</summary>
    public static readonly TimeSpan MaxAge = TimeSpan.FromMinutes(1);

    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private IReadOnlyList<BrowseLibraryDto>? _libraries;
    private Guid? _librariesProfile;
    private DateTimeOffset _readAt;
    private Task<IReadOnlyList<BrowseLibraryDto>?>? _inFlight;
    private Guid? _inFlightProfile;

    /// <summary>The libraries the active profile may browse; null when the Engine could not be read (the lanes then keep their fixed tabs).</summary>
    public async Task<IReadOnlyList<BrowseLibraryDto>?> GetAsync(CancellationToken ct = default)
    {
        var profile = (await profiles.GetActiveProfileAsync(ct))?.Id;
        if (_libraries is not null && _librariesProfile == profile && _clock.GetUtcNow() - _readAt < MaxAge)
        {
            return _libraries;
        }

        if (_inFlight is not null && _inFlightProfile == profile)
        {
            return await _inFlight;
        }

        // The read is shared by every page asking at once, so it is not tied to one caller's cancellation.
        var read = api.GetBrowseLibrariesAsync();
        _inFlight = read;
        _inFlightProfile = profile;
        try
        {
            var libraries = await read;
            if (libraries is not null)
            {
                _libraries = libraries;
                _librariesProfile = profile;
                _readAt = _clock.GetUtcNow();
            }
            else if (_librariesProfile != profile)
            {
                _libraries = null;
            }

            return libraries;
        }
        finally
        {
            if (ReferenceEquals(_inFlight, read))
            {
                _inFlight = null;
            }
        }
    }

    /// <summary>Forgets the held list, so the next ask reads it again.</summary>
    public void Invalidate() => _libraries = null;
}
