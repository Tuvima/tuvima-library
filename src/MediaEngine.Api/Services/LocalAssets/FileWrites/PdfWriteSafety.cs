using System.Text;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Exceptions;

namespace MediaEngine.Api.Services.LocalAssets.FileWrites;

/// <summary>
/// Decides whether a PDF may be written to (batched write-back D7): a signed or encrypted PDF is never touched, because adding to it
/// would break the signature or fail on the encryption. Anything the check cannot be sure about counts as not safe.
/// </summary>
public static class PdfWriteSafety
{
    // A signature always carries a /ByteRange, and an encrypted file names its /Encrypt dictionary in the trailer. Reading the raw
    // bytes catches both however the file is built; the document reader then checks what the raw text cannot show.
    private static readonly byte[][] Markers = [Encoding.ASCII.GetBytes("/ByteRange"), Encoding.ASCII.GetBytes("/Encrypt")];

    /// <summary>Null when the PDF may be written to, else why it is kept as it is.</summary>
    public static string? Inspect(string path)
    {
        if (ContainsMarker(path))
        {
            return ViewProfileNameFormats.KeptSignedPdfReason;
        }

        try
        {
            using var document = PdfDocument.Open(path, new ParsingOptions { UseLenientParsing = true, SkipMissingFonts = true });
            if (document.IsEncrypted)
            {
                return ViewProfileNameFormats.KeptSignedPdfReason;
            }

            if (document.TryGetForm(out var form) && form is not null)
            {
                // Any signature field, filled or empty, or a signature flag on the form, keeps the file as it is.
                if (form.Dictionary.ContainsKey(UglyToad.PdfPig.Tokens.NameToken.Create("SigFlags"))
                    || form.Fields.Any(field => field.FieldType == UglyToad.PdfPig.AcroForms.Fields.AcroFieldType.Signature))
                {
                    return ViewProfileNameFormats.KeptSignedPdfReason;
                }
            }

            return null;
        }
        catch (PdfDocumentEncryptedException)
        {
            return ViewProfileNameFormats.KeptSignedPdfReason;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A PDF that cannot be read cleanly is not written to; it stays as it is and is counted as kept.
            _ = ex;
            return "Kept in Library only: the PDF could not be checked.";
        }
    }

    private static bool ContainsMarker(string path)
    {
        var longest = Markers.Max(marker => marker.Length);
        var buffer = new byte[1024 * 1024 + longest];
        var carried = 0;
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.SequentialScan);
        int read;
        while ((read = stream.Read(buffer, carried, buffer.Length - carried)) > 0)
        {
            var span = buffer.AsSpan(0, carried + read);
            foreach (var marker in Markers)
            {
                if (span.IndexOf(marker) >= 0)
                {
                    return true;
                }
            }

            // Keep the tail, so a marker split across two reads is still found.
            carried = Math.Min(span.Length, longest - 1);
            span[^carried..].CopyTo(buffer);
        }

        return false;
    }
}
