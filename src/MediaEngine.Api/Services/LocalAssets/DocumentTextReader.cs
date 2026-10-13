using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Exceptions;
using A = DocumentFormat.OpenXml.Drawing;
using W = DocumentFormat.OpenXml.Wordprocessing;

namespace MediaEngine.Api.Services.LocalAssets;

/// <summary>Limits applied to one document text read.</summary>
public sealed record DocumentTextReadLimits(int MaxCharacters, int MaxPdfPages, TimeSpan Timeout, int TextLayerMinCharacters = 25);

/// <summary>
/// What a read found. <see cref="HasText"/> false means no embedded text (not an error);
/// <see cref="Failed"/> means the file could not be read at all.
/// </summary>
public sealed record DocumentTextReadResult(
    string? Text,
    bool Truncated,
    bool Encrypted,
    bool Failed,
    int? PageCount,
    string? FailureReason = null,
    int LowTextPages = 0)
{
    public bool HasText => !string.IsNullOrWhiteSpace(Text);
}

/// <summary>
/// Reads the embedded text of PDFs (text layer) and Office/OpenDocument/RTF files with a
/// capped size, page count and time. It never modifies the original and never throws for
/// a bad file: failures come back as a result so a batch can continue.
/// </summary>
public static class DocumentTextReader
{
    private static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".pdf", ".docx", ".xlsx", ".pptx", ".odt", ".rtf",
    };

    // Encrypted Office files are stored in an OLE container rather than a zip package.
    private static readonly byte[] OleSignature = [0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1];

    public static bool IsSupported(string extension) => Extensions.Contains(extension);

    public static DocumentTextReadResult Read(string path, DocumentTextReadLimits limits, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(limits.Timeout);
        var token = timeout.Token;
        var extension = Path.GetExtension(path).ToLowerInvariant();
        var text = new CappedText(limits.MaxCharacters);
        try
        {
            using var stream = new FileStream(
                path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
                bufferSize: 81920, FileOptions.SequentialScan);
            int? pages = null;
            var lowTextPages = 0;
            switch (extension)
            {
                case ".pdf":
                    (pages, lowTextPages) = ReadPdf(stream, text, limits.MaxPdfPages, limits.TextLayerMinCharacters, token);
                    break;
                case ".docx":
                    ReadDocx(stream, text, token);
                    break;
                case ".xlsx":
                    ReadXlsx(stream, text, token);
                    break;
                case ".pptx":
                    ReadPptx(stream, text, token);
                    break;
                case ".odt":
                    ReadOdt(stream, text, token);
                    break;
                case ".rtf":
                    ReadRtf(stream, text, token);
                    break;
                default:
                    return new DocumentTextReadResult(null, false, false, true, null, "unsupported format");
            }

            return new DocumentTextReadResult(text.ToString(), text.Truncated, false, false, pages, LowTextPages: lowTextPages);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            // The per-file time limit fired: keep what was read so far.
            return new DocumentTextReadResult(text.ToString(), true, false, false, null);
        }
        catch (PdfDocumentEncryptedException)
        {
            return new DocumentTextReadResult(null, false, true, false, null);
        }
        catch (Exception ex)
        {
            // Parsers throw many exception types for corrupt files; the caller records and logs the failure.

            if (IsOleContainer(path))
            {
                return new DocumentTextReadResult(null, false, true, false, null);
            }

            return new DocumentTextReadResult(null, false, false, true, null, ex.GetType().Name);
        }
    }

    private static bool IsOleContainer(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var header = new byte[OleSignature.Length];
            return stream.Read(header, 0, header.Length) == header.Length && header.AsSpan().SequenceEqual(OleSignature);
        }
        catch (IOException)
        {
            // Unreadable here too: report it as an ordinary failure, not as encrypted.
            return false;
        }
    }

    private static (int PageCount, int LowTextPages) ReadPdf(
        Stream stream, CappedText text, int maxPages, int minTextLayerCharacters, CancellationToken ct)
    {
        using var document = PdfDocument.Open(stream, new ParsingOptions { UseLenientParsing = true, SkipMissingFonts = true });
        var total = document.NumberOfPages;
        var limit = Math.Min(total, Math.Max(1, maxPages));
        var lowTextPages = 0;
        for (var page = 1; page <= limit; page++)
        {
            ct.ThrowIfCancellationRequested();
            var pageText = document.GetPage(page).Text;
            if (Ocr.PdfPageSelection.CountNonWhitespace(pageText) < minTextLayerCharacters)
            {
                lowTextPages++;
            }

            if (!text.Full)
            {
                text.AppendLine(pageText);
            }
        }

        if (limit < total)
        {
            text.MarkTruncated();
        }

        return (total, lowTextPages);
    }

    private static void ReadDocx(Stream stream, CappedText text, CancellationToken ct)
    {
        using var document = WordprocessingDocument.Open(stream, false);
        var body = document.MainDocumentPart?.Document?.Body;
        if (body is null)
        {
            return;
        }

        foreach (var paragraph in body.Descendants<W.Paragraph>())
        {
            ct.ThrowIfCancellationRequested();
            text.AppendLine(paragraph.InnerText);
            if (text.Full)
            {
                return;
            }
        }
    }

    private static void ReadXlsx(Stream stream, CappedText text, CancellationToken ct)
    {
        using var document = SpreadsheetDocument.Open(stream, false);
        var workbook = document.WorkbookPart;
        if (workbook is null)
        {
            return;
        }

        var shared = workbook.SharedStringTablePart?.SharedStringTable?
            .Elements<SharedStringItem>().Select(item => item.InnerText).ToList() ?? [];
        foreach (var sheet in workbook.WorksheetParts)
        {
            foreach (var row in sheet.Worksheet?.Descendants<Row>() ?? [])
            {
                ct.ThrowIfCancellationRequested();
                var cells = new List<string>();
                foreach (var cell in row.Elements<Cell>())
                {
                    var raw = cell.CellValue?.Text;
                    if (cell.DataType?.Value == CellValues.SharedString
                        && int.TryParse(raw, out var index) && index >= 0 && index < shared.Count)
                    {
                        raw = shared[index];
                    }
                    else if (cell.DataType?.Value == CellValues.InlineString)
                    {
                        raw = cell.InlineString?.InnerText;
                    }

                    if (!string.IsNullOrWhiteSpace(raw))
                    {
                        cells.Add(raw);
                    }
                }

                if (cells.Count > 0)
                {
                    text.AppendLine(string.Join(' ', cells));
                }

                if (text.Full)
                {
                    return;
                }
            }
        }
    }

    private static void ReadPptx(Stream stream, CappedText text, CancellationToken ct)
    {
        using var document = PresentationDocument.Open(stream, false);
        var slides = document.PresentationPart?.SlideParts;
        if (slides is null)
        {
            return;
        }

        foreach (var slide in slides)
        {
            foreach (var paragraph in slide.Slide?.Descendants<A.Paragraph>() ?? [])
            {
                ct.ThrowIfCancellationRequested();
                text.AppendLine(paragraph.InnerText);
                if (text.Full)
                {
                    return;
                }
            }
        }
    }

    private static void ReadOdt(Stream stream, CappedText text, CancellationToken ct)
    {
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);
        var entry = archive.GetEntry("content.xml") ?? throw new InvalidDataException("content.xml is missing.");
        using var content = entry.Open();
        using var reader = XmlReader.Create(content, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit });
        while (reader.Read() && !text.Full)
        {
            ct.ThrowIfCancellationRequested();
            if (reader.NodeType == XmlNodeType.Text || reader.NodeType == XmlNodeType.SignificantWhitespace)
            {
                text.Append(reader.Value);
            }
            else if (reader.NodeType == XmlNodeType.EndElement && reader.LocalName is "p" or "h")
            {
                text.Append("\n");
            }
        }
    }

    private static readonly Regex RtfControl = new(
        @"\\(?:'[0-9a-fA-F]{2}|[a-zA-Z]+-?\d* ?|[^a-zA-Z])", RegexOptions.Compiled, TimeSpan.FromSeconds(5));

    private static void ReadRtf(Stream stream, CappedText text, CancellationToken ct)
    {
        using var reader = new StreamReader(stream, Encoding.Latin1);
        var raw = new char[Math.Min(4 * 1024 * 1024, Math.Max(1, text.Capacity * 4))];
        var count = reader.ReadBlock(raw, 0, raw.Length);
        ct.ThrowIfCancellationRequested();
        var source = new string(raw, 0, count);
        if (!source.StartsWith("{\\rtf", StringComparison.Ordinal))
        {
            throw new InvalidDataException("Not an RTF document.");
        }

        var plain = RtfControl.Replace(source, match => match.Value is "\\par" or "\\par " or "\\line" ? "\n" : string.Empty)
            .Replace("{", string.Empty, StringComparison.Ordinal)
            .Replace("}", string.Empty, StringComparison.Ordinal);
        text.Append(plain);
    }

    private sealed class CappedText(int capacity)
    {
        private readonly StringBuilder _builder = new();

        public int Capacity { get; } = capacity;

        public bool Truncated { get; private set; }

        public bool Full => _builder.Length >= Capacity;

        public void MarkTruncated() => Truncated = true;

        public void AppendLine(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return;
            }

            Append(value);
            Append("\n");
        }

        public void Append(string value)
        {
            var room = Capacity - _builder.Length;
            if (room <= 0)
            {
                Truncated = true;
                return;
            }

            if (value.Length > room)
            {
                _builder.Append(value, 0, room);
                Truncated = true;
            }
            else
            {
                _builder.Append(value);
            }
        }

        public override string ToString() => _builder.ToString();
    }
}
