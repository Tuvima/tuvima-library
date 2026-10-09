using System.IO;

namespace MediaEngine.Api.Tests;

public sealed class CollectionEndpointRouteTests
{
    [Fact]
    public void CollectionEndpoints_GroupFeedsUseSharedVisibilityRulesAndRichMetadata()
    {
        // Response shaping moved out of the endpoint file into
        // CollectionResponseFormatting in stage 5A; scan both as one surface.
        var source =
            File.ReadAllText(GetRepoFilePath(@"src/MediaEngine.Api/Endpoints/CollectionEndpoints.cs"))
            + File.ReadAllText(GetRepoFilePath(@"src/MediaEngine.Api/Services/Collections/CollectionResponseFormatting.cs"));
        var browseReadServiceSource = File.ReadAllText(GetRepoFilePath(@"src/MediaEngine.Api/Services/ReadServices/CollectionBrowseReadService.cs"));

        var readServiceSource = File.ReadAllText(GetRepoFilePath(@"src/MediaEngine.Api/Services/ReadServices/CollectionMediaLookupReadService.cs"));
        Assert.Contains("ICollectionBrowseReadService browseReadService", source, StringComparison.Ordinal);
        Assert.Contains("ICollectionSearchReadService searchReadService", source, StringComparison.Ordinal);
        Assert.Contains("ICollectionMediaLookupReadService mediaLookupReadService", source, StringComparison.Ordinal);
        Assert.Contains("HomeVisibilitySql.VisibleAssetPathPredicate(\"ma.file_path_root\")", readServiceSource, StringComparison.Ordinal);
        Assert.Contains("HomeVisibilitySql.VisibleWorkPredicate(\"w.id\", \"w.curator_state\", \"w.is_catalog_only\")", readServiceSource, StringComparison.Ordinal);
        Assert.Contains("row.DescriptionOverride ?? row.Description", browseReadServiceSource, StringComparison.Ordinal);
        Assert.Contains("Tagline = row.Tagline", browseReadServiceSource, StringComparison.Ordinal);
        Assert.Contains("Network = row.Network", browseReadServiceSource, StringComparison.Ordinal);
        Assert.Contains("SeasonCount = row.SeasonCount is > 0", browseReadServiceSource, StringComparison.Ordinal);
        Assert.Contains("RootCoverAssetId", browseReadServiceSource, StringComparison.Ordinal);
        Assert.Contains("RootBackgroundAssetId", browseReadServiceSource, StringComparison.Ordinal);
        Assert.Contains("RootLogoAssetId", browseReadServiceSource, StringComparison.Ordinal);
        Assert.Contains("ea.asset_type = 'CoverArt'", browseReadServiceSource, StringComparison.Ordinal);
        Assert.Contains("? row.RootLogoAssetId is", browseReadServiceSource, StringComparison.Ordinal);
        Assert.Contains("BackgroundUrl = combinedBackground", source, StringComparison.Ordinal);
        Assert.Contains("BannerUrl = combinedBanner", source, StringComparison.Ordinal);
        Assert.Contains("HeroUrl = combinedHero", source, StringComparison.Ordinal);
        Assert.Contains("LogoUrl = combinedLogo", source, StringComparison.Ordinal);
        Assert.Contains("MapGet(\"/{id:guid}/artwork/{slot}\"", source, StringComparison.Ordinal);
        Assert.Contains("MapPost(\"/{id:guid}/artwork/{slot}\"", source, StringComparison.Ordinal);
        Assert.Contains("MapDelete(\"/{id:guid}/artwork/{slot}\"", source, StringComparison.Ordinal);
        Assert.Contains("UpdateCollectionArtworkAsync(id, normalizedSlot, targetPath, mimeType", source, StringComparison.Ordinal);
        Assert.Contains("UpdateCollectionArtworkAsync(id, slot, null, null", source, StringComparison.Ordinal);
        Assert.Contains("GetSystemViewGroupsAsync", browseReadServiceSource, StringComparison.Ordinal);
        Assert.Contains("PreviewItems = group.Items", source, StringComparison.Ordinal);
        Assert.Contains("w.media_type = 'Music'", browseReadServiceSource, StringComparison.Ordinal);
        Assert.Contains("AlbumCount = row.AlbumCount > 0 ? row.AlbumCount : null", browseReadServiceSource, StringComparison.Ordinal);
        Assert.Contains("EarliestYear = row.EarliestYear", browseReadServiceSource, StringComparison.Ordinal);
        Assert.Contains("LatestYear = row.LatestYear", browseReadServiceSource, StringComparison.Ordinal);
        Assert.Contains("AS EarliestYear", browseReadServiceSource, StringComparison.Ordinal);
        Assert.Contains("AS LatestYear", browseReadServiceSource, StringComparison.Ordinal);

        var accessPolicySource = File.ReadAllText(GetRepoFilePath(@"src/MediaEngine.Api/Models/CollectionAccessPolicy.cs"));
        Assert.Contains("Smart", accessPolicySource, StringComparison.Ordinal);
        Assert.Contains("PlaylistFolder", accessPolicySource, StringComparison.Ordinal);
        Assert.Contains("ApiErrors.Forbidden(\"The active profile cannot edit this collection.\")", source, StringComparison.Ordinal);
    }

