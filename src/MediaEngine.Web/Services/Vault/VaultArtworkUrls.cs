using MediaEngine.Web.Services.Integration;

namespace MediaEngine.Web.Services.Vault;

/// <summary>
/// The addresses of Vault pictures (Videos spec D41). A card asks for the web-sized copy the Engine keeps (scene 640 and 1280 wide,
/// performer 400 and 800, studio 300) and the larger one is offered as the high-density source. The pictures come through the
/// Dashboard's image route so the person's own session reaches the Engine; outside NSFW mode the Engine answers "not found".
/// </summary>
public static class VaultArtworkUrls
{
    public const int SceneWidth = 640;
    public const int SceneLargeWidth = 1280;
    public const int PerformerWidth = 400;
    public const int PerformerLargeWidth = 800;
    public const int StudioWidth = 300;

    public static string Scene(Guid artworkId) => Url(artworkId, SceneWidth);

    public static string SceneLarge(Guid artworkId) => Url(artworkId, SceneLargeWidth);

    public static string Performer(Guid artworkId) => Url(artworkId, PerformerWidth);

    public static string PerformerLarge(Guid artworkId) => Url(artworkId, PerformerLargeWidth);

    public static string Studio(Guid artworkId) => Url(artworkId, StudioWidth);

    private static string Url(Guid artworkId, int width) =>
        $"{EngineImageProxyPath.ProxyPrefix}/api/v1/vault/artwork/{artworkId:D}?w={width}";
}
