using MediaEngine.Api.Services.LocalAssets.Ocr;
using MediaEngine.Api.Services.Tools;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Services;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace MediaEngine.Api.DependencyInjection;

public static class TuvimaToolRequirementsServiceCollectionExtensions
{
    public static IServiceCollection AddTuvimaToolRequirements(this IServiceCollection services)
    {
        services.AddSingleton<IToolRuntimeProbe, ToolRuntimeProbe>();
        services.AddSingleton<IToolRequirementsService, ToolRequirementsService>();
        services.AddSingleton<ToolLocationService>();
        // Fetching FFmpeg and Tesseract on request: fixed addresses and checksums from config/tool_downloads.json.
        // Redirects are followed by hand, to an allow-list of hosts, so the client itself must not follow them.
        services.AddTuvimaHttpClient(HttpToolDownloadSource.HttpClientName, TimeSpan.FromMinutes(30), addStandardResilience: false)
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false });
        services.AddSingleton<IToolDownloadSource, HttpToolDownloadSource>();
        services.AddSingleton<IToolProcessLauncher, ShellProcessLauncher>();
        services.AddSingleton(ToolInstallPlatform.Current);
        services.AddSingleton<IToolInstallerService>(provider => new ToolInstallerService(
            provider.GetRequiredService<IConfigurationLoader>(),
            provider.GetRequiredService<TuvimaDataPaths>(),
            provider.GetRequiredService<IOcrService>(),
            provider.GetRequiredService<IToolDownloadSource>(),
            provider.GetRequiredService<IToolProcessLauncher>(),
            provider.GetRequiredService<ISystemActivityRepository>(),
            provider.GetRequiredService<IHostApplicationLifetime>(),
            provider.GetRequiredService<ToolInstallPlatform>(),
            () => provider.GetRequiredService<IToolRequirementsService>(),
            provider.GetRequiredService<ILogger<ToolInstallerService>>()));
        // Nothing is held back until the work that holds files registers its own counter in place of this one.
        services.TryAddSingleton<IToolWaitingWorkCounter, NoToolWaitingWorkCounter>();
        services.AddHostedService<ToolRequirementsStartupService>();
        return services;
    }
}
