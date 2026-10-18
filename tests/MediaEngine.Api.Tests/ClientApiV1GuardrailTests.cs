namespace MediaEngine.Api.Tests;

public sealed class ClientApiV1GuardrailTests
{
    [Fact]
    public void DevicePairing_UsesHashedRotatingCredentialsAndPerDeviceRevocation()
    {
        var schema = Source("src/MediaEngine.Storage/Schema/schema.snapshot.sql");
        var authorization = Source("src/MediaEngine.Api/Security/ClientAuthorizationService.cs");
        var repository = Source("src/MediaEngine.Storage/ClientAuthorizationRepository.cs");

        Assert.Contains("CREATE TABLE client_devices", schema, StringComparison.Ordinal);
        Assert.Contains("CREATE TABLE device_pairing_requests", schema, StringComparison.Ordinal);
        Assert.Contains("CREATE TABLE client_tokens", schema, StringComparison.Ordinal);
        Assert.Contains("DeviceGrantType = \"urn:ietf:params:oauth:grant-type:device_code\"", authorization, StringComparison.Ordinal);
        Assert.Contains("refresh_token_replay", authorization, StringComparison.Ordinal);
        Assert.Contains("Hash(plaintext)", authorization, StringComparison.Ordinal);
        Assert.Contains("RevokeDeviceAsync", repository, StringComparison.Ordinal);
        Assert.DoesNotContain("TokenHash = plaintext", authorization, StringComparison.Ordinal);
    }

    [Fact]
    public void PublicClientRoutes_AreVersionedScopedAndUseTrustedClaims()
    {
        var endpoints = Source("src/MediaEngine.Api/Endpoints/ClientAuthorizationEndpoints.cs");
        var player = Source("src/MediaEngine.Api/Endpoints/PlayerEndpoints.cs");
        var progress = Source("src/MediaEngine.Api/Endpoints/ProgressEndpoints.cs");
        var display = Source("src/MediaEngine.Api/Endpoints/DisplayEndpoints.cs");

        Assert.DoesNotContain("/.well-known/tuvima", endpoints, StringComparison.Ordinal);
        Assert.Contains("/api/v1/oauth", endpoints, StringComparison.Ordinal);
        Assert.Contains("/device_authorization", endpoints, StringComparison.Ordinal);
        Assert.Contains("/api/v1/devices", endpoints, StringComparison.Ordinal);
        Assert.Contains("MapGroup(\"/api/v1/player\")", player, StringComparison.Ordinal);
        Assert.Contains("MapGroup(\"/api/v1/progress\")", progress, StringComparison.Ordinal);
        Assert.Contains("RequireClientScope(ClientApiScopes.LibraryRead)", display, StringComparison.Ordinal);
        Assert.Contains("user.FindFirstValue(TuvimaClaimTypes.DeviceId)", player, StringComparison.Ordinal);
        Assert.DoesNotContain("ResolveUserId(body.UserId)", progress, StringComparison.Ordinal);
    }

    [Fact]
    public void DashboardEdge_DoesNotExposeEngineCredentialsOrBrowserSpoofedDeviceIdentity()
    {
        var edge = Source("src/MediaEngine.Web/Endpoints/ClientApiEdgeEndpoints.cs");
        var javascript = Source("src/MediaEngine.Web/wwwroot/app.js");

        Assert.Contains("ClientApiProxy", edge, StringComparison.Ordinal);
        Assert.Contains("/.well-known/tuvima", edge, StringComparison.Ordinal);
        Assert.Contains("CopyRequestHeader(context.Request, request, \"Authorization\")", edge, StringComparison.Ordinal);
        Assert.DoesNotContain("X-Tuvima-Service-Key", edge, StringComparison.Ordinal);
        Assert.DoesNotContain("params.get('device')", javascript, StringComparison.Ordinal);
        Assert.DoesNotContain("device_class_source", javascript, StringComparison.Ordinal);
        Assert.DoesNotContain("tuvima.playback.v2.device-id", javascript, StringComparison.Ordinal);
    }

    private static string Source(string relativePath) =>
        File.ReadAllText(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", relativePath)));
}
