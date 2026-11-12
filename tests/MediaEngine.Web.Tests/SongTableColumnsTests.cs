using MediaEngine.Web.Components.Listen;
using MediaEngine.Web.Services.Playback;

namespace MediaEngine.Web.Tests;

/// <summary>The song table's column set, per-place defaults, widths and remembered choice (spec music-table-and-reader-frame D3, D4).</summary>
public sealed class SongTableColumnsTests
{
    private static SongColumn[] Columns(IEnumerable<SongColumnLayout> layout) => layout.Select(item => item.Column).ToArray();

    [Fact]
    public void Songs_StartWithEveryColumnButGenre()
    {
        Assert.Equal(
            [SongColumn.Number, SongColumn.Song, SongColumn.Artist, SongColumn.Album, SongColumn.DateAdded, SongColumn.Plays, SongColumn.Favorite, SongColumn.Time],
            Columns(SongTableColumns.Resolve(SongTablePlace.Songs)));
    }

    [Fact]
    public void Albums_StartTheSameAsSongs()
    {
        Assert.Equal(Columns(SongTableColumns.Resolve(SongTablePlace.Songs)), Columns(SongTableColumns.Resolve(SongTablePlace.Albums)));
    }

    [Fact]
    public void AnAlbumPage_StartsWithoutArtist_WhenTheAlbumHasOneArtist()
    {
        Assert.Equal(
            [SongColumn.Number, SongColumn.Song, SongColumn.Status, SongColumn.Favorite, SongColumn.Time],
            Columns(SongTableColumns.Resolve(SongTablePlace.AlbumPage, albumHasMultipleArtists: false)));
    }

    [Fact]
    public void AnAlbumPage_StartsWithArtist_OnlyWhenTheAlbumHasMoreThanOneArtist()
    {
        Assert.Equal(
            [SongColumn.Number, SongColumn.Song, SongColumn.Artist, SongColumn.Status, SongColumn.Favorite, SongColumn.Time],
            Columns(SongTableColumns.Resolve(SongTablePlace.AlbumPage, albumHasMultipleArtists: true)));
    }

    [Fact]
    public void APlaylist_StartsWithArtistAlbumFavoriteAndTimeOnly()
    {
        Assert.Equal(
            [SongColumn.Number, SongColumn.Song, SongColumn.Artist, SongColumn.Album, SongColumn.Favorite, SongColumn.Time],
            Columns(SongTableColumns.Resolve(SongTablePlace.Playlist)));
    }

    [Fact]
    public void APlaylist_CannotOfferGenrePlaysOrDateAdded()
    {
        var offered = SongTableColumns.Available(SongTablePlace.Playlist);

        Assert.DoesNotContain(SongColumn.Genre, offered);
        Assert.DoesNotContain(SongColumn.Plays, offered);
        Assert.DoesNotContain(SongColumn.DateAdded, offered);
        Assert.Equal([SongColumn.Number, SongColumn.Artist, SongColumn.Album, SongColumn.Favorite, SongColumn.Time], offered);
    }

    [Fact]
    public void Songs_OfferGenreInTheColumnsList_ButNotTickOrSong()
    {
        var offered = SongTableColumns.Available(SongTablePlace.Songs);

        Assert.Contains(SongColumn.Genre, offered);
        Assert.DoesNotContain(SongColumn.Tick, offered);
        Assert.DoesNotContain(SongColumn.Song, offered);
    }

    [Theory]
    [InlineData(SongTablePlace.Songs)]
    [InlineData(SongTablePlace.Albums)]
    [InlineData(SongTablePlace.AlbumPage)]
    [InlineData(SongTablePlace.Playlist)]
    public void Favorite_IsAlwaysRightBeforeTime_WhateverTheChoice(SongTablePlace place)
    {
        var everything = new SongTableChoice { Shown = SongTableColumns.Available(place).ToHashSet() };

        foreach (var choice in new[] { SongTableChoice.Default, everything })
        {
            var columns = Columns(SongTableColumns.Resolve(place, choice, albumHasMultipleArtists: true, showTick: true));
            Assert.Equal(SongColumn.Time, columns[^1]);
            Assert.Equal(SongColumn.Favorite, columns[^2]);
        }
    }

