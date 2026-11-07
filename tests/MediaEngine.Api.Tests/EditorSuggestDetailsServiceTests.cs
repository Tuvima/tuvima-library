using System.IO.Compression;
using MediaEngine.Api.Services.EditSession;
using MediaEngine.Contracts.Editing;
using MediaEngine.Domain.Models;
using Microsoft.Extensions.Logging.Abstractions;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;

namespace MediaEngine.Api.Tests;

/// <summary>"Suggest details from the document" (universal editor v5, WP3, D18): what a supporting file says about itself, read locally.</summary>
public sealed class EditorSuggestDetailsServiceTests : IDisposable
{
    private readonly EditorHeaderTestWorld _world = new("The Expanse");

    [Fact]
    public async Task APdf_FillsTitleDescriptionAuthorPagesAndFirstLines()
    {
        var path = WritePdf("Season 02/exercises.pdf", title: "Components exercises", subject: "Homework for lesson two", author: "A. Teacher");
        var id = await StoreAsync(path);

        var suggestions = await Service().ReadAsync(id);

        Assert.NotNull(suggestions);
        Assert.Equal(EditorSuggestDetailsSources.Pdf, suggestions.Source);
        Assert.Equal("Components exercises", suggestions.Title);
        Assert.Equal("Homework for lesson two", suggestions.Description);
        Assert.Equal("A. Teacher", suggestions.Author);
        Assert.Equal(1, suggestions.PageCount);
        Assert.Contains(suggestions.FirstLines, line => line.Contains("Exercise one", StringComparison.Ordinal));
        Assert.Equal(id, suggestions.SupportingFileId);
    }

    [Fact]
    public async Task AnEpub_FillsTitleAuthorDescriptionAndFirstLines()
    {
        var path = WriteEpub("Companion.epub");
        var id = await StoreAsync(path);

        var suggestions = await Service().ReadAsync(id);

        Assert.Equal(EditorSuggestDetailsSources.Epub, suggestions!.Source);
        Assert.Equal("A Companion Guide", suggestions.Title);
        Assert.Equal("Ada Writer", suggestions.Author);
        Assert.Equal("Notes on the series.", suggestions.Description);
        Assert.Contains("Chapter one", suggestions.FirstLines);
    }

    [Fact]
    public async Task APdfWithNoSubject_DescribesItselfByItsFirstLines()
    {
        var path = WritePdf("notes.pdf", title: "Notes", subject: null, author: null);
        var id = await StoreAsync(path);

        var suggestions = await Service().ReadAsync(id);

        Assert.Equal("Notes", suggestions!.Title);
        Assert.Contains("Exercise one", suggestions.Description, StringComparison.Ordinal);
        Assert.Null(suggestions.Author);
    }

    [Fact]
    public async Task APlainTextFile_GivesItsFirstLines_NotTooMany_AndNoTitle()
    {
        var path = _world.Write("Behind the scenes.txt", string.Join('\n', Enumerable.Range(1, 20).Select(number => $"Line {number}")) + "\n");
        var id = await StoreAsync(path);

        var suggestions = await Service().ReadAsync(id);

        Assert.Equal(EditorSuggestDetailsSources.Text, suggestions!.Source);
        Assert.Null(suggestions.Title);
        Assert.Equal(["Line 1", "Line 2", "Line 3", "Line 4", "Line 5", "Line 6"], suggestions.FirstLines);
        Assert.Equal("Line 1 Line 2 Line 3 Line 4 Line 5 Line 6", suggestions.Description);
    }

