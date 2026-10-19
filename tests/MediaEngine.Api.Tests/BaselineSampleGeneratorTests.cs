using MediaEngine.Api.DevSupport;
using MediaEngine.Api.Services.LocalAssets.Ocr;

namespace MediaEngine.Api.Tests;

public sealed class BaselineSampleGeneratorTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"tuvima_baseline_samples_{Guid.NewGuid():N}");

    public BaselineSampleGeneratorTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // A scanner may still hold a temp file; the next test run's unique folder name keeps runs apart.
        }
    }

    [Fact]
    public async Task GenerateAsync_WritesIdenticalBytesAndFileTimesOnEveryRun()
    {
        var first = await BaselineSampleGenerator.GenerateAsync(Path.Combine(_root, "first"));
        var second = await BaselineSampleGenerator.GenerateAsync(Path.Combine(_root, "second"));

        Assert.Equal(first, second);
        foreach (var file in second.Where(file => !file.Path.StartsWith("personal_photos/", StringComparison.Ordinal)))
        {
            Assert.Equal(BaselineSampleGenerator.FileTimeUtc, File.GetLastWriteTimeUtc(Path.Combine(_root, "second", file.Path)));
        }
    }

    [Fact]
    public async Task GenerateAsync_CoversEveryMediaFolderIncludingBothPdfs()
    {
        var files = await BaselineSampleGenerator.GenerateAsync(Path.Combine(_root, "samples"));

        foreach (var folder in BaselineSampleGenerator.Folders)
        {
            Assert.Contains(files, file => file.Path.StartsWith(folder + "/", StringComparison.Ordinal));
        }

        Assert.Contains(files, file => file.Path == "books/" + BaselineSampleGenerator.TextPdfName);
        Assert.Contains(files, file => file.Path == "books/" + BaselineSampleGenerator.ScannedPdfName);
        Assert.Contains(files, file => file.Path.EndsWith(".epub", StringComparison.Ordinal));
        Assert.Contains(files, file => file.Path.EndsWith(".cbz", StringComparison.Ordinal));
        Assert.Contains(files, file => file.Path.EndsWith(".flac", StringComparison.Ordinal));
    }

    [Fact]
    public async Task GenerateAsync_RefusesAFolderThatAlreadyHoldsFiles()
    {
        var existing = Path.Combine(_root, "real-media");
        Directory.CreateDirectory(existing);
        await File.WriteAllTextAsync(Path.Combine(existing, "keep.epub"), "original");

        await Assert.ThrowsAsync<InvalidOperationException>(() => BaselineSampleGenerator.GenerateAsync(existing));

        Assert.Equal("original", await File.ReadAllTextAsync(Path.Combine(existing, "keep.epub")));
        Assert.Single(Directory.EnumerateFileSystemEntries(existing));
    }

    [Fact]
    public void Pdfs_AreByteIdenticalBetweenBuilds()
    {
        Assert.Equal(BaselineSampleGenerator.TextLayerPdf(), BaselineSampleGenerator.TextLayerPdf());
        Assert.Equal(BaselineSampleGenerator.ScannedPdf(), BaselineSampleGenerator.ScannedPdf());
    }

    [Fact]
    public void ScannedPdf_HasNoTextLayerAndTheTextPdfDoes()
    {
        var scanned = Path.Combine(_root, "scanned.pdf");
        var text = Path.Combine(_root, "text.pdf");
        File.WriteAllBytes(scanned, BaselineSampleGenerator.ScannedPdf());
        File.WriteAllBytes(text, BaselineSampleGenerator.TextLayerPdf());

        var scannedLayers = PdfPageSelection.ReadTextLayers(scanned, 10, CancellationToken.None);
        var textLayers = PdfPageSelection.ReadTextLayers(text, 10, CancellationToken.None);

        Assert.Equal(1, scannedLayers.PageCount);
        Assert.Equal(0, scannedLayers.Pages.Sum(page => page.NonWhitespaceCharacters));
        Assert.Equal([1], PdfPageSelection.PagesNeedingOcr(scannedLayers.Pages, minimumCharacters: 20));
        Assert.Equal(1, textLayers.PageCount);
        Assert.True(textLayers.Pages.Sum(page => page.NonWhitespaceCharacters) > 100);
        Assert.Empty(PdfPageSelection.PagesNeedingOcr(textLayers.Pages, minimumCharacters: 20));
    }

    [Fact]
    public void EpubAndCbz_AreByteIdenticalBetweenBuilds()
    {
        Assert.Equal(
            EpubBuilder.Create("Dune", "Frank Herbert", "9780441013593", 1965, "Spice."),
            EpubBuilder.Create("Dune", "Frank Herbert", "9780441013593", 1965, "Spice."));
        Assert.Equal(
            CbzBuilder.Create("Saga Chapter One", "Brian K. Vaughan", "Saga", 1, 2012),
            CbzBuilder.Create("Saga Chapter One", "Brian K. Vaughan", "Saga", 1, 2012));
    }
}
