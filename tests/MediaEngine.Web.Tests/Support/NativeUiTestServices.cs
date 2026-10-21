using MediaEngine.Web.Services.Theming;
using MediaEngine.Web.Services.Ui;
using MediaEngine.Web.Tests.Support;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace MediaEngine.Web.Tests;

internal static class NativeUiTestServices
{
    public static IServiceCollection AddNativeUiServices(this IServiceCollection services)
    {
        services.TryAddScoped<AppPopoverService>();
        services.TryAddScoped<AppDialogService>();
        services.TryAddScoped<IAppDialogService>(provider => provider.GetRequiredService<AppDialogService>());
        services.TryAddScoped<AppToastService>();
        // Components that vary by form factor read it; tests that care register their own, initialised service first.
        services.TryAddScoped(_ => new DeviceContextService(EngineApiClientStub.CreateDefault()));
        services.TryAddScoped<IAppToastService>(provider => provider.GetRequiredService<AppToastService>());
        services.TryAddScoped<MediaEngine.Web.Services.Settings.ApprovalFlow>();
        // NSFW mode and the Vault. The state asks the page's own IEngineApiClient; a stub that does not know the route leaves it
        // off, which is what every other profile gets.
        services.AddLogging();
        services.TryAddScoped<MediaEngine.Web.Services.Vault.IVaultPreferenceStore, InMemoryVaultPreferenceStore>();
        services.TryAddScoped(provider => new MediaEngine.Web.Services.Vault.NsfwModeState(
            provider.GetService<MediaEngine.Web.Services.Integration.IEngineApiClient>() ?? EngineApiClientStub.CreateDefault(),
            provider.GetRequiredService<MediaEngine.Web.Services.Vault.IVaultPreferenceStore>(),
            provider.GetRequiredService<Microsoft.Extensions.Logging.ILogger<MediaEngine.Web.Services.Vault.NsfwModeState>>(),
            provider.GetService<TimeProvider>()));
        services.TryAddScoped<MediaEngine.Web.Services.Vault.NsfwModeSwitching>();
        services.TryAddScoped<MediaEngine.Web.Services.Vault.VaultLibraryOnboarding>();
        services.TryAddScoped<MediaEngine.Web.Services.Vault.IVaultCatalogue, MediaEngine.Web.Services.Vault.PendingVaultCatalogue>();
        services.TryAddScoped<MediaEngine.Web.Services.Vault.IVaultFolderAttachment, MediaEngine.Web.Services.Vault.EngineVaultFolderAttachment>();
        services.TryAddScoped<MediaEngine.Web.Services.Integration.IItsYouConfirmer>(_ => new ConfirmedActionRunnerTests.SpyConfirmer(confirmed: false));
        return services;
    }
}
