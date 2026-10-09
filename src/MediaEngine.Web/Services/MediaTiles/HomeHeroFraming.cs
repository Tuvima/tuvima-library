namespace MediaEngine.Web.Services.MediaTiles;

public sealed record HomeHeroFraming(bool CoverLed, string Position)
{
    public static HomeHeroFraming Resolve(int? width, int? height, bool hasLargeRendition)
    {
        if (width is > 0 and < 960 && !hasLargeRendition)
        {
            return new(true, "center center");
        }
        if (width is not > 0 || height is not > 0)
        {
            return new(false, "center center");
        }
        var aspect = width.Value / (double)height.Value;
        return aspect >= 1.6 ? new(false, "center 22%") : aspect >= 1.2 ? new(false, "center 30%") : new(true, "center center");
    }
}
