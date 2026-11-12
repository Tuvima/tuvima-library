using MediaEngine.Api.DependencyInjection;
using MediaEngine.Api.Endpoints;
using MediaEngine.Api.Security;
using MediaEngine.Domain.Authorization;
using MediaEngine.Domain.Entities;
using MediaEngine.Domain.Enums;
using MediaEngine.Storage;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace MediaEngine.Api.Tests;

/// <summary>
/// The narrow read behind the Lyrics panel's "Looking for lyrics…" (spec music-player-and-listen-fixes D3). The administrator-only operations list is not
/// loosened: <c>GET /stream/{assetId}/text-tracks/refresh/{operationId}</c> has the same scope and asset access as the action that queued the lookup, and
/// answers only for a lyrics or subtitle operation of that very asset. Real data store; the access check itself is the shared asset-access filter.
/// </summary>
public sealed class TextTrackRefreshStatusEndpointTests : IDisposable
{
    private readonly string _tempRoot = Path.Combine(Path.GetTempPath(), $"tuvima_text_track_status_{Guid.NewGuid():N}");
    private readonly DatabaseConnection _database;
    private readonly MediaOperationRepository _operations;
    private readonly Guid _assetId = Guid.NewGuid();

    public TextTrackRefreshStatusEndpointTests()
    {
        DapperConfiguration.Configure();
        Directory.CreateDirectory(_tempRoot);
        _database = new DatabaseConnection(Path.Combine(_tempRoot, "library.db"));
        _database.InitializeSchema();
        _database.RunStartupChecks();
        _operations = new MediaOperationRepository(_database);
    }

    public void Dispose()
    {
        _database.Dispose();
        Directory.Delete(_tempRoot, recursive: true);
    }

    private Task<MediaOperation> QueueAsync(Guid assetId, string operationType = MediaOperationType.TextTrackLyrics) => _operations.EnsureAsync(new MediaOperation
    {
        OperationType = operationType,
        OperationKind = MediaOperationKind.TextTrack,
        EntityId = assetId,
        EntityKind = EntityType.MediaAsset.ToString(),
        Status = MediaOperationStatus.Queued,
        Stage = MediaOperationStage.Queued,
        QueueName = "text_track",
        IdempotencyKey = $"test:{Guid.NewGuid():N}",
    });

    [Fact]
    public async Task ForTheRightAssetAndALyricsOperation_TheStatusOfThatOperationIsReturned()
    {
        var queued = await QueueAsync(_assetId);

        var waiting = await StreamEndpoints.ReadTextTrackRefreshAsync(_operations, _assetId, queued.Id, CancellationToken.None);
        await _operations.MarkNoResultAsync(queued.Id, "NoResult", "No synced lyrics found.");
        var finished = await StreamEndpoints.ReadTextTrackRefreshAsync(_operations, _assetId, queued.Id, CancellationToken.None);

        Assert.Equal(("queued", queued.Id, _assetId, "TimedLyrics", false), (waiting!.status, waiting.operation_id, waiting.asset_id, waiting.enrichment_type, waiting.refreshed));
        Assert.Equal(("no_result", false), (finished!.status, finished.refreshed));
        Assert.Contains("No synced lyrics found.", finished.message);
    }

    [Fact]
    public async Task ForASubtitlesOperationOfTheAsset_TheStatusSaysSubtitles_AndASucceededOneIsRefreshed()
    {
        var queued = await QueueAsync(_assetId, MediaOperationType.TextTrackSubtitles);
        await _operations.MarkSucceededAsync(queued.Id, "Subtitles updated.");

        var finished = await StreamEndpoints.ReadTextTrackRefreshAsync(_operations, _assetId, queued.Id, CancellationToken.None);

        Assert.Equal(("Subtitles", true), (finished!.enrichment_type, finished.refreshed));
    }

    [Fact]
    public async Task AnotherAssetsOperation_IsNotFound()
    {
        var queued = await QueueAsync(Guid.NewGuid());

        var answer = await StreamEndpoints.ReadTextTrackRefreshAsync(_operations, _assetId, queued.Id, CancellationToken.None);

        Assert.Null(answer);
    }

    [Fact]
    public async Task AnOperationThatIsNotATextTrackOne_IsNotFound_EvenForTheSameAsset()
    {
        var queued = await QueueAsync(_assetId, MediaOperationType.EnrichmentCoverArt);

        var answer = await StreamEndpoints.ReadTextTrackRefreshAsync(_operations, _assetId, queued.Id, CancellationToken.None);

        Assert.Null(answer);
    }