    [Fact]
    public void CollectionSearch_UsesSqlBackedQueryInsteadOfLoadingAllCollections()
    {
        var source = File.ReadAllText(GetRepoFilePath(@"src/MediaEngine.Api/Endpoints/CollectionEndpoints.cs"));
        var serviceSource = File.ReadAllText(GetRepoFilePath(@"src/MediaEngine.Api/Services/ReadServices/CollectionSearchReadService.cs"));
        var searchStart = source.IndexOf("group.MapGet(\"/search\"", StringComparison.Ordinal);
        var nextRoute = source.IndexOf("group.MapGet(\"/parents\"", StringComparison.Ordinal);

        Assert.True(searchStart >= 0);
        Assert.True(nextRoute > searchStart);

        var searchSource = source[searchStart..nextRoute];
        Assert.Contains("searchReadService.SearchAsync(q, ct)", searchSource, StringComparison.Ordinal);
        Assert.DoesNotContain("IDatabaseConnection db", searchSource, StringComparison.Ordinal);
        Assert.Contains("QueryAsync<CollectionSearchRow>", serviceSource, StringComparison.Ordinal);
        Assert.Contains("HomeVisibilitySql.VisibleAssetPathPredicate(\"ma.file_path_root\")", serviceSource, StringComparison.Ordinal);
        Assert.Contains("HomeVisibilitySql.VisibleWorkPredicate(\"w.id\", \"w.curator_state\", \"w.is_catalog_only\")", serviceSource, StringComparison.Ordinal);
        Assert.DoesNotContain("collectionRepo.GetAllAsync", searchSource, StringComparison.Ordinal);
    }

    [Fact]
    public void SystemViewGroups_UseTypedGuidParametersForGuidBlobStorage()
    {
        var source = File.ReadAllText(GetRepoFilePath(@"src/MediaEngine.Api/Services/ReadServices/CollectionBrowseReadService.cs"));

        Assert.Contains("WHERE w.id IN @WorkIds", source, StringComparison.Ordinal);
        Assert.Contains("WorkIds = workIds.Select(GuidSql.ToBlob).ToArray()", source, StringComparison.Ordinal);
        Assert.DoesNotContain("BuildGuidBlobLiteralList", source, StringComparison.Ordinal);
    }

