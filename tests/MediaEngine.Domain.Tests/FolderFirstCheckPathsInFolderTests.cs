using MediaEngine.Domain.Models;

namespace MediaEngine.Domain.Tests;

/// <summary>Whether a file is inside a folder, by the first check's path rule (used by "Suitable for limited profiles").</summary>
public sealed class FolderFirstCheckPathsInFolderTests
{
    [Theory]
    [InlineData("/media/kids", "/media/kids/a.mkv", false, true)]
    [InlineData("/media/kids", "/media/kids/deeper/a.mkv", false, true)]
    [InlineData("/media/kids/", "/media/kids/a.mkv", false, true)]
    [InlineData("/media/kids", "/media/kids2/a.mkv", false, false)]
    [InlineData("/media/kids", "/media/kids", false, false)]
    [InlineData("/media/kids", "/media/Kids/a.mkv", false, false)]
    [InlineData("/media/kids", "/media/Kids/a.mkv", true, true)]
    [InlineData("D:\\Media\\Kids", "d:\\media\\kids\\a.mkv", true, true)]
    [InlineData("D:\\Media\\Kids", "D:\\Media\\Kids2\\a.mkv", true, false)]
    [InlineData("/", "/a.mkv", false, true)]
    public void AFile_IsInsideAFolder_OnlyWhenItsPathStartsWithTheFolderAndASeparator(string folder, string file, bool ignoreCase, bool expected) =>
        Assert.Equal(expected, FolderFirstCheckPaths.IsInFolder(FolderFirstCheckPaths.Prefix(folder), file, ignoreCase));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void ANoPath_IsInNoFolder(string? file) =>
        Assert.False(FolderFirstCheckPaths.IsInFolder("/media/kids/", file, ignoreCase: false));
}
