using MediaEngine.Contracts.Collections;
using MediaEngine.Contracts.Details;
using MediaEngine.Web.Components.Listen;
using MediaEngine.Web.Models.ViewDTOs;

namespace MediaEngine.Web.Tests;

/// <summary>The one mapper from the three current song view models to the shared song table's row (spec music-table-and-reader-frame D2).</summary>
public sealed class SongRowMapperTests
{
    private static WorkViewModel Song(string title = "Song", params (string Key, string Value)[] values) => new()
    {
        Id = Guid.NewGuid(),
        MediaType = "Music",
        CanonicalValues =
        [
            new CanonicalValueViewModel { Key = "title", Value = title },
            .. values.Select(v => new CanonicalValueViewModel { Key = v.Key, Value = v.Value }),
        ],
    };

    [Fact]
    public void ASongFromTheSongsList_MapsItsShownFacts()
    {
        var id = Guid.NewGuid();
        var album = Guid.NewGuid();
        var artist = Guid.NewGuid();
        var added = new DateTimeOffset(2026, 3, 4, 5, 6, 7, TimeSpan.Zero);
        var work = new WorkViewModel
        {
            Id = id,
            CollectionId = album,
            ArtistPersonId = artist,
            MediaType = "Music",
            CreatedAt = added,
            ResolvedCoverUrl = "/cover.jpg",
            IsUpdatingDetails = true,
            CanonicalValues =
            [
                new() { Key = "title", Value = "Karma Police" },
                new() { Key = "artist", Value = "Radiohead" },
                new() { Key = "album", Value = "OK Computer" },
                new() { Key = "genre", Value = "Rock; Alternative" },
                new() { Key = "track_number", Value = "6" },
                new() { Key = "duration_sec", Value = "263.4" },
                new() { Key = "play_count", Value = "12" },
            ],
        };

        var row = SongRowMapper.FromWork(work);

        Assert.Equal(id.ToString("D"), row.Id);
        Assert.Equal(SongRowSource.Work, row.Source);
        Assert.Equal("Karma Police", row.Title);
        Assert.Equal("Radiohead", row.Artist);
        Assert.Equal("OK Computer", row.Album);
        Assert.Equal("Rock", row.Genre);
        Assert.Equal("/cover.jpg", row.CoverUrl);
        Assert.Equal(6, row.Number);
        Assert.False(row.IsUnnumbered);
        Assert.Equal(12, row.Plays);
        Assert.Equal(added, row.DateAdded);
        Assert.Equal("4:23", row.DurationText);
        Assert.True(row.IsOwned);
        Assert.True(row.IsUpdatingDetails);
        Assert.Equal(id, row.WorkId);
        Assert.Equal(album, row.CollectionId);
        Assert.Equal(artist, row.ArtistPersonId);
        Assert.Null(row.PlaylistItemId);
    }

    [Fact]
    public void ASongWithNoArtist_UsesItsAuthor_AndMissingFactsStayEmpty()
    {
        var row = SongRowMapper.FromWork(Song(values: [("author", "Someone")]));

        Assert.Equal("Someone", row.Artist);
        Assert.Null(row.Album);
        Assert.Null(row.Genre);
        Assert.Null(row.Number);
        Assert.Null(row.DateAdded);
        Assert.Equal(0, row.Plays);
        Assert.Null(row.DurationSeconds);
        Assert.Equal("–", row.DurationText);
    }

    [Fact]
    public void AnUntitledSong_FallsBackToItsFileName()
    {
        var row = SongRowMapper.FromWork(Song(title: "Untitled (Music)", values: [("file_name", "01 Intro")]));

        Assert.Equal("01 Intro", row.Title);
    }

    [Fact]
    public void AProviderLengthInMinutes_IsNotShownAsSeconds()
    {
        var row = SongRowMapper.FromWork(Song(values: [("duration", "3.5")]));

        Assert.Equal(210, row.DurationSeconds);
        Assert.Equal("3:30", row.DurationText);
    }

    [Fact]
    public void ASongInAPlaylist_KeepsItsPlaceInTheListAndItsPlaylistItemId()
    {
        var itemId = Guid.NewGuid();
        var work = Song(values: [("track_number", "9")]);
        var item = new CollectionItemDto { Id = itemId, WorkId = work.Id, SortOrder = 4 };

        var row = SongRowMapper.FromPlaylistItem(item, work, index: 2);

        Assert.Equal(SongRowSource.PlaylistItem, row.Source);
        Assert.Equal(3, row.Number);
        Assert.Equal(itemId, row.PlaylistItemId);
        Assert.Equal(work.Id, row.WorkId);
        Assert.Equal(work.Id.ToString("D"), row.Id);
    }

    [Fact]
    public void ASongInABuiltInList_HasNoPlaylistItemId()
    {
        var row = SongRowMapper.FromPlaylistItem(null, Song(), index: 0);

        Assert.Null(row.PlaylistItemId);
        Assert.Equal(1, row.Number);
    }

    [Fact]
    public void ATrackOnAnAlbumPage_MapsItsAlbumPageFacts()
    {
        var id = Guid.NewGuid();
        var asset = Guid.NewGuid().ToString();
        var item = new MediaGroupingItemViewModel
        {
            Id = id.ToString(),
            Title = "Airbag",
            Artist = "Radiohead",
            ArtworkUrl = "/art.jpg",
            TrackNumber = "1",
            DurationSeconds = 284,
            AssetId = asset,
            IsExplicit = true,
            IsOwned = false,
        };

        var row = SongRowMapper.FromGroupingItem(item);

        Assert.Equal(id.ToString(), row.Id);
        Assert.Equal(SongRowSource.Grouping, row.Source);
        Assert.Equal("Airbag", row.Title);
        Assert.Equal("Radiohead", row.Artist);
        Assert.Equal("/art.jpg", row.CoverUrl);
        Assert.Equal(1, row.Number);
        Assert.False(row.IsUnnumbered);
        Assert.Equal("4:44", row.DurationText);
        Assert.Equal(asset, row.AssetId);
        Assert.Equal(id, row.WorkId);
        Assert.True(row.IsExplicit);
        Assert.False(row.IsOwned);
    }

    [Fact]
    public void ATrackAddedAnyway_HasNoNumberAndIsUnnumbered()
    {
        var row = SongRowMapper.FromGroupingItem(new MediaGroupingItemViewModel { Id = "x", Title = "Bonus", TrackNumber = "7", IsUnmatched = true });

        Assert.True(row.IsUnnumbered);
        Assert.Null(row.Number);
        Assert.Null(row.WorkId);
    }

    [Fact]
    public void ATrackWithoutATrackNumber_FallsBackToItsChapterIndex_AndAClockDurationString()
    {
        var row = SongRowMapper.FromGroupingItem(new MediaGroupingItemViewModel { Id = "x", Title = "Part", ChapterIndex = 3, Duration = "2:05" });

        Assert.Equal(3, row.Number);
        Assert.Equal(125, row.DurationSeconds);
    }
}
