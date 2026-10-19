namespace MediaEngine.Api.Tests;

public sealed class EngineSmokeGuardrailTests
{
    [Fact]
    public void EngineProgram_RegistersHealthChecksAndEndpointGroups()
    {
        var repoRoot = FindRepoRoot();
        var program = File.ReadAllText(Path.Combine(repoRoot, "src", "MediaEngine.Api", "Program.cs"));

        Assert.Contains("AddHealthChecks", program);
        Assert.Contains("SqliteHealthCheck", program);
        Assert.Contains("MapHealthChecks(\"/health\")", program);
        Assert.Contains("MapHealthChecks(\"/health/live\",", program);
        Assert.Contains("MapHealthChecks(\"/health/ready\",", program);
        Assert.Contains("StartupReadinessService", program);
        Assert.Contains("WorkerReadinessHealthCheck", program);
        Assert.Contains("MediaRuntimeHealthCheck", program);
        Assert.Contains("MapEngineEndpoints()", program);
        Assert.Contains("AddTuvimaStorage()", program);
        Assert.Contains("AddTuvimaProviders(configLoader)", program);
        Assert.Contains("AddTuvimaIntelligence()", program);
        Assert.Contains("AddTuvimaAi(configLoader)", program);
        Assert.Contains("AddTuvimaPlayback()", program);
        Assert.Contains("AddTuvimaPlugins()", program);
        Assert.Contains("AddTuvimaScheduledTasks()", program);
        Assert.Contains("AddTuvimaDisplay()", program);
        Assert.Contains("AddTuvimaHostedServices()", program);
    }

    [Fact]
    public void EngineLiveness_IsPublicWhileReadinessDetailsRequireAdministratorPolicy()
    {
        var repoRoot = FindRepoRoot();
        var program = File.ReadAllText(Path.Combine(repoRoot, "src", "MediaEngine.Api", "Program.cs"));
        var dashboard = File.ReadAllText(Path.Combine(repoRoot, "src", "MediaEngine.Web", "Program.cs"));

        Assert.Contains("app.MapHealthChecks(\"/health/live\"", program);
        Assert.Contains(".AllowAnonymous()", program);
        Assert.Contains("app.MapHealthChecks(\"/health/ready\"", program);
        Assert.Contains(".RequireAuthorization(AuthPolicies.Administrator)", program);
        Assert.Contains("app.MapHealthChecks(\"/health/live\"", dashboard);
        Assert.Contains("app.MapHealthChecks(\"/health/ready\"", dashboard);
        Assert.Contains("DashboardEngineHealthCheck", dashboard);
    }

    [Fact]
    public void EngineStartup_AllowsTestConfigToBypassHeavyAiModelDownloads()
    {
        var repoRoot = FindRepoRoot();
        var program = File.ReadAllText(Path.Combine(repoRoot, "src", "MediaEngine.Api", "Program.cs"));

        Assert.Contains("TUVIMA_DB_PATH", program);
        Assert.Contains("TUVIMA_CONFIG_DIR", program);
        Assert.Contains("TUVIMA_LIBRARY_ROOT", program);
        Assert.DoesNotContain("DownloadModel", program, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void IdentityStartupRecovery_RunsBeforeIdentityWorkersCanLeaseJobs()
    {
        var repoRoot = FindRepoRoot();
        var hostedRegistrations = File.ReadAllText(Path.Combine(
            repoRoot,
            "src",
            "MediaEngine.Api",
            "DependencyInjection",
            "TuvimaHostedServiceCollectionExtensions.cs"));
        var recoverySource = File.ReadAllText(Path.Combine(repoRoot, "src", "MediaEngine.Api", "Services", "HydrationStartupSweepService.cs"));

        var recoveryRegistration = hostedRegistrations.IndexOf("AddHostedService<HydrationStartupSweepService>", StringComparison.Ordinal);
        var retailRegistration = hostedRegistrations.IndexOf("AddHostedService<RetailMatchHostedService>", StringComparison.Ordinal);
        var bridgeRegistration = hostedRegistrations.IndexOf("AddHostedService<WikidataBridgeHostedService>", StringComparison.Ordinal);
        var hydrationRegistration = hostedRegistrations.IndexOf("AddHostedService<QuickHydrationHostedService>", StringComparison.Ordinal);

        Assert.True(recoveryRegistration >= 0);
        Assert.True(recoveryRegistration < retailRegistration);
        Assert.True(recoveryRegistration < bridgeRegistration);
        Assert.True(recoveryRegistration < hydrationRegistration);
        Assert.Contains("public override async Task StartAsync", recoverySource, StringComparison.Ordinal);
        Assert.Contains("RecoverInterruptedJobsAsync(cancellationToken)", recoverySource, StringComparison.Ordinal);
        Assert.DoesNotContain("AbandonRunningAsync", recoverySource, StringComparison.Ordinal);
        Assert.DoesNotContain("Task.Delay", recoverySource, StringComparison.Ordinal);
    }

    [Fact]
    public void EngineProgram_PreservesSecurityCriticalMiddlewareOrder()
    {
        var repoRoot = FindRepoRoot();
        var program = File.ReadAllText(Path.Combine(repoRoot, "src", "MediaEngine.Api", "Program.cs"));
        string[] orderedMarkers =
        [
            "app.UseExceptionHandler",
            "app.UseCors(\"BlazorWasm\")",
            "app.UseRateLimiter()",
            "app.UseAuthentication()",
            "app.UseAuthorization()",
            "app.MapHealthChecks(\"/health\")",
            "app.UseSwagger()",
            "app.MapEngineEndpoints()",
        ];

        var previous = -1;
        foreach (var marker in orderedMarkers)
        {
            var current = program.IndexOf(marker, StringComparison.Ordinal);
            Assert.True(current > previous, $"{marker} must remain after the preceding pipeline stage.");
            previous = current;
        }
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "MediaEngine.slnx")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("Could not locate repository root.");
    }
}
