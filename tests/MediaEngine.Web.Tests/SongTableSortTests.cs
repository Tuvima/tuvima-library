using MediaEngine.Web.Components.Listen;

namespace MediaEngine.Web.Tests;

/// <summary>The song table's sort rules (spec music-table-and-reader-frame D5).</summary>
public sealed class SongTableSortTests
{
    private static SongRow Row(
        string id,
        string title,
        string? artist = null,
        string? album = null,
        string? genre = null,
        int? number = null,
        int plays = 0,
        double? seconds = null,
        int? addedDay = null,
        bool owned = true,
        bool unnumbered = false) => new()
        {
            Id = id,
            Source = SongRowSource.Work,
            Title = title,
            Artist = artist,
            Album = album,
            Genre = genre,
            Number = number,
            Plays = plays,
            DurationSeconds = seconds,
            DateAdded = addedDay is { } day ? new DateTimeOffset(2026, 1, day, 0, 0, 0, TimeSpan.Zero) : null,
            IsOwned = owned,
            IsUnnumbered = unnumbered,
        };

    private static string[] Ids(IEnumerable<SongRow> rows) => rows.Select(row => row.Id).ToArray();

    private static readonly SongRow[] Library =
    [
        Row("a", "Alpha", artist: "Zed", album: "Y", genre: "Rock", number: 2, plays: 5, seconds: 200, addedDay: 3),
        Row("b", "bravo", artist: "amy", album: "X", genre: "Jazz", number: 1, plays: 9, seconds: 100, addedDay: 1),
        Row("c", "Charlie", artist: "Mia", album: "X", genre: "Pop", number: 3, plays: 0, seconds: 300, addedDay: 2),
    ];

    [Theory]
    [InlineData(SongColumn.DateAdded)]
    [InlineData(SongColumn.Plays)]
    [InlineData(SongColumn.Time)]
    [InlineData(SongColumn.Favorite)]
    public void FirstClickOnDateAddedPlaysTimeOrFavorite_SortsDescending(SongColumn column)
    {
        var state = SongTableSort.Click(SongTablePlace.Songs, new SongSortState(SongColumn.Song, false), column);

        Assert.Equal(column, state.Column);
        Assert.True(state.Descending);
    }

    [Theory]
    [InlineData(SongColumn.Song)]
    [InlineData(SongColumn.Artist)]
    [InlineData(SongColumn.Album)]
    [InlineData(SongColumn.Genre)]
    public void FirstClickOnTheOtherColumns_SortsAscending(SongColumn column)
    {
        var state = SongTableSort.Click(SongTablePlace.Songs, new SongSortState(SongColumn.Plays, true), column);

        Assert.Equal(column, state.Column);
        Assert.False(state.Descending);
    }

    [Fact]
    public void ClickingTheSortedColumnAgain_FlipsTheDirection()
    {
        var first = SongTableSort.Click(SongTablePlace.Songs, new SongSortState(SongColumn.Song, false), SongColumn.Plays);
        var second = SongTableSort.Click(SongTablePlace.Songs, first, SongColumn.Plays);
        var third = SongTableSort.Click(SongTablePlace.Songs, second, SongColumn.Plays);

        Assert.True(first.Descending);
        Assert.False(second.Descending);
        Assert.True(third.Descending);
    }

    [Fact]
    public void AClickOnAHeaderThatDoesNotSort_LeavesTheStateAsItWas()
    {
        var state = new SongSortState(SongColumn.Song, false);

        Assert.Equal(state, SongTableSort.Click(SongTablePlace.Songs, state, SongColumn.Tick));
        Assert.Equal(state, SongTableSort.Click(SongTablePlace.Songs, state, SongColumn.Number));
        Assert.Equal(state, SongTableSort.Click(SongTablePlace.Songs, state, SongColumn.Status));
    }

    [Fact]
    public void ThePlaylist_HasNoSortableHeaders_AndKeepsItsSavedOrder()
    {
        foreach (var column in SongTableColumns.Order)
        {
            Assert.False(SongTableSort.IsSortable(SongTablePlace.Playlist, column));
        }

        var start = SongTableSort.DefaultFor(SongTablePlace.Playlist);
        Assert.Null(start.Column);
        Assert.Equal(start, SongTableSort.Click(SongTablePlace.Playlist, start, SongColumn.Artist));

        // Even if a sort were asked for, the saved order is returned as given.
        Assert.Equal(["c", "a", "b"], Ids(SongTableSort.Sort([Library[2], Library[0], Library[1]], SongTablePlace.Playlist, new SongSortState(SongColumn.Song, false))));
    }

