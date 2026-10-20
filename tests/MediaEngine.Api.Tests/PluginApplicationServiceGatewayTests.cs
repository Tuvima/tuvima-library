using System.Text;
using System.Text.Json;
using MediaEngine.Api.Endpoints;
using MediaEngine.Api.Security;
using MediaEngine.Api.Services.Plugins;
using MediaEngine.Api.Services.Plugins.ApplicationServices;
using MediaEngine.Contracts.Plugins;
using MediaEngine.Domain.Authorization;
using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Contracts;
using MediaEngine.Plugin.FandomLore;
using MediaEngine.Plugins;
using MediaEngine.Storage;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaEngine.Api.Tests;

public sealed class PluginApplicationServiceGatewayTests
{
    [Fact]
    public async Task Gateway_RequiresExactRegisteredPermissionBeforeInvoking()
    {
        var operation = new FakeOperation();
        var authorization = new FakeAuthorization(AuthorizationDecision.Deny(AuthorizationDenialReason.MissingPermission));
        var gateway = Gateway(operation, authorization);

        var exception = await Assert.ThrowsAsync<PluginApplicationGatewayException>(() =>
            gateway.InvokeAsync(ApplicationAuthority(), operation.Descriptor.OperationId, Json("{}")));

        Assert.Equal(PluginApplicationGatewayFailure.Forbidden, exception.Failure);
        Assert.Equal(operation.Descriptor.Permission.Id, authorization.Permission);
        Assert.Equal(0, operation.InvocationCount);
    }

    [Fact]
    public async Task Gateway_AllowsTypedOperationAndBoundsBothPayloadDirections()
    {
        var operation = new FakeOperation();
        var gateway = Gateway(operation, new FakeAuthorization(AuthorizationDecision.Allow()));

        var response = await gateway.InvokeAsync(ApplicationAuthority(), operation.Descriptor.OperationId, Json("{\"universe_qid\":\"Q42\"}"));

        Assert.Equal("Q42", response.GetProperty("universe_qid").GetString());
        Assert.Equal(1, operation.InvocationCount);

        var oversizedRequest = await Assert.ThrowsAsync<PluginApplicationGatewayException>(() =>
            gateway.InvokeAsync(ApplicationAuthority(), operation.Descriptor.OperationId, new byte[operation.Descriptor.MaxRequestBytes + 1]));
        Assert.Equal(PluginApplicationGatewayFailure.PayloadTooLarge, oversizedRequest.Failure);

        operation.Response = new string('x', 2_000);
        var oversizedResponse = await Assert.ThrowsAsync<PluginApplicationGatewayException>(() =>
            gateway.InvokeAsync(ApplicationAuthority(), operation.Descriptor.OperationId, Json("{\"universe_qid\":\"Q42\"}")));
        Assert.Equal(PluginApplicationGatewayFailure.ResponseTooLarge, oversizedResponse.Failure);
    }

    [Fact]
    public async Task Gateway_RejectsUnknownUnavailableAndInvalidTypedPayloads()
    {
        var operation = new FakeOperation();
        var gateway = Gateway(operation, new FakeAuthorization(AuthorizationDecision.Allow()));

        var unknown = await Assert.ThrowsAsync<PluginApplicationGatewayException>(() =>
            gateway.InvokeAsync(ApplicationAuthority(), "plugin.unknown.service.read", Json("{}")));
        Assert.Equal(PluginApplicationGatewayFailure.UnknownOperation, unknown.Failure);

        operation.Available = false;
        var unavailable = await Assert.ThrowsAsync<PluginApplicationGatewayException>(() =>
            gateway.InvokeAsync(ApplicationAuthority(), operation.Descriptor.OperationId, Json("{}")));
        Assert.Equal(PluginApplicationGatewayFailure.Unavailable, unavailable.Failure);

        operation.Available = true;
        var invalid = await Assert.ThrowsAsync<PluginApplicationGatewayException>(() =>
            gateway.InvokeAsync(ApplicationAuthority(), operation.Descriptor.OperationId, Json("{\"universe_qid\":\"Q42\",\"extra\":true}")));
        Assert.Equal(PluginApplicationGatewayFailure.InvalidPayload, invalid.Failure);
    }