    [Fact]
    public async Task AnUnknownOperation_IsNotFound()
    {
        var answer = await StreamEndpoints.ReadTextTrackRefreshAsync(_operations, _assetId, Guid.NewGuid(), CancellationToken.None);

        Assert.Null(answer);
    }

    [Fact]
    public async Task TheRouteHasTheSameScopeAndAssetAccessAsTheRefreshAction_SoACallerWithoutAccessIsRefused()
    {
        await using var app = BuildApplication();
        var routes = ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .ToArray();

        var refresh = Route(routes, "/stream/{assetId:guid}/text-tracks/refresh", HttpMethods.Post);
        var status = Route(routes, "/stream/{assetId:guid}/text-tracks/refresh/{operationId:guid}", HttpMethods.Get);

        Assert.Contains(status.Metadata.OfType<ClientScopeRequirementMetadata>(), metadata => metadata.Scope == ApplicationPermissionIds.PlaybackWrite.Value);
        Assert.Contains(status.Metadata.OfType<CatalogueAssetAccessMetadata>(), metadata => metadata.Permission == ApplicationPermissionIds.PlaybackWrite.Value);
        Assert.Equal(
            refresh.Metadata.OfType<ClientScopeRequirementMetadata>().Select(metadata => metadata.Scope).Order(),
            status.Metadata.OfType<ClientScopeRequirementMetadata>().Select(metadata => metadata.Scope).Order());
        Assert.Equal(
            refresh.Metadata.OfType<CatalogueAssetAccessMetadata>().Select(metadata => metadata.Permission).Order(),
            status.Metadata.OfType<CatalogueAssetAccessMetadata>().Select(metadata => metadata.Permission).Order());
    }

    private static RouteEndpoint Route(IEnumerable<RouteEndpoint> routes, string pattern, string method) =>
        Assert.Single(routes, route => (route.RoutePattern.RawText ?? string.Empty).TrimEnd('/') == pattern
            && (route.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? []).Contains(method, StringComparer.OrdinalIgnoreCase));

    private static WebApplication BuildApplication()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = Environments.Production });
        builder.Services.AddAuthorization();
        builder.Services.AddHttpClient();
        // Every stream route is built when the application is, so each service type a handler asks for must be known; only the route
        // metadata is read here, so they are placeholders.
        builder.Services.AddSingleton<MediaEngine.Api.Services.Metadata.ArtworkScopeService>(_ => null!);
        builder.Services.AddSingleton<MediaEngine.Api.Services.Details.DetailComposerService>(_ => null!);
        builder.Services.AddSingleton<MediaEngine.Processors.Contracts.IByteStreamer>(_ => null!);
        builder.Services.AddSingleton<MediaEngine.Domain.Contracts.ICanonicalValueRepository>(_ => null!);
        builder.Services.AddSingleton<MediaEngine.Domain.Contracts.ICollectionRepository>(_ => null!);
        builder.Services.AddSingleton<MediaEngine.Api.Services.Display.IDisplayProjectionReadService>(_ => null!);
        builder.Services.AddSingleton<MediaEngine.Api.Security.IViewerAccessService>(_ => null!);
        builder.Services.AddSingleton<MediaEngine.Domain.Contracts.IMediaAssetRepository>(_ => null!);
        builder.Services.AddSingleton<MediaEngine.Domain.Contracts.IMediaOperationRepository>(_ => null!);
        builder.Services.AddSingleton<MediaEngine.Domain.Contracts.ITextTrackRepository>(_ => null!);
        builder.Services.AddSingleton<MediaEngine.Domain.Contracts.IWorkRepository>(_ => null!);
        builder.Services.AddSingleton<MediaEngine.Domain.Contracts.IEntityAssetRepository>(_ => null!);
        builder.Services.AddSingleton<MediaEngine.Api.Services.Artwork.IArtworkRenditionService>(_ => null!);
        builder.Services.AddSingleton<MediaEngine.Api.Security.IRequestAuthorityResolver>(_ => null!);
        builder.Services.AddSingleton<MediaEngine.Providers.Workers.TextTrackEnrichmentWorker>(_ => null!);
        var app = builder.Build();
        app.MapStreamEndpoints();
        return app;
    }
}
