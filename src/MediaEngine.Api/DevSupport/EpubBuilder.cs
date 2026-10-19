using System.IO.Compression;
using System.Text;

namespace MediaEngine.Api.DevSupport;

/// <summary>
/// Creates minimal valid EPUB files for development seeding.
/// Uses System.IO.Compression.ZipArchive (BCL — no external dependency).
/// </summary>
public static class EpubBuilder
{
    /// <summary>
    /// Generates a valid EPUB 3 file as a byte array.
    /// The resulting file passes <c>EpubProcessor.CanProcess</c>:
    ///   1) ZIP magic bytes (automatic from ZipArchive)
    ///   2) uncompressed <c>mimetype</c> entry = "application/epub+zip"
    ///   3) OPF package with dc:title, dc:creator, dc:identifier, dc:date, dc:description
    /// </summary>
    public static byte[] Create(
        string title,
        string author,
        string isbn,
        int year,
        string description,
        string? publisher = null,
        string language = "en",
        string[]? additionalAuthors = null,
        string? series = null,
        int? seriesPosition = null)
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            // 1. mimetype — MUST be first entry, stored (not compressed)
            AddEntry(archive, "mimetype", "application/epub+zip", CompressionLevel.NoCompression);

            // 2. META-INF/container.xml — points to the OPF
            AddEntry(archive, "META-INF/container.xml", ContainerXml());

            // 3. OEBPS/content.opf — OPF package with metadata
            AddEntry(archive, "OEBPS/content.opf", ContentOpf(title, author, isbn, year, description, publisher, language, additionalAuthors, series, seriesPosition));

            // 4. OEBPS/toc.ncx — minimal NCX for EPUB 2 compat
            AddEntry(archive, "OEBPS/toc.ncx", TocNcx(title, isbn));

            // 5. OEBPS/nav.xhtml — EPUB 3 navigation document
            AddEntry(archive, "OEBPS/nav.xhtml", NavXhtml(title));

            // 6. OEBPS/chapter1.xhtml — minimal chapter content
            AddEntry(archive, "OEBPS/chapter1.xhtml", ChapterXhtml(title, author));

