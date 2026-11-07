using System.IO.Compression;
using System.Text;
using MediaEngine.Api.Services.Playback;
using MediaEngine.Contracts.Playback;
using MediaEngine.Domain.Services;

namespace MediaEngine.Api.Tests;

// The show bookmark file (universal editor v5 D37, screen Q2) is read from a file anyone could have made, so it gets the single-title
// file's safety checks: one manifest and one payload and nothing else but small pictures, no path that climbs out, no entry that
// expands past its ratio or size, no file over the limit, and no episode named twice. A picture that is not a real small JPEG is ignored
// rather than trusted; it never fails the file.
public sealed class BookmarkShowPackFileTests
{
    private static readonly byte[] Jpeg = [0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46, 0xFF, 0xD9];

    private const string Manifest = """
        {"schema_version":"1.0","kind":"bookmark-show-pack","includes_secrets":false}
        """;

    private static string Payload(params (int Season, int Episode)[] episodes) =>
        "{\"format\":\"bookmark-show-pack\",\"version\":1,\"name\":\"A show\",\"episodes\":["
        + string.Join(',', episodes.Select(episode =>
            $"{{\"season\":{episode.Season},\"episode\":{episode.Episode},\"bookmarks\":[{{\"time\":30,\"note\":\"scene\"}}]}}"))
        + "]}";