    [Fact]
    public void TvSystemViewsExposeRootWorkIdsForWatchShowDetails()
    {
        var collectionSource = File.ReadAllText(GetRepoFilePath(@"src/MediaEngine.Api/Endpoints/CollectionEndpoints.cs"));
        var browseSource = File.ReadAllText(GetRepoFilePath(@"src/MediaEngine.Api/Services/ReadServices/CollectionBrowseReadService.cs"));
        var lookupSource = File.ReadAllText(GetRepoFilePath(@"src/MediaEngine.Api/Services/ReadServices/CollectionMediaLookupReadService.cs"));
        var detailsSource = File.ReadAllText(GetRepoFilePath(@"src/MediaEngine.Api/Endpoints/DetailEndpoints.cs"));
        var composerSource = ReadDetailComposerSource();
        var watchPageSource = File.ReadAllText(GetRepoFilePath(@"src/MediaEngine.Web/Components/Pages/UnifiedDetailPage.razor"));
        var detailRouteRequestSource = File.ReadAllText(GetRepoFilePath(@"src/MediaEngine.Web/Components/Details/DetailRouteRequest.cs"));

        Assert.Contains("group.MapGet(\"/system-views\"", collectionSource, StringComparison.Ordinal);
        Assert.Contains("groupField", collectionSource, StringComparison.Ordinal);
        Assert.Contains("show_name", browseSource, StringComparison.Ordinal);
        Assert.Contains("RootWorkId", browseSource, StringComparison.Ordinal);
        Assert.Contains("return $\"/details/tvshow/{row.WorkId:D}?context=watch\";", lookupSource, StringComparison.Ordinal);
        Assert.Contains("MapGet(\"/{entityType}/{id:guid}\"", detailsSource, StringComparison.Ordinal);
        Assert.Contains("DetailEntityType.TvShow", composerSource, StringComparison.Ordinal);
        Assert.Contains("DetailRouteRequest.ForUnified(EntityType, Id, Context, EpisodeId)", watchPageSource, StringComparison.Ordinal);
        Assert.Contains("parsedEntityType == DetailEntityType.TvShow", detailRouteRequestSource, StringComparison.Ordinal);
        Assert.Contains("DetailPresentationContext.Watch", detailRouteRequestSource, StringComparison.Ordinal);
    }

    [Fact]
    public void ContentGroups_ResolveArtworkThroughManagedStreamUrls()
    {
        var source = File.ReadAllText(GetRepoFilePath(@"src/MediaEngine.Api/Endpoints/CollectionEndpoints.cs"));

        Assert.Contains("using MediaEngine.Api.Services.Display;", source, StringComparison.Ordinal);
        Assert.Contains("DisplayArtworkUrlResolver.Resolve(value, assetId, streamKind, state)", source, StringComparison.Ordinal);
        Assert.Contains("SuppressExternalProviderArtworkUrl(value)", source, StringComparison.Ordinal);
        Assert.DoesNotContain("return w.CanonicalValues\r\n            .FirstOrDefault(c =>\r\n                string.Equals(c.Key, MetadataFieldConstants.CoverUrl", source, StringComparison.Ordinal);
    }

    [Fact]
    public void CollectionReadRoutes_DelegateMigratedProjectionSqlToReadServices()
    {
        var endpointSource = File.ReadAllText(GetRepoFilePath(@"src/MediaEngine.Api/Endpoints/CollectionEndpoints.cs"));
        var registrations = File.ReadAllText(GetRepoFilePath(@"src/MediaEngine.Api/DependencyInjection/ApiReadServiceCollectionExtensions.cs"));

        Assert.Contains("ICollectionBrowseReadService browseReadService", endpointSource, StringComparison.Ordinal);
        Assert.Contains("ICollectionSearchReadService searchReadService", endpointSource, StringComparison.Ordinal);
        Assert.Contains("ICollectionMediaLookupReadService mediaLookupReadService", endpointSource, StringComparison.Ordinal);
        Assert.Contains("CollectionCatalogReadService catalogReadService", endpointSource, StringComparison.Ordinal);
        Assert.Contains("AddSingleton<ICollectionBrowseReadService, CollectionBrowseReadService>", registrations, StringComparison.Ordinal);
        Assert.Contains("AddSingleton<ICollectionSearchReadService, CollectionSearchReadService>", registrations, StringComparison.Ordinal);
        Assert.Contains("AddSingleton<ICollectionMediaLookupReadService, CollectionMediaLookupReadService>", registrations, StringComparison.Ordinal);
        Assert.Contains("AddSingleton<CollectionCatalogReadService>", registrations, StringComparison.Ordinal);
        Assert.Contains("catalogReadService.GetCatalogAsync(activeProfile, ct, allowedWorkIds)", endpointSource, StringComparison.Ordinal);
        Assert.Contains("catalogReadService.GetSummaryAsync(id, activeProfile, ct, allowedWorkIds)", endpointSource, StringComparison.Ordinal);
        Assert.Contains("catalogReadService.GetItemsAsync(id, activeProfile, take, ct, allowedWorkIds)", endpointSource, StringComparison.Ordinal);
        Assert.Contains("authority.ActiveProfileId is not { } activeProfileId", endpointSource, StringComparison.Ordinal);
        Assert.Contains("profileId.Value != activeProfileId", endpointSource, StringComparison.Ordinal);
        Assert.Contains("outside the caller's available libraries", endpointSource, StringComparison.Ordinal);
        Assert.Contains("MapPost(\"/reconcile\"", endpointSource, StringComparison.Ordinal);
        Assert.Contains("backfillService.RunAsync", endpointSource, StringComparison.Ordinal);
    }