    [Fact]
    public async Task Gateway_PropagatesCallerCancellationWithoutCompletingPluginSideEffects()
    {
        var operation = new FakeOperation { WaitForCancellation = true };
        var gateway = Gateway(operation, new FakeAuthorization(AuthorizationDecision.Allow()));
        using var cancellation = new CancellationTokenSource();

        var pending = gateway.InvokeAsync(ApplicationAuthority(), operation.Descriptor.OperationId, Json("{\"universe_qid\":\"Q42\"}"), cancellation.Token);
        await operation.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.False(operation.SideEffectCompleted);
    }

    [Fact]
    public async Task Gateway_StopsTimedOutOperationWithoutCompletingPluginSideEffects()
    {
        var operation = new FakeOperation(timeout: TimeSpan.FromSeconds(1)) { WaitForCancellation = true };
        var gateway = Gateway(operation, new FakeAuthorization(AuthorizationDecision.Allow()));

        var exception = await Assert.ThrowsAsync<PluginApplicationGatewayException>(() =>
            gateway.InvokeAsync(ApplicationAuthority(), operation.Descriptor.OperationId, Json("{\"universe_qid\":\"Q42\"}")));

        Assert.Equal(PluginApplicationGatewayFailure.Timeout, exception.Failure);
        Assert.False(operation.SideEffectCompleted);
    }

    [Fact]
    public void Registry_RejectsOperationCollisionsAndCallerSuppliedProvenance()
    {
        var operation = new FakeOperation();
        Assert.Throws<InvalidOperationException>(() => new PluginApplicationOperationRegistry([operation, operation]));

        var spoofed = new FakeOperation(pluginId: "other.plugin");
        Assert.Throws<InvalidOperationException>(() => new PluginApplicationOperationRegistry([spoofed]));
    }

    [Fact]
    public async Task FandomAdapter_TracksLiveEnableStateAndUsesOnlyBoundContextFactory()
    {
        using var fixture = new FandomFixture();
        var operation = new FandomLoreDiscoveryOperation(fixture.Catalog, fixture.Contexts);
        var registry = new PluginApplicationOperationRegistry([operation]);
        Assert.False(operation.IsAvailable(out _));
        Assert.False(Assert.Single(registry.List()).IsAvailable);

        fixture.Catalog.SetEnabled(FandomLorePlugin.PluginId, true);
        Assert.True(operation.IsAvailable(out _));
        Assert.True(Assert.Single(registry.List()).IsAvailable);
        var response = Assert.IsType<DiscoverUniverseLoreSourcesResponse>(await operation.InvokeAsync(
            JsonDocument.Parse("{\"universe_qid\":\"Q42\"}").RootElement,
            CancellationToken.None));

        Assert.Equal("Q42", response.UniverseQid);
        Assert.Equal("example.fandom.com", Assert.Single(response.Sources).SourceKey);
        Assert.Equal(FandomLorePlugin.PluginId, fixture.Contexts.PluginId);
        Assert.Equal("application-service-universe-lore-discover", fixture.Contexts.Purpose);
        Assert.True(fixture.Contexts.Context?.IsDisposed);

        fixture.Provider.IsHealthy = false;
        var unhealthy = await Assert.ThrowsAsync<PluginApplicationGatewayException>(async () =>
            await operation.InvokeAsync(
                JsonDocument.Parse("{\"universe_qid\":\"Q42\"}").RootElement,
                CancellationToken.None));
        Assert.Equal(PluginApplicationGatewayFailure.Unavailable, unhealthy.Failure);
        Assert.True(fixture.Contexts.Context?.IsDisposed);
    }