    private static byte[] Zip(params (string Name, byte[] Content)[] entries)
    {
        using var buffer = new MemoryStream();
        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, content) in entries)
            {
                using var stream = archive.CreateEntry(name, CompressionLevel.SmallestSize).Open();
                stream.Write(content, 0, content.Length);
            }
        }

        return buffer.ToArray();
    }

    private static (string, byte[]) Text(string name, string text) => (name, Encoding.UTF8.GetBytes(text));

    private static byte[] Good(params (string Name, byte[] Content)[] extra) =>
        Zip([Text("manifest.json", Manifest), Text("pack/episodes.json", Payload((1, 1))), .. extra]);

    private static BookmarkShowPackContent Content(params BookmarkPackBookmark[] pilot) =>
        new("A show", [new BookmarkShowPackEpisode(1, 1, "Pilot", null, pilot)]);

    private static void AssertRefused(byte[] bytes, string? message = null)
    {
        var refused = Assert.Throws<BookmarkPackInvalidException>(() => BookmarkShowPackArchive.Read(bytes));
        if (message is not null)
        {
            Assert.Equal(message, refused.Message);
        }
    }

    [Fact]
    public void AShowFile_ReadsBackEachEpisodeBySeasonAndEpisodeNumber()
    {
        var written = BookmarkShowPackArchive.Write(
            new BookmarkShowPackContent("A show",
            [
                new BookmarkShowPackEpisode(1, 2, "Second", 2400, [new BookmarkPackBookmark(30, null, null, null, "scene", "words")]),
                new BookmarkShowPackEpisode(3, 1, null, null, [new BookmarkPackBookmark(60, 90, "Opening", null, null, null)]),
            ]),
            DateTimeOffset.UtcNow);

        var read = BookmarkShowPackArchive.Read(written);

        Assert.Equal([(1, 2), (3, 1)], read.Episodes.Select(episode => (episode.SeasonNumber, episode.EpisodeNumber)));
        Assert.Equal("words", read.Episodes[0].Bookmarks[0].Description);
        Assert.Equal(90, read.Episodes[1].Bookmarks[0].EndTime);
    }

    [Fact]
    public void AnEntryThatIsNeitherTheManifestNorThePayloadNorAPicture_IsRefused()
    {
        AssertRefused(Good(Text("pack/extra.txt", "hello")), "This is not a show bookmark file.");
        AssertRefused(Good(Text("notes.json", "{}")), "This is not a show bookmark file.");
    }

    [Theory]
    [InlineData("../episodes.json")]
    [InlineData("pack/../../escape.jpg")]
    [InlineData("/etc/passwd")]
    [InlineData("pack\\..\\..\\escape")]
    public void AnEntryWhosePathClimbsOutOfTheFile_IsRefused(string path)
    {
        AssertRefused(Zip(Text("manifest.json", Manifest), Text("pack/episodes.json", Payload((1, 1))), Text(path, "x")));
    }

    [Fact]
    public void AnEntryThatExpandsFarPastItsCompressedSize_IsRefusedAsNotSafe()
    {
        // 6 MB of one repeated character compresses to a few kilobytes: a ratio far past what real bookmark text reaches.
        var bomb = new string(' ', 6 * 1024 * 1024);
        var payload = Payload((1, 1)).Replace("\"name\":\"A show\"", $"\"name\":\"A show\",\"padding\":\"{bomb}\"", StringComparison.Ordinal);

        AssertRefused(Zip(Text("manifest.json", Manifest), Text("pack/episodes.json", payload)), "This show bookmark file is not safe to open.");
    }

    [Fact]
    public void AFileOverTheSizeLimit_IsRefusedBeforeItIsOpened()
    {
        var tooLarge = new byte[EditorBookmarkLimits.MaximumShowFileBytes + 1];

        AssertRefused(tooLarge, "This is not a show bookmark file, or it is too large.");
        AssertRefused([], "This is not a show bookmark file, or it is too large.");
    }

    [Fact]
    public void AnEpisodeNamedTwice_IsRefused()
    {
        var bytes = Zip(Text("manifest.json", Manifest), Text("pack/episodes.json", Payload((1, 1), (1, 2), (1, 1))));

        AssertRefused(bytes, "This show bookmark file has an episode that is not valid.");
    }

    [Fact]
    public void ATwiceNamedEntry_IsRefused()
    {
        // The same entry name twice would let a reader and a checker see different files.
        AssertRefused(Zip(Text("manifest.json", Manifest), Text("pack/episodes.json", Payload((1, 1))), Text("pack/episodes.json", Payload((1, 1)))));
    }

    [Fact]
    public void AManifestFromAnotherKindOrAsHoldingSecrets_IsRefused()
    {
        AssertRefused(
            Zip(Text("manifest.json", Manifest.Replace("bookmark-show-pack", "bookmark-pack", StringComparison.Ordinal)), Text("pack/episodes.json", Payload((1, 1)))),
            "This show bookmark file was made by a version that cannot be read here.");
        AssertRefused(
            Zip(Text("manifest.json", Manifest.Replace("false", "true", StringComparison.Ordinal)), Text("pack/episodes.json", Payload((1, 1)))),
            "This show bookmark file was made by a version that cannot be read here.");
    }

    [Fact]
    public void APictureOnABookmark_TravelsInTheFile_AndComesBackForTheSameBookmark()
    {
        var bytes = BookmarkShowPackArchive.Write(
            Content(new BookmarkPackBookmark(30, null, null, null, "a", null), new BookmarkPackBookmark(60, null, null, null, "b", null, Frame: Jpeg)),
            DateTimeOffset.UtcNow);

        var read = BookmarkShowPackArchive.Read(bytes);

        var bookmarks = read.Episodes[0].Bookmarks;
        Assert.Null(bookmarks[0].Frame);
        Assert.Equal(Jpeg, bookmarks[1].Frame);
        using var archive = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);
        Assert.Contains(archive.Entries, entry => entry.FullName == "pack/frames/1-1-1.jpg");
    }

    [Fact]
    public void APictureThatIsNotARealSmallJpeg_IsIgnored_AndNeverFailsTheFile()
    {
        var notAJpeg = Encoding.UTF8.GetBytes("this is not a picture at all");
        var tooBig = new byte[BookmarkPackLimits.MaximumFrameBytes + 1];
        tooBig[0] = 0xFF; tooBig[1] = 0xD8; tooBig[2] = 0xFF; tooBig[^2] = 0xFF; tooBig[^1] = 0xD9;

        var withFake = Good(("pack/frames/1-1-0.jpg", notAJpeg));
        var read = BookmarkShowPackArchive.Read(withFake);
        Assert.Null(read.Episodes[0].Bookmarks[0].Frame);

        // A picture over the size limit is not trusted at all: the file is refused, not just the picture.
        AssertRefused(Good(("pack/frames/1-1-0.jpg", tooBig)), "This show bookmark file is not safe to open.");
    }

    [Fact]
    public void PicturesBeyondTheFilesBudget_AreLeftOutOnWrite_AndTheFileStillReads()
    {
        var picture = new byte[90 * 1024];
        picture[0] = 0xFF; picture[1] = 0xD8; picture[2] = 0xFF; picture[^2] = 0xFF; picture[^1] = 0xD9;
        var count = (int)(EditorBookmarkLimits.MaximumShowFramesBytes / picture.Length) + 4;
        var bookmarks = Enumerable.Range(0, count)
            .Select(index => new BookmarkPackBookmark(index + 1, null, null, null, $"b{index}", null, Frame: picture))
            .ToArray();

        var bytes = BookmarkShowPackArchive.Write(Content(bookmarks), DateTimeOffset.UtcNow);

        using var archive = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);
        var framed = archive.Entries.Count(entry => entry.FullName.StartsWith(BookmarkShowPackArchive.FramePrefix, StringComparison.Ordinal));
        Assert.InRange(framed, 1, count - 1);
        Assert.True(framed * picture.Length <= EditorBookmarkLimits.MaximumShowFramesBytes);
        Assert.Equal(count, BookmarkShowPackArchive.Read(bytes).Episodes[0].Bookmarks.Count);
    }
}
