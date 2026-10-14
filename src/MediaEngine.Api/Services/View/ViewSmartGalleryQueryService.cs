using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Models;
using MediaEngine.Domain.PersonalMedia;

namespace MediaEngine.Api.Services.View;

public sealed class ViewSmartGalleryQueryService(IViewGalleryRepository galleries)
    : IViewSmartGalleryQueryService
{
    public async Task<CollectionRuleDefinition?> ResolveRuleAsync(Guid galleryId, CancellationToken ct = default)
    {
        var gallery = await galleries.GetAsync(galleryId, ct: ct).ConfigureAwait(false);
        if (gallery is null)
        {
            throw new InvalidOperationException("The Smart Gallery no longer exists.");
        }

        return gallery.Kind == ViewGalleryKind.Smart
            ? ViewSmartGalleryRules.Parse(gallery.SmartRuleJson!)
            : null;
    }
}
