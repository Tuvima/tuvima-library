using System.Text.Json;
using MediaEngine.Contracts.Display;

namespace MediaEngine.Api.Tests;

public sealed class DisplayContractTests
{
    [Fact]
    public void DisplayDtoSerialization_UsesStableCamelCaseContract()
    {
        var workId = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var assetId = Guid.Parse("22222222-2222-2222-2222-222222222222");
        var collectionId = Guid.Parse("33333333-3333-3333-3333-333333333333");
        var primaryAction = new DisplayActionDto("playAsset", "Play", WorkId: workId, AssetId: assetId, WebUrl: $"/watch/{assetId}");
        var card = new DisplayCardDto(
            Id: Guid.Parse("44444444-4444-4444-4444-444444444444"),
            WorkId: workId,
            AssetId: assetId,
            CollectionId: collectionId,
            MediaType: "Movie",
            GroupingType: "work",
            Title: "Arrival",
            Subtitle: null,
            Facts: ["2016", "Science Fiction"],
            Artwork: new DisplayArtworkDto(
                CoverUrl: "/stream/22222222-2222-2222-2222-222222222222/cover",
                CoverSmallUrl: "/stream/artwork/22222222-2222-2222-2222-222222222222?size=s",
                CoverMediumUrl: "/stream/artwork/22222222-2222-2222-2222-222222222222?size=m",
                CoverLargeUrl: "/stream/artwork/22222222-2222-2222-2222-222222222222?size=l",
                SquareUrl: null,
                SquareSmallUrl: null,
                SquareMediumUrl: null,
                SquareLargeUrl: null,
                BannerUrl: null,
                BannerSmallUrl: null,
                BannerMediumUrl: null,
                BannerLargeUrl: null,
                BackgroundUrl: null,
                BackgroundSmallUrl: null,
                BackgroundMediumUrl: null,
                BackgroundLargeUrl: null,
                LogoUrl: null,
                CoverWidthPx: 1000,
                CoverHeightPx: 1500,
                SquareWidthPx: null,
                SquareHeightPx: null,
                BannerWidthPx: null,
                BannerHeightPx: null,
                BackgroundWidthPx: null,
                BackgroundHeightPx: null,
                AccentColor: "#111111"),
            PreferredShape: "portrait",
            Presentation: "default",
            TileTextMode: "coverOnly",
            PreviewPlacement: "bottom",
            Progress: new DisplayProgressDto(Percent: 42, Label: "42%", LastAccessed: DateTimeOffset.Parse("2026-04-24T12:00:00Z"), ResumeAction: primaryAction),
            Actions: [primaryAction],
            Flags: new DisplayCardFlagsDto(IsPlayable: true, IsReadable: false, IsCollection: false, IsFavorite: false),
            SortTimestamp: DateTimeOffset.Parse("2026-04-24T12:00:00Z"))
        {
            Description = "2016 science fiction film",
            Badges = [new DisplayCardBadgeDto("quality", "4K"), new DisplayCardBadgeDto("source", "Max")],
            PreviewItems =
            [
                new DisplayCardPreviewItemDto(
                    WorkId: workId,
                    AssetId: assetId,
                    Title: "Arrival",
                    ImageUrl: "/stream/artwork/22222222-2222-2222-2222-222222222222?size=s",
                    Shape: "portrait",
                    Position: "1"),
            ],
            PreviewTotalCount = 3,
        };

        var page = new DisplayPageDto(
            Key: "home",
            Title: "Home",
            Subtitle: null,
            Hero: new DisplayHeroDto("Continue", "Pick up where you left off.", "home-hero", card.Artwork, card.Progress, card.Actions)
            {
                Facts = card.Facts,
            },
            Shelves: [new DisplayShelfDto("continue", "Continue", "continue", [card], "/api/v1/display/continue")],
            Catalog: [card]);

        var json = JsonSerializer.Serialize(page, new JsonSerializerOptions(JsonSerializerDefaults.Web));

        Assert.Contains("\"key\":\"home\"", json, StringComparison.Ordinal);
        Assert.Contains("\"workId\":\"11111111-1111-1111-1111-111111111111\"", json, StringComparison.Ordinal);
        Assert.Contains("\"assetId\":\"22222222-2222-2222-2222-222222222222\"", json, StringComparison.Ordinal);
        Assert.Contains("\"preferredShape\":\"portrait\"", json, StringComparison.Ordinal);
        Assert.Contains("\"coverSmallUrl\":\"/stream/artwork/22222222-2222-2222-2222-222222222222?size=s\"", json, StringComparison.Ordinal);
        Assert.Contains("\"coverMediumUrl\":\"/stream/artwork/22222222-2222-2222-2222-222222222222?size=m\"", json, StringComparison.Ordinal);
        Assert.Contains("\"tileTextMode\":\"coverOnly\"", json, StringComparison.Ordinal);
        Assert.Contains("\"previewPlacement\":\"bottom\"", json, StringComparison.Ordinal);
        Assert.Contains("\"progress\":{\"percent\":42", json, StringComparison.Ordinal);
        Assert.Contains("\"actions\":[{\"type\":\"playAsset\",\"label\":\"Play\"", json, StringComparison.Ordinal);
        Assert.Contains("\"facts\":[\"2016\",\"Science Fiction\"]", json, StringComparison.Ordinal);
        Assert.Contains("\"description\":\"2016 science fiction film\"", json, StringComparison.Ordinal);
        Assert.Contains("\"badges\":[{\"kind\":\"quality\",\"label\":\"4K\"}", json, StringComparison.Ordinal);
        Assert.Contains("\"previewItems\":[{\"workId\":\"11111111-1111-1111-1111-111111111111\"", json, StringComparison.Ordinal);
        Assert.Contains("\"previewTotalCount\":3", json, StringComparison.Ordinal);
        Assert.Contains("\"flags\":{\"isPlayable\":true", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"WorkId\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public void DisplayEndpoints_AreVersionedConsumerEndpointsAndMappedInProgram()
    {
        var endpointSource = File.ReadAllText(GetRepoFilePath(@"src/MediaEngine.Api/Endpoints/DisplayEndpoints.cs"));
        var programSource = File.ReadAllText(GetRepoFilePath(@"src/MediaEngine.Api/DependencyInjection/ApiEndpointRouteBuilderExtensions.cs"));

        Assert.Contains("app.MapGroup(\"/api/v1/display\")", endpointSource, StringComparison.Ordinal);
        Assert.Contains("group.MapGet(\"/home\"", endpointSource, StringComparison.Ordinal);
        Assert.Contains("group.MapGet(\"/browse\"", endpointSource, StringComparison.Ordinal);
        Assert.Contains("group.MapGet(\"/continue\"", endpointSource, StringComparison.Ordinal);
        Assert.Contains("group.MapGet(\"/search\"", endpointSource, StringComparison.Ordinal);
        Assert.Contains("group.MapGet(\"/groups/{groupId:guid}\"", endpointSource, StringComparison.Ordinal);
        Assert.Contains("includeCatalog", endpointSource, StringComparison.Ordinal);
        Assert.Contains("RequireClientScope(ClientApiScopes.LibraryRead)", endpointSource, StringComparison.Ordinal);
        Assert.Contains("app.MapDisplayEndpoints();", programSource, StringComparison.Ordinal);
    }

    [Fact]
    public void DisplayBrowse_LaneRequestsUseRichConsumerPageComposition()
    {
        var source = File.ReadAllText(GetRepoFilePath(@"src/MediaEngine.Api/Services/Display/DisplayComposerService.cs"));
        var cardBuilderSource = File.ReadAllText(GetRepoFilePath(@"src/MediaEngine.Api/Services/Display/DisplayCardBuilder.cs"));
        var shelfBuilderSource = File.ReadAllText(GetRepoFilePath(@"src/MediaEngine.Api/Services/Display/DisplayShelfBuilder.cs"));

        Assert.Contains("BuildLaneAsync(normalizedLane, includeCatalog, profileId, ct)", source, StringComparison.Ordinal);
        Assert.Contains("DisplayShelfBuilder", source, StringComparison.Ordinal);
        Assert.Contains("BuildWatchShelves", shelfBuilderSource, StringComparison.Ordinal);
        Assert.Contains("BuildReadShelves", shelfBuilderSource, StringComparison.Ordinal);
        Assert.Contains("BuildAlbumFirstListenShelves", source, StringComparison.Ordinal);
        Assert.Contains("\"continue-watching\"", shelfBuilderSource, StringComparison.Ordinal);
        Assert.Contains("\"continue-reading\"", shelfBuilderSource, StringComparison.Ordinal);
        Assert.Contains("\"continue-listening\"", source, StringComparison.Ordinal);
        Assert.Contains("\"recently-updated-albums\"", source, StringComparison.Ordinal);
        Assert.Contains("\"recently-added-audiobooks\"", source, StringComparison.Ordinal);
        Assert.Contains("\"openCollection\"", cardBuilderSource, StringComparison.Ordinal);
    }

    [Fact]
    public void WebBrowseSurfaces_UseDisplayApiAndMediaTiles()
    {
        var clientSource = ReadEngineApiClientSources("IEngineApiClient*.cs");
        var composerSource = File.ReadAllText(GetRepoFilePath(@"src/MediaEngine.Web/Services/MediaTiles/MediaTileComposerService.cs"));
        var browseShellSource = File.ReadAllText(GetRepoFilePath(@"src/MediaEngine.Web/Components/Browse/MediaBrowseShell.razor"));

        Assert.Contains("GetDisplayBrowseAsync", clientSource, StringComparison.Ordinal);
        Assert.Contains("GetDisplayBrowseAsync(lane: \"read\"", composerSource, StringComparison.Ordinal);
        Assert.Contains("GetDisplayBrowseAsync(lane: \"watch\"", composerSource, StringComparison.Ordinal);
        Assert.Contains("GetDisplayBrowseAsync(lane: \"listen\"", composerSource, StringComparison.Ordinal);
        Assert.Contains("LoadDisplayCardsAsync", browseShellSource, StringComparison.Ordinal);
        Assert.Contains("MediaTileComposerService.FromDisplayCard", browseShellSource, StringComparison.Ordinal);
        Assert.Contains("<MediaTileGrid", browseShellSource, StringComparison.Ordinal);
        Assert.DoesNotContain("ToItemCard", browseShellSource, StringComparison.Ordinal);
    }

    [Fact]
    public void DisplayProjection_ExposesSizedArtworkFields()
    {
        var workProjection = File.ReadAllText(GetRepoFilePath(@"src/MediaEngine.Api/Services/Display/DisplayWorkProjectionReader.cs"));
        var journeyProjection = File.ReadAllText(GetRepoFilePath(@"src/MediaEngine.Api/Services/Display/DisplayJourneyProjectionReader.cs"));

        Assert.Contains("entity_id = AssetId AND key IN ('cover_url', 'cover', 'poster_url', 'poster', 'episode_still_url', 'episode_still', 'still_url', 'still')", workProjection, StringComparison.Ordinal);
        Assert.Contains("entity_id = WorkId AND key IN ('cover_url', 'cover', 'poster_url', 'poster', 'episode_still_url', 'episode_still', 'still_url', 'still')", workProjection, StringComparison.Ordinal);
        Assert.Contains("entity_id = RootWorkId AND key IN ('cover_url', 'cover', 'poster_url', 'poster', 'episode_still_url', 'episode_still', 'still_url', 'still')", workProjection, StringComparison.Ordinal);
        Assert.Contains("episode_still_url", workProjection, StringComparison.Ordinal);
        Assert.Contains("episode_still_url", journeyProjection, StringComparison.Ordinal);
        Assert.Contains("cover_url_s", workProjection, StringComparison.Ordinal);
        Assert.Contains("cover_url_m", workProjection, StringComparison.Ordinal);
        Assert.Contains("background_url_l", workProjection, StringComparison.Ordinal);
        Assert.Contains("cover_url_s", journeyProjection, StringComparison.Ordinal);
        Assert.Contains("background_url_m", journeyProjection, StringComparison.Ordinal);
        Assert.Contains("cv_cover_a", journeyProjection, StringComparison.Ordinal);
        Assert.Contains("cv_cover_item", journeyProjection, StringComparison.Ordinal);
        Assert.Contains("END AS CoverUrl", journeyProjection, StringComparison.Ordinal);
    }

    [Fact]
    public void DisplayProjections_UseSharedVisibilityRulesForCatalogAndContinueRows()
    {
        var workProjection = File.ReadAllText(GetRepoFilePath(@"src/MediaEngine.Api/Services/Display/DisplayWorkProjectionReader.cs"));
        var journeyProjection = File.ReadAllText(GetRepoFilePath(@"src/MediaEngine.Api/Services/Display/DisplayJourneyProjectionReader.cs"));

        Assert.Contains("HomeVisibilitySql.VisibleWorkPredicate(\"w.id\", \"w.curator_state\", \"w.is_catalog_only\")", workProjection, StringComparison.Ordinal);
        Assert.Contains("HomeVisibilitySql.VisibleWorkPredicate(\"w.id\", \"w.curator_state\", \"w.is_catalog_only\")", journeyProjection, StringComparison.Ordinal);
        Assert.Contains("HomeVisibilitySql.VisibleAssetPathPredicate(\"ma.file_path_root\")", workProjection, StringComparison.Ordinal);
        Assert.Contains("HomeVisibilitySql.VisibleAssetPathPredicate(\"ma.file_path_root\")", journeyProjection, StringComparison.Ordinal);
    }

    private static string GetRepoFilePath(string relativePath) =>
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", relativePath));

    private static string ReadEngineApiClientSources(string pattern)
    {
        var directory = GetRepoFilePath(@"src/MediaEngine.Web/Services/Integration");
        return string.Join(
            "\n",
            Directory.EnumerateFiles(directory, pattern)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .Select(File.ReadAllText));
    }
}
