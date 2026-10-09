using System.IO.Compression;
using MediaEngine.Processors.Processors;
namespace MediaEngine.Processors.Tests;

public class EpubIdentifierTests
{
    [Theory]
    [InlineData("")]
    [InlineData(" opf:scheme=\"ISBN\"")]
    public async Task InvalidFirstIdentifier_DoesNotHideValidIsbn(string scheme)
    {
        var path = Path.Combine(Path.GetTempPath(), $"isbn-{Guid.NewGuid():N}.epub");
        try
        {
            using (var archive = ZipFile.Open(path, ZipArchiveMode.Create))
            {
                void Add(string name, string text)
                {
                    using var writer = new StreamWriter(archive.CreateEntry(name).Open());
                    writer.Write(text);
                }
                Add("mimetype", "application/epub+zip");
                Add("META-INF/container.xml", """
                    <container version="1.0" xmlns="urn:oasis:names:tc:opendocument:xmlns:container"><rootfiles><rootfile full-path="content.opf" media-type="application/oebps-package+xml"/></rootfiles></container>
                    """);
                Add("content.opf", $$"""
                    <package xmlns="http://www.idpf.org/2007/opf" version="2.0" unique-identifier="uid">
                    <metadata xmlns:dc="http://purl.org/dc/elements/1.1/" xmlns:opf="http://www.idpf.org/2007/opf">
                    <dc:title>Example</dc:title><dc:language>en</dc:language>
                    <dc:identifier id="uid"{{scheme}}>3282476326</dc:identifier>
                    <dc:identifier>9781542016421</dc:identifier></metadata>
                    <manifest><item id="ncx" href="toc.ncx" media-type="application/x-dtbncx+xml"/><item id="chapter" href="chapter.xhtml" media-type="application/xhtml+xml"/></manifest><spine toc="ncx"><itemref idref="chapter"/></spine></package>
                    """);
                Add("toc.ncx", """<ncx xmlns="http://www.daisy.org/z3986/2005/ncx/" version="2005-1"><head/><docTitle><text>Example</text></docTitle><navMap><navPoint id="c1" playOrder="1"><navLabel><text>Chapter</text></navLabel><content src="chapter.xhtml"/></navPoint></navMap></ncx>""");
                Add("chapter.xhtml", "<html xmlns=\"http://www.w3.org/1999/xhtml\"><head><title>Example</title></head><body><p>Test</p></body></html>");
            }
            await File.WriteAllTextAsync(Path.ChangeExtension(path, ".opf"), """
                <package xmlns:dc="http://purl.org/dc/elements/1.1/" xmlns:opf="http://www.idpf.org/2007/opf"><metadata>
                <dc:identifier opf:scheme="uuid">4a40febf-b65d-4ea7-810d-8317f0706a88</dc:identifier>
                <dc:identifier opf:scheme="ISBN">9781101972670</dc:identifier>
                </metadata></package>
                """);
            var result = await new EpubProcessor().ProcessAsync(path);
            Assert.False(result.IsCorrupt, result.CorruptReason);
            Assert.Contains(result.Claims, claim => claim.Key == "isbn" && claim.Value == "9781542016421");
            Assert.DoesNotContain(result.Claims, claim => claim.Key == "isbn" && claim.Value == "3282476326");
            Assert.Contains(result.Claims, claim => claim.Key == "calibre_uuid" && claim.Value == "4a40febf-b65d-4ea7-810d-8317f0706a88");
            Assert.Contains(result.Claims, claim => claim.Key == "isbn" && claim.Value == "9781101972670");
        }
        finally { File.Delete(path); File.Delete(Path.ChangeExtension(path, ".opf")); }
    }
}
