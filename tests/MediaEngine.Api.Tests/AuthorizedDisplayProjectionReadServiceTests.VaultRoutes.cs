using System.Reflection;
using System.Text;
using MediaEngine.Api.Endpoints;
using MediaEngine.Api.Security;
using MediaEngine.Api.Services.ReadServices;
using MediaEngine.Api.Services.Review;
using MediaEngine.Domain.Authorization;
using MediaEngine.Domain.Constants;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Entities;
using MediaEngine.Storage;
using MediaEngine.Storage.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace MediaEngine.Api.Tests;

// The Vault (Videos spec WP9, D17, D33): the review actions and the capability reports answer for a Vault item exactly as they
// answer for an id that does not exist, and the responses about a Vault file are never cached. The routes run for real here (their
// own request delegates, real data store); every service a Vault item must never reach is a proxy that fails the test if touched.
public sealed partial class AuthorizedDisplayProjectionReadServiceTests
{
    // ── Cache-Control ───────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Vault_EveryRouteThatClearsAVaultFile_MarksItsResponsePrivateAndUncacheable()
    {
        var shape = await SeedVaultAsync(administrator: false, grantedTheVaultLibrary: false);
        var session = NewVaultSession(shape.Account);
        await UnlockVaultAsync(session);
        var context = VaultContext(shape.Account, session);
        var resources = CreateResourceService(context);

        Assert.Equal(CatalogueResourceAccess.Allowed, await resources.EvaluateAssetAsync(context, shape.Secret.AssetId, ApplicationPermissionIds.PlaybackRead));
        Assert.Equal("private, no-store", context.Response.Headers.CacheControl.ToString());

        var viaWork = VaultContext(shape.Account, session);
        Assert.Equal(CatalogueResourceAccess.Allowed, await resources.EvaluateEntityAsync(viaWork, "Work", shape.Secret.WorkId, ApplicationPermissionIds.PlaybackRead));
        Assert.Equal("private, no-store", viaWork.Response.Headers.CacheControl.ToString());
    }

    [Fact]
    public async Task Vault_ARouteThatSetsItsOwnCaching_CannotUndoTheVaultHeader()
    {
        var shape = await SeedVaultAsync(administrator: false, grantedTheVaultLibrary: false);
        var session = NewVaultSession(shape.Account);
        await UnlockVaultAsync(session);
        var context = VaultContext(shape.Account, session);
        var feature = new CapturingResponseFeature();
        context.Features.Set<IHttpResponseFeature>(feature);
        await CreateResourceService(context).EvaluateAssetAsync(context, shape.Secret.AssetId, ApplicationPermissionIds.PlaybackRead);

        // A later "cache for a year" is overridden just before the response starts.
        context.Response.Headers.CacheControl = "public, max-age=31536000";
        await feature.RunOnStartingAsync();

        Assert.Equal("private, no-store", context.Response.Headers.CacheControl.ToString());
    }

    [Fact]
    public async Task Vault_AnOrdinaryFile_KeepsItsNormalCaching()
    {
        var shape = await SeedVaultAsync(administrator: false, grantedTheVaultLibrary: false);
        var session = NewVaultSession(shape.Account);
        await UnlockVaultAsync(session);
        var context = VaultContext(shape.Account, session);

        Assert.Equal(CatalogueResourceAccess.Allowed, await CreateResourceService(context).EvaluateAssetAsync(context, shape.Normal.AssetId, ApplicationPermissionIds.PlaybackRead));

        Assert.False(context.Response.Headers.ContainsKey("Cache-Control"));
    }

    [Fact]
    public async Task Vault_TheVaultArtworkRoute_IsUncacheableWhetherTheImageIsFoundOrNot()
    {
        var shape = await SeedVaultAsync(administrator: false, grantedTheVaultLibrary: false);
        var route = await MapRoutesAsync(builder => builder.MapVaultEndpoints());
        var locked = await InvokeAsync(route["GetVaultArtwork"], shape.Account, NewVaultSession(shape.Account), ("artworkId", Guid.NewGuid().ToString("D")));

        Assert.Equal(StatusCodes.Status404NotFound, locked.Status);
        Assert.Equal("private, no-store", locked.CacheControl);
    }

