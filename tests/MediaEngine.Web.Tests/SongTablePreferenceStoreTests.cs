using MediaEngine.Web.Components.Listen;
using MediaEngine.Web.Services.Playback;
using MediaEngine.Web.Tests.Support;
using Microsoft.Extensions.Logging;

namespace MediaEngine.Web.Tests;

/// <summary>The profile store for the song table's remembered columns: one text per place on the profile, defaults on any failure, never silent.</summary>
public sealed class SongTablePreferenceStoreTests
{
    private static SongTableChoice Chosen() => new()
    {
        Shown = new HashSet<SongColumn> { SongColumn.Genre },
        Hidden = new HashSet<SongColumn> { SongColumn.Plays },
        Widths = new Dictionary<SongColumn, int> { [SongColumn.Artist] = 220 },
    };

    private static (ProfileSongTablePreferenceStore Store, SongTableProfileHarness Harness) Create()
    {
        var harness = new SongTableProfileHarness();
        return (new ProfileSongTablePreferenceStore(harness.Settings), harness);
    }

    [Theory]
    [InlineData(SongTablePlace.Songs, "songs")]
    [InlineData(SongTablePlace.Albums, "albums")]
    [InlineData(SongTablePlace.AlbumPage, "albumpage")]
    [InlineData(SongTablePlace.Playlist, "playlist")]
    public void EachPlaceIsStoredUnderThePlainLowerCaseNameTheEngineAccepts(SongTablePlace place, string key)
    {
        Assert.Equal(key, SongTablePreferences.KeyFor(place));
        Assert.Contains(key, MediaEngine.Contracts.Playback.PlaybackPreferenceValues.SongTablePlaces);
    }

    [Fact]
    public async Task AChoiceRoundTripsThroughTheProfileForItsPlaceOnly()
    {
        var (store, harness) = Create();

        await store.SaveAsync(harness.Profile, SongTablePlace.Songs, Chosen());

        var back = await store.LoadAsync(harness.Profile, SongTablePlace.Songs);
        Assert.Equal(Chosen().Shown, back.Shown);
        Assert.Equal(Chosen().Hidden, back.Hidden);
        Assert.Equal(220, back.Widths[SongColumn.Artist]);
        Assert.Equal(["songs"], harness.Stored.Keys);
        Assert.True((await store.LoadAsync(harness.Profile, SongTablePlace.Playlist)).IsDefault);
    }

    [Fact]
    public async Task AChoiceIsReadBackFromTheEngineByAnotherDevice()
    {
        var (store, harness) = Create();
        await store.SaveAsync(harness.Profile, SongTablePlace.Albums, Chosen());

        // A different browser has its own cache but the same profile on the Engine.
        var otherDevice = new SongTableProfileHarness(harness.Profile);
        foreach (var (key, value) in harness.Stored)
        {
            otherDevice.Stored[key] = value;
        }

        var back = await new ProfileSongTablePreferenceStore(otherDevice.Settings).LoadAsync(harness.Profile, SongTablePlace.Albums);
        Assert.Equal(Chosen().Hidden, back.Hidden);
    }

    [Fact]
    public async Task AnotherProfilesChoiceIsNeverShown()
    {
        var (store, harness) = Create();
        await store.SaveAsync(harness.Profile, SongTablePlace.Songs, Chosen());

        Assert.True((await store.LoadAsync(Guid.NewGuid(), SongTablePlace.Songs)).IsDefault);
    }

    [Fact]
    public async Task ResetForgetsTheEntryAndTheDefaultsApplyAgain()
    {
        var (store, harness) = Create();
        await store.SaveAsync(harness.Profile, SongTablePlace.Albums, Chosen());
        Assert.Single(harness.Stored);

        await store.SaveAsync(harness.Profile, SongTablePlace.Albums, SongTableChoice.Default);

        Assert.Empty(harness.Stored);
        Assert.True((await store.LoadAsync(harness.Profile, SongTablePlace.Albums)).IsDefault);
    }

    [Fact]
    public async Task SavingOnePlaceKeepsTheOtherPlacesAndTheSavedSort()
    {
        var (store, harness) = Create();
        harness.Stored["songssort"] = "plays:desc";
        await store.SaveAsync(harness.Profile, SongTablePlace.Songs, Chosen());

        await store.SaveAsync(harness.Profile, SongTablePlace.Albums, Chosen());

        Assert.Equal(["albums", "songs", "songssort"], harness.Stored.Keys.Order());
    }

    [Fact]
    public async Task AnUnreadableStoredValueFallsBackTowardTheDefaults()
    {
        var (store, harness) = Create();
        harness.Stored["songs"] = "show=nothing;width=artist:wide;;=";

        Assert.True((await store.LoadAsync(harness.Profile, SongTablePlace.Songs)).IsDefault);
    }

    [Fact]
    public async Task AnEngineThatCannotBeReachedUsesTheDefaultsLogsAndKeepsTheChoiceForThisSession()
    {
        var (store, harness) = Create();
        harness.EngineDown = true;

        var loaded = await store.LoadAsync(harness.Profile, SongTablePlace.Songs);
        await store.SaveAsync(harness.Profile, SongTablePlace.Songs, Chosen());
        var again = await store.LoadAsync(harness.Profile, SongTablePlace.Songs);

        Assert.True(loaded.IsDefault);
        Assert.Equal(Chosen().Hidden, again.Hidden);
        Assert.Empty(harness.Stored);
        Assert.Equal(2, harness.Log.Entries.Count(entry => entry.Level == LogLevel.Warning && entry.Exception is HttpRequestException));
    }

    [Fact]
    public async Task ARememberedChoiceIsSentToTheEngineOnceItIsBackAndDoesNotLinger()
    {
        var (store, harness) = Create();
        harness.EngineDown = true;
        await store.SaveAsync(harness.Profile, SongTablePlace.Songs, Chosen());
        harness.EngineDown = false;

        await store.SaveAsync(harness.Profile, SongTablePlace.Songs, Chosen());

        Assert.Equal(1, harness.Saves);
        Assert.Equal(["songs"], harness.Stored.Keys);
        harness.Stored.Clear();
        harness.Accessor.Invalidate();
        Assert.True((await store.LoadAsync(harness.Profile, SongTablePlace.Songs)).IsDefault);
    }
}
