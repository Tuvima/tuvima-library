using MediaEngine.Web.Components.Listen;
using MediaEngine.Web.Services.Playback;
using MediaEngine.Web.Tests.Support;
using Microsoft.Extensions.Logging;

namespace MediaEngine.Web.Tests;

/// <summary>The sort the phone Songs header remembers per profile (spec music-table-and-reader-frame D21): its text form and the profile store.</summary>
public sealed class SongSortPreferencesTests
{
    [Theory]
    [InlineData(SongColumn.Song, false, "song:asc")]
    [InlineData(SongColumn.Artist, true, "artist:desc")]
    [InlineData(SongColumn.DateAdded, true, "dateAdded:desc")]
    [InlineData(SongColumn.Time, false, "time:asc")]
    public void ASortRoundTripsThroughItsText(SongColumn column, bool descending, string text)
    {
        Assert.Equal(text, SongSortPreferences.Encode(new SongSortState(column, descending)));
        Assert.Equal(new SongSortState(column, descending), SongSortPreferences.Decode(text));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("song")]
    [InlineData("song:sideways")]
    [InlineData("genre:asc")]
    [InlineData("nothing:asc")]
    public void DamagedOrUnofferedTextReadsAsNoChoice(string? text)
    {
        Assert.Null(SongSortPreferences.Decode(text));
    }

    [Fact]
    public void ASortThePhoneListDoesNotOfferIsNotStored()
    {
        Assert.Null(SongSortPreferences.Encode(new SongSortState(SongColumn.Genre, false)));
        Assert.Null(SongSortPreferences.Encode(new SongSortState(null, false)));
        Assert.Null(SongSortPreferences.Encode(null));
    }

    [Fact]
    public async Task TheStoreKeepsTheSortOnTheProfileUnderItsOwnKeyAndForgetsOnNull()
    {
        var harness = new SongTableProfileHarness();
        var store = new ProfileSongSortPreferenceStore(harness.Settings);

        await store.SaveAsync(harness.Profile, new SongSortState(SongColumn.Plays, true));

        Assert.Equal(new SongSortState(SongColumn.Plays, true), await store.LoadAsync(harness.Profile));
        Assert.Null(await store.LoadAsync(Guid.NewGuid()));
        Assert.Equal("plays:desc", harness.Stored["songssort"]);

        await store.SaveAsync(harness.Profile, null);

        Assert.Empty(harness.Stored);
        Assert.Null(await store.LoadAsync(harness.Profile));
    }

    [Fact]
    public async Task TheSortSitsBesideTheRememberedColumnsWithoutReplacingThem()
    {
        var harness = new SongTableProfileHarness();
        harness.Stored["songs"] = "hide=plays";
        var store = new ProfileSongSortPreferenceStore(harness.Settings);

        await store.SaveAsync(harness.Profile, new SongSortState(SongColumn.Song, false));

        Assert.Equal("hide=plays", harness.Stored["songs"]);
        Assert.Equal("song:asc", harness.Stored["songssort"]);
    }

    [Fact]
    public async Task AnEngineThatCannotBeReachedUsesTheDefaultLogsAndKeepsTheSortForThisSession()
    {
        var harness = new SongTableProfileHarness { EngineDown = true };
        var store = new ProfileSongSortPreferenceStore(harness.Settings);

        var loaded = await store.LoadAsync(harness.Profile);
        await store.SaveAsync(harness.Profile, new SongSortState(SongColumn.Song, false));

        Assert.Null(loaded);
        Assert.Equal(new SongSortState(SongColumn.Song, false), await store.LoadAsync(harness.Profile));
        Assert.Empty(harness.Stored);
        Assert.Equal(2, harness.Log.Entries.Count(entry => entry.Level == LogLevel.Warning && entry.Exception is HttpRequestException));
    }
}