    [Fact]
    public void TheNumberAndStatusHeaders_SortOnlyOnAnAlbumPage()
    {
        Assert.True(SongTableSort.IsSortable(SongTablePlace.AlbumPage, SongColumn.Number));
        Assert.True(SongTableSort.IsSortable(SongTablePlace.AlbumPage, SongColumn.Status));
        Assert.False(SongTableSort.IsSortable(SongTablePlace.Songs, SongColumn.Number));
        Assert.False(SongTableSort.IsSortable(SongTablePlace.Albums, SongColumn.Status));
    }

    [Fact]
    public void EachPlaceStartsInItsOwnOrder()
    {
        Assert.Equal(new SongSortState(SongColumn.DateAdded, true), SongTableSort.DefaultFor(SongTablePlace.Songs));
        Assert.Equal(new SongSortState(SongColumn.DateAdded, true), SongTableSort.DefaultFor(SongTablePlace.Albums));
        Assert.Equal(new SongSortState(SongColumn.Number, false), SongTableSort.DefaultFor(SongTablePlace.AlbumPage));
    }

    [Theory]
    [InlineData(SongColumn.Song, false, "a,b,c")]
    [InlineData(SongColumn.Song, true, "c,b,a")]
    [InlineData(SongColumn.Artist, false, "b,c,a")]
    [InlineData(SongColumn.Artist, true, "a,c,b")]
    [InlineData(SongColumn.Album, false, "b,c,a")]
    [InlineData(SongColumn.Album, true, "a,b,c")]
    [InlineData(SongColumn.Genre, false, "b,c,a")]
    [InlineData(SongColumn.Genre, true, "a,c,b")]
    [InlineData(SongColumn.Plays, false, "c,a,b")]
    [InlineData(SongColumn.Plays, true, "b,a,c")]
    [InlineData(SongColumn.Time, false, "b,a,c")]
    [InlineData(SongColumn.Time, true, "c,a,b")]
    [InlineData(SongColumn.DateAdded, false, "b,c,a")]
    [InlineData(SongColumn.DateAdded, true, "a,c,b")]
    public void SortingSongs_OrdersEachColumnBothWays(SongColumn column, bool descending, string expected)
    {
        var sorted = SongTableSort.Sort(Library, SongTablePlace.Songs, new SongSortState(column, descending));

        Assert.Equal(expected.Split(','), Ids(sorted));
    }

    [Fact]
    public void SortingByAlbum_ThenOrdersWithinAnAlbumByTrackNumber()
    {
        var sorted = SongTableSort.Sort(Library, SongTablePlace.Songs, new SongSortState(SongColumn.Album, false));

        // Album X holds b (track 1) and c (track 3).
        Assert.Equal(["b", "c", "a"], Ids(sorted));
    }

    [Fact]
    public void SortingByFavorite_PutsSavedSongsFirstWhenDescending_AndTiesFallBackToTitle()
    {
        var favorites = new HashSet<string> { "c", "a" };

        var descending = SongTableSort.Sort(Library, SongTablePlace.Songs, new SongSortState(SongColumn.Favorite, true), favorites);
        var ascending = SongTableSort.Sort(Library, SongTablePlace.Songs, new SongSortState(SongColumn.Favorite, false), favorites);

        Assert.Equal(["a", "c", "b"], Ids(descending));
        Assert.Equal(["b", "a", "c"], Ids(ascending));
    }

    [Fact]
    public void RowsWithUnknownValues_SortWithoutFailing()
    {
        SongRow[] rows = [Row("x", "X"), Row("y", "Y", seconds: 10, addedDay: 5, plays: 1)];

        Assert.Equal(["x", "y"], Ids(SongTableSort.Sort(rows, SongTablePlace.Songs, new SongSortState(SongColumn.Time, false))));
        Assert.Equal(["y", "x"], Ids(SongTableSort.Sort(rows, SongTablePlace.Songs, new SongSortState(SongColumn.DateAdded, true))));
        Assert.Equal(["x", "y"], Ids(SongTableSort.Sort(rows, SongTablePlace.Songs, new SongSortState(SongColumn.Artist, false))));
    }

    [Fact]
    public void WithNoSortColumn_TheRowsKeepTheirGivenOrder()
    {
        Assert.Equal(["c", "a", "b"], Ids(SongTableSort.Sort([Library[2], Library[0], Library[1]], SongTablePlace.Songs, new SongSortState(null, false))));
    }

