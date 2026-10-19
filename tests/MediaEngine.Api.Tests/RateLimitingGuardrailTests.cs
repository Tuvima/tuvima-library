namespace MediaEngine.Api.Tests;

public sealed class RateLimitingGuardrailTests
{
    [Fact]
    public void Program_RunsRateLimiterBeforeAuthentication()
    {
        var source = Read(@"src/MediaEngine.Api/Program.cs");

        var rateLimiterIndex = source.IndexOf("app.UseRateLimiter();", StringComparison.Ordinal);
        var authenticationIndex = source.IndexOf("app.UseAuthentication();", StringComparison.Ordinal);

        Assert.True(rateLimiterIndex >= 0, "app.UseRateLimiter() was not found in Program.cs.");
        Assert.True(authenticationIndex >= 0, "app.UseAuthentication() was not found in Program.cs.");
        Assert.True(
            rateLimiterIndex < authenticationIndex,
            "app.UseRateLimiter() must run before authentication so credential floods are rejected before identity storage is consulted.");
    }

    [Fact]
    public void IntercomNegotiation_RequiresPurposeTokenAfterRateLimiting()
    {
        var source = Read(@"src/MediaEngine.Api/Program.cs");
        var middleware = Read(@"src/MediaEngine.Api/Security/IntercomTokenAuthenticationMiddleware.cs");

        var rateLimiterIndex = source.IndexOf("app.UseRateLimiter();", StringComparison.Ordinal);
        var intercomIndex = source.IndexOf("app.UseMiddleware<IntercomTokenAuthenticationMiddleware>();", StringComparison.Ordinal);
        Assert.True(rateLimiterIndex >= 0 && intercomIndex > rateLimiterIndex);
        Assert.Contains("Headers.Authorization", middleware, StringComparison.Ordinal);
        Assert.Contains("Bearer", middleware, StringComparison.Ordinal);
        Assert.DoesNotContain("access_token", middleware, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("X-Api-Key", middleware, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Program_RegistersAGlobalRateLimiterDefault()
    {
        var source = Read(@"src/MediaEngine.Api/Program.cs");

        var addRateLimiterIndex = source.IndexOf("AddRateLimiter(options =>", StringComparison.Ordinal);
        Assert.True(addRateLimiterIndex >= 0, "builder.Services.AddRateLimiter(options => ...) was not found in Program.cs.");

        Assert.Contains(
            "options.GlobalLimiter",
            source,
            StringComparison.Ordinal);

        var globalLimiterIndex = source.IndexOf("options.GlobalLimiter", StringComparison.Ordinal);
        Assert.True(
            globalLimiterIndex > addRateLimiterIndex,
            "options.GlobalLimiter must be assigned inside the AddRateLimiter configuration block. Without a " +
            "GlobalLimiter, every route that does not explicitly opt into a named policy (key_generation, " +
            "streaming, general) receives no rate limiting at all — do not remove this default.");
    }

    [Fact]
    public void GeneralLimiter_AllowsDashboardRequestFanOutAndAdvertisesRetryTiming()
    {
        var defaults = new MediaEngine.Domain.Configuration.RateLimitingSettings();
        var source = Read(@"src/MediaEngine.Api/Program.cs");

        Assert.True(defaults.General.PermitLimit >= 600);
        Assert.Contains("options.OnRejected", source, StringComparison.Ordinal);
        Assert.Contains("MetadataName.RetryAfter", source, StringComparison.Ordinal);
        Assert.Contains("Response.Headers.RetryAfter", source, StringComparison.Ordinal);
    }

    [Fact]
    public void LiveUpdates_TokenMintAndHubs_UseTheirOwnPoliciesAndNeverTheSignInBudget()
    {
        var program = Read(@"src/MediaEngine.Api/Program.cs");
        var routes = Read(@"src/MediaEngine.Api/DependencyInjection/ApiEndpointRouteBuilderExtensions.cs");
        var authentication = Read(@"src/MediaEngine.Api/Endpoints/AuthenticationEndpoints.cs");

        Assert.Contains("options.AddPolicy(LiveUpdatesRateLimitPolicies.TokenMint, LiveUpdatesRateLimitPolicies.ForTokenMint);", program, StringComparison.Ordinal);
        Assert.Contains("options.AddPolicy(LiveUpdatesRateLimitPolicies.IntercomHub, LiveUpdatesRateLimitPolicies.ForHub);", program, StringComparison.Ordinal);
        Assert.Contains("options.AddPolicy(LiveUpdatesRateLimitPolicies.ApplicationEventsHub, LiveUpdatesRateLimitPolicies.ForHub);", program, StringComparison.Ordinal);
        Assert.Contains(".RequireRateLimiting(LiveUpdatesRateLimitPolicies.IntercomHub)", routes, StringComparison.Ordinal);
        Assert.Contains(".RequireRateLimiting(LiveUpdatesRateLimitPolicies.ApplicationEventsHub)", routes, StringComparison.Ordinal);

        var mint = authentication.IndexOf("group.MapPost(\"/intercom-token\"", StringComparison.Ordinal);
        Assert.True(mint >= 0, "The Intercom token route was not found.");
        var produces = authentication.IndexOf(".Produces<IntercomTokenResponse>()", mint, StringComparison.Ordinal);
        Assert.True(produces > mint, "The Intercom token route's registration was not found.");
        var registration = authentication[produces..authentication.IndexOf(';', produces)];
        Assert.Contains(".RequireRateLimiting(LiveUpdatesRateLimitPolicies.TokenMint)", registration, StringComparison.Ordinal);
        Assert.DoesNotContain("\"authentication\"", registration, StringComparison.Ordinal);

        // Four names, four budgets: sharing a name is what once let reloads use up live updates.
        Assert.Equal(4, new[]
        {
            MediaEngine.Api.Security.LiveUpdatesRateLimitPolicies.TokenMint,
            MediaEngine.Api.Security.LiveUpdatesRateLimitPolicies.IntercomHub,
            MediaEngine.Api.Security.LiveUpdatesRateLimitPolicies.ApplicationEventsHub,
            "authentication",
        }.Distinct(StringComparer.Ordinal).Count());
    }

    private static string Read(string relativePath) =>
        File.ReadAllText(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", relativePath)));
}