            // 7. OEBPS/cover.svg — generated cover image
            AddEntry(archive, "OEBPS/cover.svg", CoverSvg(title, author, year));
        }

        return stream.ToArray();
    }

    // ── Entry helper ─────────────────────────────────────────────────────────

    // UTF-8 WITHOUT byte-order mark — the EPUB mimetype entry must begin with
    // the exact ASCII bytes "application/epub+zip" (no BOM prefix).
    private static readonly Encoding Utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    // A fixed entry time keeps the archive bytes identical between runs (the ingestion baseline compares content hashes).
    private static readonly DateTimeOffset FixedEntryTime = new(2024, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static void AddEntry(ZipArchive archive, string path, string content,
        CompressionLevel level = CompressionLevel.Fastest)
    {
        ZipArchiveEntry entry = archive.CreateEntry(path, level);
        entry.LastWriteTime = FixedEntryTime;
        using StreamWriter writer = new(entry.Open(), Utf8NoBom);
        writer.Write(content);
    }

    // ── XML templates ────────────────────────────────────────────────────────

    private static string ContainerXml() => """
        <?xml version="1.0" encoding="UTF-8"?>
        <container version="1.0" xmlns="urn:oasis:names:tc:opendocument:xmlns:container">
          <rootfiles>
            <rootfile full-path="OEBPS/content.opf" media-type="application/oebps-package+xml"/>
          </rootfiles>
        </container>
        """;

    private static string ContentOpf(string title, string author, string isbn,
        int year, string description, string? publisher,
        string language = "en", string[]? additionalAuthors = null,
        string? series = null, int? seriesPosition = null)
    {
        string pubNode = publisher is not null
            ? $"\n    <dc:publisher>{Escape(publisher)}</dc:publisher>"
            : "";

        // Additional dc:creator elements for co-authors.
        var extraCreators = new StringBuilder();
        if (additionalAuthors is not null)
        {
            foreach (string coAuthor in additionalAuthors)
            {
                extraCreators.AppendLine($"    <dc:creator>{Escape(coAuthor)}</dc:creator>");
            }
        }

        // Calibre-style series metadata (widely used by ebook managers).
        string seriesNodes = "";
        if (series is not null)
        {
            seriesNodes = $"""

                <meta name="calibre:series" content="{Escape(series)}"/>
                <meta name="calibre:series_index" content="{seriesPosition ?? 1}"/>
            """;
        }

        // ISBN identifier — use a UUID fallback when ISBN is empty.
        string identifierValue = !string.IsNullOrEmpty(isbn)
            ? $"urn:isbn:{isbn}"
            : $"urn:uuid:{Guid.NewGuid()}";

        return $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <package xmlns="http://www.idpf.org/2007/opf" version="3.0" unique-identifier="bookid">
              <metadata xmlns:dc="http://purl.org/dc/elements/1.1/">
                <dc:title>{Escape(title)}</dc:title>
                <dc:creator>{Escape(author)}</dc:creator>
            {extraCreators.ToString().TrimEnd()}
                <dc:identifier id="bookid">{identifierValue}</dc:identifier>
                <dc:language>{Escape(language)}</dc:language>
                <dc:date>{(year > 0 ? $"{year}-01-01" : "")}</dc:date>
                <dc:description>{Escape(description)}</dc:description>{pubNode}{seriesNodes}
              </metadata>
              <manifest>
                <item id="nav" href="nav.xhtml" media-type="application/xhtml+xml" properties="nav"/>
                <item id="ncx" href="toc.ncx" media-type="application/x-dtbncx+xml"/>
                <item id="ch1" href="chapter1.xhtml" media-type="application/xhtml+xml"/>
                <item id="cover" href="cover.svg" media-type="image/svg+xml" properties="cover-image"/>
              </manifest>
              <spine toc="ncx">
                <itemref idref="ch1"/>
              </spine>
            </package>
            """;
    }

    private static string TocNcx(string title, string isbn) => $"""
        <?xml version="1.0" encoding="UTF-8"?>
        <ncx xmlns="http://www.daisy.org/z3986/2005/ncx/" version="2005-1">
          <head><meta name="dtb:uid" content="urn:isbn:{isbn}"/></head>
          <docTitle><text>{Escape(title)}</text></docTitle>
          <navMap>
            <navPoint id="ch1" playOrder="1">
              <navLabel><text>Chapter 1</text></navLabel>
              <content src="chapter1.xhtml"/>
            </navPoint>
          </navMap>
        </ncx>
        """;

    private static string NavXhtml(string title) => $"""
        <?xml version="1.0" encoding="UTF-8"?>
        <html xmlns="http://www.w3.org/1999/xhtml" xmlns:epub="http://www.idpf.org/2007/ops">
        <head><title>{Escape(title)}</title></head>
        <body>
          <nav epub:type="toc">
            <ol><li><a href="chapter1.xhtml">Chapter 1</a></li></ol>
          </nav>
        </body>
        </html>
        """;

    private static string ChapterXhtml(string title, string author) => $"""
        <?xml version="1.0" encoding="UTF-8"?>
        <html xmlns="http://www.w3.org/1999/xhtml">
        <head><title>{Escape(title)}</title></head>
        <body>
          <h1>{Escape(title)}</h1>
          <p>By {Escape(author)}</p>
          <p>This is a development seed file generated by Tuvima Library.</p>
        </body>
        </html>
        """;

    private static string CoverSvg(string title, string author, int year)
    {
        // Generate a unique but deterministic colour from the title hash.
        // string.GetHashCode is randomised per process, so a stable digest keeps the cover (and the EPUB bytes) identical between runs.
        uint hash = BitConverter.ToUInt32(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(title)), 0);
        int hue = (int)(hash % 360);
        string colour1 = $"hsl({hue}, 60%, 30%)";
        string colour2 = $"hsl({(hue + 40) % 360}, 50%, 20%)";

        // Word-wrap the title for display.
        string titleLines = WrapSvgText(title, 18, yStart: 340, lineHeight: 48, fontClass: "title");

        // CSS style block — uses curly braces that conflict with string interpolation,
        // so we build it separately as a non-interpolated string.
        const string styleBlock = """
                <style>
                  .title  { font: bold 40px 'Montserrat', 'Segoe UI', sans-serif; fill: #fff; text-anchor: middle; }
                  .author { font: 300 24px 'Montserrat', 'Segoe UI', sans-serif; fill: rgba(255,255,255,0.75); text-anchor: middle; }
                  .year   { font: 500 20px 'Montserrat', 'Segoe UI', sans-serif; fill: rgba(255,255,255,0.50); text-anchor: middle; }
                </style>
        """;

        return $"""
            <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 600 900" width="600" height="900">
              <defs>
                <linearGradient id="bg" x1="0" y1="0" x2="1" y2="1">
                  <stop offset="0%" stop-color="{colour1}"/>
                  <stop offset="100%" stop-color="{colour2}"/>
                </linearGradient>
                {styleBlock}
              </defs>
              <rect width="600" height="900" fill="url(#bg)" rx="0"/>
              <rect x="40" y="40" width="520" height="820" rx="12" fill="none" stroke="rgba(255,255,255,0.15)" stroke-width="2"/>
              {titleLines}
              <text x="300" y="580" class="author">{Escape(author)}</text>
              <text x="300" y="640" class="year">{year}</text>
            </svg>
            """;
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static string WrapSvgText(string text, int maxCharsPerLine, int yStart, int lineHeight, string fontClass)
    {
        var words = text.Split(' ');
        var lines = new List<string>();
        var current = new StringBuilder();

        foreach (string word in words)
        {
            if (current.Length > 0 && current.Length + 1 + word.Length > maxCharsPerLine)
            {
                lines.Add(current.ToString());
                current.Clear();
            }
            if (current.Length > 0)
            {
                current.Append(' ');
            }

            current.Append(word);
        }
        if (current.Length > 0)
        {
            lines.Add(current.ToString());
        }

        var sb = new StringBuilder();
        for (int i = 0; i < lines.Count; i++)
        {
            int y = yStart + i * lineHeight;
            sb.AppendLine($"""<text x="300" y="{y}" class="{fontClass}">{Escape(lines[i])}</text>""");
        }
        return sb.ToString().TrimEnd();
    }

    private static string Escape(string value) =>
        value.Replace("&", "&amp;")
             .Replace("<", "&lt;")
             .Replace(">", "&gt;")
             .Replace("\"", "&quot;")
             .Replace("'", "&apos;");
}