    [Fact]
    public async Task Endpoints_AdvertiseTypedGatewayAndRemoveApplicationBypassRoute()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddAuthorization();
        builder.Services.AddScoped<PluginApplicationServiceGateway>(_ => null!);
        builder.Services.AddScoped<IRequestAuthorityResolver>(_ => null!);
        builder.Services.AddScoped<PluginUniverseLoreService>(_ => null!);
        await using var app = builder.Build();
        app.MapPluginApplicationServiceEndpoints();
        app.MapUniverseLoreEndpoints();
        var endpoints = ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .ToArray();

        var operations = Assert.Single(endpoints, endpoint => endpoint.RoutePattern.RawText == "/plugin-services/operations");
        var operationsPolicy = Assert.IsType<AuthorizationPolicy>(operations.Metadata.Single(value => value is AuthorizationPolicy));
        Assert.Equal(ApplicationPermissionIds.PluginsRead,
            Assert.Single(operationsPolicy.Requirements.OfType<AdministratorOrApplicationRequirement>()).Permission);

        var invoke = Assert.Single(endpoints, endpoint => endpoint.RoutePattern.RawText == "/plugin-services/{operationId}");
        Assert.Equal(typeof(DiscoverUniverseLoreSourcesRequest), invoke.Metadata.GetMetadata<IAcceptsMetadata>()?.RequestType);
        Assert.Contains(invoke.Metadata.GetOrderedMetadata<IProducesResponseTypeMetadata>(),
            metadata => metadata.Type == typeof(DiscoverUniverseLoreSourcesResponse));