    [Fact]
    public void CollectionCatalog_ClassifiesCollectionsServerSide()
    {
        var endpointSource = File.ReadAllText(GetRepoFilePath(@"src/MediaEngine.Api/Endpoints/CollectionEndpoints.cs"));
        var catalogReadServiceSource = File.ReadAllText(GetRepoFilePath(@"src/MediaEngine.Api/Services/ReadServices/CollectionCatalogReadService.cs"));
        var mapperSource = File.ReadAllText(GetRepoFilePath(@"src/MediaEngine.Api/Models/ManagedCollectionMapper.cs"));
        var source = endpointSource + catalogReadServiceSource + mapperSource;
        var dtoSource = File.ReadAllText(GetRepoFilePath(@"src/MediaEngine.Contracts/Collections/ManagedCollections.cs"));

        Assert.Contains("MapGet(\"/catalog\"", endpointSource, StringComparison.Ordinal);
        Assert.DoesNotContain("/management-catalog", endpointSource, StringComparison.Ordinal);
        Assert.Contains("GetCatalogAsync", catalogReadServiceSource, StringComparison.Ordinal);
        Assert.Contains("ClassifyCollectionForCatalog", source, StringComparison.Ordinal);
        Assert.Contains("GetSystemCollectionKey", source, StringComparison.Ordinal);
        Assert.Contains("GetCollectionMediaCountsAsync", source, StringComparison.Ordinal);
        Assert.Contains("movie_count", dtoSource, StringComparison.Ordinal);
        Assert.Contains("tv_count", dtoSource, StringComparison.Ordinal);
        Assert.Contains("book_count", dtoSource, StringComparison.Ordinal);
        Assert.Contains("comic_count", dtoSource, StringComparison.Ordinal);
        Assert.Contains("music_count", dtoSource, StringComparison.Ordinal);
        Assert.Contains("audiobook_count", dtoSource, StringComparison.Ordinal);
        Assert.Contains("earliest_year", dtoSource, StringComparison.Ordinal);
        Assert.Contains("latest_year", dtoSource, StringComparison.Ordinal);
        Assert.Contains("ParseCatalogYear(row.Year)", source, StringComparison.Ordinal);
        Assert.Contains("GetCollectionArtworkItemsAsync", source, StringComparison.Ordinal);
        Assert.Contains("preferred_cover.id AS CoverAssetId", source, StringComparison.Ordinal);
        Assert.Contains("WHEN 'CoverArt' THEN 0", source, StringComparison.Ordinal);
        Assert.Contains("WHEN 'Background' THEN 1", source, StringComparison.Ordinal);
        Assert.Contains("/stream/artwork/{coverAssetId:D}", source, StringComparison.Ordinal);
        Assert.Contains("cover_asset", source, StringComparison.Ordinal);
        Assert.Contains("artwork_primary_hex", source, StringComparison.Ordinal);
        Assert.Contains("artwork_secondary_hex", source, StringComparison.Ordinal);
        Assert.Contains("artwork_accent_hex", source, StringComparison.Ordinal);
        Assert.Contains("preferred_cover.primary_hex", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Watchlist", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Favorites", source, StringComparison.Ordinal);
        Assert.Contains("CollectionManagementCatalogDto", dtoSource, StringComparison.Ordinal);
        Assert.Contains("CollectionArtworkItemDto", dtoSource, StringComparison.Ordinal);
        Assert.Contains("PrimaryColor", dtoSource, StringComparison.Ordinal);
        Assert.Contains("SecondaryColor", dtoSource, StringComparison.Ordinal);
        Assert.Contains("AccentColor", dtoSource, StringComparison.Ordinal);
        Assert.Contains("ArtworkItems", dtoSource, StringComparison.Ordinal);
        Assert.Contains("CollectionCatalogPersonDto", dtoSource, StringComparison.Ordinal);
        Assert.Contains("[JsonPropertyName(\"person\")]", dtoSource, StringComparison.Ordinal);
        Assert.Contains("CanToggleGlobal", dtoSource, StringComparison.Ordinal);
        Assert.Contains("PrimaryLaneOverride", dtoSource, StringComparison.Ordinal);
        Assert.Contains("SystemLaneForKey(systemKey)", source, StringComparison.Ordinal);
        Assert.Contains("\"favorites\" or \"listening-queue\" => \"Listen\"", source, StringComparison.Ordinal);
        Assert.Contains("\"watchlist\" or \"currently-watching\" => \"Watch\"", source, StringComparison.Ordinal);
        Assert.Contains("\"reading-list\" => \"Read\"", source, StringComparison.Ordinal);
        Assert.Contains("ShouldIncludeInManagementCatalog", source, StringComparison.Ordinal);
        Assert.Contains("IsPlaylistCatalogCollection(collection)", source, StringComparison.Ordinal);
        Assert.Contains("CollectionType.PlaylistFolder", source, StringComparison.Ordinal);
        Assert.Contains("CollectionType.Smart", source, StringComparison.Ordinal);
        Assert.Contains("CollectionManagementCatalogCandidate", source, StringComparison.Ordinal);
        Assert.Contains("GetCollectionCatalogAggregation(collection, collections)", source, StringComparison.Ordinal);
        Assert.Contains("TryGetStructuralParentAggregation", source, StringComparison.Ordinal);
        Assert.Contains("$\"parent:{root.Id:D}\"", source, StringComparison.Ordinal);
        Assert.Contains("fictional_universe", source, StringComparison.Ordinal);
        Assert.Contains("franchise", source, StringComparison.Ordinal);
        Assert.Contains("TryGetRelationshipAggregation(collection, \"based_on\"", source, StringComparison.Ordinal);
        Assert.True(
            source.IndexOf("TryGetRelationshipAggregation(collection, \"based_on\"", StringComparison.Ordinal)
            < source.IndexOf("TryGetStructuralParentAggregation", StringComparison.Ordinal));
        Assert.Contains("TryGetRelationshipAggregation(collection, \"series\"", source, StringComparison.Ordinal);
        Assert.Contains("ShouldIncludeCatalogGroup(entries)", source, StringComparison.Ordinal);
        Assert.Contains("generatedCollectionCount >= 2", source, StringComparison.Ordinal);
        Assert.Contains("entry.MediaCounts.IsCrossMedia", source, StringComparison.Ordinal);
        Assert.Contains("CountDistinctOwnedTitles(entry) >= 2", source, StringComparison.Ordinal);
        Assert.Contains("SelectMany(entry => entry.WorkIds)", source, StringComparison.Ordinal);
        Assert.Contains("ISeriesManifestRepository manifestRepo", source, StringComparison.Ordinal);
        Assert.Contains("IPersonRepository personRepo", source, StringComparison.Ordinal);
        Assert.Contains("ResolveCatalogPersonAsync", source, StringComparison.Ordinal);
        Assert.Contains("CollectionRuleEvaluator.ParseDefinition(collection.RuleJson)", source, StringComparison.Ordinal);
        Assert.Contains("FindByQidsAsync(", source, StringComparison.Ordinal);
        Assert.Contains("FindByNamesAsync(", source, StringComparison.Ordinal);
        Assert.DoesNotContain("FindByQidAsync(reference.LookupValue", source, StringComparison.Ordinal);
        Assert.DoesNotContain("FindByNameAsync(reference.LookupValue", source, StringComparison.Ordinal);
        Assert.Contains("ApiImageUrls.BuildPersonHeadshotUrl", source, StringComparison.Ordinal);
        Assert.Contains("HasKnownSeriesManifestAsync(collection, ct)", source, StringComparison.Ordinal);
        Assert.Contains("GetCollectionCatalogAggregation(collection, accessibleCollections)", source, StringComparison.Ordinal);
        Assert.DoesNotContain("collection:{NormalizeCatalogQid(collection.WikidataQid)}", source, StringComparison.Ordinal);
        Assert.Contains("mediaCounts.TotalCount >= 2 || hasKnownSeriesManifest", source, StringComparison.Ordinal);
        Assert.Contains("manifest?.TotalCount > 1", source, StringComparison.Ordinal);
        Assert.Contains("ResolveCollectionWorkIdsToItemsAsync", source, StringComparison.Ordinal);
        Assert.Contains("GetAggregatedCollectionWorkIdsAsync", source, StringComparison.Ordinal);
        Assert.Contains("GetCollectionCatalogSourceWorkIdsAsync", source, StringComparison.Ordinal);
        Assert.Contains("ExpandWithChildCollections", source, StringComparison.Ordinal);
        Assert.Contains("candidate.ParentCollectionId == collection.Id", source, StringComparison.Ordinal);
        Assert.Contains("GetCollectionCatalogDisplayWorkIdsAsync", source, StringComparison.Ordinal);
        Assert.Contains("GetOwnedCollectionCatalogDisplayWorkIdsAsync", source, StringComparison.Ordinal);
        Assert.Contains("INNER JOIN media_assets ma ON ma.edition_id = e.id", source, StringComparison.Ordinal);
        Assert.Contains("var itemCount = workIds.Count", source, StringComparison.Ordinal);
        Assert.Contains("WHEN w.work_kind = 'child' THEN COALESCE(gp.id, p.id, w.id)", source, StringComparison.Ordinal);
        Assert.DoesNotContain("parent_by_key.parent_key = LOWER(TRIM(CAST(show_name.value AS TEXT)))", source, StringComparison.Ordinal);
        Assert.DoesNotContain("parent_value.key IN ('show_name', 'title')", source, StringComparison.Ordinal);
        Assert.Contains("catalogReadService.ResolveMembershipWorkIdAsync(body.WorkId, ct)", source, StringComparison.Ordinal);
        Assert.Contains("existingDisplayWorkIds.Contains(collectionWorkId)", source, StringComparison.Ordinal);
        Assert.Contains("GetOwnedCollectionCatalogDisplayWorkIdsAsync(sourceWorkIds, ct)", source, StringComparison.Ordinal);
        Assert.Contains("GetCollectionWorkIdsAsync(collection, ct)", source, StringComparison.Ordinal);
        Assert.Contains("IsGeneratedTvShowContainer", source, StringComparison.Ordinal);
        Assert.Contains("mediaCounts.WatchCount == mediaCounts.TvCount", source, StringComparison.Ordinal);
        Assert.Contains("displayNameOverride", mapperSource, StringComparison.Ordinal);
        Assert.Contains("series_manifest_items series_item", source, StringComparison.Ordinal);
        Assert.Contains("return \"portrait\";", source, StringComparison.Ordinal);
        Assert.DoesNotContain("return \"landscape\";", source, StringComparison.Ordinal);
        Assert.Contains("TotalCount", dtoSource, StringComparison.Ordinal);
        Assert.DoesNotContain("Title     = title ?? $\"Work", source, StringComparison.Ordinal);

        var lookupSource = File.ReadAllText(GetRepoFilePath(@"src/MediaEngine.Api/Services/ReadServices/CollectionMediaLookupReadService.cs"));
        Assert.Contains("WHEN w.work_kind = 'child' THEN COALESCE(gp.id, p.id, w.id)", lookupSource, StringComparison.Ordinal);
        Assert.Contains("BuildLookupRoute(row)", lookupSource, StringComparison.Ordinal);
        Assert.Contains(".GroupBy(row => row.WorkId)", lookupSource, StringComparison.Ordinal);
    }

    private static string GetRepoFilePath(string relativePath) =>
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", relativePath));

    private static string ReadDetailComposerSource()
    {
        var detailsRoot = Path.GetDirectoryName(
            GetRepoFilePath(@"src/MediaEngine.Api/Services/Details/DetailComposerService.cs"))!;
        return string.Join(
            "\n",
            new[] { Path.Combine(detailsRoot, "DetailComposerService.cs") }
                .Concat(Directory.EnumerateFiles(
                    Path.Combine(detailsRoot, "Internals"),
                    "*.cs",
                    SearchOption.TopDirectoryOnly))
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .Select(File.ReadAllText));
    }
}