    // ── Review actions ──────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("ResolveReviewItem", true)]
    [InlineData("DismissReviewItem", false)]
    [InlineData("SkipUniverseMatch", false)]
    [InlineData("MoveReviewItemToTv", false)]
    public async Task Vault_AReviewItemOnAVaultFile_AnswersExactlyLikeAMissingOne_ForEveryReviewAction(string routeName, bool hasBody)
    {
        var shape = await SeedVaultAsync(administrator: true, grantedTheVaultLibrary: true);
        var vaultReview = await InsertReviewAsync(shape.Secret.AssetId, ReviewStatus.Pending);
        var workReview = await InsertReviewAsync(shape.Secret.WorkId, ReviewStatus.Pending, "Work");
        var session = NewVaultSession(shape.Account);
        var routes = await MapReviewRoutesAsync();

        var missingId = Guid.NewGuid();
        var missing = await InvokeAsync(routes[routeName], shape.Account, session, ("id", missingId.ToString("D")), body: hasBody ? "{}" : null);
        Assert.True(missing.Status == StatusCodes.Status404NotFound, missing.Body);

        foreach (var reviewId in new[] { vaultReview, workReview })
        {
            var vault = await InvokeAsync(routes[routeName], shape.Account, session, ("id", reviewId.ToString("D")), body: hasBody ? "{}" : null);

            Assert.Equal(missing.Status, vault.Status);
            Assert.Equal(
                missing.Body.Replace(missingId.ToString("D"), "ID", StringComparison.OrdinalIgnoreCase),
                vault.Body.Replace(reviewId.ToString("D"), "ID", StringComparison.OrdinalIgnoreCase));
        }

        Assert.DoesNotContain(vaultReview, _moveToTv.Seen);
        Assert.DoesNotContain(workReview, _moveToTv.Seen);

        // Nothing was changed on the way.
        Assert.Equal(ReviewStatus.Pending, (await new ReviewQueueRepository(_database).GetByIdAsync(vaultReview))!.Status);
    }

    [Fact]
    public async Task Vault_AnOrdinaryReviewItem_StillGetsPastTheVaultCheck()
    {
        var shape = await SeedVaultAsync(administrator: true, grantedTheVaultLibrary: true);
        var ordinary = await InsertReviewAsync(shape.Normal.AssetId, ReviewStatus.Dismissed);
        var routes = await MapReviewRoutesAsync();

        // Already dismissed, so the action stops at its own "not pending" answer, which proves it was reached.
        var result = await InvokeAsync(routes["DismissReviewItem"], shape.Account, NewVaultSession(shape.Account), ("id", ordinary.ToString("D")));

        Assert.Equal(StatusCodes.Status400BadRequest, result.Status);
    }

    // ── Capability reports ──────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Vault_CapabilityStateOfAVaultAsset_IsEmptyExactlyLikeAnIdThatDoesNotExist()
    {
        var shape = await SeedVaultAsync(administrator: true, grantedTheVaultLibrary: true);
        await SeedCapabilityAsync(shape.Secret.AssetId);
        await SeedCapabilityAsync(shape.Normal.AssetId);
        var routes = await MapRoutesAsync(builder => builder.MapCapabilityEndpoints());
        var session = NewVaultSession(shape.Account);

        var missing = await InvokeAsync(routes["GetAssetCapabilities"], shape.Account, session, ("id", Guid.NewGuid().ToString("D")));
        var vault = await InvokeAsync(routes["GetAssetCapabilities"], shape.Account, session, ("id", shape.Secret.AssetId.ToString("D")));
        var viaWork = await InvokeAsync(routes["GetAssetCapabilities"], shape.Account, session, ("id", shape.Secret.WorkId.ToString("D")));
        var ordinary = await InvokeAsync(routes["GetAssetCapabilities"], shape.Account, session, ("id", shape.Normal.AssetId.ToString("D")));

        Assert.Equal(StatusCodes.Status200OK, vault.Status);
        Assert.Equal(missing.Body, vault.Body);
        Assert.Equal(missing.Body, viaWork.Body);
        Assert.NotEqual(missing.Body, ordinary.Body);
    }

