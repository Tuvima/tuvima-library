using MediaEngine.Contracts.Playback;
using MediaEngine.Web.Services.Integration;

namespace MediaEngine.Web.Services.Playback;

public sealed class UserPlaybackPreferencesAccessor : IUserPlaybackPreferencesAccessor, IDisposable
{
    private readonly UIOrchestratorService _orchestrator;
    private UserPlaybackSettingsDto? _cached;

    public UserPlaybackPreferencesAccessor(UIOrchestratorService orchestrator)
    {
        _orchestrator = orchestrator;
        _orchestrator.OnProfileChanged += Invalidate;
    }

    public async Task<UserPlaybackSettingsDto?> GetAsync(CancellationToken ct = default)
    {
        if (_cached is not null)
        {
            return _cached;
        }

        _cached = await _orchestrator.GetPlaybackSettingsAsync(ct);
        return _cached;
    }

    public void UpdateCache(UserPlaybackSettingsDto settings) => _cached = settings;

    public void Invalidate() => _cached = null;

    public void Dispose() => _orchestrator.OnProfileChanged -= Invalidate;
}
