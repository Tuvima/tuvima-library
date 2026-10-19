using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using SkiaSharp;

namespace MediaEngine.Api.DevSupport;

/// <summary>
/// Creates minimal valid CBZ files with ComicInfo.xml for development seeding.
/// Produces files that pass <c>ComicProcessor.CanProcess</c>:
///   1) ZIP magic bytes (automatic from ZipArchive)
///   2) Contains image entries (.jpg)
///   3) No "mimetype" entry with EPUB content (excluded by ComicProcessor)
/// </summary>
public static class CbzBuilder
{
    // A fixed entry time keeps the archive bytes identical between runs (the ingestion baseline compares content hashes).
    private static readonly DateTimeOffset FixedEntryTime = new(2024, 1, 1, 0, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// Generates a valid CBZ file as a byte array.
    /// Contains a minimal JPEG image and a ComicInfo.xml with metadata.
    /// </summary>
    public static byte[] Create(
        string title,
        string? writer = null,
        string? series = null,
        int? number = null,
        int year = 0,
        string? genre = null,
        string? summary = null,
        string? publisher = null,
        string? penciller = null,
        int pageCount = 1)
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            // 1. Add deterministic issue-specific JPEG pages. The first page
            // becomes the local comic cover when provider artwork is absent.
            for (int i = 1; i <= pageCount; i++)
            {
                var entry = archive.CreateEntry($"page_{i:D3}.jpg", CompressionLevel.Fastest);
                entry.LastWriteTime = FixedEntryTime;
                using var entryStream = entry.Open();
                entryStream.Write(CreatePageJpeg(title, series, number, i));
            }

            // 2. Add ComicInfo.xml with metadata
            string comicInfo = BuildComicInfoXml(title, writer, series, number, year, genre, summary, publisher, penciller, pageCount);
            var xmlEntry = archive.CreateEntry("ComicInfo.xml", CompressionLevel.Fastest);
            xmlEntry.LastWriteTime = FixedEntryTime;
            using (var xmlStream = new StreamWriter(xmlEntry.Open(), new UTF8Encoding(false)))
            {
                xmlStream.Write(comicInfo);
            }
        }

        return stream.ToArray();
    }

    private static string BuildComicInfoXml(
        string title, string? writer, string? series, int? number,
        int year, string? genre, string? summary, string? publisher,
        string? penciller, int pageCount)
    {
        var sb = new StringBuilder();
        sb.AppendLine("<?xml version=\"1.0\" encoding=\"utf-8\"?>");
        sb.AppendLine("<ComicInfo>");
        sb.AppendLine($"  <Title>{Escape(title)}</Title>");

        if (writer is not null)
        {
            sb.AppendLine($"  <Writer>{Escape(writer)}</Writer>");
        }

        if (series is not null)
        {
            sb.AppendLine($"  <Series>{Escape(series)}</Series>");
        }

        if (number is not null)
        {
            sb.AppendLine($"  <Number>{number}</Number>");
        }

        if (year > 0)
        {
            sb.AppendLine($"  <Year>{year}</Year>");
        }

        if (genre is not null)
        {
            sb.AppendLine($"  <Genre>{Escape(genre)}</Genre>");
        }

        if (summary is not null)
        {
            sb.AppendLine($"  <Summary>{Escape(summary)}</Summary>");
        }

        if (publisher is not null)
        {
            sb.AppendLine($"  <Publisher>{Escape(publisher)}</Publisher>");
        }

        if (penciller is not null)
        {
            sb.AppendLine($"  <Penciller>{Escape(penciller)}</Penciller>");
        }

        sb.AppendLine($"  <PageCount>{pageCount}</PageCount>");
        sb.AppendLine("</ComicInfo>");

        return sb.ToString();
    }

    private static string Escape(string value) =>
        value.Replace("&", "&amp;")
             .Replace("<", "&lt;")
             .Replace(">", "&gt;")
             .Replace("\"", "&quot;");

    private static byte[] CreatePageJpeg(string title, string? series, int? number, int pageIndex)
    {
        var seed = SHA256.HashData(Encoding.UTF8.GetBytes($"{series}|{title}|{number}|{pageIndex}"));
        var primary = ColorFromSeed(seed, 0, 74);
        var secondary = ColorFromSeed(seed, 3, 122);
        var accent = ColorFromSeed(seed, 6, 182);

        using var surface = SKSurface.Create(new SKImageInfo(640, 960, SKColorType.Rgba8888, SKAlphaType.Premul));
        var canvas = surface.Canvas;
        canvas.Clear(primary);

        using var paint = new SKPaint { IsAntialias = true };
        paint.Color = secondary;
        canvas.DrawRect(new SKRect(0, 0, 640, 960), paint);

        paint.Color = primary.WithAlpha(230);
        canvas.DrawRect(new SKRect(0, 0, 640, 590), paint);

        paint.Color = accent.WithAlpha(222);
        for (var i = 0; i < 7; i++)
        {
            var left = -220 + (i * 142) + (seed[(i + 9) % seed.Length] % 34);
            canvas.DrawRect(new SKRect(left, 650, left + 98, 1040), paint);
        }

        paint.Color = SKColors.White.WithAlpha(238);
        var issueBars = Math.Clamp(number ?? pageIndex, 1, 12);
        for (var i = 0; i < issueBars; i++)
        {
            var x = 72 + (i * 36);
            canvas.DrawRoundRect(new SKRect(x, 96, x + 22, 232), 11, 11, paint);
        }

        paint.Color = SKColors.Black.WithAlpha(84);
        canvas.DrawRect(new SKRect(0, 0, 640, 960), paint);

        using var image = surface.Snapshot();
        using var data = image.Encode(SKEncodedImageFormat.Jpeg, 88);
        using var output = new MemoryStream();
        data.SaveTo(output);
        return output.ToArray();
    }

    private static SKColor ColorFromSeed(byte[] seed, int offset, byte floor)
    {
        var r = (byte)Math.Max(floor, seed[offset]);
        var g = (byte)Math.Max(floor, seed[offset + 1]);
        var b = (byte)Math.Max(floor, seed[offset + 2]);
        return new SKColor(r, g, b);
    }
}
