using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dapper;
using MediaEngine.Api.Endpoints;
using MediaEngine.Api.Models;
using MediaEngine.Api.Services.Display;
using MediaEngine.Api.Services.Playback;
using MediaEngine.Api.Services.ReadServices;
using MediaEngine.Contracts.Collections;
using MediaEngine.Contracts.Details;
using MediaEngine.Contracts.Persons;
using MediaEngine.Domain;
using MediaEngine.Domain.Aggregates;
using MediaEngine.Domain.Constants;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Entities;
using MediaEngine.Domain.Enums;
using MediaEngine.Domain.Models;
using MediaEngine.Domain.Services;
using MediaEngine.Storage;
using MediaEngine.Storage.Contracts;
using static MediaEngine.Api.Services.Details.Internals.DetailPresentationPolicy;
using SeriesManifestItemDto = MediaEngine.Domain.Models.SeriesManifestItemDto;
using SeriesManifestViewDto = MediaEngine.Domain.Models.SeriesManifestViewDto;

namespace MediaEngine.Api.Services.Details.Internals;

internal sealed partial class DetailCompositionOrchestrator
{
    private static readonly Guid DefaultOwnerUserId = Guid.Parse("00000000-0000-0000-0000-000000000001");

    private readonly IDatabaseConnection _db;
    private readonly ILibraryItemRepository _libraryItems;
    private readonly IPersonRepository _persons;
    private readonly IEntityAssetRepository _entityAssets;
    private readonly ICanonicalValueArrayRepository _canonicalArrays;
    private readonly ISeriesManifestRepository _seriesManifests;
    private readonly IPersonCreditReadService _personCredits;
    private readonly DetailRecommendationService _recommendations;
    private readonly PlaybackCapabilitiesService? _playback;
    private readonly ILogger<DetailComposerService>? _logger;
    private readonly ICollectionBrowseReadService? _collectionBrowse;
    private readonly CollectionCatalogReadService? _collectionCatalog;
    private readonly IProfileRepository? _profiles;
    private readonly DetailProjectionReader _reader;
    private readonly ProviderSourceLinkResolver _providerSourceLinks;
    private readonly IBridgeIdRepository? _bridgeIds;

    public DetailCompositionOrchestrator(
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
        _db = db;
        _libraryItems = libraryItems;
        _persons = persons;
        _entityAssets = entityAssets;
        _canonicalArrays = canonicalArrays;
        _seriesManifests = seriesManifests;
        _personCredits = personCredits;
        _recommendations = recommendations;
        _playback = playback;
        _logger = logger;
        _collectionBrowse = collectionBrowse;
        _collectionCatalog = collectionCatalog;
        _profiles = profiles;
        _reader = new DetailProjectionReader(db, entityAssets);
        _providerSourceLinks = new ProviderSourceLinkResolver(
            configurationLoader?.LoadAllProviders() ?? []);
        _bridgeIds = bridgeIds;
    }

    public async Task<DetailPageViewModel?> BuildAsync(
        DetailEntityType entityType,
        Guid id,
        DetailPresentationContext context,
        CancellationToken ct = default,
        string? selectedContainerId = null,
        Guid? profileId = null,
        DetailActionAuthorizationContext actionAuthorization = default,
        IReadOnlyList<Guid>? authorizedAssetIds = null,
        IReadOnlyList<DisplayWorkRow>? authorizedWorks = null)
    {
        var isAdminView = context is DetailPresentationContext.Admin;
        var favoriteWorkIds = await _reader.LoadFavoriteWorkIdsAsync(profileId, ct);

        var model = entityType switch
        {
            DetailEntityType.Person => await BuildPersonAsync(id, entityType, context, isAdminView, actionAuthorization, authorizedWorks, ct),
            DetailEntityType.BookSeries => await BuildBookSeriesAsync(id, context, isAdminView, actionAuthorization, favoriteWorkIds, profileId, selectedContainerId, authorizedWorks, ct),
            DetailEntityType.Collection or DetailEntityType.TvShow or DetailEntityType.MovieSeries
                or DetailEntityType.ComicSeries or DetailEntityType.MusicAlbum => await BuildCollectionAsync(
                    id,
                    entityType,
                    context,
                    isAdminView,
                    actionAuthorization,
                    favoriteWorkIds,
                    ct,
                    profileId: profileId,
                    selectedContainerId: selectedContainerId,
                    authorizedWorks: authorizedWorks),
            DetailEntityType.Character => await BuildCharacterAsync(id, context, isAdminView, actionAuthorization, authorizedWorks, ct),
            DetailEntityType.Universe => await BuildUniverseAsync(id, context, isAdminView, actionAuthorization, authorizedWorks, ct),
            _ => await BuildWorkAsync(
                id,
                entityType,
                context,
                isAdminView,
                actionAuthorization,
                selectedContainerId,
                favoriteWorkIds,
                profileId,
                authorizedAssetIds,
                authorizedWorks,
                ct),
        };
        if (model is not null && profileId.HasValue)
        {
            var personalStatusAssetIds = authorizedAssetIds is not null
                ? authorizedAssetIds.ToHashSet()
                : authorizedWorks?.Select(work => work.AssetId).ToHashSet();
            await AddPersonalActionsAsync(
                model,
                profileId.Value,
                actionAuthorization,
                personalStatusAssetIds,
                ct);
        }
        return model;
    }
}
