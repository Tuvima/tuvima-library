using MediaEngine.Domain.Contracts;
using MediaEngine.Providers.Services;
using Microsoft.Extensions.Hosting;

namespace MediaEngine.Api.Tests;

public sealed class DeferredEnrichmentHostingTests
{
    [Fact]
    public void DeferredEnrichmentService_UsesTheHostedServiceLifecycle()
    {
        Assert.True(typeof(IHostedService).IsAssignableFrom(typeof(DeferredEnrichmentService)));
        Assert.True(typeof(IDeferredEnrichmentService).IsAssignableFrom(typeof(DeferredEnrichmentService)));
    }

    [Fact]
    public void EngineRegistration_UsesTheSameSingletonForCommandsAndHostedExecution()
    {
        var providerRegistrations = File.ReadAllText(GetRepoFilePath(
            @"src/MediaEngine.Api/DependencyInjection/TuvimaProviderServiceCollectionExtensions.cs"));
        var hostedRegistrations = File.ReadAllText(GetRepoFilePath(
            @"src/MediaEngine.Api/DependencyInjection/TuvimaHostedServiceCollectionExtensions.cs"));

        Assert.Contains("AddSingleton<DeferredEnrichmentService>()", providerRegistrations, StringComparison.Ordinal);
        Assert.Contains("GetRequiredService<DeferredEnrichmentService>()", providerRegistrations, StringComparison.Ordinal);
        Assert.Contains("GetRequiredService<DeferredEnrichmentService>()", hostedRegistrations, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "AddSingleton<IDeferredEnrichmentService,    DeferredEnrichmentService>()",
            providerRegistrations,
            StringComparison.Ordinal);
    }

    private static string GetRepoFilePath(string relativePath)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, relativePath);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException($"Could not find {relativePath} from {AppContext.BaseDirectory}");
    }
}
