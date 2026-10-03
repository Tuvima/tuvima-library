using MediaEngine.Contracts.Playback;

namespace MediaEngine.Web.Services.Playback;

public interface IUserPlaybackPreferencesAccessor
{
    long Generation => 0;
    Guid? ActiveProfileId => null;
    Task<UserPlaybackSettingsDto?> GetAsync(CancellationToken ct = default);
    void UpdateCache(UserPlaybackSettingsDto settings);
    void Invalidate();
}
