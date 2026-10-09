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

    private static string Read(string relativePath) =>
        File.ReadAllText(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", relativePath)));
}
