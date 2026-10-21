using MediaEngine.Api.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace MediaEngine.Api.Tests;

public sealed class StartupReadinessServiceTests
{
    [Fact]
    public async Task StorageCategory_RunsOnlyTheStorageChecks_AndNeverTheDataStoreScan()
    {
        var dataStoreChecks = 0;
        var readiness = Create(() => dataStoreChecks++);

        var response = await readiness.GetAsync("storage");

        Assert.Equal(["library_storage", "watch_folders"], response.Checks.Select(check => check.Name).Order().ToArray());
        Assert.All(response.Checks, check => Assert.Equal("storage", check.Category));
        Assert.Equal(0, dataStoreChecks);
    }

    [Fact]
    public async Task NoCategory_StillRunsEveryReadinessCheck()
    {
        var dataStoreChecks = 0;
        var readiness = Create(() => dataStoreChecks++);

        var response = await readiness.GetAsync();

        Assert.Equal(3, response.Checks.Count);
        Assert.Equal(1, dataStoreChecks);
    }

    [Theory]
    [InlineData("storge")]
    [InlineData("readiness")]
    [InlineData("required")]
    public async Task UnknownCategory_IsRefused_RatherThanReadingAsReady(string category)
    {
        var readiness = Create(() => { });

        await Assert.ThrowsAsync<ArgumentException>(() => readiness.GetAsync(category));
    }

    private static StartupReadinessService Create(Action onDataStoreCheck)
    {
        var storage = new Dictionary<string, object> { ["category"] = "storage" };
        var services = new ServiceCollection()
            .AddLogging()
            .AddHealthChecks()
            .AddCheck("database_integrity", () =>
            {
                onDataStoreCheck();
                return HealthCheckResult.Healthy("Scanned.", new Dictionary<string, object> { ["category"] = "database" });
            }, tags: ["readiness", "database", "required"])
            .AddCheck("library_storage", () => HealthCheckResult.Healthy("Writable.", storage), tags: ["readiness", "storage", "required"])
            .AddCheck("watch_folders", () => HealthCheckResult.Healthy("Readable.", storage), tags: ["readiness", "storage"])
            .Services
            .BuildServiceProvider();
        return new StartupReadinessService(services.GetRequiredService<HealthCheckService>());
    }
}
