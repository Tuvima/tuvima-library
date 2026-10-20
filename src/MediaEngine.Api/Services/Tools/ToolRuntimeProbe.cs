using MediaEngine.AI.Infrastructure;
using SkiaSharp;

namespace MediaEngine.Api.Services.Tools;

/// <summary>The real probes: a 2x2 image encode for the image engine, loading the local AI runtime, and re-reading model files.</summary>
public sealed class ToolRuntimeProbe(
    IAiRuntimeStatus runtime,
    ModelInventory inventory,
    ILogger<ToolRuntimeProbe> logger) : IToolRuntimeProbe
{
    public bool ImageEngineWorks()
    {
        try
        {
            using var bitmap = new SKBitmap(2, 2);
            bitmap.Erase(SKColors.Purple);
            using var image = SKImage.FromBitmap(bitmap);
            using var encoded = image.Encode(SKEncodedImageFormat.Png, 100);
            if (encoded is { Size: > 0 })
            {
                return true;
            }

            logger.LogWarning("SkiaSharp could not encode a probe image.");
            return false;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "SkiaSharp native runtime failed.");
            return false;
        }
    }

    // A tool install or refresh asks again here; the answer is stored so the Local AI engine room and /ai/status see it too.
    public bool AiRuntimeLoads() => runtime.RefreshTextRuntime();

    public void RefreshModelFiles() => inventory.Refresh();
}
