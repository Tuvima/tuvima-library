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
        services.AddSingleton<IContainerProbe, EnvironmentContainerProbe>();
        return services;
    }
}
