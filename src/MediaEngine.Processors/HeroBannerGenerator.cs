using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Models;
using Microsoft.Extensions.Logging;
using SkiaSharp;

namespace MediaEngine.Processors;

/// <summary>
/// Generates cinematic hero banner images from cover art using SkiaSharp.
/// Pipeline: load cover → extract palette → blur/stretch → vignette → grain → save.
/// </summary>
public sealed class HeroBannerGenerator : IHeroBannerGenerator
{
    private const int HeroWidth = 1920;
    private const int HeroHeight = 600;
    private const float BlurSigma = 40f;
    private const int JpegQuality = 85;
    private const int GrainTileSize = 256;
    private const byte GrainAlpha = 15; // ~6% opacity
    private const string HeroFileName = "hero.jpg";

    private readonly ILogger<HeroBannerGenerator> _logger;

    public HeroBannerGenerator(ILogger<HeroBannerGenerator> logger)
    {
        _logger = logger;
    }

    public Task<HeroBannerResult> GenerateAsync(
        string coverImagePath,
        string outputDirectory,
        CancellationToken ct = default)
    {
        var heroPath = Path.Combine(outputDirectory, HeroFileName);

        // Cache check: skip if hero.jpg exists and is newer than cover.jpg.
        if (File.Exists(heroPath))
        {
            var coverModified = File.GetLastWriteTimeUtc(coverImagePath);
            var heroModified = File.GetLastWriteTimeUtc(heroPath);
            if (heroModified >= coverModified)
            {
                // Hero is up to date — extract dominant color from cover without regenerating.
                var cachedColor = ExtractDominantColor(coverImagePath);
                _logger.LogDebug("Hero banner cache hit for {Path}", heroPath);
                return Task.FromResult(new HeroBannerResult(heroPath, cachedColor, false));
            }
        }

        // Generate the hero banner.
        var dominantHex = GenerateHero(coverImagePath, heroPath);
        _logger.LogInformation("Generated hero banner at {Path} (dominant: {Color})", heroPath, dominantHex);

        return Task.FromResult(new HeroBannerResult(heroPath, dominantHex, true));
    }

    private string GenerateHero(string coverPath, string heroPath)
    {
        using var coverBitmap = SKBitmap.Decode(coverPath);
        if (coverBitmap is null)
        {
            _logger.LogWarning("Could not decode cover image at {Path}", coverPath);
            return "#000000";
        }

        var dominantHex = ExtractDominantColorFromBitmap(coverBitmap);
        var dominantColor = SKColor.Parse(dominantHex);

        using var surface = SKSurface.Create(new SKImageInfo(HeroWidth, HeroHeight));
        var canvas = surface.Canvas;

        // Layer 1: Blurred, stretched cover art.
        DrawBlurredCover(canvas, coverBitmap);

        // Layer 2: Radial vignette gradient from dominant color to near-black.
        DrawVignette(canvas, dominantColor);

        // Layer 3: Film grain noise overlay.
        DrawGrain(canvas);

        // Encode and save.
        using var image = surface.Snapshot();
        using var data = image.Encode(SKEncodedImageFormat.Jpeg, JpegQuality);
        using var stream = File.OpenWrite(heroPath);
        data.SaveTo(stream);

        return dominantHex;
    }

    private static void DrawBlurredCover(SKCanvas canvas, SKBitmap cover)
    {
        using var blurFilter = SKImageFilter.CreateBlur(BlurSigma, BlurSigma);
        using var paint = new SKPaint
        {
            ImageFilter = blurFilter,
        };

        var destRect = new SKRect(0, 0, HeroWidth, HeroHeight);

        // Slight overscale to prevent blur edge artifacts.
        var inset = -30f;
        var oversized = new SKRect(inset, inset, HeroWidth - inset, HeroHeight - inset);

        canvas.DrawBitmap(cover, oversized, SKSamplingOptions.Default, paint);

        // Darken layer to ensure text readability.
        using var darken = new SKPaint
        {
            Color = new SKColor(0, 0, 0, 100), // ~39% opacity black
        };
        canvas.DrawRect(destRect, darken);
    }

