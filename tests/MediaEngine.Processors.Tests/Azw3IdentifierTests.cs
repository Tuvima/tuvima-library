using MediaEngine.Processors.Processors;

namespace MediaEngine.Processors.Tests;

public sealed class Azw3IdentifierTests
{
    [Fact]
    public async Task CompanionReadsTypedIdentifiersInsteadOfCalibreRowNumber()
    {
        var folder = Path.Combine(Path.GetTempPath(), "azw3-" + Guid.NewGuid());
        Directory.CreateDirectory(folder);
        try
        {
            var path = Path.Combine(folder, "Book.azw3");
            var bytes = new byte[68];
            "BOOKMOBI"u8.CopyTo(bytes.AsSpan(60));
            await File.WriteAllBytesAsync(path, bytes);
            await File.WriteAllTextAsync(Path.Combine(folder, "metadata.opf"), """
                <package xmlns:dc="http://purl.org/dc/elements/1.1/" xmlns:opf="http://www.idpf.org/2007/opf"><metadata>
                <dc:title>Full book title</dc:title><dc:creator>Example Author</dc:creator>
                <dc:identifier opf:scheme="calibre">81</dc:identifier>
                <dc:identifier opf:scheme="uuid">4a40febf-b65d-4ea7-810d-8317f0706a88</dc:identifier>
                <dc:identifier opf:scheme="ISBN">3282476326</dc:identifier>
                <dc:identifier opf:scheme="ISBN">9781101972670</dc:identifier>
                <dc:identifier opf:scheme="AMAZON">0451493249</dc:identifier>
                <dc:identifier opf:scheme="GOODREADS">37506348</dc:identifier>
                </metadata></package>
                """);
            var result = await new AzW3Processor().ProcessAsync(path);
            Assert.Contains(result.Claims, c => c.Key == "isbn" && c.Value == "9781101972670");
            Assert.Contains(result.Claims, c => c.Key == "calibre_uuid" && c.Value == "4a40febf-b65d-4ea7-810d-8317f0706a88");
            Assert.Contains(result.Claims, c => c.Key == "asin" && c.Value == "0451493249");
            Assert.Contains(result.Claims, c => c.Key == "reader_support" && c.Value == "external");
            Assert.DoesNotContain(result.Claims, c => c.Value == "81" || c.Value == "3282476326");
            File.Move(Path.Combine(folder, "metadata.opf"), Path.Combine(folder, "Unrelated.opf"));
            var unrelated = await new AzW3Processor().ProcessAsync(path);
            Assert.DoesNotContain(unrelated.Claims, c => c.Key == "isbn" || c.Key == "calibre_uuid");
        }
        finally { Directory.Delete(folder, true); }
    }
}
