using MediaEngine.Domain;
using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Models;

namespace MediaEngine.Api.Services.Unmatched;

/// <summary>
/// The <c>lookup_state</c> of one item (Library setup spec D34, D38): Unmatched or kept as it is when its file has an Unmatched row, not
/// looked up when its library never looks anything up (a plain kind, or a local-only or manual library), otherwise matched.
/// </summary>
public sealed class ItemLookupStateService(
    IUnmatchedAssetRepository unmatched,
    IMediaAssetRepository assets,
    IConfigurationLoader configuration,
    ILibraryKindSource? kindSource = null)
{
    /// <param name="entityId">A media asset, or a work (answered for its first file).</param>
    public async Task<string> GetAsync(Guid entityId, CancellationToken ct)
    {
        var asset = await assets.FindByIdAsync(entityId, ct).ConfigureAwait(false)
            ?? await assets.FindFirstByWorkIdAsync(entityId, ct).ConfigureAwait(false);
        if (asset is null)
        {
            return ItemLookupStates.Matched;
        }

        if (await unmatched.GetAsync(asset.Id, ct).ConfigureAwait(false) is { } row)
        {
            return row.KeptAsIs ? ItemLookupStates.KeptAsIs : ItemLookupStates.Unmatched;
        }

        var library = string.IsNullOrWhiteSpace(asset.LibraryId)
            ? null
            : configuration.LoadLibraries().Libraries.FirstOrDefault(candidate =>
                string.Equals(candidate.Id, asset.LibraryId, StringComparison.OrdinalIgnoreCase));
        if (library is null)
        {
            return ItemLookupStates.Matched;
        }

        var effective = LibraryKindLookupPolicy.For(library, kindSource?.GetKinds() ?? configuration.LoadLibraryKinds().Kinds);
        // The YouTube kind looks a video up by its ID, so it is not "not looked up"; a plain kind, or a library whose own policy is local-only
        // or manual, asks nobody anything.
        var neverLooksUp = effective.Mode == LibraryLookupMode.Plain
            || (effective.Mode == LibraryLookupMode.FromFields && LibraryMetadataPolicies.BypassesExternalIdentity(effective.MetadataPolicy));
        return neverLooksUp ? ItemLookupStates.NotLookedUp : ItemLookupStates.Matched;
    }
}
