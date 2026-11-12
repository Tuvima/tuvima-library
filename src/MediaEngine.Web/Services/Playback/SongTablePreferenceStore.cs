using MediaEngine.Web.Components.Listen;

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
/// Keeps the choice on the profile (<c>Listening.SongTableColumns</c>, one text per place, written and read with
/// <see cref="SongTablePreferences"/>), so it follows the person to every device. Failures are logged by
/// <see cref="SongTableProfileSettings"/> and the table keeps working with the defaults or the in-memory choice.
/// </summary>
public sealed class ProfileSongTablePreferenceStore(SongTableProfileSettings settings) : ISongTablePreferenceStore
{
    public async ValueTask<SongTableChoice> LoadAsync(Guid profileId, SongTablePlace place)
    {
        var key = SongTablePreferences.KeyFor(place);
        var stored = new Dictionary<string, string>();
        if (await settings.LoadAsync(profileId, key) is { } text && !string.IsNullOrWhiteSpace(text))
        {
            stored[key] = text;
        }

        return SongTablePreferences.Read(stored, place);
    }

    public async ValueTask SaveAsync(Guid profileId, SongTablePlace place, SongTableChoice choice)
    {
        var stored = new Dictionary<string, string>();
        SongTablePreferences.Write(stored, place, choice);
        var key = SongTablePreferences.KeyFor(place);
        await settings.SaveAsync(profileId, key, stored.TryGetValue(key, out var text) ? text : null);
    }
}
