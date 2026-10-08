using MediaEngine.Contracts.Playback;
using MediaEngine.Web.Services.Integration;

namespace MediaEngine.Web.Services.Playback;

public sealed class UserPlaybackPreferencesAccessor : IUserPlaybackPreferencesAccessor, IDisposable
{
    private readonly UIOrchestratorService _orchestrator;
    private readonly object _sync = new();
    private UserPlaybackSettingsDto? _cached;
    private long _generation;

    public UserPlaybackPreferencesAccessor(UIOrchestratorService orchestrator)
    {
        _orchestrator = orchestrator;
        _orchestrator.OnProfileChanged += Invalidate;
    }

    public long Generation { get { lock (_sync)
    {
        return _generation;
    } } }
    public Guid? ActiveProfileId => _orchestrator.ActivePlaybackProfileId;

    public async Task<UserPlaybackSettingsDto?> GetAsync(CancellationToken ct = default)
    {
        long generation;
        lock (_sync)
        {
            if (_cached is not null && _cached.ProfileId == ActiveProfileId)
            {
                return _cached;
            }
            generation = _generation;
        }

        var loaded = await _orchestrator.GetPlaybackSettingsAsync(ct);
        lock (_sync)
        {
            if (generation != _generation || loaded?.ProfileId != ActiveProfileId)
            {
                return null;
            }
            _cached = loaded;
            return _cached;
        }
    }

    public void UpdateCache(UserPlaybackSettingsDto settings)
    {
        lock (_sync)
        {
            if (settings.ProfileId == ActiveProfileId)
            {
                _cached = settings;
            }
        }
    }

    public void Invalidate()
    {
        lock (_sync)
        {
            _generation++;
            _cached = null;
        }
    }

    public void Dispose() => _orchestrator.OnProfileChanged -= Invalidate;
}
