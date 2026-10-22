using System.IO.Compression;
using System.Xml;
using System.Xml.Linq;
using DocumentFormat.OpenXml.CustomProperties;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.VariantTypes;

namespace MediaEngine.Api.Services.LocalAssets.FileWrites;

/// <summary>
/// Writes the profile name into a Word, Excel or PowerPoint file as the custom document property <c>Tuvima Added By</c>
/// (batched write-back D7). Author, title and every other property are left as they are, and so is the document itself.
/// </summary>
public static class OfficeProfileName
{
    private const string DigitalSignatureOriginType = "http://schemas.openxmlformats.org/package/2006/relationships/digital-signature/origin";
    private const string StrictNamespacePrefix = "http://purl.oclc.org/ooxml/";

    // The format id every custom document property carries.
    private const string CustomPropertyFormatId = "{D5CDD505-2E9C-101B-9397-08002B2CF9AE}";

    /// <summary>
    /// Null when the file is an ordinary (Transitional) Open XML package that may be written to. A file that is not a zip package (an
    /// encrypted Office file is a different container), that carries a digital signature, or that is Strict Open XML is kept as it is,
    /// with the reason.
    /// </summary>
    public static string? Inspect(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var package = new ZipArchive(stream, ZipArchiveMode.Read);
            if (package.GetEntry("[Content_Types].xml") is null)
            {
                return ViewProfileNameFormats.KeptSignedOfficeReason;
            }

            // A signed package keeps its signatures beside the document parts (and says so in the package relationships); changing
            // any part would break them.
            if (package.Entries.Any(entry => entry.FullName.StartsWith("_xmlsignatures/", StringComparison.OrdinalIgnoreCase)))
            {
                return ViewProfileNameFormats.KeptSignedOfficeReason;
            }

            var relationships = ReadRelationships(package);
            if (relationships.Any(relationship => string.Equals(relationship.Type, DigitalSignatureOriginType, StringComparison.OrdinalIgnoreCase)))
            {
                return ViewProfileNameFormats.KeptSignedOfficeReason;
            }

            return IsStrict(package, relationships) ? ViewProfileNameFormats.KeptStrictOfficeReason : null;
        }
        catch (Exception ex) when (ex is InvalidDataException or XmlException)
        {
            // Anything that cannot be read cleanly is not written to.
            return ViewProfileNameFormats.KeptSignedOfficeReason;
        }
    }

    private static List<(string Type, string Target)> ReadRelationships(ZipArchive package)
    {
        var entry = package.GetEntry("_rels/.rels");
        if (entry is null)
        {
            return [];
        }

        using var stream = entry.Open();
        return XDocument.Load(stream).Descendants()
            .Where(element => element.Name.LocalName == "Relationship")
            .Select(element => (Type: (string?)element.Attribute("Type") ?? string.Empty, Target: (string?)element.Attribute("Target") ?? string.Empty))
            .ToList();
    }

    /// <summary>Strict Open XML uses other namespaces and marks its main part <c>conformance="strict"</c>; the writer only knows the Transitional form.</summary>
    private static bool IsStrict(ZipArchive package, List<(string Type, string Target)> relationships)
    {
        var main = relationships.FirstOrDefault(relationship => relationship.Type.EndsWith("/officeDocument", StringComparison.OrdinalIgnoreCase));
        if (main.Target.Length == 0)
        {
            return false;
        }

        if (main.Type.StartsWith(StrictNamespacePrefix, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var entry = package.GetEntry(main.Target.TrimStart('/')) ?? package.Entries.FirstOrDefault(candidate =>
            string.Equals(candidate.FullName, main.Target.TrimStart('/'), StringComparison.OrdinalIgnoreCase));
        if (entry is null)
        {
            return false;
        }

        using var stream = entry.Open();
        using var reader = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
        reader.MoveToContent();
        if (reader.NamespaceURI.StartsWith(StrictNamespacePrefix, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        while (reader.MoveToNextAttribute())
        {
            if (reader.LocalName == "conformance" && string.Equals(reader.Value, "strict", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Sets the property on the document at <paramref name="path"/> (a copy the caller owns).</summary>
    public static void Write(string path, string name)
    {
        switch (Path.GetExtension(path).ToLowerInvariant())
        {
            case ".docx":
                using (var document = WordprocessingDocument.Open(path, true))
                {
                    Apply(document.CustomFilePropertiesPart ?? document.AddCustomFilePropertiesPart(), name);
                }

                break;
            case ".xlsx":
                using (var document = SpreadsheetDocument.Open(path, true))
                {
                    Apply(document.CustomFilePropertiesPart ?? document.AddCustomFilePropertiesPart(), name);
                }

                break;
            case ".pptx":
                using (var document = PresentationDocument.Open(path, true))
                {
                    Apply(document.CustomFilePropertiesPart ?? document.AddCustomFilePropertiesPart(), name);
                }

                break;
            default:
                throw new NotSupportedException("Only Word, Excel and PowerPoint files carry the property.");
        }
    }

    /// <summary>Reads the property back from the document at <paramref name="path"/>; null when it has none.</summary>
    public static string? Read(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".docx" => ReadFrom(() =>
        {
            using var document = WordprocessingDocument.Open(path, false);
            return Find(document.CustomFilePropertiesPart);
        }),
        ".xlsx" => ReadFrom(() =>
        {
            using var document = SpreadsheetDocument.Open(path, false);
            return Find(document.CustomFilePropertiesPart);
        }),
        ".pptx" => ReadFrom(() =>
        {
            using var document = PresentationDocument.Open(path, false);
            return Find(document.CustomFilePropertiesPart);
        }),
        _ => null,
    };

    private static string? ReadFrom(Func<string?> read) => read();

    private static void Apply(CustomFilePropertiesPart part, string name)
    {
        var properties = part.Properties ??= new Properties();
        var existing = properties.Elements<CustomDocumentProperty>()
            .FirstOrDefault(property => string.Equals(property.Name?.Value, ViewProfileNameFormats.OfficePropertyName, StringComparison.Ordinal));
        var id = existing?.PropertyId?.Value
            ?? properties.Elements<CustomDocumentProperty>().Select(property => property.PropertyId?.Value ?? 0).DefaultIfEmpty(1).Max() + 1;
        existing?.Remove();
        properties.AppendChild(new CustomDocumentProperty(new VTLPWSTR(name))
        {
            FormatId = CustomPropertyFormatId,
            PropertyId = id,
            Name = ViewProfileNameFormats.OfficePropertyName,
        });
        properties.Save();
    }

    private static string? Find(CustomFilePropertiesPart? part) =>
        part?.Properties?.Elements<CustomDocumentProperty>()
            .FirstOrDefault(property => string.Equals(property.Name?.Value, ViewProfileNameFormats.OfficePropertyName, StringComparison.Ordinal))
            ?.GetFirstChild<VTLPWSTR>()?.Text;
}
