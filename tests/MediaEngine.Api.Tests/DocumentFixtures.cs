using System.IO.Compression;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;
using A = DocumentFormat.OpenXml.Drawing;
using P = DocumentFormat.OpenXml.Presentation;
using S = DocumentFormat.OpenXml.Spreadsheet;
using W = DocumentFormat.OpenXml.Wordprocessing;

namespace MediaEngine.Api.Tests;

/// <summary>Builds tiny document fixtures in code so no binary files are checked in.</summary>
internal static class DocumentFixtures
{
    public static byte[] Pdf(string text)
    {
        var builder = new PdfDocumentBuilder();
        var font = builder.AddStandard14Font(Standard14Font.Helvetica);
        var page = builder.AddPage(UglyToad.PdfPig.Content.PageSize.A4);
        if (!string.IsNullOrEmpty(text))
        {
            page.AddText(text, 12, new PdfPoint(25, 700), font);
        }

        return builder.Build();
    }

    public static byte[] Docx(string text)
    {
        using var stream = new MemoryStream();
        using (var document = WordprocessingDocument.Create(stream, WordprocessingDocumentType.Document))
        {
            var main = document.AddMainDocumentPart();
            main.Document = new W.Document(new W.Body(new W.Paragraph(new W.Run(new W.Text(text)))));
            main.Document.Save();
        }

        return stream.ToArray();
    }

    public static byte[] Xlsx(string text)
    {
        using var stream = new MemoryStream();
        using (var document = SpreadsheetDocument.Create(stream, SpreadsheetDocumentType.Workbook))
        {
            var workbook = document.AddWorkbookPart();
            workbook.Workbook = new S.Workbook();
            var sheetPart = workbook.AddNewPart<WorksheetPart>();
            sheetPart.Worksheet = new S.Worksheet(new S.SheetData(
                new S.Row(new S.Cell { DataType = S.CellValues.InlineString, InlineString = new S.InlineString(new S.Text(text)) })));
            workbook.Workbook.AppendChild(new S.Sheets(new S.Sheet
            {
                Id = workbook.GetIdOfPart(sheetPart),
                SheetId = 1,
                Name = "Sheet1",
            }));
            workbook.Workbook.Save();
        }

        return stream.ToArray();
    }

    /// <summary>A minimal OpenDocument package (.ods or .odp style): a zip holding only content.xml with the text in one paragraph.</summary>
    public static byte[] OpenDocument(string text)
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            var entry = archive.CreateEntry("content.xml");
            using var writer = new StreamWriter(entry.Open());
            writer.Write(
                "<?xml version=\"1.0\" encoding=\"UTF-8\"?>"
                + "<office:document-content xmlns:office=\"urn:oasis:names:tc:opendocument:xmlns:office:1.0\""
                + " xmlns:text=\"urn:oasis:names:tc:opendocument:xmlns:text:1.0\">"
                + "<office:body><office:spreadsheet><text:p>" + System.Security.SecurityElement.Escape(text) + "</text:p>"
                + "</office:spreadsheet></office:body></office:document-content>");
        }

        return stream.ToArray();
    }

    public static byte[] Pptx(string text)
    {
        using var stream = new MemoryStream();
        using (var document = PresentationDocument.Create(stream, PresentationDocumentType.Presentation))
        {
            var presentation = document.AddPresentationPart();
            presentation.Presentation = new P.Presentation();
            var slide = presentation.AddNewPart<SlidePart>();
            slide.Slide = new P.Slide(new P.CommonSlideData(new P.ShapeTree(
                new P.Shape(new P.TextBody(
                    new A.BodyProperties(),
                    new A.Paragraph(new A.Run(new A.Text(text))))))));
            slide.Slide.Save();
            presentation.Presentation.Save();
        }

        return stream.ToArray();
    }
}