    [Fact]
    public void TheColumnOrder_IsTheTableOrder()
    {
        Assert.Equal(
            [SongColumn.Tick, SongColumn.Number, SongColumn.Song, SongColumn.Artist, SongColumn.Album, SongColumn.DateAdded, SongColumn.Plays, SongColumn.Genre, SongColumn.Status, SongColumn.Favorite, SongColumn.Time],
            SongTableColumns.Order);
    }

    [Fact]
    public void TheTick_ComesFirstOnlyWhenTheHostAsksForIt()
    {
        Assert.DoesNotContain(SongColumn.Tick, Columns(SongTableColumns.Resolve(SongTablePlace.Songs)));
        Assert.Equal(SongColumn.Tick, Columns(SongTableColumns.Resolve(SongTablePlace.Songs, showTick: true))[0]);
    }

    [Fact]
    public void AChoice_ShowsAndHidesColumnsAgainstTheDefaults()
    {
        var choice = new SongTableChoice
        {
            Shown = new HashSet<SongColumn> { SongColumn.Genre },
            Hidden = new HashSet<SongColumn> { SongColumn.Plays },
        };

        var columns = Columns(SongTableColumns.Resolve(SongTablePlace.Songs, choice));

        Assert.Contains(SongColumn.Genre, columns);
        Assert.DoesNotContain(SongColumn.Plays, columns);
        Assert.True(Array.IndexOf(columns, SongColumn.Genre) < Array.IndexOf(columns, SongColumn.Favorite));
    }

    [Fact]
    public void TheSongColumn_CannotBeHidden_AndAColumnThePlaceLacksCannotBeShown()
    {
        var choice = new SongTableChoice
        {
            Hidden = new HashSet<SongColumn> { SongColumn.Song },
            Shown = new HashSet<SongColumn> { SongColumn.Genre, SongColumn.Status },
        };

        var columns = Columns(SongTableColumns.Resolve(SongTablePlace.Playlist, choice));

        Assert.Contains(SongColumn.Song, columns);
        Assert.DoesNotContain(SongColumn.Genre, columns);
        Assert.DoesNotContain(SongColumn.Status, columns);
    }

    [Fact]
    public void Widths_StartAtTheDefaults_AndAChosenWidthIsKeptWithinLimits()
    {
        var choice = new SongTableChoice
        {
            Widths = new Dictionary<SongColumn, int> { [SongColumn.Artist] = 9999, [SongColumn.Album] = 1 },
        };

        var layout = SongTableColumns.Resolve(SongTablePlace.Songs, choice).ToDictionary(item => item.Column, item => item.Width);

        Assert.Equal(SongTableColumns.DefaultWidth(SongColumn.Song), layout[SongColumn.Song]);
        Assert.Equal(SongTableColumns.MaxWidth, layout[SongColumn.Artist]);
        Assert.Equal(SongTableColumns.MinWidth(SongColumn.Album), layout[SongColumn.Album]);
    }

    [Fact]
    public void TheTickColumn_IsNeverResized()
    {
        var choice = new SongTableChoice { Widths = new Dictionary<SongColumn, int> { [SongColumn.Tick] = 300 } };

        var tick = SongTableColumns.Resolve(SongTablePlace.Songs, choice, showTick: true).First(item => item.Column == SongColumn.Tick);

        Assert.Equal(SongTableColumns.DefaultWidth(SongColumn.Tick), tick.Width);
    }

    [Fact]
    public void AnAlbumWithTwoArtists_HasMultipleArtists_AndOneArtistInAnyCaseDoesNot()
    {
        SongRow Row(string? artist) => new() { Id = Guid.NewGuid().ToString(), Source = SongRowSource.Grouping, Title = "t", Artist = artist };

        Assert.True(SongTableColumns.HasMultipleArtists([Row("A"), Row("B")]));
        Assert.False(SongTableColumns.HasMultipleArtists([Row("A"), Row(" a "), Row(null)]));
    }

