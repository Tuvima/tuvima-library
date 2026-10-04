namespace MediaEngine.Web.Services.MediaTiles;

public sealed record HomeHeroFraming(bool CoverLed, string Position)
{
    public static HomeHeroFraming Resolve(int? width, int? height, bool hasLargeRendition)
    {
        if (width is not > 0 || height is not > 0 || (width < 1280 && !hasLargeRendition)) return new(true, "center center");
        var aspect = width.Value / (double)height.Value;
        return aspect >= 1.6 ? new(false, "center 22%") : aspect >= 1.2 ? new(false, "center 30%") : new(true, "center center");
    }
}
