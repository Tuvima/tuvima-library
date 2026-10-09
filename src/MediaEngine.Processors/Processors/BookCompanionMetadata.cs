using System.Xml;
using System.Xml.Linq;
using MediaEngine.Domain.Services;
using MediaEngine.Processors.Models;

namespace MediaEngine.Processors.Processors;

/// <summary>Reads explicit Calibre/book companions without guessing from an arbitrary OPF file.</summary>
internal static class BookCompanionMetadata
{
    internal static XDocument? Read(string filePath)
    {
        var named = Path.ChangeExtension(filePath, ".opf");
        var calibre = Path.Combine(Path.GetDirectoryName(filePath)!, "metadata.opf");
        var path = File.Exists(named) ? named : calibre;
        if (!File.Exists(path))
        {
            return null;
        }
        try
        {
            using var reader = XmlReader.Create(path, new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
            });
            return XDocument.Load(reader);
        }
        catch (XmlException) { return null; }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    internal static void AddIdentifiers(XDocument document, List<ExtractedClaim> claims)
    {
        foreach (var identifier in document.Descendants().Where(element => element.Name.LocalName == "identifier"))
        {
            var scheme = identifier.Attributes().FirstOrDefault(attribute => attribute.Name.LocalName == "scheme")?.Value.ToLowerInvariant();
            var value = identifier.Value.Trim();
            string? key = null;
            if (scheme == "isbn" && IsbnValidation.NormalizeValid(value) is { } isbn)
            {
                key = "isbn";
                value = isbn;
            }
            else if (scheme == "uuid" && Guid.TryParse(value.Replace("urn:uuid:", "", StringComparison.OrdinalIgnoreCase), out var uuid)
                     && uuid != Guid.Empty)
            {
                key = "calibre_uuid";
                value = uuid.ToString("D");
            }
            else if (scheme is "asin" or "amazon" && value.Length == 10 && value.All(char.IsAsciiLetterOrDigit))
            {
                key = "asin";
            }
            else if (scheme == "goodreads" && value.Length > 0 && value.All(char.IsAsciiDigit))
            {
                key = "goodreads_id";
            }
            else if (scheme == "google" && value.Length > 0)
            {
                key = "google_books_id";
            }

            // Calibre's numeric row ID is local to one database, never a book identity.
            if (key is not null && !claims.Any(claim => claim.Key == key && claim.Value == value))
            {
                claims.Add(ProcessorClaimFactory.Create(key, value, 0.9));
            }
        }
    }
}