    private static void DrawVignette(SKCanvas canvas, SKColor dominantColor)
    {
        var centerX = HeroWidth * 0.35f; // Offset left for asymmetric vignette.
        var centerY = HeroHeight * 0.5f;
        var radius = Math.Max(HeroWidth, HeroHeight) * 0.8f;

        var innerColor = dominantColor.WithAlpha(60);  // Subtle color wash at center.
        var outerColor = new SKColor(6, 10, 22, 200);  // Near-black at edges (#060A16).

        using var shader = SKShader.CreateRadialGradient(
            new SKPoint(centerX, centerY),
            radius,
            [innerColor, outerColor],
            [0.2f, 1.0f],
            SKShaderTileMode.Clamp);

        using var paint = new SKPaint
        {
            Shader = shader,
        };

        canvas.DrawRect(0, 0, HeroWidth, HeroHeight, paint);
    }

    private static void DrawGrain(SKCanvas canvas)
    {
        // Create a small noise tile and draw it repeatedly at low opacity.
        using var grainBitmap = new SKBitmap(GrainTileSize, GrainTileSize);
        var random = new Random(42); // Deterministic seed for consistent grain.

        for (int y = 0; y < GrainTileSize; y++)
        {
            for (int x = 0; x < GrainTileSize; x++)
            {
                var v = (byte)random.Next(0, 256);
                grainBitmap.SetPixel(x, y, new SKColor(v, v, v, GrainAlpha));
            }
        }

        using var paint = new SKPaint
        {
            BlendMode = SKBlendMode.SoftLight,
        };

        for (int y = 0; y < HeroHeight; y += GrainTileSize)
        {
            for (int x = 0; x < HeroWidth; x += GrainTileSize)
            {
                canvas.DrawBitmap(grainBitmap, x, y, SKSamplingOptions.Default, paint);
            }
        }
    }

    private string ExtractDominantColor(string imagePath)
    {
        try
        {
            using var bitmap = SKBitmap.Decode(imagePath);
            return bitmap is null ? "#000000" : ExtractDominantColorFromBitmap(bitmap);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to extract dominant color from {Path}", imagePath);
            return "#000000";
        }
    }

    /// <summary>
    /// Extracts the dominant color from a bitmap by grid-sampling pixels,
    /// bucketing by hue, and selecting the most saturated dominant bucket.
    /// </summary>
    private static string ExtractDominantColorFromBitmap(SKBitmap bitmap)
    {
        const int sampleStep = 8; // Sample every 8th pixel for speed.
        const int hueBucketCount = 18; // 20° per bucket.

        var hueBuckets = new int[hueBucketCount];
        var saturationSums = new float[hueBucketCount];
        var brightnessSums = new float[hueBucketCount];

        int marginX = bitmap.Width / 10;
        int marginY = bitmap.Height / 10;

        for (int y = marginY; y < bitmap.Height - marginY; y += sampleStep)
        {
            for (int x = marginX; x < bitmap.Width - marginX; x += sampleStep)
            {
                var pixel = bitmap.GetPixel(x, y);
                pixel.ToHsl(out float h, out float s, out float l);

                // Skip very dark or very light pixels (backgrounds, highlights).
                if (l < 0.1f || l > 0.9f || s < 0.15f)
                {
                    continue;
                }

                int bucket = Math.Clamp((int)(h / 20f), 0, hueBucketCount - 1);
                hueBuckets[bucket]++;
                saturationSums[bucket] += s;
                brightnessSums[bucket] += l;
            }
        }

        // Find the bucket with the most pixels.
        int bestBucket = 0;
        int bestCount = 0;
        for (int i = 0; i < hueBucketCount; i++)
        {
            if (hueBuckets[i] > bestCount)
            {
                bestCount = hueBuckets[i];
                bestBucket = i;
            }
        }

        if (bestCount == 0)
        {
            return "#1a1a2e"; // Fallback for very dark/monochrome covers.
        }

        float avgHue = (bestBucket * 20f) + 10f; // Center of bucket.
        float avgSat = saturationSums[bestBucket] / bestCount;
        float avgLightness = brightnessSums[bestBucket] / bestCount;

        // Boost saturation slightly for more vivid hero gradients.
        avgSat = Math.Min(avgSat * 1.2f, 1.0f);
        // Clamp lightness to a usable range for dark UI.
        avgLightness = Math.Clamp(avgLightness, 0.25f, 0.55f);

        var resultColor = SKColor.FromHsl(avgHue, avgSat * 100f, avgLightness * 100f);
        return $"#{resultColor.Red:X2}{resultColor.Green:X2}{resultColor.Blue:X2}";
    }
}
