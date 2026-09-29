using System.Text.Json;
using MediaEngine.Api.Services.Details.Internals;
using MediaEngine.Api.Services.Display;
using MediaEngine.Api.Services.Playback;
using MediaEngine.Api.Services.ReadServices;
using MediaEngine.Contracts.Details;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Models;
using MediaEngine.Storage.Contracts;

namespace MediaEngine.Api.Services.Details;

/// <summary>
/// Stable public facade for composing unified detail pages.
/// Feature-specific orchestration, projection reads, builders, and policies live
/// under <c>Services/Details/Internals</c>.
/// </summary>
public sealed class DetailComposerService
{
    private readonly DetailCompositionOrchestrator _composer;

    public DetailComposerService(
        IDatabaseConnection db,
        ILibraryItemRepository libraryItems,
        IPersonRepository persons,
        IEntityAssetRepository entityAssets,
        ICanonicalValueArrayRepository canonicalArrays,
        ISeriesManifestRepository seriesManifests,
        IPersonCreditReadService personCredits,
        DetailRecommendationService recommendations,
        PlaybackCapabilitiesService? playback = null,
        ILogger<DetailComposerService>? logger = null,
        ICollectionBrowseReadService? collectionBrowse = null,
        CollectionCatalogReadService? collectionCatalog = null,
        IProfileRepository? profiles = null,
        IConfigurationLoader? configurationLoader = null,
        IBridgeIdRepository? bridgeIds = null)
    {
        _composer = new DetailCompositionOrchestrator(
            db,
            libraryItems,
            persons,
            entityAssets,
            canonicalArrays,
            seriesManifests,
            personCredits,
            recommendations,
            playback,
            logger,
            collectionBrowse,
            collectionCatalog,
            profiles,
            configurationLoader,
            bridgeIds);
    }

    public Task<DetailPageViewModel?> BuildAsync(
        DetailEntityType entityType,
        Guid id,
        DetailPresentationContext context,
        CancellationToken ct = default,
        string? selectedContainerId = null,
        Guid? profileId = null,
        string? callerRole = null)
        => _composer.BuildAsync(entityType, id, context, ct, selectedContainerId, profileId);

    internal Task<DetailPageViewModel?> BuildAuthorizedAsync(
        DetailEntityType entityType,
        Guid id,
        DetailPresentationContext context,
        CancellationToken ct,
        string? selectedContainerId,
        Guid? profileId,
        DetailActionAuthorizationContext actionAuthorization,
        IReadOnlyList<Guid>? authorizedAssetIds = null,
        IReadOnlyList<DisplayWorkRow>? authorizedWorks = null)
        => _composer.BuildAsync(
            entityType,
            id,
            context,
            ct,
            selectedContainerId,
            profileId,
            actionAuthorization,
            authorizedAssetIds,
            authorizedWorks);

    public static bool TryParseEntityType(string value, out DetailEntityType entityType)
        => DetailPresentationPolicy.TryParseEntityType(value, out entityType);

    public static DetailPresentationContext ParseContext(string? value)
        => DetailPresentationPolicy.ParseContext(value);

    public static ArtworkPresentationMode ResolveArtworkPresentationMode(
        DetailEntityType entityType,
        string? backdropUrl,
        string? bannerUrl,
        string? coverUrl,
        string? posterUrl,
        string? portraitUrl,
        int relatedArtworkCount,
        int ownedFormatCount)
        => DetailPresentationPolicy.ResolveArtworkPresentationMode(
            entityType,
            backdropUrl,
            bannerUrl,
            coverUrl,
            posterUrl,
            portraitUrl,
            relatedArtworkCount,
            ownedFormatCount);

    internal static IReadOnlyList<JsonElement> SelectMusicAlbumManifestTracks(
        JsonElement tracks,
        IReadOnlyDictionary<string, string> canonicalValues,
        IEnumerable<int?> ownedDiscNumbers)
        => DetailCompositionOrchestrator.SelectMusicAlbumManifestTracks(
            tracks,
            canonicalValues,
            ownedDiscNumbers);
}