    // ── The remembered choice ─────────────────────────────────────────────────────────────

    [Fact]
    public void WithNothingStored_TheChoiceIsTheDefaults()
    {
        var choice = SongTablePreferences.Read(new Dictionary<string, string>(), SongTablePlace.Songs);

        Assert.True(choice.IsDefault);
        Assert.Equal(Columns(SongTableColumns.Resolve(SongTablePlace.Songs)), Columns(SongTableColumns.Resolve(SongTablePlace.Songs, choice)));
    }

    [Fact]
    public void ARememberedChoice_RoundTripsThroughTheStore()
    {
        var store = new Dictionary<string, string>();
        var choice = SongTableChoice.Default;
        choice = SongTablePreferences.WithColumnVisible(choice, SongTablePlace.Songs, SongColumn.Genre, visible: true);
        choice = SongTablePreferences.WithColumnVisible(choice, SongTablePlace.Songs, SongColumn.Plays, visible: false);
        choice = SongTablePreferences.WithWidth(choice, SongColumn.Artist, 240);

        SongTablePreferences.Write(store, SongTablePlace.Songs, choice);
        var read = SongTablePreferences.Read(store, SongTablePlace.Songs);

        Assert.Equal(choice.Shown, read.Shown);
        Assert.Equal(choice.Hidden, read.Hidden);
        Assert.Equal(choice.Widths, read.Widths);
        Assert.Equal(
            Columns(SongTableColumns.Resolve(SongTablePlace.Songs, choice)),
            Columns(SongTableColumns.Resolve(SongTablePlace.Songs, read)));
        Assert.Equal(240, SongTableColumns.Resolve(SongTablePlace.Songs, read).First(item => item.Column == SongColumn.Artist).Width);
    }

    [Fact]
    public void ResettingOneColumnsWidth_KeepsTheOtherWidthsAndTheColumnChoices()
    {
        var choice = SongTablePreferences.WithColumnVisible(SongTableChoice.Default, SongTablePlace.Songs, SongColumn.Genre, visible: true);
        choice = SongTablePreferences.WithWidth(choice, SongColumn.Artist, 240);
        choice = SongTablePreferences.WithWidth(choice, SongColumn.Album, 300);

        var reset = SongTablePreferences.WithDefaultWidth(choice, SongColumn.Artist);

        Assert.False(reset.Widths.ContainsKey(SongColumn.Artist));
        Assert.Equal(300, reset.Widths[SongColumn.Album]);
        Assert.Equal(choice.Shown, reset.Shown);
        Assert.Equal(SongTableColumns.DefaultWidth(SongColumn.Artist), SongTableColumns.Resolve(SongTablePlace.Songs, reset).First(item => item.Column == SongColumn.Artist).Width);
    }

    [Fact]
    public void ResettingTheOnlyChangedWidth_ReturnsToTheDefaults()
    {
        var choice = SongTablePreferences.WithWidth(SongTableChoice.Default, SongColumn.Artist, 240);

        Assert.True(SongTablePreferences.WithDefaultWidth(choice, SongColumn.Artist).IsDefault);
    }

    [Fact]
    public void ResettingAColumnThatHasNoSavedWidth_ChangesNothing()
    {
        var choice = SongTablePreferences.WithWidth(SongTableChoice.Default, SongColumn.Artist, 240);

        Assert.Same(choice, SongTablePreferences.WithDefaultWidth(choice, SongColumn.Album));
    }

