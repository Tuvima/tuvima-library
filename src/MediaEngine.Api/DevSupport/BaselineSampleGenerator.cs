using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using SkiaSharp;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;

namespace MediaEngine.Api.DevSupport;

/// <summary>
/// Writes the deterministic sample library of the ingestion baseline: the same bytes and file times on every
/// run, no network downloads. Folder names match the real-media harness (books, audiobooks, movies, tv, music,
/// personal_photos, personal_videos) plus comics.
/// </summary>
public static class BaselineSampleGenerator
{
    public const string ManifestFile = "sample-manifest.json";
    public const string TextPdfName = "Alice's Adventures in Wonderland.pdf";
    public const string ScannedPdfName = "Pride and Prejudice.pdf";

    /// <summary>Every generated file carries this creation and modification time.</summary>
    public static readonly DateTime FileTimeUtc = new(2024, 1, 1, 12, 0, 0, DateTimeKind.Utc);

    public static readonly string[] Folders =
        ["books", "audiobooks", "movies", "tv", "music", "comics", "personal_photos", "personal_videos"];

    private const int PhotosPerCity = 3;

    /// <summary>Offline command: <c>--generate-baseline-samples &lt;sample-root&gt;</c>.</summary>
    public static async Task RunAsync(string sampleRoot)
    {
        var files = await GenerateAsync(sampleRoot).ConfigureAwait(false);
        Console.WriteLine($"Baseline samples: {files.Count} files written under {Path.GetFullPath(sampleRoot)}.");
    }

    /// <summary>
    /// Generates the sample library. Refuses a folder that already holds anything, so a real media folder can
    /// never receive generated files.
    /// </summary>
    public static async Task<IReadOnlyList<BaselineSampleFile>> GenerateAsync(string sampleRoot, CancellationToken ct = default)
    {
        sampleRoot = Path.GetFullPath(sampleRoot);
        if (Directory.Exists(sampleRoot) && Directory.EnumerateFileSystemEntries(sampleRoot).Any())
        {
            throw new InvalidOperationException($"Sample root '{sampleRoot}' is not empty; generated samples are only written into a new folder.");
        }

        Directory.CreateDirectory(sampleRoot);
        foreach (var folder in Folders)
        {
            Directory.CreateDirectory(Path.Combine(sampleRoot, folder));
        }

        var written = new List<string>();
        foreach (var relative in await DevSeedEndpoints.WriteBaselineCatalogueAsync(sampleRoot, FileTimeUtc, ct).ConfigureAwait(false))
        {
            written.Add(relative);
        }

        await WriteAsync(Path.Combine(sampleRoot, "books", TextPdfName), TextLayerPdf(), FileTimeUtc, written, sampleRoot, ct).ConfigureAwait(false);
        await WriteAsync(Path.Combine(sampleRoot, "books", ScannedPdfName), ScannedPdf(), FileTimeUtc, written, sampleRoot, ct).ConfigureAwait(false);

        foreach (var city in ViewDensityFixtures.Create().GroupBy(sample => sample.City))
        {
            foreach (var sample in city.Take(PhotosPerCity))
            {
                var path = Path.Combine(sampleRoot, "personal_photos", sample.City, sample.FileName);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                await WriteAsync(path, ViewDensityFixtures.Render(sample), sample.CapturedAt.UtcDateTime, written, sampleRoot, ct).ConfigureAwait(false);
            }
        }

        foreach (var (name, title, year) in new[] { ("Birthday Party 2022.mp4", "Birthday Party", 2022), ("Beach Day 2021.mp4", "Beach Day", 2021) })
        {
            await WriteAsync(Path.Combine(sampleRoot, "personal_videos", name), Mp4Builder.Create(title, year: year),
                FileTimeUtc, written, sampleRoot, ct).ConfigureAwait(false);
        }

        // Directory times also carry the fixed time so a rerun looks identical to the file system.
        foreach (var directory in Directory.EnumerateDirectories(sampleRoot, "*", SearchOption.AllDirectories).Append(sampleRoot))
        {
            Directory.SetCreationTimeUtc(directory, FileTimeUtc);
            Directory.SetLastWriteTimeUtc(directory, FileTimeUtc);
        }

        var manifest = Describe(sampleRoot, written);
        await File.WriteAllTextAsync(Path.Combine(sampleRoot, ManifestFile),
            JsonSerializer.Serialize(manifest, ManifestJson), ct).ConfigureAwait(false);
        return manifest;
    }

