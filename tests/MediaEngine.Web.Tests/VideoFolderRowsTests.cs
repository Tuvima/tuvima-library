using MediaEngine.Domain.Services;
using MediaEngine.Web.Models.ViewDTOs;
using MediaEngine.Web.Services.MediaTiles;
using Xunit;

namespace MediaEngine.Web.Tests;

public sealed class VideoFolderRowsTests
{
    private static MediaTileViewModel Folder() => new() { Title = "Lessons", IsCollection = true, MediaKind = VideoFolderWording.FolderMediaType };

    private static MediaTileViewModel Loose() => new() { Title = "Clip", IsCollection = false, MediaKind = "Video" };

    [Fact]
    public void AFolderOfVideosIsTold_ApartFromALooseVideo()
    {
        Assert.True(VideoFolderRows.IsFolder(Folder()));
        Assert.False(VideoFolderRows.IsFolder(Loose()));
    }

    [Fact]
    public void TheTwoRowsShowOnlyWhenTheListHoldsBothKinds()
    {
        Assert.True(VideoFolderRows.HoldsBoth([Folder(), Loose()]));
        Assert.False(VideoFolderRows.HoldsBoth([Folder(), Folder()]));
        Assert.False(VideoFolderRows.HoldsBoth([Loose()]));
        Assert.False(VideoFolderRows.HoldsBoth([]));
    }
}
