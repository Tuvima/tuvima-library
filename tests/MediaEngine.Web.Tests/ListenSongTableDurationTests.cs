using MediaEngine.Web.Components.Listen;
using MediaEngine.Web.Models.ViewDTOs;

namespace MediaEngine.Web.Tests;

/// <summary>
/// Song lengths in the Songs list (spec music-player-and-queue D1/D2, bug F44): the file's measured length wins,
/// a provider length in minutes is never shown as seconds, and an unknown length shows a dash.
/// </summary>
public sealed class ListenSongTableDurationTests
{
    private static WorkViewModel Song(params (string Key, string Value)[] values) => new()
    {
        Id = Guid.NewGuid(),
        MediaType = "Music",
        CanonicalValues = [.. values.Select(v => new CanonicalValueViewModel { Key = v.Key, Value = v.Value })],
    };

    [Fact]
    public void AMinutesBasedProviderLengthWithNoFileLength_IsNotShownAsSeconds()
    {
        var song = Song(("duration", "3.5"));

        Assert.Equal(210, ListenSongTable.GetTrackDurationSeconds(song));
        Assert.Equal("3:30", ListenSongTable.GetTrackDuration(song));
    }

    [Fact]
    public void TheFileLength_WinsOverAProviderLength()
    {
        var song = Song(("duration", "9"), ("duration_sec", "243.4"));

        Assert.Equal("4:03", ListenSongTable.GetTrackDuration(song));
    }

    [Fact]
    public void AProviderLengthStoredInSeconds_IsUsedWhenTheFileHasNone()
    {
        var song = Song(("duration_seconds", "243"));

        Assert.Equal("4:03", ListenSongTable.GetTrackDuration(song));
    }

    [Fact]
    public void AnUnknownLength_ShowsADashNeverZero()
    {
        Assert.Equal("–", ListenSongTable.GetTrackDuration(Song(("title", "Song"))));
    }

    [Fact]
    public void SortingByTime_OrdersAMixedSetByRealLength()
    {
        var minutesOnly = Song(("duration", "3.5"));
        var fileOnly = Song(("duration_sec", "300"));
        var unknown = Song(("title", "x"));

        var ordered = new[] { fileOnly, unknown, minutesOnly }
            .OrderBy(ListenSongTable.GetTrackDurationSeconds)
            .ToList();

        Assert.Equal([unknown, minutesOnly, fileOnly], ordered);
    }
}
