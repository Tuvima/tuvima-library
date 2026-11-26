using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Enums;
using MediaEngine.Providers.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaEngine.Providers.Tests;

public sealed class SlowRetailLaneConcurrencyTests
{
    [Fact]
    public async Task SlowRetailWork_DoesNotWaitWhenEveryOrdinaryRetailSlotIsBusy()
    {
        using var limiter = new EnrichmentConcurrencyLimiter(
            new HydrationSettings { MaxConcurrentRetailProviderJobs = 1 },
            NullLogger<EnrichmentConcurrencyLimiter>.Instance);

        var release = new TaskCompletionSource();
        var ordinary = limiter.RunAsync(EnrichmentWorkKind.RetailProvider, _ => release.Task);

        var slowRan = false;
        await limiter.RunAsync(EnrichmentWorkKind.SlowRetailProvider, _ =>
        {
            slowRan = true;
            return Task.CompletedTask;
        }).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(slowRan);
        release.SetResult();
        await ordinary;
    }
}