    [Fact]
    public async Task Vault_TheCapabilitySummary_NeverCountsAVaultAsset()
    {
        var shape = await SeedVaultAsync(administrator: true, grantedTheVaultLibrary: true);
        await SeedCapabilityAsync(shape.Secret.AssetId);
        await SeedCapabilityAsync(shape.Normal.AssetId);

        var summary = await new EntityCapabilityStateRepository(_database).GetSummaryAsync();

        Assert.Equal(1, Assert.Single(summary).Value);
    }

    // ── Helpers ─────────────────────────────────────────────────────────────────────────────────

    private async Task<Guid> InsertReviewAsync(Guid entityId, string status, string entityType = "MediaAsset") =>
        await new ReviewQueueRepository(_database).InsertAsync(new ReviewQueueEntry
        {
            EntityId = entityId,
            EntityType = entityType,
            Trigger = ReviewTrigger.MultipleQidMatches,
            Status = status,
            ReviewReadyAt = DateTimeOffset.UtcNow,
        });

    private Task SeedCapabilityAsync(Guid entityId) =>
        new EntityCapabilityStateRepository(_database).EnsureAsync(new EntityCapabilityState
        {
            EntityId = entityId,
            EntityKind = "MediaAsset",
            CapabilityId = "test.capability",
            CapabilityKind = "enrichment",
        });

    private sealed class HasBody : IHttpRequestBodyDetectionFeature
    {
        public bool CanHaveBody => true;
    }

    private sealed class CapturingResponseFeature : HttpResponseFeature
    {
        private readonly List<(Func<object, Task> Callback, object State)> _callbacks = [];

        public override void OnStarting(Func<object, Task> callback, object state) => _callbacks.Add((callback, state));

        public async Task RunOnStartingAsync()
        {
            foreach (var (callback, state) in _callbacks)
            {
                await callback(state);
            }
        }
    }

    /// <summary>Records which review ids reached the move-to-TV service; every id is answered as a missing item.</summary>
    private sealed class RecordingMoveToTv : IReviewMoveToTvService
    {
        public List<Guid> Seen { get; } = [];

        public Task<ReviewMoveToTvResult> MoveAsync(Guid reviewItemId, string resolvedBy, CancellationToken ct = default)
        {
            Seen.Add(reviewItemId);
            return Task.FromResult(new ReviewMoveToTvResult(ReviewMoveToTvOutcome.ReviewItemNotFound));
        }
    }

    private sealed record RouteResult(int Status, string Body, string? CacheControl);

    private async Task<IReadOnlyDictionary<string, RouteEndpoint>> MapReviewRoutesAsync() =>
        await MapRoutesAsync(builder => builder.MapReviewEndpoints());

