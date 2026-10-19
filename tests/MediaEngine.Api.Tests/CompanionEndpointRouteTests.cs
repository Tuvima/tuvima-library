namespace MediaEngine.Api.Tests;

public sealed class CompanionEndpointRouteTests
{
    [Fact]
    public void CompanionEndpoints_ExposeFiveOwnProfileActionsWithTheirScopes()
    {
        var endpointSource = File.ReadAllText(GetRepoFilePath("src/MediaEngine.Api/Endpoints/CompanionEndpoints.cs"));
        var routeBuilderSource = File.ReadAllText(GetRepoFilePath("src/MediaEngine.Api/DependencyInjection/ApiEndpointRouteBuilderExtensions.cs"));
        var playbackRegistrations = File.ReadAllText(GetRepoFilePath(
            "src/MediaEngine.Api/DependencyInjection/TuvimaPlaybackServiceCollectionExtensions.cs"));

        Assert.Contains("app.MapCompanionEndpoints();", routeBuilderSource, StringComparison.Ordinal);
        Assert.Contains("services.AddSingleton<PlayerDeviceStateRepository>();", playbackRegistrations, StringComparison.Ordinal);
        Assert.Contains("services.AddSingleton<CompanionSessionRepository>();", playbackRegistrations, StringComparison.Ordinal);
        Assert.Contains("services.AddSingleton<ICompanionSessionService, CompanionSessionService>();", playbackRegistrations, StringComparison.Ordinal);
        Assert.Contains("app.MapGroup(\"/api/v1/companion\")", endpointSource, StringComparison.Ordinal);
        Assert.Contains("new PlayerResourceFailureFilter()", endpointSource, StringComparison.Ordinal);

        // Each action is paired with the scope that guards it: reads like /player/state, writes like /player/heartbeat.
        AssertRoute(endpointSource, "group.MapGet(\"/devices\"", "ClientApiScopes.QueueRead");
        AssertRoute(endpointSource, "group.MapGet(\"/session\"", "ClientApiScopes.QueueRead");
        AssertRoute(endpointSource, "group.MapPut(\"/session/follow\"", "ClientApiScopes.ProgressWrite");
        AssertRoute(endpointSource, "group.MapPut(\"/session/manual\"", "ClientApiScopes.ProgressWrite");
        AssertRoute(endpointSource, "group.MapDelete(\"/session\"", "ClientApiScopes.ProgressWrite");

        // Identity comes from the caller's claims through the player's shared binding, never from storage in the route.
        Assert.Contains("PlayerEndpoints.Bind(user,", endpointSource, StringComparison.Ordinal);
        Assert.DoesNotContain("IDatabaseConnection", endpointSource, StringComparison.Ordinal);
    }

    private static void AssertRoute(string source, string route, string scope)
    {
        var start = source.IndexOf(route, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Missing route {route}");
        var next = source.IndexOf("group.Map", start + route.Length, StringComparison.Ordinal);
        var block = next < 0 ? source[start..] : source[start..next];
        Assert.Contains($"RequireClientScope({scope})", block, StringComparison.Ordinal);
    }

    private static string GetRepoFilePath(string relativePath) =>
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", relativePath));
}
