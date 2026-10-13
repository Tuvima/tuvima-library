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
        return services;
    }
}
