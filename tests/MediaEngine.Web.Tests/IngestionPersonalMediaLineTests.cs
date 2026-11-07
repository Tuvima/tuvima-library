using MediaEngine.Contracts.Ingestion;
using MediaEngine.Web.Components.Settings;

namespace MediaEngine.Web.Tests;

/// <summary>The photo and personal-video count line of the Library activity list is worded in one place.</summary>
public sealed class IngestionPersonalMediaLineTests
{
    [Theory]
    [InlineData(2300, 0, "2,300 photos in progress")]
    [InlineData(0, 12, "12 personal videos in progress")]
    [InlineData(1, 1, "1 photo and 1 personal video in progress")]
    [InlineData(2300, 12, "2,300 photos and 12 personal videos in progress")]
    public void PhotosAndPersonalVideos_AreOneCountLineWorded(int photos, int videos, string line)
    {
        Assert.Equal(line, IngestionNowText.PersonalMediaLine(new IngestionPersonalMediaProgressDto { Photos = photos, Videos = videos }));
    }

    [Fact]
    public void WithNoPhotosOrVideos_TheCountLineIsHidden()
    {
        Assert.Null(IngestionNowText.PersonalMediaLine(new IngestionPersonalMediaProgressDto()));
    }
}
