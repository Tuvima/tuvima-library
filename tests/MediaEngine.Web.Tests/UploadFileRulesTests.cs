using MediaEngine.Web.Components.Settings;
using MediaEngine.Web.Tests.Support;

namespace MediaEngine.Web.Tests;

// The one small mapping from a dropped file's type to the kinds it fits (Library setup D44), and the words for a refused start.
public sealed class UploadFileRulesTests
{
    [Theory]
    [InlineData("film.MKV", "", UploadFileClass.Video)]
    [InlineData("song.flac", "", UploadFileClass.Audio)]
    [InlineData("book.epub", "", UploadFileClass.Book)]
    [InlineData("paper.pdf", "", UploadFileClass.Book)]
    [InlineData("issue.cbz", "", UploadFileClass.Comic)]
    [InlineData("photo.heic", "", UploadFileClass.Image)]
    [InlineData("noextension", "video/mp4", UploadFileClass.Video)]
    [InlineData("noextension", "audio/mpeg", UploadFileClass.Audio)]
    [InlineData("notes.txt", "text/plain", UploadFileClass.Unknown)]
    [InlineData("film.mkv", "text/plain", UploadFileClass.Video)]
    public void AFilesClassComesFromItsExtension_ThenTheTypeTheBrowserReports(string name, string mime, UploadFileClass expected) =>
        Assert.Equal(expected, UploadFileRules.Classify(name, mime));

    [Fact]
    public void VideoFitsWatchKinds_AudioListenKinds_BooksAndComicsTheirOwnReadKinds_AndPicturesNone()
    {
        var kinds = LibraryKindsTestData.Catalog;

        Assert.Equal(["watch"], UploadFileRules.FittingKinds(kinds, UploadFileClass.Video).Select(kind => kind.Area).Distinct());
        Assert.Contains("movies", UploadFileRules.FittingKinds(kinds, UploadFileClass.Video).Select(kind => kind.Key));
        Assert.Equal(["listen"], UploadFileRules.FittingKinds(kinds, UploadFileClass.Audio).Select(kind => kind.Area).Distinct());
        Assert.Equal(["books"], UploadFileRules.FittingKinds(kinds, UploadFileClass.Book).Select(kind => kind.Key));
        Assert.Equal(["comics"], UploadFileRules.FittingKinds(kinds, UploadFileClass.Comic).Select(kind => kind.Key));
        Assert.Empty(UploadFileRules.FittingKinds(kinds, UploadFileClass.Image));
        Assert.Empty(UploadFileRules.FittingKinds(kinds, UploadFileClass.Unknown));
    }

    [Fact]
    public void AFileFitsAPageOfItsAreaAndAKindThatTakesIt_ButNotAnotherAreaOrAKindThatDoesNot()
    {
        var kinds = LibraryKindsTestData.Catalog;

        Assert.True(UploadFileRules.Fits(kinds, UploadFileClass.Audio, "listen", null));
        Assert.True(UploadFileRules.Fits(kinds, UploadFileClass.Audio, "listen", "music"));
        Assert.False(UploadFileRules.Fits(kinds, UploadFileClass.Video, "listen", "music"));
        Assert.False(UploadFileRules.Fits(kinds, UploadFileClass.Comic, "read", "books"));
        Assert.True(UploadFileRules.Fits(kinds, UploadFileClass.Comic, "read", "comics"));
    }

    [Fact]
    public void TheMostLikelyKindIsTheUsualOneForTheClass_ElseTheFirst()
    {
        var kinds = LibraryKindsTestData.Catalog;

        Assert.Equal("movies", UploadFileRules.MostLikely(UploadFileClass.Video, UploadFileRules.FittingKinds(kinds, UploadFileClass.Video))!.Key);
        Assert.Equal("music", UploadFileRules.MostLikely(UploadFileClass.Audio, UploadFileRules.FittingKinds(kinds, UploadFileClass.Audio))!.Key);
        Assert.Null(UploadFileRules.MostLikely(UploadFileClass.Image, []));
    }

    [Theory]
    [InlineData("Music", "music")]
    [InlineData("TV Shows", "TV Shows")]
    [InlineData("X", "X")]
    public void ALabelReadsInTheMiddleOfASentence(string label, string expected) => Assert.Equal(expected, UploadFileRules.InSentence(label));

    [Theory]
    [InlineData("no_import_folder", "ignored", "Set an Import folder first.", "/settings/libraries/import")]
    [InlineData("vault_not_available", "ignored", "Adding files to the Vault by drag and drop isn't available yet.", null)]
    [InlineData("some_new_reason", "That is too big.", "That is too big.", null)]
    [InlineData("no_managed_destination", "No folder yet.", "No folder yet.", "/settings/libraries/watch/movies")]
    [InlineData(null, null, "This file could not be added.", null)]
    public void EachRefusalReasonHasOnePlainMessage_AndALinkWhenThereIsAFix(string? code, string? said, string text, string? href)
    {
        var (shown, link, _) = UploadRefusalMessages.Describe(code, said, "/settings/libraries/watch/movies");

        Assert.Equal(text, shown);
        Assert.Equal(href, link);
    }
}
