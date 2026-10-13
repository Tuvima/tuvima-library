using MediaEngine.Api.Services.LocalAssets.Ocr;
using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Services;
using MediaEngine.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using SkiaSharp;

namespace MediaEngine.Api.Tests;

/// <summary>Runs only when a real Tesseract is installed (tools/tesseract or PATH); otherwise it is skipped with a clear message.</summary>
public sealed class RealTesseractFactAttribute : FactAttribute
{
    public RealTesseractFactAttribute()
    {
        if (RealTesseract.Find() is null)
        {
            Skip = "Tesseract is not installed. Run tools/Download-Tesseract.ps1 or install the tesseract package to run this test.";
        }
    }
}

internal static class RealTesseract
{
    public static string? Find()
    {
        var name = OperatingSystem.IsWindows() ? "tesseract.exe" : "tesseract";
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var packaged = Path.Combine(directory.FullName, "tools", "tesseract", name);
            if (File.Exists(packaged))
            {
                return packaged;
            }

            directory = directory.Parent;
        }

        return (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(folder => Path.Combine(folder.Trim(), name))
            .FirstOrDefault(File.Exists);
    }
}

public sealed class RealTesseractTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"tuvima-real-ocr-{Guid.NewGuid():N}");

    [RealTesseractFact]
    public async Task ReadsRealTextFromAnImage()
    {
        var service = RealService();
        Assert.True((await service.GetCapabilityAsync()).IsAvailable);
        var image = Path.Combine(_root, "invoice.png");
        using (var bitmap = new SKBitmap(1200, 300))
        {
            Draw(bitmap, "Zeppelin hangar rental agreement");
            OcrImagePreparer.SavePng(bitmap, image);
        }

        var result = await service.RecognizeAsync(image, new OcrRunOptions(["eng"], false, TimeSpan.FromMinutes(1)));

        Assert.Contains("Zeppelin", result.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("rental", result.Text, StringComparison.OrdinalIgnoreCase);
        Assert.True(result.AverageConfidence > 50);
    }

    [RealTesseractFact]
    public async Task ReadsAPdfPageDrawnByThePdfRenderer()
    {
        var service = RealService();
        var pdf = Path.Combine(_root, "page.pdf");
        File.WriteAllBytes(pdf, OcrFixtures.Pdf("Zeppelin hangar rental agreement"));
        using var page = PdfPageRenderer.RenderPage(pdf, 0, 300, 3500);
        using var prepared = OcrImagePreparer.Prepare(page, 0, 3500);
        var image = Path.Combine(_root, "page.png");
        OcrImagePreparer.SavePng(prepared, image);

        var result = await service.RecognizeAsync(image, new OcrRunOptions(["eng"], true, TimeSpan.FromMinutes(1)));

        Assert.Contains("Zeppelin", result.Text, StringComparison.OrdinalIgnoreCase);
    }

    private OcrService RealService()
    {
        Directory.CreateDirectory(_root);
        var configuration = new ConfigurationDirectoryLoader(Path.Combine(_root, "config"));
        configuration.SaveOcr(new OcrSettings { TesseractBinaryPath = RealTesseract.Find()! });
        return new OcrService(
            configuration,
            new AssetPathService(Path.Combine(_root, "library"), null, Path.Combine(_root, "data")),
            new OcrProcessRunner(),
            NullLogger<OcrService>.Instance);
    }

    private static void Draw(SKBitmap bitmap, string text)
    {
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.White);
        using var font = new SKFont(SKTypeface.Default, 72);
        using var paint = new SKPaint { Color = SKColors.Black, IsAntialias = true };
        canvas.DrawText(text, 30, 180, SKTextAlign.Left, font, paint);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