    [Fact]
    public async Task ADescription_IsCutAtTheConfiguredLength()
    {
        var path = _world.Write("long.md", "# Heading\n" + new string('a', 900) + "\n");
        var id = await StoreAsync(path);

        var suggestions = await Service().ReadAsync(id);

        Assert.Equal("Heading", suggestions!.FirstLines[0]);
        Assert.True(suggestions.Description!.Length <= 501);
        Assert.EndsWith("…", suggestions.Description, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ADamagedPdf_GivesNoSuggestions_NotAnError()
    {
        var path = _world.Write("broken.pdf", "this is not a pdf at all");
        var id = await StoreAsync(path);

        var suggestions = await Service().ReadAsync(id);

        Assert.Equal(EditorSuggestDetailsSources.None, suggestions!.Source);
        Assert.Null(suggestions.Title);
        Assert.Empty(suggestions.FirstLines);
    }

    [Fact]
    public async Task AFileThatLeftTheDisk_GivesNoSuggestions()
    {
        var path = _world.Write("gone.txt", "hello");
        var id = await StoreAsync(path);
        File.Delete(path);

        var suggestions = await Service().ReadAsync(id);

        Assert.Equal(EditorSuggestDetailsSources.None, suggestions!.Source);
    }

    [Fact]
    public async Task AFileOfAKindWithNothingToRead_GivesNoSuggestions()
    {
        var path = _world.Write("poster.jpg", "not really an image");
        var id = await StoreAsync(path, ItemExtraKinds.Picture);

        Assert.Equal(EditorSuggestDetailsSources.None, (await Service().ReadAsync(id))!.Source);
    }

    [Fact]
    public async Task AnUnknownSupportingFile_HasNoSuggestions()
    {
        Assert.Null(await Service().ReadAsync(Guid.NewGuid()));
    }

    private EditorSuggestDetailsService Service() => new(
        _world.Extras, new EditorConfigurationProvider(() => null), NullLogger<EditorSuggestDetailsService>.Instance);

    private Task<Guid> StoreAsync(string absolutePath, string kind = ItemExtraKinds.Document) =>
        _world.SupportingFileAsync(Path.GetRelativePath(_world.TitleFolder, absolutePath), ExtraScope.Root, kind);

    private string WriteEpub(string relativePath)
    {
        var path = Path.Combine(_world.TitleFolder, relativePath);
        using (var archive = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            Add(archive, "mimetype", "application/epub+zip");
            Add(archive, "META-INF/container.xml",
                "<?xml version=\"1.0\"?><container version=\"1.0\" xmlns=\"urn:oasis:names:tc:opendocument:xmlns:container\"><rootfiles>"
                + "<rootfile full-path=\"content.opf\" media-type=\"application/oebps-package+xml\"/></rootfiles></container>");
            Add(archive, "content.opf",
                "<?xml version=\"1.0\"?><package xmlns=\"http://www.idpf.org/2007/opf\" version=\"3.0\" unique-identifier=\"id\">"
                + "<metadata xmlns:dc=\"http://purl.org/dc/elements/1.1/\"><dc:identifier id=\"id\">urn:uuid:1</dc:identifier><dc:title>A Companion Guide</dc:title>"
                + "<dc:creator>Ada Writer</dc:creator><dc:description>Notes on the series.</dc:description><dc:language>en</dc:language></metadata>"
                + "<manifest><item id=\"nav\" href=\"nav.xhtml\" media-type=\"application/xhtml+xml\" properties=\"nav\"/>"
                + "<item id=\"c1\" href=\"c1.xhtml\" media-type=\"application/xhtml+xml\"/></manifest><spine><itemref idref=\"c1\"/></spine></package>");
            Add(archive, "nav.xhtml",
                "<?xml version=\"1.0\"?><html xmlns=\"http://www.w3.org/1999/xhtml\" xmlns:epub=\"http://www.idpf.org/2007/ops\"><head><title>nav</title></head>"
                + "<body><nav epub:type=\"toc\"><ol><li><a href=\"c1.xhtml\">One</a></li></ol></nav></body></html>");
            Add(archive, "c1.xhtml",
                "<?xml version=\"1.0\"?><html xmlns=\"http://www.w3.org/1999/xhtml\"><head><title>c1</title></head><body><h1>Chapter one</h1><p>It begins.</p></body></html>");
        }

        return path;

        static void Add(ZipArchive archive, string name, string text)
        {
            var entry = archive.CreateEntry(name);
            using var writer = new StreamWriter(entry.Open());
            writer.Write(text);
        }
    }

    private string WritePdf(string relativePath, string? title, string? subject, string? author)
    {
        var builder = new PdfDocumentBuilder();
        if (title is not null)
        {
            builder.DocumentInformation.Title = title;
        }

        if (subject is not null)
        {
            builder.DocumentInformation.Subject = subject;
        }

        if (author is not null)
        {
            builder.DocumentInformation.Author = author;
        }

        var page = builder.AddPage(595, 842);
        var font = builder.AddStandard14Font(Standard14Font.Helvetica);
        page.AddText("Exercise one: add the two numbers", 12, new PdfPoint(25, 800), font);
        page.AddText("Exercise two: subtract them", 12, new PdfPoint(25, 780), font);
        var path = Path.Combine(_world.TitleFolder, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, builder.Build());
        return path;
    }

    public void Dispose() => _world.Dispose();
}
