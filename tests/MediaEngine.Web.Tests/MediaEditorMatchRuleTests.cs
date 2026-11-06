using MediaEngine.Web.Services.Editing;

namespace MediaEngine.Web.Tests;

public sealed class MediaEditorMatchRuleTests
{
    [Theory]
    [InlineData("Videos", "series", false)]
    [InlineData("Videos", "work", true)]
    [InlineData("TV", "series", true)]
    [InlineData("TV", "file-group", false)]
    [InlineData("Movies", "work", true)]
    public void OnlyAFolderOfOtherVideosHasNoMatchTab(string mediaType, string scope, bool expected) =>
        Assert.Equal(expected, MediaEditorMatchRule.CanMatch(mediaType, scope));
}