    [Fact]
    public void EachPlaceRemembersItsOwnChoice()
    {
        var store = new Dictionary<string, string>();
        SongTablePreferences.Write(store, SongTablePlace.Songs, SongTablePreferences.WithColumnVisible(SongTableChoice.Default, SongTablePlace.Songs, SongColumn.Genre, true));

        Assert.False(SongTablePreferences.Read(store, SongTablePlace.Songs).IsDefault);
        Assert.True(SongTablePreferences.Read(store, SongTablePlace.Playlist).IsDefault);
        Assert.True(SongTablePreferences.Read(store, SongTablePlace.Albums).IsDefault);
        Assert.True(SongTablePreferences.Read(store, SongTablePlace.AlbumPage).IsDefault);
    }

    [Fact]
    public void ResetColumns_PutsTheDefaultsBackAndRemovesTheEntry()
    {
        var store = new Dictionary<string, string>();
        SongTablePreferences.Write(store, SongTablePlace.Playlist, SongTablePreferences.WithColumnVisible(SongTableChoice.Default, SongTablePlace.Playlist, SongColumn.Album, false));
        Assert.Single(store);

        SongTablePreferences.Reset(store, SongTablePlace.Playlist);

        Assert.Empty(store);
        Assert.True(SongTablePreferences.Read(store, SongTablePlace.Playlist).IsDefault);
    }

    [Fact]
    public void TickingAColumnBackToItsDefault_RemovesTheEntry_SoTheProfileDoesNotFillUp()
    {
        var store = new Dictionary<string, string>();
        var hidden = SongTablePreferences.WithColumnVisible(SongTableChoice.Default, SongTablePlace.Songs, SongColumn.Plays, false);
        var restored = SongTablePreferences.WithColumnVisible(hidden, SongTablePlace.Songs, SongColumn.Plays, true);

        SongTablePreferences.Write(store, SongTablePlace.Songs, restored);

        Assert.True(restored.IsDefault);
        Assert.Empty(store);
    }

    [Fact]
    public void ADamagedStoredValue_IsReadAsFarAsItMakesSense_AndNeverThrows()
    {
        var store = new Dictionary<string, string>
        {
            [SongTablePreferences.KeyFor(SongTablePlace.Songs)] = "bogus;show=genre,nope,song;hide=plays,plays;width=artist:abc,album:300,tick:500;x=",
        };

        var choice = SongTablePreferences.Read(store, SongTablePlace.Songs);

        Assert.Equal([SongColumn.Genre], choice.Shown);
        Assert.Equal([SongColumn.Plays], choice.Hidden);
        var width = Assert.Single(choice.Widths);
        Assert.Equal(SongColumn.Album, width.Key);
        Assert.Equal(300, width.Value);
    }

    [Fact]
    public void AColumnStoredAsBothShownAndHidden_IsLeftAtItsDefault()
    {
        var choice = SongTablePreferences.Decode("show=genre;hide=genre");

        Assert.True(choice.IsDefault);
    }

    [Fact]
    public void AlbumPageArtist_CanBeChosenOnOrOffAgainstTheAlbumsOwnDefault()
    {
        // One artist: Artist is off by default, so turning it on is remembered as "show".
        var shown = SongTablePreferences.WithColumnVisible(SongTableChoice.Default, SongTablePlace.AlbumPage, SongColumn.Artist, true, albumHasMultipleArtists: false);
        Assert.Contains(SongColumn.Artist, shown.Shown);

        // Several artists: Artist is on by default, so turning it off is remembered as "hide".
        var hidden = SongTablePreferences.WithColumnVisible(SongTableChoice.Default, SongTablePlace.AlbumPage, SongColumn.Artist, false, albumHasMultipleArtists: true);
        Assert.Contains(SongColumn.Artist, hidden.Hidden);
    }

    [Fact]
    public void ChoosingSongOrAColumnThePlaceLacks_ChangesNothing()
    {
        Assert.True(SongTablePreferences.WithColumnVisible(SongTableChoice.Default, SongTablePlace.Songs, SongColumn.Song, false).IsDefault);
        Assert.True(SongTablePreferences.WithColumnVisible(SongTableChoice.Default, SongTablePlace.Playlist, SongColumn.Genre, true).IsDefault);
    }
}
