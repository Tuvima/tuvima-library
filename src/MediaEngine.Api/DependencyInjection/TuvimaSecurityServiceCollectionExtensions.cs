using MediaEngine.Api.Security;
using MediaEngine.Api.Services;
using MediaEngine.Api.Services.Security;
using MediaEngine.Domain.Contracts;

namespace MediaEngine.Api.DependencyInjection;

public static class TuvimaSecurityServiceCollectionExtensions
{
    public static IServiceCollection AddTuvimaSecurity(this IServiceCollection services)
    {
        services.AddScoped<IUsableAdministratorService, UsableAdministratorService>();
        services.AddScoped<SecureAccountGate>();
        services.AddScoped<RecentSignInGuard>();
        services.AddScoped<AccountNoticeService>();
        services.AddScoped<ManagedClientDeviceService>();
        services.AddScoped<ManagedSessionService>();
        services.AddSingleton<ClientAuthorizationService>();
        services.AddScoped<PhoneBackupProfileService>();
        services.AddSingleton<DeviceActionThrottle>();
        services.AddScoped<DeviceProfileSwitchService>();
        services.AddSingleton<IContainerProbe, EnvironmentContainerProbe>();
        return services;
    }
}
