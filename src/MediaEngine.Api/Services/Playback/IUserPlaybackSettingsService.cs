using MediaEngine.Contracts.Playback;
using MediaEngine.Domain.Models;

namespace MediaEngine.Api.Services.Playback;

public interface IUserPlaybackSettingsService
{
    Task<UserPlaybackSettingsDto> GetAsync(Guid profileId, CancellationToken ct = default);
    Task<UserPlaybackSettingsDto> GetOrCreateDefaultsAsync(Guid profileId, CancellationToken ct = default);
    Task<UserPlaybackSettingsDto> UpdateAsync(Guid profileId, UserPlaybackSettingsDto settings, CancellationToken ct = default);

    /// <summary>The profile's spoiler level; "up to where I am" until the person chooses.</summary>
    Task<SpoilerLevel> GetSpoilerLevelAsync(Guid profileId, CancellationToken ct = default);

    /// <summary>Saves the spoiler level (<c>up_to_progress</c> or <c>everything</c>); the only way to change it. Throws <see cref="ArgumentException"/> for anything else.</summary>
    Task<SpoilerLevel> SetSpoilerLevelAsync(Guid profileId, string level, CancellationToken ct = default);
}
