using MediaEngine.Web.Components.Listen;
using MediaEngine.Web.Models.ViewDTOs;

namespace MediaEngine.Web.Tests;

/// <summary>
/// Song lengths in the Songs list (spec music-player-and-queue D1/D2, bug F44), now read through the row the shared song table draws:
/// the file's measured length wins, a provider length in minutes is never shown as seconds, and an unknown length shows a dash.
/// </summary>
public sealed class SongRowDurationTests
{
    private static WorkViewModel Song(params (string Key, string Value)[] values) => new()
    {
        Id = Guid.NewGuid(),
        MediaType = "Music",
        CanonicalValues = [.. values.Select(v => new CanonicalValueViewModel { Key = v.Key, Value = v.Value })],
    };

    private static SongRow Row(WorkViewModel song) => SongRowMapper.FromWork(song);

    [Fact]
    public void AMinutesBasedProviderLengthWithNoFileLength_IsNotShownAsSeconds()
    {
        var row = Row(Song(("duration", "3.5")));

        Assert.Equal(210, row.DurationSeconds);
        Assert.Equal("3:30", row.DurationText);
    }

    [Fact]
    public void TheFileLength_WinsOverAProviderLength()
    {
        Assert.Equal("4:03", Row(Song(("duration", "9"), ("duration_sec", "243.4"))).DurationText);
    }

    [Fact]
    public void AProviderLengthStoredInSeconds_IsUsedWhenTheFileHasNone()
    {
        Assert.Equal("4:03", Row(Song(("duration_seconds", "243"))).DurationText);
    }

    [Fact]
    public void AnUnknownLength_ShowsADashNeverZero()
    {
        Assert.Equal("–", Row(Song(("title", "Song"))).DurationText);
    }

    [Fact]
    public void SortingByTime_OrdersAMixedSetByRealLength()
    {
        var minutesOnly = Row(Song(("duration", "3.5")));
        var fileOnly = Row(Song(("duration_sec", "300")));
        var unknown = Row(Song(("title", "x")));

        var ordered = SongTableSort.Sort([fileOnly, unknown, minutesOnly], SongTablePlace.Songs, new SongSortState(SongColumn.Time, false));

        Assert.Equal([unknown, minutesOnly, fileOnly], ordered);
    }
}
