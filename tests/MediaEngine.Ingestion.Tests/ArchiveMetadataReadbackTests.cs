using System.IO.Compression;
using System.Text;
using System.Xml.Linq;
using MediaEngine.Ingestion.Contracts;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaEngine.Ingestion.Tests;

public sealed class ArchiveMetadataReadbackTests
{
    private static readonly byte[] EpubChapter = Encoding.UTF8.GetBytes("<html><body>Keep this chapter.</body></html>");
    private static readonly byte[] ComicPage = [0x89, 0x50, 0x4E, 0x47, 0x01, 0x02, 0x03];

    [Fact]
    public async Task Epub_DublinCoreReadbackPreservesUnrelatedEntriesAndXml()
    {
        var path = CreateEpub();
        try
        {
            IMetadataTagger tagger = new EpubMetadataTagger(NullLogger<EpubMetadataTagger>.Instance);
            var tags = new Dictionary<string, string>
            {
                ["title"] = "New title", ["author"] = "New author",
                ["publisher"] = "New publisher", ["year"] = "2026",
            };
            await tagger.WriteTagsAsync(path, tags);
            var readback = await tagger.VerifyTagsAsync(path, tags);
            Assert.True(readback.IsVerified, readback.Reason);
            Assert.False(File.Exists(path + BackedUpMetadataTagger.BackupSuffix));
            Assert.False((await tagger.VerifyTagsAsync(path,
                new Dictionary<string, string>(tags) { ["title"] = "Wrong title" })).IsVerified);

            using var zip = ZipFile.OpenRead(path);
            Assert.Equal(EpubChapter, ReadEntry(zip, "OEBPS/chapter.xhtml"));
            Assert.Equal([0xFF, 0xD8, 0xFF, 0xD9], ReadEntry(zip, "OEBPS/cover.jpg"));
            var opf = XDocument.Load(zip.GetEntry("OEBPS/content.opf")!.Open());
            Assert.Equal("keep", opf.Descendants().Single(e => e.Name.LocalName == "meta"
                && (string?)e.Attribute("name") == "unrelated").Attribute("content")?.Value);
            Assert.Equal("chapter.xhtml", opf.Descendants().Single(e => (string?)e.Attribute("id") == "chapter")
                .Attribute("href")?.Value);
        }
        finally { Delete(path); }
    }

    [Fact]
    public async Task Epub_MissingPackageReadbackRestoresExactArchiveBytes()
    {
        var path = CreateEpub(opfPathInContainer: "OEBPS/missing.opf");
        try
        {
            var before = await File.ReadAllBytesAsync(path);
            var tagger = new EpubMetadataTagger(NullLogger<EpubMetadataTagger>.Instance);
            await Assert.ThrowsAsync<InvalidDataException>(() => tagger.WriteTagsAsync(path,
                new Dictionary<string, string> { ["title"] = "Cannot verify" }));
            Assert.Equal(before, await File.ReadAllBytesAsync(path));
            Assert.True(File.Exists(path + BackedUpMetadataTagger.BackupSuffix));
        }
        finally { Delete(path); }
    }

    [Fact]
    public async Task Epub_CustomOpfKeyRemainsUnverified()
    {
        var path = CreateEpub();
        try
        {
            IMetadataTagger tagger = new EpubMetadataTagger(NullLogger<EpubMetadataTagger>.Instance);
            var tags = new Dictionary<string, string> { ["isbn"] = "9781234567890" };
            await tagger.WriteTagsAsync(path, tags);
            Assert.False((await tagger.VerifyTagsAsync(path, tags)).IsVerified);
        }
        finally { Delete(path); }
    }

    [Fact]
    public async Task Epub_CoverManifestAndImageRoundTripPreservesChapter()
    {
        var path = CreateEpub();
        try
        {
            var cover = Convert.FromBase64String(
                "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+/aI8AAAAASUVORK5CYII=");
            var tagger = new EpubMetadataTagger(NullLogger<EpubMetadataTagger>.Instance);
            await tagger.WriteCoverArtAsync(path, cover);
            Assert.False(File.Exists(path + BackedUpMetadataTagger.BackupSuffix));

            using var zip = ZipFile.OpenRead(path);
            var opf = XDocument.Load(zip.GetEntry("OEBPS/content.opf")!.Open());
            var coverItem = opf.Descendants().Single(e => (string?)e.Attribute("id") == "cover");
            Assert.Equal("cover.png", coverItem.Attribute("href")?.Value);
            Assert.Equal("image/png", coverItem.Attribute("media-type")?.Value);
            Assert.Equal(cover, ReadEntry(zip, "OEBPS/cover.png"));
            Assert.Equal(EpubChapter, ReadEntry(zip, "OEBPS/chapter.xhtml"));
            Assert.Null(zip.GetEntry("OEBPS/cover.jpg"));
        }
        finally { Delete(path); }
    }

    [Fact]
    public async Task Epub_UnreferencedCoverRestoresExactArchiveBytes()
    {
        var path = CreateEpub(includeCoverManifestItem: false);
        try
        {
            var before = await File.ReadAllBytesAsync(path);
            var tagger = new EpubMetadataTagger(NullLogger<EpubMetadataTagger>.Instance);
            await Assert.ThrowsAsync<InvalidDataException>(() => tagger.WriteCoverArtAsync(path,
                [0xFF, 0xD8, 0xFF, 0xD9]));
            Assert.Equal(before, await File.ReadAllBytesAsync(path));
            Assert.True(File.Exists(path + BackedUpMetadataTagger.BackupSuffix));
        }
        finally { Delete(path); }
    }

