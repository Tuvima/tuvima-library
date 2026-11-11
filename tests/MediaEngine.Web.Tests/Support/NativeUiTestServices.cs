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
        // The universal editor is one service per circuit; pages bind to it and the layout draws it (render EditorPopover to see it).
        services.TryAddScoped<MediaEngine.Web.Services.Editing.EditorHost>();
        services.TryAddScoped<MediaEngine.Web.Services.Editing.ListEditLauncher>();
        // The photos, videos, documents and galleries editor: one window per circuit, and the ribbon the quick actions speak through.
        services.TryAddScoped<MediaEngine.Web.Services.Editing.ViewRibbonState>();
        services.TryAddScoped<MediaEngine.Web.Services.Editing.ViewRibbonVoice>();
        services.TryAddScoped<MediaEngine.Web.Services.Editing.ViewDropUploader>();
        services.TryAddScoped<MediaEngine.Web.Services.Editing.ViewFolderGate>();
        services.TryAddScoped(provider =>
        {
            var api = provider.GetService<MediaEngine.Web.Services.Integration.IEngineApiClient>() ?? EngineApiClientStub.CreateDefault();
            return new MediaEngine.Web.Services.Editing.ViewEditorHost(
                api,
                provider.GetRequiredService<DeviceContextService>(),
                provider.GetService<MediaEngine.Web.Services.Integration.ViewWorkspaceService>() ?? new MediaEngine.Web.Services.Integration.ViewWorkspaceService(api),
                provider.GetRequiredService<Microsoft.Extensions.Logging.ILogger<MediaEngine.Web.Services.Editing.ViewEditorHost>>());
        });
        services.TryAddScoped<AppDialogService>();
        services.TryAddScoped<IAppDialogService>(provider => provider.GetRequiredService<AppDialogService>());
        // Components that vary by form factor read it; tests that care register their own, initialised service first.
        services.TryAddScoped(_ => new DeviceContextService(EngineApiClientStub.CreateDefault()));
        services.TryAddScoped<MediaEngine.Web.Services.Settings.ApprovalFlow>();
        services.TryAddScoped<MediaEngine.Web.Services.Settings.ImportUploadState>();
        // Vault mode and the Vault. The state asks the page's own IEngineApiClient; a stub that does not know the route leaves it
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
        services.TryAddScoped<MediaEngine.Web.Components.Settings.LibraryFolderAddService>();
        // The Libraries page's status line and Activity list follow the Engine's live ingestion state.
        services.TryAddScoped(provider => new MediaEngine.Web.Services.Integration.IngestionLiveDashboardState(
            provider.GetRequiredService<MediaEngine.Web.Services.Integration.IEngineApiClient>(),
            provider.GetRequiredService<MediaEngine.Web.Services.Integration.UIOrchestratorService>(),
            provider.GetRequiredService<MediaEngine.Web.Services.Integration.UniverseStateContainer>()));
        return services;
    }
}
