using MediaEngine.Web.Services.Ui;
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
        services.TryAddScoped<IAppToastService>(provider => provider.GetRequiredService<AppToastService>());
        return services;
    }
}