    [Fact]
    public async Task Cbz_ComicInfoReadbackPreservesPagesAndUnrelatedXml()
    {
        var path = CreateCbz();
        try
        {
            IMetadataTagger tagger = new ComicMetadataTagger(NullLogger<ComicMetadataTagger>.Instance);
            var tags = new Dictionary<string, string>
            {
                ["title"] = "New title", ["author"] = "New writer", ["genre"] = "Fantasy",
                ["description"] = "New summary", ["series"] = "New series",
                ["series_position"] = "2", ["year"] = "2026", ["publisher"] = "New publisher",
                ["illustrator"] = "New penciller", ["page_count"] = "1",
            };
            await tagger.WriteTagsAsync(path, tags);
            var readback = await tagger.VerifyTagsAsync(path, tags);
            Assert.True(readback.IsVerified, readback.Reason);
            Assert.False(File.Exists(path + BackedUpMetadataTagger.BackupSuffix));
            Assert.False((await tagger.VerifyTagsAsync(path,
                new Dictionary<string, string>(tags) { ["title"] = "Wrong title" })).IsVerified);

            using var zip = ZipFile.OpenRead(path);
            Assert.Equal(ComicPage, ReadEntry(zip, "001.png"));
            Assert.Equal("Keep inker", XDocument.Load(zip.GetEntry("ComicInfo.xml")!.Open())
                .Root?.Element("Inker")?.Value);
        }
        finally { Delete(path); }
    }

    [Fact]
    public async Task Cbz_OmittedEmptyFieldReadbackRestoresExactArchiveBytes()
    {
        var path = CreateCbz();
        try
        {
            var before = await File.ReadAllBytesAsync(path);
            var tagger = new ComicMetadataTagger(NullLogger<ComicMetadataTagger>.Instance);
            await Assert.ThrowsAsync<InvalidDataException>(() => tagger.WriteTagsAsync(path,
                new Dictionary<string, string> { ["title"] = "" }));
            Assert.Equal(before, await File.ReadAllBytesAsync(path));
            Assert.True(File.Exists(path + BackedUpMetadataTagger.BackupSuffix));
        }
        finally { Delete(path); }
    }

    [Fact]
    public async Task Cbz_CustomIdentifierRemainsUnverified()
    {
        var path = CreateCbz();
        try
        {
            IMetadataTagger tagger = new ComicMetadataTagger(NullLogger<ComicMetadataTagger>.Instance);
            var tags = new Dictionary<string, string> { ["wikidata_qid"] = "Q123" };
            await tagger.WriteTagsAsync(path, tags);
            Assert.False((await tagger.VerifyTagsAsync(path, tags)).IsVerified);
        }
        finally { Delete(path); }
    }

    private static string CreateEpub(
        string opfPathInContainer = "OEBPS/content.opf", bool includeCoverManifestItem = true)
    {
        var path = Path.Combine(Path.GetTempPath(), $"tuvima_archive_{Guid.NewGuid():N}.epub");
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        Add(zip, "mimetype", "application/epub+zip");
        Add(zip, "META-INF/container.xml",
            $"<container><rootfiles><rootfile full-path=\"{opfPathInContainer}\" media-type=\"application/oebps-package+xml\"/></rootfiles></container>");
        var coverItem = includeCoverManifestItem
            ? "<item id=\"cover\" href=\"cover.jpg\" media-type=\"image/jpeg\" />"
            : string.Empty;
        Add(zip, "OEBPS/content.opf", $$"""
            <package xmlns="http://www.idpf.org/2007/opf" xmlns:dc="http://purl.org/dc/elements/1.1/">
              <metadata><dc:title>Old title</dc:title><dc:creator>Old author</dc:creator>
                <dc:publisher>Old publisher</dc:publisher><dc:date>2000</dc:date>
                <meta name="unrelated" content="keep" /></metadata>
              <manifest><item id="chapter" href="chapter.xhtml" media-type="application/xhtml+xml" />
                {{coverItem}}</manifest>
              <spine><itemref idref="chapter" /></spine>
            </package>
            """);
        Add(zip, "OEBPS/chapter.xhtml", EpubChapter);
        Add(zip, "OEBPS/cover.jpg", [0xFF, 0xD8, 0xFF, 0xD9]);
        return path;
    }

    private static string CreateCbz()
    {
        var path = Path.Combine(Path.GetTempPath(), $"tuvima_archive_{Guid.NewGuid():N}.cbz");
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        Add(zip, "001.png", ComicPage);
        Add(zip, "ComicInfo.xml", "<ComicInfo><Title>Old title</Title><Inker>Keep inker</Inker></ComicInfo>");
        return path;
    }

    private static void Add(ZipArchive zip, string name, string content) =>
        Add(zip, name, Encoding.UTF8.GetBytes(content));

    private static void Add(ZipArchive zip, string name, byte[] bytes)
    {
        using var stream = zip.CreateEntry(name, CompressionLevel.NoCompression).Open();
        stream.Write(bytes);
    }

    private static byte[] ReadEntry(ZipArchive zip, string name)
    {
        using var stream = zip.GetEntry(name)!.Open();
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }

    private static void Delete(string path)
    {
        if (File.Exists(path)) File.Delete(path);
        if (File.Exists(path + BackedUpMetadataTagger.BackupSuffix))
            File.Delete(path + BackedUpMetadataTagger.BackupSuffix);
    }
}
