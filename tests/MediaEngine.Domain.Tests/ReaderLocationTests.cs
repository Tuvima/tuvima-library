using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Services;

namespace MediaEngine.Domain.Tests;

// How two saved places in the same EPUB compare (Global bookmarks spec D12): the reader's location text names a chapter file and a
// paragraph inside it; the character inside the paragraph does not matter.
public sealed class ReaderLocationTests
{
    private static readonly BookmarkGapLimits Text = new BookmarkClosenessConfiguration().Text;

    [Fact]
    public void ALocation_NamesItsChapterAndParagraph_AndIgnoresTheCharacter()
    {
        var spot = ReaderLocation.TryParse("epubcfi(/6/14[ch7]!/4/2/6/1:20)");

        Assert.NotNull(spot);
        Assert.Equal(6, spot!.Chapter);
        Assert.Equal("/4/2/6", spot.Paragraph);
        Assert.Equal(spot, ReaderLocation.TryParse("epubcfi(/6/14!/4/2/6/1:99)"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("1532")]
    [InlineData("epubcfi(/6/14!/4/2,/6/1:0,/6/1:9)")]
    [InlineData("epubcfi(/6)")]
    public void TextThatIsNotAPointLocation_IsNotUnderstood(string? text) =>
        Assert.Null(ReaderLocation.TryParse(text));

    [Theory]
    [InlineData("epubcfi(/6/14!/4/2/6/1:0)", "epubcfi(/6/14!/4/2/6/1:300)", BookmarkCloseness.SameSpot)]
    [InlineData("epubcfi(/6/14!/4/2/6/1:0)", "epubcfi(/6/14!/4/2/8/1:0)", BookmarkCloseness.Close)]
    [InlineData("epubcfi(/6/14!/4/2/6/1:0)", "epubcfi(/6/16!/4/2/6/1:0)", BookmarkCloseness.Far)]
    [InlineData("epubcfi(/6/14!/4/2/6/1:0)", "epubcfi(/6/18!/4/2/6/1:0)", BookmarkCloseness.Far)]
    [InlineData("epubcfi(/6/14!/4/2/6/1:0)", "epubcfi(/6/20!/4/2/6/1:0)", BookmarkCloseness.New)]
    public void TwoPlacesInOneBook_AreLabelledByHowFarApartTheyAre(string a, string b, BookmarkCloseness expected) =>
        Assert.Equal(expected, ReaderLocation.Classify(Text, ReaderLocation.TryParse(a)!, ReaderLocation.TryParse(b)!));
}