    // ── Album page ────────────────────────────────────────────────────────────────────────

    private static readonly SongRow[] AlbumTracks =
    [
        Row("t3", "Three", number: 3, seconds: 30),
        Row("u2", "Zed bonus", unnumbered: true),
        Row("t1", "One", number: 1, seconds: 10),
        Row("u1", "Alpha bonus", unnumbered: true),
        Row("t2", "Two", number: 2, seconds: 20, owned: false),
    ];

    [Fact]
    public void OnAnAlbumPage_TracksStartInTrackOrder_AndTheUnnumberedGroupIsLast()
    {
        var sorted = SongTableSort.Sort(AlbumTracks, SongTablePlace.AlbumPage, SongTableSort.DefaultFor(SongTablePlace.AlbumPage));

        Assert.Equal(["t1", "t2", "t3", "u1", "u2"], Ids(sorted));
    }

    [Theory]
    [InlineData(SongColumn.Number, false)]
    [InlineData(SongColumn.Number, true)]
    [InlineData(SongColumn.Song, false)]
    [InlineData(SongColumn.Song, true)]
    [InlineData(SongColumn.Time, false)]
    [InlineData(SongColumn.Time, true)]
    [InlineData(SongColumn.Status, false)]
    [InlineData(SongColumn.Status, true)]
    [InlineData(SongColumn.Favorite, true)]
    [InlineData(SongColumn.Artist, false)]
    public void OnAnAlbumPage_TheUnnumberedGroupStaysLast_WhateverTheSort(SongColumn column, bool descending)
    {
        var sorted = SongTableSort.Sort(AlbumTracks, SongTablePlace.AlbumPage, new SongSortState(column, descending), new HashSet<string> { "t2" });

        Assert.Equal(["u1", "u2"], Ids(sorted.TakeLast(2)));
        Assert.DoesNotContain(sorted.Take(3), row => row.IsUnnumbered);
    }

    [Fact]
    public void OnAnAlbumPage_SortingNumberDescending_ReversesOnlyTheNumberedTracks()
    {
        var sorted = SongTableSort.Sort(AlbumTracks, SongTablePlace.AlbumPage, new SongSortState(SongColumn.Number, true));

        Assert.Equal(["t3", "t2", "t1", "u1", "u2"], Ids(sorted));
    }

    [Fact]
    public void OnAnAlbumPage_SortingByStatus_PutsOwnedFirstThenMissing()
    {
        var sorted = SongTableSort.Sort(AlbumTracks, SongTablePlace.AlbumPage, new SongSortState(SongColumn.Status, false));

        Assert.Equal(["t1", "t3", "t2", "u1", "u2"], Ids(sorted));
    }

    [Fact]
    public void OnSongsAndAlbums_AnUnnumberedFlagDoesNotMoveARow()
    {
        var sorted = SongTableSort.Sort(AlbumTracks, SongTablePlace.Songs, new SongSortState(SongColumn.Song, false));

        Assert.Equal(["u1", "t1", "t3", "t2", "u2"], Ids(sorted));
    }

    [Theory]
    [InlineData("Africa", "A")]
    [InlineData("  zebra", "Z")]
    [InlineData("Émilie", "E")]
    [InlineData("Ångström", "A")]
    [InlineData("99 Luftballons", "#")]
    [InlineData("(Don't Fear) The Reaper", "#")]
    [InlineData("日本語", "#")]
    [InlineData("", "#")]
    [InlineData(null, "#")]
    public void AHeadingLetter_IsTheFirstLetterWithAccentsDropped_OrHashForAnythingElse(string? text, string expected)
    {
        Assert.Equal(expected, SongTableSort.LetterFor(text));
    }

    [Theory]
    [InlineData(SongTablePlace.Songs, SongColumn.Song, true)]
    [InlineData(SongTablePlace.Albums, SongColumn.Artist, true)]
    [InlineData(SongTablePlace.Songs, SongColumn.DateAdded, false)]
    [InlineData(SongTablePlace.Songs, SongColumn.Album, false)]
    [InlineData(SongTablePlace.AlbumPage, SongColumn.Song, false)]
    [InlineData(SongTablePlace.Playlist, SongColumn.Song, false)]
    public void LettersShowOnlyOnTheSongListsSortedBySongOrArtist(SongTablePlace place, SongColumn column, bool shown)
    {
        Assert.Equal(shown, SongTableSort.ShowsLetters(place, new SongSortState(column, false)));
        Assert.Equal(shown, SongTableSort.ShowsLetters(place, new SongSortState(column, true)));
    }
}