    /// <summary>Maps the real routes over this test's data store. Services a Vault item must never reach fail the test when touched.</summary>
    private Task<IReadOnlyDictionary<string, RouteEndpoint>> MapRoutesAsync(Action<IEndpointRouteBuilder> map)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = Environments.Production });
        builder.Services.AddAuthorization();
        builder.Services.AddSingleton<IDatabaseConnection>(_database);
        builder.Services.AddSingleton<IVaultLibraryRepository>(new VaultLibraryRepository(_database));
        builder.Services.AddSingleton<INsfwAccessRepository>(new NsfwAccessRepository(_database));
        builder.Services.AddSingleton<IVaultArtworkRepository>(new VaultArtworkRepository(_database));
        builder.Services.AddSingleton<IReviewQueueRepository>(new ReviewQueueRepository(_database));
        builder.Services.AddSingleton<IEntityCapabilityStateRepository>(new EntityCapabilityStateRepository(_database));
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddSingleton<VaultAccessResolver>();
        foreach (var service in new[]
        {
            typeof(IReviewQueueReadService), typeof(IMetadataClaimRepository), typeof(IHydrationPipelineService),
            typeof(ICanonicalValueRepository), typeof(IEventPublisher), typeof(ISystemActivityRepository),
            typeof(ICollectionRepository), typeof(IConfigurationLoader),
        })
        {
            builder.Services.AddSingleton(service, _ => CreateTouchFailsProxy(service));
        }

        builder.Services.AddSingleton<IReviewMoveToTvService>(_moveToTv);
        builder.Services.AddSingleton<IRequestAuthorityResolver>(new RequestAuthorityResolver(_accounts, _applications, new ProfileRepository(_database)));
        // Only here so mapping can infer each route's parameters; the routes under test never ask for them.
        builder.Services.AddSingleton(typeof(MediaEngine.Api.Services.Vault.VaultAdministrationService), _ => throw new InvalidOperationException("Not used by these routes."));
        builder.Services.AddSingleton(typeof(MediaEngine.Api.Services.Security.RecentSignInGuard), _ => throw new InvalidOperationException("Not used by these routes."));
        // The add-a-folder route: its refusals (not an administrator, locked) never reach the service, so a service with nothing behind it fails the test if touched.
        builder.Services.AddSingleton(new MediaEngine.Api.Services.Vault.VaultFolderAttachmentService(
            null!, null!, null!, null!, Microsoft.Extensions.Logging.Abstractions.NullLogger<MediaEngine.Api.Services.Vault.VaultFolderAttachmentService>.Instance));
        // The Identify routes (WP12) are mapped by MapVaultEndpoints too; they are only inferred here, never invoked.
        builder.Services.AddSingleton<IVaultMatchRepository>(new VaultMatchRepository(_database));
        builder.Services.AddSingleton<IVaultActivityRepository>(new VaultActivityRepository(_database));
        // The browsing routes (WP14) read through their own repository and check the Watch feature on the account.
        builder.Services.AddSingleton<IVaultBrowseRepository>(new VaultBrowseRepository(_database));
        builder.Services.AddSingleton<IAccountRepository>(_accounts);
        // The images routes (WP16) go through one service over this test's data store.
        builder.Services.AddSingleton(_ => CreateVaultImageService());
        builder.Services.AddSingleton(NewTouchFailsIdentifyService());
        var app = builder.Build();
        map(app);
        _routeApps.Add(app);
        IReadOnlyDictionary<string, RouteEndpoint> routes = ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(source => source.Endpoints).OfType<RouteEndpoint>()
            .Where(endpoint => endpoint.Metadata.GetMetadata<IEndpointNameMetadata>() is not null)
            .ToDictionary(endpoint => endpoint.Metadata.GetMetadata<IEndpointNameMetadata>()!.EndpointName!, StringComparer.Ordinal);
        return Task.FromResult(routes);
    }

    private readonly List<WebApplication> _routeApps = [];
    private readonly RecordingMoveToTv _moveToTv = new();

    private async Task<RouteResult> InvokeAsync(
        RouteEndpoint endpoint, Guid account, Guid session, (string Name, string Value) route, string? body = null)
    {
        var app = _routeApps[^1];
        await using var scope = app.Services.CreateAsyncScope();
        var context = VaultContext(account, session);
        context.RequestServices = scope.ServiceProvider;
        context.Request.RouteValues[route.Name] = route.Value;
        context.Response.Body = new MemoryStream();
        if (body is not null)
        {
            context.Request.ContentType = "application/json";
            var bytes = Encoding.UTF8.GetBytes(body);
            context.Request.Body = new MemoryStream(bytes);
            context.Request.ContentLength = bytes.Length;
            context.Features.Set<IHttpRequestBodyDetectionFeature>(new HasBody());
        }

        await endpoint.RequestDelegate!(context);
        context.Response.Body.Position = 0;
        var text = await new StreamReader(context.Response.Body).ReadToEndAsync();
        return new RouteResult(
            context.Response.StatusCode, text,
            context.Response.Headers.TryGetValue("Cache-Control", out var cache) ? cache.ToString() : null);
    }

    private static object CreateTouchFailsProxy(Type serviceType) =>
        typeof(DispatchProxy).GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Single(method => method.Name == nameof(DispatchProxy.Create) && method.GetGenericArguments().Length == 2)
            .MakeGenericMethod(serviceType, typeof(TouchFailsProxy))
            .Invoke(null, null)!;

    /// <summary>Stands in for a service a Vault item must never reach; any call is a leak and fails the test.</summary>
    public class TouchFailsProxy : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) =>
            throw new InvalidOperationException($"A Vault item reached {targetMethod?.DeclaringType?.Name}.{targetMethod?.Name}.");
    }
}
