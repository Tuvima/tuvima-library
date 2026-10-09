namespace MediaEngine.Api.Tests;

public sealed class NetworkEndpointRouteTests
{
    [Fact]
    public void NetworkAdministrationRoutesUseExplicitPermissionsAndPortWorkflow()
    {
        var source = Read(@"src/MediaEngine.Api/Endpoints/NetworkEndpoints.cs");

        Assert.Contains("MapGroup(\"/settings/network\")", source, StringComparison.Ordinal);
        Assert.Contains("RequireAdministratorOrApplication(ApplicationPermissionIds.NetworkStatusRead)", source, StringComparison.Ordinal);
        Assert.Contains("RequireAdministratorOrApplication(ApplicationPermissionIds.NetworkConfigWrite)", source, StringComparison.Ordinal);
        Assert.Contains("MapPost(\"/port-change/check\"", source, StringComparison.Ordinal);
        Assert.Contains("MapPost(\"/port-change/apply\"", source, StringComparison.Ordinal);
        Assert.Contains("Use the Change Port action", source, StringComparison.Ordinal);
        Assert.Contains("MapPost(\"/reset\"", source, StringComparison.Ordinal);
    }

    [Fact]
    public void RouterCoordinatorUsesProtocolPriorityAndOwnedCleanup()
    {
        var source = Read(@"src/MediaEngine.Api/Services/Networking/RouterPortMappingCoordinator.cs");
        var registration = Read(@"src/MediaEngine.Api/DependencyInjection/TuvimaNetworkServiceCollectionExtensions.cs");

        Assert.Contains("OrderBy(mapper => mapper.Priority)", source, StringComparison.Ordinal);
        Assert.Contains("RemoveActiveMappingAsync", source, StringComparison.Ordinal);
        Assert.Contains("RemoveOwnedAsync", source, StringComparison.Ordinal);
        Assert.Contains("PcpRouterPortMapper", registration, StringComparison.Ordinal);
        Assert.Contains("NatPmpRouterPortMapper", registration, StringComparison.Ordinal);
        Assert.Contains("UpnpRouterPortMapper", registration, StringComparison.Ordinal);
        Assert.Contains("LocalDiscoveryHostedService", registration, StringComparison.Ordinal);
    }

    [Fact]
    public void PlaybackManifestAcceptsConnectionFactsAndAppliesNetworkPolicy()
    {
        var endpoints = Read(@"src/MediaEngine.Api/Endpoints/PlaybackEndpoints.cs");
        var playback = Read(@"src/MediaEngine.Api/Services/Playback/PlaybackCapabilitiesService.cs");

        Assert.Contains("NetworkConnectionClassifier classifier", endpoints, StringComparison.Ordinal);
        Assert.Contains("ShouldUseAdaptiveRemoteDelivery", playback, StringComparison.Ordinal);
        Assert.Contains("ReservedUploadMbps", playback, StringComparison.Ordinal);
        Assert.Contains("SourceBitrateKbps", playback, StringComparison.Ordinal);
    }

    private static string Read(string relativePath) => File.ReadAllText(
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", relativePath)));
}
