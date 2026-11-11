using MediaEngine.Contracts.Playback;
using MediaEngine.Web.Components.Listen;

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
/// Keeps the sort on the profile (<c>Listening.SongTableColumns</c> under the key <see cref="PlaybackPreferenceValues.SongsSortKey"/>),
/// so it follows the person to every device. Failures are logged by <see cref="SongTableProfileSettings"/> and the page's default
/// sort is used.
/// </summary>
public sealed class ProfileSongSortPreferenceStore(SongTableProfileSettings settings) : ISongSortPreferenceStore
{
    public async ValueTask<SongSortState?> LoadAsync(Guid profileId)
        => SongSortPreferences.Decode(await settings.LoadAsync(profileId, PlaybackPreferenceValues.SongsSortKey));

    public ValueTask SaveAsync(Guid profileId, SongSortState? state)
        => settings.SaveAsync(profileId, PlaybackPreferenceValues.SongsSortKey, SongSortPreferences.Encode(state));
}