        var legacyDiscovery = Assert.Single(endpoints,
            endpoint => endpoint.RoutePattern.RawText == "/universe/{qid}/lore-sources/discover");
        Assert.Contains(legacyDiscovery.Metadata.GetOrderedMetadata<IAuthorizeData>(),
            metadata => metadata.Policy == AuthPolicies.Administrator);
    }

    private static PluginApplicationServiceGateway Gateway(FakeOperation operation, FakeAuthorization authorization) =>
        new(new PluginApplicationOperationRegistry([operation]), authorization);

    private static RequestAuthority ApplicationAuthority() => new(
        PrincipalKind.ServiceApplication,
        true,
        ApplicationId: Guid.NewGuid(),
        ApplicationEnabled: true);

    private static byte[] Json(string value) => Encoding.UTF8.GetBytes(value);

    private sealed class FakeAuthorization(AuthorizationDecision decision) : MediaEngine.Domain.Contracts.IAuthorizationEvaluator
    {
        public ApplicationPermissionId? Permission { get; private set; }

        public ValueTask<AuthorizationDecision> EvaluateAsync(RequestAuthority authority, AuthorizationRequirement requirement, ResourceAuthorizationContext? resource, CancellationToken cancellationToken = default)
        {
            Permission = requirement.ApplicationPermission;
            return ValueTask.FromResult(decision);
        }
    }

    private sealed class FakeOperation : IPluginApplicationOperation
    {
        public FakeOperation(string pluginId = "example", TimeSpan? timeout = null)
        {
            var permission = new PermissionDefinition(
                new("plugin.example.universe-lore.discover"), "Plugins", "Discover", "Discovers sources.",
                PermissionRiskLevel.Read, [ApplicationType.Automation], false, PermissionProvenance.Plugin,
                pluginId, 1_000, PermissionAvailability.Available, null);
            Descriptor = new(
                permission.Id.Value, "example", "Discover", "Discovers sources.", permission,
                typeof(DiscoverUniverseLoreSourcesRequest), typeof(object), 128, 512, timeout ?? TimeSpan.FromSeconds(5));
        }

        public PluginApplicationOperationDescriptor Descriptor { get; }
        public bool Available { get; set; } = true;
        public bool WaitForCancellation { get; set; }
        public bool SideEffectCompleted { get; private set; }
        public int InvocationCount { get; private set; }
        public object Response { get; set; } = new DiscoverUniverseLoreSourcesResponse("Q42", []);
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool IsAvailable(out string? reason)
        {
            reason = Available ? null : "Plugin is disabled.";
            return Available;
        }

        public async ValueTask<object> InvokeAsync(JsonElement payload, CancellationToken ct)
        {
            InvocationCount++;
            _ = PluginApplicationServiceGateway.ReadRequest<DiscoverUniverseLoreSourcesRequest>(payload);
            if (WaitForCancellation)
            {
                Started.SetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                SideEffectCompleted = true;
            }
            return Response;
        }
    }

    private sealed class FandomFixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "tuvima-plugin-application-tests", Guid.NewGuid().ToString("N"));
        private readonly ConfigurationDirectoryLoader _loader;

        public FandomFixture()
        {
            var config = Path.Combine(_root, "config");
            var library = Path.Combine(_root, "library");
            Directory.CreateDirectory(config);
            Directory.CreateDirectory(library);
            _loader = new ConfigurationDirectoryLoader(config);
            _loader.SaveCore(new CoreConfiguration { LibraryRoot = library });
            Catalog = new PluginCatalog([new TestFandomPlugin(Provider)], new PluginSettingsService(_loader), _loader, AllowAllPluginApprovals.Instance, NullLogger<PluginCatalog>.Instance);
        }

        public PluginCatalog Catalog { get; }
        public FakeContextFactory Contexts { get; } = new();
        public TestLoreProvider Provider { get; } = new();

        public void Dispose()
        {
            _loader.Dispose();
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
    }

    private sealed class TestFandomPlugin(TestLoreProvider provider) : ITuvimaPlugin
    {
        public PluginManifest Manifest { get; } = new()
        {
            Id = FandomLorePlugin.PluginId,
            Name = "Test Fandom",
            Permissions = [PluginPermissionIds.NetworkHttp],
            Network = [new PluginNetworkDestination { Host = "*.fandom.com", Sends = "Requests for the page list of a wiki" }],
        };

        public IReadOnlyList<IPluginCapability> CreateCapabilities() => [provider];
    }

    private sealed class TestLoreProvider : IUniverseLoreProvider, IPluginHealthCheck
    {
        public string Kind => "universe-lore-provider";
        public bool IsHealthy { get; set; } = true;
        public Task<PluginHealthResult> GetHealthAsync(IPluginExecutionContext context, CancellationToken cancellationToken = default) =>
            Task.FromResult(new PluginHealthResult
            {
                Status = IsHealthy ? "healthy" : "degraded",
                Message = IsHealthy ? "Ready." : "Unavailable.",
            });
        public Task<IReadOnlyList<PluginLoreSourceCandidate>> DiscoverSourcesAsync(PluginUniverseLoreContext universe, IPluginExecutionContext context, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<PluginLoreSourceCandidate>>([
                new("example.fandom.com", "Example Fandom", "https://example.fandom.com", "https://example.fandom.com/api.php", "CC", .9, default)
            ]);
        public Task<PluginUniverseLoreResult> EnrichUniverseAsync(PluginUniverseLoreContext universe, PluginLoreSource source, IPluginExecutionContext context, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class FakeContextFactory : IPluginExecutionContextFactory
    {
        public string? PluginId { get; private set; }
        public string? Purpose { get; private set; }
        public FakeContext? Context { get; private set; }
        public IPluginExecutionContext Create(string pluginId, string purpose)
        {
            PluginId = pluginId;
            Purpose = purpose;
            return Context = new FakeContext(pluginId);
        }
        public IPluginExecutionContext CreateForMedia(string pluginId, string purpose, PluginMediaAssetContext asset) => throw new NotSupportedException();
    }

    private sealed class FakeContext(string pluginId) : IPluginExecutionContext
    {
        public bool IsDisposed { get; private set; }
        public string PluginId { get; } = pluginId;
        public IReadOnlyDictionary<string, JsonElement> Settings { get; } = new Dictionary<string, JsonElement>();
        public IPluginMediaAccess Media => null!;
        public IPluginHttpClient Http => null!;
        public IPluginStorage Storage => null!;
        public IPluginToolRuntime Tools => null!;
        public IPluginAiClient Ai => null!;
        public IPluginSecrets Secrets => null!;
        public IPluginRecordStore Records => null!;
        public ValueTask DisposeAsync()
        {
            IsDisposed = true;
            return ValueTask.CompletedTask;
        }
    }
}
