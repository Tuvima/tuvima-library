using System.Text.Json;
using MediaEngine.Contracts.Details;

namespace MediaEngine.Contracts.Tests;

/// <summary>The details page's "Your copy" wire shape (details-page-bottom spec D4, D5, section 5): what rides on the page and under which names.</summary>
public sealed class YourCopyContractTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public void YourCopy_RoundTripsChipsGroupsAndTheFileNameAndLocation()
    {
        var page = new DetailPageViewModel
        {
            Id = "work-1",
            EntityType = DetailEntityType.Movie,
            YourCopy = new DetailYourCopyViewModel
            {
                Blocks =
                [
                    new DetailYourCopyBlockViewModel
                    {
                        Label = "Director's cut",
                        Kind = DetailYourCopyKinds.Video,
                        Chips = ["1080p", "H.264", "AAC 5.1", "MKV"],
                        Groups = [new DetailYourCopyGroupViewModel { Key = DetailYourCopyGroupKeys.Video, Lines = ["1920 × 1080 · 23.976 fps"] }],
                        FileName = "Test.mkv",
                        FileLocation = "/media/Test.mkv",
                    },
                    new DetailYourCopyBlockViewModel { Kind = DetailYourCopyKinds.Video, IsReadingFacts = true },
                ],
            },
        };

        var json = JsonSerializer.Serialize(page, JsonOptions);
        var roundTrip = JsonSerializer.Deserialize<DetailPageViewModel>(json, JsonOptions);

        var blocks = Assert.IsType<DetailYourCopyViewModel>(roundTrip?.YourCopy).Blocks;
        Assert.Equal(2, blocks.Count);
        Assert.Equal("Director's cut", blocks[0].Label);
        Assert.Equal(["1080p", "H.264", "AAC 5.1", "MKV"], blocks[0].Chips);
        Assert.Equal("video", Assert.Single(blocks[0].Groups).Key);
        Assert.Equal("/media/Test.mkv", blocks[0].FileLocation);
        Assert.True(blocks[1].IsReadingFacts);
        Assert.Contains("\"yourCopy\":", json, StringComparison.Ordinal);
        Assert.Contains("\"isReadingFacts\":", json, StringComparison.Ordinal);
    }

    [Fact]
    public void APageWithoutYourCopy_WritesNullAndReadsBackAsNone()
    {
        var json = JsonSerializer.Serialize(new DetailPageViewModel { Id = "work-1" }, JsonOptions);

        Assert.Contains("\"yourCopy\":null", json, StringComparison.Ordinal);
        Assert.Null(JsonSerializer.Deserialize<DetailPageViewModel>(json, JsonOptions)?.YourCopy);
    }

    [Fact]
    public void TheKindAndGroupKeysTheDashboardSwitchesOn_AreTheseExactWords()
    {
        Assert.Equal(["video", "show", "album", "audio", "book", "audiobook", "comic"],
            new[] { DetailYourCopyKinds.Video, DetailYourCopyKinds.Show, DetailYourCopyKinds.Album, DetailYourCopyKinds.Audio, DetailYourCopyKinds.Book, DetailYourCopyKinds.Audiobook, DetailYourCopyKinds.Comic });
        Assert.Equal(["video", "audio_subtitles", "file", "files", "audio", "tracks", "book", "reading"],
            new[] { DetailYourCopyGroupKeys.Video, DetailYourCopyGroupKeys.AudioSubtitles, DetailYourCopyGroupKeys.File, DetailYourCopyGroupKeys.Files, DetailYourCopyGroupKeys.Audio, DetailYourCopyGroupKeys.Tracks, DetailYourCopyGroupKeys.Book, DetailYourCopyGroupKeys.Reading });
    }
}