    /// <summary>Lists generated files (relative path, size, SHA-256), sorted by path.</summary>
    public static IReadOnlyList<BaselineSampleFile> Describe(string sampleRoot, IEnumerable<string> relativePaths) =>
        relativePaths
            .Select(path => Path.GetRelativePath(sampleRoot, Path.Combine(sampleRoot, path)).Replace('\\', '/'))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .Select(relative =>
            {
                var full = Path.Combine(sampleRoot, relative.Replace('/', Path.DirectorySeparatorChar));
                using var stream = File.OpenRead(full);
                return new BaselineSampleFile(relative, stream.Length, Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant());
            })
            .ToList();

    /// <summary>A PDF with a real text layer, titled and attributed so retail providers can match it.</summary>
    public static byte[] TextLayerPdf()
    {
        var builder = new PdfDocumentBuilder();
        builder.DocumentInformation.Title = "Alice's Adventures in Wonderland";
        builder.DocumentInformation.Author = "Lewis Carroll";
        var font = builder.AddStandard14Font(Standard14Font.Helvetica);
        var page = builder.AddPage(UglyToad.PdfPig.Content.PageSize.A4);
        var y = 760d;
        foreach (var line in new[]
        {
            "Alice's Adventures in Wonderland",
            "by Lewis Carroll",
            "",
            "Alice was beginning to get very tired of sitting by her sister on the bank,",
            "and of having nothing to do: once or twice she had peeped into the book her",
            "sister was reading, but it had no pictures or conversations in it, and what",
            "is the use of a book, thought Alice, without pictures or conversations?",
        })
        {
            if (line.Length > 0)
            {
                page.AddText(line, 12, new PdfPoint(50, y), font);
            }

            y -= 20;
        }

        return FixDocumentId(builder.Build());
    }

    /// <summary>
    /// A "scanned" PDF: one page that holds only a picture of text, so its text layer is empty and the
    /// Engine would need OCR to read it. No document title is embedded, as with a real scan.
    /// </summary>
    public static byte[] ScannedPdf()
    {
        var builder = new PdfDocumentBuilder();
        var page = builder.AddPage(UglyToad.PdfPig.Content.PageSize.A4);
        page.AddPng(RenderScanPage(), new PdfRectangle(0, 0, 595, 842));
        return FixDocumentId(builder.Build());
    }

    // PdfPig writes a random document id into the trailer. Replacing it with a digest of the rest of the file (same
    // length, so the cross-reference offsets stay valid) makes the PDF bytes identical between runs.
    private static byte[] FixDocumentId(byte[] pdf)
    {
        var text = Encoding.Latin1.GetString(pdf);
        var blank = DocumentId.Replace(text, "/ID [ <00000000000000000000000000000000><00000000000000000000000000000000>]");
        var digest = Convert.ToHexString(SHA256.HashData(Encoding.Latin1.GetBytes(blank)).AsSpan(0, 16));
        return Encoding.Latin1.GetBytes(DocumentId.Replace(text, $"/ID [ <{digest}><{digest}>]"));
    }

    private static readonly Regex DocumentId = new(@"/ID \[ <[0-9A-Fa-f]{32}><[0-9A-Fa-f]{32}>\]", RegexOptions.CultureInvariant);

    private static byte[] RenderScanPage()
    {
        using var bitmap = new SKBitmap(620, 877);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.White);
        using var paint = new SKPaint { IsAntialias = true, Color = SKColors.Black };
        using var font = new SKFont(SKTypeface.Default, 22);
        var y = 90f;
        foreach (var line in new[]
        {
            "Pride and Prejudice",
            "by Jane Austen",
            "",
            "It is a truth universally acknowledged, that a single man in possession",
            "of a good fortune, must be in want of a wife.",
        })
        {
            if (line.Length > 0)
            {
                canvas.DrawText(line, 40, y, SKTextAlign.Left, font, paint);
            }

            y += 38;
        }

        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    private static async Task WriteAsync(
        string path, byte[] bytes, DateTime fileTimeUtc, List<string> written, string sampleRoot, CancellationToken ct)
    {
        await File.WriteAllBytesAsync(path, bytes, ct).ConfigureAwait(false);
        DevSeedEndpoints.StampFile(path, fileTimeUtc);
        written.Add(Path.GetRelativePath(sampleRoot, path).Replace('\\', '/'));
    }

    private static readonly JsonSerializerOptions ManifestJson = new() { WriteIndented = true };
}

public sealed record BaselineSampleFile(string Path, long Bytes, string Sha256);
