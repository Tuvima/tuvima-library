using System.Formats.Tar;
using System.IO.Compression;
using System.Text;
using MediaEngine.Api.Services.Reading;
using MediaEngine.Domain.Configuration;
using SkiaSharp;

namespace MediaEngine.Api.Tests;

/// <summary>PDF and comic reader spec WP1: page lists and page pictures for CBZ, CBR and CB7.</summary>
public sealed class ComicPageServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"tuvima_comic_pages_{Guid.NewGuid():N}");
    private readonly ReaderConfiguration _configuration = new();

    public ComicPageServiceTests() => Directory.CreateDirectory(_root);

    private ComicPageService NewService() => new(() => _configuration, Path.Combine(_root, "cache"));

    [Fact]
    public async Task Cbz_ListsPagesInNaturalOrderWithSizes()
    {
        var path = WriteZip("issue.cbz", ("page10.jpg", Jpeg(100, 150)), ("page2.jpg", Jpeg(200, 300)), ("page1.jpg", Jpeg(300, 450)));

        var list = await NewService().GetPageListAsync(path, CancellationToken.None);

        Assert.Equal(3, list.PageCount);
        Assert.Equal([300, 200, 100], list.Pages.Select(p => p.Width));
        Assert.Equal([1, 2, 3], list.Pages.Select(p => p.Number));
        Assert.False(list.RightToLeft);
    }

    [Fact]
    public async Task NonPictureEntriesAndSystemLeftovers_AreNeverPages()
    {
        var path = WriteZip("issue.cbz",
            ("001.jpg", Jpeg(100, 150)),
            ("notes.txt", Encoding.UTF8.GetBytes("hello")),
            ("__MACOSX/001.jpg", Jpeg(10, 10)),
            ("._002.jpg", Jpeg(10, 10)),
            ("script.exe", new byte[] { 1, 2, 3 }),
            ("002.png", Png(100, 150)));

        var list = await NewService().GetPageListAsync(path, CancellationToken.None);

        Assert.Equal(2, list.PageCount);
    }

    [Fact]
    public async Task ComicInfo_SetsRightToLeftAndMovesTheFrontCoverFirst()
    {
        var info = """<ComicInfo><Manga>YesAndRightToLeft</Manga><Pages><Page Image="1" Type="FrontCover"/></Pages></ComicInfo>""";
        var path = WriteZip("manga.cbz", ("a.jpg", Jpeg(100, 150)), ("b.jpg", Jpeg(111, 150)), ("ComicInfo.xml", Encoding.UTF8.GetBytes(info)));

        var list = await NewService().GetPageListAsync(path, CancellationToken.None);

        Assert.True(list.RightToLeft);
        Assert.Equal(111, list.Pages[0].Width);
    }

    [Fact]
    public async Task Page_IsMadeAtTheAskedWidth_AndOriginalWhenSmallerOrZero()
    {
        var path = WriteZip("issue.cbz", ("1.jpg", Jpeg(1000, 1500)));
        var service = NewService();

        var sized = await service.GetPageAsync(path, 1, 480, CancellationToken.None);
        var original = await service.GetPageAsync(path, 1, 0, CancellationToken.None);
        var large = await service.GetPageAsync(path, 1, 1440, CancellationToken.None);

        using var bitmap = SKBitmap.Decode(sized.Bytes);
        Assert.Equal(480, bitmap.Width);
        Assert.Equal(720, bitmap.Height);
        Assert.Equal("image/jpeg", sized.ContentType);
        Assert.Equal(1000, SKBitmap.Decode(original.Bytes).Width);
        Assert.Equal(1000, SKBitmap.Decode(large.Bytes).Width);
    }

    [Fact]
    public async Task WidthOutsideTheAllowedSet_AndMissingPage_AreRefused()
    {
        var path = WriteZip("issue.cbz", ("1.jpg", Jpeg(100, 150)));
        var service = NewService();

        var width = await Assert.ThrowsAsync<ComicPageException>(() => service.GetPageAsync(path, 1, 123, CancellationToken.None));
        var missing = await Assert.ThrowsAsync<ComicPageException>(() => service.GetPageAsync(path, 2, 0, CancellationToken.None));

        Assert.Equal(ComicPageFailure.BadWidth, width.Failure);
        Assert.Equal(ComicPageFailure.NoSuchPage, missing.Failure);
    }

    [Fact]
    public async Task ArchiveOverTheEntryLimit_IsRefused()
    {
        _configuration.Comic.MaxEntries = 2;
        var path = WriteZip("big.cbz", ("1.jpg", Jpeg(10, 10)), ("2.jpg", Jpeg(10, 10)), ("3.jpg", Jpeg(10, 10)), ("4.jpg", Jpeg(10, 10)));

        var failure = await Assert.ThrowsAsync<ComicPageException>(() => NewService().GetPageListAsync(path, CancellationToken.None));

        Assert.Equal(ComicPageFailure.Refused, failure.Failure);
    }

    [Fact]
    public async Task PageOverThePixelLimit_IsRefused()
    {
        _configuration.Comic.MaxPageMegapixels = 1;
        var path = WriteZip("huge.cbz", ("1.png", Png(1500, 1500)));

        var failure = await Assert.ThrowsAsync<ComicPageException>(() => NewService().GetPageListAsync(path, CancellationToken.None));

        Assert.Equal(ComicPageFailure.Refused, failure.Failure);
    }

    [Fact]
    public async Task ArchiveWithNoPictures_AndAFileThatIsNotAnArchive_CannotBeOpened()
    {
        var empty = WriteZip("empty.cbz", ("notes.txt", Encoding.UTF8.GetBytes("x")));
        var garbage = Path.Combine(_root, "garbage.cbr");
        File.WriteAllBytes(garbage, Enumerable.Range(0, 500).Select(i => (byte)(i % 200)).ToArray());
        var service = NewService();

        var first = await Assert.ThrowsAsync<ComicPageException>(() => service.GetPageListAsync(empty, CancellationToken.None));
        var second = await Assert.ThrowsAsync<ComicPageException>(() => service.GetPageListAsync(garbage, CancellationToken.None));

        Assert.Equal(ComicPageFailure.Unreadable, first.Failure);
        Assert.Equal(ComicPageFailure.Unreadable, second.Failure);
    }

    [Fact]
    public async Task NonZipArchive_IsUnpackedOnceAndGivesTheSamePagesAsZip()
    {
        var pages = new[] { ("p10.jpg", Jpeg(100, 150)), ("p2.jpg", Jpeg(200, 300)), ("p1.jpg", Jpeg(300, 450)), ("readme.txt", new byte[] { 1 }) };
        var zip = WriteZip("same.cbz", pages);
        var tar = WriteTar("same.cb7", pages); // a stand-in for a CBR or CB7: any archive that is not a zip takes the unpack path
        var service = NewService();

        var fromZip = await service.GetPageListAsync(zip, CancellationToken.None);
        var fromTar = await service.GetPageListAsync(tar, CancellationToken.None);
        var picture = await service.GetPageAsync(tar, 2, 160, CancellationToken.None);

        Assert.Equal(fromZip.Pages.Select(p => (p.Number, p.Width, p.Height)), fromTar.Pages.Select(p => (p.Number, p.Width, p.Height)));
        Assert.Equal(160, SKBitmap.Decode(picture.Bytes).Width);
        Assert.True(Directory.EnumerateDirectories(Path.Combine(_root, "cache")).Any());
    }

    [Fact]
    public async Task UnpackCache_ClearsTheOldestComicPastItsSizeLimit()
    {
        _configuration.Comic.CacheSizeMb = 1;
        var service = NewService();
        var first = WriteTar("one.cb7", ("1.png", Png(900, 900, noisy: true)));
        var second = WriteTar("two.cb7", ("1.png", Png(900, 900, noisy: true)));

        await service.GetPageListAsync(first, CancellationToken.None);
        await Task.Delay(50);
        await service.GetPageListAsync(second, CancellationToken.None);

        Assert.Single(Directory.EnumerateDirectories(Path.Combine(_root, "cache")));
    }

    [Fact]
    public async Task NonZipArchiveOverTheUnpackLimit_IsRefusedBeforeAnythingIsWritten()
    {
        _configuration.Comic.MaxUnpackedMegabytes = 1;
        var tar = WriteTar("big.cb7", ("1.png", Png(900, 900, noisy: true)), ("2.png", Png(900, 900, noisy: true)));

        var failure = await Assert.ThrowsAsync<ComicPageException>(() => NewService().GetPageListAsync(tar, CancellationToken.None));

        Assert.Equal(ComicPageFailure.Refused, failure.Failure);
        Assert.False(Directory.Exists(Path.Combine(_root, "cache")) && Directory.EnumerateDirectories(Path.Combine(_root, "cache")).Any());
    }

    // ── helpers ──────────────────────────────────────────────────────────

    internal static byte[] Jpeg(int width, int height) => Encode(width, height, SKEncodedImageFormat.Jpeg, noisy: false);

    internal static byte[] Png(int width, int height, bool noisy = false) => Encode(width, height, SKEncodedImageFormat.Png, noisy);

    private static byte[] Encode(int width, int height, SKEncodedImageFormat format, bool noisy)
    {
        using var bitmap = new SKBitmap(width, height);
        if (noisy)
        {
            var random = new Random(7);
            var pixels = new byte[width * height * 4];
            random.NextBytes(pixels);
            System.Runtime.InteropServices.Marshal.Copy(pixels, 0, bitmap.GetPixels(), pixels.Length);
        }
        else
        {
            bitmap.Erase(new SKColor(30, 120, 200));
        }

        using var image = SKImage.FromBitmap(bitmap);
        return image.Encode(format, 90).ToArray();
    }

    private string WriteZip(string name, params (string Name, byte[] Bytes)[] entries)
    {
        var path = Path.Combine(_root, name);
        using var stream = File.Create(path);
        using var zip = new ZipArchive(stream, ZipArchiveMode.Create);
        foreach (var (entryName, bytes) in entries)
        {
            using var entry = zip.CreateEntry(entryName, CompressionLevel.NoCompression).Open();
            entry.Write(bytes);
        }

        return path;
    }

    private string WriteTar(string name, params (string Name, byte[] Bytes)[] entries)
    {
        var path = Path.Combine(_root, name);
        using var stream = File.Create(path);
        using var tar = new TarWriter(stream);
        foreach (var (entryName, bytes) in entries)
        {
            tar.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, entryName) { DataStream = new MemoryStream(bytes) });
        }

        return path;
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { /* temporary files; the machine clears them later */ }
    }
}
