using MediaEngine.Api.Services.Display;
using MediaEngine.Api.Services.Playback;
using MediaEngine.Api.Services.ReadServices;
using MediaEngine.Storage;
using MediaEngine.Storage.Contracts;

namespace MediaEngine.Api.Tests;

/// <summary>Builds the real spoiler helper over a real temp data store for tests that construct the Universe read services by hand.</summary>
internal static class SpoilerTestKit
{
    public static CharacterSpoilerService Create(IDisplayProjectionReadService display, IDatabaseConnection database)
    {
        var progress = new ViewerProgressRepository(database);
        return new CharacterSpoilerService(
            new CharacterFirstSeenReader(display, progress),
            progress,
            new UserPlaybackSettingsService(database, new ProfileRepository(database)),
            new FictionalEntityRepository(database));
    }
}
