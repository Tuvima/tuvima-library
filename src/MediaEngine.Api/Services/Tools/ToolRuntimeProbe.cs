using MediaEngine.AI.Infrastructure;
using SkiaSharp;

namespace MediaEngine.Api.Services.Tools;

/// <summary>The real probes: a 2x2 image encode for the image engine, loading the local AI runtime, and re-reading model files.</summary>
public sealed class ToolRuntimeProbe(
    SharedAiRuntime runtime,
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

    public bool AiRuntimeLoads()
    {
        try
        {
            runtime.EnsureLlama();
            return true;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "LLamaSharp native runtime failed.");
            return false;
        }
    }

    public void RefreshModelFiles() => inventory.Refresh();
}
