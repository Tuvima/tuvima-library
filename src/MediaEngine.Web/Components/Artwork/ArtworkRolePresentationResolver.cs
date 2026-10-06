using MediaEngine.Contracts.Artwork;
using MediaEngine.Web.Components.Shared;
using MediaEngine.Web.Services.Ui;

namespace MediaEngine.Web.Components.Artwork;

public sealed record ArtworkRolePresentation(
    string Role,
    string? SourceAssetType,
    string Label,
    string Icon,
    bool IsDefault,
    bool SupportsAutomatic);

public static class ArtworkRolePresentationResolver
{
    public static IReadOnlyList<ArtworkRolePresentation> Resolve(
        ArtworkEntityWorkspaceDto? workspace,
        ArtworkLibraryItemDto item)
    {
        var descriptors = workspace?.SupportedRoles.Count > 0
            ? workspace.SupportedRoles
            : ArtworkRoleCatalog.Resolve(item.EntityType, item.MediaType, item.GroupKind, item.AssetTypes);
        return descriptors.Select(Resolve).ToList();
    }

    public static ArtworkRolePresentation Resolve(ArtworkRoleDescriptorDto descriptor)
    {
        var (label, icon) = descriptor.PresentationKey switch
        {
            "poster-cover" => ("Poster / Cover", AppMaterialIcons.Outlined.Image),
            "poster" => ("Poster", AppMaterialIcons.Outlined.Image),
            "cover" => ("Cover", AppMaterialIcons.Outlined.MenuBook),
            "still" => ("Still", AppMaterialIcons.Outlined.Photo),
            "portrait" => ("Portrait", AppMaterialIcons.Outlined.Portrait),
            "background" => ("Background", AppMaterialIcons.Outlined.Panorama),
            "logo" => ("Logo", AppMaterialIcons.Outlined.BrandingWatermark),
            "primary-artwork" => ("Primary artwork", AppMaterialIcons.Outlined.Image),
            _ => (descriptor.Role, AppMaterialIcons.Outlined.Image),
        };
        return new(descriptor.Role, descriptor.SourceAssetType, label, icon, descriptor.IsDefault, descriptor.SupportsAutomatic);
    }
}
